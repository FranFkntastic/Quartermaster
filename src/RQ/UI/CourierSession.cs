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

/// <summary>One container of a character as the destination picker shows it; Free and Has are null when it was never observed.</summary>
internal sealed record CourierContainer(ulong? Retainer, string Name, int Capacity, int? Free, int? Has,
    IReadOnlyList<(string Name, long Quantity)> Contents)
{
    internal int? Stacks => Free is { } free ? Capacity - free : null;
}

/// <param name="CarrierSourcing">True when this carrier can send stock it already holds (the claim command); null from
/// carriers built before it.</param>
/// <param name="OwnerRun">The run this client's owner journal holds (<see cref="Guid.Empty"/> for none); null from clients
/// built before it. A carrier forgets a held run its owner client no longer has.</param>
/// <param name="OwnerActiveRun">The owner journal's run while it is running; null when none runs or from clients built before
/// it. A carrier counts it as the owner still working on the run, so a long owner-only step never reads as owner silence.</param>
/// <param name="AdvertisesOwnerActiveRun">True when this client publishes <paramref name="OwnerActiveRun"/>; null from clients
/// built before it. A carrier pauses a held run on owner silence only for an owner that advertises it: an older owner's long
/// owner-only step would otherwise read as silence.</param>
public sealed record CourierPeer(OwnerScope Character, InventoryView? Bags,IReadOnlyList<OwnerScope>? Roster=null,
    bool? CarrierOptIn=null,IReadOnlyList<OwnerScope>? PairedOwners=null,bool? CarrierSourcing=null,Guid? OwnerRun=null,Guid? OwnerActiveRun=null,
    bool? AdvertisesOwnerActiveRun=null);

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

    /// <summary>Asks the carrier, over the authenticated peer link, to pair another character of this client's roster.</summary>
    Task<Result<bool>> PairOwnerAsync(OwnerScope carrier, OwnerScope owner, CancellationToken ct = default) =>
        Task.FromResult(Result<bool>.Fail("CarrierUnreachable", "No courier peer link is available."));
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
    private static readonly string[] SaddlebagNames = InventoryScanner.SaddlebagContainers.Select(bag => bag.ToString()).ToArray();
    private const int PlayerBagSlots = 140;
    private const int SaddlebagSlots = 140;
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
    // The run the current launch starts or resumes, when known before it journals (StartPlan's id, Resume's journal run).
    private Guid? startingRun;
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
        autoResume = new(this) { Enabled = () => state.ReadConfiguration(document => document.Courier.AutoResume) };
    }

    public ICourierLink Link { get; set; } = NullCourierLink.Instance;
    public CourierConductor? Conductor { get; set; }
    public CourierFollower? Follower { get; set; }

    /// <summary>Warning-level log sink for failure detail that never reaches the visible status.</summary>
    public Action<string>? Warn { get; set; }

    /// <summary>Information-level log sink.</summary>
    public Action<string>? Inform { get; set; }

    private readonly CourierAutoResume autoResume;
    // Serializes the check-then-launch of Resume, StartPlan and Clear across the UI, the IPC and the automatic resume.
    private readonly object launchGate = new();

    /// <summary>Whether the owner client could run a resume now (logged in, character loaded, not switching).</summary>
    public Func<bool> OwnerReady { set => autoResume.Ready = value; }

    /// <summary>The current run's automatic resume, for the run pane and the courier IPC; null when there is none to show.</summary>
    internal CourierAutoResumeView? AutoResume => autoResume.View;

    /// <summary>Runs the automatic resume's schedule; called every framework tick.</summary>
    public void TickAutoResume() => autoResume.Tick();

    /// <summary>Drops any pending automatic resume for good; called first on unload.</summary>
    public void DisposeAutoResume() => autoResume.Dispose();

    /// <summary>Drops a pending automatic resume (the run pane's Cancel auto-retry, or a stop of a run already halted); Resume stays available.</summary>
    internal void CancelAutoResume(string why = "Cancel auto-retry")
    {
        // Under the launch lock: an automatic attempt claims itself under it, so a cancel lands before or after its launch, never between.
        lock (launchGate) autoResume.Cancel(why);
    }

    /// <summary>Runs a check-then-stop decision under the launch lock, so no launch (manual or automatic) slips between them.</summary>
    internal T UnderLaunchGate<T>(Func<T> decide)
    {
        lock (launchGate) return decide();
    }

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

    /// <summary>The player containers the session reads from the player cache: bags, then the saddlebag pages.</summary>
    internal static IReadOnlyList<string> PlayerStorage { get; } = [.. PlayerBagNames, .. SaddlebagNames];

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

    /// <summary>Maps a World RowId to its FFXIV WorldRegionGroup RowId (1=JP, 2=NA, 3=EU, 4=OCE; 0=unknown).</summary>
    public Func<uint, uint>? WorldRegion { get; set; }

    /// <summary>
    /// Whether the configured carrier (or logged-in character when no carrier is set) can travel to
    /// <paramref name="character"/>'s home world region. JP/EU are ineligible from an NA carrier; OCE is reachable.
    /// </summary>
    internal bool IsRegionEligible(QuartermasterRuntimeSnapshot runtime, OwnerScope character)
    {
        if (WorldRegion is null)
            return true;
        var anchor = runtime.State.Courier.Carrier is { HasStableIdentity: true } carrier
            ? carrier.HomeWorldId ?? 0
            : runtime.Owner.HomeWorldId ?? 0;
        return CourierPlanner.CanReachRegion(WorldRegion(anchor), WorldRegion(character.HomeWorldId ?? 0));
    }

    /// <summary>
    /// Characters a courier line can deliver to from its source: from the logged-in character, every
    /// other reachable-region character; from another character, the logged-in one first, then the rest.
    /// </summary>
    internal IEnumerable<OwnerScope> Destinations(QuartermasterRuntimeSnapshot runtime, OwnerScope? source = null)
    {
        var others = Characters(runtime).Where(owner => !owner.Matches(runtime.Owner) && !IsCarrier(runtime, owner) && IsRegionEligible(runtime, owner));
        if (source is null || source.Matches(runtime.Owner))
            return others;
        var here = runtime.Owner.HasStableIdentity && !IsCarrier(runtime, runtime.Owner) && IsRegionEligible(runtime, runtime.Owner) ? new[] { runtime.Owner } : [];
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
        // The configured carrier belongs to another service account (and is excluded from local
        // Browsable): never let a stale local cache entry hide it from the Other accounts section.
        if (runtime.State.Courier.Carrier?.LocalContentId is { } carrierId &&
            runtime.Owner.LocalContentId != carrierId)
            ids.Remove(carrierId);
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

    /// <summary>
    /// Characters a courier line can add as a destination: every known character the carrier can
    /// reach, except the carrier, with at least one container observed on this client.
    /// </summary>
    internal IEnumerable<OwnerScope> DestinationCharacters(QuartermasterRuntimeSnapshot runtime) =>
        Characters(runtime).Where(character => !IsCarrier(runtime, character) && IsRegionEligible(runtime, character) &&
                                               Locations(runtime).Any(location => location.Character.Matches(character)));

    private (long Revision, Dictionary<(ulong?, uint, ItemQualityPolicy), IReadOnlyList<CourierContainer>> Views)? containers;

    /// <summary>
    /// A character's bags then retainers in game order with their space, their units of an item and
    /// everything they hold, from the last observation on this client. An unobserved container has no
    /// free count and no contents. Cached per runtime revision.
    /// </summary>
    internal IReadOnlyList<CourierContainer> Containers(QuartermasterRuntimeSnapshot runtime, OwnerScope character, uint itemId, ItemQualityPolicy quality)
    {
        if (containers is not { } cache || cache.Revision != runtime.Revision)
            containers = cache = (runtime.Revision, []);
        var key = (character.LocalContentId, itemId, quality);
        if (cache.Views.TryGetValue(key, out var hit))
            return hit;
        CourierContainer View(ulong? retainer, string name, int capacity, IEnumerable<(uint ItemId, string ItemName, uint Quantity, bool IsHq)>? items)
        {
            if (items is null)
                return new(retainer, name, capacity, null, null, []);
            var held = items.Where(item => item.ItemId > 0 && item.Quantity > 0).ToArray();
            var contents = held.GroupBy(item => item.ItemId)
                .Select(group => (Name: group.Select(item => item.ItemName).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? metadata(group.Key).Name,
                    Quantity: group.Sum(item => (long)item.Quantity)))
                .OrderByDescending(entry => entry.Quantity)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var has = held.Where(item => item.ItemId == itemId && (quality == ItemQualityPolicy.Any || item.IsHq == (quality == ItemQualityPolicy.HqOnly)))
                .Sum(item => (long)item.Quantity);
            return new(retainer, name, capacity, Math.Max(0, capacity - held.Length), (int)Math.Min(int.MaxValue, has), contents);
        }
        var capture = cachedBags(character);
        var views = new List<CourierContainer>
        {
            View(null, ContainerLabel(runtime, null), PlayerBagSlots,
                PlayerBagNames.All(name => capture.ObservedSources.Contains(name, StringComparer.Ordinal))
                    ? capture.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal))
                        .SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.ItemName, item.Quantity, item.IsHq))
                    : null),
        };
        foreach (var retainer in Retainers(runtime, character))
            views.Add(View(retainer.RetainerId, retainer.RetainerName, RetainerSlots,
                RetainerPageNames.All(name => retainer.ObservedSources.Contains(name, StringComparer.Ordinal))
                    ? retainer.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
                        .SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.ItemName, item.Quantity, item.IsHq))
                    : null));
        cache.Views[key] = views;
        return views;
    }

    private (long Revision, long External, Dictionary<(ulong? Character, string Scope), (int Capacity, int? Free)> Spaces)? spaces;

    /// <summary>
    /// A rail container's slots and how many are free, from the cache that last saw it whole: this
    /// client's player and retainer caches, or a sibling profile's for a character on another
    /// account. Free is null while any of its pages is unobserved; a saddlebag needs all four.
    /// </summary>
    internal (int Capacity, int? Free) ContainerSpace(QuartermasterRuntimeSnapshot runtime, OwnerScope character, BrowserScope scope, bool external)
    {
        var snapshot = ExternalStockCache?.Current ?? ExternalAccountSnapshot.Empty;
        if (spaces is not { } cache || cache.Revision != runtime.Revision || cache.External != snapshot.Revision)
            spaces = cache = (runtime.Revision, snapshot.Revision, []);
        var key = (character.LocalContentId, scope.Key);
        if (cache.Spaces.TryGetValue(key, out var hit))
            return hit;

        (int, int?) Pages(IReadOnlyList<string> pages, int capacity)
        {
            IEnumerable<string> observed;
            IEnumerable<(string BagName, IEnumerable<InventoryItem> Items)> bags;
            if (external)
            {
                var cached = snapshot.PlayerBags.GetValueOrDefault(character.LocalContentId ?? 0);
                observed = cached?.ObservedSources ?? [];
                bags = cached?.Bags.Select(bag => (bag.BagName, (IEnumerable<InventoryItem>)bag.Items)) ?? [];
            }
            else
            {
                var capture = cachedBags(character);
                observed = capture.ObservedSources;
                bags = capture.Bags.Select(bag => (bag.BagName, (IEnumerable<InventoryItem>)bag.Items));
            }
            if (!pages.All(observed.Contains))
                return (capacity, null);
            var used = bags.Where(bag => pages.Contains(bag.BagName, StringComparer.Ordinal))
                .SelectMany(bag => bag.Items).Count(item => item.ItemId > 0 && item.Quantity > 0);
            return (capacity, Math.Max(0, capacity - used));
        }

        (int Capacity, int? Free) space = scope.Kind switch
        {
            BrowserScopeKind.Player => Pages(PlayerBagNames, PlayerBagSlots),
            BrowserScopeKind.Saddlebag => Pages(SaddlebagNames, SaddlebagSlots),
            BrowserScopeKind.Retainer when scope.RetainerId is { } id =>
                (RetainerSlots, (external ? snapshot.Retainers.GetValueOrDefault(id) : runtime.Retainers.GetValueOrDefault(id)) is { } retainer
                    ? StowageRouter.FreeInventorySlots(retainer)
                    : null),
            _ => (0, null),
        };
        cache.Spaces[key] = space;
        return space;
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

    /// <summary>"Bags, Taffy-swordsman → Gryngle": the containers a line gives from and lands in.</summary>
    internal static string RouteSummary(QuartermasterRuntimeSnapshot runtime, CourierAllocation allocation)
    {
        string Containers(Func<CourierSpot, bool> side) => string.Join(", ", allocation.Places.Where(side)
            .Select(spot => ContainerLabel(runtime, spot.Retainer)).Distinct());
        return $"{Containers(spot => spot.Delta < 0)} → {Containers(spot => spot.Delta > 0)}";
    }

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
                built = CourierPlanner.Build(stowagePlan, rules, Locations(runtime), carrier, settings, metadata, testSlotCap, WorldRegion);
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

    private (long Revision, IReadOnlyList<CourierLocation> Locations)? located;
    private (long Revision, object Settings, Dictionary<Guid, (CourierLine Line, ItemQualityPolicy Quality, OwnerScope Owner, CourierAllocation Allocation)> Lines)? allocations;

    /// <summary>
    /// Where a courier line's item sits and where it would move, as the planner sees it: the listed
    /// places with their observed units, bounds and end states. Cached per runtime revision and line.
    /// </summary>
    internal CourierAllocation Allocation(QuartermasterRuntimeSnapshot runtime, OwnerScope planOwner, TargetPlanItem rule)
    {
        var line = rule.Courier ?? new CourierLine();
        var settings = runtime.State.Courier;
        if (allocations is not { } cache || cache.Revision != runtime.Revision || !ReferenceEquals(cache.Settings, settings))
            allocations = cache = (runtime.Revision, settings, []);
        if (cache.Lines.TryGetValue(rule.Id, out var hit) && ReferenceEquals(hit.Line, line) && hit.Quality == rule.Quality && hit.Owner.Matches(planOwner))
            return hit.Allocation;
        var allocation = CourierDistribution.Allocate(line, rule.ItemId, rule.Quality, planOwner, Locations(runtime),
            who => CourierPlanner.Listable(who, settings, WorldRegion), CourierPlanner.StackSize(metadata(rule.ItemId)));
        cache.Lines[rule.Id] = (line, rule.Quality, planOwner, allocation);
        return allocation;
    }

    private long rolesChecked = -1;

    /// <summary>
    /// Writes back courier lines still in an older shape (places predating roles, or roles), converted
    /// against observed stock (<see cref="CourierDistribution.Resolve"/>). A line waits while its plan
    /// owner's bags or any of its typed places are unobserved; until then it is converted afresh wherever
    /// it is solved. Checked once per runtime revision.
    /// </summary>
    internal void MigrateRoles(QuartermasterRuntimeSnapshot runtime)
    {
        if (runtime.Revision == rolesChecked || !runtime.Owner.HasStableIdentity)
            return;
        rolesChecked = runtime.Revision;
        var pending = runtime.State.PlanItems.Where(rule => rule.Courier is { IsLegacy: false, NeedsResolve: true }).ToArray();
        if (pending.Length == 0)
            return;
        var locations = Locations(runtime);
        var settings = runtime.State.Courier;
        bool Seen(OwnerScope who, ulong? retainer) => locations.Any(l => l.Retainer == retainer && l.Character.Matches(who));
        var converted = new Dictionary<(Guid Plan, Guid Rule), (CourierLine From, CourierLine To)>();
        foreach (var rule in pending)
        {
            var line = rule.Courier!;
            var owner = runtime.State.StowagePlans.FirstOrDefault(plan => plan.Id == rule.StowagePlanId)?.Owner;
            if (owner is null || !Seen(owner, null) || line.Places.Any(place => !place.Destination && place.After is not null && !Seen(place.Character, place.Retainer)))
                continue;
            converted[(rule.StowagePlanId, rule.Id)] = (line, CourierDistribution.Resolve(line, rule.ItemId, rule.Quality, owner, locations,
                who => CourierPlanner.Listable(who, settings, WorldRegion)));
        }
        if (converted.Count == 0)
            return;
        state.Mutate(document =>
        {
            foreach (var rule in document.PlanItems)
                if (converted.TryGetValue((rule.StowagePlanId, rule.Id), out var change) && Equals(rule.Courier, change.From))
                    rule.Courier = change.To;
        });
    }

    /// <summary>
    /// A courier line's moves as the plan table words them, with their colour (null: plain) and, when
    /// targets stay unmet, what is left on hover. Every solved move runs even while some are left.
    /// </summary>
    internal static (string Text, System.Numerics.Vector4? Color, string? Detail) Outcome(CourierAllocation allocation) =>
        (allocation.Moving, allocation.Short, allocation.Stuck) switch
        {
            ( > 0 and var moving, > 0 and var shortfall, _) => ($"{moving:N0} · {shortfall:N0} short", QuartermasterPalette.Amber,
                $"Moves {moving:N0}, everything the listed places can give.\nTargets still want {shortfall:N0} more."),
            ( > 0 and var moving, _, > 0 and var stuck) => ($"{moving:N0} · {stuck:N0} unplaced", QuartermasterPalette.Amber,
                $"Moves {moving:N0}, everything the listed places can take.\n{stuck:N0} above targets has no listed place to go."),
            ( > 0 and var moving, _, _) => (moving.ToString("N0"), null, null),
            (_, > 0 and var shortfall, _) => ($"Short {shortfall:N0}", QuartermasterPalette.Amber,
                $"Targets want {shortfall:N0} more than the listed places can give."),
            (_, _, > 0 and var stuck) => ($"Unplaced {stuck:N0}", QuartermasterPalette.Amber,
                $"{stuck:N0} above targets has no listed place to go."),
            _ when allocation.Line.Entries.Length < 2 => ("Add a place", QuartermasterPalette.Amber, null),
            _ => ("In place", QuartermasterPalette.Green, null),
        };

    /// <summary>Observed containers of every known character; unobserved ones are omitted. Cached per runtime revision.</summary>
    internal IReadOnlyList<CourierLocation> Locations(QuartermasterRuntimeSnapshot runtime)
    {
        if (located is { } cached && cached.Revision == runtime.Revision)
            return cached.Locations;
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
        located = (runtime.Revision, locations);
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
        "RegionUnreachable" => "Other region",
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
            "RegionUnreachable" => $"{string.Join(" or ", named.DefaultIfEmpty(destination))} is in another region that {carrier} cannot travel to",
            "CharacterNotKnown" when named.FirstOrDefault(name => Same(name, settings.Carrier?.CharacterName)) is { } isCarrier => $"{isCarrier} is the carrier",
            "CharacterNotKnown" => $"Bags of {source} or {destination} not observed",
            "MeetingBellMissing" => $"No meeting bell for {string.Join(" or ", MissingBells(runtime, named).DefaultIfEmpty(source))}",
            "ItemNotTradeable" => $"{refusal.Name} cannot be traded",
            "SourceUnknown" => $"Source container on {source} not observed",
            "DestinationUnknown" => $"Destination container on {destination} not observed",
            "ItemMetadataUnknown" => $"No item data for {refusal.Name}",
            "InvalidQuantity" => $"No quantity for {refusal.Name}",
            "UniqueConflict" => $"{refusal.Name} is unique and {destination} or {carrier} already holds one",
            "DestinationFull" => $"No room for {refusal.Name} on {destination}",
            "SourceShort" => $"Not enough {refusal.Name} on {source}",
            "BagSlotLimit" when Same(source, destination) => $"No free bag slot on {source}",
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
    /// <summary>
    /// The run being launched, while a launch is in flight and its run is known (StartPlan, Resume); null otherwise,
    /// including for a stowage-view start or a clear. IsStarting alone does not say which run is starting.
    /// </summary>
    internal Guid? StartingRun => IsStarting ? startingRun : null;
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
        }
        catch (Exception exception) { Fail("Courier run state unreadable", exception.ToString()); }
        try { carried = Follower?.View(); }
        catch (Exception exception) { Fail("Carrier state unreadable", exception.ToString()); }
    }

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
        if (!state.ReadConfiguration(document => document.StowagePlans.Any(p => p.Id == stowagePlan.Id && p.Revision == stowagePlan.Revision)))
            return;
        plan = null;
        var courierPlan = Plan(runtime, stowagePlan);
        if (Conductor is not { } conductor || !StartGate(runtime, courierPlan).CanStart)
            return;
        var settings = runtime.State.Courier;
        lock (launchGate)
        {
            if (IsActive)
                return;
            autoResume.Cancel("a new run starts");
            Launch(async () => await conductor.StartAsync(courierPlan!, settings).ConfigureAwait(false), watch: true);
        }
    }

    /// <summary>Clears the run, whatever it holds or awaits: the escape hatch. One click, no acknowledgement.</summary>
    /// <returns>Whether the clear was launched (false when the courier is not loaded or a run is active).</returns>
    internal bool Clear(Guid expectedRun, Action<CourierSnapshot?, Exception?>? settled = null, string clearedBy = CourierClearSource.User)
    {
        lock (launchGate)
        {
            if (Conductor is not { } conductor || IsActive) return false;
            autoResume.Cancel("Clear");
            Launch(async () => await conductor.ClearAsync(expectedRun, clearedBy).ConfigureAwait(false), settled);
            return true;
        }
    }

    /// <summary>
    /// Resumes an interrupted run from its journal; the journal owns the plan and settings. The run pane, the courier
    /// IPC and the automatic resume all come through here; one that is not automatic drops a pending automatic attempt.
    /// The check and the launch are atomic, so of two concurrent resumes the second is refused (false).
    /// </summary>
    /// <returns>Whether a resume was launched (false when active, busy draining, or not resumable).</returns>
    internal bool Resume(bool automatic = false)
    {
        lock (launchGate)
        {
            Poll(force: true);
            if (Conductor is not { } conductor || IsActive || conductor.IsBusy || Run?.CanResume!=true)
                return false;
            // An automatic attempt launches only if no cancel (Stop, Clear, Cancel auto-retry) has landed since it was due.
            if (automatic && !autoResume.TryClaim())
                return false;
            if (!automatic)
                autoResume.Cancel("Resume started");
            Launch(async () => await conductor.StartAsync(new(Guid.Empty, 0, [], []), new()).ConfigureAwait(false), quiet: true, run: Run?.Run, watch: true);
            return true;
        }
    }

    /// <summary>
    /// Starts a plan built outside the stowage view (the courier IPC). A clean, settled previous run
    /// is archived first. False when a run is already active. <paramref name="settled"/>, when given, hears how the
    /// start ended on the background thread: the conductor's last snapshot, or the exception it threw. A start that
    /// failed before the run began is not journaled, so this is the only place its reason survives.
    /// </summary>
    internal bool StartPlan(CourierPlan courierPlan, CourierSettings settings, Guid? clearFirst, Guid runId,
        Action<CourierSnapshot?, Exception?>? settled = null)
    {
        lock (launchGate)
        {
            Poll(force: true);
            if (Conductor is not { } conductor || IsActive)
                return false;
            autoResume.Cancel("a new run replaces it");
            Launch(async () =>
            {
                if (clearFirst is { } previous)
                    await conductor.ClearAsync(previous, CourierClearSource.Replace).ConfigureAwait(false);
                return await conductor.StartAsync(courierPlan, settings, runId).ConfigureAwait(false);
            }, settled, run: runId, watch: true);
            return true;
        }
    }

    private string? CarrierName() => state.ReadConfiguration(document => document.Courier.Carrier?.CharacterName);

    /// <summary>Why the last Resume could not start, in plain words: a quiet note beside Resume, not an error.</summary>
    internal string? ResumeNote { get; private set; }

    private void Launch(Func<Task<CourierSnapshot?>> start, Action<CourierSnapshot?, Exception?>? settled = null, bool quiet = false, Guid? run = null,
        bool watch = false)
    {
        if (watch)
            autoResume.Watch();
        // Set before the launch task exists, so StartingRun never pairs a new launch with the previous run's id.
        startingRun = run is { } id && id != Guid.Empty ? id : null;
        Error = null;
        ErrorDetail = null;
        ResumeNote = null;
        var carrier = CarrierName();
        Conductor?.PrepareLaunch();
        starting = Task.Run(async () =>
        {
            CourierSnapshot? outcome = null;
            Exception? fault = null;
            try
            {
                // Failed, interrupted and indeterminate runs are journaled; the run pane shows those.
                if ((outcome = await start().ConfigureAwait(false)) is { State: "Refused" } refused)
                    Fail(OutcomeLabel(refused.Code, carrier), $"{refused.Code}: {refused.Detail}");
            }
            catch (Exception exception)
            {
                fault = exception;
                if (quiet)
                {
                    ResumeNote = FailureLabel(exception, carrier);
                    Warn?.Invoke($"Courier: {ResumeNote}: {exception}");
                }
                else
                    Fail(FailureLabel(exception, carrier), exception.ToString());
            }
            finally
            {
                polledAt = 0;
                try { settled?.Invoke(outcome, fault); }
                catch (Exception exception) { Warn?.Invoke($"Courier: start listener failed ({exception.Message})"); }
            }
        });
        polledAt = 0;
    }

    internal void Stop()
    {
        if (Conductor is not { } conductor)
            return;
        // An escape hatch: it never waits on, and is never undone by, an automatic resume.
        lock (launchGate) autoResume.Cancel("Stop");
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

    /// <summary>The carrier pane's Clear: this client forgets the run it carries, whatever it holds or awaits.</summary>
    internal void ClearCarrier()
    {
        if (Follower is not { } follower)
            return;
        _ = Task.Run(async () =>
        {
            try { await follower.ForgetLocalAsync().ConfigureAwait(false); }
            catch (Exception exception) { Fail("Could not clear the carried run", exception.ToString()); }
            finally { polledAt = 0; }
        });
    }

    /// <summary>A failed request in plain words; unknown failures stay generic and their detail goes to the log.</summary>
    private static string FailureLabel(Exception exception, string? carrier) => exception switch
    {
        CourierFault fault => OutcomeLabel(fault.Code, carrier),
        OperationCanceledException => "Courier unloaded",
        InvalidOperationException { Message: "CourierAlreadyRunning" } => "A courier run is already active",
        InvalidOperationException { Message: "RunNotClearable" } => "This run was already cleared",
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
            "LifestreamBusy" => "Lifestream is still travelling",
            "BagSlotLimit" => "No free bag slot",
            "SourceGilShort" => "Source carries too little gil",
            "DestinationGilShort" => "Gil did not arrive",
            "CarrierGilCapExceeded" => $"{who} would pass the gil cap",
            "DestinationGilCapExceeded" => "Destination would pass the gil cap",
            "BagStockShort" => "Items no longer in bags",
            "DestinationBagsFull" => "Destination bags full",
            "SenderStockShort" => "Items no longer at the source",
            "CustodyInsufficient" => $"{who} holds fewer items than expected",
            "CustodyRemaining" => $"{who} still holds items",
            // The carrier's ledger already holds its own claimed stock for this run; nothing left its bags.
            "ClaimCustodyNotEmpty" => $"{who}'s stock is already reserved",
            "CarrierSourceUnsupported" => $"{who} cannot send its own stock",
            "CarrierStockShort" => $"{who} holds too few items",
            "CarrierGilShort" => $"{who} carries too little gil",
            "CarrierBagsMissing" => $"{who} did not report bags",
            "CarrierIdentityMismatch" => "A different character is carrying",
            "OwnerIdentityMismatch" or "SwitchIdentityMismatch" => "Wrong character logged in",
            "PeerLinkLost" or "CarrierEndpointAmbiguous" => $"Lost contact with {who}",
            "TradeStartNotAcknowledged" => $"{who} did not accept the trade",
            "TradeHoldLost" or "TradeHoldRefused" or "TradeHoldUnavailable" => "Trade hold unavailable",
            "HandOffDeltaMismatch" or "LedgerMismatch" or "SettlementDeltaMismatch" or "SettlementLedgerMismatch" or "CustodyMismatch"
                or "ReceiptMismatch" or "NotDeliveredDeltaMismatch" or "PlacementSettlementMismatch" or "PlacementSettlementIdentity" or "ClaimLedgerMismatch"
                => "Hand-off did not match the plan",
            "UnmanifestedTransfer" => "Unplanned items moved in the hand-off",
            "ManifestDeltaMismatch" or "ManifestDeltaOutOfRange" or "TradeRecordDisagrees" or "SettlementIdentityMismatch"
                or "SettlementEvidenceMissing" or "SettlementUnavailable" or "TradeManifestUnsettled" => "Hand-off did not match the plan",
            "TradeClosureUnproven" or "ResumeUnsettled" or "ResumePendingUnsettled" or "ResumeCarrierUnconfirmed" => "Hand-off unverified",
            "ResumeCustodyMismatch" => $"{who}'s held items differ from the run",
            "ResumeCheckpointMismatch" => "Bags changed since the last step",
            "HandInNeedsSettlement" => "Hand-in unproven",
            "OwnerReceiveStalledAfterSenderFinished" => $"Receive stalled after {who} finished",
            "OwnerUnloaded" => "Quartermaster unloaded mid-run",
            "HandOffEvidenceUnavailable" => "Bags unreadable after the trade",
            "SettleSentEvidenceMismatch" or "SettleSentUnproven"
                => $"{who}'s trade receipt does not show the full hand-in",
            "SettleSentBagsChanged" => $"{who}'s bags changed since the trade",
            "CarrierRunConflict" => $"{who} is holding another run",
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
        nameof(CourierPhase.Claim) => $"Reservation on {step.From} unverified",
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
        nameof(CourierPhase.ReturnDestinationHome) => "Returning home",
        nameof(CourierPhase.Finish) => "Finishing",
        nameof(CourierPhase.Claim) => "Reserving carrier stock",
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
