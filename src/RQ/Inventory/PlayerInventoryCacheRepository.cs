using RQ.Domain;
using RQ.Persistence;

namespace RQ.Inventory;

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

    public event Action? Changed;
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
        lock (gate)
        {
            var contentId = owner.LocalContentId!.Value;
            var current = cache.TryGetValue(contentId, out var existing) && owner.Matches(existing.Owner)
                ? Copy(existing)
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

            changed = !cache.TryGetValue(contentId, out var previous) || !Equivalent(previous, current);
            if (!changed)
            {
                cache[contentId] = current;
                return false;
            }

            var candidate = new Dictionary<ulong, CachedPlayerInventory>(cache) { [contentId] = current };
            cache = candidate;
            persistenceDirty = true;
            Revision++;
        }

        Changed?.Invoke();
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
