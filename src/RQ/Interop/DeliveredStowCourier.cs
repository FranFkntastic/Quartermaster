using System.Collections.Concurrent;
using System.Collections.Immutable;
using RQ.Automation;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.Runtime;
using RQ.UI;

namespace RQ.Interop;

/// <summary>One forwarded line: its courier line id and the units bound for each remote home retainer, in rank order.</summary>
internal sealed record DeliveredStowForwardUnit(Guid Line, uint ItemId, string ItemName, bool IsHq, IReadOnlyList<(ulong RetainerId, int Units)> Homes)
{
    public int Units => Homes.Sum(home => home.Units);
}

/// <summary>
/// Why a forward (or one destination of it) cannot run: a stable code and the plain reason. Wait: the cause is
/// another courier run that will end on its own (or be resumed or cleared), so the forward may wait for it.
/// </summary>
internal sealed record DeliveredStowForwardRefusal(string Code, string Message, bool Wait = false);

/// <summary>
/// How a forward's courier run stands. <c>Starting</c> while the launch has not journaled the run;
/// <c>Missing</c> when it never did (Code and Detail then carry the reason the start reported). Current: the run is
/// still the courier's current run, so a stopped one can still be resumed and may hold goods; otherwise it was
/// closed (cleared or replaced) and State and Delivered are its archived journal's.
/// </summary>
internal sealed record DeliveredStowForwardProgress(string State, string Code, string Detail, IReadOnlyDictionary<Guid, int> Delivered, bool Current = false);

/// <summary>
/// A run started earlier under a forward's courier request id. Planned: units per forwarded line its plan carries,
/// null when its journal could not be read.
/// </summary>
internal sealed record DeliveredStowAdoptedRun(Guid Run, IReadOnlyDictionary<Guid, int>? Planned);

/// <summary>
/// Forwards delivered goods from the owner's bags into retainers of other characters of this account with the
/// courier: the owner is the source, each remote character a destination whose shipments name the home retainer,
/// so the conductor deposits them there. The gates are the courier IPC's: one run at a time, an unresolved run
/// holding custody blocks, the carrier must be opted in and paired with the owner, and the transfer planner's
/// roster, region, meeting-bell and item checks apply per destination. Nothing here waits or retries; refusals
/// caused by another courier run say so (<see cref="DeliveredStowForwardRefusal.Wait"/>) and the service waits.
/// Run state comes from <see cref="CourierConductor.PolledSnapshot"/> (a file stat per call unless the journal
/// changed); a closed run's archive is read once, off the framework thread.
/// </summary>
internal interface IDeliveredStowCourier
{
    /// <summary>Why no forward can start now (another job holds automation), or null.</summary>
    string? AutomationBlock { get; }
    DeliveredStowForwardRefusal? Blocked();
    /// <summary>Stops the courier run a forward started (normal Stop: at the next verified checkpoint); false when it is not running.</summary>
    bool Stop(Guid run) => false;
    (Guid? Run, DeliveredStowForwardRefusal? Refusal, IReadOnlyDictionary<ulong, DeliveredStowForwardRefusal> Refused) Start(
        string courierRequestId, string requestHash, OwnerScope owner, IReadOnlyList<(OwnerScope Destination, IReadOnlyList<DeliveredStowForwardUnit> Units)> groups);
    (DeliveredStowAdoptedRun? Run, bool Loading) Adopt(string courierRequestId);
    void Forget(Guid run, string courierRequestId);
    DeliveredStowForwardProgress? Progress(Guid run);
}

internal sealed class DeliveredStowCourier : IDeliveredStowCourier
{
    private readonly CourierSession session;
    private readonly Func<CourierSettings> settings;
    private readonly Func<InventoryView?> localBags;
    private readonly Func<MeetingBell?> bridge;
    private readonly Func<uint, uint> worldRegion;
    private readonly Func<uint, ItemMetadata> metadata;
    private readonly Func<QuartermasterRuntimeSnapshot> runtime;
    private readonly AutomationLease automation;
    private readonly ConcurrentDictionary<Guid, (CourierSnapshot? Outcome, Exception? Fault)> settled = new();
    private readonly ConcurrentDictionary<string, DeliveredStowAdoptedRun> launched = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task<CourierJournal?>> closedReads = new();
    private readonly HashSet<string> historyRead = new(StringComparer.Ordinal);

    public DeliveredStowCourier(CourierSession session, Func<CourierSettings> settings, Func<InventoryView?> localBags,
        Func<MeetingBell?> bridge, Func<uint, uint> worldRegion, Func<uint, ItemMetadata> metadata,
        Func<QuartermasterRuntimeSnapshot> runtime, AutomationLease automation)
    {
        this.session = session;
        this.settings = settings;
        this.localBags = localBags;
        this.bridge = bridge;
        this.worldRegion = worldRegion;
        this.metadata = metadata;
        this.runtime = runtime;
        this.automation = automation;
    }

    /// <summary>Why automation cannot start a courier run right now (another job holds it); null when it can.</summary>
    public string? AutomationBlock => automation.AcquisitionBlockReason;

    /// <summary>Why no courier run can start now, whatever it would carry; null when one can.</summary>
    public DeliveredStowForwardRefusal? Blocked()
    {
        if (session.Conductor is not { } conductor)
            return new("CourierNotLoaded", "The courier is not loaded.");
        var current = conductor.PolledSnapshot();
        var journal = conductor.PolledJournal();
        if (session.IsStarting || current.State == "Running")
            return new("CourierBusy", $"Another courier run is active: {CourierIpcService.DescribeRun(journal)}.", Wait: true);
        if (conductor.IsBusy)
            return new("CourierBusy", "The previous courier run is still finishing cleanup.", Wait: true);
        // A run started under this forward's own request id was adopted before this check, so any unresolved run here is
        // another request's: it is archived only when it holds no custody and has no pending step (Start), else it blocks.
        var unresolved = journal.Run != Guid.Empty && journal.State is "Failed" or "Interrupted" or "Indeterminate";
        if (unresolved && CourierIpcService.Replaceable(journal, null) is null)
            return new("CourierRunUnresolved",
                $"The previous courier {CourierIpcService.DescribeRun(journal)} is {journal.State} ({journal.Code})" +
                (!journal.Custody.IsEmpty ? $" holding {journal.Custody.Sum(s => (long)s.Units):N0} unit(s) in custody"
                    : journal.Pending is { } pending ? $" with its {pending.Action.Phase} step unverified" : "") +
                "; resume or clear it in Courier.", Wait: true);
        var cfg = settings();
        if (cfg.Carrier is not { HasStableIdentity: true })
            return new("CarrierNotConfigured", "No carrier is chosen.");
        var carrierPeer = CarrierPeer(cfg);
        if (carrierPeer?.CarrierOptIn == false)
            return new("CarrierOptInOff", $"{cfg.Carrier.CharacterName} does not have 'Carry for another character' enabled.");
        return null;
    }

    /// <summary>
    /// Plans and starts one courier run carrying every forwardable destination. Destinations the planner refuses are
    /// returned with their reason and left out; when none is left nothing starts and <c>Run</c> is null.
    /// </summary>
    public (Guid? Run, DeliveredStowForwardRefusal? Refusal, IReadOnlyDictionary<ulong, DeliveredStowForwardRefusal> Refused) Start(
        string courierRequestId, string requestHash, OwnerScope owner, IReadOnlyList<(OwnerScope Destination, IReadOnlyList<DeliveredStowForwardUnit> Units)> groups)
    {
        var refused = new Dictionary<ulong, DeliveredStowForwardRefusal>();
        if (Blocked() is { } blocked)
            return (null, blocked, refused);
        var conductor = session.Conductor!;
        var cfg = settings();
        var carrierPeer = CarrierPeer(cfg);
        var live = localBags();
        if (live is null || !CourierPlanner.Exact(live.Character, owner))
            return (null, new("OwnerBagsUnobserved", $"{owner.CharacterName}'s bags are not observed on this client yet."), refused);
        if (carrierPeer?.PairedOwners is { } paired && !paired.Any(pairedOwner => CourierPlanner.Exact(pairedOwner, owner)))
            return (null, new("OwnerNotPaired", $"{owner.CharacterName} is not paired as an owner on {cfg.Carrier!.CharacterName}'s Courier settings."), refused);
        var fromCarrier = CourierPlanner.Exact(owner, cfg.Carrier);
        if (fromCarrier && carrierPeer != null && carrierPeer.CarrierSourcing != true)
            return (null, new("CarrierSourceUnsupported", $"{cfg.Carrier!.CharacterName}'s Quartermaster cannot send stock it holds."), refused);
        var source = cfg.LocalRoster.FirstOrDefault(rostered => CourierPlanner.Exact(rostered, owner)) ?? owner;
        var snapshot = runtime();
        var locations = session.Locations(snapshot);

        var trips = ImmutableArray.CreateBuilder<CourierTrip>();
        foreach (var (wanted, units) in groups)
        {
            var key = wanted.LocalContentId ?? 0;
            var destination = cfg.LocalRoster.FirstOrDefault(rostered => CourierPlanner.Exact(rostered, wanted)) ?? wanted;
            // Every unit names its home retainer, so the transfer planner applies the same deposit checks an external
            // retainer-targeted transfer gets (no plaza drop-off, destination bags observed with a free slot, room).
            var order = new CourierTransferOrder(source, destination, 0,
                [.. units.SelectMany(unit => unit.Homes.Select(home => new CourierTransferItem(unit.ItemId, unit.ItemName, unit.IsHq, home.Units, home.RetainerId)))],
                ReturnDestinationHome: true, courierRequestId);
            var (built, refusal) = CourierTransferPlanner.Build(order, cfg, carrierPeer?.Bags, live, metadata, worldRegion, bridge(), locations,
                retainerId => snapshot.Retainers.GetValueOrDefault(retainerId)?.RetainerName);
            if (refusal != null || built is not { Trips.Length: > 0 })
            {
                refused[key] = new(refusal?.Code ?? "PlanningFailed", refusal?.Message ?? "The courier planned no trip.");
                continue;
            }
            // The stacks travel under each forwarded line's own id, which the run's delivery tally is kept by.
            var lines = units.ToDictionary(unit => (unit.ItemId, unit.IsHq), unit => unit.Line);
            trips.AddRange(built.Trips.Select(trip => trip with
            {
                Manifest = [.. trip.Manifest.Select(stack => stack with { Line = lines[(stack.Item, stack.Hq)] })],
            }));
        }
        if (trips.Count == 0)
            return (null, null, refused);

        var plan = new CourierPlan(Guid.NewGuid(), 0, trips.ToImmutable(), [], ReturnDestinationHome: true, courierRequestId) { RequestHash = requestHash };
        var journal = conductor.Journal();
        // A finished, or unresolved but provably idle, previous run is archived first, exactly as the courier IPC does.
        var clear = CourierIpcService.Replaceable(journal, courierRequestId);
        var run = Guid.NewGuid();
        // The owner asked for this stow: the run ends back on the owner.
        if (!session.StartPlan(plan, cfg with { ReturnOwner = true }, clear, run, (outcome, fault) => settled[run] = (outcome, fault)))
            return (null, new("CourierBusy", "Another courier run is active.", Wait: true), refused);
        // Bound in memory at once: a forward whose journal save fails still finds this run instead of starting another.
        launched[courierRequestId] = new(run, PlannedPerLine(plan.Trips));
        return (run, null, refused);
    }

    /// <summary>
    /// The latest run already started under this courier request id, which a forward adopts instead of starting
    /// another: one this session launched, the courier's current run, or the latest in the courier's request history
    /// (read once per request id per session). Null when there is none; <c>Loading</c> while that history is still
    /// being indexed.
    /// </summary>
    public (DeliveredStowAdoptedRun? Run, bool Loading) Adopt(string courierRequestId)
    {
        if (launched.TryGetValue(courierRequestId, out var mine))
            return (mine, false);
        if (session.Conductor is not { } conductor)
            return (null, false);
        var current = conductor.PolledSnapshot();
        if (current.Run != Guid.Empty && current.RequestId == courierRequestId)
            return (new(current.Run, current.Lines.ToDictionary(line => line.Line, line => line.Moved + line.Remaining)), false);
        if (historyRead.Contains(courierRequestId))
            return (null, false);
        if (conductor.RunsFor(courierRequestId) is not { } known)
            return (null, true);
        historyRead.Add(courierRequestId);
        if (known.OrderByDescending(record => record.At).FirstOrDefault() is not { } latest)
            return (null, false);
        IReadOnlyDictionary<Guid, int>? planned = null;
        try { planned = conductor.Closed(latest.Run) is { } closed ? PlannedPerLine(closed.Plan.Trips) : null; }
        catch (Exception) { /* Adopted without per-line plans; the caller reports those units as unverified. */ }
        return (new(latest.Run, planned), false);
    }

    private static IReadOnlyDictionary<Guid, int> PlannedPerLine(IEnumerable<CourierTrip> trips) =>
        trips.SelectMany(trip => trip.Manifest).GroupBy(stack => stack.Line).ToDictionary(group => group.Key, group => group.Sum(stack => stack.Units));

    public bool Stop(Guid run)
    {
        if (session.Conductor is not { } conductor)
            return false;
        var journal = conductor.Journal();
        if (journal.Run != run || journal.State != "Running" && !session.IsStarting)
            return false;
        session.Stop();
        return true;
    }

    /// <summary>Drops what this adapter keeps about a run whose forward has settled.</summary>
    public void Forget(Guid run, string courierRequestId)
    {
        closedReads.TryRemove(run, out _);
        settled.TryRemove(run, out _);
        launched.TryRemove(courierRequestId, out _);
    }

    /// <summary>
    /// The run's state, or null while its archive is still being read. Throws when that archive is unreadable (the
    /// read is tried again on the next call).
    /// </summary>
    public DeliveredStowForwardProgress? Progress(Guid run)
    {
        IReadOnlyDictionary<Guid, int> none = ImmutableDictionary<Guid, int>.Empty;
        if (session.Conductor is not { } conductor)
            return new("Missing", "CourierNotLoaded", "The courier is not loaded.", none);
        var current = conductor.PolledSnapshot();
        if (current.Run == run)
            return new(current.State, current.Code, current.Detail, current.Lines.ToDictionary(line => line.Line, line => line.Moved), Current: true);
        if (!settled.ContainsKey(run) && session.IsStarting)
            return new("Starting", "", "", none);
        // No longer the current run: it was cleared or replaced, and its archive says how it ended.
        var read = closedReads.GetOrAdd(run, id => Task.Run(() => conductor.Closed(id)));
        if (!read.IsCompleted)
            return null;
        if (!read.IsCompletedSuccessfully)
        {
            closedReads.TryRemove(run, out _);
            var cause = read.Exception?.GetBaseException();
            throw new InvalidOperationException($"Courier run {run}'s archive could not be read: {cause?.Message ?? "cancelled"}", cause);
        }
        if (read.Result is { } closed)
            return new(closed.State, closed.Code, closed.Detail, new Dictionary<Guid, int>(closed.Delivered));
        var delivered = none;
        if (settled.TryGetValue(run, out var end))
            return end.Fault is { } fault
                ? new("Missing", (fault as CourierFault)?.Code ?? "UnexpectedFailure", fault.Message, delivered)
                : new("Missing", end.Outcome?.Code is { Length: > 0 } code ? code : "RunNotJournaled",
                    end.Outcome?.Detail is { Length: > 0 } detail ? detail : $"The run ended {end.Outcome?.State ?? "without a snapshot"} before it was journaled.", delivered);
        return new("Missing", "RunNotJournaled", "The courier has no record of this run (Quartermaster may have reloaded before it began).", delivered);
    }

    private CourierPeer? CarrierPeer(CourierSettings cfg)
    {
        if (cfg.Carrier is not { HasStableIdentity: true } carrier)
            return null;
        var matches = session.Link.Peers.Where(peer => CourierPlanner.Exact(peer.Character, carrier)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
