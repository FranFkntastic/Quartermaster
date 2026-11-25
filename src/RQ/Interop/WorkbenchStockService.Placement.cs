using System.Text.Json;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;
using RQ.UI;

namespace RQ.Interop;

/// <summary>
/// Placement over IPC (capabilities <c>placementEdit.v1</c> and <c>placementEdit.v2</c>): <see cref="GetPlacement"/>
/// returns what the Placement window's matrix shows, and <see cref="EditPlacement"/> applies one toolbar action through
/// <see cref="ApplyPlacementEdit"/>, the same path the window's edits take. v2: an item's homes are one ranked list
/// across characters (each an explicit retainer with an optional cap); there is no "any space" home.
/// </summary>
public sealed partial class WorkbenchStockService
{
    public const string PlacementEditCapability = "placementEdit.v1";
    public const string PlacementEditV2Capability = "placementEdit.v2";
    public const string PlacementRequestSchema = "gooseworks-quartermaster-placement-request/v1";
    public const string PlacementSchema = "gooseworks-quartermaster-placement/v1";
    public const string PlacementEditSchema = "gooseworks-quartermaster-placement-edit/v1";
    public const string PlacementEditAckSchema = "gooseworks-quartermaster-placement-edit-ack/v1";

    private const int PlayerBagSlots = 140;
    private (long Runtime, long Players, long State, string Json)? placement;

    /// <summary>Characters the placement editor can show besides the owner; set once the courier exists.</summary>
    public Func<QuartermasterRuntimeSnapshot, IEnumerable<OwnerScope>>? PlacementCharacters { get; set; }

    /// <summary>Another character's observed stock (null while unobserved); set once the courier exists.</summary>
    public Func<QuartermasterRuntimeSnapshot, OwnerScope, BrowserProjection?>? CharacterStock { get; set; }

    /// <summary>
    /// Runs work on the framework thread and returns its result (inline when already there). Both placement channels
    /// go through it, so the courier's caches are only touched, and placement saves and their Changed notifications
    /// only happen, on the framework thread whichever thread the caller used. Unset (tests), work runs inline.
    /// </summary>
    public Func<Func<string>, string>? RunOnFramework { get; set; }

    /// <summary>The game's name for an item; may return the <c>Item {id}</c> placeholder when it cannot resolve one.</summary>
    public Func<uint, string>? ItemNames { get; set; }

    private string OnFramework(Func<string> work) => RunOnFramework is { } run ? run(work) : work();

    internal static string PlaceholderName(uint itemId) => $"Item {itemId}";

    private static bool IsPlaceholder(uint itemId, string? name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), PlaceholderName(itemId), StringComparison.Ordinal);

    /// <summary>The caller's name, else the game's; null when neither is a real name (the placeholder is display-only).</summary>
    private string? RealItemName(uint itemId, string? given)
    {
        if (!IsPlaceholder(itemId, given))
            return given!.Trim();
        string? resolved = null;
        try { resolved = ItemNames?.Invoke(itemId); }
        catch (Exception exception) { warn?.Invoke($"Quartermaster could not resolve the name of item {itemId}: {exception.Message}"); }
        return IsPlaceholder(itemId, resolved) ? null : resolved!.Trim();
    }

    /// <summary>
    /// The same character for placement: by content id when both have one (a world transfer keeps it), else by name.
    /// </summary>
    private static bool SameCharacter(OwnerScope left, OwnerScope right) => ReservePlacement.SameCharacter(left, right);

    // ---- Read ---------------------------------------------------------------------------------

    public string GetPlacement(string requestJson) => OnFramework(() => GetPlacementCore(requestJson));

    private string GetPlacementCore(string requestJson)
    {
        object Rejected(string code, string message) => new { schema = PlacementSchema, providerInstanceId, status = "rejected", errorCode = code, message };
        PlacementRequest? request = null;
        if (!string.IsNullOrWhiteSpace(requestJson))
        {
            try { request = JsonSerializer.Deserialize<PlacementRequest>(requestJson, JsonOptions); }
            catch (JsonException exception) { return JsonSerializer.Serialize(Rejected("invalid_json", exception.Message), JsonOptions); }
        }
        if (request is not null && !string.IsNullOrEmpty(request.Schema) && request.Schema != PlacementRequestSchema)
            return JsonSerializer.Serialize(Rejected("unsupported_schema", $"Expected schema '{PlacementRequestSchema}'."), JsonOptions);
        List<(uint ItemId, ItemQualityPolicy Quality)>? keys = null;
        if (request?.Items is { } requested)
        {
            if (requested.Count > MaxPlacementItems)
                return JsonSerializer.Serialize(Rejected("too_many_items", $"At most {MaxPlacementItems:N0} items."), JsonOptions);
            keys = [];
            foreach (var item in requested)
            {
                if (item.ItemId == 0)
                    return JsonSerializer.Serialize(Rejected("invalid_item", "Each item needs an itemId."), JsonOptions);
                if (!Enum.TryParse<ItemQualityPolicy>(item.Quality, ignoreCase: false, out var quality) || quality == ItemQualityPolicy.Any)
                    return JsonSerializer.Serialize(Rejected("invalid_quality", $"Item {item.ItemId}: quality must be NqOnly or HqOnly."), JsonOptions);
                if (!keys.Contains((item.ItemId, quality)))
                    keys.Add((item.ItemId, quality));
            }
        }

        var current = runtime();
        if (!current.Owner.HasStableIdentity)
            return JsonSerializer.Serialize(Rejected("missing_owner_scope", "No character is logged in."), JsonOptions);
        var (stateRevision, plan) = state.Read(document => (document.Revision,
            ListingPlanCatalog.OwnerPlan(document, current.Owner) is { } found ? Snapshot(found) : null));
        if (keys is null)
        {
            lock (gate)
                if (placement is { } cached && cached.Runtime == current.Revision && cached.Players == playerInventory.Revision && cached.State == stateRevision)
                    return cached.Json;
        }

        string json;
        try { json = BuildPlacement(current, plan, keys); }
        catch (Exception exception)
        {
            warn?.Invoke($"Quartermaster could not build the placement view: {exception}");
            return JsonSerializer.Serialize(Rejected("unavailable", exception.Message), JsonOptions);
        }
        if (keys is null)
            lock (gate)
                placement = (current.Revision, playerInventory.Revision, stateRevision, json);
        return json;
    }

    private static ListingPlan Snapshot(ListingPlan plan) => new()
    {
        Id = plan.Id,
        Revision = plan.Revision,
        Owner = plan.Owner with { },
        Reserves = plan.Reserves.Select(ListingPlanCatalog.Copy).ToList(),
        UpdatedAtUtc = plan.UpdatedAtUtc,
    };

    private string BuildPlacement(QuartermasterRuntimeSnapshot current, ListingPlan? plan, IReadOnlyList<(uint ItemId, ItemQualityPolicy Quality)>? keys)
    {
        var owner = current.Owner;
        var now = DateTime.UtcNow;
        var reserves = (plan?.Reserves ?? []).Where(reserve => reserve.Enabled && reserve.ItemId > 0)
            .GroupBy(reserve => (reserve.ItemId, reserve.Quality))
            .ToDictionary(group => group.Key, group => group.First());
        var rows = (keys ?? reserves.Keys.OrderBy(key => reserves[key].ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(key => key.ItemId).ToList())
            .Select(key => (Key: key, Rule: reserves.GetValueOrDefault(key)))
            .ToArray();
        // Every per-character map is keyed by content id, the identity the character list is unique on.
        var characters = PlacementCharacterList(current, plan);
        var bags = playerInventory.Read(entries => entries
            .Where(entry => entry.Owner.LocalContentId is > 0)
            .GroupBy(entry => entry.Owner.LocalContentId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAtUtc).First()));
        var stocks = characters.ToDictionary(character => character.LocalContentId!.Value,
            character => SameCharacter(character, owner) ? current.Browser : CharacterStock?.Invoke(current, character));
        // A stored home names its character as it was then; report it as the listed (current) character.
        OwnerScope Listed(OwnerScope stored) => characters.FirstOrDefault(character => SameCharacter(character, stored)) ?? stored;

        object Character(OwnerScope character) => new
        {
            character.LocalContentId,
            character.HomeWorldId,
            character.CharacterName,
            character.HomeWorldName,
        };
        static long? Age(DateTime? observed, DateTime now) => observed is { } at ? (long)Math.Max(0, (now - at).TotalSeconds) : null;
        static object Ranked(IEnumerable<ulong> ids) => ids.Distinct().Select((id, index) => new { rank = index + 1, retainerId = id }).ToArray();
        static IReadOnlyList<PlacementHome> HomesOf(ListingReserveRule? rule) => rule?.Homes ?? [];

        return JsonSerializer.Serialize(new
        {
            schema = PlacementSchema,
            providerInstanceId,
            status = "ok",
            revision = current.Revision,
            generatedAtUtc = now,
            owner = Character(owner),
            characters = characters.Select(character =>
            {
                var isOwner = SameCharacter(character, owner);
                var bag = BagView(bags.GetValueOrDefault(character.LocalContentId!.Value), character);
                // The character's assigned retainers in game order, then any retainer an item names there.
                var named = rows.SelectMany(row => HomesOf(row.Rule))
                    .Where(home => SameCharacter(home.Character, character))
                    .Select(home => home.RetainerId);
                return new
                {
                    character.LocalContentId,
                    character.HomeWorldId,
                    character.CharacterName,
                    character.HomeWorldName,
                    isOwner,
                    offersAnySpace = false,
                    bags = new
                    {
                        observed = bag.Observed,
                        freeSlots = bag.FreeSlots,
                        observedAtUtc = bag.ObservedAtUtc,
                        observedAgeSeconds = Age(bag.ObservedAtUtc, now),
                    },
                    retainers = PlacementColumns(current, character, named).Select(column => new
                    {
                        column.RetainerId,
                        retainerName = column.Retainer?.RetainerName,
                        assigned = column.Retainer is { IsCurrentlyAssigned: not false },
                        stockObserved = column.FreeSlots is not null,
                        freeSlots = column.FreeSlots,
                        observedAtUtc = column.ObservedAtUtc,
                        observedAgeSeconds = Age(column.ObservedAtUtc, now),
                    }),
                };
            }),
            items = rows.Select(row =>
            {
                var rule = row.Rule;
                var homes = HomesOf(rule);
                var local = homes.Where(home => SameCharacter(home.Character, owner)).Select(home => home.RetainerId).ToArray();
                // Compatibility (v1): the first other character the ranked list reaches.
                var remoteCharacter = homes.FirstOrDefault(home => !SameCharacter(home.Character, owner))?.Character;
                return new
                {
                    itemId = row.Key.ItemId,
                    // The rule's name, else the game's; null only when neither is known.
                    itemName = RealItemName(row.Key.ItemId, rule?.ItemName),
                    quality = row.Key.Quality.ToString(),
                    hasRule = rule is not null,
                    reserveUnits = rule?.ReserveUnits ?? 0,
                    // Canonical (v2): every home in global rank order, across characters, with its cap.
                    homes = homes.Select((home, index) => new
                    {
                        rank = index + 1,
                        character = Character(Listed(home.Character)),
                        home.RetainerId,
                        home.MaxUnits,
                    }),
                    // Derived for v1 readers: the owner's homes and the first other character's, each ranked within itself.
                    local = new
                    {
                        homes = Ranked(local),
                        anySpace = false,
                        anySpaceRank = (int?)null,
                        consolidate = rule?.Consolidate ?? false,
                    },
                    remote = remoteCharacter is null ? null : new
                    {
                        character = Character(Listed(remoteCharacter)),
                        homes = Ranked(homes.Where(home => SameCharacter(home.Character, remoteCharacter)).Select(home => home.RetainerId)),
                    },
                    // Per shown character: units in its bags (null while unobserved) and in each retainer whose stock
                    // was observed (a retainer absent here holds none when its stockObserved is true, unknown otherwise).
                    held = characters.Select(character =>
                    {
                        var stock = stocks[character.LocalContentId!.Value];
                        var observedBags = BagView(bags.GetValueOrDefault(character.LocalContentId.Value), character).Observed;
                        var holdings = SameCharacter(character, owner)
                            ? PlacementMatrixData.Holdings(current, row.Key.ItemId, row.Key.Quality)
                            : PlacementMatrixData.Holdings(current, row.Key.ItemId, row.Key.Quality, character, stock);
                        return new
                        {
                            character.LocalContentId,
                            bags = observedBags
                                ? (SameCharacter(character, owner)
                                    ? PlacementMatrixData.BagHoldings(current, row.Key.ItemId, row.Key.Quality)
                                    : PlacementMatrixData.BagHoldings(current, row.Key.ItemId, row.Key.Quality, character, stock))
                                : (int?)null,
                            retainers = holdings.Where(entry => entry.Value > 0)
                                .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key)
                                .Select(entry => new { retainerId = entry.Key, quantity = entry.Value }),
                        };
                    }),
                };
            }),
        }, JsonOptions);
    }

    /// <summary>
    /// The characters the editor can show: the owner first, then (by name) every character the courier can deliver
    /// to and every character a reserve item already has homes on. One entry per content id: a character that
    /// changed worlds is listed once, as the courier knows it now, ahead of the world its stored homes still carry.
    /// </summary>
    private IReadOnlyList<OwnerScope> PlacementCharacterList(QuartermasterRuntimeSnapshot current, ListingPlan? plan)
    {
        var owner = current.Owner;
        IEnumerable<OwnerScope> offered = [];
        try { offered = PlacementCharacters?.Invoke(current)?.ToArray() ?? []; }
        catch (Exception exception) { warn?.Invoke($"Quartermaster could not list placement characters: {exception.Message}"); }
        var homed = (plan?.Reserves ?? []).Where(reserve => reserve.Enabled)
            .SelectMany(reserve => reserve.Homes)
            .Select(home => home.Character);
        var others = offered.Concat(homed)
            .Where(character => character.HasStableIdentity && !SameCharacter(character, owner))
            .DistinctBy(character => character.LocalContentId!.Value)
            .OrderBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(character => character.HomeWorldName, StringComparer.OrdinalIgnoreCase);
        return [owner with { }, .. others.Select(character => character with { })];
    }

    private sealed record PlacementColumnView(ulong RetainerId, CachedRetainer? Retainer, int? FreeSlots, DateTime? ObservedAtUtc);

    /// <summary>The character's assigned retainers in game order, then any other retainer named there (as the matrix columns).</summary>
    private static IReadOnlyList<PlacementColumnView> PlacementColumns(QuartermasterRuntimeSnapshot current, OwnerScope character, IEnumerable<ulong> named)
    {
        PlacementColumnView View(ulong id, CachedRetainer? retainer)
        {
            var free = retainer is null ? null : StowageRouter.FreeInventorySlots(retainer);
            DateTime? observed = null;
            if (free is not null)
            {
                var pages = retainer!.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal) && bag.ObservedAtUtc is not null).ToArray();
                observed = pages.Length == 0 ? retainer.ObservedAtUtc : pages.Min(bag => bag.ObservedAtUtc!.Value);
            }
            return new(id, retainer, free, observed);
        }
        var columns = current.Retainers.Values
            .Where(retainer => SameCharacter(retainer.Owner, character) && retainer.IsCurrentlyAssigned is not false)
            .OrderBy(retainer => retainer.DisplayOrder ?? int.MaxValue)
            .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .Select(retainer => View(retainer.RetainerId, retainer))
            .ToList();
        var shown = columns.Select(column => column.RetainerId).ToHashSet();
        foreach (var id in named)
            if (id != 0 && shown.Add(id))
                columns.Add(View(id, current.Retainers.TryGetValue(id, out var retainer) && SameCharacter(retainer.Owner, character) ? retainer : null));
        return columns;
    }

    private sealed record BagObservation(bool Observed, int? FreeSlots, DateTime? ObservedAtUtc);

    /// <summary>The main bags as this client last saw them whole; unobserved bags have no free count (never zero).</summary>
    private static BagObservation BagView(CachedPlayerInventory? cached, OwnerScope character)
    {
        if (cached is null || !SameCharacter(character, cached.Owner) ||
            !PlayerBagNames.All(name => cached.ObservedSources.Contains(name, StringComparer.Ordinal)))
            return new(false, null, null);
        var main = cached.Bags.Where(bag => PlayerBagNames.Contains(bag.BagName, StringComparer.Ordinal)).ToArray();
        var used = main.SelectMany(bag => bag.Items).Count(item => item.ItemId > 0 && item.Quantity > 0);
        return new(true, Math.Max(0, PlayerBagSlots - used), main.Length == 0 ? cached.UpdatedAtUtc : main.Min(bag => bag.ObservedAtUtc));
    }

    // ---- Write --------------------------------------------------------------------------------

    /// <summary>
    /// One item's edit: the item and its next ranked homes from its stored homes and what every listed character's
    /// retainers hold of it (for use current holders).
    /// </summary>
    internal sealed record PlacementEditLine(
        PlacementEditorItem Item,
        Func<IReadOnlyList<PlacementHome>, IReadOnlyList<PlacementHolding>, List<PlacementHome>> Edit);

    /// <summary>
    /// What happened to one item. Outcome: applied, unchanged, refused or not_applied (a refusal elsewhere stopped an
    /// all-or-nothing edit). Code: Applied, Unchanged, InvalidItem, InvalidQuality, DuplicateItem, UnknownRetainer,
    /// RuleInvalid or NotApplied.
    /// </summary>
    internal sealed record PlacementItemOutcome(
        uint ItemId,
        string ItemName,
        ItemQualityPolicy? Quality,
        string QualityText,
        string Outcome,
        string Code,
        string Message)
    {
        public ulong? RetainerId { get; init; }
        public string? Field { get; init; }
        public bool RuleCreated { get; init; }
        public bool RuleRemoved { get; init; }
    }

    internal sealed record PlacementEditResult(IReadOnlyList<PlacementItemOutcome> Items, string? FailureCode = null, string? FailureMessage = null);

    private sealed class PlacementEditAborted : Exception;

    /// <summary>
    /// Queues a Placement window edit onto the framework thread; it then runs <see cref="ApplyPlacementEdit"/> and hands
    /// the result to <paramref name="completed"/> there (the window's draw thread).
    /// </summary>
    internal void QueuePlacementEdit(OwnerScope owner, IReadOnlyList<PlacementEditLine> lines,
        Action<PlacementEditResult>? completed = null) =>
        workQueue.Enqueue(() =>
        {
            var result = ApplyPlacementEdit(owner, lines, allOrNothing: false, source: "Placement window");
            completed?.Invoke(result);
        });

    /// <summary>
    /// The one placement edit path, for the Placement window and the IPC alike. Each item's next homes come from its
    /// stored homes through the line's edit; a home the edit adds must be a retainer the editor offers on that home's
    /// character (an assigned retainer, one a reserve item already names there, or one observed holding the item),
    /// and the result must pass the Listing Plan's reserve validation of Homes. A placeholder item name is never
    /// saved. Every accepted change is written in one state mutation, creating the plan and a level-0 rule as needed
    /// and removing a level-0 rule left with no home. Refusals are logged at Warning.
    /// </summary>
    internal PlacementEditResult ApplyPlacementEdit(OwnerScope owner, IReadOnlyList<PlacementEditLine> lines,
        bool allOrNothing, string source)
    {
        var current = runtime();
        var listed = PlacementCharacterList(current,
            state.Read(document => ListingPlanCatalog.OwnerPlan(document, owner) is { } found ? Snapshot(found) : null));
        var stocks = listed.Where(character => !SameCharacter(character, owner))
            .Select(character => (Character: character, Stock: CharacterStock?.Invoke(current, character)))
            .ToArray();
        // What every listed character's observed retainers hold of the item.
        IReadOnlyList<PlacementHolding> Holdings(PlacementEditorItem item) =>
            PlacementMatrixData.Holdings(current, item.ItemId, item.Quality)
                .Select(entry => new PlacementHolding(owner, entry.Key, entry.Value))
                .Concat(stocks.SelectMany(other => PlacementMatrixData.Holdings(current, item.ItemId, item.Quality, other.Character, other.Stock)
                    .Select(entry => new PlacementHolding(other.Character, entry.Key, entry.Value))))
                .ToArray();
        bool Offered(PlacementHome home, IEnumerable<PlacementHome> named, IReadOnlyList<PlacementHolding> holdings) =>
            current.Retainers.TryGetValue(home.RetainerId, out var retainer) && SameCharacter(retainer.Owner, home.Character) &&
            retainer.IsCurrentlyAssigned is not false ||
            named.Any(other => other.SameHome(home.Character, home.RetainerId)) ||
            holdings.Any(holding => holding.Units > 0 && holding.RetainerId == home.RetainerId && SameCharacter(holding.Character, home.Character));

        IReadOnlyList<PlacementItemOutcome> outcomes = [];
        try
        {
            state.Mutate(StateChangeKind.Listings, document =>
            {
                var results = new List<PlacementItemOutcome>();
                var accepted = new List<(PlacementEditorItem Item, List<PlacementHome> Homes, int Index)>();
                var target = ListingPlanCatalog.OwnerPlan(document, owner);
                // As the editor's columns: any retainer a reserve item already names counts as offered there.
                var named = (target?.Reserves ?? []).Where(reserve => reserve.Enabled).SelectMany(reserve => reserve.Homes).ToArray();
                foreach (var line in lines)
                {
                    var item = line.Item;
                    var rule = target?.Reserves.FirstOrDefault(reserve => reserve.Enabled && reserve.ItemId == item.ItemId && reserve.Quality == item.Quality);
                    IReadOnlyList<PlacementHome> before = rule?.Homes ?? [];
                    var holdings = Holdings(item);
                    PlacementItemOutcome Outcome(string outcome, string code, string message) =>
                        new(item.ItemId, item.ItemName, item.Quality, item.Quality.ToString(), outcome, code, message);
                    List<PlacementHome> next;
                    try { next = line.Edit(before, holdings); }
                    catch (ArgumentException exception)
                    {
                        results.Add(Outcome("refused", "RuleInvalid", $"{item.ItemName}: {exception.Message}") with { Field = nameof(ListingReserveRule.Homes) });
                        continue;
                    }
                    if (ReservePlacement.Same(before, next))
                    {
                        results.Add(Outcome("unchanged", "Unchanged", $"{item.ItemName} already has these destinations."));
                        continue;
                    }
                    if (next.FirstOrDefault(home => !before.Any(stored => stored.SameHome(home.Character, home.RetainerId)) &&
                                                   !Offered(home, named, holdings)) is { } unknown)
                    {
                        results.Add(Outcome("refused", "UnknownRetainer",
                            $"Retainer {unknown.RetainerId} is not a current retainer of {unknown.Character.CharacterName}; {item.ItemName} was left as it was.") with
                        {
                            RetainerId = unknown.RetainerId,
                        });
                        continue;
                    }
                    if (ListingPlanCatalog.HomesIssue(next) is { } issue)
                    {
                        results.Add(Outcome("refused", "RuleInvalid", $"{item.ItemName}: {issue}") with { Field = nameof(ListingReserveRule.Homes) });
                        continue;
                    }
                    accepted.Add((item, next, results.Count));
                    results.Add(Outcome("applied", "Applied", $"{item.ItemName}: destinations saved."));
                }

                var refused = results.Any(result => result.Outcome == "refused");
                if (accepted.Count == 0 || allOrNothing && refused)
                {
                    foreach (var entry in accepted)
                        results[entry.Index] = results[entry.Index] with
                        {
                            Outcome = "not_applied",
                            Code = "NotApplied",
                            Message = $"{entry.Item.ItemName}: not applied because another item in this all-or-nothing edit was refused.",
                        };
                    outcomes = results;
                    throw new PlacementEditAborted();
                }

                if (target is null)
                {
                    target = new ListingPlan { Owner = owner with { } };
                    document.ListingPlans.Add(target);
                }
                foreach (var (item, next, index) in accepted)
                {
                    var reserve = target.Reserves.FirstOrDefault(rule => rule.Enabled && rule.ItemId == item.ItemId && rule.Quality == item.Quality);
                    var created = reserve is null;
                    // The "Item {id}" placeholder is for messages only; a rule keeps a blank name until a real one is known.
                    var savedName = IsPlaceholder(item.ItemId, item.ItemName) ? string.Empty : item.ItemName.Trim();
                    if (reserve is null)
                    {
                        reserve = new ListingReserveRule { ItemId = item.ItemId, ItemName = savedName, Quality = item.Quality };
                        target.Reserves.Add(reserve);
                    }
                    if (string.IsNullOrWhiteSpace(reserve.ItemName) || IsPlaceholder(item.ItemId, reserve.ItemName))
                        reserve.ItemName = savedName;
                    reserve.Homes = ReservePlacement.Copy(next);
                    var removed = reserve.ReserveUnits == 0 && !reserve.HasAnyHome;
                    if (removed)
                        target.Reserves.Remove(reserve);
                    results[index] = results[index] with
                    {
                        RuleCreated = created && !removed,
                        RuleRemoved = removed && !created,
                    };
                }
                target.UpdatedAtUtc = DateTime.UtcNow;
                target.Revision = checked(target.Revision + 1);
                document.Schema = "gooseworks-quartermaster-state/v5";
                outcomes = results;
            });
        }
        catch (PlacementEditAborted)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warn?.Invoke($"[Quartermaster] Placement edit ({source}) for {owner.CharacterName} could not be saved for " +
                         $"{lines.Count:N0} item(s): {exception.Message}");
            return new(lines.Select(line => new PlacementItemOutcome(line.Item.ItemId, line.Item.ItemName, line.Item.Quality,
                line.Item.Quality.ToString(), "not_applied", "NotApplied", $"{line.Item.ItemName}: not saved ({exception.Message}).")).ToArray(),
                "save_failed", exception.Message);
        }
        foreach (var refusal in outcomes.Where(outcome => outcome.Outcome == "refused"))
            warn?.Invoke($"[Quartermaster] Placement edit ({source}) refused {refusal.ItemName} ({refusal.ItemId} {refusal.QualityText}) " +
                         $"for {owner.CharacterName}: {refusal.Code}" +
                         (refusal.RetainerId is { } id ? $" retainer={id}" : string.Empty) +
                         (refusal.Field is { } field ? $" field={field}" : string.Empty) + $": {refusal.Message}");
        return new(outcomes);
    }

    /// <summary>
    /// Applies one Placement toolbar action to the named items of the logged-in owner, synchronously, and returns
    /// per-item outcomes (<c>gooseworks-quartermaster-placement-edit-ack/v1</c>).
    /// </summary>
    public string EditPlacement(string requestJson) => OnFramework(() => EditPlacementCore(requestJson));

    private string EditPlacementCore(string requestJson)
    {
        PlacementEditRequest? request;
        try { request = JsonSerializer.Deserialize<PlacementEditRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return EditAck(string.Empty, "rejected", "invalid_json", exception.Message, null, []); }
        if (request is null)
            return EditAck(string.Empty, "rejected", "invalid_json", "Request body is empty.", null, []);
        string Reject(string code, string message)
        {
            warn?.Invoke($"[Quartermaster] Placement edit request {request.RequestId} rejected: {code}: {message}");
            return EditAck(request.RequestId, "rejected", code, message, null, []);
        }
        if (request.Schema != PlacementEditSchema)
            return Reject("unsupported_schema", $"Expected {PlacementEditSchema}.");
        if (request.ProviderInstanceId != providerInstanceId)
            return Reject("provider_mismatch", "The request names another Quartermaster instance.");
        var current = runtime();
        var owner = current.Owner;
        if (!owner.HasStableIdentity || request.Owner is null ||
            request.Owner.LocalContentId != owner.LocalContentId || request.Owner.HomeWorldId != owner.HomeWorldId)
            return Reject("owner_mismatch", "The request owner is not the current character.");
        if (!TryParseOperation(request.Operation, out var operation))
            return Reject("invalid_operation", $"Operation '{request.Operation}' is not one of makeFirst, addFallback, remove, toggle, setCap, useCurrentHolders, clearDestinations.");
        if (operation == PlacementOperation.SetCap && request.MaxUnits is < 0)
            return Reject("invalid_max_units", "maxUnits cannot be negative; omit it (null) to clear a cap.");
        if (operation != PlacementOperation.SetCap && request.MaxUnits is not null)
            return Reject("invalid_max_units", $"{request.Operation} takes no maxUnits.");

        var shown = owner;
        if (request.Character is { } named)
        {
            if (named.LocalContentId == 0 || named.HomeWorldId == 0)
                return Reject("invalid_character", "character needs localContentId and homeWorldId.");
            var plan = state.Read(document => ListingPlanCatalog.OwnerPlan(document, owner) is { } found ? Snapshot(found) : null);
            // By content id: a character that changed worlds is still named by the world its stored homes carry.
            var match = PlacementCharacterList(current, plan)
                .FirstOrDefault(character => character.LocalContentId == named.LocalContentId);
            if (match is null)
                return Reject("character_not_offered", $"Character {named.LocalContentId}@{named.HomeWorldId} is not one the placement editor can show.");
            shown = match;
        }

        if (request.Destination is { AnySpace: true })
            return Reject("any_space_not_supported", "There is no \"any space\" home: name a retainer. Homes are explicit retainers, ranked across characters.");
        ulong destination = 0;
        if (ReservePlacement.NeedsDestination(operation))
        {
            if (request.Destination is not { RetainerId: { } chosen } || chosen == 0)
                return Reject("invalid_destination", $"{request.Operation} needs a destination retainerId.");
            destination = chosen;
        }
        else if (request.Destination is not null)
            return Reject("invalid_destination", $"{request.Operation} takes no destination: it acts on every home of the item.");

        if (request.Items is not { Count: > 0 } items)
            return Reject("no_items", "Name at least one item.");
        if (items.Count > MaxPlacementItems)
            return Reject("too_many_items", $"At most {MaxPlacementItems:N0} items.");

        var outcomes = new List<PlacementItemOutcome>();
        var lines = new List<PlacementEditLine>();
        var seen = new HashSet<(uint, ItemQualityPolicy)>();
        foreach (var item in items)
        {
            // The caller's name, else the game's; the placeholder only labels messages and is never saved.
            var name = (item.ItemId == 0 ? null : RealItemName(item.ItemId, item.ItemName)) ?? PlaceholderName(item.ItemId);
            PlacementItemOutcome Refused(ItemQualityPolicy? quality, string code, string message) =>
                new(item.ItemId, name, quality, item.Quality, "refused", code, message);
            if (item.ItemId == 0)
                outcomes.Add(Refused(null, "InvalidItem", "An item needs an itemId."));
            else if (!Enum.TryParse<ItemQualityPolicy>(item.Quality, ignoreCase: false, out var quality) || quality == ItemQualityPolicy.Any)
                outcomes.Add(Refused(null, "InvalidQuality", $"{name}: quality must be NqOnly or HqOnly."));
            else if (!seen.Add((item.ItemId, quality)))
                outcomes.Add(Refused(quality, "DuplicateItem", $"{name} ({quality}) is named more than once."));
            else
                lines.Add(new(new(item.ItemId, name, quality), (before, holdings) =>
                    ReservePlacement.Operate(operation, before, shown, destination, request.MaxUnits, holdings)));
        }
        foreach (var refusal in outcomes)
            warn?.Invoke($"[Quartermaster] Placement edit request {request.RequestId} refused item {refusal.ItemId} ({refusal.QualityText}): {refusal.Code}: {refusal.Message}");
        if (request.AllOrNothing && outcomes.Count > 0 || lines.Count == 0)
            return EditAck(request.RequestId, "rejected", "item_refused", "No item was changed: an item was refused.", shown,
                outcomes.Concat(lines.Select(line => new PlacementItemOutcome(line.Item.ItemId, line.Item.ItemName, line.Item.Quality,
                    line.Item.Quality.ToString(), "not_applied", "NotApplied", $"{line.Item.ItemName}: not applied because another item was refused."))).ToArray());

        var result = ApplyPlacementEdit(owner, lines, request.AllOrNothing, $"IPC request {request.RequestId}");
        var all = outcomes.Concat(result.Items).ToArray();
        if (result.FailureCode is { } failure)
            return EditAck(request.RequestId, "rejected", failure, $"Nothing was saved: {result.FailureMessage}", shown, all);
        var applied = all.Count(outcome => outcome.Outcome == "applied");
        var refusedCount = all.Count(outcome => outcome.Outcome == "refused");
        return applied > 0
            ? EditAck(request.RequestId, refusedCount > 0 ? "partially_applied" : "applied", null,
                refusedCount > 0 ? $"Applied to {applied:N0} item(s); {refusedCount:N0} refused." : $"Applied to {applied:N0} item(s).", shown, all)
            : refusedCount > 0
                ? EditAck(request.RequestId, "rejected", "item_refused", "No item was changed: an item was refused.", shown, all)
                : EditAck(request.RequestId, "unchanged", null, "Every item already had these destinations.", shown, all);
    }

    private static bool TryParseOperation(string text, out PlacementOperation operation)
    {
        operation = default;
        return text switch
        {
            "makeFirst" => Set(PlacementOperation.MakeFirst, out operation),
            "addFallback" => Set(PlacementOperation.AddFallback, out operation),
            "remove" => Set(PlacementOperation.Remove, out operation),
            "toggle" => Set(PlacementOperation.Toggle, out operation),
            "setCap" => Set(PlacementOperation.SetCap, out operation),
            "useCurrentHolders" => Set(PlacementOperation.UseCurrentHolders, out operation),
            "clearDestinations" => Set(PlacementOperation.ClearDestinations, out operation),
            _ => false,
        };

        static bool Set(PlacementOperation value, out PlacementOperation operation)
        {
            operation = value;
            return true;
        }
    }

    private string EditAck(string requestId, string status, string? errorCode, string message, OwnerScope? character,
        IReadOnlyList<PlacementItemOutcome> items) =>
        JsonSerializer.Serialize(new
        {
            schema = PlacementEditAckSchema,
            providerInstanceId,
            requestId,
            status,
            errorCode,
            message,
            character = character is null ? null : new { character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName },
            items = items.Select(item => new
            {
                item.ItemId,
                item.ItemName,
                quality = item.QualityText,
                item.Outcome,
                item.Code,
                item.Message,
                item.RetainerId,
                item.Field,
                item.RuleCreated,
                item.RuleRemoved,
            }),
        }, JsonOptions);
}
