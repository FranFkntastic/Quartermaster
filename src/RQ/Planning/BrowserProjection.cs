using System.Globalization;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Planning;

public enum BrowserScopeKind { All, Player, Retainer }

public sealed record BrowserScope(string Key, string Label, BrowserScopeKind Kind, ulong? RetainerId)
{
    public const string AllKey = "all";
    public const string PlayerKey = "player";
    public static string RetainerKey(ulong id) => $"retainer:{id.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record StockStack(
    string ScopeKey,
    BrowserScopeKind ScopeKind,
    ulong? RetainerId,
    string OwnerName,
    string Storage,
    int? SlotIndex,
    uint ItemId,
    string ItemName,
    int Quantity,
    FfxivItemQuality Quality,
    DateTime? ObservedAtUtc,
    string? ItemType = null,
    decimal? ConditionPercent = null,
    bool? Equipped = null);

public sealed record StockGroup(uint ItemId, string ItemName, IReadOnlyList<StockStack> Stacks, ItemMetadata? Definition = null)
{
    public int TotalQuantity => Stacks.Sum(stack => stack.Quantity);
    public int PlayerQuantity => Stacks.Where(stack => stack.ScopeKind == BrowserScopeKind.Player).Sum(stack => stack.Quantity);
    public int RetainerQuantity => Stacks.Where(stack => stack.ScopeKind == BrowserScopeKind.Retainer).Sum(stack => stack.Quantity);
    public IReadOnlyCollection<FfxivRetainerKey> Retainers => Stacks.Where(stack => stack.RetainerId is not null).Select(stack => new FfxivRetainerKey(stack.RetainerId!.Value)).Distinct().ToArray();
}

public sealed record ListingRow(
    string ScopeKey,
    ulong RetainerId,
    string RetainerName,
    uint ItemId,
    string ItemName,
    int Quantity,
    FfxivItemQuality Quality,
    FieldEvidence<decimal> Condition,
    FieldEvidence<decimal> UnitPrice,
    FieldEvidence<decimal> TotalPrice,
    DateTime? ObservedAtUtc,
    ItemMetadata? Definition = null);

public sealed class BrowserProjection
{
    public required IReadOnlyList<BrowserScope> Scopes { get; init; }
    public required IReadOnlyList<StockGroup> Items { get; init; }
    public required IReadOnlyList<ListingRow> Listings { get; init; }
    public required OwnerScope Owner { get; init; }
    public required string Identity { get; init; }

    public IReadOnlyList<StockGroup> GetItems(string? scopeKey) => string.IsNullOrWhiteSpace(scopeKey) || scopeKey == BrowserScope.AllKey
        ? Items
        : Aggregate(Items.SelectMany(item => item.Stacks).Where(stack => stack.ScopeKey == scopeKey), Items.ToDictionary(item => item.ItemId, item => item.Definition));

    public IReadOnlyList<ListingRow> GetListings(string? scopeKey) => string.IsNullOrWhiteSpace(scopeKey) || scopeKey == BrowserScope.AllKey
        ? Listings
        : Listings.Where(listing => listing.ScopeKey == scopeKey).ToArray();

    internal static IReadOnlyList<StockGroup> Aggregate(IEnumerable<StockStack> stacks, IReadOnlyDictionary<uint, ItemMetadata?>? definitions = null) => stacks
        .GroupBy(stack => stack.ItemId)
        .Select(group => new StockGroup(
            group.Key,
            group.Select(stack => stack.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {group.Key}",
            group.OrderBy(stack => stack.ScopeKind).ThenBy(stack => stack.OwnerName).ToArray(),
            definitions?.GetValueOrDefault(group.Key)))
        .OrderBy(group => group.ItemName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.ItemId)
        .ToArray();
}

public static class BrowserProjectionBuilder
{
    public static BrowserProjection Build(
        IReadOnlyList<InventoryBag> playerBags,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        Func<uint, ItemMetadata>? resolveMetadata = null)
    {
        var scopes = new List<BrowserScope>
        {
            new(BrowserScope.AllKey, "All accessible stock", BrowserScopeKind.All, null),
            new(BrowserScope.PlayerKey, "Player", BrowserScopeKind.Player, null),
        };
        var stacks = new List<StockStack>();
        var listings = new List<ListingRow>();
        foreach (var bag in playerBags)
        foreach (var item in bag.Items.Where(item => item.ItemId > 0 && item.Quantity > 0))
            stacks.Add(new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", item.ContainerKey ?? bag.Location ?? bag.BagName, item.SlotIndex, item.ItemId, DisplayName(item.ItemId, item.ItemName), checked((int)item.Quantity), item.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, null, item.ItemType, item.ConditionPercent is { } condition ? (decimal)condition : null, item.Equipped));

        foreach (var retainer in cache.Values.Where(retainer => retainer.Owner.Matches(owner)).OrderBy(retainer => retainer.RetainerName).ThenBy(retainer => retainer.RetainerId))
        {
            var key = BrowserScope.RetainerKey(retainer.RetainerId);
            var name = string.IsNullOrWhiteSpace(retainer.RetainerName) ? $"Retainer {retainer.RetainerId}" : retainer.RetainerName;
            scopes.Add(new(key, name, BrowserScopeKind.Retainer, retainer.RetainerId));
            foreach (var bag in retainer.Bags.Where(bag => bag.BagName is not "RetainerGil" and not "RetainerMarket"))
            foreach (var item in bag.Items.Where(item => item.ItemId > 0 && item.Quantity > 0))
                stacks.Add(new(key, BrowserScopeKind.Retainer, retainer.RetainerId, name, item.ContainerKey ?? bag.Location ?? bag.BagName, item.SlotIndex, item.ItemId, DisplayName(item.ItemId, item.ItemName), checked((int)item.Quantity), item.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, bag.ObservedAtUtc, item.ItemType, item.ConditionPercent is { } condition ? (decimal)condition : null, item.Equipped));
            foreach (var listing in retainer.Listings.Where(listing => listing.ItemId > 0 && listing.Quantity > 0))
            {
                var price = listing.UnitPrice is { } known ? Evidence.Known((decimal)known) : Evidence.Unknown<decimal>("Unit price was not observed.");
                listings.Add(new(key, retainer.RetainerId, name, listing.ItemId, DisplayName(listing.ItemId, listing.ItemName), checked((int)listing.Quantity), listing.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
                    listing.ConditionPercent is >= 0 and <= 100 ? Evidence.Known((decimal)listing.ConditionPercent.Value) : Evidence.Unknown<decimal>("Condition was not observed."),
                    price,
                    price.IsKnown ? Evidence.Known(price.Value * listing.Quantity) : Evidence.Unknown<decimal>("Total price requires a unit price."),
                    listing.ListedAtUtc,
                    resolveMetadata?.Invoke(listing.ItemId)));
            }
        }

        var definitions = stacks.Select(stack => stack.ItemId).Distinct().ToDictionary(itemId => itemId, itemId => resolveMetadata?.Invoke(itemId));
        var items = BrowserProjection.Aggregate(stacks, definitions);
        var sortedListings = listings.OrderBy(listing => listing.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new BrowserProjection
        {
            Scopes = scopes,
            Items = items,
            Listings = sortedListings,
            Owner = owner,
            Identity = CreateIdentity(items, sortedListings, scopes),
        };
    }

    private static string DisplayName(uint id, string? name) => string.IsNullOrWhiteSpace(name) ? $"Item {id}" : name;

    private static string CreateIdentity(IEnumerable<StockGroup> items, IEnumerable<ListingRow> listings, IEnumerable<BrowserScope> scopes)
    {
        var hash = new HashCode();
        foreach (var stack in items.SelectMany(item => item.Stacks).OrderBy(stack => stack.ScopeKey).ThenBy(stack => stack.ItemId).ThenBy(stack => stack.SlotIndex))
            hash.Add((stack.ScopeKey, stack.ItemId, stack.Quantity, stack.Quality, stack.ObservedAtUtc));
        foreach (var listing in listings.OrderBy(listing => listing.RetainerId).ThenBy(listing => listing.ItemId))
            hash.Add((listing.RetainerId, listing.ItemId, listing.Quantity, listing.ObservedAtUtc));
        foreach (var scope in scopes)
            hash.Add((scope.Key, scope.Label));
        return hash.ToHashCode().ToString("X8", CultureInfo.InvariantCulture);
    }
}

public sealed record BrowserFilterStatus(bool IsValid, bool ShowingLastValid, IReadOnlyList<FilterDiagnostic> Diagnostics);
public sealed record ItemQueryResult(IReadOnlyList<StockGroup> Items, BrowserFilterStatus Filter);
public sealed record ListingQueryResult(IReadOnlyList<ListingRow> Listings, BrowserFilterStatus Filter);

public sealed class BrowserQueryController
{
    private readonly QueryState<StockGroup> items = new();
    private readonly QueryState<ListingRow> listings = new();

    public ItemQueryResult QueryItems(BrowserProjection projection, string? expression, string? scopeKey = null)
    {
        var source = projection.GetItems(scopeKey);
        var context = CreateItemContext(source, projection.Owner);
        var result = items.Query(source, $"{projection.Identity}|{scopeKey}", expression, context);
        return new(result.Rows, result.Status);
    }

    public ListingQueryResult QueryListings(BrowserProjection projection, string? expression, string? scopeKey = null)
    {
        var source = projection.GetListings(scopeKey);
        var context = CreateListingContext(source, projection.Owner);
        var result = listings.Query(source, $"{projection.Identity}|{scopeKey}", expression, context);
        return new(result.Rows, result.Status);
    }

    public static FilterContext<StockGroup> CreateItemContext(IReadOnlyList<StockGroup> source, OwnerScope owner)
    {
        var catalog = CreateCatalog(source.Select(item => (item.ItemId, item.ItemName)), source.SelectMany(item => item.Stacks).Where(stack => stack.RetainerId is not null).Select(stack => (stack.RetainerId!.Value, stack.OwnerName)), source.Select(item => item.Definition), owner);
        var builder = new FilterContextBuilder<StockGroup>(catalog.Catalog)
            .Bind(catalog.ItemName, item => Evidence.Known(new FfxivItemKey(item.ItemId)))
            .Bind(catalog.ItemLevel, item => Value(item.Definition?.ItemLevel, "Item-level metadata is unavailable."))
            .Bind(catalog.EquipLevel, item => Value(item.Definition?.EquipLevel, "Equipment-level metadata is unavailable."))
            .BindSet(catalog.ItemJobs, item => Set(item.Definition?.EligibleJobs?.Select(job => job.Key).ToArray(), "Equipment-job metadata is unavailable."))
            .BindSet(catalog.ItemSlots, item => Set(item.Definition?.Slots, "Equipment-slot metadata is unavailable."))
            .Bind(catalog.ItemRarity, item => Value(item.Definition?.Rarity, "Rarity metadata is unavailable."))
            .Bind(catalog.ItemUiCategory, item => Value(item.Definition?.UiCategory, "Category metadata is unavailable."))
            .Bind(catalog.ItemUnique, item => Value(item.Definition?.IsUnique, "Unique-item metadata is unavailable."))
            .Bind(catalog.ItemTradable, item => Value(item.Definition?.IsTradable, "Tradability metadata is unavailable."))
            .Bind(catalog.ItemDesynthesizable, item => Value(item.Definition?.IsDesynthesizable, "Desynthesis metadata is unavailable."))
            .Bind(catalog.OwnershipQuantity, item => Evidence.Known((long)item.TotalQuantity))
            .BindSet(catalog.OwnershipRetainers, item => Evidence.Known(item.Retainers))
            .UseDefaultText(catalog.ItemName, item => Evidence.Known(item.ItemName));
        if (owner.LocalContentId is > 0)
            builder.BindSet(catalog.OwnershipCharacters, _ => Evidence.Known<IReadOnlyCollection<FfxivCharacterKey>>([new(owner.LocalContentId.Value)]));
        return builder.Build("quartermaster-items", "1");
    }

    public static FilterContext<ListingRow> CreateListingContext(IReadOnlyList<ListingRow> source, OwnerScope owner)
    {
        var catalog = CreateCatalog(source.Select(item => (item.ItemId, item.ItemName)), source.Select(item => (item.RetainerId, item.RetainerName)), source.Select(item => item.Definition), owner);
        var builder = new FilterContextBuilder<ListingRow>(catalog.Catalog)
            .Bind(catalog.ItemName, item => Evidence.Known(new FfxivItemKey(item.ItemId)))
            .Bind(catalog.ItemLevel, item => Value(item.Definition?.ItemLevel, "Item-level metadata is unavailable."))
            .Bind(catalog.EquipLevel, item => Value(item.Definition?.EquipLevel, "Equipment-level metadata is unavailable."))
            .BindSet(catalog.ItemJobs, item => Set(item.Definition?.EligibleJobs?.Select(job => job.Key).ToArray(), "Equipment-job metadata is unavailable."))
            .BindSet(catalog.ItemSlots, item => Set(item.Definition?.Slots, "Equipment-slot metadata is unavailable."))
            .Bind(catalog.ItemRarity, item => Value(item.Definition?.Rarity, "Rarity metadata is unavailable."))
            .Bind(catalog.ItemUiCategory, item => Value(item.Definition?.UiCategory, "Category metadata is unavailable."))
            .Bind(catalog.ItemUnique, item => Value(item.Definition?.IsUnique, "Unique-item metadata is unavailable."))
            .Bind(catalog.ItemTradable, item => Value(item.Definition?.IsTradable, "Tradability metadata is unavailable."))
            .Bind(catalog.ItemDesynthesizable, item => Value(item.Definition?.IsDesynthesizable, "Desynthesis metadata is unavailable."))
            .Bind(catalog.InstanceQuality, item => Evidence.Known(item.Quality))
            .Bind(catalog.InstanceCondition, item => item.Condition)
            .Bind(catalog.OfferSource, _ => Evidence.Known(FfxivOfferSource.Market))
            .Bind(catalog.OfferPrice, item => item.UnitPrice)
            .Bind(catalog.OfferTotalPrice, item => item.TotalPrice)
            .Bind(catalog.OfferQuantity, item => Evidence.Known((long)item.Quantity))
            .BindSet(catalog.OwnershipRetainers, item => Evidence.Known<IReadOnlyCollection<FfxivRetainerKey>>([new(item.RetainerId)]))
            .UseDefaultText(catalog.ItemName, item => Evidence.Known(item.ItemName));
        if (owner.LocalContentId is > 0)
            builder.BindSet(catalog.OwnershipCharacters, _ => Evidence.Known<IReadOnlyCollection<FfxivCharacterKey>>([new(owner.LocalContentId.Value)]));
        return builder.Build("quartermaster-listings", "1");
    }

    private static FfxivFilterCatalog CreateCatalog(
        IEnumerable<(uint Id, string Name)> itemRows,
        IEnumerable<(ulong Id, string Name)> retainerRows,
        IEnumerable<ItemMetadata?> definitions,
        OwnerScope owner)
    {
        var itemValues = itemRows.GroupBy(row => row.Id).Select(group => new FilterLiteralCandidate<FfxivItemKey>(new(group.Key), group.First().Name)).ToArray();
        var retainerValues = retainerRows.GroupBy(row => row.Id).Select(group => new FilterLiteralCandidate<FfxivRetainerKey>(new(group.Key), group.First().Name)).ToArray();
        var availableDefinitions = definitions.Where(definition => definition is not null).Select(definition => definition!).ToArray();
        var jobValues = availableDefinitions.SelectMany(definition => definition.EligibleJobs ?? [])
            .GroupBy(job => job.Key)
            .Select(group => group.First())
            .Select(job => new FilterLiteralCandidate<FfxivJobKey>(job.Key, job.Name, [job.Abbreviation]))
            .ToArray();
        var categoryValues = availableDefinitions
            .Where(definition => definition.UiCategory is not null && !string.IsNullOrWhiteSpace(definition.UiCategoryName))
            .GroupBy(definition => definition.UiCategory!.Value)
            .Select(group => new FilterLiteralCandidate<FfxivUiCategoryKey>(group.Key, group.First().UiCategoryName!))
            .ToArray();
        var characterValues = owner.LocalContentId is > 0
            ? new[] { new FilterLiteralCandidate<FfxivCharacterKey>(new(owner.LocalContentId.Value), $"{owner.CharacterName}@{owner.HomeWorldName}") }
            : [];
        return FfxivFilterCatalog.Create(new FfxivFilterResolvers(
            new FilterNamedValueCatalog<FfxivItemKey>(itemValues),
            new FilterNamedValueCatalog<FfxivJobKey>(jobValues),
            new FilterNamedValueCatalog<FfxivUiCategoryKey>(categoryValues),
            new FilterNamedValueCatalog<FfxivCharacterKey>(characterValues),
            new FilterNamedValueCatalog<FfxivRetainerKey>(retainerValues),
            new FilterNamedValueCatalog<FfxivWorldKey>([]),
            new FilterNamedValueCatalog<FfxivDataCenterKey>([])));
    }

    private static FieldEvidence<T> Value<T>(T? value, string reason) where T : struct =>
        value is { } known ? Evidence.Known(known) : Evidence.Unknown<T>(reason);

    private static FieldEvidence<IReadOnlyCollection<T>> Set<T>(IReadOnlyCollection<T>? value, string reason) =>
        value is not null ? Evidence.Known(value) : Evidence.Unknown<IReadOnlyCollection<T>>(reason);

    private sealed class QueryState<T>
    {
        private string identity = string.Empty;
        private string expression = string.Empty;
        private FilterCompilation<T>? compilation;
        private FilterCompilation<T>? lastValid;
        private IReadOnlyList<T> lastValidRows = [];

        public (IReadOnlyList<T> Rows, BrowserFilterStatus Status) Query(IReadOnlyList<T> source, string newIdentity, string? value, FilterContext<T> context)
        {
            if (identity != newIdentity)
            {
                identity = newIdentity;
                compilation = null;
                lastValid = null;
                lastValidRows = [];
            }
            var input = value ?? string.Empty;
            if (compilation is null || expression != input)
            {
                expression = input;
                compilation = FilterCompiler.Compile(input, context);
            }
            if (compilation.IsValid)
            {
                lastValid = compilation;
                lastValidRows = source.Where(compilation.Matches).ToArray();
                return (lastValidRows, new(true, false, compilation.Diagnostics));
            }
            if (lastValid is not null)
                lastValidRows = source.Where(lastValid.Matches).ToArray();
            return (lastValidRows, new(false, lastValid is not null, compilation.Diagnostics));
        }
    }
}

public static class WithdrawalPlanStager
{
    public static bool TryUpsert(IList<TargetPlanItem> plan, StockGroup stock, int targetQuantity)
    {
        if (targetQuantity <= 0)
            return false;
        var existing = plan.FirstOrDefault(item => item.ItemId == stock.ItemId);
        if (existing is null)
            plan.Add(new TargetPlanItem { ItemId = stock.ItemId, ItemName = stock.ItemName, TargetQuantity = targetQuantity });
        else
        {
            existing.ItemName = stock.ItemName;
            existing.TargetQuantity = targetQuantity;
            existing.Enabled = true;
        }
        return true;
    }
}
