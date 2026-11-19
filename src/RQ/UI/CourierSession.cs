using System.Collections.Immutable;
using System.Diagnostics;
using RQ.AgentBridge;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>A character logged in on another linked client, with its latest bag observation.</summary>
public sealed record CourierPeer(OwnerScope Character, InventoryView? Bags,IReadOnlyList<OwnerScope>? Roster=null);

/// <summary>
/// The live facts only the courier wiring can supply to the UI. Both members are read on the
/// framework thread every frame, so they must be cheap.
/// </summary>
public interface ICourierLink
{
    /// <summary>
    /// The summoning bell the logged-in character is standing at, ready to be stored as its
    /// meeting bell; null when none is in range or the place cannot be a meeting bell
    /// (housing interiors, apartments, private chambers).
    /// </summary>
    MeetingBell? StandingAt { get; }

    /// <summary>
    /// Characters logged in on the other linked clients: carrier candidates on the owner client,
    /// owner candidates on the carrier client. Return a new list instance whenever an entry or
    /// its bags change; the plan projection is cached on the list reference.
    /// </summary>
    IReadOnlyList<CourierPeer> Peers { get; }
}

public sealed class NullCourierLink : ICourierLink
{
    public static readonly NullCourierLink Instance = new();
    public MeetingBell? StandingAt => null;
    public IReadOnlyList<CourierPeer> Peers { get; } = [];
}

/// <summary>
/// UI-side courier state: planner inputs projected from this client's caches, run control,
/// throttled run snapshots and settings writes. The conductor and follower stay null until
/// the courier wiring constructs them.
/// </summary>
public sealed class CourierSession
{
    private static readonly string[] PlayerBagNames = InventoryScanner.PlayerBags.Select(bag => bag.ToString()).ToArray();
    private static readonly string[] RetainerPageNames = InventoryScanner.RequiredRetainerContainers.Select(bag => bag.ToString()).ToArray();
    private const int PlayerBagSlots = 140;
    private const int RetainerSlots = 175;
    private static readonly long PollTicks = Stopwatch.Frequency / 4;

    private readonly StateRepository state;
    private readonly Func<OwnerScope, PlayerStorageCapture> cachedBags;
    private readonly Func<uint, ItemMetadata> metadata;
    private (long Revision, IReadOnlyList<OwnerScope> Characters)? characters;
    private (long Revision, Guid PlanId, object Peers, CourierPlan? Plan)? plan;
    private CourierSnapshot? run;
    private CarrierView? carried;
    private long polledAt;
    private Task? starting;
    internal string? ClearAcknowledgement { get; private set; }
    private int? testSlotCap;
    internal void SetTestSlotCap(int? value)
    {
        if(value is <1 or >140 || IsActive) throw new InvalidOperationException("Expected an idle run and 1–140 carrier slots.");
        testSlotCap=value;plan=null;
    }

    public CourierSession(
        StateRepository state,
        Func<OwnerScope, PlayerStorageCapture> cachedBags,
        Func<uint, ItemMetadata> metadata)
    {
        this.state = state;
        this.cachedBags = cachedBags;
        this.metadata = metadata;
    }

    public ICourierLink Link { get; set; } = NullCourierLink.Instance;
    public CourierConductor? Conductor { get; set; }
    public CourierFollower? Follower { get; set; }

    /// <summary>The last start, stop or resume failure; cleared by the next successful request.</summary>
    internal string? Error { get; private set; }

    internal static IReadOnlyList<string> PlayerBags => PlayerBagNames;

    // ---- Characters -------------------------------------------------------------------------

    /// <summary>Every character this client knows, by name.</summary>
    internal IReadOnlyList<OwnerScope> Characters(QuartermasterRuntimeSnapshot runtime)
    {
        if (characters is { } cached && cached.Revision == runtime.Revision)
            return cached.Characters;
        var known = runtime.State.Courier.LocalRoster.AsEnumerable()
            .Append(runtime.Owner)
            .Where(owner => owner.HasStableIdentity && !string.IsNullOrWhiteSpace(owner.CharacterName))
            .DistinctBy(owner => (owner.LocalContentId, owner.HomeWorldId))
            .OrderBy(owner => owner.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(owner => owner.HomeWorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        characters = (runtime.Revision, known);
        return known;
    }

    /// <summary>Characters a courier line can deliver to from the logged-in character.</summary>
    internal IEnumerable<OwnerScope> Destinations(QuartermasterRuntimeSnapshot runtime) =>
        Characters(runtime).Where(owner => !owner.Matches(runtime.Owner) && !IsCarrier(runtime, owner));

    internal static bool IsCarrier(QuartermasterRuntimeSnapshot runtime, OwnerScope owner) =>
        runtime.State.Courier.Carrier is { } carrier && carrier.Matches(owner);

    internal static IReadOnlyList<CachedRetainer> Retainers(QuartermasterRuntimeSnapshot runtime, OwnerScope owner) =>
        runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(owner) && retainer.IsCurrentlyAssigned is not false)
            .OrderBy(retainer => retainer.DisplayOrder ?? int.MaxValue)
            .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();

    internal static string CharacterLabel(OwnerScope owner) => owner.CharacterName;

    internal static string ContainerLabel(QuartermasterRuntimeSnapshot runtime, ulong? retainer) =>
        retainer is { } id
            ? runtime.Retainers.GetValueOrDefault(id)?.RetainerName is { Length: > 0 } name ? name : $"Retainer {id}"
            : "Bags";

    /// <summary>"Eris → Fran · Skrrt": each side names its character only when it is not the logged-in one.</summary>
    internal static string RouteLabel(QuartermasterRuntimeSnapshot runtime, CourierRoute route) =>
        $"{Side(runtime, route.SourceCharacter, route.SourceRetainerId)} → {Side(runtime, route.Character, route.RetainerId)}";

    internal static string Side(QuartermasterRuntimeSnapshot runtime, OwnerScope? character, ulong? retainer)
    {
        var container = ContainerLabel(runtime, retainer);
        return character is null || character.Matches(runtime.Owner) ? container : $"{CharacterLabel(character)} · {container}";
    }

    // ---- Planning ---------------------------------------------------------------------------

    /// <summary>The courier plan for a transfer plan's courier lines, or null when it has none.</summary>
    internal CourierPlan? Plan(QuartermasterRuntimeSnapshot runtime, StowagePlan stowagePlan)
    {
        var peers = Link.Peers;
        if (plan is { } cached && cached.Revision == runtime.Revision && cached.PlanId == stowagePlan.Id && ReferenceEquals(cached.Peers, peers))
            return cached.Plan;
        var rules = runtime.State.PlanItems
            .Where(rule => rule.StowagePlanId == stowagePlan.Id && rule.Courier is not null && rule.Enabled)
            .ToArray();
        CourierPlan? built = null;
        if (rules.Length > 0)
        {
            var settings = runtime.State.Courier;
            var carrier = peers.FirstOrDefault(peer => CourierPlanner.Exact(peer.Character, settings.Carrier))?.Bags;
            try
            {
                built = CourierPlanner.Build(stowagePlan, rules, Locations(runtime), carrier, settings, metadata,testSlotCap);
            }
            catch (Exception exception)
            {
                built = new(stowagePlan.Id, stowagePlan.Revision, [],
                    [.. rules.Select(rule => new Refusal(rule.Id, rule.ItemName, "PlanningFailed"))]);
                Error = exception.Message;
            }
        }
        plan = (runtime.Revision, stowagePlan.Id, peers, built);
        return built;
    }

    /// <summary>Observed containers of every known character; unobserved ones are omitted.</summary>
    private IReadOnlyList<CourierLocation> Locations(QuartermasterRuntimeSnapshot runtime)
    {
        var locations = new List<CourierLocation>();
        foreach (var owner in Characters(runtime))
        {
            var capture = cachedBags(owner);
            if (PlayerBagNames.All(name => capture.ObservedSources.Contains(name, StringComparer.Ordinal)))
            {
                var items = capture.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal))
                    .SelectMany(bag => bag.Items).Where(item => item.ItemId > 0 && item.Quantity > 0).ToArray();
                locations.Add(new(owner, null, new(owner, Math.Max(0, PlayerBagSlots - items.Length),
                    [.. items.Select(item => new BagStock(item.ItemId, item.IsHq, checked((int)item.Quantity)))])));
            }
            foreach (var retainer in Retainers(runtime, owner))
            {
                if (!RetainerPageNames.All(name => retainer.ObservedSources.Contains(name, StringComparer.Ordinal)))
                    continue;
                var items = retainer.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
                    .SelectMany(bag => bag.Items).Where(item => item.ItemId > 0 && item.Quantity > 0).ToArray();
                locations.Add(new(owner, retainer.RetainerId, new(owner, Math.Max(0, RetainerSlots - items.Length),
                    [.. items.Select(item => new BagStock(item.ItemId, item.IsHq, checked((int)item.Quantity)))])));
            }
        }
        return locations;
    }

    /// <summary>Refusals that hold for every courier line, so the plan shows them once.</summary>
    internal static bool IsPlanWide(string code) => code is "CarrierNotConfigured" or "CarrierUnreachable";

    internal static string RefusalLabel(string code) => code switch
    {
        "CarrierNotConfigured" => "No carrier set",
        "CarrierUnreachable" => "Carrier not online",
        "CharacterNotKnown" => "Character not known",
        "MeetingBellMissing" => "No meeting bell",
        "ItemNotTradeable" => "Not tradeable",
        "SourceUnknown" => "Source not observed",
        "DestinationUnknown" => "Destination not observed",
        "ItemMetadataUnknown" => "Item data unavailable",
        "InvalidQuantity" => "No quantity",
        "UniqueConflict" => "Unique item already held",
        "DestinationFull" => "Destination full",
        "SourceShort" => "Source short",
        "BagSlotLimit" => "No bag room",
        _ => Words(code),
    };

    // ---- Run control ------------------------------------------------------------------------

    internal CourierSnapshot? Run { get { Poll(); return run; } }
    internal CarrierView? Carried { get { Poll(); return carried; } }

    internal bool IsStarting => starting is { IsCompleted: false };
    internal bool IsActive => IsStarting || Run?.State == "Running";
    internal bool IsUnresolved => Run?.State is "Interrupted" or "Failed" or "Indeterminate";

    private void Poll(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - polledAt < PollTicks)
            return;
        polledAt = now;
        try { run = Conductor?.Snapshot(); }
        catch (Exception exception) { Error = exception.Message; }
        try { carried = Follower?.View(); }
        catch (Exception exception) { Error = exception.Message; }
    }

    /// <summary>Why a courier plan cannot start now, or null when it can.</summary>
    internal string? StartBlock(QuartermasterRuntimeSnapshot runtime, CourierPlan? courierPlan) =>
        Conductor is null ? "Courier runs are unavailable."
        : !runtime.Owner.HasStableIdentity ? "Character identity is unavailable."
        : IsActive ? "A courier run is active."
        : IsUnresolved ? "Resume the interrupted courier run first."
        : courierPlan is null || courierPlan.Trips.IsEmpty && courierPlan.Refusals.IsEmpty ? "Nothing to send."
        : courierPlan.Refusals.FirstOrDefault(refusal => IsPlanWide(refusal.Code)) is { } wide ? RefusalLabel(wide.Code) + "."
        : !courierPlan.Refusals.IsEmpty ? $"{courierPlan.Refusals.Length:N0} line{(courierPlan.Refusals.Length == 1 ? string.Empty : "s")} cannot be sent."
        : null;

    internal void Start(QuartermasterRuntimeSnapshot runtime, StowagePlan stowagePlan)
    {
        plan = null;
        var courierPlan = Plan(runtime, stowagePlan);
        if (Conductor is not { } conductor || StartBlock(runtime, courierPlan) is not null)
            return;
        Launch(() => conductor.StartAsync(courierPlan!, runtime.State.Courier));
    }

    internal void Clear(Guid expectedRun,string? acknowledgement)
    {
        if(Conductor is not {} conductor || IsActive) return;
        Launch(async ()=>{
            try{var snapshot=await conductor.ClearAsync(expectedRun,acknowledgement);ClearAcknowledgement=null;return snapshot;}
            catch(CourierConductor.CustodyAcknowledgementRequired e){ClearAcknowledgement=e.Message;throw;}
        });
    }

    /// <summary>Resumes an interrupted run from its journal; the journal owns the plan and settings.</summary>
    internal void Resume()
    {
        if (Conductor is not { } conductor || IsActive || Run?.CanResume!=true)
            return;
        Launch(() => conductor.StartAsync(new(Guid.Empty, 0, [], []), new()));
    }

    private void Launch(Func<Task<CourierSnapshot>> start)
    {
        Error = null;
        starting = Task.Run(async () =>
        {
            try { var result=await start().ConfigureAwait(false);
                if(result.State is "Refused" or "Failed" or "Indeterminate" or "Interrupted")Error=result.Code+": "+result.Detail; }
            catch (Exception exception) { Error = exception.Message; }
            finally { polledAt = 0; }
        });
        polledAt = 0;
    }

    internal void Stop()
    {
        if (Conductor is not { } conductor)
            return;
        _ = Task.Run(async () =>
        {
            try { await conductor.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { Error = exception.Message; }
            finally { polledAt = 0; }
        });
    }

    internal void StopCarrier()
    {
        if (Follower is not { } follower)
            return;
        _ = Task.Run(async () =>
        {
            try { await follower.StopLocalAsync().ConfigureAwait(false); }
            catch (Exception exception) { Error = exception.Message; }
            finally { polledAt = 0; }
        });
    }

    internal static string PhaseLabel(string phase) => phase switch
    {
        nameof(CourierPhase.EnsureSource) => "Going to the meeting bell",
        nameof(CourierPhase.Retrieve) => "Retrieving",
        nameof(CourierPhase.CarrierToSource) => "Waiting for the carrier",
        nameof(CourierPhase.HandOut) => "Trading to the carrier",
        nameof(CourierPhase.SwitchToDestination) => "Switching character",
        nameof(CourierPhase.CarrierToDestination) => "Waiting for the carrier",
        nameof(CourierPhase.HandIn) => "Receiving from the carrier",
        nameof(CourierPhase.Deposit) => "Depositing",
        nameof(CourierPhase.ReturnOwner) => "Returning to the start character",
        nameof(CourierPhase.ReturnCarrier) => "Carrier returning",
        nameof(CourierPhase.Finish) => "Finishing",
        nameof(CourierCommand.Travel) => "Traveling to the meeting bell",
        nameof(CourierCommand.Receive) => "Receiving a trade",
        nameof(CourierCommand.Send) => "Trading back",
        nameof(CourierCommand.Confirm) => "Confirming a hand-off",
        nameof(CourierCommand.ReturnHome) => "Returning to start",
        nameof(CourierCommand.Status) => "Reporting bags",
        nameof(CourierCommand.Begin) => "Joining the run",
        nameof(CourierCommand.Stop) => "Stopping",
        "Idle" => "Idle",
        _ => Words(phase),
    };

    /// <summary>"StopRequested" → "Stop requested".</summary>
    internal static string Words(string code)
    {
        if (string.IsNullOrEmpty(code))
            return code;
        var text = new System.Text.StringBuilder(code.Length + 8);
        for (var index = 0; index < code.Length; index++)
        {
            var c = code[index];
            if (index > 0 && char.IsUpper(c) && !char.IsUpper(code[index - 1]))
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    // ---- Agent bridge -----------------------------------------------------------------------

    internal CourierBridgeTruth Truth(QuartermasterRuntimeSnapshot runtime,Guid? planId=null)
    {
        var settings = runtime.State.Courier;
        var current = Run;
        var view = Carried;
        var known = Characters(runtime);
        var selected=runtime.State.StowagePlans.FirstOrDefault(p=>p.Id==planId);
        var projection=selected==null?null:Plan(runtime,selected);
        var blocked=Conductor==null?"CourierUnavailable":!runtime.Owner.HasStableIdentity?"OwnerIdentityUnavailable":
            IsActive?"CourierActive":IsUnresolved?"RunUnresolved":projection==null?"NothingToSend":
            projection.Refusals.FirstOrDefault()?.Code ?? (projection.Trips.IsEmpty?"NothingToSend":null);
        return new(
            Conductor is not null || Follower is not null,
            settings.Carrier?.CharacterName,
            settings.CarrierOptIn,
            settings.Owners.FirstOrDefault()?.CharacterName,
            [.. known.Where(owner => settings.Bells.ContainsKey(owner.LocalContentId!.Value)).Select(owner => owner.CharacterName)],
            IsStarting,
            current?.State,
            current?.OwnerPhase,
            current?.CarrierPhase,
            current?.Trip ?? 0,
            current?.Trips ?? 0,
            current?.Code,
            current?.Detail,
            [.. current?.Lines.Select(line => new CourierLineBridgeTruth(line.Name, line.Moved, line.Remaining)) ?? []],
            view?.Active?.ToString(),
            view?.Custody.Sum(shipment => shipment.Units) ?? 0,
            view?.Paused ?? false,
            view?.Unconfirmed ?? false,
            Error,current?.CanResume==true && !IsActive,current?.CanClear==true && !IsActive,
            view is {Custody.IsEmpty:false}?CourierConductor.Rows(view.Custody,settings.Carrier):current?.Custody??[],
            blocked,projection?.Refusals??[],[..settings.Owners.Select(o=>o.CharacterName)]);
    }

    // ---- Settings ---------------------------------------------------------------------------

    internal void UpdateSettings(Func<CourierSettings, CourierSettings> change)
    {
        try
        {
            state.Mutate(document => { document.Courier = change(document.Courier); });
            plan = null;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    /// <summary>The bell the logged-in character stands at, when it can be that character's meeting bell.</summary>
    internal MeetingBell? CapturableBell(QuartermasterRuntimeSnapshot runtime) =>
        runtime.Owner.HasStableIdentity && Link.StandingAt is { } bell && bell.World == runtime.Owner.HomeWorldId &&
        !(runtime.State.Courier.Bells.GetValueOrDefault(runtime.Owner.LocalContentId!.Value) is { } stored && stored.SamePlace(bell))
            ? bell
            : null;

    internal void CaptureBell(QuartermasterRuntimeSnapshot runtime)
    {
        if (CapturableBell(runtime) is not { } bell)
            return;
        var id = runtime.Owner.LocalContentId!.Value;
        UpdateSettings(settings => settings with { Bells = settings.Bells.SetItem(id, bell) });
    }
}
