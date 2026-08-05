using Franthropy.Observations.V1;
using RQ.Domain;
using RQ.Persistence;
using InventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;

namespace RQ.Inventory;

public sealed record PlayerInventorySlotMutation(
    string ContainerKey,
    int SlotIndex,
    InventoryItem? Previous,
    InventoryItem? Current);

public sealed record PlayerInventoryCacheChange(
    OwnerScope Owner,
    DateTime ObservedAtUtc,
    bool IsBaseline,
    IReadOnlyList<PlayerInventorySlotMutation> Slots)
{
    public IReadOnlySet<uint> AffectedItemIds => Slots
        .SelectMany(slot => new uint?[] { slot.Previous?.ItemId, slot.Current?.ItemId })
        .Where(itemId => itemId is > 0)
        .Select(itemId => itemId!.Value)
        .ToHashSet();
}

public sealed class PlayerInventoryCacheRepository
{
    private readonly object gate = new();
    private readonly PlayerInventoryCacheStore store;
    private Dictionary<ulong, CachedPlayerInventory> cache;
    private bool persistenceDirty;

    public PlayerInventoryCacheRepository(PlayerInventoryCacheStore store)
    {
        this.store = store;
        cache = store.Load();
    }

    public event Action<PlayerInventoryCacheChange>? Changed;
    public long Revision { get; private set; }

    public PlayerStorageCapture Snapshot(OwnerScope owner, IReadOnlyList<string> requestedSources)
    {
        lock (gate)
        {
            if (owner.LocalContentId is not > 0 ||
                !cache.TryGetValue(owner.LocalContentId.Value, out var current) ||
                !owner.Matches(current.Owner))
            {
                return new([], requestedSources.ToArray(), []);
            }

            var requested = requestedSources.ToHashSet(StringComparer.Ordinal);
            return new(
                current.Bags
                    .Where(bag => requested.Contains(bag.BagName))
                    .Select(ToInventoryBag)
                    .ToArray(),
                requestedSources.ToArray(),
                current.ObservedSources.Where(requested.Contains).ToArray());
        }
    }

    public bool Observe(OwnerScope owner, PlayerStorageCapture observation, DateTime observedAtUtc)
    {
        if (!owner.HasStableIdentity)
            return false;

        bool changed;
        PlayerInventoryCacheChange? notification = null;
        lock (gate)
        {
            var contentId = owner.LocalContentId!.Value;
            var previous = cache.TryGetValue(contentId, out var persisted) && owner.Matches(persisted.Owner)
                ? persisted
                : null;
            var current = previous is not null
                ? Copy(previous)
                : new CachedPlayerInventory { Owner = owner with { } };
            var observed = observation.ObservedSources.ToHashSet(StringComparer.Ordinal);
            var observedBags = observation.Bags.ToDictionary(bag => bag.BagName, StringComparer.Ordinal);
            var bags = current.Bags.ToDictionary(bag => bag.BagName, StringComparer.Ordinal);

            foreach (var source in observed)
            {
                var live = observedBags.GetValueOrDefault(source) ?? new InventoryBag { BagName = source };
                bags[source] = new CachedPlayerBag
                {
                    BagName = source,
                    Location = live.Location,
                    ObservedAtUtc = observedAtUtc,
                    Items = live.Items.Select(Copy).ToList(),
                };
            }

            current.Owner = owner with { };
            current.UpdatedAtUtc = observedAtUtc;
            current.RequestedSources = observation.RequestedSources.Distinct(StringComparer.Ordinal).ToList();
            current.ObservedSources = observation.ObservedSources.Distinct(StringComparer.Ordinal).ToList();
            current.Bags = bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToList();

            changed = previous is null || !Equivalent(previous, current);
            if (!changed)
            {
                cache[contentId] = current;
                return false;
            }

            var affectedSources = observation.ObservedSources.ToHashSet(StringComparer.Ordinal);
            var oldSlots = previous?.Bags
                .Where(bag => affectedSources.Contains(bag.BagName))
                .SelectMany(bag => bag.Items.Where(item => item.SlotIndex is not null).Select(item => (bag.BagName, Item: item)))
                .ToDictionary(entry => (entry.BagName, SlotIndex: entry.Item.SlotIndex!.Value)) ?? [];
            var newSlots = current.Bags
                .Where(bag => affectedSources.Contains(bag.BagName))
                .SelectMany(bag => bag.Items.Where(item => item.SlotIndex is not null).Select(item => (bag.BagName, Item: item)))
                .ToDictionary(entry => (entry.BagName, SlotIndex: entry.Item.SlotIndex!.Value));
            notification = new(
                owner with { },
                observedAtUtc,
                true,
                oldSlots.Keys.Union(newSlots.Keys)
                    .OrderBy(key => key.BagName, StringComparer.Ordinal)
                    .ThenBy(key => key.SlotIndex)
                    .Select(key => new PlayerInventorySlotMutation(
                        key.BagName,
                        key.SlotIndex,
                        oldSlots.TryGetValue(key, out var old) ? Copy(old.Item) : null,
                        newSlots.TryGetValue(key, out var next) ? Copy(next.Item) : null))
                    .ToArray());

            var candidate = new Dictionary<ulong, CachedPlayerInventory>(cache) { [contentId] = current };
            cache = candidate;
            persistenceDirty = true;
            Revision++;
        }

        Changed?.Invoke(notification!);
        return true;
    }

    public bool ApplyChanges(
        OwnerScope owner,
        IReadOnlyList<InventoryChangeBatch> batches,
        Func<uint, ItemMetadata> resolveMetadata)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(resolveMetadata);
        if (!owner.HasStableIdentity)
            return false;

        PlayerInventoryCacheChange? notification = null;
        lock (gate)
        {
            var relevant = batches
                .Where(batch => batch.Scope.Owner.LocalContentId == owner.LocalContentId &&
                                batch.Scope.Owner.HomeWorldId == owner.HomeWorldId &&
                                batch.Scope.Subject.Kind == ObservationSubjectKind.Character &&
                                batch.Scope.Container is ObservationContainerKind.PlayerInventory or ObservationContainerKind.Saddlebag)
                .OrderBy(batch => batch.Revision)
                .ToArray();
            if (relevant.Length == 0)
                return false;

            var contentId = owner.LocalContentId!.Value;
            var existing = cache.TryGetValue(contentId, out var persisted) && owner.Matches(persisted.Owner)
                ? persisted
                : new CachedPlayerInventory { Owner = owner with { } };
            var current = new CachedPlayerInventory
            {
                Owner = owner with { },
                UpdatedAtUtc = existing.UpdatedAtUtc,
                RequestedSources = [.. existing.RequestedSources],
                ObservedSources = [.. existing.ObservedSources],
                Bags = [.. existing.Bags],
            };
            var changedBags = new Dictionary<string, CachedPlayerBag>(StringComparer.Ordinal);
            var mutations = new List<PlayerInventorySlotMutation>();
            foreach (var batch in relevant)
            {
                var observedAtUtc = batch.Capture.ObservedAtUtc.UtcDateTime;
                current.UpdatedAtUtc = current.UpdatedAtUtc > observedAtUtc ? current.UpdatedAtUtc : observedAtUtc;
                foreach (var change in batch.Changes)
                {
                    var containerKey = ((InventoryType)change.ContainerId).ToString();
                    if (!changedBags.TryGetValue(containerKey, out var bag))
                    {
                        var existingBag = current.Bags.FirstOrDefault(candidate => candidate.BagName == containerKey);
                        bag = existingBag is null
                            ? new CachedPlayerBag { BagName = containerKey, Location = containerKey }
                            : new CachedPlayerBag
                            {
                                BagName = existingBag.BagName,
                                Location = existingBag.Location,
                                ObservedAtUtc = existingBag.ObservedAtUtc,
                                Items = existingBag.Items.Select(Copy).ToList(),
                            };
                        changedBags.Add(containerKey, bag);
                    }

                    bag.ObservedAtUtc = observedAtUtc;
                    var index = bag.Items.FindIndex(item => item.SlotIndex == change.SlotIndex);
                    var previous = index >= 0 ? Copy(bag.Items[index]) : null;
                    InventoryItem? next = null;
                    if (change.Current is { } value)
                    {
                        var metadata = resolveMetadata(value.ItemId);
                        var preserveInstance = previous?.ItemId == value.ItemId && previous.IsHq == value.IsHighQuality;
                        next = new InventoryItem
                        {
                            ItemId = value.ItemId,
                            ItemName = metadata.Name,
                            Quantity = checked((uint)value.Quantity),
                            IsHq = value.IsHighQuality,
                            ItemType = metadata.ItemType,
                            Condition = preserveInstance ? previous!.Condition : 0,
                            ConditionPercent = preserveInstance ? previous!.ConditionPercent : null,
                            ContainerKey = containerKey,
                            SlotIndex = change.SlotIndex,
                            Equipped = (InventoryType)change.ContainerId == InventoryType.EquippedItems,
                        };
                    }

                    if (index >= 0)
                        bag.Items.RemoveAt(index);
                    if (next is not null)
                        bag.Items.Add(next);
                    bag.Items.Sort((left, right) => Nullable.Compare(left.SlotIndex, right.SlotIndex));
                    mutations.Add(new(containerKey, change.SlotIndex, previous, next is null ? null : Copy(next)));
                }
            }

            foreach (var (containerKey, bag) in changedBags)
            {
                current.Bags.RemoveAll(candidate => candidate.BagName == containerKey);
                current.Bags.Add(bag);
                if (!current.ObservedSources.Contains(containerKey, StringComparer.Ordinal))
                    current.ObservedSources.Add(containerKey);
            }
            current.Bags = current.Bags.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToList();
            current.ObservedSources = current.ObservedSources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (mutations.Count == 0)
                return false;

            cache = new Dictionary<ulong, CachedPlayerInventory>(cache) { [contentId] = current };
            persistenceDirty = true;
            Revision++;
            notification = new(owner with { }, current.UpdatedAtUtc, false, mutations);
        }

        Changed?.Invoke(notification!);
        return true;
    }

    public void Flush()
    {
        lock (gate)
        {
            if (!persistenceDirty)
                return;
            store.Save(cache);
            persistenceDirty = false;
        }
    }

    private static bool Equivalent(CachedPlayerInventory left, CachedPlayerInventory right)
    {
        if (!left.Owner.Matches(right.Owner) ||
            !left.RequestedSources.SequenceEqual(right.RequestedSources, StringComparer.Ordinal) ||
            !left.ObservedSources.SequenceEqual(right.ObservedSources, StringComparer.Ordinal) ||
            left.Bags.Count != right.Bags.Count)
        {
            return false;
        }

        return left.Bags.Zip(right.Bags).All(pair =>
            pair.First.BagName == pair.Second.BagName &&
            pair.First.Location == pair.Second.Location &&
            ItemsEquivalent(pair.First.Items, pair.Second.Items));
    }

    private static bool ItemsEquivalent(IReadOnlyList<InventoryItem> left, IReadOnlyList<InventoryItem> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.ItemId == pair.Second.ItemId &&
            pair.First.Quantity == pair.Second.Quantity &&
            pair.First.IsHq == pair.Second.IsHq &&
            pair.First.Condition == pair.Second.Condition &&
            pair.First.ContainerKey == pair.Second.ContainerKey &&
            pair.First.SlotIndex == pair.Second.SlotIndex &&
            pair.First.Equipped == pair.Second.Equipped);

    private static InventoryBag ToInventoryBag(CachedPlayerBag source) => new()
    {
        BagName = source.BagName,
        Location = source.Location,
        Items = source.Items.Select(Copy).ToList(),
    };

    private static CachedPlayerInventory Copy(CachedPlayerInventory source) => new()
    {
        Owner = source.Owner with { },
        UpdatedAtUtc = source.UpdatedAtUtc,
        RequestedSources = [.. source.RequestedSources],
        ObservedSources = [.. source.ObservedSources],
        Bags = source.Bags.Select(bag => new CachedPlayerBag
        {
            BagName = bag.BagName,
            Location = bag.Location,
            ObservedAtUtc = bag.ObservedAtUtc,
            Items = bag.Items.Select(Copy).ToList(),
        }).ToList(),
    };

    private static InventoryItem Copy(InventoryItem source) => new()
    {
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        Quantity = source.Quantity,
        IsHq = source.IsHq,
        ItemType = source.ItemType,
        Condition = source.Condition,
        ConditionPercent = source.ConditionPercent,
        ContainerKey = source.ContainerKey,
        SlotIndex = source.SlotIndex,
        Equipped = source.Equipped,
    };
}
