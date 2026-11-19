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
        cfg = new() { Carrier = carrier, PairedOwner = source, CarrierOptIn = true, Bells = ImmutableDictionary<ulong, MeetingBell>.Empty.Add(1, bell).Add(2, bell) };
        line = new()
        {
            StowagePlanId = plan.Id,
            ItemId = 1,
            ItemName = "Titanium Ore",
            TargetQuantity = 7,
            Quality = ItemQualityPolicy.NqOnly,
            Courier = new(destination, 22, 11)
        };
        mule.Quantities[carrier] = 10; // Private carrier stock must survive.
        follower = Follower(); wire.Handler = m => follower.HandleAsync(source, m, Deadline.After("Fixture", 30));
        engine = Conductor();
    }
    CourierFollower Follower(string? root = null) => new(root ?? dir, () => cfg, new(), new(new AR()), mule, mule, mule, mule, _ => { });
    CourierConductor Conductor(string? root = null) => new(root ?? dir, wire, owner, owner, owner, owner, owner, owner, new(), new(new AR()), _ => { });
    CourierPlan Plan() => CourierPlanner.Build(plan, [line],
        [new(source,null,new(source,5,[])),new(source,11,new(source,0,[new(1,false,7)])),
        new(destination,null,new(destination,5,[])),new(destination,22,new(destination,8,[]))],
        new(carrier, 4, [new(1, false, 10)]), cfg, id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 3), 1);

    [Fact]
    public async Task CarrierCapSplitsTripsAndDeliversExactUnits()
    {
        var p = Plan(); Assert.Equal(3, p.Trips.Length);
        var result = await engine.StartAsync(p, cfg);
        Assert.True(result.State == "Completed", result.Code + ":" + result.Detail);
        Assert.Equal(7, owner.Deposited); Assert.Equal(10, mule.Units);
        Assert.Empty(new AtomicDocumentStore<CarrierLedger>(Path.Combine(dir, "courier-custody.json")).Load().Units);
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
    public void Dispose() { follower.Dispose(); Directory.Delete(dir, true); }

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
    sealed class Client(OwnerScope initial) : ICourierPlacementPort, ICourierTradePort, ICourierSettlement, ICharacterSwitchPort, IInventoryObservationPort, IWorldVisitPort, IMeetingBellPort
    {
        OwnerScope who = initial;
        public Dictionary<OwnerScope, int> Quantities = new(); public int Units => Quantities.GetValueOrDefault(who);
        public int Deposited, Sends, ReceiveOffset;
        static Task<Result<T>> OK<T>(T value) => Task.FromResult(Result<T>.Ok(value));
        public bool PauseTrade, UncertainTrade;
        public bool LoseSenderReply;
        Guid? recoverable;
        public Task<Result<bool>> Settle(Guid step,Deadline d)=>recoverable==step ? OK(true) :
            Task.FromResult(Result<bool>.Fail("NoVerifiedCheckpoint","No matching locked-window proof."));
        public TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<Result<bool>> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<bool>> Pause() { Opened.TrySetResult(); return cancelled.Task; }
        public Task<Result<bool>> SendAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) {
            if (PauseTrade) return Pause(); Sends++; Quantities[who] = Units - m.Sum(x => x.Units);
            if(LoseSenderReply){LoseSenderReply=false; recoverable=CourierGame.Step.Value;
                return Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate,"AwaitingVerification","Reply lost."));}
            return OK(true);
        }
        public Task<Result<bool>> ReceiveAsync(OwnerScope partner, ImmutableArray<Shipment> m, Deadline d) { if (PauseTrade) return Pause(); Quantities[who] = Units + m.Sum(x => x.Units) + ReceiveOffset; return UncertainTrade ? Task.FromResult(new Result<bool>(CourierOutcome.Indeterminate, "LockUnknown", "Missing locked partner evidence")) : OK(true); }
        public Task<Result<bool>> CancelPreLockAsync(Deadline d) { cancelled.TrySetResult(new(CourierOutcome.Cancelled, "PreLock", "Trade cancelled")); return OK(true); }
        public Task<Result<bool>> MoveAsync(StowagePlan plan, Shipment s, bool deposit, IDisposable lease, Func<bool> auth, Deadline d)
        {
            Assert.True(auth()); Assert.Equal(plan.Owner, who); Assert.Equal(deposit ? 22ul : 11ul, deposit ? s.To : s.From);
            Quantities[who] = Units + (deposit ? -s.Units : s.Units); if (deposit) Deposited += s.Units; return OK(true);
        }
        public Task<Result<InventoryView>> ObserveAsync(Deadline d) => OK(new InventoryView(who, 4, [new(1, false, Units)]));
        public Task<Result<string>> SwitchRefusalAsync(Deadline deadline) => OK("");
        public Task<Result<OwnerScope>> SwitchAsync(OwnerScope target, Deadline d) => OK(who = target);
        public Task<Result<bool>> EnsureWorldAsync(uint world, Deadline d) => OK(true);
        public Task<Result<bool>> ReturnHomeAsync(Deadline d) => OK(true);
        public Task<Result<bool>> GoInRangeAsync(MeetingBell bell, Deadline d) => OK(true);
        public Task<Result<CourierPosition>> PositionAsync(Deadline d) => OK(new CourierPosition(1, 1, 0, 0, 0));
        public Task<Result<bool>> ReturnToAsync(CourierPosition position, Deadline d) => OK(true);
        public Task<Result<bool>> ObserveTradeableAsync(OwnerScope character, Deadline d) => OK(true);
    }
}
