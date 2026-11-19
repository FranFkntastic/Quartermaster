using System.Collections.Immutable;
using RQ.Automation;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Courier;

public enum CourierPhase
{
    EnsureSource, Retrieve, CarrierToSource, HandOut, SwitchToDestination,
    CarrierToDestination, HandIn, Deposit, ReturnOwner, ReturnCarrier, Finish
}
public sealed record CourierStep(CourierPhase Phase, int Trip, ImmutableArray<Shipment> Batch);
public sealed record RunIntent(Guid Id, CourierStep Action, InventoryView OwnerBefore, InventoryView CarrierBefore);
public sealed record HandOff(Guid Step, TradeEvidence? Owner, TradeEvidence? Carrier, bool BothTradesVerified);
public sealed class CourierJournal
{
    public Guid Run { get; set; }
    public string? RunKey {get;set;}
    public bool PeerClaimed { get; set; }
    public CourierSettings Settings { get; set; } = new();
    public CourierPlan Plan { get; set; } = new(Guid.Empty, 0, [], []);
    public OwnerScope Start { get; set; } = new();
    public int Cursor { get; set; }
    public string State { get; set; } = "Idle";
    public string Code { get; set; } = "";
    public string Detail { get; set; } = "";
    public RunIntent? Pending { get; set; }
    public List<HandOff> Evidence { get; set; } = [];
    public ImmutableArray<CourierStep> Schedule { get; set; } = [];
    public ImmutableArray<Shipment> Custody { get; set; } = [];
    public Dictionary<Guid, int> Delivered { get; set; } = [];
    public InventoryView? OwnerCheckpoint { get; set; }
    public InventoryView? CarrierCheckpoint { get; set; }
    public InventoryView? ResumeOwner { get; set; }
    public InventoryView? ResumeCarrier { get; set; }
    public string? ClearAcknowledgement { get; set; }
}
public sealed record LineProgress(Guid Line, string Name, int Moved, int Remaining);
public sealed record CourierSnapshot(Guid Run, string State, string OwnerPhase, string CarrierPhase, int Trip, int Trips,
    string Code, string Detail, ImmutableArray<LineProgress> Lines, ImmutableArray<Refusal> Refusals);

public sealed class CourierConductor
{
    private readonly CourierStore<CourierJournal> store;
    private readonly string directory;
    private readonly ICourierPeerPort peer;
    private readonly ICourierTradePort trade;
    private readonly IInventoryObservationPort inventory;
    private readonly IWorldVisitPort worlds;
    private readonly ICharacterSwitchPort login;
    private readonly IMeetingBellPort bells;
    private readonly ICourierPlacementPort placement;
    private readonly AutomationLease automation;
    private readonly AutoRetainerSuppression suppression;
    private readonly Action<string> warning;
    private readonly Action<string>? information;
    private readonly Func<bool> needsSuppression;
    private readonly CourierCalls calls;
    private readonly SemaphoreSlim gate = new(1);
    private volatile bool stop;
    private volatile bool unloaded;
    internal void Unload(){unloaded=stop=true;}
    private string peerPhase = "Idle";

    public CourierConductor(string directory, ICourierPeerPort peer, ICourierTradePort trade, IInventoryObservationPort inventory,
        IWorldVisitPort worlds, ICharacterSwitchPort login, IMeetingBellPort bells, ICourierPlacementPort placement,
        AutomationLease automation, AutoRetainerSuppression suppression, Action<string> warning, Action<string>? information = null,
        Func<bool>? needsSuppression=null)
    {
        this.peer = peer; this.trade = trade; this.inventory = inventory; this.worlds = worlds; this.login = login; this.bells = bells;
        this.placement = placement; this.automation = automation; this.suppression = suppression; this.warning = warning; calls = new(warning);
        store = new(Path.Combine(directory, "courier-run.json"));
        this.information = information;
        this.needsSuppression=needsSuppression??(()=>true);
        this.directory = directory;
        var r = store.Load();
        if (r.State == "Running") { r.State = "Interrupted"; store.Save(r); }
    }
    private static IEnumerable<CourierStep> Steps(CourierJournal r)
    {
        for (int i = 0; i < r.Plan.Trips.Length; i++)
        {
            var trip = r.Plan.Trips[i];
            yield return new(CourierPhase.EnsureSource, i, []);
            foreach (var s in trip.Manifest) yield return new(CourierPhase.Retrieve, i, [s]);
            yield return new(CourierPhase.CarrierToSource, i, []);
            foreach (var b in trip.Manifest.Chunk(5)) yield return new(CourierPhase.HandOut, i, [.. b]);
            yield return new(CourierPhase.SwitchToDestination, i, []);
            yield return new(CourierPhase.CarrierToDestination, i, []);
            foreach (var b in trip.Manifest.Chunk(5))
            {
                yield return new(CourierPhase.HandIn, i, [.. b]);
                foreach (var s in b) yield return new(CourierPhase.Deposit, i, [s]);
            }
        }
        if (r.Settings.ReturnOwner) yield return new(CourierPhase.ReturnOwner, 0, []);
        if (r.Settings.ReturnCarrier) yield return new(CourierPhase.ReturnCarrier, 0, []);
        yield return new(CourierPhase.Finish, 0, []);
    }
    public CourierSnapshot Snapshot()
    {
        var r = store.Load(); var step = r.Schedule.Skip(r.Cursor).FirstOrDefault();
        return new(r.Run, r.State, step?.Phase.ToString() ?? r.State, peerPhase, r.Plan.Trips.IsEmpty ? 0 :
            step is null || step.Phase >= CourierPhase.ReturnOwner ? r.Plan.Trips.Length : step.Trip + 1, r.Plan.Trips.Length, r.Code, r.Detail,
            [..r.Plan.Trips.SelectMany(t=>t.Manifest).GroupBy(s=>s.Line).Select(g=>new LineProgress(g.Key,g.First().Name,
                r.Delivered.GetValueOrDefault(g.Key),g.Sum(s=>s.Units)-r.Delivered.GetValueOrDefault(g.Key)))], r.Plan.Refusals);
    }
    public async Task<CourierSnapshot> ClearAsync(Guid expectedRun,string? acknowledgement)
    {
        if(!await gate.WaitAsync(0)) throw new InvalidOperationException("CourierAlreadyRunning");
        try
        {
            var r=store.Load();
            if(r.Run!=expectedRun || r.State is not ("Completed" or "Failed" or "Interrupted" or "Indeterminate" or "Refused"))
                throw new InvalidOperationException("RunNotClearable");
            if(r.Pending?.Action.Phase is CourierPhase.HandOut or CourierPhase.HandIn)
                throw new InvalidOperationException("Settle the pending trade before clearing custody.");
            // Completed includes a durable empty-ledger Finish acknowledgement.
            var receipt=r.State is "Completed" or "Refused" || !r.Start.HasStableIdentity ? null :
                await Send(r,CourierCommand.Status,r.Start,[]);
            if(receipt?.Unconfirmed!=null) throw new InvalidOperationException("Carrier trade is unsettled.");
            var held=receipt?.Custody ?? [];
            if(!held.IsEmpty || !r.Custody.IsEmpty)
            {
                static string Describe(IEnumerable<Shipment> units)=>string.Join(", ", units.OrderBy(s=>s.Line)
                    .Select(s=>$"{s.Name}{(s.Hq?" HQ":"")} × {s.Units}"));
                var required=$"Owner ledger [{Describe(r.Custody)}]; carrier ledger [{Describe(held)}]";
                if(acknowledgement!=required)throw new CustodyAcknowledgementRequired(required);
                r.ClearAcknowledgement=required;store.Save(r);
            }
            if(receipt?.ActiveRun==r.Run)await Send(r,CourierCommand.Clear,r.Start,held);
            new CourierStore<CourierJournal>(Path.Combine(directory,$"courier-closed-{r.Run:N}.json")).Save(r);
            store.Save(new()); return Snapshot();
        }
        finally{gate.Release();}
    }
    public sealed class CustodyAcknowledgementRequired(string held) : Exception(held);
    public async Task StopAsync()
    {
        stop = true;
        await calls.Call("OwnerCancelPreLock", 10, trade.CancelPreLockAsync);
        var r = store.Load();
        if (r.State == "Running") await Send(r, CourierCommand.Stop, r.Start, []);
    }
    private Task<InventoryView> Observe() => calls.Call("OwnerBags", 15, inventory.ObserveAsync);
    private async Task<CourierReceipt> Send(CourierJournal r, CourierCommand type, OwnerScope partner, ImmutableArray<Shipment> manifest,
        MeetingBell? bell = null, Guid? id = null, Guid? confirm = null, TradeEvidence? evidence = null, Action<CourierReceipt>? cancelled = null)
    {
        var step = id ?? Guid.NewGuid();
        var message = new CourierMessage(1, r.Run, step, type, r.Start, partner, r.Settings.Carrier!, manifest, bell, confirm, evidence,r.RunKey);
        var reply = await calls.Call("Peer" + type, 360, async d =>
        {
            var result = await peer.SendAsync(message, d);
            if (result is { Outcome: not CourierOutcome.Succeeded, Value: { } receipt } && receipt.Run == r.Run && receipt.Step == step)
                cancelled?.Invoke(receipt);
            return result;
        });
        if (reply.Run != r.Run || reply.Step != step) throw Fault("ReceiptMismatch", "Wrong run or step.");
        return reply;
    }
    private static CourierFault Fault(string code, string detail) => new(CourierOutcome.Indeterminate, code, detail);
    private async Task Switch(OwnerScope target)
    {
        if (CourierPlanner.Exact((await Observe()).Character, target)) return;
        var deadline = Deadline.After("MaySwitchNow", 60);
        while (await calls.Call("SwitchEligibilityObservation",5,login.SwitchRefusalAsync) is {Length:>0} reason)
        {
            if (stop || DateTimeOffset.UtcNow >= deadline.Expires) throw new CourierFault(CourierOutcome.Failed, "MaySwitchNowDeadline", $"MaySwitchNow 60s: {reason}");
            await Task.Delay(150);
        }
        if (!CourierPlanner.Exact(await calls.Call("CharacterSwitch", 180, d => login.SwitchAsync(target, d)), target))
            throw Fault("SwitchIdentityMismatch", target.CharacterName);
    }
    public async Task<CourierSnapshot> StartAsync(CourierPlan plan, CourierSettings settings)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("CourierAlreadyRunning");
        CourierJournal r;
        try { r = store.Load(); } catch { gate.Release(); throw; }
        async Task CancelLocal()
        {
            try { await calls.Call("OwnerCancelPreLock", 10, trade.CancelPreLockAsync); }
            catch (Exception e) { warning($"Cancel pre-lock: {e.Message}"); }
        }
        void Lost() { stop = true; warning($"CourierLinkLost: {r.Run}"); _ = CancelLocal(); }
        void RemoteStop(Guid run) { if (run == r.Run) { stop = true; _ = CancelLocal(); } }
        void Progress(CourierProgress p) { if (p.Run == r.Run) peerPhase = p.Phase; }
        bool beginAttempted = false;
        IDisposable? lease = null, tradeHold = null; AutoRetainerSuppression.Scope? suppress = null;
        try
        {
            stop = false;
            if(unloaded)throw new OperationCanceledException("Courier unloaded.");
            var resume = r.State is not ("Idle" or "Completed" or "Refused");
            if (!resume)
            {
                r = new() { Run = Guid.NewGuid(), Plan = plan, Settings = settings,
                    RunKey=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
                if (!plan.Refusals.IsEmpty || plan.Trips.IsEmpty) { r.State = "Refused"; r.Code = "PlanRefused"; store.Save(r); return Snapshot(); }
            }
            if (!automation.TryAcquire("courier owner", out lease) || lease == null) throw new CourierFault(CourierOutcome.Failed, "AutomationBusy", automation.AcquisitionBlockReason ?? "Busy");
            if(needsSuppression())suppress = suppression.Acquire();
            if (suppression.IsBusy) throw new CourierFault(CourierOutcome.Failed, "AutoRetainerBusy", "AutoRetainer still active");
            peer.LinkLost += Lost; peer.Progress += Progress; peer.StopRequested += RemoteStop;
            if (trade is ICourierHoldPort holds)
                tradeHold = await calls.Call("OwnerTradeHold",10,d=>holds.BeginHold(r.Run,d));
            var owner = await Observe();
            if (!resume || !r.Start.HasStableIdentity && !r.PeerClaimed && r.Cursor == 0 && r.Pending == null) r.Start = owner.Character;
            var status = await Send(r, CourierCommand.Status, r.Start, []);
            var carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Fresh observation required.");
            if (!CourierPlanner.Exact(carrier.Character, r.Settings.Carrier)) throw Fault("CarrierIdentityMismatch", "Unexpected carrier.");
            if (resume)
            {
                r.ResumeOwner = owner; r.ResumeCarrier = carrier; store.Save(r);
                if (r.Pending is {Action.Phase: CourierPhase.HandOut or CourierPhase.HandIn} uncertain && trade is ICourierSettlement recovery &&
                    (status.Unconfirmed==uncertain.Id || status.LastConfirmed==uncertain.Id))
                {
                    int sign=uncertain.Action.Phase==CourierPhase.HandOut ? -1 : 1;
                    var local=new TradeEvidence(uncertain.OwnerBefore,owner);
                    var remote=new TradeEvidence(uncertain.CarrierBefore,carrier);
                    if(Custody.TrySlice(local,uncertain.OwnerBefore.Character,remote,r.Settings.Carrier!,uncertain.Action.Batch,sign,out var moved,out var rest))
                    {
                        var previous=r.Evidence.LastOrDefault(e=>e.Step==uncertain.Id);
                        bool verified = previous?.BothTradesVerified==true &&
                            Custody.Matches(previous.Owner,uncertain.OwnerBefore.Character,uncertain.Action.Batch,sign) &&
                            Custody.Matches(previous.Carrier,r.Settings.Carrier!,uncertain.Action.Batch,-sign);
                        if(!verified){
                            // Settle the sender first; a bag observation alone is not a settled confirmation.
                            if(sign==1 && status.Unconfirmed==uncertain.Id)
                                await Send(r,CourierCommand.VerifySettlement,owner.Character,[],confirm:uncertain.Id,evidence:local);
                            await calls.Call("OwnerSettlement",15,d=>recovery.Settle(uncertain.Id,r.Settings.Carrier!,remote,d));
                        } else if(!rest.IsEmpty) throw Fault("SettlementDeltaMismatch","A verified full receipt cannot become a partial transfer.");
                        if(status.Unconfirmed==uncertain.Id)
                            await Send(r,CourierCommand.Settle,owner.Character,[],confirm:uncertain.Id,evidence:local);
                        status=await Send(r,CourierCommand.Status,r.Start,[]);
                        var expected=Custody.Apply(r.Custody,moved,-sign);
                        if(status.LastConfirmed!=uncertain.Id || !Custody.Covers(status.Custody,expected) || !Custody.Covers(expected,status.Custody))
                            throw Fault("SettlementLedgerMismatch","The carrier has not acknowledged the same custody delta.");
                        r.Evidence.Add(new(uncertain.Id,local,remote,true));
                        r.Custody=expected; r.OwnerCheckpoint=owner; r.CarrierCheckpoint=carrier;
                        if(rest.IsEmpty)r.Cursor++;
                        else
                        {
                            var action=uncertain.Action;
                            var after=r.Cursor+1+(sign==1 ? action.Batch.Length : 0);
                            var next=new List<CourierStep>();
                            if(sign==1)foreach(var unit in moved)next.Add(new(CourierPhase.Deposit,action.Trip,[unit]));
                            next.Add(action with {Batch=rest});
                            if(sign==1)foreach(var unit in rest)next.Add(new(CourierPhase.Deposit,action.Trip,[unit]));
                            r.Schedule=[..r.Schedule.Take(r.Cursor),..next,..r.Schedule.Skip(after)];
                        }
                        r.Pending=null;store.Save(r);
                    }
                    else throw Fault("ResumeUnsettled","Fresh owner and carrier deltas do not agree with the pending manifest.");
                }
                if (r.Pending is { Action.Phase: CourierPhase.HandOut or CourierPhase.HandIn } pending &&
                    r.Evidence.LastOrDefault(e => e.Step == pending.Id) is { } proof)
                {
                    var outbound = pending.Action.Phase == CourierPhase.HandOut;
                    var batch = pending.Action.Batch;
                    if (proof.BothTradesVerified && Custody.Matches(proof.Owner, pending.OwnerBefore.Character, batch, outbound ? -1 : 1)
                        && Custody.Matches(proof.Carrier, r.Settings.Carrier!, batch, outbound ? 1 : -1)
                        && Agrees(proof.Owner!.After, owner, r) && Agrees(proof.Carrier!.After, carrier, r))
                    {
                        // Only settle bookkeeping. Never ask either trade port to dispatch a second time.
                        if (status.Unconfirmed == pending.Id)
                        {
                            await Send(r, CourierCommand.Confirm, owner.Character, [], confirm: pending.Id, evidence: proof.Owner);
                            status = await Send(r, CourierCommand.Status, r.Start, []);
                        }
                        var expected = Custody.Apply(r.Custody, batch, outbound ? 1 : -1);
                        if (status.LastConfirmed == pending.Id && Custody.Covers(status.Custody, expected) && Custody.Covers(expected, status.Custody))
                        {
                            r.Custody = expected; r.OwnerCheckpoint = owner; r.CarrierCheckpoint = carrier; r.Pending = null; r.Cursor++; store.Save(r);
                        }
                    }
                }
                var travel=r.Pending?.Action.Phase is CourierPhase.EnsureSource or CourierPhase.SwitchToDestination or CourierPhase.ReturnOwner
                    or CourierPhase.ReturnCarrier or CourierPhase.CarrierToSource or CourierPhase.CarrierToDestination;
                if (r.Pending != null && !travel || status.Unconfirmed != null || !Custody.Covers(status.Custody, r.Custody) || !Custody.Covers(r.Custody, status.Custody)
                    || (!travel && r.Cursor != 0 && (!Agrees(r.OwnerCheckpoint, owner, r) || !Agrees(r.CarrierCheckpoint, carrier, r))))
                    throw Fault("ResumeUnsettled", "Journal intent, ledger or bags cannot prove a forward checkpoint.");
            }
            if (r.Schedule.IsEmpty) r.Schedule = [.. Steps(r)];
            // Begin may succeed on the peer even if its acknowledgement is lost.
            r.State = "Running"; store.Save(r); beginAttempted = true;
            await Send(r, CourierCommand.Begin, r.Start, []);
            r.PeerClaimed = true; store.Save(r);
            var steps = r.Schedule;
            for (; r.Cursor < steps.Length;)
            {
                (trade as ICourierHoldPort)?.CheckHold();
                if (stop) throw new CourierFault(CourierOutcome.Cancelled, "StopRequested", "Stopped at verified checkpoint.");
                var s = steps[r.Cursor]; var t = r.Plan.Trips[s.Trip];
                owner = await Observe(); status = await Send(r, CourierCommand.Status, r.Start, []);
                carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Missing before snapshot");
                if (s.Phase == CourierPhase.Retrieve && steps[r.Cursor - 1].Phase == CourierPhase.EnsureSource)
                {
                    var cap = Math.Min(carrier.FreeSlots, owner.FreeSlots);
                    if (cap <= 0) throw new CourierFault(CourierOutcome.Failed, "BagSlotLimit", "No free slot for the next trip.");
                    if (cap < t.Manifest.Length)
                    {
                        var split = t.Manifest.Chunk(cap).Select(b => t with { Manifest = [.. b] }).ToArray();
                        r.Plan = r.Plan with { Trips = r.Plan.Trips.RemoveAt(s.Trip).InsertRange(s.Trip, split) };
                        // Previous trips may have dynamic hand-in batches; only replace the unstarted suffix.
                        var remaining = Steps(r).SkipWhile(x => x.Trip != s.Trip).Skip(1);
                        steps = r.Schedule = [.. steps.Take(r.Cursor), .. remaining];
                        s = steps[r.Cursor]; t = r.Plan.Trips[s.Trip]; store.Save(r);
                    }
                }
                if (s.Phase == CourierPhase.HandIn && owner.FreeSlots < s.Batch.Length)
                {
                    int count = 0, slots = owner.FreeSlots;
                    foreach (var unit in s.Batch)
                    {
                        // A partial stack can take a whole manifest slot without consuming a free bag slot.
                        var held = owner.Count(unit.Item, unit.Hq) + s.Batch.Take(count).Where(x => x.Item == unit.Item && x.Hq == unit.Hq).Sum(x => x.Units);
                        if ((unit.StackSize - held % unit.StackSize) % unit.StackSize < unit.Units && --slots < 0) break;
                        count++;
                    }
                    if (count == 0) throw new CourierFault(CourierOutcome.Failed, "DestinationBagsFull", "Cannot accept the next custody stack.");
                    if (count < s.Batch.Length)
                    {
                        var deposits = steps.Skip(r.Cursor + 1).Take(s.Batch.Length).ToArray();
                        steps = r.Schedule = [..steps.Take(r.Cursor),s with {Batch=s.Batch[..count]},..deposits.Take(count),
                            s with {Batch=s.Batch[count..]},..deposits.Skip(count),..steps.Skip(r.Cursor+1+s.Batch.Length)];
                        s = steps[r.Cursor]; store.Save(r);
                    }
                }
                if (s.Phase is CourierPhase.HandOut or CourierPhase.HandIn)
                {
                    // Five manifest rows need not fit one window: one row can draw from many
                    // fragmented bag slots. Credit each game window before allowing another.
                    var stock=(s.Phase==CourierPhase.HandOut?owner:carrier).Stock;
                    var batch=s.Batch;
                    int Slots(IEnumerable<Shipment> lines)=>lines.GroupBy(x=>(x.Item,x.Hq)).Sum(g=>{
                        var n=g.Sum(x=>x.Units);var count=0;
                        foreach(var slot in stock.Where(x=>x.ItemId==g.Key.Item && x.Hq==g.Key.Hq && x.Units>0))
                        {count++;n-=slot.Units;if(n<=0)break;}
                        return count;
                    });
                    if(Slots(batch)>5)
                    {
                        var first=batch[0];
                        var qty=Math.Min(first.Units,stock.Where(x=>x.ItemId==first.Item && x.Hq==first.Hq && x.Units>0)
                            .Take(5).Sum(x=>x.Units));
                        if(qty<=0)throw Fault("SenderStockShort",first.Name);
                        ImmutableArray<Shipment> head=[first with {Units=qty}];
                        var tail=batch.RemoveAt(0);
                        if(qty<first.Units)tail=tail.Insert(0,first with {Units=first.Units-qty});
                        IEnumerable<CourierStep> Window(ImmutableArray<Shipment> part)
                        {
                            yield return s with {Batch=part};
                            if(s.Phase==CourierPhase.HandIn)foreach(var unit in part)yield return new(CourierPhase.Deposit,s.Trip,[unit]);
                        }
                        var suffix=r.Cursor+1+(s.Phase==CourierPhase.HandIn?batch.Length:0);
                        steps=r.Schedule=[..steps.Take(r.Cursor),..Window(head),..Window(tail),..steps.Skip(suffix)];
                        s=steps[r.Cursor];store.Save(r);
                    }
                }
                if (!CourierPlanner.Exact(carrier.Character, r.Settings.Carrier) ||
                    !Custody.Covers(status.Custody, r.Custody) || !Custody.Covers(r.Custody, status.Custody))
                    throw Fault("CustodyMismatch", "Carrier identity or custody changed.");
                if (s.Phase is CourierPhase.Retrieve or CourierPhase.CarrierToSource or CourierPhase.HandOut &&
                    !CourierPlanner.Exact(owner.Character, t.Source) ||
                    s.Phase is CourierPhase.Deposit or CourierPhase.CarrierToDestination or CourierPhase.HandIn &&
                    !CourierPlanner.Exact(owner.Character, t.Destination)) throw Fault("OwnerIdentityMismatch", owner.Character.CharacterName);
                r.Pending ??= new(Guid.NewGuid(), s, owner, carrier); store.Save(r);
                CourierGame.Step.Value = r.Pending.Id;
                CourierGame.CanContinue.Value=()=>!stop && !unloaded;
                information?.Invoke($"{r.Run}/{r.Pending.Id} owner {s.Phase} trip {s.Trip + 1}");
                if (stop) { r.Pending = null; throw new CourierFault(CourierOutcome.Cancelled, "StopRequested", "Stopped before dispatch."); }
                switch (s.Phase)
                {
                    case CourierPhase.EnsureSource:
                        await Switch(t.Source); await Meet(t.Pickup); break;
                    case CourierPhase.SwitchToDestination:
                        // Both legs are authorized by this single persisted rendezvous intent.
                        async Task OwnerToDestination() { await Switch(t.Destination); await Meet(t.Dropoff); }
                        await Task.WhenAll(OwnerToDestination(), t.Pickup.SamePlace(t.Dropoff) ? Task.CompletedTask :
                            Send(r, CourierCommand.Travel, t.Destination, [], t.Dropoff,id:r.Pending.Id)); break;
                    case CourierPhase.CarrierToSource:
                    case CourierPhase.CarrierToDestination:
                        if (s.Phase == CourierPhase.CarrierToSource) await Send(r, CourierCommand.Travel, owner.Character, [], t.Pickup,id:r.Pending.Id);
                        await calls.Call("Rendezvous", 30, d => bells.ObserveTradeableAsync(r.Settings.Carrier!, d));
                        break;
                    case CourierPhase.HandOut:
                    case CourierPhase.HandIn:
                        await HandOffAsync(r, s, owner); break;
                    case CourierPhase.Retrieve:
                    case CourierPhase.Deposit:
                        var unit = s.Batch.Single(); bool deposit = s.Phase == CourierPhase.Deposit;
                        await calls.Call("CourierPlacement", 180, d => placement.MoveAsync(new() { Id = r.Plan.PlanId, Revision = r.Plan.Revision, Owner = owner.Character },
                            unit, deposit, lease, () => !stop && automation.Owns(lease), d));
                        if (deposit) r.Delivered[unit.Line] = checked(r.Delivered.GetValueOrDefault(unit.Line) + unit.Units);
                        break;
                    case CourierPhase.ReturnOwner: await Switch(r.Start); break;
                    case CourierPhase.ReturnCarrier: await Send(r, CourierCommand.ReturnHome, r.Start, [],id:r.Pending.Id); break;
                    case CourierPhase.Finish:
                        if (!r.Custody.IsEmpty) throw Fault("CustodyRemaining", "Carrier still holds stock");
                        await Send(r, CourierCommand.Finish, r.Start, []); r.PeerClaimed = false; break;
                }
                if (s.Phase is CourierPhase.HandOut or CourierPhase.HandIn)
                {
                    r.OwnerCheckpoint = r.Evidence[^1].Owner!.After;
                    r.CarrierCheckpoint = r.Evidence[^1].Carrier!.After;
                }
                else
                {
                    r.OwnerCheckpoint = await Observe();
                    r.CarrierCheckpoint = (await Send(r, CourierCommand.Status, r.Start, [])).Bags;
                }
                r.Pending = null; r.Cursor++; store.Save(r);
            }
            r.State = "Completed"; r.Code = "Succeeded"; r.Detail = ""; store.Save(r);
        }
        catch (Exception e)
        {
            r.Code = (e as CourierFault)?.Code ?? "UnexpectedFailure"; r.Detail = e.Message;
            r.State = r.Pending != null || e is CourierFault { Outcome: CourierOutcome.Indeterminate } ? "Indeterminate" :
                e is CourierFault { Outcome: CourierOutcome.Cancelled } ? "Interrupted" : "Failed";
            warning($"{r.Run}/{r.Pending?.Id} {r.State} {r.Code}: {e.Message}"); store.Save(r);
            if (r.PeerClaimed || beginAttempted) try { await Send(r, CourierCommand.Stop, r.Start, []); } catch (Exception cleanup) { warning(cleanup.Message); }
        }
        finally { peer.LinkLost -= Lost; peer.Progress -= Progress; peer.StopRequested -= RemoteStop; calls.WhenSettled(() => { try { tradeHold?.Dispose(); suppress?.Dispose(); } finally { lease?.Dispose(); gate.Release(); } }); }
        return Snapshot();
    }
    private static bool Agrees(InventoryView? checkpoint, InventoryView fresh, CourierJournal r) =>
        checkpoint != null && CourierPlanner.Exact(checkpoint.Character, fresh.Character) &&
        r.Plan.Trips.SelectMany(t => t.Manifest).All(s => checkpoint.Count(s.Item, s.Hq) == fresh.Count(s.Item, s.Hq));
    private async Task Meet(MeetingBell bell)
    {
        if ((await calls.Call("OwnerPosition", 15, bells.PositionAsync)).World != bell.World)
            await calls.Call("OwnerWorld", 180, d => worlds.EnsureWorldAsync(bell.World, d));
        await calls.Call("OwnerBell", 150, d => bells.GoInRangeAsync(bell, d));
    }
    private async Task HandOffAsync(CourierJournal r, CourierStep step, InventoryView before)
    {
        var send = step.Phase == CourierPhase.HandOut;
        var batch = step.Batch;
        if (!send && !Custody.Covers(r.Custody, batch)) throw Fault("CustodyInsufficient", "HandIn exceeds custody");
        await calls.Call("OwnerPartner", 30, d => bells.ObserveTradeableAsync(r.Settings.Carrier!, d));
        if (stop) throw new CourierFault(CourierOutcome.Cancelled, "StopBeforeTrade", "No trade dispatched.");
        var sender = send ? before : r.Pending!.CarrierBefore;
        if (batch.GroupBy(s => (s.Item, s.Hq)).Any(g => sender.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => s.Units)))
            throw Fault("SenderStockShort", "Fresh sender inventory does not cover the manifest.");
        var id = r.Pending!.Id;
        CourierReceipt? cancellationReceipt = null;
        try { (trade as ICourierHoldPort)?.CheckHold(); }
        catch (CourierFault) { r.Pending=null; store.Save(r); throw; } // Neither trade has been dispatched.
        var remote = Send(r, send ? CourierCommand.Receive : CourierCommand.Send, before.Character, batch, id: id, cancelled: receipt => cancellationReceipt = receipt);
        var local = calls.Call("OwnerTrade", 120, d => send ? trade.SendAsync(r.Settings.Carrier!, batch, d) : trade.ReceiveAsync(r.Settings.Carrier!, batch, d));
        try { await Task.WhenAll(local, remote); }
        catch (CourierFault) when (remote.Exception?.GetBaseException() is CourierFault { Outcome: CourierOutcome.Cancelled } && cancellationReceipt != null && local.Exception?.GetBaseException() is CourierFault { Outcome: CourierOutcome.Cancelled })
        {
            var ownerEvidence = new TradeEvidence(before, await Observe());
            if (Custody.Matches(ownerEvidence, before.Character, batch, 0) && Custody.Matches(cancellationReceipt.Evidence, r.Settings.Carrier!, batch, 0))
            {
                r.OwnerCheckpoint = ownerEvidence.After; r.CarrierCheckpoint = cancellationReceipt.Evidence!.After;
                r.Pending = null;
                throw new CourierFault(CourierOutcome.Cancelled, "CancelledPreLock", "Both clients cancelled before lock with unchanged bags.");
            }
            throw;
        }
        finally
        {
            var e = new HandOff(id, new(before, await Observe()), remote.IsCompletedSuccessfully ? remote.Result.Evidence : cancellationReceipt?.Evidence,
                local.IsCompletedSuccessfully && remote.IsCompletedSuccessfully);
            r.Evidence.Add(e); store.Save(r);
        }
        var proof = r.Evidence[^1];
        if (!Custody.Matches(proof.Owner, before.Character, batch, send ? -1 : 1) ||
            !Custody.Matches(proof.Carrier, r.Settings.Carrier!, batch, send ? 1 : -1)) throw Fault("HandOffDeltaMismatch", "Both inventory deltas must equal the manifest.");
        var updated = Custody.Apply(r.Custody, batch, send ? 1 : -1);
        var receipt = await Send(r, CourierCommand.Confirm, before.Character, [], confirm: id, evidence: proof.Owner);
        if (!Custody.Covers(updated, receipt.Custody) || !Custody.Covers(receipt.Custody, updated)) throw Fault("LedgerMismatch", "Carrier confirmation differs.");
        r.Custody = updated;
    }
}
