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
    ReturnDestinationHome,
    // A trip whose source is the carrier itself: the carrier's own units enter the run's custody in place of a pickup leg.
    Claim
}
public sealed record CourierStep(CourierPhase Phase, int Trip, ImmutableArray<Shipment> Batch);
/// <param name="RetainerBefore">Legacy single-stack placement baseline; batched placement settles from the journal and bags.</param>
/// <param name="PlacementOperation">Consolidation's operation id (and a legacy single-stack placement's).</param>
/// <param name="PlacementOperations">A batched Retrieve/Deposit's journaled operations, in dispatch order
/// (retrieval: one per <see cref="CourierPlacementBatch.Retrievals"/> source, deposit: one).</param>
public sealed record RunIntent(Guid Id, CourierStep Action, InventoryView OwnerBefore, InventoryView CarrierBefore, bool LocalMayHaveStarted=true,int? RetainerBefore=null,string? PlacementOperation=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> PlacementOperations = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? At = null)
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
/// <summary>
/// A hand-in being settled from the carrier's own send receipt (<see cref="CourierCommand.SettleSent"/>), journaled before
/// it is sent: it is re-sent unchanged, under the same id, until settled. Owner is the owner's evidence it carries.
/// </summary>
public sealed record CourierOutsideSettlement(Guid HandIn, Guid Command, string Disposition, TradeEvidence Owner, ImmutableArray<string> Operations);
/// <summary>Hand-in units the owner had already placed when the hand-in settled: credited as delivered with no deposit.</summary>
public sealed record CourierPlacedOutside(Guid HandIn, string Disposition, ImmutableArray<Shipment> Units, ImmutableArray<string> Operations);
/// <summary>What the carrier's send receipt proves for a pending hand-in: the rows that left its bags.</summary>
public sealed record CourierSentProof(Guid Run, Guid Step, string From, string To, ImmutableArray<CustodyRow> Left, bool Complete, string? Refusal);
public sealed class CourierJournal
{
    public Guid Run { get; set; }
    public string? RunKey {get;set;}
    /// <summary>The IPC request that started this run (its idempotency key); null for runs started in the UI.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }
    /// <summary>The step id of an unclaim that was sent and not yet confirmed; the same id is sent again until it is.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? UnclaimStep { get; set; }
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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CourierOutsideSettlement? OutsideSettlement { get; set; }
    public List<CourierPlacedOutside> PlacedOutside { get; set; } = [];
}
public sealed record CourierCarrierMove(Guid Id, CourierCommand Command, OwnerScope Partner, MeetingBell? Bell);
/// <summary>A run an IPC request started, as the request index keeps it (and the current journal projects).</summary>
/// <param name="Untouched">The run provably moved nothing, so its request may run again.</param>
public sealed record CourierRequestRecord(Guid Run, string RequestId, string State, string Code, string Detail, string? Hash, bool Untouched, DateTimeOffset At)
{
    internal static CourierRequestRecord Of(CourierJournal r, DateTimeOffset at) => new(r.Run, r.RequestId ?? "", r.State, r.Code,
        r.Detail.Length > 300 ? r.Detail[..300] : r.Detail, r.Plan.RequestHash, CourierConductor.Untouched(r), at);
}
/// <summary>
/// <c>courier-requests.json</c>: every closed run an IPC request started, so a resubmitted request id is answered without
/// reading the closed journals. Files lists the closed journals already indexed.
/// </summary>
public sealed class CourierRequestIndex
{
    public List<CourierRequestRecord> Runs { get; set; } = [];
    public HashSet<string> Files { get; set; } = [];
}
public sealed record LineProgress(Guid Line, string Name, int Moved, int Remaining, Shipment? Route = null, bool Nq = false, bool Hq = false);
public sealed record CourierSnapshot(Guid Run, string State, string OwnerPhase, string CarrierPhase, int Trip, int Trips,
    string Code, string Detail, ImmutableArray<LineProgress> Lines, ImmutableArray<Refusal> Refusals,bool CanResume,bool CanClear,ImmutableArray<CustodyRow> Custody,
    CourierUnverified? Unverified=null, Guid PlanId=default, string? RequestId=null, bool CanSettleDelivered=false, string? ClearBlocked=null);
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
        store = new(Path.Combine(directory, "courier-run.json"), warning);
        this.information = information;
        this.needsSuppression=needsSuppression??(()=>true);
        this.directory = directory;
        requests = new(Path.Combine(directory, RequestIndexName), warning);
        var r = store.Load();
        if (r.State == "Running") { r.State = "Interrupted"; store.Save(r); }
        // Built off the calling (framework) thread; only a first load after an upgrade has closed journals to read.
        history = Task.Run(LoadHistory);
    }
    private const string RequestIndexName = "courier-requests.json";
    private readonly CourierStore<CourierRequestIndex> requests;
    private readonly Task<CourierRequestIndex> history;
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
            if (FromCarrier(r, trip))
            {
                // The carrier is the source: no pickup leg. Once both sides are at the drop-off, its own units are
                // claimed into custody (against its post-travel bags) and delivered exactly as any carried custody is.
                yield return new(CourierPhase.SwitchToDestination, i, []);
                yield return new(CourierPhase.CarrierToDestination, i, []);
                yield return new(CourierPhase.Claim, i, trip.Manifest);
                yield return new(CourierPhase.HandIn, i, trip.Manifest);
            }
            else
            {
                yield return new(CourierPhase.EnsureSource, i, []);
                yield return new(CourierPhase.Retrieve, i, trip.Manifest);
            }
            if (!trip.Local && !FromCarrier(r, trip))
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
    /// <summary>Whether a trip's source is the run's carrier: it delivers stock the carrier already holds.</summary>
    internal static bool FromCarrier(CourierJournal r, CourierTrip t) => !t.Local && CourierPlanner.Exact(t.Source, r.Settings.Carrier);
    private static bool SameCustody(ImmutableArray<Shipment> a, ImmutableArray<Shipment> b) => Custody.Covers(a, b) && Custody.Covers(b, a);
    private static ImmutableArray<Shipment> Held(CourierReceipt receipt) => receipt.Custody.IsDefault ? [] : receipt.Custody;
    /// <summary>
    /// Whether the run's custody can only be stock its carrier claimed that no hand-in reached: every carried trip is
    /// sourced by the carrier, nothing is pending but a claim, and every hand-in on record proved nothing moved.
    /// </summary>
    private static bool ClaimOnly(CourierJournal r) =>
        r.Plan.Trips.Any(t => FromCarrier(r, t)) && r.Plan.Trips.All(t => t.Local || FromCarrier(r, t)) &&
        r.Pending is null or { Action.Phase: CourierPhase.Claim } && r.Evidence.All(e => e.Disposition == "NotDelivered");
    /// <summary>
    /// Whether a run provably moved nothing: nothing pending, held or delivered, and every hand-in on record proved
    /// nothing moved. Its request may run again under the same id.
    /// </summary>
    internal static bool Untouched(CourierJournal r) => r.Pending == null && r.Custody.IsEmpty &&
        r.Delivered.Values.All(units => units == 0) && r.Evidence.All(e => e.Disposition == "NotDelivered");
    /// <summary>The carrier trip nearest before or after trip <paramref name="i"/>, skipping local trips; null when none.</summary>
    private static CourierTrip? Carried(CourierJournal r, int i, int step)
    {
        for (i += step; i >= 0 && i < r.Plan.Trips.Length; i += step)
            if (!r.Plan.Trips[i].Local)
                return r.Plan.Trips[i];
        return null;
    }
    /// <summary>Whether a phase runs inside a trip (before the closing returns), with the owner at a bell.</summary>
    private static bool InTrip(CourierPhase phase) => phase < CourierPhase.ReturnOwner || phase is CourierPhase.Consolidate or CourierPhase.Claim;
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
            // A resume that already proved it cannot settle the hand-in would only fail again; the settle action replaces it.
            connected && attempt && r.State is "Failed" or "Interrupted" or "Indeterminate" && r.Code != HandInNeedsSettlement,
            (connected || r.Custody.IsEmpty) && !HasPendingTrade(r) && r.State is "Completed" or "Failed" or "Interrupted" or "Indeterminate" or "Refused",Rows(r.Custody,r.Settings.Carrier),
            Unverified(r),r.Plan.PlanId,r.RequestId,
            connected && !carrierless && r.Pending is {Action.Phase:CourierPhase.HandIn} && r.State is "Failed" or "Interrupted" or "Indeterminate",
            r.State is not ("Failed" or "Interrupted" or "Indeterminate") ? null : HasPendingTrade(r) ? "HandOffUnverified" :
                !connected && !r.Custody.IsEmpty ? "CarrierNotConnected" : null);
    }
    /// <summary>A resume found a hand-in the carrier holds unconfirmed that neither fresh bags nor the owner's journal settle.</summary>
    internal const string HandInNeedsSettlement = "HandInNeedsSettlement";
    private static CourierUnverified? Unverified(CourierJournal r)
    {
        if(r.Pending is not {} p || p.Action.Batch.IsEmpty || r.State is not ("Failed" or "Interrupted" or "Indeterminate")) return null;
        var owner=p.OwnerBefore.Character; var carrier=r.Settings.Carrier;
        var (from,to)=p.Action.Phase==CourierPhase.HandIn?(carrier,owner):p.Action.Phase==CourierPhase.HandOut?(owner,carrier):
            p.Action.Phase==CourierPhase.Claim?(carrier,carrier):(owner,owner);
        return new(p.Action.Phase.ToString(),from?.CharacterName??"Unknown",to?.CharacterName??"Unknown",Rows(p.Action.Batch,from));
    }
    internal static ImmutableArray<CustodyRow> Rows(IEnumerable<Shipment> units,OwnerScope? holder)=>
        [..units.GroupBy(s=>(s.Item,s.Hq)).Select(g=>new CustodyRow(g.First().Name,g.First().IsGil?"":g.Key.Hq?"HQ":"NQ",g.Sum(s=>s.Units),holder?.CharacterName??"Unknown"))];
    /// <summary>The full journal, for the IPC status projection (custody split into stacks and gil).</summary>
    internal CourierJournal Journal() => store.Load();
    private readonly object polledGate = new();
    private (DateTime Stamp, long Length, CourierJournal Journal)? polled;
    /// <summary>
    /// The current run's snapshot for a once-a-second poller: the journal is parsed again only when its file's write
    /// time or length changed since the last parse (one file stat otherwise). The projection itself is in memory.
    /// </summary>
    internal CourierSnapshot PolledSnapshot()
    {
        var file = new FileInfo(Path.Combine(directory, "courier-run.json"));
        var (stamp, length) = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);
        CourierJournal journal;
        lock (polledGate)
        {
            if (polled is not { } cached || cached.Stamp != stamp || cached.Length != length)
                polled = cached = (stamp, length, store.Load());
            journal = cached.Journal;
        }
        return Project(journal);
    }
    /// <summary>A closed run's journal as it was archived, or null when there is no archive of it. Reads a file.</summary>
    internal CourierJournal? Closed(Guid run)
    {
        lock (archiveGate)
        {
            var path = ArchiveFile(run);
            return File.Exists(path) ? new CourierStore<CourierJournal>(path).Load() : null;
        }
    }

    private const string ArchivePattern = "courier-closed-*.json";
    private string ArchiveFile(Guid run) => Path.Combine(directory, $"courier-closed-{run:N}.json");
    private IEnumerable<string> Archives() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, ArchivePattern).Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
        : [];
    private readonly object archiveGate = new();
    /// <summary>
    /// Keeps a finished journal beside the cleared ones (replacing an earlier archive of the same run only when asked),
    /// then records it in the request index. The archive is written first: one the index missed is indexed on the next load.
    /// </summary>
    private async Task ArchiveAsync(CourierJournal r, bool replace)
    {
        var index = await history.ConfigureAwait(false);
        lock (archiveGate)
        {
            var path = ArchiveFile(r.Run);
            if (!replace && File.Exists(path)) return;
            new CourierStore<CourierJournal>(path, warning).Save(r);
            index.Runs.RemoveAll(x => x.Run == r.Run);
            if (r.RequestId is { Length: > 0 }) index.Runs.Add(CourierRequestRecord.Of(r, DateTimeOffset.UtcNow));
            index.Files.Add(Path.GetFileName(path));
            Prune(index);
            requests.Save(index);
        }
    }
    /// <summary>
    /// Every persisted run the IPC request <paramref name="requestId"/> started: the current journal first, then the
    /// request index. Null while the index is still being built (only on the first load after an upgrade).
    /// </summary>
    internal IReadOnlyList<CourierRequestRecord>? RunsFor(string requestId)
    {
        if (!history.IsCompleted) return null;
        var found = new List<CourierRequestRecord>();
        var current = store.Load();
        if (current.Run != Guid.Empty && current.RequestId == requestId) found.Add(CourierRequestRecord.Of(current, DateTimeOffset.UtcNow));
        lock (archiveGate)
            found.AddRange(history.Result.Runs.Where(x => x.RequestId == requestId && x.Run != current.Run));
        return found;
    }
    /// <summary>
    /// Loads the request index, indexing any closed journal it does not list yet (all of them when the index is new). An
    /// unreadable file is moved aside with a warning and the history goes on without it. Never throws.
    /// </summary>
    private CourierRequestIndex LoadHistory()
    {
        CourierRequestIndex index;
        try { index = requests.Load(); }
        catch (Exception e) { Quarantine(Path.Combine(directory, RequestIndexName), e); index = new(); }
        try
        {
            var changed = false;
            foreach (var path in Archives().ToArray())
            {
                var name = Path.GetFileName(path);
                if (index.Files.Contains(name)) continue;
                CourierJournal archived;
                try { archived = new CourierStore<CourierJournal>(path).Load(); }
                catch (Exception e) { Quarantine(path, e); continue; }
                index.Files.Add(name); changed = true;
                if (archived.RequestId is { Length: > 0 } && archived.Run != Guid.Empty && index.Runs.All(x => x.Run != archived.Run))
                    index.Runs.Add(CourierRequestRecord.Of(archived, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
            }
            lock (archiveGate)
            {
                changed |= Prune(index);
                if (changed) requests.Save(index);
            }
        }
        catch (Exception e) { warning($"CourierHistoryIndexIncomplete: {e.GetType().Name}: {e.Message}"); }
        return index;
    }
    /// <summary>Moves an unreadable history file aside (never deletes it) and warns; the history goes on without it.</summary>
    private void Quarantine(string path, Exception e)
    {
        var target = path + ".unreadable";
        try
        {
            if (File.Exists(target)) target = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}.unreadable";
            File.Move(path, target);
            warning($"CourierHistoryQuarantined: {Path.GetFileName(path)} is unreadable ({e.Message}); moved aside as {Path.GetFileName(target)}. " +
                "A request id only it recorded is no longer recognised as a duplicate.");
        }
        catch (Exception move)
        {
            warning($"CourierHistoryQuarantineFailed: {Path.GetFileName(path)} is unreadable ({e.Message}) and could not be moved aside ({move.Message}); it is skipped.");
        }
    }
    /// <summary>Forgets closed journals that are gone; request records are kept for good. True when anything changed.</summary>
    private bool Prune(CourierRequestIndex index)
    {
        var present = Archives().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = index.Files.RemoveWhere(name => !present.Contains(name)) > 0;
        return changed;
    }
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
            // A carrier-sourced run's unhanded claim goes back to the carrier's own stock without an acknowledgement.
            if(r.State is not ("Completed" or "Refused") && (!r.Custody.IsEmpty || r.Pending is {Action.Phase:CourierPhase.Claim}))
                await ReleaseClaimAsync(r);
            // Completed includes a durable empty-ledger Finish acknowledgement. A run refused after its carrier joined it
            // still closes the carrier's side.
            CourierReceipt? receipt=null;
            if (r.State != "Completed" && (r.State != "Refused" || r.PeerClaimed) && r.Start.HasStableIdentity && !Carrierless(r))
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
            await ArchiveAsync(r,replace:true);
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
        // A carrier-sourced run that cannot claim what it must deliver ends Refused: nothing held, not resumable.
        bool refusedRun = false;
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
                // A finished run a new one replaces is kept with the cleared ones, so its request id stays answerable.
                if (r.Run != Guid.Empty) await ArchiveAsync(r, replace: false);
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
                // A hand-in the owner's fresh bags no longer show because Quartermaster placed the goods since: settled when
                // its journaled placements account for the bags exactly, or when an outside settlement is already under way.
                if (r.Pending is {Action.Phase: CourierPhase.HandIn} placedHandIn && !carrierless)
                {
                    if (r.OutsideSettlement?.HandIn != placedHandIn.Id && status.Unconfirmed == placedHandIn.Id &&
                        DeliveredThenPlaced(r, placedHandIn, owner, carrier) is { } placedSince)
                        r.OutsideSettlement = new(placedHandIn.Id, Guid.NewGuid(), DeliveredThenPlacedDisposition, new(placedHandIn.OwnerBefore, owner), placedSince);
                    if (r.OutsideSettlement?.HandIn == placedHandIn.Id && (status.Unconfirmed == placedHandIn.Id || status.LastConfirmed == placedHandIn.Id))
                        (status, carrier) = await SettleOutsideAsync(r, placedHandIn, owner, carrier);
                }
                if (r.Pending is {Action.Phase: CourierPhase.HandOut or CourierPhase.HandIn} uncertain && trade is ICourierSettlement recovery &&
                    (status.Unconfirmed==uncertain.Id || status.LastConfirmed==uncertain.Id))
                {
                    int sign=uncertain.Action.Phase==CourierPhase.HandOut ? -1 : 1;
                    var local=new TradeEvidence(uncertain.OwnerBefore,owner);
                    var remote=new TradeEvidence(uncertain.CarrierBefore,carrier);
                    if(Custody.TrySlice(local,uncertain.OwnerBefore.Character,remote,r.Settings.Carrier!,uncertain.Action.Batch,sign,out var moved,out var rest))
                    {
                        if(moved.IsEmpty)throw sign==1 && status.Unconfirmed==uncertain.Id
                            ? Fault(HandInNeedsSettlement,"The owner's bags do not show the hand-in; settle it as delivered from the carrier's send receipt.")
                            : Fault("TradeClosureUnproven","Unchanged bags alone cannot settle a dispatched trade.");
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
                    else throw sign==1 && status.Unconfirmed==uncertain.Id
                        ? Fault(HandInNeedsSettlement,"The owner's bags no longer show the hand-in; settle it as delivered from the carrier's send receipt.")
                        : Fault("ResumeUnsettled","Fresh owner and carrier deltas do not agree with the pending manifest.");
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
                // An unstarted claim moves nothing and re-checks the carrier's fresh bags itself; a released claim resumes
                // here as its hand-in would have.
                var unstartedTrade=r.Pending==null && r.Schedule.Skip(r.Cursor).FirstOrDefault()?.Phase is CourierPhase.HandIn or CourierPhase.HandOut or CourierPhase.Claim;
                // A pending claim wrote at most the carrier's ledger, never its bags: the loop re-sends it under its
                // id and the carrier answers with its recorded receipt or runs it once. Its ledger may hold the claim.
                var claiming=r.Pending is {Action.Phase:CourierPhase.Claim} claim ? claim : null;
                var custodyAgrees=SameCustody(status.Custody,r.Custody) ||
                    claiming!=null && SameCustody(status.Custody,Custody.Apply(r.Custody,claiming.Action.Batch,1));
                if (r.Pending != null && !travel && claiming==null || status.Unconfirmed != null || !custodyAgrees
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
                // A claim takes only units the carrier's fresh bags hold beyond the custody it already carries; a
                // shortfall fails before anything is journaled. Bag counts never change in a claim, so a re-sent
                // claim whose ledger entry already landed passes the same check.
                // The claim follows the carrier's travel, so its fees are already spent: a carrier left short refuses
                // the run here, holding nothing, rather than claiming what a hand-in could never deliver.
                if (s.Phase == CourierPhase.Claim && Custody.Apply(r.Custody, s.Batch, 1).GroupBy(x => (x.Item, x.Hq))
                        .FirstOrDefault(g => carrier.Count(g.Key.Item, g.Key.Hq) < g.Sum(x => (long)x.Units)) is { } unheld)
                {
                    refusedRun = r.Pending == null && r.Custody.IsEmpty && Held(status).IsEmpty && status.Unconfirmed == null;
                    throw new CourierFault(CourierOutcome.Failed, unheld.Key.Item == CourierPlanner.GilItemId ? "CarrierGilShort" : "CarrierStockShort",
                        $"{unheld.First().Name}: {carrier.Character.CharacterName} holds {carrier.Count(unheld.Key.Item, unheld.Key.Hq):N0}, the run needs {unheld.Sum(x => (long)x.Units):N0}.");
                }
                // A re-sent claim (resumed with its intent pending) may already be in the carrier's ledger.
                var claimLanded = s.Phase == CourierPhase.Claim && r.Pending?.Action.Phase == CourierPhase.Claim &&
                    SameCustody(status.Custody, Custody.Apply(r.Custody, s.Batch, 1));
                // No five-slot pre-split: one trade step runs every window back to back (the trade runner
                // packs fragmented bag slots into successive windows and credits each before the next).
                if (!carrierless && !CourierPlanner.Exact(carrier.Character, r.Settings.Carrier) ||
                    !SameCustody(status.Custody, r.Custody) && !claimLanded)
                    throw Fault("CustodyMismatch", "Carrier identity or custody changed.");
                if (s.Phase is CourierPhase.Retrieve or CourierPhase.CarrierToSource or CourierPhase.HandOut &&
                    !CourierPlanner.Exact(owner.Character, t.Source) ||
                    s.Phase is CourierPhase.Deposit or CourierPhase.CarrierToDestination or CourierPhase.HandIn or CourierPhase.Consolidate &&
                    !CourierPlanner.Exact(owner.Character, t.Destination)) throw Fault("OwnerIdentityMismatch", owner.Character.CharacterName);
                if (stop) throw new CourierFault(CourierOutcome.Cancelled,"StopRequested","Stopped before dispatch.");
                // Placement needs no retainer baseline: a resume settles it from the journal and the bags.
                // At bounds the owner's own placements a resume may attribute to a hand-in (DeliveredThenPlaced).
                var intent=r.Pending ?? new(Guid.NewGuid(),s,owner,carrier,At:DateTimeOffset.UtcNow);
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
                        // An owner leg that faults stops the carrier instead of waiting out its travel deadline. A carrier
                        // that is itself the source was never at a pickup: it always travels (arriving at once if already there).
                        await await FirstFaultCancelsOther(r, OwnerToDestination(), !FromCarrier(r, t) && t.Pickup.SamePlace(t.Dropoff) ? null :
                            Send(r, CourierCommand.Travel, t.Destination, [], t.Dropoff,id:intent.Id), "SwitchToDestination"); break;
                    case CourierPhase.Claim:
                        // The carrier's own units enter this run's custody; nothing moves. A refused claim whose absence
                        // the carrier's ledger proves leaves nothing pending, so a resume claims anew; otherwise the
                        // intent stays and a resume re-sends it under the same id.
                        try { finished = await Send(r, CourierCommand.Claim, t.Destination, s.Batch, id: intent.Id); }
                        catch (CourierFault refused) when (refused.Outcome == CourierOutcome.Failed)
                        {
                            var unknown = refused.Code == "UnknownCommand";
                            try
                            {
                                if (!SameCustody(Held(await Send(r, CourierCommand.Status, r.Start, [])), r.Custody))
                                    warning($"{r.Run}/{intent.Id} ClaimRefusedLedgerChanged: {refused.Code}: the carrier's ledger no longer equals the run's custody.");
                                // A carrier without claims (one downgraded mid-run) never records its refusal: fencing the step
                                // with a bilateral not-delivered receipt settles its intent, so later commands are not refused.
                                else if (unknown) await Fence(r, intent, await Observe());
                                else { r.Pending = null; store.Save(r); }
                            }
                            catch (Exception e) { warning($"{r.Run}/{intent.Id} ClaimRefusalStatus: {e.Message}"); }
                            refusedRun = r.Pending == null && r.Custody.IsEmpty && (unknown || refused.Code is "CarrierGilShort" or "CarrierStockShort");
                            if (unknown)
                                throw new CourierFault(CourierOutcome.Failed, "CarrierSourceUnsupported",
                                    $"{r.Settings.Carrier?.CharacterName}'s Quartermaster cannot claim its own stock: {refused.Message}");
                            throw;
                        }
                        var claimed = Custody.Apply(r.Custody, s.Batch, 1);
                        if (!SameCustody(Held(finished), claimed))
                            throw Fault("ClaimLedgerMismatch", "The carrier's ledger after the claim differs from the run's custody plus the claim.");
                        r.Custody = claimed;
                        break;
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
            // Plugin unload cancels every pending game poll; name it so the journal reads as resumable, not as a crash.
            r.Code = (e as CourierFault)?.Code ?? (unloaded && e is OperationCanceledException ? "OwnerUnloaded" : "UnexpectedFailure"); r.Detail = e.Message;
            string Settled() => r.Pending != null ? "Indeterminate" :
                e is CourierFault { Outcome: CourierOutcome.Cancelled } ? "Interrupted" : "Failed";
            r.State = refusedRun && r.Pending == null && r.Custody.IsEmpty ? "Refused" : Settled();
            warning($"{r.Run}/{r.Pending?.Id} {r.State} {r.Code}: {e.Message}");
            if(!existingRun && !beginAttempted)return Project(r) with {CanResume=false,CanClear=false};
            store.Save(r);
            // An unloading owner has already disposed its peer link; the carrier pauses (and releases its locks) on link loss.
            if (!carrierless && (r.PeerClaimed || beginAttempted))
            {
                if (unloaded) warning($"{r.Run} CarrierStopUnsent: owner unloading; the carrier pauses when the link drops.");
                else try { await Send(r, CourierCommand.Stop, r.Start, []); }
                    catch (Exception cleanup) { warning($"{r.Run} CarrierStopFailed: {cleanup.GetType().Name}: {cleanup.Message}"); }
            }
            // Stop reaches the carrier ahead of its command queue and cancels a background move; the move stays
            // journaled so a resume re-sends it under the same id. Its fault is observed at dispatch.
            if (Interlocked.Exchange(ref carrierMove, null) is { } abandoned &&
                await Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromSeconds(15))) != abandoned)
                warning($"{r.Run} CarrierMoveStopDeadline: 15s elapsed after Stop.");
            // A stopped or failed carrier-sourced run hands its unhanded claim back to the carrier's own stock, so nothing
            // is left held for acknowledgement; a resume claims again.
            if (!carrierless && !unloaded && (!r.Custody.IsEmpty || r.Pending is { Action.Phase: CourierPhase.Claim }) && await ReleaseClaimAsync(r) && r.State == "Indeterminate")
            {
                r.State = Settled(); store.Save(r);
            }
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
    /// <summary>
    /// Hands a stopped, failed or cleared carrier-sourced run's unhanded claim back to the carrier's own stock: both
    /// ledgers end empty and the run resumes from its claim. A pending claim first settles from the carrier's ledger
    /// (landed or not). Anything the carrier's ledger or bags leave ambiguous stays custody for acknowledgement.
    /// Never throws; true when the run holds nothing afterwards.
    /// </summary>
    private async Task<bool> ReleaseClaimAsync(CourierJournal r)
    {
        if (!ClaimOnly(r) || Carrierless(r)) return false;
        try
        {
            var status = await Send(r, CourierCommand.Status, r.Start, []);
            if (status.ActiveRun != r.Run || status.Unconfirmed != null)
            {
                warning($"{r.Run} ClaimReleaseSkipped: the carrier {(status.ActiveRun != r.Run ? "does not hold this run" : "has an unconfirmed hand-off")}.");
                return false;
            }
            var held = Held(status);
            if (r.Pending is { Action.Phase: CourierPhase.Claim } claim)
            {
                // A claim's ledger entry and its receipt land together: the ledger shows whether it did. One that did not
                // land is fenced, so the carrier never keeps an unsettled claim intent that would refuse the next claim.
                if (SameCustody(held, Custody.Apply(r.Custody, claim.Action.Batch, 1)))
                {
                    // Adopted as the run loop would have: past the claim, so a failed release below resumes at the hand-in.
                    r.Custody = held; r.Pending = null;
                    if (r.Schedule.Skip(r.Cursor).FirstOrDefault() is { Phase: CourierPhase.Claim }) r.Cursor++;
                    r.CarrierCheckpoint = status.Bags ?? r.CarrierCheckpoint;
                    store.Save(r);
                }
                else if (SameCustody(held, r.Custody)) await Fence(r, claim, await Observe());
                else
                {
                    warning($"{r.Run}/{claim.Id} ClaimReleaseSkipped: the carrier's ledger matches neither the run's custody nor it plus the pending claim.");
                    return false;
                }
            }
            if (!r.Custody.IsEmpty)
            {
                // An empty carrier ledger here is an earlier unclaim whose reply was lost: this custody is claim-only,
                // so nothing else could have emptied it.
                var receipt = status;
                if (!held.IsEmpty)
                {
                    if (!SameCustody(held, r.Custody))
                    {
                        warning($"{r.Run} ClaimReleaseSkipped: the carrier's ledger differs from the run's custody.");
                        return false;
                    }
                    // Journaled first: a carrier that stopped mid-unclaim only settles a replay of the same step.
                    if (r.UnclaimStep == null) { r.UnclaimStep = Guid.NewGuid(); store.Save(r); }
                    try { receipt = await Send(r, CourierCommand.Unclaim, r.Start, r.Custody, id: r.UnclaimStep); }
                    catch (CourierFault refused) when (refused.Code.StartsWith("Unclaim", StringComparison.Ordinal))
                    {
                        // The carrier answered and recorded its refusal under this step: the next attempt is a new step.
                        r.UnclaimStep = null; store.Save(r);
                        throw;
                    }
                    if (!Held(receipt).IsEmpty)
                    {
                        warning($"{r.Run} ClaimReleaseLedgerMismatch: the carrier's ledger is not empty after the unclaim.");
                        return false;
                    }
                }
                r.Custody = [];
                r.UnclaimStep = null;
                r.CarrierCheckpoint = receipt.Bags ?? r.CarrierCheckpoint;
            }
            // The run resumes from the released claim's trip: both sides travel to the drop-off again before claiming.
            if (r.Schedule.Skip(r.Cursor).FirstOrDefault() is not { Phase: CourierPhase.Claim })
                for (var i = Math.Min(r.Cursor, r.Schedule.Length) - 1; i >= 0; i--)
                    if (r.Schedule[i].Phase == CourierPhase.Claim) { r.Cursor = i; break; }
            if (r.Cursor >= 2 && r.Cursor < r.Schedule.Length && r.Schedule[r.Cursor] is { Phase: CourierPhase.Claim } released &&
                r.Schedule[r.Cursor - 2] is { Phase: CourierPhase.SwitchToDestination } travel && travel.Trip == released.Trip)
                r.Cursor -= 2;
            store.Save(r);
            information?.Invoke($"{r.Run} ClaimReleased: the carrier's claimed stock is its own again.");
            return true;
        }
        catch (Exception e)
        {
            warning($"{r.Run} ClaimReleaseFailed: {(e as CourierFault)?.Code ?? e.GetType().Name}: {e.Message}");
            return false;
        }
    }
    internal const string DeliveredThenPlacedDisposition = "DeliveredThenPlaced", OperatorAttestedDisposition = "OperatorAttested";
    /// <summary>
    /// The DeliveredThenPlaced rule. A pending hand-in whose carrier reports its send finished settles when every
    /// Quartermaster transfer journaled for the owner since the hand-in started is terminal and verified, and the owner's
    /// fresh bags minus those transfers' net change (deposits out, retrievals in) differ from the bags before the hand-in
    /// by exactly the batch, on every key including unrelated items and gil, while the carrier's bags show exactly the
    /// batch left. Returns the operations it relied on; null when the rule does not apply (never when it is ambiguous).
    /// </summary>
    private ImmutableArray<string>? DeliveredThenPlaced(CourierJournal r, RunIntent u, InventoryView owner, InventoryView carrier)
    {
        if (u.At is not { } at || placement.PlacedSince(owner.Character, at) is not { Units.IsEmpty: false } since) return null;
        if (since.Unusable is { } why)
        {
            warning($"{r.Run}/{u.Id} DeliveredThenPlacedUnproven: {why}");
            return null;
        }
        var counts = owner.Stock.GroupBy(x => (x.ItemId, x.Hq)).ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Units));
        foreach (var moved in since.Units)
            counts[(moved.Item, moved.Hq)] = counts.GetValueOrDefault((moved.Item, moved.Hq)) - moved.Units;
        foreach (var (key, units) in counts)
            if (units < 0 || units > int.MaxValue)
            {
                warning($"{r.Run}/{u.Id} DeliveredThenPlacedUnproven: item {key.ItemId}{(key.Hq ? " HQ" : "")} nets {units:N0} after the journaled transfers.");
                return null;
            }
        var adjusted = owner with { Stock = [.. counts.Where(x => x.Value > 0).Select(x => new BagStock(x.Key.ItemId, x.Key.Hq, (int)x.Value))] };
        if (!Custody.TrySlice(new(u.OwnerBefore, adjusted), u.OwnerBefore.Character, new(u.CarrierBefore, carrier), r.Settings.Carrier!,
                u.Action.Batch, 1, out _, out var rest) || !rest.IsEmpty)
        {
            warning($"{r.Run}/{u.Id} DeliveredThenPlacedMismatch: the owner's bags net of {since.Operations.Length} journaled transfer(s) " +
                $"({string.Join(", ", since.Operations)}) and the carrier's bags do not show exactly the hand-in.");
            return null;
        }
        return since.Operations;
    }
    /// <summary>
    /// Settles a pending hand-in from the carrier's own send receipt: the carrier settles its ledger (SettleSent), the
    /// owner's receive checkpoint closes on the same receipt, the carrier's ledger is checked, and the hand-in's deposits
    /// shrink to what is still in the owner's bags (the rest is recorded as already placed). The caller sets
    /// <see cref="CourierJournal.OutsideSettlement"/>; it is journaled here before anything is sent.
    /// </summary>
    private async Task<(CourierReceipt Status, InventoryView Carrier)> SettleOutsideAsync(CourierJournal r, RunIntent u, InventoryView owner, InventoryView carrier)
    {
        var o = r.OutsideSettlement!;
        store.Save(r);
        if (trade is not ICourierReceiveAttestation attest) throw Fault("ReceiveAttestationUnavailable", "This client cannot close its receive checkpoint.");
        var batch = u.Action.Batch;
        CourierReceipt settled;
        try { settled = await Send(r, CourierCommand.SettleSent, u.OwnerBefore.Character, batch, id: o.Command, confirm: u.Id, evidence: o.Owner); }
        catch (CourierFault refused) when (refused.Outcome == CourierOutcome.Failed)
        {
            // The carrier recorded its refusal under this id; a later attempt is a new request.
            r.OutsideSettlement = null; store.Save(r);
            warning($"{r.Run}/{u.Id} SettleSentRefused {refused.Code}: {refused.Message}");
            throw;
        }
        if (settled.Evidence is not { } sent || !Custody.Matches(sent, r.Settings.Carrier!, batch, -1))
            throw Fault("SettleSentEvidenceMismatch", "The carrier's send receipt does not show exactly the hand-in left its bags.");
        await calls.Call("OwnerReceiveAttestation", 15, d => attest.AttestReceived(u.Id, r.Settings.Carrier!, sent, o.Disposition, d));
        var status = await Send(r, CourierCommand.Status, r.Start, []);
        var expected = Custody.Apply(r.Custody, batch, -1);
        if (status.LastConfirmed != u.Id || status.Unconfirmed != null || !SameCustody(Held(status), expected))
            throw Fault("SettlementLedgerMismatch", "The carrier has not acknowledged the same custody delta.");
        var placed = PlaceOutside(r, u, owner);
        r.Evidence.Add(new(u.Id, o.Owner, sent, false, o.Disposition));
        if (!placed.IsEmpty) r.PlacedOutside.Add(new(u.Id, o.Disposition, placed, o.Operations));
        r.Custody = expected; r.OwnerCheckpoint = owner; r.CarrierCheckpoint = status.Bags ?? carrier;
        r.Pending = null; r.OutsideSettlement = null; store.Save(r);
        warning($"{r.Run}/{u.Id} HandInSettled{o.Disposition}: carrier send receipt {o.Command}; " +
            (placed.IsEmpty ? "every unit is still in the owner's bags." :
                $"already placed: {string.Join(", ", placed.Select(x => $"{x.Name}{(x.Hq ? " HQ" : "")} x{x.Units:N0}"))}" +
                (o.Operations.IsEmpty ? "." : $" (operations {string.Join(", ", o.Operations)}).")));
        return (status, r.CarrierCheckpoint);
    }
    /// <summary>
    /// Shrinks a settled hand-in's deposits to the units its owner's bags still hold above the pre-hand-in count (in
    /// manifest order), credits the rest as delivered, and moves past the hand-in. Gil is never placed: it stays in the deposit.
    /// </summary>
    private static ImmutableArray<Shipment> PlaceOutside(CourierJournal r, RunIntent u, InventoryView owner)
    {
        var batch = u.Action.Batch;
        var left = batch.GroupBy(x => (x.Item, x.Hq)).ToDictionary(g => g.Key, g => g.First().IsGil ? long.MaxValue :
            Math.Clamp((long)owner.Count(g.Key.Item, g.Key.Hq) - u.OwnerBefore.Count(g.Key.Item, g.Key.Hq), 0, g.Sum(x => (long)x.Units)));
        var kept = new List<Shipment>(); var placed = new List<Shipment>();
        foreach (var x in batch)
        {
            var take = (int)Math.Min(x.Units, left[(x.Item, x.Hq)]);
            left[(x.Item, x.Hq)] -= take;
            if (take > 0) kept.Add(x with { Units = take });
            if (take < x.Units) placed.Add(x with { Units = x.Units - take });
        }
        if (placed.Count > 0)
        {
            var deposits = DepositsFor(r.Schedule, r.Cursor, batch);
            ImmutableArray<CourierStep> remaining = kept.Count == 0 ? [] : [new(CourierPhase.Deposit, u.Action.Trip, [.. kept])];
            r.Schedule = [.. r.Schedule.Take(r.Cursor + 1), .. remaining, .. r.Schedule.Skip(r.Cursor + 1 + deposits)];
            foreach (var x in placed) r.Delivered[x.Line] = checked(r.Delivered.GetValueOrDefault(x.Line) + x.Units);
        }
        r.Cursor++;
        return [.. placed];
    }
    private RunIntent PendingHandIn(CourierJournal r, Guid expectedRun)
    {
        if (r.Run != expectedRun || r.State is not ("Failed" or "Interrupted" or "Indeterminate"))
            throw new InvalidOperationException("RunNotSettleable");
        if (r.Pending is not { Action.Phase: CourierPhase.HandIn } u || Carrierless(r))
            throw new InvalidOperationException("NoPendingHandIn");
        return u;
    }
    /// <summary>What the carrier's send receipt proves for the run's pending hand-in. Moves nothing.</summary>
    public async Task<CourierSentProof> SentProofAsync(Guid expectedRun)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("CourierAlreadyRunning");
        try
        {
            var r = store.Load();
            var u = PendingHandIn(r, expectedRun);
            var status = await Send(r, CourierCommand.Status, r.Start, []);
            var from = r.Settings.Carrier?.CharacterName ?? "Unknown";
            var to = u.OwnerBefore.Character.CharacterName ?? "Unknown";
            var resuming = r.OutsideSettlement?.HandIn == u.Id && status.LastConfirmed == u.Id;
            string? refusal = status.ActiveRun != r.Run ? "CarrierRunConflict" :
                status.Unconfirmed != u.Id && !resuming ? "CarrierHandOffNotPending" :
                status.Evidence == null ? "CarrierSendUnproven" :
                !Custody.Matches(status.Evidence, r.Settings.Carrier!, u.Action.Batch, -1) ? "CarrierSendIncomplete" : null;
            ImmutableArray<CustodyRow> left = status.Evidence is { } e
                ? [.. u.Action.Batch.GroupBy(x => (x.Item, x.Hq)).Select(g => new CustodyRow(g.First().Name, g.First().IsGil ? "" : g.Key.Hq ? "HQ" : "NQ",
                    Math.Max(0, e.Before.Count(g.Key.Item, g.Key.Hq) - e.After.Count(g.Key.Item, g.Key.Hq)), from))]
                : [];
            return new(r.Run, u.Id, from, to, left, refusal == null, refusal);
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// The operator's attestation that a pending hand-in was delivered, for a hand-in no evidence Quartermaster owns can
    /// settle (the owner's goods were moved on outside its journal, or before this build recorded when the hand-in began).
    /// The carrier still settles only its own proven send; the owner's checkpoint, the run's custody and its deposits
    /// follow as for DeliveredThenPlaced, under the OperatorAttested disposition. The run stays resumable.
    /// </summary>
    public async Task<CourierSnapshot> SettleDeliveredAsync(Guid expectedRun)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("CourierAlreadyRunning");
        IDisposable? lease = null;
        try
        {
            var r = store.Load();
            var u = PendingHandIn(r, expectedRun);
            // Holds Quartermaster's own placements off the owner's bags while they are read and the deposits shrink.
            if (!automation.TryAcquire("courier owner", out lease) || lease == null)
                throw new CourierFault(CourierOutcome.Failed, "AutomationBusy", automation.AcquisitionBlockReason ?? "Busy");
            var owner = await Observe();
            if (!CourierPlanner.Exact(owner.Character, u.OwnerBefore.Character)) throw Fault("OwnerIdentityMismatch", owner.Character.CharacterName);
            var status = await Send(r, CourierCommand.Status, r.Start, []);
            var carrier = status.Bags ?? throw Fault("CarrierBagsMissing", "Fresh observation required.");
            var resuming = r.OutsideSettlement?.HandIn == u.Id;
            if (status.ActiveRun != r.Run || !(status.Unconfirmed == u.Id || resuming && status.LastConfirmed == u.Id))
                throw Fault("CarrierHandOffNotPending", "The carrier does not hold this hand-in unconfirmed.");
            if (!resuming)
                r.OutsideSettlement = new(u.Id, Guid.NewGuid(), OperatorAttestedDisposition, new(u.OwnerBefore, owner),
                    u.At is { } at && placement.PlacedSince(owner.Character, at) is { } since ? since.Operations : []);
            await SettleOutsideAsync(r, u, owner, carrier);
            r.State = "Interrupted"; r.Code = "SettledAsDelivered"; r.Detail = "";
            store.Save(r);
            return Project(r);
        }
        catch (CourierFault e)
        {
            warning($"{expectedRun} SettleDeliveredRefused {e.Code}: {e.Message}");
            throw;
        }
        finally { lease?.Dispose(); gate.Release(); }
    }
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
    /// <summary>
    /// How long the owner's receive may stay open after the carrier reported its send finished. Each window's closure
    /// is observed within 30s; a receive still open after this no longer tracks the trade (a window opened before the
    /// previous one's closure was seen) and would otherwise wait out the whole trade deadline.
    /// </summary>
    internal static readonly TimeSpan ReceiveStallGrace = TimeSpan.FromSeconds(45);
    private async Task EndStalledReceive(CourierJournal r, Guid id, Task local, Task<CourierReceipt> remote)
    {
        try { await remote; } catch { return; } // A carrier fault cancels the receive through FirstFaultCancelsOther.
        if (await Task.WhenAny(local, Task.Delay(ReceiveStallGrace)) == local || trade is not ICourierReceiveStall stall) return;
        warning($"{r.Run}/{id} OwnerReceiveStalledAfterSenderFinished: the carrier's send finished {ReceiveStallGrace.TotalSeconds:0}s ago " +
            "and the owner's receive is still open; ending it so the hand-in settles from both clients' fresh bags.");
        try { await calls.Call("OwnerReceiveStall", 10, d => stall.EndStalledReceiveAsync(id, d)); }
        catch (Exception e) { warning($"{r.Run}/{id} OwnerReceiveStallUnended: {(e as CourierFault)?.Code ?? e.GetType().Name}: {e.Message}"); }
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
        if (!send) _ = EndStalledReceive(r, id, local, remote);
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
                // An unloading owner cannot observe its bags; the trade's own fault stays the step's outcome and the
                // pending intent settles from fresh bags on resume.
                InventoryView? after = null;
                try { after = await Observe(); }
                catch (Exception missing) { warning($"{r.Run}/{id} HandOffEvidenceUnavailable: {missing.GetType().Name}: {missing.Message}"); }
                if (after != null)
                {
                    var e = new HandOff(id, new(before, after), remote.IsCompletedSuccessfully ? remote.Result.Evidence : cancellationReceipt?.Evidence,
                        local.IsCompletedSuccessfully && remote.IsCompletedSuccessfully,r.Pending==null?"NotDelivered":null);
                    r.Evidence.Add(e); store.Save(r);
                }
                else if (local.IsCompletedSuccessfully && remote.IsCompletedSuccessfully)
                    throw Fault("HandOffEvidenceUnavailable", "Both trades finished; the owner's bags could not be observed.");
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
