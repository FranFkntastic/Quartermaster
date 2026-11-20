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
    readonly MeetingBell bell = new(1, 1, 1, 0, 0, 0, "Fixture bell");

    public CourierRunTests()
    {
        cfg = new() { Carrier = carrier, LocalRoster=[source,destination], PairedOwner = source, CarrierOptIn = true, Bells = ImmutableDictionary<ulong, MeetingBell>.Empty.Add(1, bell).Add(2, bell) };
        line = new()
        {
            StowagePlanId = plan.Id,
            ItemId = 1,
            ItemName = "Titanium Ore",
            TargetQuantity = 7,
            Quality = ItemQualityPolicy.NqOnly,
            Courier = new(destination, 11),
            Routing = new() { PreferredRetainerIds = [22] },
        };
        mule.Quantities[carrier] = 10; // Private carrier stock must survive.
        follower = Follower(); wire.Handler = m => follower.HandleAsync(source, m, Deadline.After("Fixture", 30),started:wire.Started);
        engine = Conductor();
    }
    CourierFollower Follower(string? root = null) => new(root ?? dir, () => cfg, new(), new(new AR()), mule, mule, mule, mule, _ => { });
    CourierConductor Conductor(string? root = null) => new(root ?? dir, wire, owner, owner, owner, owner, owner, owner, new(), new(new AR()), _ => { });
    CourierPlan Plan() => CourierPlanner.Build(plan, [line],
        [new(source,null,new(source,5,[])),new(source,11,new(source,0,[new(1,false,7)])),
        new(destination,null,new(destination,5,[])),new(destination,22,new(destination,8,[]))],
        new(carrier, 4, [new(1, false, 10)]), cfg, id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 3), 1);

    [Fact]
    public void LegacyBellRoundTripsWithoutChangingReplayIdentity()
    {
        const string old="""{"Territory":1,"World":1,"ObjectId":1,"X":0,"Y":0,"Z":0,"Label":"Fixture bell"}""";
        var restored=System.Text.Json.JsonSerializer.Deserialize<MeetingBell>(old)!;
        Assert.True(bell.SamePlace(restored));
        Assert.Equal(old,System.Text.Json.JsonSerializer.Serialize(restored));
    }

    [Fact]
    public async Task IdenticalBellInDifferentWardRequiresCarrierTravel()
    {
        var pickup=bell with {Territory=339,Ward=1};
        var drop=pickup with {Ward=2};
        cfg=cfg with {Bells=cfg.Bells.SetItem(1,pickup).SetItem(2,drop)};
        var plan=Plan();
        var result=await engine.StartAsync(plan,cfg);
        Assert.True(result.State=="Completed",result.Code+":"+result.Detail);
        Assert.Equal(plan.Trips.SelectMany(t=>new[]{t.Pickup,t.Dropoff}),mule.Approaches);
    }

    [Fact]
    public async Task CarrierCapSplitsTripsAndDeliversExactUnits()
    {
        var p = Plan(); Assert.Equal(3, p.Trips.Length);
        var result = await engine.StartAsync(p, cfg);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
        Assert.Empty(new AtomicDocumentStore<CarrierLedger>(Path.Combine(dir, "courier-custody.json")).Load().Units);
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public async Task ReturnFailureOnlyCompletesWithEmptyCustody(bool ownerReturn,bool held)
    {
        var p=Plan();
        owner.FailReturnSwitch=ownerReturn;
        if(!ownerReturn)mule.ReturnFailure=()=>{
            if(held)
            {
                var store=new CourierStore<CarrierLedger>(Path.Combine(dir,"courier-custody.json"));
                var ledger=store.Load();ledger.Units=[p.Trips[0].Manifest[0]];store.Save(ledger);
            }
            return new(CourierOutcome.Indeterminate,"CarrierReturnDeadline","ward arrival timed out");
        };
        var result=await engine.StartAsync(p,cfg);
        Assert.All(result.Lines,l=>Assert.Equal(0,l.Remaining));
        Assert.Equal(held?"Indeterminate":"Completed",result.State);
        Assert.Equal(held?"ReturnCustodyUnsettled":ownerReturn?"CharacterSwitchDeadline":"CarrierReturnDeadline",result.Code);
        if(!held)Assert.Contains(ownerReturn?"login timed out":"ward arrival timed out",engine.Snapshot().Detail);
        Assert.Equal(held,new CourierStore<CarrierLedger>(Path.Combine(dir,"courier-custody.json")).Load().Run.HasValue);
    }
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
    [InlineData(-3, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task UnverifiedHandOffPreservesBothObservationsAndNeverReplays(int offset, bool uncertainLock)
    {
        mule.ReceiveOffset = offset; mule.UncertainTrade = uncertainLock;
        var result = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Indeterminate", result.State); Assert.Equal(uncertainLock ? "LockUnknown" : "HandOffDeltaMismatch", result.Code);
        var r = new AtomicDocumentStore<CourierJournal>(Path.Combine(dir, "courier-run.json")).Load();
        var e = Assert.Single(r.Evidence); Assert.NotNull(e.Owner); Assert.NotNull(e.Carrier);
        Assert.Equal(3, e.Owner.Before.Count(1, false) - e.Owner.After.Count(1, false));
        Assert.Equal(3 + offset, e.Carrier.After.Count(1, false) - e.Carrier.Before.Count(1, false));
        Assert.Empty(r.Custody); engine = Conductor(); Assert.Equal("Indeterminate", (await engine.StartAsync(Plan(), cfg)).State);
        Assert.Equal(1, owner.Sends); Assert.Equal(0, owner.Deposited);
    }
    [Fact]
    public async Task StopBeforeLockKeepsBagsAndAllowsForwardRestart()
    {
        owner.PauseTrade = mule.PauseTrade = true;
        var run = engine.StartAsync(Plan(), cfg);
        await Task.WhenAll(owner.Opened.Task, mule.Opened.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();
        var paused = await run;
        Assert.True(paused.State == "Interrupted", paused.Code + ":" + paused.Detail);
        Assert.Equal("CancelledPreLock", paused.Code);
        Assert.Equal(3, owner.Units); Assert.Equal(10, mule.Units);
        owner.PauseTrade = mule.PauseTrade = false;
        var finished = await engine.StartAsync(Plan(), cfg);
        Assert.True(finished.State == "Completed", finished.Code + ":" + finished.Detail);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
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
    public async Task UnstartedTradeIsFencedBeforeClearOrResume(bool refuseOnCarrier)
    {
        var handle=wire.Handler;
        CourierMessage? delayed=null;
        mule.RefusePartner=refuseOnCarrier;
        wire.Handler=m=>{
            if(m.Command==CourierCommand.Receive && delayed==null){
                delayed=m;
                if(!refuseOnCarrier)return Task.FromResult(Result<CourierReceipt>.Fail("StartNotReceived","fixture"));
            }
            return handle(m);
        };
        var result=await engine.StartAsync(Plan(),cfg);
        Assert.True(result.CanResume && result.CanClear,result.Code+":"+result.Detail);
        Assert.Equal(0,owner.Sends);Assert.Equal(0,mule.Receives);
        Assert.Equal("NotDelivered",(await handle(delayed!)).Code);
        mule.RefusePartner=false;
        if(refuseOnCarrier){
            wire.Handler=m=>Task.FromResult(Result<CourierReceipt>.Fail("CarrierEndpointAmbiguous","offline"));
            Assert.Equal("Idle",(await engine.ClearAsync(result.Run,null)).State);
            wire.Handler=handle;
        }
        Assert.Equal("Completed",(await engine.StartAsync(Plan(),cfg)).State);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostPlacementReceiptResumesOnlyRemainingUnits(bool deposit)
    {
        owner.FailPlacement=deposit;
        var result=await engine.StartAsync(Plan(),cfg);
        Assert.False(result.CanClear);Assert.True(result.CanResume);
        engine=Conductor();
        var resumed=await engine.StartAsync(Plan(),cfg);
        Assert.True(resumed.State=="Completed",resumed.Code+":"+resumed.Detail);
        Assert.Equal(7,owner.Deposited);Assert.Equal(7,owner.Retrieved);
        Assert.Equal(10,mule.Units);
    }
    [Fact]
    public async Task ReverseRunCanStartFromSecondPairedOwner()
    {
        cfg=cfg with {PairedOwners=[destination]};
        await owner.SwitchAsync(destination,Deadline.After("fixture",5));
        wire.Handler=m=>follower.HandleAsync(destination,m,Deadline.After("fixture",10),started:wire.Started);
        var p=Plan();
        var reverse=p with {Trips=[..p.Trips.Select(t=>t with {Source=destination,Destination=source,
            Manifest=[..t.Manifest.Select(s=>s with {Source=destination,Destination=source})]})]};
        var result=await engine.StartAsync(reverse,cfg);
        Assert.True(result.State=="Completed",result.Code+":"+result.Detail);
        Assert.Equal(7,owner.Deposited);Assert.Equal(10,mule.Units);
    }
    [Fact]
    public async Task LegacyUnopenedIntentsCanResumeWithoutRetrievingTwice()
    {
        var p=Plan();var trip=p.Trips[0];var run=Guid.NewGuid();var step=Guid.NewGuid();
        var action=new CourierStep(CourierPhase.HandOut,0,trip.Manifest);
        var before=new InventoryView(source,4,[new(1,false,3)]);
        var cb=new InventoryView(carrier,4,[new(1,false,10)]);
        owner.Quantities[source]=3;
        new CourierStore<CourierJournal>(Path.Combine(dir,"courier-run.json")).Save(new(){
            Run=run,Plan=p,Settings=cfg,Start=source,PeerClaimed=true,State="Indeterminate",
            Schedule=[action,new(CourierPhase.SwitchToDestination,0,[]),new(CourierPhase.HandIn,0,trip.Manifest),
                new(CourierPhase.Deposit,0,trip.Manifest),new(CourierPhase.Finish,0,[])],
            Pending=new(step,action,before,cb)});
        var msg=new CourierMessage(1,run,step,CourierCommand.Receive,source,source,carrier,trip.Manifest);
        new CourierStore<CarrierLedger>(Path.Combine(dir,"courier-custody.json")).Save(new(){
            Run=run,Unconfirmed=step,Steps={[step]=new("legacy",msg,null,cb)}});
        var result=await engine.StartAsync(p,cfg);
        Assert.True(result.State=="Completed",result.Code+":"+result.Detail);
        Assert.Equal(0,owner.Retrieved);Assert.Equal(3,owner.Deposited);Assert.Equal(1,owner.Sends);
    }
    [Fact]
    public async Task PreBeginFailureCreatesNoRunToResume()
    {
        wire.Handler=m=>Task.FromResult(Result<CourierReceipt>.Fail("CarrierEndpointAmbiguous","offline"));
        var failed=await engine.StartAsync(Plan(),cfg);
        Assert.Equal("CarrierEndpointAmbiguous",failed.Code);
        Assert.False(File.Exists(Path.Combine(dir,"courier-run.json")));
        Assert.False(failed.CanResume);Assert.False(failed.CanClear);
    }
    [Fact]
    public async Task StoppedCarrierKeepsLeaseAcrossUnconfirmedAndCreditedCustody()
    {
        follower.Dispose();var lease=new AutomationLease();var ar=new AR();
        follower=new(dir,()=>cfg,lease,new(ar),mule,mule,mule,mule,_=>{});
        var m=new CourierMessage(1,Guid.NewGuid(),Guid.NewGuid(),CourierCommand.Begin,source,source,carrier,[]);
        async Task<Result<CourierReceipt>> Send(CourierMessage msg)=>await follower.HandleAsync(source,msg,Deadline.After("fixture",10));
        Assert.Equal(CourierOutcome.Succeeded,(await Send(m)).Outcome);
        var recv=m with {Step=Guid.NewGuid(),Command=CourierCommand.Receive,Manifest=Plan().Trips[0].Manifest};
        Assert.Equal(CourierOutcome.Succeeded,(await Send(recv)).Outcome);
        await follower.StopLocalAsync();Assert.True(ar.IsSuppressed);Assert.False(lease.CanAcquire);
        InventoryView Bags(int n)=>new(source,5,[new(1,false,n)]);
        Assert.Equal(CourierOutcome.Succeeded,(await Send(m with {Step=Guid.NewGuid(),Command=CourierCommand.Confirm,
            ConfirmStep=recv.Step,OwnerEvidence=new(Bags(3),Bags(0))})).Outcome);
        var status=await Send(m with {Step=Guid.NewGuid(),Run=Guid.NewGuid(),Command=CourierCommand.Status});
        Assert.Equal(m.Run,status.Value?.ActiveRun);Assert.Equal(3,status.Value!.Custody.Sum(x=>x.Units));
        Assert.True(ar.IsSuppressed);Assert.False(lease.CanAcquire);
        follower.Dispose();Assert.False(ar.IsSuppressed);Assert.True(lease.CanAcquire);
    }
    public void Dispose() { follower.Dispose(); Directory.Delete(dir, true); }

    // Destination consolidation runs once per destination, after that destination's final deposit,
    // as the destination character, under the run's lease.
    [Fact]
    public async Task ConsolidatingLineGathersOnceAfterTheFinalDeposit()
    {
        line.Routing.Consolidate = true;
        var p = Plan();
        Assert.Equal(3, p.Trips.Length);
        var gather = Assert.Single(p.Consolidations);
        Assert.Equal(22ul, gather.Retainer);
        var result = await engine.StartAsync(p, cfg);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail);
        var call = Assert.Single(owner.Consolidations);
        Assert.True(call.Who.Matches(destination));
        Assert.Equal(7, call.DepositedBefore);
        Assert.Equal(line.Id, Assert.Single(call.Lines).Line);
    }

    [Fact]
    public async Task UnconsolidatedPlanNeverCallsConsolidation()
    {
        var result = await engine.StartAsync(Plan(), cfg);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail);
        Assert.Empty(owner.Consolidations);
    }

    // A settled consolidation shortfall leaves the delivery standing: the run completes and says so.
    [Fact]
    public async Task ConsolidationShortfallCompletesAsConsolidationLimited()
    {
        line.Routing.Consolidate = true;
        owner.ConsolidationResult = Result<bool>.Fail("ConsolidationLimited", "Grungel is full");
        var result = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Completed", result.State);
        Assert.Equal("ConsolidationLimited", result.Code);
        Assert.Contains("Grungel is full", result.Detail);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
    }

    // A pre-journal consolidation interruption (nothing recorded, bags unchanged) retries cleanly on resume.
    [Fact]
    public async Task PreJournalConsolidationInterruptionRetriesOnResume()
    {
        line.Routing.Consolidate = true;
        owner.SkipConsolidationRecord = true;
        owner.ConsolidationResult = new Result<bool>(CourierOutcome.Indeterminate, "ConsolidationUnverified", "fixture");
        var interrupted = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Indeterminate", interrupted.State);
        Assert.True(interrupted.CanResume);
        owner.SkipConsolidationRecord = false;
        owner.ConsolidationResult = null;
        engine = Conductor();
        var resumed = await engine.StartAsync(Plan(), cfg);
        Assert.True(resumed.State == "Completed", resumed.Code + ":" + resumed.Detail);
        Assert.Equal(2, owner.Consolidations.Count);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
    }

    // An unsettled journaled consolidation (or one that left retrieved units in destination bags)
    // must not silently start a fresh moves-only pass that strands items in bags.
    [Theory]
    [InlineData(OperationStatuses.Indeterminate, 0, "ConsolidationUnsettled")]
    [InlineData(OperationStatuses.Succeeded, 2, "ConsolidationUnsettled")]
    [InlineData(OperationStatuses.Succeeded, 0, "Succeeded")]
    public async Task PendingJournaledConsolidationReconcilesJournalAndBagsBeforeResuming(string journalStatus, int bagDelta, string expectedCode)
    {
        line.Routing.Consolidate = true;
        owner.ConsolidationJournalStatus = journalStatus;
        owner.ConsolidationResult = new Result<bool>(CourierOutcome.Indeterminate, "ConsolidationUnverified", "fixture");
        var interrupted = await engine.StartAsync(Plan(), cfg);
        Assert.Equal("Indeterminate", interrupted.State);
        if (bagDelta != 0) owner.Quantities[destination] = owner.Quantities.GetValueOrDefault(destination) + bagDelta;
        owner.ConsolidationResult = null;
        engine = Conductor();
        var resumed = await engine.StartAsync(Plan(), cfg);
        Assert.Equal(expectedCode, resumed.Code);
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
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task MissingOrRefusedHoldNeverDispatchesEitherTrade(bool onCarrier,bool missing)
    {
        (onCarrier?mule:owner).Hold = new("fixture",()=>true,()=>!missing,
            ()=>"""{"Acknowledged":false,"Reason":"held by other courier"}""",()=>false,()=>{},_=>{});
        var stopped=await engine.StartAsync(Plan(),cfg);
        Assert.Equal(missing?"TradeHoldUnavailable":"TradeHoldRefused",stopped.Code);
        Assert.Contains(missing?"gates":"other courier",stopped.Detail);
        Assert.Equal(0,owner.Sends+mule.Sends+owner.Receives+mule.Receives);
    }

    [Theory]
    [InlineData(3)]
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
        InventoryView Bag(int n)=>new(source,4,[new(1,false,n)]);
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
    sealed class Client(OwnerScope initial) : ICourierPlacementPort, ICourierTradePort, ICourierDispatchEvidence, ICourierSettlement, ICourierHoldPort, ICharacterSwitchPort, IInventoryObservationPort, IWorldVisitPort, IMeetingBellPort
    {
        OwnerScope who = initial;
        public Dictionary<OwnerScope, int> Quantities = new(); public int Units => Quantities.GetValueOrDefault(who);
        public int Deposited, Sends, ReceiveOffset;
        static Task<Result<T>> OK<T>(T value) => Task.FromResult(Result<T>.Ok(value));
        public bool PauseTrade, UncertainTrade;
        public bool LoseSenderReply;
        public CourierTradeHold? Hold;
        public string? CheckpointDirectory;
        public int Receives;
        public CourierStore<TradeCheckpoint> ReceiveStore=>new(Path.Combine(CheckpointDirectory!,"courier-trade.json"));
        public Task<Result<IDisposable>> BeginHold(Guid run,Deadline deadline)=>Task.FromResult(Hold?.Begin()??Result<IDisposable>.Ok(new MemoryStream()));
        public void CheckHold()=>Hold?.Check(false);
        Guid? recoverable, openedStep;
        public Task<Result<bool>> WasNotOpened(Guid step,Deadline d)=>OK(step!=openedStep);
        public Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,Deadline d){
            if(CheckpointDirectory!=null){
                var store=ReceiveStore;var record=store.Load();
                return Task.FromResult(CourierReceiveRecovery.Settle(record,step,partner,new(who,4,[new(1,false,Units)]),peer,()=>store.Save(record)));
            }
            return recoverable==step ? OK(true) :
            Task.FromResult(Result<bool>.Fail("NoVerifiedCheckpoint","No matching locked-window proof.")); }
        public TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<Result<bool>> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<bool>> Pause() { Opened.TrySetResult(); return cancelled.Task; }
        public Task<Result<bool>> SendAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) {
            openedStep=CourierGame.Step.Value;CourierGame.TradeStarted.Value?.Invoke();
            if (PauseTrade) return Pause(); Sends++; Quantities[who] = Units - m.Sum(x => x.Units);
            if(LoseSenderReply){LoseSenderReply=false; recoverable=CourierGame.Step.Value;
                return Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate,"AwaitingVerification","Reply lost."));}
            return OK(true);
        }
        public Task<Result<bool>> ReceiveAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) {
            openedStep=CourierGame.Step.Value;CourierGame.TradeStarted.Value?.Invoke();
            Receives++;
            if(CheckpointDirectory!=null){
                var step=CourierGame.Step.Value;var item=m.Single();
                ReceiveStore.Save(new(){Step=step,Receiving=true,Arm=new(step,partner,new(who,4,[new(1,false,Units)])),
                    Items=[new(){ItemId=item.Item,Quantity=item.Units}],Original=[new(item.Item,false,item.Units)]});
            }
            if (PauseTrade) return Pause(); Quantities[who] = Units + m.Sum(x => x.Units) + ReceiveOffset; return UncertainTrade ? Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate, "LockUnknown", "Missing locked partner evidence")) : OK(true); }
        public Task<Result<bool>> CancelPreLockAsync(Deadline d) { cancelled.TrySetResult(new(CourierOutcome.Cancelled, "PreLock", "Trade cancelled")); return OK(true); }
        public Task<Result<int>> RetainerUnits(OwnerScope owner,Shipment s,bool deposit,Deadline d)=>OK(deposit?Deposited:100-Retrieved);
        public int Retrieved;
        public bool? FailPlacement;
        public bool RefusePartner;
        readonly Dictionary<string,int> movements=[];
        public int? JournalUnits(string id,OwnerScope owner,Shipment s)=>movements.GetValueOrDefault(id);
        public Task<Result<bool>> MoveAsync(StowagePlan plan,Shipment s,bool deposit,IDisposable lease,Func<bool> auth,Deadline d,Action<string> recordOperation)
        {
            Assert.True(auth()); Assert.Equal(plan.Owner, who); Assert.Equal(deposit ? 22ul : 11ul, deposit ? s.To : s.From);
            var id=Guid.NewGuid().ToString();recordOperation(id);
            bool fail=FailPlacement==deposit;int n=fail?1:s.Units;movements[id]=n;if(!deposit)Retrieved+=n;
            if(fail){FailPlacement=null;Quantities[who]=Units+(deposit?-n:n);if(deposit)Deposited+=n;
                return Task.FromResult(Result<bool>.Fail("PlacementStopped","fixture"));}
            Quantities[who] = Units + (deposit ? -s.Units : s.Units); if (deposit) Deposited += s.Units; return OK(true);
        }
        public readonly List<(OwnerScope Who, int DepositedBefore, ImmutableArray<CourierConsolidation> Lines)> Consolidations = [];
        public Result<bool>? ConsolidationResult;
        public bool SkipConsolidationRecord;
        public string? ConsolidationJournalStatus = OperationStatuses.Indeterminate;
        public string? OperationStatus(string id, OwnerScope owner) => ConsolidationJournalStatus;
        public Task<Result<bool>> ConsolidateAsync(StowagePlan plan, ImmutableArray<CourierConsolidation> lines, IDisposable lease, Func<bool> auth, Deadline d, Action<string> recordOperation)
        {
            Assert.True(auth()); Assert.Equal(plan.Owner, who);
            Assert.All(lines, l => Assert.True(l.Destination.Matches(who)));
            if (!SkipConsolidationRecord) recordOperation(Guid.NewGuid().ToString());
            Consolidations.Add((who, Deposited, lines));
            return Task.FromResult(ConsolidationResult ?? Result<bool>.Ok(true));
        }
        public Task<Result<InventoryView>> ObserveAsync(Deadline d) => OK(new InventoryView(who, 4, [new(1, false, Units)]));
        public Task<Result<string>> SwitchRefusalAsync(Deadline deadline) => OK("");
        public bool FailReturnSwitch;
        public Task<Result<OwnerScope>> SwitchAsync(OwnerScope target, Deadline d) =>
            FailReturnSwitch && Deposited==7
                ?Task.FromResult(new Result<OwnerScope>(CourierOutcome.Indeterminate,"CharacterSwitchDeadline","login timed out")):OK(who = target);
        public Task<Result<bool>> EnsureWorldAsync(uint world, Deadline d) => OK(true);
        public Task<Result<bool>> ReturnHomeAsync(Deadline d) => OK(true);
        public readonly List<MeetingBell> Approaches=[];
        public Task<Result<bool>> GoInRangeAsync(MeetingBell bell, Deadline d) { Approaches.Add(bell); return OK(true); }
        public Task<Result<CourierPosition>> PositionAsync(Deadline d) => OK(new CourierPosition(1, 1, 0, 0, 0));
        public Func<Result<bool>>? ReturnFailure;
        public Task<Result<bool>> ReturnToAsync(CourierPosition position, Deadline d) => ReturnFailure==null?OK(true):Task.FromResult(ReturnFailure());
        public Task<Result<bool>> ObserveTradeableAsync(OwnerScope character, Deadline d) => RefusePartner?Task.FromResult(Result<bool>.Fail("CarrierPartnerDeadline","fixture")):OK(true);
    }
}
