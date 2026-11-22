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

/// <summary>Why a forward (or one destination of it) cannot run: a stable code and the plain reason.</summary>
internal sealed record DeliveredStowForwardRefusal(string Code, string Message);

/// <summary>
/// How a forward's courier run stands. <c>Starting</c> while the launch has not journaled the run;
/// <c>Missing</c> when it never did (Code and Detail then carry the reason the start reported).
/// </summary>
internal sealed record DeliveredStowForwardProgress(string State, string Code, string Detail, IReadOnlyDictionary<Guid, int> Delivered);

/// <summary>
/// Forwards delivered goods from the owner's bags into retainers of other characters of this account with the
/// courier: the owner is the source, each remote character a destination whose shipments name the home retainer,
/// so the conductor deposits them there. The gates are the courier IPC's: one run at a time, an unresolved run
/// holding custody blocks, the carrier must be opted in and paired with the owner, and the transfer planner's
/// roster, region, meeting-bell and item checks apply per destination. Nothing here waits or retries.
/// </summary>
internal sealed class DeliveredStowCourier
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
        var journal = conductor.Journal();
        if (session.IsActive)
            return new("CourierBusy", "Another courier run is active.");
        if (conductor.IsBusy)
            return new("CourierBusy", "The previous courier run is still finishing cleanup.");
        var unresolved = journal.Run != Guid.Empty && journal.State is "Failed" or "Interrupted" or "Indeterminate";
        if (unresolved && (!journal.Custody.IsEmpty || !conductor.Snapshot().CanClear))
            return new("CourierRunUnresolved", $"The previous courier run is {journal.State} ({journal.Code}); resume or clear it in Courier.");
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
        var locations = session.Locations(runtime());

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
            var (built, refusal) = CourierTransferPlanner.Build(order, cfg, carrierPeer?.Bags, live, metadata, worldRegion, bridge(), locations);
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
        var unresolved = journal.Run != Guid.Empty && journal.State is "Failed" or "Interrupted" or "Indeterminate";
        // A finished or zero-custody unresolved previous run is archived first, exactly as the courier IPC does.
        Guid? clear = journal.Run != Guid.Empty && (journal.State is "Completed" or "Refused" || unresolved) && journal.Custody.IsEmpty ? journal.Run : null;
        var run = Guid.NewGuid();
        // The owner asked for this stow: the run ends back on the owner.
        if (!session.StartPlan(plan, cfg with { ReturnOwner = true }, clear, run, (outcome, fault) => settled[run] = (outcome, fault)))
            return (null, new("CourierBusy", "Another courier run is active."), refused);
        return (run, null, refused);
    }

    /// <summary>The run's state, or null while the courier's request history is still loading.</summary>
    public DeliveredStowForwardProgress? Progress(Guid run, string courierRequestId)
    {
        if (session.Conductor is not { } conductor)
            return new("Missing", "CourierNotLoaded", "The courier is not loaded.", ImmutableDictionary<Guid, int>.Empty);
        var journal = conductor.Journal();
        IReadOnlyDictionary<Guid, int> delivered = journal.Run == run ? new Dictionary<Guid, int>(journal.Delivered) : ImmutableDictionary<Guid, int>.Empty;
        if (conductor.RunsFor(courierRequestId) is not { } known)
            return null;
        if (known.FirstOrDefault(record => record.Run == run) is { } found)
            return new(found.State, found.Code, found.Detail, delivered);
        if (!settled.ContainsKey(run) && session.IsStarting)
            return new("Starting", "", "", delivered);
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
