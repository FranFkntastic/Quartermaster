using System.Collections.Immutable;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Automation;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class CourierRunTests : IDisposable
{
    const uint Ore = 5114; // Not 1: item 1 is gil.
    static OwnerScope Who(ulong id) => new() { LocalContentId = id, HomeWorldId = 1, CharacterName = $"Character {id}" };
    readonly OwnerScope source = Who(1), destination = Who(2), carrier = Who(3);
    readonly string dir = Directory.CreateTempSubdirectory("rq-courier-").FullName;
    readonly Client owner = new(Who(1)), mule = new(Who(3));
    readonly Wire wire = new();
    readonly StowagePlan plan = new() { Owner = Who(1) };
    CourierSettings cfg;
    CourierFollower follower;
    CourierConductor engine;
    readonly TargetPlanItem line;
    bool consolidate; // New plans plan no consolidation; a journaled one from an older plan still settles.
    readonly MeetingBell bell = new(1, 1, 1, 0, 0, 0, "Fixture bell");

    readonly OwnerScope shopper = new() { LocalContentId = 4, HomeWorldId = 100, CharacterName = "Shopper" };
    static readonly MeetingBell Bridge = new(130, 21, 0, 0, 0, 0, "Ravana — Ul'dah");
    static uint Region(uint world) => world switch { 100 => 1, 21 => 4, _ => 2 };
    (CourierPlan? Plan, CourierTransferRefusal? Refusal) Order(OwnerScope from, OwnerScope to, int gil, bool returnDestinationHome,
        int? sourceGil = null, CourierTransferItem[]? items = null) =>
        CourierTransferPlanner.Build(new(from, to, gil, [.. items ?? []], returnDestinationHome, $"request-{Guid.NewGuid():N}"), cfg,
            new(carrier, 4, [new(Ore, false, mule.Quantities.GetValueOrDefault(carrier))], mule.Gil.GetValueOrDefault(carrier)),
            sourceGil is { } held ? new(from, 4, [], held) : null,
            id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 999), Region, Bridge);
    CourierPlan Transfer(OwnerScope from, OwnerScope to, int gil, bool returnDestinationHome, params CourierTransferItem[] items)
    {
        var (built, refusal) = Order(from, to, gil, returnDestinationHome, items: items);
        Assert.True(refusal == null, refusal?.Code + ": " + refusal?.Message);
        return built!;
    }

    public CourierRunTests()
    {
        cfg = new() { Carrier = carrier, LocalRoster=[source,destination], PairedOwner = source, CarrierOptIn = true, Bells = ImmutableDictionary<ulong, MeetingBell>.Empty.Add(1, bell).Add(2, bell) };
        line = new()
        {
            StowagePlanId = plan.Id,
            ItemId = Ore,
            ItemName = "Titanium Ore",
            Quality = ItemQualityPolicy.NqOnly,
            Courier = new CourierLine { Sources = [new(source, 11, 0)], Destinations = [new(destination, 22)] },
        };
        mule.Quantities[carrier] = 10; // Private carrier stock must survive.
        follower = Follower(); wire.Handler = m => follower.HandleAsync(source, m, Deadline.After("Fixture", 30),started:wire.Started);
        engine = Conductor();
    }
    CourierFollower Follower(string? root = null) => new(root ?? dir, () => cfg, new(), new(new AR()), mule, mule, mule, mule, _ => { });
    CourierConductor Conductor(string? root = null) => new(root ?? dir, wire, owner, owner, owner, owner, owner, owner, new(), new(new AR()), _ => { });
    CourierPlan Plan() => consolidate ? Planned() with { Consolidations = [new(destination, line.Id, Ore, "Titanium Ore", ItemQualityPolicy.NqOnly, 22, 3)] } : Planned();
    CourierPlan Planned() => CourierPlanner.Build(plan, [line],
        [new(source,null,new(source,5,[])),new(source,11,new(source,0,[new(Ore,false,7)])),
        new(destination,null,new(destination,5,[])),new(destination,22,new(destination,8,[]))],
        new(carrier, 4, [new(Ore, false, 10)]), cfg, id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 3), 1);

    [Fact]
    public async Task ReloadWithCustodyDeliversForwardOnly()
    {
        var crash = Directory.CreateDirectory(Path.Combine(dir, "restart")).FullName;
        wire.CrashCheckpoint = () =>
        {
            File.Copy(Path.Combine(dir, "courier-run.json"), Path.Combine(crash, "courier-run.json"));
            File.Copy(Path.Combine(dir, "courier-custody.json"), Path.Combine(crash, "courier-custody.json"));
        };
        var paused = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Interrupted", paused.State); Assert.Equal(1, owner.Sends); Assert.Equal(13, mule.Units);
        Assert.Equal("Running", new AtomicDocumentStore<CourierJournal>(Path.Combine(crash, "courier-run.json")).Load().State);
        follower.Dispose(); follower = Follower(crash); engine = Conductor(crash);
        Assert.Equal("Interrupted", engine.Snapshot().State);
        var result = await engine.StartAsync(Plan(), cfg);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail);
        Assert.Equal(3, owner.Sends); Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
    }
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task UnverifiedHandOffPreservesBothObservationsAndNeverReplays(int offset, bool uncertainLock)
    {
        mule.ReceiveOffset = offset; mule.UncertainTrade = uncertainLock;
        var result = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Indeterminate", result.State); Assert.Equal(uncertainLock ? "LockUnknown" : "HandOffDeltaMismatch", result.Code);
        var r = new AtomicDocumentStore<CourierJournal>(Path.Combine(dir, "courier-run.json")).Load();
        var e = Assert.Single(r.Evidence); Assert.NotNull(e.Owner); Assert.NotNull(e.Carrier);
        Assert.Equal(3, e.Owner.Before.Count(Ore, false) - e.Owner.After.Count(Ore, false));
        Assert.Equal(3 + offset, e.Carrier.After.Count(Ore, false) - e.Carrier.Before.Count(Ore, false));
        Assert.Empty(r.Custody); engine = Conductor(); Assert.Equal("Indeterminate", (await engine.StartAsync(Plan(), cfg)).State);
        Assert.Equal(1, owner.Sends); Assert.Equal(0, owner.Deposited);
    }
    [Fact]
    public async Task VerifiedSenderCheckpointAfterLostConfirmationCreditsOnlyOneCustodyBatch()
    {
        // Both bags changed, but the sender lost its completion reply. Restarting it would
        // send a second stack, while crediting the carrier twice would encumber private stock.
        owner.LoseSenderReply=true;
        var interrupted=await engine.StartAsync(Plan(),cfg);
        Assert.Equal("Indeterminate",interrupted.State);
        Assert.Equal(13,mule.Units);
        engine=Conductor();
        var completed=await engine.StartAsync(Plan(),cfg);
        Assert.True(completed.State=="Completed",completed.Code+":"+completed.Detail);
        Assert.Equal(3,owner.Sends);Assert.Equal(7,owner.Deposited);Assert.Equal(10,mule.Units);
    }
    [Fact]
    public async Task FollowerRefusesUnpairedDisarmedAndNonCustodyStock()
    {
        var m = new CourierMessage(1, Guid.NewGuid(), Guid.NewGuid(), CourierCommand.Send, source, destination, carrier, Plan().Trips[0].Manifest);
        Task<Result<CourierReceipt>> Handle(OwnerScope who) => follower.HandleAsync(who, m, Deadline.After("Command", 10));
        cfg = cfg with { CarrierOptIn = false }; Assert.Equal("CarrierOptInOff", (await Handle(source)).Code);
        cfg = cfg with { CarrierOptIn = true }; Assert.Equal("OwnerNotPaired", (await Handle(destination)).Code);
        var claim = await follower.HandleAsync(source, m with { Step = Guid.NewGuid(), Command = CourierCommand.Begin, Manifest = [] }, Deadline.After("Begin", 10));
        Assert.Equal(CourierOutcome.Succeeded, claim.Outcome);
        Assert.Equal("CustodyInsufficient", (await Handle(source)).Code); Assert.Equal(0, mule.Sends); Assert.Equal(10, mule.Units);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostPlacementReceiptResumesOnlyRemainingUnits(bool deposit)
    {
        owner.FailPlacement=deposit;
        var result=await engine.StartAsync(Plan(),cfg);
        Assert.True(result.CanClear);Assert.True(result.CanResume);
        engine=Conductor();
        var resumed=await engine.StartAsync(Plan(),cfg);
        Assert.True(resumed.State=="Completed",resumed.Code+":"+resumed.Detail);
        Assert.Equal(7,owner.Deposited);Assert.Equal(7,owner.Retrieved);
        Assert.Equal(10,mule.Units);
    }
    // Live 2026-11-23: a stopped carrier kept MarketMafioso's trade hold, its automation lease and AutoRetainer
    // suppression for as long as its ledger held custody, locking trading until Quartermaster reloaded. A paused run
    // releases every lock; the ledger alone keeps the custody, and commands re-take the locks on demand.
    [Fact]
    public async Task StoppedCarrierReleasesLocksWhileLedgerKeepsCustody()
    {
        follower.Dispose();var lease=new AutomationLease();var ar=new AR();
        follower=new(dir,()=>cfg,lease,new(ar),mule,mule,mule,mule,_=>{});
        var m=new CourierMessage(1,Guid.NewGuid(),Guid.NewGuid(),CourierCommand.Begin,source,source,carrier,[]);
        async Task<Result<CourierReceipt>> Send(CourierMessage msg)=>await follower.HandleAsync(source,msg,Deadline.After("fixture",10));
        Assert.Equal(CourierOutcome.Succeeded,(await Send(m)).Outcome);
        var recv=m with {Step=Guid.NewGuid(),Command=CourierCommand.Receive,Manifest=Plan().Trips[0].Manifest};
        Assert.Equal(CourierOutcome.Succeeded,(await Send(recv)).Outcome);
        await follower.StopLocalAsync();Assert.False(ar.IsSuppressed);Assert.True(lease.CanAcquire);
        InventoryView Bags(int n)=>new(source,5,[new(Ore,false,n)]);
        Assert.Equal(CourierOutcome.Succeeded,(await Send(m with {Step=Guid.NewGuid(),Command=CourierCommand.Confirm,
            ConfirmStep=recv.Step,OwnerEvidence=new(Bags(3),Bags(0))})).Outcome);
        var status=await Send(m with {Step=Guid.NewGuid(),Run=Guid.NewGuid(),Command=CourierCommand.Status});
        Assert.Equal(m.Run,status.Value?.ActiveRun);Assert.Equal(3,status.Value!.Custody.Sum(x=>x.Units));
        Assert.False(ar.IsSuppressed);Assert.True(lease.CanAcquire);
        follower.Dispose();Assert.False(ar.IsSuppressed);Assert.True(lease.CanAcquire);
    }
    // Live 2026-11-23: every trade window completed but the owner's receive never proved it, and before anyone resumed,
    // an ordinary placement stowed part of the delivery. The fresh bags no longer showed the hand-in, so resume refused
    // ResumeUnsettled forever. The owner's journaled placements since the hand-in account for the difference exactly:
    // resume settles both ledgers once, and the deposit stows only what is still in the bags.
    [Fact]
    public async Task UnconfirmedHandInStowedSinceSettlesFromJournalAndDepositsOnlyTheRemainder()
    {
        owner.UncertainTrade = true;
        var stalled = await engine.StartAsync(Plan(), cfg);
        Assert.True(stalled is { State: "Indeterminate" }, stalled.State + " " + stalled.Code);
        Assert.Equal(3, owner.Quantities[destination]); Assert.Equal(10, mule.Units);
        owner.Quantities[destination] -= 2; // A Quartermaster placement stowed two of the three since.
        owner.Placed = new([new(null, Ore, false, -2)], ["stow-after-hand-in"], null);
        owner.UncertainTrade = false;
        engine = Conductor();
        var resumed = await engine.StartAsync(Plan(), cfg);
        Assert.True(resumed.State == "Completed", resumed.Code + ":" + resumed.Detail);
        // Three trips of the seven units, each received once; of the first trip's three, two were stowed outside the run.
        Assert.Equal((3, 7 - 2, 10), (owner.Receives, owner.Deposited, mule.Units));
        Assert.Equal(["DeliveredThenPlaced"], owner.Attested);
        var r = engine.Journal();
        var placed = Assert.Single(r.PlacedOutside);
        Assert.Equal((2, "DeliveredThenPlaced", "stow-after-hand-in"), (placed.Units.Sum(x => x.Units), placed.Disposition, placed.Operations.Single()));
        Assert.Equal(7, Assert.Single(resumed.Lines).Moved); Assert.Empty(r.Custody);
    }
    public void Dispose() { follower.Dispose(); Directory.Delete(dir, true); }

    // A gil trade interrupted after part of the gil moved must credit exactly that part on resume and send
    // only the remainder: re-sending the whole amount would spend gil twice.
    [Fact]
    public async Task GilTransferPartialInterruptSettlesWithoutDoubleSpend()
    {
        const int Five = 5_000_000, Two = 2_000_000;
        owner.Gil[source] = Five; mule.Gil[carrier] = 100;
        Assert.Equal("SourceGilShort", Order(source, destination, Five, false, sourceGil: Five - 1).Refusal?.Code);
        mule.Gil[carrier] = CourierPlanner.GilCap - Five + 1;
        Assert.Equal("CarrierGilCapExceeded", Order(source, destination, Five, false, sourceGil: Five).Refusal?.Code);
        mule.Gil[carrier] = 100;

        owner.PartialGil = mule.PartialGil = Two;
        var interrupted = await engine.StartAsync(Transfer(source, destination, Five, false), cfg);
        Assert.True(interrupted.State != "Completed" && interrupted.CanResume, interrupted.State + " " + interrupted.Code + ":" + interrupted.Detail);
        Assert.Equal(Five - Two, owner.Gil[source]); Assert.Equal(100 + Two, mule.Gil[carrier]);

        engine = Conductor();
        var resumed = await engine.StartAsync(new(Guid.Empty, 0, [], []), new());
        Assert.True(resumed.State == "Completed", resumed.Code + ":" + resumed.Detail);
        Assert.Equal([Five, Five - Two], owner.SentGil);
        Assert.Equal(0, owner.Gil[source]); Assert.Equal(Five, owner.Gil[destination]);
        Assert.Equal(100, mule.Gil[carrier]); Assert.Equal(10, mule.Units);
        Assert.Empty(engine.Journal().Custody);
    }

    // Live 2026-11-24: a 66,198,372-gil hand-out moved 45 of its million-gil windows, then the next window closed before
    // anything was offered. Before the resume, the owner gained 4,995 Cobalt Joint Plates from ordinary play that the
    // carrier never saw, and every resume refused because that unrelated key did not reconcile: the run was stranded
    // holding 45M with its new transfers refused. A change on one side alone is outside the trade and must not block
    // settlement; a move that mirrors between the two (left the owner, reached the carrier) still refuses, by name.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialGilHandOutSettlesPastOneSidedChangesButNotMirroredOnes(bool mirrored)
    {
        const int Total = 66_198_372, Moved = 45_000_000, Carried = 40_199_677, Held = 919_387_159;
        const uint Plates = 7023;
        owner.Gil[source] = Held; mule.Gil[carrier] = Carried;
        owner.Other[source] = new(Plates, false, 10_000);
        owner.PartialGil = mule.PartialGil = Moved;
        var interrupted = await engine.StartAsync(Transfer(source, destination, Total, false), cfg);
        Assert.True(interrupted is { State: "Indeterminate", CanResume: true }, interrupted.State + " " + interrupted.Code + ":" + interrupted.Detail);
        Assert.Equal((Held - Moved, Carried + Moved), (owner.Gil[source], mule.Gil[carrier]));
        if (mirrored) { owner.Other[source] = new(Plates, false, 10_000 - 100); mule.Other[carrier] = new(Plates, false, 100); }
        else owner.Other[source] = new(Plates, false, 10_000 + 4_995);

        engine = Conductor();
        var resumed = await engine.StartAsync(new(Guid.Empty, 0, [], []), new());
        if (mirrored)
        {
            Assert.True(resumed is { State: "Indeterminate", Code: "UnmanifestedTransfer" }, resumed.State + " " + resumed.Code + ":" + resumed.Detail);
            Assert.Contains("item 7023 NQ", resumed.Detail); Assert.Contains("Character 1 -100, Character 3 +100", resumed.Detail);
            Assert.Equal([Total], owner.SentGil); Assert.Empty(engine.Journal().Custody);
            return;
        }
        Assert.True(resumed.State == "Completed", resumed.Code + ":" + resumed.Detail);
        Assert.Equal([Total, Total - Moved], owner.SentGil);
        Assert.Equal((Held - Total, Total, Carried), (owner.Gil[source], owner.Gil[destination], mule.Gil[carrier]));
        Assert.Equal(10_000 + 4_995, owner.Other[source].Units); Assert.Empty(engine.Journal().Custody);
    }

    // A funding leg into another region: hand-in at the bridge, then the shopper goes home alongside the
    // carrier, and the run ends on the shopper (no switch back to the primary).
    [Fact]
    public async Task CrossRegionFundLegReturnsDestinationHomeAndStaysOnShopper()
    {
        cfg = cfg with { LocalRoster = [source, shopper] };
        owner.Gil[source] = 3_000_000; mule.Gil[carrier] = 100;
        var funding = Transfer(source, shopper, 3_000_000, true);
        Assert.True(funding.Trips.Single().Dropoff.IsPlaza);
        var result = await engine.StartAsync(funding, cfg with { ReturnOwner = false, ReturnCarrier = true });
        var log = string.Join(" | ", owner.Log);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail + " :: " + log);
        Assert.Equal(["Switch:Shopper"], owner.Log.Where(x => x.StartsWith("Switch:", StringComparison.Ordinal)));
        int handIn = owner.Log.IndexOf("Receive:Shopper@21"), home = owner.Log.IndexOf("ReturnHome:Shopper");
        Assert.True(handIn >= 0 && home > handIn, log);
        Assert.Equal(shopper, owner.Who); Assert.Equal(100u, owner.World);
        Assert.Contains("ReturnHome:Character 3", mule.Log); Assert.Equal(1u, mule.World);
        Assert.Equal(3_000_000, owner.Gil[shopper]); Assert.Equal(0, owner.Gil[source]); Assert.Equal(100, mule.Gil[carrier]);
    }

    // A haul leg out of another region: the shopper hands off at the bridge, then goes home before the
    // primary logs in, so it is never left on a visited data center.
    [Fact]
    public async Task CrossRegionHaulLegReturnsSourceHomeBeforeSwitchingToPrimary()
    {
        cfg = cfg with { LocalRoster = [source, shopper] };
        owner.Quantities[shopper] = 5; mule.Gil[carrier] = 100;
        var haul = Transfer(shopper, source, 0, false, new CourierTransferItem(Ore, "Titanium Ore", false, 5));
        var result = await engine.StartAsync(haul, cfg);
        var log = string.Join(" | ", owner.Log);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail + " :: " + log);
        int handOff = owner.Log.IndexOf("Send:Shopper@21"), home = owner.Log.IndexOf("ReturnHome:Shopper"),
            primary = owner.Log.LastIndexOf("Switch:Character 1");
        Assert.True(handOff >= 0 && home > handOff && primary > home, log);
        Assert.Equal(source, owner.Who); Assert.Equal(100u, owner.WorldOf(shopper));
        Assert.Empty(engine.Journal().Custody);
        Assert.Equal(5, owner.Quantities[source]); Assert.Equal(0, owner.Quantities[shopper]); Assert.Equal(10, mule.Units);
    }

    // An unsettled journaled consolidation (or one that left retrieved units in destination bags)
    // must not silently start a fresh moves-only pass that strands items in bags.
    [Theory]
    [InlineData(OperationStatuses.Indeterminate, 0)]
    [InlineData(OperationStatuses.Succeeded, 2)]
    public async Task PendingJournaledConsolidationReconcilesJournalAndBagsBeforeResuming(string journalStatus, int bagDelta)
    {
        consolidate = true;
        owner.ConsolidationJournalStatus = journalStatus;
        owner.ConsolidationResult = new Result<bool>(CourierOutcome.Indeterminate, "ConsolidationUnverified", "fixture");
        var interrupted = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Indeterminate", interrupted.State);
        if (bagDelta != 0) owner.Quantities[destination] = owner.Quantities.GetValueOrDefault(destination) + bagDelta;
        owner.ConsolidationResult = null;
        engine = Conductor();
        var resumed = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("ConsolidationUnsettled", resumed.Code);
        Assert.Single(owner.Consolidations);
    }

    [Fact]
    public async Task LostReplyAndCarrierRestartCannotReceiveTwiceOrRebindToAnotherEndpoint()
    {
        var endpoint=Guid.NewGuid();
        var m=new CourierMessage(1,Guid.NewGuid(),Guid.NewGuid(),CourierCommand.Receive,source,source,carrier,Plan().Trips[0].Manifest);
        var begin=m with {Step=Guid.NewGuid(),Command=CourierCommand.Begin,Manifest=[]};
        Assert.Equal(CourierOutcome.Succeeded,(await follower.HandleAsync(source,begin,Deadline.After("begin",10),endpoint)).Outcome);
        var first=await follower.HandleAsync(source,m,Deadline.After("receive",10),endpoint);
        Assert.Equal(CourierOutcome.Succeeded,first.Outcome);
        var credited=mule.Units;
        follower.Dispose();follower=Follower();
        var replay=await follower.HandleAsync(destination,m,Deadline.After("reconnect",10),endpoint);
        Assert.Equal(first.Value!.Step,replay.Value!.Step);
        Assert.Equal(credited,mule.Units);
        Assert.Equal("OwnerNotPaired",(await follower.HandleAsync(destination,m,Deadline.After("impostor",10),Guid.NewGuid())).Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task ReceiverRestartSettlesDurableArmExactlyOnce(int delivered)
    {
        var manifest=Plan().Trips[0].Manifest;
        var msg=new CourierMessage(1,Guid.NewGuid(),Guid.NewGuid(),CourierCommand.Receive,source,source,carrier,manifest);
        async Task<Result<CourierReceipt>> Call(CourierMessage m)=>await follower.HandleAsync(source,m,Deadline.After("restart",10));
        Assert.Equal(CourierOutcome.Succeeded,(await Call(msg with {Step=Guid.NewGuid(),Command=CourierCommand.Begin,Manifest=[]})).Outcome);
        mule.CheckpointDirectory=dir; mule.ReceiveOffset=delivered-3; mule.UncertainTrade=true;
        await Call(msg);
        follower.Dispose();
        // A hard exit has the intent and pre-arm checkpoint, but no returned receive receipt.
        var ledger=new CourierStore<CarrierLedger>(Path.Combine(dir,"courier-custody.json"));
        var interrupted=ledger.Load();
        interrupted.Steps[msg.Step]=interrupted.Steps[msg.Step] with {Result=null};
        ledger.Save(interrupted);
        follower=Follower();
        InventoryView Bag(int n)=>new(source,4,[new(Ore,false,n)]);
        var settle=msg with {Step=Guid.NewGuid(),Command=CourierCommand.Settle,ConfirmStep=msg.Step,
            Manifest=[],OwnerEvidence=new(Bag(3),Bag(3-delivered))};
        var first=await Call(settle);Assert.True(first.Outcome==CourierOutcome.Succeeded,first.Code+":"+first.Detail);
        follower.Dispose();follower=Follower();
        Assert.Equal(CourierOutcome.Succeeded,(await Call(settle)).Outcome);
        Assert.Equal(delivered,ledger.Load().Units.Sum(s=>s.Units));
        Assert.Equal(1,mule.Receives);
        var checkpoint=mule.ReceiveStore.Load();
        Assert.Null(checkpoint.Arm);Assert.Equal(3-delivered,checkpoint.Items.Single().Quantity);
    }

    // Live 2026-11-22: the carrier's window closed before lock (the owner had just logged in), so its sender ended
    // Failed rather than Cancelled. The run went indeterminate and every resume was refused TradeMayHaveOpened,
    // stranding the carrier's custody. Nothing moved, so the step must fence NotDelivered and the run carry on.
    [Fact]
    public async Task CarrierSendFailedBeforeLockFencesNotDeliveredAndResumes()
    {
        mule.BounceSendRecord = Path.Combine(dir, "carrier-trade.json"); owner.CancelNextReceive = true;
        var bounced = await engine.StartAsync(Plan(), cfg);
        Assert.True(bounced is { State: "Interrupted", Code: "CancelledPreLock", CanResume: true }, bounced.State + " " + bounced.Code + ":" + bounced.Detail);
        Assert.Contains(engine.Journal().Evidence, e => e.Disposition == "NotDelivered");
        var resumed = await engine.StartAsync(Plan(), cfg);
        Assert.True(resumed.State == "Completed", resumed.Code + ":" + resumed.Detail);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units); Assert.Empty(engine.Journal().Custody);
    }

    // A caller that crashed before saving its acknowledgement resubmits the same RequestId after other runs have
    // replaced the journal. Starting it again would repeat a real cross-character transfer.
    [Fact]
    public async Task CompletedRequestResubmittedAfterLaterRunsStartsNothing()
    {
        owner.Quantities[source] = 10;
        var session = new RQ.UI.CourierSession(new StateRepository(new QuartermasterStateStore(Path.Combine(dir, "state.json"))),
            _ => throw new InvalidOperationException("Not used"), _ => throw new InvalidOperationException("Not used"))
        { Conductor = engine, Link = new CarrierLink(new(carrier, new(carrier, 4, [new(Ore, false, mule.Units)]), [], true, [source], true)) };
        var ipc = new RQ.Interop.CourierIpcService("fixture", session, () => cfg,
            () => new InventoryView(owner.Who, 4, [new(Ore, false, owner.Units)], owner.GilHeld), () => null, Region, _ => "World",
            id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 999), _ => { });
        RQ.Interop.CourierTransferAcknowledgement Submit(string id, int quantity) => System.Text.Json.JsonSerializer.Deserialize<RQ.Interop.CourierTransferAcknowledgement>(
            ipc.Submit(System.Text.Json.JsonSerializer.Serialize(new RQ.Interop.CourierTransferRequest
            {
                Schema = RQ.Interop.CourierIpcService.TransferSchema, RequestId = id, ReturnOwner = true, ReturnCarrier = true,
                Source = new() { LocalContentId = 1, HomeWorldId = 1, CharacterName = source.CharacterName },
                Destination = new() { LocalContentId = 2, HomeWorldId = 1, CharacterName = destination.CharacterName },
                Items = [new() { ItemId = Ore, ItemName = "Titanium Ore", Quantity = quantity }],
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        async Task<CourierJournal> Ran(string id, int quantity)
        {
            var ack = Submit(id, quantity);
            Assert.True(ack.Status == "accepted", ack.Status + " " + ack.ErrorCode + ": " + ack.Message);
            for (var waited = 0; session.IsStarting && waited < 500; waited++) await Task.Delay(10);
            var done = engine.Journal();
            Assert.True(done.State == "Completed" && done.RequestId == id, done.State + " " + done.Code + ": " + done.Detail);
            return done;
        }

        var first = await Ran("transfer-a", 3);
        var second = await Ran("transfer-b", 2); // The IPC archives the first run before starting.
        var third = await engine.StartAsync(Transfer(source, destination, 0, false, new CourierTransferItem(Ore, "Titanium Ore", false, 1)), cfg);
        Assert.True(third.State == "Completed", third.Code + ": " + third.Detail); // A non-IPC start replaces the second run.
        int ownerSends = owner.Sends, carrierSends = mule.Sends;

        foreach (var earlier in new[] { first, second })
        {
            var replay = Submit(earlier.RequestId!, earlier.RequestId == "transfer-a" ? 3 : 2);
            Assert.True(replay.Status == "already_completed", replay.Status + " " + replay.ErrorCode + ": " + replay.Message);
            Assert.Equal(earlier.Run.ToString(), replay.RunId);
        }
        Assert.False(session.IsStarting);
        Assert.Equal(third.Run, engine.Journal().Run);
        Assert.Equal((ownerSends, carrierSends), (owner.Sends, mule.Sends));
        Assert.Equal(6, owner.Quantities[destination]); Assert.Equal(4, owner.Quantities[source]); Assert.Equal(10, mule.Units);
    }
    sealed class CarrierLink(RQ.UI.CourierPeer carrier) : RQ.UI.ICourierLink
    {
        public MeetingBell? StandingAt => null;
        public string? BellBlock => null;
        public IReadOnlyList<RQ.UI.CourierPeer> Peers { get; } = [carrier];
    }

    sealed class AR : IAutoRetainerIpc
    {
        public bool IsAvailable => true; public bool IsBusy => false; public bool IsSuppressed { get; private set; }
        public void SetSuppressed(bool value) => IsSuppressed = value;
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void Dispose() { }
    }
    sealed class Wire : ICourierPeerPort
    {
        public Func<CourierMessage, Task<Result<CourierReceipt>>> Handler = null!;
        public event Action<CourierProgress>? Progress; public event Action? LinkLost;
        public event Action<Guid> StopRequested { add { } remove { } }
        public void Started(CourierProgress p)=>Progress?.Invoke(p);
        public Action? CrashCheckpoint;
        bool confirmed;
        public async Task<Result<CourierReceipt>> SendAsync(CourierMessage m, Deadline d)
        {
            Progress?.Invoke(new(m.Run, m.Step, m.Command.ToString())); var result = await Handler(m);
            if (m.Command == CourierCommand.Status && confirmed && CrashCheckpoint != null) { CrashCheckpoint(); CrashCheckpoint = null; LinkLost?.Invoke(); }
            if (m.Command == CourierCommand.Confirm) confirmed = true;
            return result;
        }
    }
    sealed class Client(OwnerScope initial) : ICourierPlacementPort, ICourierTradePort, ICourierDispatchEvidence, ICourierSettlement, ICourierHoldPort,
        ICourierReceiveAttestation, ICharacterSwitchPort, IInventoryObservationPort, IWorldVisitPort, IMeetingBellPort
    {
        // The owner's journaled placements since a hand-in, and the dispositions its receive checkpoint was closed under.
        public CourierPlacedSince? Placed;
        public CourierPlacedSince? PlacedSince(OwnerScope owner, DateTimeOffset since) => Placed;
        public readonly List<string> Attested = [];
        public Task<Result<bool>> AttestReceived(Guid step, OwnerScope partner, TradeEvidence sender, string disposition, Deadline d, bool commit = true)
        { if (commit) Attested.Add(disposition); return OK(true); }
        OwnerScope who = initial;
        public OwnerScope Who => who;
        public Dictionary<OwnerScope, int> Quantities = new(); public int Units => Quantities.GetValueOrDefault(who);
        public Dictionary<OwnerScope, int> Gil = new(); public int GilHeld => Gil.GetValueOrDefault(who);
        readonly Dictionary<OwnerScope, uint> worlds = new();
        public uint WorldOf(OwnerScope o) => worlds.TryGetValue(o, out var w) ? w : o.HomeWorldId ?? 1;
        public uint World => WorldOf(who);
        public readonly List<string> Log = [];
        public readonly List<int> SentGil = [];
        // The next gil trade moves only this much, then reports an unsettled closure.
        public int? PartialGil;
        // One unrelated stack per character, outside every manifest (ordinary play changes it between steps).
        public Dictionary<OwnerScope, BagStock> Other = new();
        InventoryView View() => new(who, 4, Other.TryGetValue(who, out var other) ? [new(Ore, false, Units), other] : [new(Ore, false, Units)], GilHeld);
        static int GilIn(ImmutableArray<Shipment> m) => m.Where(x => x.IsGil).Sum(x => x.Units);
        static int ItemsIn(ImmutableArray<Shipment> m) => m.Where(x => !x.IsGil).Sum(x => x.Units);
        bool Partial(ImmutableArray<Shipment> m, int sign)
        {
            if (PartialGil is not { } part || GilIn(m) == 0) return false;
            PartialGil = null; recoverable = CourierGame.Step.Value;
            Quantities[who] = Units + sign * ItemsIn(m); Gil[who] = GilHeld + sign * part;
            return true;
        }
        public int Deposited, Sends, ReceiveOffset;
        static Task<Result<T>> OK<T>(T value) => Task.FromResult(Result<T>.Ok(value));
        public bool UncertainTrade;
        public bool LoseSenderReply;
        public string? CheckpointDirectory;
        public int Receives;
        public CourierStore<TradeCheckpoint> ReceiveStore=>new(Path.Combine(CheckpointDirectory!,"courier-trade.json"));
        public Task<Result<IDisposable>> BeginHold(Guid run,Deadline deadline)=>OK<IDisposable>(new MemoryStream());
        public void CheckHold(){}
        Guid? recoverable, openedStep;
        // Set: the next send bounces like a partner window that closed before lock, persisting the real port's
        // checkpoint shape (dispatched, unchanged queue, no lock or confirmation marker); never-opened is then
        // proven from that record by the production predicate. The next receive likewise ends cancelled pre-lock.
        public string? BounceSendRecord; public bool CancelNextReceive; bool bounced;
        public Task<Result<bool>> WasNotOpened(Guid step,Deadline d)=>OK(BounceSendRecord is {} path
            ? CourierTrade.NeverCredited(new CourierStore<TradeCheckpoint>(path).Load(),step,true) : step!=openedStep);
        public Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,ImmutableArray<Shipment> credit,Deadline d){
            if(CheckpointDirectory!=null){
                var store=ReceiveStore;var record=store.Load();
                return Task.FromResult(CourierReceiveRecovery.Settle(record,step,partner,View(),peer,()=>store.Save(record)));
            }
            return recoverable==step ? OK(true) :
            Task.FromResult(Result<bool>.Fail("NoVerifiedCheckpoint","No matching locked-window proof.")); }
        public Task<Result<bool>> SendAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) {
            openedStep=CourierGame.Step.Value;CourierGame.TradeStarted.Value?.Invoke();
            if(BounceSendRecord is {} record && !bounced){
                bounced=true;
                new CourierStore<TradeCheckpoint>(record).Save(new(){Step=CourierGame.Step.Value,DispatchAuthorized=true,
                    Items=[..m.Select(x=>new Franthropy.Dalamud.Automation.Trades.TradeItem{ItemId=x.Item,Quantity=x.Units})],
                    Original=[..m.Select(x=>new BagStock(x.Item,false,x.Units))]});
                return Task.FromResult(Result<bool>.Fail("TradeOpenBounced","Window closed before lock."));
            }
            Sends++; Log.Add($"Send:{who.CharacterName}@{World}");
            if (GilIn(m) > 0) SentGil.Add(GilIn(m));
            if (Partial(m, -1)) return Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate, "TradeClosureUnproven", "Partial gil."));
            Quantities[who] = Units - ItemsIn(m); Gil[who] = GilHeld - GilIn(m);
            if(LoseSenderReply){LoseSenderReply=false; recoverable=CourierGame.Step.Value;
                return Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate,"AwaitingVerification","Reply lost."));}
            return OK(true);
        }
        public Task<Result<bool>> ReceiveAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) {
            openedStep=CourierGame.Step.Value;CourierGame.TradeStarted.Value?.Invoke();
            if(CancelNextReceive){CancelNextReceive=false;
                return Task.FromResult(new Result<bool>(CourierOutcome.Cancelled,"ReceiveStopped","Stopped before lock."));}
            Receives++; Log.Add($"Receive:{who.CharacterName}@{World}");
            if (Partial(m, 1)) return Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate, "TradeClosureUnproven", "Partial gil."));
            if(CheckpointDirectory!=null){
                var step=CourierGame.Step.Value;var item=m.Single();
                ReceiveStore.Save(new(){Step=step,Receiving=true,Arm=new(step,partner,View()),
                    Items=[new(){ItemId=item.Item,Quantity=item.Units}],Original=[new(item.Item,false,item.Units)]});
            }
            Quantities[who] = Units + ItemsIn(m) + ReceiveOffset; Gil[who] = GilHeld + GilIn(m); return UncertainTrade ? Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate, "LockUnknown", "Missing locked partner evidence")) : OK(true); }
        public Task<Result<bool>> CancelPreLockAsync(Deadline d) => OK(true);
        public int Retrieved;
        public bool? FailPlacement;
        readonly Dictionary<string,(ImmutableArray<PlacementTally> Lines,string Status)> movements=[];
        public ImmutableArray<PlacementTally>? JournalUnits(string id,OwnerScope owner)=>movements.TryGetValue(id,out var m)?m.Lines:null;
        // A failing batch stops after one verified unit, journaled like the coordinator's lines.
        public Task<Result<bool>> MoveBatchAsync(StowagePlan plan,ImmutableArray<Shipment> batch,bool deposit,IDisposable lease,Func<bool> auth,Deadline d,Action<string> recordOperation)
        {
            Assert.True(auth()); Assert.Equal(plan.Owner, who); Assert.All(batch,s=>Assert.Equal(deposit ? 22ul : 11ul, deposit ? s.To : s.From));
            var id=Guid.NewGuid().ToString();recordOperation(id);
            bool fail=FailPlacement==deposit;int n=fail?1:batch.Sum(s=>s.Units),left=n;
            var lines=deposit
                ? CourierPlacementBatch.Deposits(batch).Select(r=>(r.Line,r.Item,r.Hq,Units:r.Shipments.Sum(s=>s.Units)))
                : batch.GroupBy(s=>(s.Item,s.Hq)).Select(g=>(g.First().Line,g.Key.Item,g.Key.Hq,Units:g.Sum(s=>s.Units)));
            movements[id]=([..lines.Select(l=>{var u=Math.Min(left,l.Units);left-=u;return new PlacementTally(l.Line,l.Item,l.Hq,u);})],
                fail?OperationStatuses.Failed:OperationStatuses.Succeeded);
            Quantities[who]=Units+(deposit?-n:n);if(deposit)Deposited+=n;else Retrieved+=n;
            if(fail){FailPlacement=null;return Task.FromResult(Result<bool>.Fail("PlacementStopped","fixture"));}
            return OK(true);
        }
        public readonly List<OwnerScope> Consolidations = [];
        public Result<bool>? ConsolidationResult;
        public string? ConsolidationJournalStatus = OperationStatuses.Indeterminate;
        public string? OperationStatus(string id, OwnerScope owner) => movements.TryGetValue(id,out var m) ? m.Status : ConsolidationJournalStatus;
        public Task<Result<bool>> ConsolidateAsync(StowagePlan plan, ImmutableArray<CourierConsolidation> lines, IDisposable lease, Func<bool> auth, Deadline d, Action<string> recordOperation)
        {
            Assert.True(auth()); Assert.Equal(plan.Owner, who);
            Assert.All(lines, l => Assert.True(l.Destination.Matches(who)));
            recordOperation(Guid.NewGuid().ToString());
            Consolidations.Add(who);
            return Task.FromResult(ConsolidationResult ?? Result<bool>.Ok(true));
        }
        public Task<Result<InventoryView>> ObserveAsync(Deadline d) => OK(View());
        public Task<Result<string>> SwitchRefusalAsync(Deadline deadline) => OK("");
        public Task<Result<OwnerScope>> SwitchAsync(OwnerScope target, Deadline d) { Log.Add($"Switch:{target.CharacterName}"); return OK(who = target); }
        public Task<Result<bool>> EnsureWorldAsync(uint world, Deadline d) { Log.Add($"World:{who.CharacterName}@{world}"); worlds[who] = world; return OK(true); }
        public Task<Result<bool>> ReturnHomeAsync(Deadline d) { Log.Add($"ReturnHome:{who.CharacterName}"); worlds[who] = who.HomeWorldId ?? 1; return OK(true); }
        public Task<Result<bool>> GoInRangeAsync(MeetingBell bell, Deadline d) { Log.Add($"Meet:{bell.Label}@{World}"); return OK(true); }
        public Task<Result<CourierPosition>> PositionAsync(Deadline d) => OK(new CourierPosition(World, 1, 0, 0, 0));
        public Task<Result<bool>> ReturnToAsync(CourierPosition position, Deadline d) => OK(true);
        public Task<Result<bool>> ObserveTradeableAsync(OwnerScope character, Deadline d) => OK(true);
    }
}
