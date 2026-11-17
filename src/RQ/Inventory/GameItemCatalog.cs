using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace RQ.Inventory;

internal sealed record GameCatalogItem(uint ItemId, string Name, uint CategoryId, int ItemLevel);

internal sealed record GameCatalogCategory(uint Id, string Name, IReadOnlyList<GameCatalogItem> Items);

internal sealed record CraftMaterial(uint ItemId, string Name, int Quantity);

internal sealed record GameCatalogCraft(GameCatalogItem Item, IReadOnlyList<CraftMaterial> Materials);

/// <summary>
/// The whole game item catalog grouped by ItemUICategory in the game's category order, plus each
/// craftable item's direct materials. Immutable once built.
/// </summary>
internal sealed class GameCatalogSnapshot
{
    public required IReadOnlyList<GameCatalogCategory> Categories { get; init; }
    public required IReadOnlyDictionary<uint, GameCatalogItem> Items { get; init; }
    public required IReadOnlyDictionary<uint, GameCatalogCategory> CategoriesById { get; init; }
    public required IReadOnlyDictionary<uint, IReadOnlyList<CraftMaterial>> Materials { get; init; }
    public required IReadOnlyList<GameCatalogCraft> Crafts { get; init; }
    public required int MinItemLevel { get; init; }
    public required int MaxItemLevel { get; init; }

    public IReadOnlyList<CraftMaterial>? MaterialsFor(uint itemId) =>
        Materials.TryGetValue(itemId, out var materials) ? materials : null;
}

/// <summary>
/// Builds <see cref="GameCatalogSnapshot"/> once, off the draw thread, on first request.
/// Callers receive null until the build finishes; the result is cached for the session.
/// </summary>
internal sealed class GameItemCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private Task<GameCatalogSnapshot>? build;

    public GameItemCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>The built catalog, or null while it is still being built (the build starts on first call).</summary>
    public GameCatalogSnapshot? TryGet()
    {
        lock (gate)
        {
            build ??= Task.Run(Build);
            return build.IsCompletedSuccessfully ? build.Result : null;
        }
    }

    private GameCatalogSnapshot Build()
    {
        try
        {
            return BuildCore();
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Unable to build the game item catalog.");
            return new GameCatalogSnapshot
            {
                Categories = [],
                Items = new Dictionary<uint, GameCatalogItem>(),
                CategoriesById = new Dictionary<uint, GameCatalogCategory>(),
                Materials = new Dictionary<uint, IReadOnlyList<CraftMaterial>>(),
                Crafts = [],
                MinItemLevel = 0,
                MaxItemLevel = 1,
            };
        }
    }

    private GameCatalogSnapshot BuildCore()
    {
        var categoryRows = dataManager.GetExcelSheet<ItemUICategory>()
            .Where(row => row.RowId > 0)
            .Select(row => new { row.RowId, Name = row.Name.ExtractText(), row.OrderMajor, row.OrderMinor })
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .ToDictionary(row => row.RowId);

        var items = new Dictionary<uint, GameCatalogItem>();
        foreach (var row in dataManager.GetExcelSheet<Item>())
        {
            var categoryId = row.ItemUICategory.RowId;
            if (row.RowId == 0 || categoryId == 0 || !categoryRows.ContainsKey(categoryId))
                continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            items[row.RowId] = new GameCatalogItem(row.RowId, name, categoryId, (int)row.LevelItem.RowId);
        }

        var categories = items.Values
            .GroupBy(item => item.CategoryId)
            .Select(group =>
            {
                var category = categoryRows[group.Key];
                return new
                {
                    category.OrderMajor,
                    category.OrderMinor,
                    Value = new GameCatalogCategory(
                        group.Key,
                        category.Name,
                        group
                            .OrderBy(item => item.ItemLevel)
                            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.ItemId)
                            .ToArray()),
                };
            })
            .OrderBy(entry => entry.OrderMajor)
            .ThenBy(entry => entry.OrderMinor)
            .ThenBy(entry => entry.Value.Id)
            .Select(entry => entry.Value)
            .ToArray();

        string NameOf(uint itemId) =>
            items.TryGetValue(itemId, out var known)
                ? known.Name
                : dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() is { Length: > 0 } name
                    ? name
                    : $"Item {itemId}";

        var materials = new Dictionary<uint, IReadOnlyList<CraftMaterial>>();

        // Recipes: direct ingredients (crystals included) of the lowest Recipe row per result item.
        foreach (var recipe in dataManager.GetExcelSheet<Recipe>().OrderBy(row => row.RowId))
        {
            var resultId = recipe.ItemResult.RowId;
            if (recipe.RowId == 0 || resultId == 0 || materials.ContainsKey(resultId))
                continue;
            var ingredients = new List<(uint ItemId, int Quantity)>();
            for (var index = 0; index < recipe.Ingredient.Count && index < recipe.AmountIngredient.Count; index++)
            {
                var ingredientId = recipe.Ingredient[index].RowId;
                var quantity = (int)recipe.AmountIngredient[index];
                if (ingredientId > 0 && quantity > 0)
                    ingredients.Add((ingredientId, quantity));
            }
            if (ingredients.Count > 0)
                materials[resultId] = Merge(ingredients, NameOf);
        }

        // Workshop projects: every supply item across all parts and phases, summed.
        var supplyItems = dataManager.GetExcelSheet<CompanyCraftSupplyItem>()
            .Where(row => row.RowId > 0 && row.Item.RowId > 0)
            .ToDictionary(row => row.RowId, row => row.Item.RowId);
        foreach (var sequence in dataManager.GetExcelSheet<CompanyCraftSequence>().OrderBy(row => row.RowId))
        {
            var resultId = sequence.ResultItem.RowId;
            if (sequence.RowId == 0 || resultId == 0 || materials.ContainsKey(resultId))
                continue;
            var supplies = new List<(uint ItemId, int Quantity)>();
            foreach (var part in sequence.CompanyCraftPart)
            {
                if (part.RowId == 0 || part.ValueNullable is not { } partRow)
                    continue;
                foreach (var processRef in partRow.CompanyCraftProcess)
                {
                    if (processRef.RowId == 0 || processRef.ValueNullable is not { } process)
                        continue;
                    for (var index = 0; index < process.SupplyItem.Count; index++)
                    {
                        var supplyId = process.SupplyItem[index].RowId;
                        var quantity = index < process.SetQuantity.Count && index < process.SetsRequired.Count
                            ? process.SetQuantity[index] * process.SetsRequired[index]
                            : 0;
                        if (supplyId > 0 && quantity > 0 && supplyItems.TryGetValue(supplyId, out var itemId))
                            supplies.Add((itemId, quantity));
                    }
                }
            }
            if (supplies.Count > 0)
                materials[resultId] = Merge(supplies, NameOf);
        }

        var crafts = materials
            .Where(entry => items.ContainsKey(entry.Key))
            .Select(entry => new GameCatalogCraft(items[entry.Key], entry.Value))
            .OrderBy(craft => craft.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(craft => craft.Item.ItemId)
            .ToArray();

        log.Debug($"Game item catalog: {items.Count:N0} items, {categories.Length:N0} categories, {crafts.Length:N0} craftable.");
        return new GameCatalogSnapshot
        {
            Categories = categories,
            Items = items,
            CategoriesById = categories.ToDictionary(category => category.Id),
            Materials = materials,
            Crafts = crafts,
            MinItemLevel = Math.Max(0, items.Values.Select(item => item.ItemLevel).DefaultIfEmpty(0).Min()),
            MaxItemLevel = Math.Max(1, items.Values.Select(item => item.ItemLevel).DefaultIfEmpty(1).Max()),
        };
    }

    private static IReadOnlyList<CraftMaterial> Merge(
        IEnumerable<(uint ItemId, int Quantity)> entries,
        Func<uint, string> nameOf) =>
        entries
            .GroupBy(entry => entry.ItemId)
            .Select(group => new CraftMaterial(group.Key, nameOf(group.Key), group.Sum(entry => entry.Quantity)))
            .ToArray();
}

/// <summary>Parses the plan add box's <c>&gt;craft [xN]</c> syntax.</summary>
internal static class CraftQuery
{
    public const int MaxCount = 999;

    /// <summary>Splits <c>"shark-class bow x3"</c> into its name needle and a count clamped to 1–999.</summary>
    public static (string Needle, int Count) Parse(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space > 0 && space + 2 <= trimmed.Length &&
            trimmed[space + 1] is 'x' or 'X' or '×')
        {
            var digits = trimmed[(space + 2)..];
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
            {
                var count = digits.Length > 4 ? MaxCount : Math.Clamp(int.Parse(digits), 1, MaxCount);
                return (trimmed[..space].TrimEnd(), count);
            }
        }
        return (trimmed, 1);
    }
}
