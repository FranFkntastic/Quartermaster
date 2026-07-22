using Dalamud.Plugin.Services;
using Franthropy.FFXIV.Filtering;
using Lumina.Excel.Sheets;

namespace RQ.Inventory;

public sealed record ItemJob(FfxivJobKey Key, string Name, string Abbreviation);

public sealed record ItemMetadata(
    uint ItemId,
    string Name,
    string? ItemType,
    bool SupportsCondition,
    long? ItemLevel,
    long? EquipLevel,
    IReadOnlyList<ItemJob>? EligibleJobs,
    IReadOnlyCollection<FfxivEquipmentSlot>? Slots,
    FfxivItemRarity? Rarity,
    FfxivUiCategoryKey? UiCategory,
    string? UiCategoryName,
    bool? IsUnique,
    bool? IsTradable,
    bool? IsDesynthesizable,
    bool? IsHighQualityCapable,
    long? MaxStackSize);

public sealed class ItemMetadataCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, ItemMetadata> cache = [];

    public ItemMetadataCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public ItemMetadata Resolve(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var cached))
            return cached;
        try
        {
            var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
            if (item is null)
                return cache[itemId] = Unknown(itemId);
            var value = item.Value;
            var name = value.Name.ToString();
            var itemType = ResolveItemType(value);
            var equipment = value.EquipSlotCategory.RowId != 0;
            return cache[itemId] = new(
                itemId,
                string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name,
                itemType,
                value.StackSize == 1 && equipment,
                value.LevelItem.RowId,
                equipment ? value.LevelEquip : null,
                equipment ? ResolveEligibleJobs(value) : null,
                equipment ? ResolveSlots(value.EquipSlotCategory.RowId) : null,
                ResolveRarity(value.Rarity),
                value.ItemUICategory.RowId > 0 ? new FfxivUiCategoryKey(value.ItemUICategory.RowId) : null,
                itemType,
                value.IsUnique,
                !value.IsUntradable,
                value.Desynth > 0,
                value.CanBeHq,
                value.StackSize > 0 ? value.StackSize : null);
        }
        catch (Exception exception)
        {
            log.Verbose(exception, $"Unable to resolve metadata for item {itemId}.");
            return cache[itemId] = Unknown(itemId);
        }
    }

    private string? ResolveItemType(Item item)
    {
        if (item.ItemUICategory.RowId == 0)
            return null;
        var value = item.ItemUICategory.Value.Name.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private IReadOnlyList<ItemJob> ResolveEligibleJobs(Item item)
    {
        if (item.ClassJobCategory.RowId == 0)
            return [];
        var category = item.ClassJobCategory.Value;
        return dataManager.GetExcelSheet<ClassJob>()
            .Where(job => job.RowId > 0 && !string.IsNullOrWhiteSpace(job.Abbreviation.ToString()))
            .Where(job => typeof(ClassJobCategory).GetProperty(job.Abbreviation.ToString())?.GetValue(category) is true)
            .Select(job => new ItemJob(new(job.RowId), job.Name.ToString(), job.Abbreviation.ToString()))
            .ToArray();
    }

    private static IReadOnlyCollection<FfxivEquipmentSlot> ResolveSlots(uint category) => category switch
    {
        1 or 13 or 14 => [FfxivEquipmentSlot.MainHand],
        2 => [FfxivEquipmentSlot.OffHand],
        3 => [FfxivEquipmentSlot.Head],
        4 => [FfxivEquipmentSlot.Body],
        5 => [FfxivEquipmentSlot.Hands],
        7 => [FfxivEquipmentSlot.Legs],
        8 => [FfxivEquipmentSlot.Feet],
        9 => [FfxivEquipmentSlot.Ears],
        10 => [FfxivEquipmentSlot.Neck],
        11 => [FfxivEquipmentSlot.Wrists],
        12 => [FfxivEquipmentSlot.Ring],
        17 => [FfxivEquipmentSlot.SoulCrystal],
        _ => [],
    };

    private static FfxivItemRarity? ResolveRarity(byte rarity) => rarity switch
    {
        1 => FfxivItemRarity.Common,
        2 => FfxivItemRarity.Uncommon,
        3 => FfxivItemRarity.Rare,
        4 => FfxivItemRarity.Relic,
        _ => null,
    };

    private static ItemMetadata Unknown(uint itemId) => new(
        itemId, $"Item {itemId}", null, false, null, null, null, null, null, null, null, null, null, null, null, null);
}
