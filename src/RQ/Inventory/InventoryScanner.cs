using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using RQ.Domain;
using GameInventoryItem = FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;

namespace RQ.Inventory;

public sealed record RetainerCapture(
    IReadOnlyList<CachedBag> Bags,
    IReadOnlySet<InventoryType> LoadedContainers,
    ulong? Gil,
    IReadOnlyList<CachedMarketListing>? Listings);
public sealed record PlayerStorageOptions(bool IncludeArmoury, bool IncludeCrystals, bool IncludeEquipped, bool IncludeSaddlebag);
public sealed record PlayerStorageCapture(IReadOnlyList<InventoryBag> Bags, IReadOnlyList<string> RequestedSources, IReadOnlyList<string> ObservedSources);

public sealed class InventoryScanner
{
    public static readonly IReadOnlyList<InventoryType> PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public static readonly IReadOnlyList<InventoryType> ArmouryContainers =
    [
        InventoryType.ArmoryBody, InventoryType.ArmoryEar, InventoryType.ArmoryFeets, InventoryType.ArmoryHands,
        InventoryType.ArmoryHead, InventoryType.ArmoryLegs, InventoryType.ArmoryMainHand, InventoryType.ArmoryNeck,
        InventoryType.ArmoryOffHand, InventoryType.ArmoryRings, InventoryType.ArmoryWrist, InventoryType.ArmorySoulCrystal,
    ];

    public static readonly IReadOnlyList<InventoryType> RetainerContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerCrystals,
    ];

    public static readonly IReadOnlyList<InventoryType> RequiredRetainerContainers = RetainerContainers.Take(7).ToArray();

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Func<PlayerStorageOptions> storageOptions;
    private readonly ItemMetadataCatalog metadata;

    public InventoryScanner(IDataManager dataManager, IPluginLog log, Func<PlayerStorageOptions>? storageOptions = null)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.storageOptions = storageOptions ?? (() => new(false, true, false, false));
        metadata = new(dataManager, log);
    }

    public IReadOnlyList<InventoryBag> ScanPlayerBags() => CapturePlayerStorage().Bags;

    public unsafe PlayerStorageCapture CapturePlayerStorage()
    {
        var options = storageOptions();
        var types = PlayerStorageTypes(options);
        var manager = InventoryManager.Instance();
        if (manager is null)
            return new([], types.Select(type => type.ToString()).ToArray(), []);
        var result = new List<InventoryBag>();
        var observed = new List<string>();
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;
            observed.Add(type.ToString());
            var bag = new InventoryBag { BagName = type.ToString(), Location = ResolveLocation(type) };
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot is null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;
                var definition = metadata.Resolve(slot->ItemId);
                bag.Items.Add(new RQ.Domain.InventoryItem
                {
                    ItemId = slot->ItemId,
                    ItemName = definition.Name,
                    ItemType = definition.ItemType,
                    Quantity = checked((uint)slot->Quantity),
                    IsHq = (slot->Flags & GameInventoryItem.ItemFlags.HighQuality) != 0,
                    Condition = definition.SupportsCondition ? slot->Condition / 30000f : 0,
                    ConditionPercent = definition.SupportsCondition ? slot->Condition / 300f : null,
                    ContainerKey = type.ToString(),
                    SlotIndex = slotIndex,
                    Equipped = type == InventoryType.EquippedItems,
                });
            }
            result.Add(bag);
        }
        return new(result, types.Select(type => type.ToString()).ToArray(), observed);
    }

    public IReadOnlyList<string> RequestedPlayerStorageSources() =>
        PlayerStorageTypes(storageOptions()).Select(type => type.ToString()).ToArray();

    private static InventoryType[] PlayerStorageTypes(PlayerStorageOptions options) => PlayerBags
        .Concat(options.IncludeEquipped ? [InventoryType.EquippedItems] : [])
        .Concat(options.IncludeArmoury ? ArmouryContainers : [])
        .Concat(options.IncludeCrystals ? [InventoryType.Crystals] : [])
        .Concat(options.IncludeSaddlebag ? [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2] : [])
        .ToArray();

    public IReadOnlyDictionary<uint, int> CountPlayerItems() => ScanPlayerBags()
        .SelectMany(bag => bag.Items)
        .GroupBy(item => item.ItemId)
        .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));

    public unsafe IReadOnlyDictionary<uint, int> CountPlayerCrystals()
    {
        var manager = InventoryManager.Instance();
        var container = manager is null ? null : manager->GetInventoryContainer(InventoryType.Crystals);
        if (container is null || !container->IsLoaded)
            return new Dictionary<uint, int>();
        var quantities = new Dictionary<uint, int>();
        for (var index = 0; index < container->Size; index++)
        {
            var slot = container->GetInventorySlot(index);
            if (slot is not null && slot->ItemId > 0 && slot->Quantity > 0)
                quantities[slot->ItemId] = quantities.GetValueOrDefault(slot->ItemId) + checked((int)slot->Quantity);
        }
        return quantities;
    }

    public unsafe RetainerCapture CaptureRetainer()
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return new([], new HashSet<InventoryType>(), null, null);
        var loaded = new HashSet<InventoryType>();
        var bags = new List<CachedBag>();
        foreach (var type in RetainerContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;
            loaded.Add(type);
            var bag = new CachedBag { BagName = type.ToString(), Location = type.ToString() };
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot is null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;
                var definition = metadata.Resolve(slot->ItemId);
                bag.Items.Add(new CachedItem
                {
                    ItemId = slot->ItemId,
                    ItemName = definition.Name,
                    ItemType = definition.ItemType,
                    Quantity = checked((uint)slot->Quantity),
                    IsHq = (slot->Flags & GameInventoryItem.ItemFlags.HighQuality) != 0,
                    Condition = definition.SupportsCondition ? slot->Condition / 30000f : 0,
                    ConditionPercent = definition.SupportsCondition ? slot->Condition / 300f : null,
                    ContainerKey = type.ToString(),
                    SlotIndex = slotIndex,
                });
            }
            bags.Add(bag);
        }
        var gil = ReadSingleContainerQuantity(manager, InventoryType.RetainerGil, loaded);
        var listings = ReadListings(manager, loaded);
        return new(bags, loaded, gil, listings);
    }

    public unsafe IReadOnlyList<CachedMarketListing>? CaptureRetainerListings()
    {
        var manager = InventoryManager.Instance();
        return manager is null ? null : ReadListings(manager, new HashSet<InventoryType>());
    }

    public string ResolveItemName(uint itemId)
    {
        try
        {
            var name = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
        }
        catch (Exception exception)
        {
            log.Debug(exception, $"Unable to resolve item name for {itemId}.");
            return $"Item {itemId}";
        }
    }

    public ItemMetadata ResolveItemMetadata(uint itemId) => metadata.Resolve(itemId);

    private static unsafe ulong? ReadSingleContainerQuantity(InventoryManager* manager, InventoryType type, ISet<InventoryType> loaded)
    {
        var container = manager->GetInventoryContainer(type);
        if (container is null || !container->IsLoaded)
            return null;
        loaded.Add(type);
        ulong total = 0;
        for (var index = 0; index < container->Size; index++)
        {
            var slot = container->GetInventorySlot(index);
            if (slot != null)
                total = checked(total + (ulong)slot->Quantity);
        }
        return total;
    }

    private unsafe IReadOnlyList<CachedMarketListing>? ReadListings(InventoryManager* manager, ISet<InventoryType> loaded)
    {
        var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container is null || !container->IsLoaded)
            return null;
        loaded.Add(InventoryType.RetainerMarket);
        var now = DateTime.UtcNow;
        var result = new List<CachedMarketListing>();
        for (var index = 0; index < container->Size; index++)
        {
            var slot = container->GetInventorySlot(index);
            if (slot is null || slot->ItemId == 0 || slot->Quantity == 0)
                continue;
            var definition = metadata.Resolve(slot->ItemId);
            var observedPrice = manager->GetRetainerMarketPrice(checked((short)index));
            result.Add(new CachedMarketListing
            {
                ItemId = slot->ItemId,
                ItemName = definition.Name,
                ItemType = definition.ItemType,
                Quantity = checked((uint)slot->Quantity),
                IsHq = (slot->Flags & GameInventoryItem.ItemFlags.HighQuality) != 0,
                Condition = definition.SupportsCondition ? slot->Condition / 30000f : 0,
                ConditionPercent = definition.SupportsCondition ? slot->Condition / 300f : null,
                ContainerKey = InventoryType.RetainerMarket.ToString(),
                SlotIndex = index,
                UnitPrice = observedPrice is > 0 and <= uint.MaxValue ? (uint)observedPrice : null,
                ListedAtUtc = now,
            });
        }
        return result;
    }

    private static string ResolveLocation(InventoryType type)
    {
        if (type == InventoryType.EquippedItems)
            return "Equipped";
        if (ArmouryContainers.Contains(type))
            return "Armoury";
        if (type is InventoryType.SaddleBag1 or InventoryType.SaddleBag2 or InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2)
            return "Saddlebag";
        return "Inventory";
    }
}
