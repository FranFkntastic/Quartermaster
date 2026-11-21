using System.Collections.Immutable;
using System.Diagnostics;
using RQ.AgentBridge;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>A character logged in on another linked client, with its latest bag observation.</summary>
/// <summary>Whether Start is offered, whether it can run, and the short cause when it cannot.</summary>
internal readonly record struct CourierStartGate(bool Offer, bool CanStart, string? Status, bool OpensSettings);

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
    bool InWard=>false;
    int RoutePoints=>0;
    string? RouteError=>null;
    void StartRoute(){}
    void CancelRoute(){}
    MeetingBell? CaptureHere(ulong character)=>null;

    /// <summary>Why the logged-in character's current place cannot hold a meeting bell; null where one could.</summary>
    string? BellBlock { get; }

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
    public string? BellBlock => null;
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
    private Guid? settledRun;
    private CarrierView? carried;
    private long polledAt;
    private Task? starting;
    internal string? ClearAcknowledgement { get; private set; }
    /// <summary>The carrier's confirmed ledger behind <see cref="ClearAcknowledgement"/>.</summary>
    internal ImmutableArray<CustodyRow> ClearLedger { get; private set; } = [];
    private long? settingsRequestedAt;
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

    /// <summary>Warning-level log sink for failure detail that never reaches the visible status.</summary>
    public Action<string>? Warn { get; set; }

    /// <summary>The last start, stop, resume or clear failure in plain words; cleared by the next request.</summary>
    internal string? Error { get; private set; }

    /// <summary>The raw failure behind <see cref="Error"/>, for the agent bridge and the log only.</summary>
    internal string? ErrorDetail { get; private set; }

    private void Fail(string visible, string detail)
    {
        if (Error == visible && ErrorDetail == detail)
            return;
        Error = visible;
        ErrorDetail = detail;
        Warn?.Invoke($"Courier: {visible} ({detail})");
    }

    /// <summary>Asks the stock options popup, which holds the courier settings, to open.</summary>
    internal void RequestSettings() => settingsRequestedAt = Environment.TickCount64;

    /// <summary>Takes a pending settings request; one the stock pane could not show promptly lapses.</summary>
    internal bool TakeSettingsRequest()
    {
        var requested = settingsRequestedAt is { } at && Environment.TickCount64 - at < 500;
        settingsRequestedAt = null;
        return requested;
    }

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

    /// <summary>
    /// Characters a courier line can deliver to from its source: from the logged-in character, every
    /// other character; from another character, the logged-in one first, then the rest.
    /// </summary>
    internal IEnumerable<OwnerScope> Destinations(QuartermasterRuntimeSnapshot runtime, OwnerScope? source = null)
    {
        var others = Characters(runtime).Where(owner => !owner.Matches(runtime.Owner) && !IsCarrier(runtime, owner));
        if (source is null || source.Matches(runtime.Owner))
            return others;
        var here = runtime.Owner.HasStableIdentity && !IsCarrier(runtime, runtime.Owner) ? new[] { runtime.Owner } : [];
        return here.Concat(others.Where(owner => !owner.Matches(source)));
    }

    /// <summary>Other characters whose stock this client has observed, for the stock browser.</summary>
    internal IEnumerable<OwnerScope> Browsable(QuartermasterRuntimeSnapshot runtime) =>
        Characters(runtime).Where(owner => !owner.Matches(runtime.Owner) && !IsCarrier(runtime, owner) && Stock(runtime, owner) is not null);

    private (long Revision, Dictionary<ulong, BrowserProjection?> Projections)? stock;

    /// <summary>
    /// Another character's last observed bags and retainers as a stock browser projection; null when
    /// neither has been observed on this client. Cached per planning revision.
    /// </summary>
    internal BrowserProjection? Stock(QuartermasterRuntimeSnapshot runtime, OwnerScope character)
    {
        if (character.LocalContentId is not { } id)
            return null;
        if (stock is not { } cache || cache.Revision != runtime.PlanningRevision)
            stock = cache = (runtime.PlanningRevision, []);
        if (cache.Projections.TryGetValue(id, out var hit))
            return hit;
        var capture = cachedBags(character);
        var bags = PlayerBagNames.All(name => capture.ObservedSources.Contains(name, StringComparer.Ordinal))
            ? capture.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal)).ToArray()
            : [];
        var observed = bags.Length > 0 || Retainers(runtime, character).Any(retainer =>
            RetainerPageNames.All(name => retainer.ObservedSources.Contains(name, StringComparer.Ordinal)));
        var projection = observed ? BrowserProjectionBuilder.Build(bags, runtime.Retainers, character, metadata) : null;
        cache.Projections[id] = projection;
        return projection;
    }

    private (long Revision, BrowserProjection Browser, IReadOnlyList<ExternalCharacter> External, AccountStockProjection Projection)? account;

    /// <summary>
    /// The logged-in character's stock merged with every other observed, non-carrier character's
    /// (<see cref="Browsable"/>), then every character on another service account
    /// (<see cref="ExternalCharacters"/>). The logged-in character's own projection stays the source
    /// for its planning; this view only adds the other characters beside it. Cached per planning
    /// revision and sibling-cache revision.
    /// </summary>
    internal AccountStockProjection AccountStock(QuartermasterRuntimeSnapshot runtime)
    {
        var external = ExternalCharacters(runtime);
        if (account is { } cached && cached.Revision == runtime.PlanningRevision && ReferenceEquals(cached.Browser, runtime.Browser) &&
            ReferenceEquals(cached.External, external))
            return cached.Projection;
        var others = Browsable(runtime)
            .Select(character => (character, Stock(runtime, character)))
            .Where(entry => entry.Item2 is not null)
            .Select(entry => (entry.character, entry.Item2!));
        var projection = AccountStockProjection.Build(runtime.Owner, runtime.Browser, others,
            external.Select(entry => (entry.Character, entry.Stock, entry.Profile)));
        account = (runtime.PlanningRevision, runtime.Browser, external, projection);
        return projection;
    }

    // ---- Other service accounts -------------------------------------------------------------

    /// <summary>
    /// Sibling XIVLauncher profiles' cached stock. Null keeps the stock browser to this account.
    /// Its characters are read-only visibility: never courier sources, plan stock or retrievable.
    /// </summary>
    public ExternalAccountStockCache? ExternalStockCache { get; set; }

    /// <summary>Characters this client's own player cache holds, so a sibling's copy of one is never shown as another account's.</summary>
    public Func<IReadOnlyList<OwnerScope>>? LocalOwners { get; set; }

    /// <summary>A character on another service account, with the profile its cached stock came from.</summary>
    internal sealed record ExternalCharacter(OwnerScope Character, string Profile, BrowserProjection Stock);

    private (long Revision, IReadOnlyList<ExternalCharacter> All)? externalBuilt;
    private (long Revision, long ExternalRevision, IReadOnlyList<ExternalCharacter> Visible)? externalVisible;

    /// <summary>
    /// Characters whose stock only a sibling profile's cache holds, by name. Characters this client
    /// knows (logged in, roster, own caches) are left out. The list instance changes only when its
    /// contents do, so the account projection stays cached between sibling polls.
    /// </summary>
    internal IReadOnlyList<ExternalCharacter> ExternalCharacters(QuartermasterRuntimeSnapshot runtime)
    {
        var snapshot = ExternalStockCache?.Current ?? ExternalAccountSnapshot.Empty;
        if (externalVisible is { } cached && cached.Revision == runtime.Revision && cached.ExternalRevision == snapshot.Revision)
            return cached.Visible;
        var local = LocalContentIds(runtime);
        IReadOnlyList<ExternalCharacter> visible = BuildExternal(snapshot)
            .Where(entry => !local.Contains(entry.Character.LocalContentId!.Value))
            .ToArray();
        if (externalVisible is { } previous && previous.Visible.SequenceEqual(visible))
            visible = previous.Visible;
        externalVisible = (runtime.Revision, snapshot.Revision, visible);
        return visible;
    }

    /// <summary>The cached stock of a character on another service account; null for this account's characters.</summary>
    internal BrowserProjection? ExternalStock(QuartermasterRuntimeSnapshot runtime, OwnerScope character) =>
        FindExternal(runtime, character)?.Stock;

    internal bool IsExternal(QuartermasterRuntimeSnapshot runtime, OwnerScope character) =>
        FindExternal(runtime, character) is not null;

    /// <summary>"XIVLauncher-Multibox-2" for a character on another service account; null otherwise.</summary>
    internal string? ExternalProfile(QuartermasterRuntimeSnapshot runtime, OwnerScope character) =>
        FindExternal(runtime, character)?.Profile;

    private ExternalCharacter? FindExternal(QuartermasterRuntimeSnapshot runtime, OwnerScope character)
    {
        foreach (var entry in ExternalCharacters(runtime))
            if (entry.Character.Matches(character))
                return entry;
        return null;
    }

    private HashSet<ulong> LocalContentIds(QuartermasterRuntimeSnapshot runtime)
    {
        var ids = new HashSet<ulong>();
        if (runtime.Owner.LocalContentId is > 0)
            ids.Add(runtime.Owner.LocalContentId.Value);
        foreach (var owner in runtime.State.Courier.LocalRoster)
            if (owner.LocalContentId is > 0)
                ids.Add(owner.LocalContentId.Value);
        foreach (var retainer in runtime.Retainers.Values)
            if (retainer.Owner.LocalContentId is > 0)
                ids.Add(retainer.Owner.LocalContentId.Value);
        try
        {
            foreach (var owner in LocalOwners?.Invoke() ?? [])
                if (owner.LocalContentId is > 0)
                    ids.Add(owner.LocalContentId.Value);
        }
        catch (Exception exception) { Warn?.Invoke($"Quartermaster could not list this client's cached characters: {exception.Message}"); }
        return ids;
    }

    /// <summary>One read-only stock projection per sibling-profile character, rebuilt only when a sibling cache changed.</summary>
    private IReadOnlyList<ExternalCharacter> BuildExternal(ExternalAccountSnapshot snapshot)
    {
        if (externalBuilt is { } cached && cached.Revision == snapshot.Revision)
            return cached.All;
        var characters = new Dictionary<ulong, OwnerScope>();
        foreach (var player in snapshot.PlayerBags.Values)
            if (player.Owner.LocalContentId is > 0 && !string.IsNullOrWhiteSpace(player.Owner.CharacterName))
                characters.TryAdd(player.Owner.LocalContentId.Value, player.Owner);
        foreach (var retainer in snapshot.Retainers.Values)
            if (retainer.Owner.LocalContentId is > 0 && !string.IsNullOrWhiteSpace(retainer.Owner.CharacterName))
                characters.TryAdd(retainer.Owner.LocalContentId.Value, retainer.Owner);
        var built = new List<ExternalCharacter>(characters.Count);
        foreach (var (id, owner) in characters)
        {
            var bags = ExternalBags(snapshot.PlayerBags.GetValueOrDefault(id), owner);
            var retainerObserved = snapshot.Retainers.Values.Any(retainer =>
                retainer.Owner.Matches(owner) && retainer.IsCurrentlyAssigned is not false &&
                RetainerPageNames.All(name => retainer.ObservedSources.Contains(name, StringComparer.Ordinal)));
            if (bags.Length == 0 && !retainerObserved)
                continue;
            try
            {
                built.Add(new(owner, snapshot.ProfileByCharacter.GetValueOrDefault(id) ?? "another profile",
                    BrowserProjectionBuilder.Build(bags, snapshot.Retainers, owner, metadata)));
            }
            catch (Exception exception)
            {
                Warn?.Invoke($"Quartermaster skipped {owner.CharacterName}'s other-account stock: {exception.Message}");
            }
        }
        built.Sort((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Character.CharacterName, right.Character.CharacterName);
            return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(left.Character.HomeWorldName, right.Character.HomeWorldName);
        });
        externalBuilt = (snapshot.Revision, built);
        return built;
    }

    /// <summary>A sibling character's fully observed bags and any observed saddlebag pages.</summary>
    private static InventoryBag[] ExternalBags(CachedPlayerInventory? cached, OwnerScope owner)
    {
        if (cached is null || !owner.Matches(cached.Owner))
            return [];
        var observed = cached.ObservedSources;
        var bagsComplete = PlayerBagNames.All(name => observed.Contains(name, StringComparer.Ordinal));
        return cached.Bags
            .Where(bag => observed.Contains(bag.BagName, StringComparer.Ordinal) &&
                          (bagsComplete && PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal) || InventoryScanner.IsSaddlebag(bag.BagName)))
            .Select(bag => new InventoryBag { BagName = bag.BagName, Location = bag.Location, Items = bag.Items })
            .ToArray();
    }

    private (long Revision, Dictionary<(Guid, ulong, ItemQualityPolicy), PlacementMovePlan?> Plans)? gathers;

    /// <summary>
    /// What destination consolidation would gather for a courier line on its destination character
    /// today: the item's stacks on that character's other retainers moved into the line's rank 1.
    /// Null when the line does not consolidate or its stack size is unknown. Delivery itself is not
    /// projected; the run plans the moves again from fresh stock after its deposits.
    /// </summary>
    internal PlacementMovePlan? Gather(QuartermasterRuntimeSnapshot runtime, TargetPlanItem rule)
    {
        if (rule.Courier is not { } route || !rule.Routing.Consolidate || rule.Routing.PreferredRetainerIds.Count == 0)
            return null;
        if (gathers is not { } cache || cache.Revision != runtime.Revision)
            gathers = cache = (runtime.Revision, []);
        var key = (rule.Id, rule.Routing.PreferredRetainerIds[0], rule.Quality);
        if (cache.Plans.TryGetValue(key, out var hit))
            return hit;
        PlacementMovePlan? plan = null;
        if (metadata(rule.ItemId).MaxStackSize is > 0 and <= int.MaxValue and var size)
        {
            var probe = new TargetPlanItem
            {
                Id = rule.Id, ItemId = rule.ItemId, ItemName = rule.ItemName, Quality = rule.Quality,
                Routing = new() { PreferredRetainerIds = [rule.Routing.PreferredRetainerIds[0]], Consolidate = true },
            };
            plan = PlacementMovePlanner.FromCache(probe, Retainers(runtime, route.Character), (int)size);
        }
        cache.Plans[key] = plan;
        return plan;
    }

    /// <summary>Free slots in a character's observed bags; null while its bags have not been observed.</summary>
    internal int? FreeBagSlots(OwnerScope character)
    {
        var capture = cachedBags(character);
        if (!PlayerBagNames.All(name => capture.ObservedSources.Contains(name, StringComparer.Ordinal)))
            return null;
        var used = capture.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal))
            .SelectMany(bag => bag.Items).Count(item => item.ItemId > 0 && item.Quantity > 0);
        return Math.Max(0, PlayerBagSlots - used);
    }

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

    /// <summary>"Eris → Fran · Skrrt +1": each side names its character only when it is not the logged-in one.</summary>
    internal static string RouteLabel(QuartermasterRuntimeSnapshot runtime, TargetPlanItem rule) =>
        rule.Courier is { } route
            ? $"{Side(runtime, route.SourceCharacter, route.SourceRetainerId)} → {Destination(runtime, route.Character, rule.Routing)}"
            : string.Empty;

    /// <summary>
    /// Where a courier line lands, as Transfer Placement reads it: "Bags" with no destinations, else its
    /// rank-1 retainer and how many fallbacks follow ("Grungel +1"), or "Any space" alone. The character
    /// is named unless it is the logged-in one and <paramref name="includeOwner"/> is off.
    /// </summary>
    internal static string Destination(QuartermasterRuntimeSnapshot runtime, OwnerScope character, StowageRoutingPolicy routing, bool includeOwner = false)
    {
        var listed = routing.PreferredRetainerIds.Distinct().ToArray();
        var container = listed.Length == 0
            ? routing.AnySpace ? TransferPresentation.AnySpaceLabel : ContainerLabel(runtime, null)
            : ContainerLabel(runtime, listed[0]) + (listed.Length - 1 + (routing.AnySpace ? 1 : 0) is > 0 and var more ? $" +{more}" : string.Empty);
        if (!includeOwner && character.Matches(runtime.Owner))
            return container;
        return $"{CharacterLabel(character)} · {container}";
    }

    /// <summary>The full destination order ("Grungel → Eris → Any space", or "Bags"), for tooltips and the review.</summary>
    internal static string DestinationOrder(QuartermasterRuntimeSnapshot runtime, OwnerScope character, StowageRoutingPolicy routing)
    {
        var listed = routing.PreferredRetainerIds.Distinct().Select(id => ContainerLabel(runtime, id)).ToList();
        if (routing.AnySpace)
            listed.Add(TransferPresentation.AnySpaceLabel);
        var order = listed.Count == 0 ? ContainerLabel(runtime, null) : string.Join(" → ", listed);
        return $"{CharacterLabel(character)} · {order}{(routing.Consolidate && routing.PreferredRetainerIds.Count > 0 ? $" · gathers strays into {ContainerLabel(runtime, routing.PreferredRetainerIds[0])}" : string.Empty)}";
    }

    /// <summary>
    /// The container a destination chooser checks for a line: null for bags (no destinations), its
    /// rank-1 retainer, or <see cref="PlacementEdits.AnySpace"/> (no retainer) for Any space alone.
    /// </summary>
    internal static ulong? FirstDestination(StowageRoutingPolicy routing) =>
        routing.PreferredRetainerIds.Count > 0 ? routing.PreferredRetainerIds[0]
        : routing.AnySpace ? PlacementEdits.AnySpace : null;

    internal static string Side(QuartermasterRuntimeSnapshot runtime, OwnerScope? character, ulong? retainer, bool includeOwner = false)
    {
        var container = ContainerLabel(runtime, retainer);
        if (!includeOwner && (character is null || character.Matches(runtime.Owner)))
            return container;
        return $"{CharacterLabel(character ?? runtime.Owner)} · {container}";
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
                Warn?.Invoke($"Courier planning failed for transfer plan {stowagePlan.Id}: {exception}");
            }
        }
        plan =(runtime.Revision, stowagePlan.Id, peers, built);
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

    private (long Revision, Dictionary<(ulong?, ulong?, uint, ItemQualityPolicy), int?> Units)? available;

    /// <summary>
    /// Units of an item at a courier source container, by the line's quality policy; null while
    /// that container has not been observed. Cached per runtime revision.
    /// </summary>
    internal int? Available(QuartermasterRuntimeSnapshot runtime, OwnerScope source, ulong? retainer, uint itemId, ItemQualityPolicy quality)
    {
        if (available is not { } cache || cache.Revision != runtime.Revision)
            available = cache = (runtime.Revision, []);
        var key = (source.LocalContentId, retainer, itemId, quality);
        if (cache.Units.TryGetValue(key, out var hit))
            return hit;
        IEnumerable<(uint ItemId, bool Hq, long Quantity)>? items = null;
        if (retainer is { } id)
        {
            if (runtime.Retainers.GetValueOrDefault(id) is { } cached && RetainerPageNames.All(name => cached.ObservedSources.Contains(name, StringComparer.Ordinal)))
                items = cached.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
                    .SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.IsHq, (long)item.Quantity));
        }
        else
        {
            var capture = cachedBags(source);
            if (PlayerBagNames.All(name => capture.ObservedSources.Contains(name, StringComparer.Ordinal)))
                items = capture.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal))
                    .SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.IsHq, (long)item.Quantity));
        }
        int? units = items is null ? null : (int)Math.Min(int.MaxValue, items
            .Where(item => item.ItemId == itemId && (quality == ItemQualityPolicy.Any || item.Hq == (quality == ItemQualityPolicy.HqOnly)))
            .Sum(item => Math.Max(0, item.Quantity)));
        cache.Units[key] = units;
        return units;
    }

    /// <summary>Refusals that hold for every courier line, so the plan shows them once.</summary>
    internal static bool IsPlanWide(string code) => code is "CarrierNotConfigured" or "CarrierUnreachable" or "PlanningFailed";

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
        "OutsideClientRoster" => "Not on this account",
        "PlanningFailed" => "Courier plan failed",
        _ => Words(code),
    };

    /// <summary>The line's refusal as a sentence naming the item and characters involved.</summary>
    internal string RefusalDetail(QuartermasterRuntimeSnapshot runtime, Refusal refusal)
    {
        var settings = runtime.State.Courier;
        var carrier = settings.Carrier?.CharacterName ?? "the carrier";
        var source = refusal.Source ?? "the source";
        var destination = refusal.Destination ?? "the destination";
        string[] named = [.. new[] { refusal.Source, refusal.Destination }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return refusal.Code switch
        {
            "CarrierNotConfigured" => "No carrier chosen",
            "CarrierUnreachable" => $"{carrier} is not running",
            "OutsideClientRoster" => $"{string.Join(" and ", named.Where(name => !settings.LocalRoster.Any(owner => Same(owner.CharacterName, name))).DefaultIfEmpty(source))} not on this account",
            "CharacterNotKnown" when Same(source, destination) => $"{refusal.Name} would go from {source} to {source}",
            "CharacterNotKnown" when named.FirstOrDefault(name => Same(name, settings.Carrier?.CharacterName)) is { } isCarrier => $"{isCarrier} is the carrier",
            "CharacterNotKnown" => $"Bags of {source} or {destination} not observed",
            "MeetingBellMissing" => $"No meeting bell for {string.Join(" or ", MissingBells(runtime, named).DefaultIfEmpty(source))}",
            "ItemNotTradeable" => $"{refusal.Name} cannot be traded",
            "SourceUnknown" => $"Source container on {source} not observed",
            "DestinationUnknown" => $"Destination retainers on {destination} not observed",
            "ItemMetadataUnknown" => $"No item data for {refusal.Name}",
            "InvalidQuantity" => $"No quantity for {refusal.Name}",
            "UniqueConflict" => $"{refusal.Name} is unique and {destination} or {carrier} already holds one",
            "DestinationFull" => $"No room for {refusal.Name} on {destination}",
            "SourceShort" => $"Not enough {refusal.Name} on {source}",
            "BagSlotLimit" => $"No free bag slot on {source}, {destination} or {carrier}",
            "PlanningFailed" => $"Courier plan failed for {refusal.Name}",
            _ => $"{refusal.Name}: {RefusalLabel(refusal.Code)}",
        };
    }

    /// <summary>The named characters that have no meeting bell on their home world.</summary>
    internal IEnumerable<string> MissingBells(QuartermasterRuntimeSnapshot runtime, IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).Where(name =>
            Characters(runtime).FirstOrDefault(owner => string.Equals(owner.CharacterName, name, StringComparison.OrdinalIgnoreCase)) is not { LocalContentId: { } id } owner ||
            runtime.State.Courier.Bells.GetValueOrDefault(id) is not { } bell || bell.World != owner.HomeWorldId);

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
        try
        {
            run = Conductor?.Snapshot();
            if (run is { State: "Completed" } completed && settledRun != completed.Run)
            {
                ApplyCompleted(completed);
                settledRun = completed.Run;
            }
        }
        catch (Exception exception) { Fail("Courier run state unreadable", exception.ToString()); }
        try { carried = Follower?.View(); }
        catch (Exception exception) { Fail("Carrier state unreadable", exception.ToString()); }
    }

    /// <summary>
    /// The unsaved plan a completed run's delivered lines clear from: the run's own plan, or, for a
    /// run started from an unsaved Transfer plan before courier lines moved to their own unsaved
    /// Courier plan, that owner's unsaved Courier plan. Saved plans keep their lines.
    /// </summary>
    private static StowagePlan? CompletedScratch(QuartermasterState document, CourierSnapshot completed) =>
        document.StowagePlans.SingleOrDefault(p => p.Id == completed.PlanId && p.IsEphemeral) is { } scratch
            ? scratch.Kind == StowagePlanKind.Courier ? scratch : StowagePlanCatalog.FindScratch(document, scratch.Owner, StowagePlanKind.Courier)
            : null;

    private void ApplyCompleted(CourierSnapshot completed)
    {
        if (!state.ReadConfiguration(document => CompletedScratch(document, completed) is { } p && p.LastAppliedCourierRun != completed.Run))
            return;
        state.Mutate(document =>
        {
            var scratch = CompletedScratch(document, completed);
            if (scratch is null || scratch.LastAppliedCourierRun == completed.Run)
                return;
            var draft = StowagePlanCatalog.Draft(document, scratch.Owner, scratch.Id);
            foreach (var line in completed.Lines)
            {
                var rule = draft.Rules.Find(r => r.Id == line.Line && r.Courier is not null);
                // A line re-routed or re-qualified during the run is a new instruction; what was delivered does not count against it.
                if (rule is null || line.Moved <= 0 || line.Route is not { } sent || !StillLandsAt(rule.Routing, sent.To) ||
                    rule.Courier!.SourceRetainerId != sent.From || !CourierPlanner.Exact(rule.Courier.Character, sent.Destination) ||
                    !CourierPlanner.Exact(rule.Courier.SourceCharacter ?? scratch.Owner, sent.Source) ||
                    rule.Quality != ItemQualityPolicy.Any && (rule.Quality == ItemQualityPolicy.HqOnly ? line.Nq : line.Hq))
                    continue;
                if (rule.TargetQuantity <= line.Moved)
                    draft.Rules.Remove(rule);
                else
                    rule.TargetQuantity -= line.Moved;
            }
            StowagePlanCatalog.Apply(document, scratch.Owner, draft);
            scratch.LastAppliedCourierRun = completed.Run;
        });
    }

    /// <summary>
    /// Whether a line's destinations still take a delivered shipment's container: bags only when the
    /// line has none, a retainer when the line lists it or fills Any space.
    /// </summary>
    internal static bool StillLandsAt(StowageRoutingPolicy routing, ulong? container) =>
        container is { } retainer
            ? routing.AnySpace || routing.PreferredRetainerIds.Contains(retainer)
            : routing.PreferredRetainerIds.Count == 0 && !routing.AnySpace;

    /// <summary>
    /// Whether Start is offered for a courier plan and, when offered but blocked, the short cause.
    /// Start is not offered while a run is active or unsettled: the run pane owns that state.
    /// Lines refused one by one block without a status; each line carries its own mark.
    /// </summary>
    internal CourierStartGate StartGate(QuartermasterRuntimeSnapshot runtime, CourierPlan? courierPlan)
    {
        if (Conductor is null)
            return new(true, false, "Courier not loaded", false);
        if (!runtime.Owner.HasStableIdentity)
            return new(true, false, "Character not identified", false);
        if (IsActive || IsUnresolved)
            return new(false, false, null, false);
        if (courierPlan is null || courierPlan.Trips.IsEmpty && courierPlan.Refusals.IsEmpty)
            return new(true, false, "Nothing to send", false);
        var refusals = courierPlan.Refusals;
        if (refusals.Any(refusal => refusal.Code == "CarrierNotConfigured"))
            return new(true, false, "No carrier chosen", true);
        if (refusals.Any(refusal => refusal.Code == "CarrierUnreachable"))
            return new(true, false, $"{runtime.State.Courier.Carrier?.CharacterName ?? "Carrier"} not running", false);
        if (refusals.Any(refusal => refusal.Code == "PlanningFailed"))
            return new(true, false, "Courier plan failed", false);
        if (refusals.Any(refusal => refusal.Code == "MeetingBellMissing"))
        {
            var missing = MissingBells(runtime, refusals.Where(refusal => refusal.Code == "MeetingBellMissing")
                .SelectMany(refusal => new[] { refusal.Source, refusal.Destination }).OfType<string>()).ToArray();
            return new(true, false, missing.Length == 0 ? "No meeting bell" : $"No meeting bell for {string.Join(", ", missing)}", true);
        }
        return new(true, refusals.IsEmpty, null, false);
    }

    internal void Start(QuartermasterRuntimeSnapshot runtime, StowagePlan stowagePlan)
    {
        Poll(force: true);
        if (run is { State: "Completed" } completed && settledRun != completed.Run ||
            !state.ReadConfiguration(document => document.StowagePlans.Any(p => p.Id == stowagePlan.Id && p.Revision == stowagePlan.Revision)))
            return;
        plan = null;
        var courierPlan = Plan(runtime, stowagePlan);
        if (Conductor is not { } conductor || !StartGate(runtime, courierPlan).CanStart)
            return;
        var settings = runtime.State.Courier;
        Launch(async () => await conductor.StartAsync(courierPlan!, settings).ConfigureAwait(false));
    }

    /// <summary>
    /// Clears a settled run. When the carrier's confirmed ledger equals the custody the pane showed,
    /// that view was the acknowledgement; otherwise the carrier's ledger replaces it on the pane and
    /// the next Clear acknowledges that.
    /// </summary>
    internal void Clear(Guid expectedRun, ImmutableArray<CustodyRow> shown)
    {
        if (Conductor is not { } conductor || IsActive || run is { State: "Completed" } completed && settledRun != completed.Run) return;
        var acknowledgement = ClearAcknowledgement;
        Launch(async () =>
        {
            try
            {
                var snapshot = await conductor.ClearAsync(expectedRun, acknowledgement).ConfigureAwait(false);
                ClearAcknowledgement = null;
                ClearLedger = [];
                return snapshot;
            }
            catch (CourierConductor.CustodyAcknowledgementRequired required) when (acknowledgement is null && SameRows(required.CarrierRows, shown))
            {
                var snapshot = await conductor.ClearAsync(expectedRun, required.Message).ConfigureAwait(false);
                ClearAcknowledgement = null;
                ClearLedger = [];
                return snapshot;
            }
            catch (CourierConductor.CustodyAcknowledgementRequired required)
            {
                ClearAcknowledgement = required.Message;
                ClearLedger = required.CarrierRows;
                return null;
            }
        });
    }

    private static bool SameRows(ImmutableArray<CustodyRow> a, ImmutableArray<CustodyRow> b) =>
        a.Length == b.Length &&
        a.OrderBy(row => (row.Item, row.Quality, row.Holder)).SequenceEqual(b.OrderBy(row => (row.Item, row.Quality, row.Holder)));

    /// <summary>Resumes an interrupted run from its journal; the journal owns the plan and settings.</summary>
    internal void Resume()
    {
        if (Conductor is not { } conductor || IsActive || Run?.CanResume!=true)
            return;
        Launch(async () => await conductor.StartAsync(new(Guid.Empty, 0, [], []), new()).ConfigureAwait(false));
    }

    private string? CarrierName() => state.ReadConfiguration(document => document.Courier.Carrier?.CharacterName);

    private void Launch(Func<Task<CourierSnapshot?>> start)
    {
        Error = null;
        ErrorDetail = null;
        var carrier = CarrierName();
        starting = Task.Run(async () =>
        {
            try
            {
                // Failed, interrupted and indeterminate runs are journaled; the run pane shows those.
                if (await start().ConfigureAwait(false) is { State: "Refused" } refused)
                    Fail(OutcomeLabel(refused.Code, carrier), $"{refused.Code}: {refused.Detail}");
            }
            catch (Exception exception) { Fail(FailureLabel(exception, carrier), exception.ToString()); }
            finally { polledAt = 0; }
        });
        polledAt = 0;
    }

    internal void Stop()
    {
        if (Conductor is not { } conductor)
            return;
        var carrier = CarrierName();
        _ = Task.Run(async () =>
        {
            try { await conductor.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { Fail(FailureLabel(exception, carrier), exception.ToString()); }
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
            catch (Exception exception) { Fail("Could not stop carrying", exception.ToString()); }
            finally { polledAt = 0; }
        });
    }

    /// <summary>A failed request in plain words; unknown failures stay generic and their detail goes to the log.</summary>
    private static string FailureLabel(Exception exception, string? carrier) => exception switch
    {
        CourierFault fault => OutcomeLabel(fault.Code, carrier),
        OperationCanceledException => "Courier unloaded",
        InvalidOperationException { Message: "CourierAlreadyRunning" } => "A courier run is already active",
        InvalidOperationException { Message: "RunNotClearable" } => "This run can no longer be cleared",
        InvalidOperationException { Message: "Settle the pending trade before clearing custody." } => "A hand-off is still unverified",
        InvalidOperationException { Message: "Carrier does not acknowledge this run's custody." } => $"{carrier ?? "The carrier"} does not report holding this run's items",
        InvalidOperationException { Message: "Carrier trade is unsettled." } => $"{carrier ?? "The carrier"} has an unconfirmed trade",
        _ => "Courier request failed",
    };

    /// <summary>A run outcome code in plain words.</summary>
    internal static string OutcomeLabel(string code, string? carrier)
    {
        var who = carrier ?? "The carrier";
        return code switch
        {
            "" or "Succeeded" => "Completed",
            "ConsolidationLimited" => "Delivered; consolidation incomplete",
            "StopRequested" or "StopBeforeBegin" or "StopBeforeTrade" or "StopBeforeLocalTrade" or "CancelledPreLock" => "Stopped",
            "PlanRefused" => "Some lines cannot be sent",
            "AutomationBusy" => "Another automation is running",
            "AutoRetainerBusy" => "AutoRetainer is still running",
            "BagSlotLimit" => "No free bag slot",
            "DestinationBagsFull" => "Destination bags full",
            "SenderStockShort" => "Items no longer at the source",
            "CustodyInsufficient" => $"{who} holds fewer items than expected",
            "CustodyRemaining" => $"{who} still holds items",
            "CarrierBagsMissing" => $"{who} did not report bags",
            "CarrierIdentityMismatch" => "A different character is carrying",
            "OwnerIdentityMismatch" or "SwitchIdentityMismatch" => "Wrong character logged in",
            "PeerLinkLost" or "CarrierEndpointAmbiguous" => $"Lost contact with {who}",
            "TradeStartNotAcknowledged" => $"{who} did not accept the trade",
            "TradeHoldLost" or "TradeHoldRefused" or "TradeHoldUnavailable" => "Trade hold unavailable",
            "HandOffDeltaMismatch" or "LedgerMismatch" or "SettlementDeltaMismatch" or "SettlementLedgerMismatch" or "CustodyMismatch"
                or "ReceiptMismatch" or "NotDeliveredDeltaMismatch" or "PlacementSettlementMismatch" or "PlacementSettlementIdentity"
                => "Hand-off did not match the plan",
            "TradeClosureUnproven" or "ResumeUnsettled" => "Hand-off unverified",
            _ when code.EndsWith("Deadline", StringComparison.Ordinal) => "Timed out",
            _ => "Run stopped",
        };
    }

    /// <summary>The unverified step's hand-off in plain words.</summary>
    internal static string UnverifiedLabel(CourierUnverified step) => step.Step switch
    {
        nameof(CourierPhase.HandOut) or nameof(CourierPhase.HandIn) => $"Trade {step.From} → {step.To} unverified",
        nameof(CourierPhase.Retrieve) => $"Retrieval for {step.From} unverified",
        nameof(CourierPhase.Deposit) => $"Deposit for {step.From} unverified",
        _ => $"{PhaseLabel(step.Step)} unverified",
    };

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
        nameof(CourierPhase.Consolidate) => "Consolidating",
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
            ErrorDetail ?? Error,current?.CanResume==true && !IsActive,current?.CanClear==true && !IsActive,
            view is {Custody.IsEmpty:false}?CourierConductor.Rows(view.Custody,settings.Carrier):current?.Custody??[],
            blocked,projection?.Refusals??[],[..settings.Owners.Select(o=>o.CharacterName)],
            [.. known.Where(o=>settings.Bells.ContainsKey(o.LocalContentId!.Value))
                .Select(o=>{var b=settings.Bells[o.LocalContentId!.Value];return new CourierBellBridgeTruth(o.CharacterName,o.HomeWorldName,b.Label,b.HasRoute,b.Trail?.Length??0);})],
            Link.RoutePoints>0,Link.RoutePoints);
    }

    // ---- Settings ---------------------------------------------------------------------------

    internal bool UpdateSettings(Func<CourierSettings, CourierSettings> change)
    {
        try
        {
            state.Mutate(document => { document.Courier = change(document.Courier); });
            plan = null;
            return true;
        }
        catch (Exception exception)
        {
            Fail("Courier settings not saved", exception.ToString());
            return false;
        }
    }

    /// <summary>The bell the logged-in character stands at, when it can be that character's meeting bell.</summary>
    internal MeetingBell? CapturableBell(QuartermasterRuntimeSnapshot runtime) =>
        runtime.Owner.HasStableIdentity && Link.StandingAt is { } bell && bell.World == runtime.Owner.HomeWorldId &&
        (Link.RoutePoints>0 || !(runtime.State.Courier.Bells.GetValueOrDefault(runtime.Owner.LocalContentId!.Value) is { } stored && stored.SamePlace(bell)))
            ? bell
            : null;

    internal void CaptureBell(QuartermasterRuntimeSnapshot runtime)
    {
        if (runtime.Owner.LocalContentId is not {} id || CapturableBell(runtime) is not { } shown ||
            Link.CaptureHere(id) is not {} bell || !shown.SamePlace(bell))
            return;
        if(UpdateSettings(settings => settings with { Bells = settings.Bells.SetItem(id, bell) }))Link.CancelRoute();
    }
}
