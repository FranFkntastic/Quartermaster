using System.Text.Json;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.Interop;

/// <summary>
/// Stock by source for every character this machine knows, and the owner's reserve items (reserve level,
/// counted spare stock, home retainers) for an external buy-list builder. Reads come from in-memory caches
/// rebuilt only when a cache revision moves; edits are validated on the caller's thread and applied to the
/// Listing Plan on the framework work queue, then announced through Changed like any other plan edit.
/// </summary>
public sealed partial class WorkbenchStockService
{
    public const string StockSourcesCapability = "stockSources.v1";
    public const string ReserveItemsCapability = "reserveItems.v1";
    public const string PlacementEditorCapability = "placementEditor.v1";
    public const string ReserveLevelsCapability = "reserveLevels.v1";
    /// <summary>Reserve items carry <c>remoteHomes</c> (homes on another character) and the placement editor can set them.</summary>
    public const string RemoteHomesCapability = "reserveRemoteHomes.v1";
    public const string ArrivalPlanCapability = "arrivalPlan.v1";
    /// <summary>The arrival plan's canonical <c>destinations</c> (every character the ranked homes reach) and AtCap.</summary>
    public const string ArrivalPlanV2Capability = "arrivalPlan.v2";
    public const string ArrivalPlanRequestSchema = "gooseworks-quartermaster-arrival-plan-request/v1";
    public const string ArrivalPlanSchema = "gooseworks-quartermaster-arrival-plan/v1";
    public const string StockSourcesRequestSchema = "gooseworks-quartermaster-stock-sources-request/v1";
    public const string StockSourcesSchema = "gooseworks-quartermaster-stock-sources/v1";
    public const string ReserveItemsSchema = "gooseworks-quartermaster-reserve-items/v1";
    public const string OpenPlacementSchema = "gooseworks-quartermaster-open-placement/v1";
    public const int MaxPlacementItems = 500;
    public const string ReserveLevelSchema = "gooseworks-quartermaster-reserve-level/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] PlayerBagNames = InventoryScanner.PlayerBags.Select(bag => bag.ToString()).ToArray();
    private static readonly string[] RetainerPageNames = InventoryScanner.RequiredRetainerContainers.Select(bag => bag.ToString()).ToArray();
    private const string PlayerCrystals = "Crystals";
    private const string RetainerCrystals = "RetainerCrystals";

    private readonly string providerInstanceId;
    private readonly Func<QuartermasterRuntimeSnapshot> runtime;
    private readonly PlayerInventoryCacheRepository playerInventory;
    private readonly Func<ExternalAccountSnapshot> external;
    private readonly StateRepository state;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly Action<string>? warn;
    private readonly Func<uint, int>? maxStackSize;
    private readonly object gate = new();
    private (long Runtime, long Players, long External, IReadOnlyList<SourceCharacter> Characters)? sources;
    private (long Runtime, string Json)? reserves;

    /// <summary>Opens the placement editor for the owner and items; set once the window exists. Runs on the framework thread.</summary>
    public Action<OwnerScope, IReadOnlyList<PlacementEditorItem>>? OpenPlacementEditor { get; set; }

    public WorkbenchStockService(
        string providerInstanceId,
        Func<QuartermasterRuntimeSnapshot> runtime,
        PlayerInventoryCacheRepository playerInventory,
        Func<ExternalAccountSnapshot> external,
        StateRepository state,
        IFrameworkWorkQueue workQueue,
        Action<string>? warn = null,
        Func<uint, int>? maxStackSize = null)
    {
        this.maxStackSize = maxStackSize;
        this.providerInstanceId = providerInstanceId;
        this.runtime = runtime;
        this.playerInventory = playerInventory;
        this.external = external;
        this.state = state;
        this.workQueue = workQueue;
        this.warn = warn;
    }

    public static IReadOnlyList<string> Capabilities { get; } =
        [StockSourcesCapability, ReserveItemsCapability, PlacementEditorCapability, ReserveLevelsCapability, RemoteHomesCapability, ArrivalPlanCapability,
            PlacementEditCapability, PlacementEditV2Capability, ArrivalPlanV2Capability];

    // ---- Stock sources ------------------------------------------------------------------------

    public string GetStockSources(string requestJson)
    {
        StockSourcesRequest? request = null;
        try { request = JsonSerializer.Deserialize<StockSourcesRequest>(requestJson, JsonOptions); }
        catch (JsonException) { }
        var wanted = (request?.ItemIds ?? []).Where(id => id > 0).ToHashSet();
        var current = runtime();
        var characters = Characters(current);
        return JsonSerializer.Serialize(new
        {
            schema = StockSourcesSchema,
            providerInstanceId,
            revision = current.Revision,
            generatedAtUtc = DateTime.UtcNow,
            characters = characters.Select(character => new
            {
                character.Owner.LocalContentId,
                character.Owner.HomeWorldId,
                character.Owner.CharacterName,
                character.Owner.HomeWorldName,
                isCurrent = character.Owner.Matches(current.Owner),
                character.OtherAccount,
                bags = character.Bags is { } bags ? new { bags.ObservedAtUtc, items = Items(bags.Items, wanted) } : null,
                retainers = character.Retainers?.Select(retainer => new
                {
                    retainer.RetainerId,
                    retainer.RetainerName,
                    retainer.ObservedAtUtc,
                    items = Items(retainer.Items, wanted),
                }),
            }),
        }, JsonOptions);
    }

    private static IEnumerable<object> Items(IReadOnlyDictionary<(uint ItemId, bool Hq), long> items, HashSet<uint> wanted) =>
        wanted.Count == 0
            ? []
            : items.Where(entry => wanted.Contains(entry.Key.ItemId) && entry.Value > 0)
                .Select(entry => new { itemId = entry.Key.ItemId, hq = entry.Key.Hq, quantity = entry.Value });

    private IReadOnlyList<SourceCharacter> Characters(QuartermasterRuntimeSnapshot current)
    {
        var externalSnapshot = external();
        lock (gate)
        {
            if (sources is { } cached && cached.Runtime == current.Revision && cached.Players == playerInventory.Revision &&
                cached.External == externalSnapshot.Revision)
                return cached.Characters;
        }

        var built = new Dictionary<ulong, SourceCharacter>();
        void Offer(SourceCharacter candidate)
        {
            var id = candidate.Owner.LocalContentId!.Value;
            // The same character can be cached by this client and a sibling profile; the newer view wins.
            if (!built.TryGetValue(id, out var existing) || candidate.NewestObservation > existing.NewestObservation)
                built[id] = candidate;
        }

        try
        {
            var localBags = playerInventory.Read(entries => entries
                .Where(entry => entry.Owner.LocalContentId is > 0 && !string.IsNullOrWhiteSpace(entry.Owner.CharacterName))
                .ToDictionary(entry => entry.Owner.LocalContentId!.Value, entry => (entry.Owner, Bags: PlayerBags(entry))));
            foreach (var owner in Owners(localBags.Values.Select(entry => entry.Owner), current.Retainers.Values))
                Offer(Character(owner, false, localBags.TryGetValue(owner.LocalContentId!.Value, out var local) ? local.Bags : null,
                    current.Retainers.Values));

            foreach (var owner in Owners(externalSnapshot.PlayerBags.Values.Select(entry => entry.Owner), externalSnapshot.Retainers.Values))
                Offer(Character(owner, true,
                    externalSnapshot.PlayerBags.TryGetValue(owner.LocalContentId!.Value, out var cachedBags) && owner.Matches(cachedBags.Owner)
                        ? PlayerBags(cachedBags) : null,
                    externalSnapshot.Retainers.Values));
        }
        catch (Exception exception)
        {
            warn?.Invoke($"Quartermaster could not index stock sources: {exception.Message}");
        }

        IReadOnlyList<SourceCharacter> result = built.Values
            .Where(character => character.Bags is not null || character.Retainers is not null)
            .OrderBy(character => character.Owner.Matches(current.Owner) ? 0 : 1)
            .ThenBy(character => character.Owner.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(character => character.Owner.HomeWorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (gate)
            sources = (current.Revision, playerInventory.Revision, externalSnapshot.Revision, result);
        return result;
    }

    private static IEnumerable<OwnerScope> Owners(IEnumerable<OwnerScope> players, IEnumerable<CachedRetainer> retainers)
    {
        var seen = new HashSet<ulong>();
        foreach (var owner in players.Concat(retainers.Select(retainer => retainer.Owner)))
            if (owner.LocalContentId is > 0 && !string.IsNullOrWhiteSpace(owner.CharacterName) && seen.Add(owner.LocalContentId.Value))
                yield return owner;
    }

    /// <summary>The main bags plus the crystal pouch when all four bags were observed; otherwise unknown.</summary>
    private static SourceContainer? PlayerBags(CachedPlayerInventory cached)
    {
        var observed = cached.ObservedSources;
        if (!PlayerBagNames.All(name => observed.Contains(name, StringComparer.Ordinal)))
            return null;
        var bags = cached.Bags.Where(bag => observed.Contains(bag.BagName, StringComparer.Ordinal) &&
                                            (PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal) || bag.BagName == PlayerCrystals))
            .ToArray();
        var main = bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal)).ToArray();
        var observedAt = main.Length == 0 ? cached.UpdatedAtUtc : main.Min(bag => bag.ObservedAtUtc);
        return new(observedAt, Sum(bags.SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.IsHq, item.Quantity))));
    }

    private static SourceCharacter Character(OwnerScope owner, bool otherAccount, SourceContainer? bags, IEnumerable<CachedRetainer> all)
    {
        var assigned = all.Where(retainer => retainer.Owner.Matches(owner) && retainer.IsCurrentlyAssigned is not false)
            .OrderBy(retainer => retainer.DisplayOrder ?? int.MaxValue)
            .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Retainer stock counts only when every assigned retainer's pages were observed: a missing
        // retainer would make the total a guess, and unknown never becomes zero.
        IReadOnlyList<SourceRetainer>? retainers = assigned.Length > 0 && assigned.All(retainer =>
            RetainerPageNames.All(name => retainer.ObservedSources.Contains(name, StringComparer.Ordinal)))
            ? assigned.Select(retainer =>
            {
                var bagsSeen = retainer.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal) ||
                                                          bag.BagName == RetainerCrystals && retainer.ObservedSources.Contains(RetainerCrystals, StringComparer.Ordinal))
                    .ToArray();
                var pages = bagsSeen.Where(bag => bag.BagName != RetainerCrystals && bag.ObservedAtUtc is not null).ToArray();
                var observedAt = pages.Length == 0 ? retainer.ObservedAtUtc : pages.Min(bag => bag.ObservedAtUtc!.Value);
                return new SourceRetainer(retainer.RetainerId, retainer.RetainerName, observedAt,
                    Sum(bagsSeen.SelectMany(bag => bag.Items).Select(item => (item.ItemId, item.IsHq, item.Quantity))));
            }).ToArray()
            : null;
        var newest = new[] { bags?.ObservedAtUtc ?? DateTime.MinValue }
            .Concat(retainers?.Select(retainer => retainer.ObservedAtUtc) ?? []).Max();
        return new(owner with { }, otherAccount, bags, retainers, newest);
    }

    private static IReadOnlyDictionary<(uint ItemId, bool Hq), long> Sum(IEnumerable<(uint ItemId, bool Hq, uint Quantity)> items) =>
        items.Where(item => item.ItemId > 0 && item.Quantity > 0)
            .GroupBy(item => (item.ItemId, item.Hq))
            .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));

    // ---- Reserve items ------------------------------------------------------------------------

    public string GetReserveItems()
    {
        var current = runtime();
        lock (gate)
            if (reserves is { } cached && cached.Runtime == current.Revision)
                return cached.Json;

        var owner = current.Owner;
        var plan = owner.HasStableIdentity ? ListingPlanCatalog.OwnerPlan(current.State, owner) : null;
        MarketRunEvaluation? evaluation = null;
        try { evaluation = plan is null ? null : MarketRunPlanner.Evaluate(plan, current.Browser); }
        catch (Exception exception) { warn?.Invoke($"Quartermaster could not count reserve stock: {exception.Message}"); }
        var json = JsonSerializer.Serialize(new
        {
            schema = ReserveItemsSchema,
            providerInstanceId,
            revision = current.Revision,
            owner,
            retainers = OwnerRetainers(current).Select(retainer => new { retainer.RetainerId, retainer.RetainerName }),
            items = (plan?.Reserves ?? []).Where(reserve => reserve.Enabled && reserve.ItemId > 0)
                .GroupBy(reserve => (reserve.ItemId, reserve.Quality))
                .Select(group => group.First())
                .Select(reserve =>
                {
                    var counted = evaluation?.Find(reserve.ItemId, reserve.Quality)?.SpareUnits;
                    return new
                    {
                        reserve.ItemId,
                        reserve.ItemName,
                        quality = reserve.Quality.ToString(),
                        reserve.ReserveUnits,
                        // Null when any accessible stock is unobserved: unknown, not zero.
                        countedUnits = counted is { IsKnown: true } known ? known.Value : (int?)null,
                        // Derived for v1 readers: the owner's homes in rank order, and the first other character's.
                        homes = reserve.Homes.Where(home => ReservePlacement.SameCharacter(home.Character, owner))
                            .Select(home => home.RetainerId).Distinct().ToArray(),
                        anySpace = false,
                        remoteHomes = RemoteHomesView(reserve, owner, current),
                        // Canonical: every home in global rank order across characters, with its cap.
                        placementHomes = reserve.Homes.Select((home, index) => new
                        {
                            rank = index + 1,
                            character = new { home.Character.LocalContentId, home.Character.HomeWorldId, home.Character.CharacterName, home.Character.HomeWorldName },
                            home.RetainerId,
                            home.MaxUnits,
                        }),
                    };
                }),
        }, JsonOptions);
        lock (gate)
            reserves = (current.Revision, json);
        return json;
    }

    // ---- Arrival plan -------------------------------------------------------------------------

    /// <summary>
    /// Where units arriving in an owner's bags would go now, by the same router a delivered stow uses
    /// (<see cref="PlacementRouter"/>): the reserve's ranked homes across every character, in rank order, each taking
    /// what its observed room and its cap allow. A home whose stock is unobserved takes nothing. Unplaced units stay
    /// in the bags. Room is as last observed, so the answer is a plan, not a promise; a delivered stow re-plans
    /// against the room it finds.
    /// </summary>
    public string PlanArrival(string requestJson)
    {
        object Rejected(string code, string message) => new { schema = ArrivalPlanSchema, providerInstanceId, status = "rejected", errorCode = code, message };
        ArrivalPlanRequest? request;
        try { request = JsonSerializer.Deserialize<ArrivalPlanRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return JsonSerializer.Serialize(Rejected("invalid_json", exception.Message), JsonOptions); }
        if (request is null)
            return JsonSerializer.Serialize(Rejected("invalid_json", "Request body is empty."), JsonOptions);
        if (request.Schema != ArrivalPlanRequestSchema)
            return JsonSerializer.Serialize(Rejected("unsupported_schema", $"Expected schema '{ArrivalPlanRequestSchema}'."), JsonOptions);
        if (request.Items is not { Count: > 0 and <= MaxPlacementItems } items)
            return JsonSerializer.Serialize(Rejected("invalid_items", $"Between 1 and {MaxPlacementItems} items are required."), JsonOptions);
        if (items.Any(item => item.ItemId == 0 || item.Quantity <= 0) || items.GroupBy(item => (item.ItemId, item.IsHq)).Any(group => group.Count() > 1))
            return JsonSerializer.Serialize(Rejected("invalid_items", "Each item needs an itemId and a positive quantity, once per quality."), JsonOptions);
        if (maxStackSize is null)
            return JsonSerializer.Serialize(Rejected("unavailable", "Item stack sizes are unavailable."), JsonOptions);

        var current = runtime();
        var asked = request.Owner is { LocalContentId: > 0, HomeWorldId: > 0 } named
            ? new OwnerScope { LocalContentId = named.LocalContentId, HomeWorldId = named.HomeWorldId, CharacterName = named.CharacterName }
            : current.Owner;
        if (!asked.HasStableIdentity)
            return JsonSerializer.Serialize(Rejected("missing_owner_scope", "No owner was named and no character is logged in."), JsonOptions);
        var plan = ListingPlanCatalog.OwnerPlan(current.State, asked);
        var owner = asked.Matches(current.Owner) ? current.Owner : plan?.Owner ?? asked;
        var now = DateTime.UtcNow;
        try
        {
            IReadOnlyList<PlacementHome> HomesOf(ArrivalPlanRequestItem item) => ReserveFor(plan, item.ItemId, item.IsHq)?.Homes ?? [];
            // One shared projection: items sharing a retainer share its room, on every character.
            var routes = PlacementRouter.Route(
                items.Select(item => (new PlacementItem(item.ItemId, $"Item {item.ItemId}", item.IsHq, item.Quantity), HomesOf(item))),
                current.Retainers, maxStackSize);
            static object Character(OwnerScope character) =>
                new { character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName };
            object Placed(IEnumerable<PlacementAllocation> allocations) => allocations
                .Select(allocation => new { allocation.RetainerId, allocation.RetainerName, units = allocation.Units }).ToArray();
            return JsonSerializer.Serialize(new
            {
                schema = ArrivalPlanSchema,
                providerInstanceId,
                status = "planned",
                revision = current.Revision,
                generatedAtUtc = now,
                owner = Character(owner),
                items = items.Select((item, index) =>
                {
                    var route = routes[index];
                    // Per character in the order the ranked homes first reach it.
                    var destinations = route.Allocations
                        .GroupBy(allocation => (allocation.Character.LocalContentId ?? 0, allocation.Character.CharacterName))
                        .Select(group => (Character: group.First().Character, Allocations: group.ToArray()))
                        .ToArray();
                    var local = destinations.FirstOrDefault(entry => ReservePlacement.SameCharacter(entry.Character, owner));
                    var homes = HomesOf(item);
                    var firstRemote = homes.FirstOrDefault(home => !ReservePlacement.SameCharacter(home.Character, owner))?.Character;
                    var remote = firstRemote is null ? default : destinations.FirstOrDefault(entry => ReservePlacement.SameCharacter(entry.Character, firstRemote));
                    return new
                    {
                        item.ItemId,
                        item.IsHq,
                        item.Quantity,
                        // Canonical (v2): every character the ranked homes send units to.
                        destinations = destinations.Select(entry => new
                        {
                            character = Character(entry.Character),
                            isOwner = ReservePlacement.SameCharacter(entry.Character, owner),
                            units = entry.Allocations.Sum(allocation => allocation.Units),
                            retainers = Placed(entry.Allocations),
                        }),
                        // Derived for v1 readers: the owner's share, and the first other character the homes reach.
                        local = new
                        {
                            units = local.Allocations?.Sum(allocation => allocation.Units) ?? 0,
                            retainers = Placed(local.Allocations ?? []),
                        },
                        remote = firstRemote is null ? null : new
                        {
                            character = Character(firstRemote),
                            units = remote.Allocations?.Sum(allocation => allocation.Units) ?? 0,
                            retainers = Placed(remote.Allocations ?? []),
                        },
                        unplaced = route.Unplaced,
                        // NoHome: no home anywhere. AtCap: a cap stopped it while that home still had room.
                        // NoRoom: the homes' observed room is short (a home with unobserved stock takes nothing).
                        unplacedReason = route.UnplacedReason,
                        unobservedHomes = route.Unplaced <= 0 || route.UnobservedHomes.Count == 0
                            ? null
                            : route.UnobservedHomes.Select(home => new { character = Character(home.Character), home.RetainerId }).ToArray(),
                    };
                }),
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            warn?.Invoke($"Quartermaster could not plan an arrival: {exception}");
            return JsonSerializer.Serialize(Rejected("planning_failed", exception.Message), JsonOptions);
        }
    }

    /// <summary>The enabled reserve rule for an arriving item variant (a rule of either quality counts for both).</summary>
    internal static ListingReserveRule? ReserveFor(ListingPlan? plan, uint itemId, bool isHq) =>
        plan?.Reserves.FirstOrDefault(reserve => reserve.Enabled && reserve.ItemId == itemId &&
            (reserve.Quality == ItemQualityPolicy.Any || reserve.Quality == ItemQualityPolicy.HqOnly == isHq));

    /// <summary>The v1 <c>remoteHomes</c> view: the first other character the ranked homes reach, and its homes there.</summary>
    private static object? RemoteHomesView(ListingReserveRule reserve, OwnerScope owner, QuartermasterRuntimeSnapshot current)
    {
        if (reserve.Homes.FirstOrDefault(home => !ReservePlacement.SameCharacter(home.Character, owner))?.Character is not { } character)
            return null;
        return new
        {
            character = new { character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName },
            retainers = reserve.Homes.Where(home => ReservePlacement.SameCharacter(home.Character, character)).Select(home => new
            {
                retainerId = home.RetainerId,
                // Null while this client has not observed the retainer under that character.
                retainerName = current.Retainers.TryGetValue(home.RetainerId, out var retainer) && ReservePlacement.SameCharacter(retainer.Owner, character)
                    ? retainer.RetainerName
                    : null,
            }),
        };
    }

    private static IEnumerable<CachedRetainer> OwnerRetainers(QuartermasterRuntimeSnapshot current) => current.Retainers.Values
        .Where(retainer => retainer.Owner.Matches(current.Owner) && retainer.IsCurrentlyAssigned is not false)
        .OrderBy(retainer => retainer.DisplayOrder ?? int.MaxValue)
        .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase);

    // ---- Edits --------------------------------------------------------------------------------

    /// <summary>
    /// Opens the placement editor for the current character on the supplied items. Validated here; the
    /// window opens on the framework thread.
    /// </summary>
    public string OpenPlacement(string requestJson)
    {
        OpenPlacementRequest? request;
        try { request = JsonSerializer.Deserialize<OpenPlacementRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Ack(string.Empty, "rejected", "invalid_json", exception.Message); }
        if (request is null)
            return Ack(string.Empty, "rejected", "invalid_json", "Request body is empty.");
        if (request.Schema != OpenPlacementSchema)
            return Ack(request.RequestId, "rejected", "unsupported_schema", $"Expected {OpenPlacementSchema}.");
        if (OpenPlacementEditor is not { } open)
            return Ack(request.RequestId, "rejected", "unavailable", "The placement editor is not available.");
        var current = runtime();
        var items = new List<PlacementEditorItem>();
        foreach (var item in request.Items ?? [])
        {
            if (Validate(request.ProviderInstanceId, request.Owner, item.ItemId, item.Quality, current, out var quality) is { } error)
                return Ack(request.RequestId, "rejected", error.Code, error.Message);
            if (items.All(existing => existing.ItemId != item.ItemId || existing.Quality != quality))
                items.Add(new(item.ItemId, RealItemName(item.ItemId, item.ItemName) ?? PlaceholderName(item.ItemId), quality));
        }
        if (items.Count == 0)
            return Ack(request.RequestId, "rejected", "invalid_item", "Name at least one item.");
        if (items.Count > MaxPlacementItems)
            return Ack(request.RequestId, "rejected", "too_many_items", $"At most {MaxPlacementItems:N0} items.");

        var owner = current.Owner with { };
        workQueue.Enqueue(() => open(owner, items));
        return Ack(request.RequestId, "queued", null, "Placement editor queued for the framework thread.");
    }

    public string SetReserveLevel(string requestJson)
    {
        ReserveLevelRequest? request;
        try { request = JsonSerializer.Deserialize<ReserveLevelRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Ack(string.Empty, "rejected", "invalid_json", exception.Message); }
        if (request is null)
            return Ack(string.Empty, "rejected", "invalid_json", "Request body is empty.");
        if (request.Schema != ReserveLevelSchema)
            return Ack(request.RequestId, "rejected", "unsupported_schema", $"Expected {ReserveLevelSchema}.");
        var current = runtime();
        if (Validate(request.ProviderInstanceId, request.Owner, request.ItemId, request.Quality, current, out var quality) is { } error)
            return Ack(request.RequestId, "rejected", error.Code, error.Message);
        var units = Math.Clamp(request.ReserveUnits, 0, ListingReserveRule.MaxReserveUnits);

        var owner = current.Owner with { };
        workQueue.Enqueue(() => EditReserve(owner, request.ItemId, request.ItemName, quality, reserve =>
        {
            if (reserve.ReserveUnits == units) return false;
            reserve.ReserveUnits = units;
            return true;
        }, create: units > 0));
        return Ack(request.RequestId, "queued", null, "Reserve level queued for the framework thread.");
    }

    private (string Code, string Message)? Validate(string provider, RequestOwner? requestOwner, uint itemId, string qualityText,
        QuartermasterRuntimeSnapshot current, out ItemQualityPolicy quality)
    {
        quality = ItemQualityPolicy.NqOnly;
        if (provider != providerInstanceId)
            return ("provider_mismatch", "The request names another Quartermaster instance.");
        if (!current.Owner.HasStableIdentity || requestOwner is null ||
            requestOwner.LocalContentId != current.Owner.LocalContentId || requestOwner.HomeWorldId != current.Owner.HomeWorldId)
            return ("owner_mismatch", "The request owner is not the current character.");
        if (itemId == 0)
            return ("invalid_item", "Choose an item.");
        if (!Enum.TryParse(qualityText, ignoreCase: false, out quality) || quality == ItemQualityPolicy.Any)
            return ("invalid_quality", "Quality must be NqOnly or HqOnly.");
        return null;
    }

    /// <summary>
    /// Edits the owner's enabled reserve rule for one item and quality, creating the rule (and the
    /// Listing Plan) when <paramref name="create"/> allows it. A rule left with no level and no home is
    /// removed so a cleared home leaves nothing behind. Runs on the framework thread.
    /// </summary>
    private void EditReserve(OwnerScope owner, uint itemId, string itemName, ItemQualityPolicy quality,
        Func<ListingReserveRule, bool> edit, bool create)
    {
        try
        {
            var plan = state.Read(document => ListingPlanCatalog.OwnerPlan(document, owner));
            var existing = plan?.Reserves.FirstOrDefault(reserve => reserve.Enabled && reserve.ItemId == itemId && reserve.Quality == quality);
            if (existing is null && !create)
                return;
            if (existing is not null && !edit(ListingPlanCatalog.Copy(existing)))
                return;
            state.Mutate(StateChangeKind.Listings, document =>
            {
                var target = ListingPlanCatalog.OwnerPlan(document, owner);
                if (target is null)
                {
                    target = new ListingPlan { Owner = owner with { } };
                    document.ListingPlans.Add(target);
                }
                var reserve = target.Reserves.FirstOrDefault(rule => rule.Enabled && rule.ItemId == itemId && rule.Quality == quality);
                if (reserve is null)
                {
                    reserve = new ListingReserveRule { ItemId = itemId, ItemName = itemName, Quality = quality };
                    target.Reserves.Add(reserve);
                }
                if (string.IsNullOrWhiteSpace(reserve.ItemName))
                    reserve.ItemName = itemName;
                edit(reserve);
                if (reserve.ReserveUnits == 0 && !reserve.HasAnyHome)
                    target.Reserves.Remove(reserve);
                target.UpdatedAtUtc = DateTime.UtcNow;
                target.Revision = checked(target.Revision + 1);
                document.Schema = "gooseworks-quartermaster-state/v5";
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warn?.Invoke($"Quartermaster could not save the reserve edit for {itemName}: {exception.Message}");
        }
    }

    private string Ack(string requestId, string status, string? errorCode, string message) =>
        JsonSerializer.Serialize(new ReserveEditAcknowledgement
        {
            ProviderInstanceId = providerInstanceId,
            RequestId = requestId,
            Status = status,
            ErrorCode = errorCode,
            Message = message,
        }, JsonOptions);

    public sealed record PlacementEditorItem(uint ItemId, string ItemName, ItemQualityPolicy Quality);

    private sealed record SourceContainer(DateTime ObservedAtUtc, IReadOnlyDictionary<(uint ItemId, bool Hq), long> Items);

    private sealed record SourceRetainer(ulong RetainerId, string RetainerName, DateTime ObservedAtUtc,
        IReadOnlyDictionary<(uint ItemId, bool Hq), long> Items);

    private sealed record SourceCharacter(OwnerScope Owner, bool OtherAccount, SourceContainer? Bags,
        IReadOnlyList<SourceRetainer>? Retainers, DateTime NewestObservation);
}
