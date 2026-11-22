using System.Collections.Immutable;
using RQ.Automation;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Courier;

public enum CourierPhase
{
    EnsureSource, Retrieve, CarrierToSource, HandOut, SwitchToDestination,
    CarrierToDestination, HandIn, Deposit, ReturnOwner, ReturnCarrier, Finish,
    // Journals store phases by number: new phases go last. Consolidate runs inside the trips,
    // after the last trip's deposits into a destination, while that destination is at its bell.
    Consolidate,
    // After the last deposit, a destination met off its home world (at the cross-region bridge) returns home.
    ReturnDestinationHome
}
public sealed record CourierStep(CourierPhase Phase, int Trip, ImmutableArray<Shipment> Batch);
/// <param name="RetainerBefore">Legacy single-stack placement baseline; batched placement settles from the journal and bags.</param>
/// <param name="PlacementOperation">Consolidation's operation id (and a legacy single-stack placement's).</param>
/// <param name="PlacementOperations">A batched Retrieve/Deposit's journaled operations, in dispatch order
/// (retrieval: one per <see cref="CourierPlacementBatch.Retrievals"/> source, deposit: one).</param>
public sealed record RunIntent(Guid Id, CourierStep Action, InventoryView OwnerBefore, InventoryView CarrierBefore, bool LocalMayHaveStarted=true,int? RetainerBefore=null,string? PlacementOperation=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> PlacementOperations = default)
{
    internal ImmutableArray<string> Operations
    {
        get
        {
            var batched = PlacementOperations.IsDefault ? ImmutableArray<string>.Empty : PlacementOperations;
            return PlacementOperation is { } legacy ? batched.Insert(0, legacy) : batched;
        }
    }
}
public sealed record HandOff(Guid Step, TradeEvidence? Owner, TradeEvidence? Carrier, bool BothTradesVerified,string? Disposition=null);
public sealed class CourierJournal
{
    public Guid Run { get; set; }
    public string? RunKey {get;set;}
    /// <summary>The IPC request that started this run (its idempotency key); null for runs started in the UI.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }
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
    /// <summary>
    /// Destination consolidations that stopped short ("Name: code: message"), one per destination.
    /// Delivery still completes; the run then ends as ConsolidationLimited with these in its detail.
    /// </summary>
    public List<string> ConsolidationLimits { get; set; } = [];
    /// <summary>
    /// 0: a schedule written before batching (per-stack placement, carrier travels in CarrierToSource).
    /// 1: batched placement and trades, carrier travels to the pickup during EnsureSource.
    /// </summary>
    public int ScheduleVersion { get; set; }
    /// <summary>
    /// A carrier travel or return dispatched in the background of owner-only steps, journaled before it is
    /// sent. Until the step that needs the carrier settles it, it is re-sent with the same id (the carrier
    /// resumes or replays its receipt), so an interrupted one never leaves the carrier an unsettled intent.
    /// </summary>
    public CourierCarrierMove? CarrierMove { get; set; }
}
public sealed record CourierCarrierMove(Guid Id, CourierCommand Command, OwnerScope Partner, MeetingBell? Bell);
public sealed record LineProgress(Guid Line, string Name, int Moved, int Remaining, Shipment? Route = null, bool Nq = false, bool Hq = false);
public sealed record CourierSnapshot(Guid Run, string State, string OwnerPhase, string CarrierPhase, int Trip, int Trips,
    string Code, string Detail, ImmutableArray<LineProgress> Lines, ImmutableArray<Refusal> Refusals,bool CanResume,bool CanClear,ImmutableArray<CustodyRow> Custody,
    CourierUnverified? Unverified=null, Guid PlanId=default, string? RequestId=null);
public sealed record CustodyRow(string Item,string Quality,int Quantity,string Holder);
/// <summary>The journaled step whose outcome is not yet proven; Rows hold its batch at the last verified holder (From).</summary>
public sealed record CourierUnverified(string Step,string From,string To,ImmutableArray<CustodyRow> Rows);

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
    private volatile bool launchPrepared;
    private volatile bool unloaded;
    /// <summary>True while a run (or its late calls after a fault or stop) still holds the gate.</summary>
    internal bool IsBusy => gate.CurrentCount == 0;
    /// <summary>
    /// Clears a previous stop before a launch is scheduled, so a Stop arriving while the launch is still
    /// archiving or starting is honored instead of reset by <see cref="StartAsync"/>.
    /// </summary>
    internal void PrepareLaunch() { if (IsBusy) return; stop = false; launchPrepared = true; }
    private Action? releaseOwner;
    internal void Unload(){unloaded=stop=true;}
    internal void DrainUnload()
    {
        bool drained=gate.Wait(TimeSpan.FromSeconds(2));
        if(!drained)warning("OwnerUnloadDrainDeadline: 2s elapsed.");
        try { Volatile.Read(ref releaseOwner)?.Invoke(); } finally { if(drained)gate.Release(); }
    }
    private volatile string peerPhase = "Idle";

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
            // One bell session per side and one trade per hand-off: the placement port and the trade
            // runner both work through the whole trip manifest without leaving the bell or the trade.
            // Retrieve and Deposit stay even for bag-only manifests: they verify bag stock, Retrieve
            // anchors the bag-slot split and Deposit credits delivery.
            // A local trip is the owner's own withdrawal and deposit at its bell: no carrier, no trade.
            yield return new(CourierPhase.EnsureSource, i, []);
            yield return new(CourierPhase.Retrieve, i, trip.Manifest);
            if (!trip.Local)
            {
                yield return new(CourierPhase.CarrierToSource, i, []);
                yield return new(CourierPhase.HandOut, i, trip.Manifest);
                yield return new(CourierPhase.SwitchToDestination, i, []);
                yield return new(CourierPhase.CarrierToDestination, i, []);
                yield return new(CourierPhase.HandIn, i, trip.Manifest);
            }
            yield return new(CourierPhase.Deposit, i, trip.Manifest);
            if (r.Plan.Consolidations.Any(c => CourierPlanner.Exact(c.Destination, trip.Destination)) &&
                !r.Plan.Trips.Skip(i + 1).Any(later => CourierPlanner.Exact(later.Destination, trip.Destination)))
                yield return new(CourierPhase.Consolidate, i, []);
        }
        // A destination left at a bridge off its home world goes home before the closing returns.
        if (r.Plan.ReturnDestinationHome && r.Plan.Trips.Length > 0 && r.Plan.Trips[^1] is { } last &&
            last.Dropoff.World != last.Destination.HomeWorldId)
            yield return new(CourierPhase.ReturnDestinationHome, r.Plan.Trips.Length - 1, []);
        if (r.Settings.ReturnOwner) yield return new(CourierPhase.ReturnOwner, 0, []);
        if (r.Settings.ReturnCarrier && !Carrierless(r)) yield return new(CourierPhase.ReturnCarrier, 0, []);
        yield return new(CourierPhase.Finish, 0, []);
    }
    /// <summary>
    /// Whether every trip is the owner's own: the run then never needs, contacts or waits on a carrier. A run
    /// journaled before that, which already claimed the carrier, keeps it until it finishes.
    /// </summary>
    private static bool Carrierless(CourierJournal r) => !r.PeerClaimed && r.Plan.Trips.All(t => t.Local);
    /// <summary>The carrier trip nearest before or after trip <paramref name="i"/>, skipping local trips; null when none.</summary>
    private static CourierTrip? Carried(CourierJournal r, int i, int step)
    {
        for (i += step; i >= 0 && i < r.Plan.Trips.Length; i += step)
            if (!r.Plan.Trips[i].Local)
                return r.Plan.Trips[i];
        return null;
    }
    /// <summary>Whether a phase runs inside a trip (before the closing returns), with the owner at a bell.</summary>
    private static bool InTrip(CourierPhase phase) => phase < CourierPhase.ReturnOwner || phase == CourierPhase.Consolidate;
    /// <summary>Only an unsettled trade blocks clearing a stopped run; travel and local placement have no cross-account custody in limbo.</summary>
    private static bool HasPendingTrade(CourierJournal r) => r.Pending?.Action.Phase is CourierPhase.HandOut or CourierPhase.HandIn;
    public CourierSnapshot Snapshot()=>Project(store.Load());
    private CourierSnapshot Project(CourierJournal r)
    {
        var carrierless=!r.Plan.Trips.IsEmpty && Carrierless(r);
        var connected=carrierless || peer is not RQ.UI.ICourierLink link || link.Peers.Count(p=>CourierPlanner.Exact(p.Character,r.Settings.Carrier))==1;
        var pending=r.Pending;
        var attempt=pending?.Action.Phase switch {
            CourierPhase.HandOut or CourierPhase.HandIn => pending.LocalMayHaveStarted==false ||
                trade is ICourierSettlement or ICourierDispatchEvidence || r.Evidence.Any(e=>e.Step==pending.Id && e.BothTradesVerified),
            // Placement settles from its journaled operations and fresh bags; no retainer baseline is needed.
            CourierPhase.Finish => false,
            _=>true
        };
        var step = r.Schedule.Skip(r.Cursor).FirstOrDefault();
        return new(r.Run, r.State, step?.Phase.ToString() ?? r.State, carrierless?"":r.State=="Running"?peerPhase:"Idle", r.Plan.Trips.IsEmpty ? 0 :
            step is null || !InTrip(step.Phase) ? r.Plan.Trips.Length : step.Trip + 1, r.Plan.Trips.Length, r.Code, r.Detail,
            [..r.Plan.Trips.SelectMany(t=>t.Manifest).GroupBy(s=>s.Line).Select(g=>new LineProgress(g.Key,g.First().Name,
                r.Delivered.GetValueOrDefault(g.Key),g.Sum(s=>s.Units)-r.Delivered.GetValueOrDefault(g.Key),g.First(),g.Any(s=>!s.Hq),g.Any(s=>s.Hq)))], r.Plan.Refusals,
            connected && attempt && r.State is "Failed" or "Interrupted" or "Indeterminate",
            (connected || r.Custody.IsEmpty) && !HasPendingTrade(r) && r.State is "Completed" or "Failed" or "Interrupted" or "Indeterminate" or "Refused",Rows(r.Custody,r.Settings.Carrier),
            Unverified(r),r.Plan.PlanId,r.RequestId);
    }
    private static CourierUnverified? Unverified(CourierJournal r)
    {
        if(r.Pending is not {} p || p.Action.Batch.IsEmpty || r.State is not ("Failed" or "Interrupted" or "Indeterminate")) return null;
        var owner=p.OwnerBefore.Character; var carrier=r.Settings.Carrier;
        var (from,to)=p.Action.Phase==CourierPhase.HandIn?(carrier,owner):p.Action.Phase==CourierPhase.HandOut?(owner,carrier):(owner,owner);
        return new(p.Action.Phase.ToString(),from?.CharacterName??"Unknown",to?.CharacterName??"Unknown",Rows(p.Action.Batch,from));
    }
    internal static ImmutableArray<CustodyRow> Rows(IEnumerable<Shipment> units,OwnerScope? holder)=>
        [..units.GroupBy(s=>(s.Item,s.Hq)).Select(g=>new CustodyRow(g.First().Name,g.First().IsGil?"":g.Key.Hq?"HQ":"NQ",g.Sum(s=>s.Units),holder?.CharacterName??"Unknown"))];
    /// <summary>The full journal, for the IPC status projection (custody split into stacks and gil).</summary>
    internal CourierJournal Journal() => store.Load();
    public async Task<CourierSnapshot> ClearAsync(Guid expectedRun,string? acknowledgement)
    {
        if(!await gate.WaitAsync(0)) throw new InvalidOperationException("CourierAlreadyRunning");
        try
        {
            var r=store.Load();
            if(r.Run!=expectedRun || r.State is not ("Completed" or "Failed" or "Interrupted" or "Indeterminate" or "Refused"))
                throw new InvalidOperationException("RunNotClearable");
            if(HasPendingTrade(r))
                throw new InvalidOperationException("Settle the pending trade before clearing custody.");
            // Completed includes a durable empty-ledger Finish acknowledgement.
            CourierReceipt? receipt=null;
            if (r.State is not ("Completed" or "Refused") && r.Start.HasStableIdentity && !Carrierless(r))
                try { receipt=await Send(r,CourierCommand.Status,r.Start,[]); }
                catch(CourierFault e) when(r.Custody.IsEmpty && e.Code is "CarrierEndpointAmbiguous" or "PeerLinkLost")
                { warning($"OfflineClear {r.Run}: {e.Message}"); }
            if(receipt?.ActiveRun!=r.Run){
                if(!r.Custody.IsEmpty)throw new InvalidOperationException("Carrier does not acknowledge this run's custody.");
                receipt=null;
            }
            if(receipt?.Unconfirmed!=null) throw new InvalidOperationException("Carrier trade is unsettled.");
            var held=receipt?.Custody ?? [];
            if(!held.IsEmpty || !r.Custody.IsEmpty)
            {
                static string Describe(IEnumerable<Shipment> units)=>string.Join(", ", units.OrderBy(s=>s.Line)
                    .Select(s=>$"{s.Name}{(s.Hq?" HQ":"")} × {s.Units}"));
                var required=$"Owner ledger [{Describe(r.Custody)}]; carrier ledger [{Describe(held)}]";
                if(acknowledgement!=required)throw new CustodyAcknowledgementRequired(required,Rows(held,r.Settings.Carrier));
                r.ClearAcknowledgement=required;store.Save(r);
            }
            if(receipt?.ActiveRun==r.Run)await Send(r,CourierCommand.Clear,r.Start,held);
            new CourierStore<CourierJournal>(Path.Combine(directory,$"courier-closed-{r.Run:N}.json")).Save(r);
            store.Save(new()); return Snapshot();
        }
        finally{gate.Release();}
    }
    public sealed class CustodyAcknowledgementRequired(string held,ImmutableArray<CustodyRow> carrierRows=default) : Exception(held)
    {
        /// <summary>The carrier's confirmed ledger behind this acknowledgement, for display.</summary>
        public ImmutableArray<CustodyRow> CarrierRows => carrierRows.IsDefault ? [] : carrierRows;
    }
    public async Task StopAsync()
    {
        stop = true;
        try { await calls.Call("OwnerCancelPreLock",10,trade.CancelPreLockAsync); }
        finally {var r=store.Load();if(r.State=="Running" && !Carrierless(r))await Send(r,CourierCommand.Stop,r.Start,[]);}
    }
    private Task<InventoryView> Observe() => calls.Call("OwnerBags", 15, inventory.ObserveAsync);
    private async Task<CourierReceipt> Send(CourierJournal r, CourierCommand type, OwnerScope partner, ImmutableArray<Shipment> manifest,
        MeetingBell? bell = null, Guid? id = null, Guid? confirm = null, TradeEvidence? evidence = null, Action<CourierReceipt>? cancelled = null,InventoryView? carrierBaseline=null)
    {
        var step = id ?? Guid.NewGuid();
        var message = new CourierMessage(bell?.Ward != null ? 2 : 1, r.Run, step, type, r.Start, partner, r.Settings.Carrier!, manifest, bell, confirm, evidence,r.RunKey,carrierBaseline);
        var reply = await calls.Call("Peer" + type, Deadline.PeerSeconds(bell, type, manifest), async d =>
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
    public async Task<CourierSnapshot> StartAsync(CourierPlan plan, CourierSettings settings, Guid? runId = null)
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
        bool beginAttempted = false, existingRun=false, carrierless=false;
        // A carrier-less run has no carrier to ask: it stands in an empty bag view and ledger for one.
        Task<CourierReceipt> CarrierStatus() => carrierless
            ? Task.FromResult(new CourierReceipt(r.Run, Guid.Empty, new(r.Settings.Carrier ?? new(), 0, []), Custody: []))
            : Send(r, CourierCommand.Status, r.Start, []);
        // The carrier's gate serializes its commands, so while it travels in the background the owner sends it
        // nothing: owner-only steps reuse the last verified carrier snapshot (its custody cannot change in transit).
        Task<CourierReceipt>? carrierMove = null;
        bool Moving() => carrierMove is { IsCompleted: false };
        Task<CourierReceipt> Dispatch(CourierCarrierMove move)
        {
            var sent = Send(r, move.Command, move.Partner, [], move.Bell, id: move.Id);
            // Observed here as well, so a move abandoned by a failed run never raises an unobserved exception.
            _ = sent.ContinueWith(static x => _ = x.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return sent;
        }
        void Launch(CourierCarrierMove move) { r.CarrierMove = move; store.Save(r); carrierMove = Dispatch(move); }
        // Waits for the journaled move (re-sending it on a resume) and returns it once the carrier has arrived.
        async Task<CourierCarrierMove?> SettleMove()
        {
            if (r.CarrierMove is not { } move) return null;
            var sent = carrierMove ?? Dispatch(move); carrierMove = null;
            await sent;
            r.CarrierMove = null; store.Save(r);
            return move;
        }
        IDisposable? lease = null, tradeHold = null; AutoRetainerSuppression.Scope? suppress = null;
        void ReleaseOwner()
        {
            try { Interlocked.Exchange(ref tradeHold,null)?.Dispose(); }
            finally { try { Interlocked.Exchange(ref suppress,null)?.Dispose(); } finally { Interlocked.Exchange(ref lease,null)?.Dispose(); } }
        }
        Volatile.Write(ref releaseOwner,ReleaseOwner);
        try
        {
            // A prepared launch already cleared stop; any Stop since then stands.
            if (!launchPrepared) stop = false;
            launchPrepared = false;
            if(unloaded)throw new OperationCanceledException("Courier unloaded.");
            var resume = existingRun = r.State is not ("Idle" or "Completed" or "Refused");
            if (!resume)
            {
                r = new() { Run = runId ?? Guid.NewGuid(), Plan = plan, Settings = settings, RequestId = plan.RequestId,
                    RunKey=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
                if (!plan.Refusals.IsEmpty || plan.Trips.IsEmpty) { r.State = "Refused"; r.Code = "PlanRefused"; warning($"{r.Run} PlanRefused"); return Project(r) with {CanClear=false}; }
            }
            carrierless = Carrierless(r);
            if (!automation.TryAcquire("courier owner", out lease) || lease == null) throw new CourierFault(CourierOutcome.Failed, "AutomationBusy", automation.AcquisitionBlockReason ?? "Busy");
            if(needsSuppression())suppress = suppression.Acquire();
            if (suppression.IsBusy) throw new CourierFault(CourierOutcome.Failed, "AutoRetainerBusy", "AutoRetainer still active");
            peerPhase = "Idle";
            if (!carrierless) peer.LinkLost += Lost;
            peer.Progress += Progress; peer.StopRequested += RemoteStop;
            // The trade hold keeps MarketMafioso off the trade window; a run with no trades needs none.
            if (!carrierless && trade is ICourierHoldPort holds)
                tradeHold = await calls.Call("OwnerTradeHold",10,d=>holds.BeginHold(r.Run,d));
            var owner = await Observe();
            if (!resume || !r.Start.HasStableIdentity && !r.PeerClaimed && r.Cursor == 0 && r.Pending == null) r.Start = owner.Character;
            var status = await CarrierStatus();
            var carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Fresh observation required.");
            if (!carrierless && !CourierPlanner.Exact(carrier.Character, r.Settings.Carrier)) throw Fault("CarrierIdentityMismatch", "Unexpected carrier.");
            if(status.ActiveRun is {} other && other!=r.Run && !resume && status.Custody.IsEmpty && status.Unconfirmed==null)
            {
                var closed=new CourierStore<CourierJournal>(Path.Combine(directory,$"courier-closed-{other:N}.json")).Load();
                if(closed.Run==other && closed.Custody.IsEmpty && closed.Pending==null && CourierPlanner.Exact(closed.Settings.Carrier,r.Settings.Carrier))
                    await Send(closed,CourierCommand.Clear,closed.Start,[]);
            }
            if (resume)
            {
                if(r.Pending is {Action.Phase:CourierPhase.HandOut or CourierPhase.HandIn} candidate &&
                    (!candidate.LocalMayHaveStarted || trade is ICourierDispatchEvidence trace &&
                    await trace.WasNotOpened(candidate.Id,Deadline.After("OwnerNeverOpened",10)).WaitAsync(TimeSpan.FromSeconds(10)) is {Outcome:CourierOutcome.Succeeded,Value:true}))
                {
                    await Fence(r,candidate,owner);
                    status=await Send(r,CourierCommand.Status,r.Start,[]);
                }
                r.ResumeOwner = owner; r.ResumeCarrier = carrier; store.Save(r);
                if (r.Pending is {Action.Phase: CourierPhase.HandOut or CourierPhase.HandIn} uncertain && trade is ICourierSettlement recovery &&
                    (status.Unconfirmed==uncertain.Id || status.LastConfirmed==uncertain.Id))
                {
                    int sign=uncertain.Action.Phase==CourierPhase.HandOut ? -1 : 1;
                    var local=new TradeEvidence(uncertain.OwnerBefore,owner);
                    var remote=new TradeEvidence(uncertain.CarrierBefore,carrier);
                    if(Custody.TrySlice(local,uncertain.OwnerBefore.Character,remote,r.Settings.Carrier!,uncertain.Action.Batch,sign,out var moved,out var rest))
                    {
                        if(moved.IsEmpty)throw Fault("TradeClosureUnproven","Unchanged bags alone cannot settle a dispatched trade.");
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
                            var after=r.Cursor+1+(sign==1 ? DepositsFor(r.Schedule,r.Cursor,action.Batch) : 0);
                            var next=new List<CourierStep>();
                            if(sign==1)next.Add(new(CourierPhase.Deposit,action.Trip,moved));
                            next.Add(action with {Batch=rest});
                            if(sign==1)next.Add(new(CourierPhase.Deposit,action.Trip,rest));
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
                if(r.Pending is {Action.Phase:CourierPhase.Retrieve or CourierPhase.Deposit} move)
                {
                    if(!CourierPlanner.Exact(owner.Character,move.OwnerBefore.Character) || !carrierless && !Agrees(move.CarrierBefore,carrier,r) ||
                        !Custody.Covers(status.Custody,r.Custody) || !Custody.Covers(r.Custody,status.Custody))
                        throw Fault("PlacementSettlementIdentity","Placement owner, carrier or custody changed.");
                    SettlePlacement(r,move,owner);
                    r.OwnerCheckpoint=owner;r.CarrierCheckpoint=carrier;r.Pending=null;store.Save(r);
                }
                if (r.Pending is { Action.Phase: CourierPhase.Consolidate } con)
                {
                    if (!CourierPlanner.Exact(owner.Character, con.OwnerBefore.Character) || !carrierless && !Agrees(con.CarrierBefore, carrier, r) ||
                        !Custody.Covers(status.Custody, r.Custody) || !Custody.Covers(r.Custody, status.Custody))
                        throw Fault("PlacementSettlementIdentity", "Consolidation owner, carrier or custody changed.");
                    var conLines = r.Plan.Consolidations.Where(c => CourierPlanner.Exact(c.Destination, owner.Character)).ToArray();
                    var bagsUnchanged = conLines.All(c =>
                        (c.Quality == ItemQualityPolicy.HqOnly || owner.Count(c.Item, false) == con.OwnerBefore.Count(c.Item, false)) &&
                        (c.Quality == ItemQualityPolicy.NqOnly || owner.Count(c.Item, true) == con.OwnerBefore.Count(c.Item, true)));
                    if (!bagsUnchanged)
                        throw Fault("ConsolidationUnsettled", $"Destination bags changed during interrupted consolidation {con.PlacementOperation ?? "(unrecorded)"}; settle in-transit units before retrying.");
                    if (con.PlacementOperation is null)
                    {
                        r.OwnerCheckpoint = owner; r.CarrierCheckpoint = carrier; r.Pending = null; store.Save(r);
                    }
                    else
                    {
                        var opStatus = placement.OperationStatus(con.PlacementOperation, owner.Character);
                        if (opStatus == OperationStatuses.Succeeded)
                        {
                            r.Cursor++; r.OwnerCheckpoint = owner; r.CarrierCheckpoint = carrier; r.Pending = null; store.Save(r);
                        }
                        else if (opStatus == OperationStatuses.Failed)
                        {
                            var prefix = $"{owner.Character.CharacterName}: ";
                            r.ConsolidationLimits.RemoveAll(limit => limit.StartsWith(prefix, StringComparison.Ordinal));
                            r.ConsolidationLimits.Add(prefix + $"ConsolidationLimited: {con.PlacementOperation}");
                            r.Cursor++; r.OwnerCheckpoint = owner; r.CarrierCheckpoint = carrier; r.Pending = null; store.Save(r);
                        }
                        else
                        {
                            throw Fault("ConsolidationUnsettled", $"Previous consolidation {con.PlacementOperation} did not settle ({opStatus ?? "unknown"}); verify retainers and bags before retrying.");
                        }
                    }
                }
                var travel=r.Pending?.Action.Phase is CourierPhase.EnsureSource or CourierPhase.SwitchToDestination or CourierPhase.ReturnOwner
                    or CourierPhase.ReturnCarrier or CourierPhase.CarrierToSource or CourierPhase.CarrierToDestination or CourierPhase.ReturnDestinationHome;
                var unstartedTrade=r.Pending==null && r.Schedule.Skip(r.Cursor).FirstOrDefault()?.Phase is CourierPhase.HandIn or CourierPhase.HandOut;
                if (r.Pending != null && !travel || status.Unconfirmed != null || !Custody.Covers(status.Custody, r.Custody) || !Custody.Covers(r.Custody, status.Custody)
                    || (!travel && !unstartedTrade && r.Cursor != 0 && (!Agrees(r.OwnerCheckpoint, owner, r) || !carrierless && !Agrees(r.CarrierCheckpoint, carrier, r))))
                    throw Fault("ResumeUnsettled", "Journal intent, ledger or bags cannot prove a forward checkpoint.");
            }
            if (r.Schedule.IsEmpty) { r.Schedule = [.. Steps(r)]; r.ScheduleVersion = 1; }
            // Begin may succeed on the peer even if its acknowledgement is lost.
            if(stop)throw new CourierFault(CourierOutcome.Cancelled,"StopBeforeBegin","Begin was not dispatched.");
            r.State = "Running"; r.Code=r.Detail=""; store.Save(r); beginAttempted = true;
            if (!carrierless)
            {
                await Send(r, CourierCommand.Begin, r.Start, []);
                r.PeerClaimed = true; store.Save(r);
            }
            var steps = r.Schedule;
            for (; r.Cursor < steps.Length;)
            {
                if (!carrierless) (trade as ICourierHoldPort)?.CheckHold();
                if (stop) throw new CourierFault(CourierOutcome.Cancelled, "StopRequested", "Stopped at verified checkpoint.");
                var s = steps[r.Cursor]; var t = r.Plan.Trips[s.Trip];
                bool ownerOnly = s.Phase is CourierPhase.EnsureSource or CourierPhase.Retrieve or CourierPhase.Deposit
                    or CourierPhase.Consolidate or CourierPhase.ReturnOwner or CourierPhase.ReturnDestinationHome;
                // CarrierToSource and ReturnCarrier settle the move themselves; any other carrier step needs it settled first.
                if (r.CarrierMove != null && !ownerOnly && s.Phase is not (CourierPhase.CarrierToSource or CourierPhase.ReturnCarrier))
                    await SettleMove();
                else if (r.CarrierMove is { } resumed && carrierMove == null && ownerOnly)
                    carrierMove = Dispatch(resumed); // A resumed run sets the carrier off again behind the owner.
                if (InTrip(s.Phase) || r.Code=="")owner=await Observe();
                if (!Moving())
                {
                    status = await CarrierStatus();
                    carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Missing before snapshot");
                }
                // A trip's bag slots are re-checked once, before anything of it is retrieved. A resumed,
                // partly retrieved batch is never the whole manifest, so it cannot re-split the trip.
                if (s.Phase == CourierPhase.Retrieve && steps[r.Cursor - 1].Phase == CourierPhase.EnsureSource &&
                    (r.ScheduleVersion == 0 ? s.Batch[0] == t.Manifest[0] : s.Batch.SequenceEqual(t.Manifest)))
                {
                    // Stacks already in the owner's bags take no owner slot; only retainer-sourced ones are retrieved into it.
                    var retrieved = t.Manifest.Count(x => x.From != null);
                    // Gil takes no slot anywhere: a gil-only trip needs no free slot at all.
                    var slotted = t.Manifest.Count(x => !x.IsGil);
                    // A local trip takes nothing into the carrier's bags.
                    var room = t.Local ? int.MaxValue : carrier.FreeSlots;
                    if (slotted > 0 && room <= 0 || retrieved > 0 && owner.FreeSlots <= 0)
                        throw new CourierFault(CourierOutcome.Failed, "BagSlotLimit", "No free slot for the next trip.");
                    if (slotted > room || retrieved > owner.FreeSlots)
                    {
                        var split = CourierPlanner.Pack(t.Manifest, room, owner.FreeSlots).Select(b => t with { Manifest = b }).ToArray();
                        r.Plan = r.Plan with { Trips = r.Plan.Trips.RemoveAt(s.Trip).InsertRange(s.Trip, split) };
                        // Previous trips may have dynamic hand-in batches; only replace the unstarted suffix.
                        var remaining = Steps(r).SkipWhile(x => x.Trip != s.Trip).Skip(1);
                        steps = r.Schedule = [.. steps.Take(r.Cursor), .. remaining];
                        s = steps[r.Cursor]; t = r.Plan.Trips[s.Trip]; store.Save(r);
                    }
                }
                // Gil is fresh-checked against both caps before any of it is traded: the source must carry it and
                // neither side may pass the carried-gil cap. Retainers are never drawn on for gil.
                if (s.Batch.Where(x => x.IsGil).Sum(x => (long)x.Units) is > 0 and var gil)
                {
                    if (s.Phase == CourierPhase.Retrieve && owner.Gil < gil)
                        throw new CourierFault(CourierOutcome.Failed, "SourceGilShort", $"{owner.Character.CharacterName} carries {owner.Gil:N0} gil; the trip needs {gil:N0}.");
                    if (s.Phase is CourierPhase.Retrieve or CourierPhase.HandOut && !t.Local && (long)carrier.Gil + gil > CourierPlanner.GilCap)
                        throw new CourierFault(CourierOutcome.Failed, "CarrierGilCapExceeded", $"{carrier.Character.CharacterName} carries {carrier.Gil:N0} gil; {gil:N0} more passes the {CourierPlanner.GilCap:N0} cap.");
                    if (s.Phase == CourierPhase.HandIn && (long)owner.Gil + gil > CourierPlanner.GilCap)
                        throw new CourierFault(CourierOutcome.Failed, "DestinationGilCapExceeded", $"{owner.Character.CharacterName} carries {owner.Gil:N0} gil; {gil:N0} more passes the {CourierPlanner.GilCap:N0} cap.");
                }
                // Stock a placement step only verifies (bag-held stacks and gil) must be in the fresh bags before
                // the step is journaled, as the placement port checks: a shortfall then fails without an intent.
                if (s.Phase is CourierPhase.Retrieve or CourierPhase.Deposit &&
                    s.Batch.Where(x => s.Phase == CourierPhase.Deposit || x.From == null).GroupBy(x => (x.Item, x.Hq))
                        .FirstOrDefault(g => owner.Count(g.Key.Item, g.Key.Hq) < g.Sum(x => (long)x.Units)) is { } shortfall)
                    throw new CourierFault(CourierOutcome.Failed, shortfall.Key.Item == CourierPlanner.GilItemId
                        ? s.Phase == CourierPhase.Retrieve ? "SourceGilShort" : "DestinationGilShort" : "BagStockShort",
                        $"{shortfall.First().Name}: {owner.Character.CharacterName} holds {owner.Count(shortfall.Key.Item, shortfall.Key.Hq):N0}, the step needs {shortfall.Sum(x => (long)x.Units):N0}.");
                if (s.Phase == CourierPhase.HandIn && owner.FreeSlots < s.Batch.Count(x => !x.IsGil))
                {
                    // Hand in retainer-bound stacks before bag-bound ones so each Deposit frees slots for the next batch.
                    var ordered = s.Batch.Any(x => x.To == null) && s.Batch.Any(x => x.To != null)
                        ? [.. s.Batch.Where(x => x.To != null), .. s.Batch.Where(x => x.To == null)]
                        : s.Batch;
                    int count = 0, slots = owner.FreeSlots;
                    foreach (var unit in ordered)
                    {
                        if (unit.IsGil) { count++; continue; } // Gil needs no bag slot.
                        // A partial stack can take a whole manifest slot without consuming a free bag slot.
                        var held = owner.Count(unit.Item, unit.Hq) + ordered.Take(count).Where(x => x.Item == unit.Item && x.Hq == unit.Hq).Sum(x => x.Units);
                        if ((unit.StackSize - held % unit.StackSize) % unit.StackSize < unit.Units && --slots < 0) break;
                        count++;
                    }
                    if (count == 0) throw new CourierFault(CourierOutcome.Failed, "DestinationBagsFull", "Cannot accept the next custody stack.");
                    if (count < ordered.Length)
                    {
                        // Hand in what fits, stow it in one bell session, then hand in the rest.
                        var deposits = DepositsFor(steps, r.Cursor, s.Batch);
                        ImmutableArray<Shipment> head = ordered[..count], tail = ordered[count..];
                        steps = r.Schedule = [..steps.Take(r.Cursor), s with {Batch=head}, new(CourierPhase.Deposit, s.Trip, head),
                            s with {Batch=tail}, new(CourierPhase.Deposit, s.Trip, tail), ..steps.Skip(r.Cursor+1+deposits)];
                        s = steps[r.Cursor]; store.Save(r);
                    }
                }
                // No five-slot pre-split: one trade step runs every window back to back (the trade runner
                // packs fragmented bag slots into successive windows and credits each before the next).
                if (!carrierless && !CourierPlanner.Exact(carrier.Character, r.Settings.Carrier) ||
                    !Custody.Covers(status.Custody, r.Custody) || !Custody.Covers(r.Custody, status.Custody))
                    throw Fault("CustodyMismatch", "Carrier identity or custody changed.");
                if (s.Phase is CourierPhase.Retrieve or CourierPhase.CarrierToSource or CourierPhase.HandOut &&
                    !CourierPlanner.Exact(owner.Character, t.Source) ||
                    s.Phase is CourierPhase.Deposit or CourierPhase.CarrierToDestination or CourierPhase.HandIn or CourierPhase.Consolidate &&
                    !CourierPlanner.Exact(owner.Character, t.Destination)) throw Fault("OwnerIdentityMismatch", owner.Character.CharacterName);
                if (stop) throw new CourierFault(CourierOutcome.Cancelled,"StopRequested","Stopped before dispatch.");
                // Placement needs no retainer baseline: a resume settles it from the journal and the bags.
                var intent=r.Pending ?? new(Guid.NewGuid(),s,owner,carrier);
                if (s.Phase is not (CourierPhase.HandOut or CourierPhase.HandIn)) { r.Pending=intent; store.Save(r); }
                CourierGame.Step.Value = intent.Id;
                CourierGame.CanContinue.Value=()=>!stop && !unloaded;
                information?.Invoke($"{r.Run}/{intent.Id} owner {s.Phase} trip {s.Trip + 1}");
                CourierReceipt? finished = null;
                switch (s.Phase)
                {
                    case CourierPhase.EnsureSource when t.Local:
                        // The carrier is not needed here. Ahead of the run's first carrier trip it sets out for that
                        // pickup meanwhile; later ones it heads for after the previous hand-in.
                        if (r.CarrierMove == null && r.Custody.IsEmpty && Carried(r, s.Trip, -1) == null && Carried(r, s.Trip, 1) is { } first)
                            Launch(new(Guid.NewGuid(), CourierCommand.Travel, first.Source, first.Pickup));
                        await Switch(t.Source); await Meet(t.Pickup); break;
                    case CourierPhase.EnsureSource:
                        // The carrier heads for the pickup in the background while the owner switches, reaches it
                        // and retrieves; CarrierToSource waits for it. It may already have set out after the previous
                        // trip's last hand-in, or be there when that trip dropped off at the same bell.
                        if (r.CarrierMove is { } earlier && !(earlier.Command == CourierCommand.Travel && earlier.Bell?.SamePlace(t.Pickup) == true))
                            await SettleMove();
                        if (r.CarrierMove == null && Carried(r, s.Trip, -1)?.Dropoff.SamePlace(t.Pickup) != true)
                            Launch(new(Guid.NewGuid(), CourierCommand.Travel, t.Source, t.Pickup));
                        await Switch(t.Source); await Meet(t.Pickup); break;
                    case CourierPhase.SwitchToDestination:
                        // Both legs are authorized by this single persisted rendezvous intent.
                        async Task OwnerToDestination()
                        {
                            // A source met at a bridge off its home world goes home first, alongside the carrier's travel,
                            // so it is never left on a visited data center. A resumed step whose switch already ran skips it.
                            if (t.Pickup.World != t.Source.HomeWorldId && CourierPlanner.Exact(owner.Character, t.Source))
                                await calls.Call("SourceReturnHome", Deadline.DataCenterSeconds, worlds.ReturnHomeAsync);
                            await Switch(t.Destination); await Meet(t.Dropoff);
                        }
                        // An owner leg that faults stops the carrier instead of waiting out its travel deadline.
                        await await FirstFaultCancelsOther(r, OwnerToDestination(), t.Pickup.SamePlace(t.Dropoff) ? null :
                            Send(r, CourierCommand.Travel, t.Destination, [], t.Dropoff,id:intent.Id), "SwitchToDestination"); break;
                    case CourierPhase.CarrierToSource:
                    case CourierPhase.CarrierToDestination:
                        // The carrier set out for the pickup in the background (in EnsureSource, or after the previous
                        // trip's last hand-in). A pre-batching schedule, or a resume whose move already settled, sends
                        // it here; a carrier already at the bell arrives on the first tick.
                        if (s.Phase == CourierPhase.CarrierToSource &&
                            !(await SettleMove() is { Command: CourierCommand.Travel, Bell: { } went } && went.SamePlace(t.Pickup)) &&
                            Carried(r, s.Trip, -1)?.Dropoff.SamePlace(t.Pickup) != true)
                            await Send(r, CourierCommand.Travel, owner.Character, [], t.Pickup,id:intent.Id);
                        await calls.Call("Rendezvous", 30, d => bells.ObserveTradeableAsync(r.Settings.Carrier!, d));
                        break;
                    case CourierPhase.HandOut:
                    case CourierPhase.HandIn:
                        try { await HandOffAsync(r,intent); }
                        catch(CourierFault) when(r.Pending==null && r.Evidence.All(e=>e.Step!=intent.Id))
                        {
                            // Local preflight failed. No start could have been sent without Pending.
                            // Record a bilateral zero receipt if the peer can still be reached.
                            try {await Fence(r,intent,await Observe());}
                            catch(Exception e){warning($"{r.Run}/{intent.Id} PreflightSettlement: {e.Message}");}
                            throw;
                        }
                        break;
                    case CourierPhase.Retrieve:
                    case CourierPhase.Deposit:
                        bool deposit = s.Phase == CourierPhase.Deposit;
                        // Gil and bag-held stacks never touch a retainer and were verified in the fresh bags above. A
                        // batch with no retainer leg opens no summoning bell at all (a bridge plaza has none); its
                        // Deposit still credits delivery below.
                        var placed = s.Batch.Where(x => !x.IsGil).ToImmutableArray();
                        if (!placed.Any(x => deposit ? x.To != null : x.From != null))
                            break;
                        // The whole batch moves in one bell session, so its budget scales with the stacks.
                        await calls.Call("CourierPlacement", Math.Clamp(placed.Length * 30, 180, 900), d => placement.MoveBatchAsync(
                            new() { Id = r.Plan.PlanId, Revision = r.Plan.Revision, Owner = owner.Character },
                            placed, deposit, lease!, () => !stop && automation.Owns(lease), d, id =>
                            {
                                var held = r.Pending ?? intent;
                                r.Pending = held with { PlacementOperations = (held.PlacementOperations.IsDefault ? ImmutableArray<string>.Empty : held.PlacementOperations).Add(id) };
                                store.Save(r);
                            }));
                        break;
                    case CourierPhase.Consolidate:
                        await ConsolidateAsync(r, intent, t.Destination, owner.Character, lease!);
                        break;
                    case CourierPhase.ReturnCarrier when carrierless:
                        break;
                    case CourierPhase.ReturnOwner:
                    case CourierPhase.ReturnCarrier:
                    case CourierPhase.ReturnDestinationHome:
                        try
                        {
                            if(s.Phase==CourierPhase.ReturnDestinationHome)
                            {
                                // The carrier usually set off home after the last hand-in; a run resumed here sends it now,
                                // so it returns alongside the destination.
                                if(!carrierless && r.CarrierMove==null && r.Custody.IsEmpty && status.Custody.IsEmpty && status.Unconfirmed==null &&
                                    steps.Skip(r.Cursor+1).Any(x=>x.Phase==CourierPhase.ReturnCarrier))
                                    Launch(new(Guid.NewGuid(),CourierCommand.ReturnHome,r.Start,null));
                                await Switch(t.Destination);
                                await calls.Call("DestinationReturnHome",Deadline.DataCenterSeconds,worlds.ReturnHomeAsync);
                            }
                            else if(s.Phase==CourierPhase.ReturnOwner)
                            {
                                // A run resumed here has not sent the carrier home yet: it returns alongside the owner.
                                if(!carrierless && r.CarrierMove==null && r.Custody.IsEmpty && status.Custody.IsEmpty && status.Unconfirmed==null &&
                                    steps.Skip(r.Cursor+1).Any(x=>x.Phase==CourierPhase.ReturnCarrier))
                                    Launch(new(Guid.NewGuid(),CourierCommand.ReturnHome,r.Start,null));
                                await Switch(r.Start);
                            }
                            // The carrier usually set off home after the last hand-in, while the owner stowed and returned.
                            else if(await SettleMove() is not {Command:CourierCommand.ReturnHome})
                                await Send(r,CourierCommand.ReturnHome,r.Start,[],id:intent.Id);
                        }
                        catch(CourierFault fault) when (!stop && !unloaded && fault.Outcome!=CourierOutcome.Cancelled &&
                            r.Custody.IsEmpty && status.Custody.IsEmpty && status.Unconfirmed==null &&
                            r.Plan.Trips.SelectMany(trip=>trip.Manifest).GroupBy(x=>x.Line)
                                .All(g=>r.Delivered.GetValueOrDefault(g.Key)==g.Sum(x=>x.Units)))
                        {
                            // A carrier still returning holds the custody it held when it set off: empty, as checked above.
                            if(s.Phase==CourierPhase.ReturnCarrier)r.CarrierMove=null; // The failed return is abandoned, as before.
                            finished=Moving() ? null : await CarrierStatus();
                            if(finished is {} fresh && (!fresh.Custody.IsEmpty || fresh.Unconfirmed!=null))
                                throw Fault("ReturnCustodyUnsettled","Carrier reported custody or an unconfirmed hand-off after the return failed.");
                            r.Code=fault.Code;
                            r.Detail+=(r.Detail.Length==0?"":"; ")+$"{s.Phase}: {fault.Code}: {fault.Message}";
                            warning($"{r.Run} {r.Detail}");
                        }
                        break;
                    case CourierPhase.Finish when carrierless:
                        break;
                    case CourierPhase.Finish:
                        if (!r.Custody.IsEmpty) throw Fault("CustodyRemaining", "Carrier still holds stock");
                        // Finish closes the carrier's run, so its receipt is the terminal checkpoint;
                        // a later Status could no longer be authorized against this run.
                        finished = await Send(r, CourierCommand.Finish, r.Start, []); r.PeerClaimed = false; break;
                }
                if (s.Phase is CourierPhase.HandOut or CourierPhase.HandIn)
                {
                    r.OwnerCheckpoint = r.Evidence[^1].Owner!.After;
                    r.CarrierCheckpoint = r.Evidence[^1].Carrier!.After;
                }
                else
                {
                    if(InTrip(s.Phase) || r.Code=="")r.OwnerCheckpoint = await Observe();
                    r.CarrierCheckpoint = finished?.Bags ?? (s.Phase == CourierPhase.Finish ? r.CarrierCheckpoint : Moving() ? carrier :
                        (await CarrierStatus()).Bags);
                }
                if(s.Phase==CourierPhase.Deposit)
                    foreach(var shipped in s.Batch)r.Delivered[shipped.Line]=checked(r.Delivered.GetValueOrDefault(shipped.Line)+shipped.Units);
                r.Pending = null; r.Cursor++; store.Save(r);
                // The trip's last hand-in frees the carrier (a bag-slot split leaves custody for the next one): it sets
                // out for the next trip's pickup, or home, while the owner deposits, consolidates and returns.
                if (s.Phase == CourierPhase.HandIn && r.Custody.IsEmpty && r.CarrierMove == null && !stop)
                {
                    CourierCarrierMove? next = Carried(r, s.Trip, 1) is { } following
                        ? !following.Pickup.SamePlace(t.Dropoff)
                            ? new(Guid.NewGuid(), CourierCommand.Travel, following.Source, following.Pickup) : null
                        : steps.Skip(r.Cursor).Any(x => x.Phase == CourierPhase.ReturnCarrier)
                            ? new(Guid.NewGuid(), CourierCommand.ReturnHome, r.Start, null) : null;
                    if (next != null)
                    {
                        // The snapshot the owner-only steps reuse while the carrier is away.
                        status = await Send(r, CourierCommand.Status, r.Start, []);
                        carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Missing before the carrier set out.");
                        if (status.Unconfirmed == null && Custody.Covers(status.Custody, r.Custody) && Custody.Covers(r.Custody, status.Custody))
                            Launch(next);
                    }
                }
            }
            r.State = "Completed";
            if (r.ConsolidationLimits.Count > 0)
            {
                r.Detail += (r.Detail.Length == 0 ? "" : "; ") + string.Join("; ", r.ConsolidationLimits);
                if (r.Code == "") r.Code = "ConsolidationLimited";
            }
            if(r.Code=="")r.Code="Succeeded"; store.Save(r);
        }
        catch (Exception e)
        {
            r.Code = (e as CourierFault)?.Code ?? "UnexpectedFailure"; r.Detail = e.Message;
            r.State = r.Pending != null ? "Indeterminate" :
                e is CourierFault { Outcome: CourierOutcome.Cancelled } ? "Interrupted" : "Failed";
            warning($"{r.Run}/{r.Pending?.Id} {r.State} {r.Code}: {e.Message}");
            if(!existingRun && !beginAttempted)return Project(r) with {CanResume=false,CanClear=false};
            store.Save(r);
            if (!carrierless && (r.PeerClaimed || beginAttempted)) try { await Send(r, CourierCommand.Stop, r.Start, []); } catch (Exception cleanup) { warning(cleanup.Message); }
            // Stop reaches the carrier ahead of its command queue and cancels a background move; the move stays
            // journaled so a resume re-sends it under the same id. Its fault is observed at dispatch.
            if (Interlocked.Exchange(ref carrierMove, null) is { } abandoned &&
                await Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromSeconds(15))) != abandoned)
                warning($"{r.Run} CarrierMoveStopDeadline: 15s elapsed after Stop.");
        }
        finally { peer.LinkLost -= Lost; peer.Progress -= Progress; peer.StopRequested -= RemoteStop; calls.WhenSettled(() => { try { ReleaseOwner(); } finally { Volatile.Write(ref releaseOwner,null);gate.Release(); } }); }
        return Snapshot();
    }
    /// <summary>
    /// Gathers the destination's stray stacks of every consolidating line into the line's rank-1
    /// retainer, while the destination is still at its bell after its last deposit. The placement
    /// port runs it under this run's lease and authority. A consolidation that stops short with
    /// nothing unverified is recorded and delivery carries on; an unverified one, or one that ran
    /// past its deadline, faults the run with the step pending, so a resume runs it again.
    /// </summary>
    private async Task ConsolidateAsync(CourierJournal r, RunIntent intent, OwnerScope destination, OwnerScope owner, IDisposable lease)
    {
        var lines = r.Plan.Consolidations.Where(c => CourierPlanner.Exact(c.Destination, destination)).ToImmutableArray();
        if (lines.IsEmpty) return;
        var prefix = $"{destination.CharacterName}: ";
        r.ConsolidationLimits.RemoveAll(limit => limit.StartsWith(prefix, StringComparison.Ordinal));
        try
        {
            await calls.Call("DestinationConsolidation", 900, d => placement.ConsolidateAsync(
                new() { Id = r.Plan.PlanId, Revision = r.Plan.Revision, Owner = owner }, lines, lease,
                () => !stop && automation.Owns(lease), d, id => { r.Pending = intent with { PlacementOperation = id }; store.Save(r); }));
        }
        catch (CourierFault fault) when (fault.Outcome == CourierOutcome.Failed)
        {
            // Failed means every leg settled: nothing is in transit, so the step is not pending.
            r.Pending = null; store.Save(r);
            if (stop || unloaded) throw new CourierFault(CourierOutcome.Cancelled, "StopRequested", $"Stopped during consolidation: {fault.Message}");
            r.ConsolidationLimits.Add(prefix + $"{fault.Code}: {fault.Message}");
            warning($"{r.Run}/{intent.Id} ConsolidationLimited {destination.CharacterName} {fault.Code}: {fault.Message}");
        }
    }
    /// <summary>
    /// Settles an interrupted Retrieve or Deposit from its journaled operations and the owner's fresh
    /// bags, never by replaying it. Every journaled operation must be settled (never indeterminate),
    /// and for every item and quality the journal's verified transfers must equal the bag delta, with
    /// nothing unrelated changed. Verified units are credited; only the unmoved remainder stays scheduled.
    /// </summary>
    private void SettlePlacement(CourierJournal r, RunIntent move, InventoryView owner)
    {
        var batch=move.Action.Batch; bool deposit=move.Action.Phase==CourierPhase.Deposit; int sign=deposit?-1:1;
        CourierFault Mismatch(string detail)=>Fault("PlacementSettlementMismatch",$"Bags and placement journal must agree: {detail}");
        var tallies=new List<ImmutableArray<PlacementTally>>();
        var journaled=new Dictionary<(uint Item,bool Hq),long>();
        foreach(var op in move.Operations)
        {
            var state=placement.OperationStatus(op,owner.Character);
            // Accepted never started; every other settled status carries only verified transfers.
            if(state is null || state==OperationStatuses.Indeterminate || state!=OperationStatuses.Accepted && !OperationStatuses.IsTerminal(state) ||
                placement.JournalUnits(op,owner.Character) is not {} units)
                throw Mismatch($"operation {op} is {state ?? "unknown"}.");
            tallies.Add(units);
            foreach(var t in units)journaled[(t.Item,t.Hq)]=journaled.GetValueOrDefault((t.Item,t.Hq))+t.Units;
        }
        bool Moves(Shipment s)=>deposit ? s.To!=null : s.From!=null;
        foreach(var (item,hq) in move.OwnerBefore.Stock.Concat(owner.Stock).Select(x=>(x.ItemId,x.Hq))
            .Concat(batch.Select(s=>(s.Item,s.Hq))).Concat(journaled.Keys).Distinct())
        {
            long seen=sign*((long)owner.Count(item,hq)-move.OwnerBefore.Count(item,hq));
            long movable=batch.Where(s=>Moves(s) && s.Item==item && s.Hq==hq).Sum(s=>(long)s.Units);
            long recorded=journaled.GetValueOrDefault((item,hq));
            if(seen<0 || seen>movable || seen!=recorded)
                throw Mismatch($"item {item}{(hq?" HQ":"")}: bags moved {seen}, journal {recorded}, scheduled {movable}.");
        }
        var moved=new List<Shipment>(); var rest=new List<Shipment>();
        void Allocate(IEnumerable<Shipment> shipments,long units)
        {
            foreach(var s in shipments)
            {
                var n=(int)Math.Min(s.Units,units); units-=n;
                if(n>0)moved.Add(s with {Units=n});
                if(n<s.Units)rest.Add(s with {Units=s.Units-n});
            }
            if(units!=0)throw Mismatch("journaled units exceed the shipments they served.");
        }
        if(deposit)
        {
            // One deposit operation; its journal lines follow CourierPlacementBatch.Deposits order.
            var routes=CourierPlacementBatch.Deposits(batch);
            var lines=tallies.Count switch { 0 => ImmutableArray<PlacementTally>.Empty, 1 => tallies[0], _ => throw Mismatch("a deposit batch journals one operation.") };
            if(!lines.IsEmpty && lines.Length!=routes.Length)throw Mismatch("deposit journal lines differ from the batch routes.");
            for(int i=0;i<routes.Length;i++)
            {
                var route=routes[i];
                if(!lines.IsEmpty && (lines[i].Item!=route.Item || lines[i].Hq!=route.Hq || lines[i].Line is {} line && line!=route.Line))
                    throw Mismatch("deposit journal lines differ from the batch routes.");
                Allocate(route.Shipments,lines.IsEmpty?0:lines[i].Units);
            }
        }
        else
        {
            // One retrieval operation per source retainer, journaled in CourierPlacementBatch.Retrievals order.
            var sources=CourierPlacementBatch.Retrievals(batch);
            if(tallies.Count>sources.Length)throw Mismatch("more retrieval operations than source retainers.");
            for(int i=0;i<sources.Length;i++)
            {
                var units=i<tallies.Count?tallies[i]:ImmutableArray<PlacementTally>.Empty;
                var keys=sources[i].Shipments.Select(s=>(s.Item,s.Hq)).ToHashSet();
                if(units.Any(t=>t.Units>0 && !keys.Contains((t.Item,t.Hq))))throw Mismatch("a retrieval journaled an item its source did not supply.");
                foreach(var group in sources[i].Shipments.GroupBy(s=>(s.Item,s.Hq)))
                    Allocate(group,units.Where(t=>t.Item==group.Key.Item && t.Hq==group.Key.Hq).Sum(t=>(long)t.Units));
            }
        }
        if(moved.Count==0)return; // Nothing moved: the step runs again as scheduled.
        // Bag-only shipments need no retainer: a deposit that moved anything has settled them too.
        if(deposit)foreach(var s in moved.Concat(batch.Where(s=>!Moves(s))))
            r.Delivered[s.Line]=checked(r.Delivered.GetValueOrDefault(s.Line)+s.Units);
        if(rest.Count==0)r.Cursor++;
        else r.Schedule=r.Schedule.SetItem(r.Cursor,move.Action with {Batch=[..rest]});
    }
    /// <summary>
    /// The Deposit steps right after the hand-in at <paramref name="cursor"/> that together stow exactly
    /// its batch: one batched step, or a pre-batching schedule's one step per stack.
    /// </summary>
    private static int DepositsFor(ImmutableArray<CourierStep> steps, int cursor, ImmutableArray<Shipment> batch)
    {
        var stowed=new List<Shipment>(); int count=0;
        foreach(var step in steps.Skip(cursor+1))
        {
            if(step.Phase!=CourierPhase.Deposit || stowed.Count>=batch.Length)break;
            stowed.AddRange(step.Batch); count++;
        }
        if(!stowed.SequenceEqual(batch))throw Fault("ScheduleShapeMismatch","The hand-in's deposits do not match its batch.");
        return count;
    }
    // Carried gil is left out: teleport and travel fees spend it outside any trade. Every trade still
    // verifies its gil delta exactly (Custody.Matches and TrySlice always include the gil key).
    private static bool Agrees(InventoryView? checkpoint, InventoryView fresh, CourierJournal r) =>
        checkpoint != null && CourierPlanner.Exact(checkpoint.Character, fresh.Character) &&
        r.Plan.Trips.SelectMany(t => t.Manifest).Where(s => !s.IsGil).All(s => checkpoint.Count(s.Item, s.Hq) == fresh.Count(s.Item, s.Hq));
    private async Task Meet(MeetingBell bell)
    {
        if ((await calls.Call("OwnerPosition", 15, bells.PositionAsync)).World != bell.World)
            await calls.Call("OwnerWorld", Deadline.WorldSeconds(bell), d => worlds.EnsureWorldAsync(bell.World, d));
        await calls.Call(bell.Ward.HasValue ? "WardTravel" : bell.IsPlaza ? "OwnerPlaza" : "OwnerBell", bell.ApproachSeconds, d => bells.GoInRangeAsync(bell, d));
    }
    private async Task Fence(CourierJournal r,RunIntent intent,InventoryView owner)
    {
        var local=new TradeEvidence(intent.OwnerBefore,owner);
        r.Evidence.Add(new(intent.Id,local,null,false));store.Save(r);
        var receipt=await Send(r,CourierCommand.NotDelivered,owner.Character,intent.Action.Batch,confirm:intent.Id,evidence:local,carrierBaseline:intent.CarrierBefore,
            cancelled:reply=>{if(reply.Bags is {} fresh){r.Evidence[^1]=new(intent.Id,local,new(intent.CarrierBefore,fresh),false);store.Save(r);}});
        var remote=receipt.Bags==null?null:new TradeEvidence(intent.CarrierBefore,receipt.Bags);
        r.Evidence[^1]=new(intent.Id,local,remote,false);store.Save(r);
        if(!Custody.Matches(local,intent.OwnerBefore.Character,intent.Action.Batch,0) ||
            !Custody.Matches(remote,r.Settings.Carrier!,intent.Action.Batch,0)) throw Fault("NotDeliveredDeltaMismatch","Both fresh baselines must agree.");
        r.Evidence[^1]=r.Evidence[^1] with {Disposition="NotDelivered"};
        r.OwnerCheckpoint=owner;r.CarrierCheckpoint=receipt.Bags;r.Pending=null;store.Save(r);
    }
    /// <summary>
    /// Waits for the first of a paired owner/carrier operation. When it faults, the other side is told to stop
    /// (carrier Stop for a local fault, local pre-lock cancel for a remote one) and gets a bounded drain. Returns
    /// the task to await for the outcome: both tasks once drained, otherwise the first fault.
    /// </summary>
    private async Task<Task> FirstFaultCancelsOther(CourierJournal r, Task local, Task? remote, string name)
    {
        if (remote == null) return local;
        var both = Task.WhenAll(local, remote);
        var first = await Task.WhenAny(local, remote);
        if (!(first.IsFaulted || first.IsCanceled)) return both;
        if (first == local)
        {
            // The carrier cancels its pre-lock on receipt, but replies only after its active step drains,
            // so the Stop is not awaited here; the bounded drain below covers the counterpart.
            if (!Carrierless(r))
                try
                {
                    _ = Send(r, CourierCommand.Stop, r.Start, []).ContinueWith(x => warning($"{name} carrier stop: {x.Exception?.GetBaseException().Message}"),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception e) { warning($"{name} carrier stop: {e.Message}"); }
        }
        else
        {
            // Signal CanContinue=false so an in-flight local travel/navigation/switch stops promptly,
            // and also cancel any pre-lock local trade.
            stop = true;
            try { await calls.Call("LocalCancelPreLock", 10, trade.CancelPreLockAsync); }
            catch (Exception e) { warning($"{name} local cancel: {e.Message}"); }
        }
        if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30))) == both) return both;
        warning($"{name}DrainDeadline: counterpart still running 30s after the first fault.");
        // Retain automation lease and gate ownership until any still-running local counterpart settles.
        if (!local.IsCompleted) calls.Track(local);
        // Observed so the abandoned counterpart never raises an unobserved exception.
        _ = both.ContinueWith(static x => _ = x.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return first;
    }
    private async Task HandOffAsync(CourierJournal r,RunIntent intent)
    {
        var step=intent.Action;var before=intent.OwnerBefore;
        var send = step.Phase == CourierPhase.HandOut;
        var batch = step.Batch;
        if (!send && !Custody.Covers(r.Custody, batch)) throw new CourierFault(CourierOutcome.Failed,"CustodyInsufficient", "HandIn exceeds custody");
        await calls.Call("OwnerPartner", 30, d => bells.ObserveTradeableAsync(r.Settings.Carrier!, d));
        if (stop) throw new CourierFault(CourierOutcome.Cancelled, "StopBeforeTrade", "No trade dispatched.");
        var sender = send ? before : intent.CarrierBefore;
        if (batch.GroupBy(s => (s.Item, s.Hq)).Any(g => sender.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => s.Units)))
            throw new CourierFault(CourierOutcome.Failed,"SenderStockShort", "Fresh sender inventory does not cover the manifest.");
        var id = intent.Id;
        CourierReceipt? cancellationReceipt = null;
        try { (trade as ICourierHoldPort)?.CheckHold(); }
        catch (CourierFault) { r.Pending=null; store.Save(r); throw; } // Neither trade has been dispatched.
        var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(CourierProgress p){if(p.Run==r.Run && p.Step==id && p.Phase=="TradeStarted")ack.TrySetResult();}
        peer.Progress+=Started;
        Task<CourierReceipt> remote;
        try {
            if(stop)throw new CourierFault(CourierOutcome.Cancelled,"StopBeforeTrade","No trade dispatched.");
            r.Pending=intent with {LocalMayHaveStarted=false};store.Save(r);
            remote=Send(r,send ? CourierCommand.Receive : CourierCommand.Send,before.Character,batch,id:id,cancelled:receipt=>cancellationReceipt=receipt);
            try { await Task.WhenAny(remote,ack.Task).WaitAsync(TimeSpan.FromSeconds(60)); }
            catch(TimeoutException){throw Fault("CarrierTradeStartDeadline","CarrierTradeStart: 60s elapsed without matching acknowledgement.");}
            if(!ack.Task.IsCompleted) {
                try { await remote; throw Fault("TradeStartNotAcknowledged","Missing matching carrier acknowledgement."); }
                catch(CourierFault original){
                    try { await Fence(r,intent,await Observe()); }
                    catch(Exception e){warning($"NotDelivered settlement: {e.Message}");throw;}
                    throw new CourierFault(CourierOutcome.Failed,original.Code,original.Message);
                }
            }
            if(stop)throw Fault("StopBeforeLocalTrade","Carrier acknowledged; owner did not dispatch.");
            r.Pending=intent;store.Save(r);
        } finally {peer.Progress-=Started;}
        var local = calls.Call("OwnerTrade", Deadline.TradeSeconds(batch), d => send ? trade.SendAsync(r.Settings.Carrier!, batch, d) : trade.ReceiveAsync(r.Settings.Carrier!, batch, d));
        // A side that faults first cancels its counterpart so neither waits out its full trade deadline.
        var settled = await FirstFaultCancelsOther(r, local, remote, "HandOff");
        var fenced = false;
        try { await settled; }
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
        catch (CourierFault) when (remote.Exception?.GetBaseException() is CourierFault { Outcome: not CourierOutcome.Cancelled } carrierFault &&
            local.Exception?.GetBaseException() is CourierFault { Outcome: CourierOutcome.Cancelled })
        {
            // The carrier's trade failed rather than cancelled (e.g. its window closed before lock), and this side
            // cancelled pre-lock. Its failure receipt is not a settlement: the carrier still holds the step
            // unconfirmed. Fence it instead, so the carrier re-proves from its own trade record and fresh bags that
            // nothing moved; any disagreement leaves the step pending for settlement as before.
            var owner = await Observe();
            if (!Custody.Matches(new TradeEvidence(before, owner), before.Character, batch, 0) ||
                cancellationReceipt?.Evidence is { } reported && !Custody.Matches(reported, r.Settings.Carrier!, batch, 0))
                throw;
            try { await Fence(r, intent, owner); }
            catch (Exception e) { warning($"{r.Run}/{id} NotDelivered after carrier {carrierFault.Code}: {e.Message}"); throw; }
            fenced = true;
            throw new CourierFault(CourierOutcome.Cancelled, "CancelledPreLock",
                $"Carrier trade ended {carrierFault.Code} before lock; both clients' bags unchanged: {carrierFault.Message}");
        }
        finally
        {
            // A fenced step already journaled its NotDelivered evidence.
            if (!fenced)
            {
                var e = new HandOff(id, new(before, await Observe()), remote.IsCompletedSuccessfully ? remote.Result.Evidence : cancellationReceipt?.Evidence,
                    local.IsCompletedSuccessfully && remote.IsCompletedSuccessfully,r.Pending==null?"NotDelivered":null);
                r.Evidence.Add(e); store.Save(r);
            }
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
