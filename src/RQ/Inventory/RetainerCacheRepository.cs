using RQ.Domain;
using RQ.Persistence;
using Franthropy.Dalamud.Automation.Inventory;

namespace RQ.Inventory;

public sealed record CacheInvalidationResult(bool Removed, bool Persisted, string? Error);
public sealed record RetainerVariantObservation(
    ulong RetainerId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    DateTime ObservedAtUtc,
    IReadOnlyList<DalamudInventoryStack> Stacks);
public sealed record RetainerListingsObservation(
    ulong RetainerId,
    string RetainerName,
    OwnerScope Owner,
    DateTime ObservedAtUtc,
    IReadOnlyList<CachedMarketListing> Listings);

public sealed class RetainerCacheRepository
{
    private readonly object gate = new();
    private readonly RetainerCacheStore store;
    private Dictionary<ulong, CachedRetainer> cache;

    public RetainerCacheRepository(RetainerCacheStore store)
    {
        this.store = store;
        cache = store.Load();
    }

    public event Action? Changed;
    public long Revision { get; private set; }

    public IReadOnlyDictionary<ulong, CachedRetainer> Snapshot()
    {
        lock (gate)
            return new Dictionary<ulong, CachedRetainer>(cache);
    }

    public void Upsert(CachedRetainer retainer)
    {
        lock (gate)
        {
            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [retainer.RetainerId] = retainer };
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke();
    }

    public void ReplaceObservedVariant(RetainerVariantObservation observation)
    {
        if (observation.RetainerId == 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "A stable retainer identity is required.");
        if (observation.ItemId == 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "A stable item identity is required.");
        if (observation.Stacks.Any(stack =>
                stack.ItemId != observation.ItemId ||
                stack.IsHighQuality != observation.IsHighQuality ||
                stack.Quantity <= 0))
        {
            throw new InvalidOperationException("Observed stacks do not describe one valid item variant.");
        }

        lock (gate)
        {
            if (!cache.TryGetValue(observation.RetainerId, out var current))
                throw new KeyNotFoundException($"Retainer {observation.RetainerId} has no cached evidence to update.");

            var updated = Copy(current);
            var previousItems = updated.Bags
                .SelectMany(bag => bag.Items)
                .Where(item => item.ItemId == observation.ItemId && item.IsHq == observation.IsHighQuality)
                .ToArray();
            foreach (var bag in updated.Bags)
                bag.Items.RemoveAll(item => item.ItemId == observation.ItemId && item.IsHq == observation.IsHighQuality);

            foreach (var stack in observation.Stacks)
            {
                var container = stack.Container.ToString();
                var bag = updated.Bags.FirstOrDefault(candidate =>
                              string.Equals(candidate.Location, container, StringComparison.Ordinal) ||
                              string.Equals(candidate.BagName, container, StringComparison.Ordinal))
                          ?? AddBag(updated, container, observation.ObservedAtUtc);
                var template = previousItems.FirstOrDefault(item =>
                                   string.Equals(item.ContainerKey, container, StringComparison.Ordinal) &&
                                   item.SlotIndex == stack.SlotIndex)
                               ?? previousItems.FirstOrDefault();
                bag.Items.Add(new CachedItem
                {
                    ItemId = observation.ItemId,
                    ItemName = string.IsNullOrWhiteSpace(template?.ItemName) ? observation.ItemName : template.ItemName,
                    ItemType = template?.ItemType,
                    Quantity = checked((uint)stack.Quantity),
                    IsHq = observation.IsHighQuality,
                    Condition = template?.Condition ?? 0,
                    ConditionPercent = template?.ConditionPercent,
                    ContainerKey = container,
                    SlotIndex = stack.SlotIndex,
                    Equipped = template?.Equipped,
                });
            }

            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [observation.RetainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke();
    }

    public void ReplaceListings(RetainerListingsObservation observation)
    {
        if (observation.RetainerId == 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "A stable retainer identity is required.");
        if (!observation.Owner.HasStableIdentity)
            throw new InvalidOperationException("A stable owner identity is required.");

        lock (gate)
        {
            var updated = cache.TryGetValue(observation.RetainerId, out var current)
                ? Copy(current)
                : new CachedRetainer
                {
                    RetainerId = observation.RetainerId,
                    RetainerName = observation.RetainerName,
                    Owner = observation.Owner with { },
                    ObservedAtUtc = observation.ObservedAtUtc,
                };
            updated.RetainerName = observation.RetainerName;
            updated.Owner = observation.Owner with { };
            updated.ListingsObservedAtUtc = observation.ObservedAtUtc;
            updated.Listings = observation.Listings.Select(Copy).ToList();
            var marketSource = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket.ToString();
            if (!updated.RequestedSources.Contains(marketSource, StringComparer.Ordinal))
                updated.RequestedSources.Add(marketSource);
            if (!updated.ObservedSources.Contains(marketSource, StringComparer.Ordinal))
                updated.ObservedSources.Add(marketSource);

            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [observation.RetainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke();
    }

    public CacheInvalidationResult Invalidate(ulong retainerId)
    {
        Exception? persistenceError = null;
        bool removed;
        lock (gate)
        {
            var candidate = new Dictionary<ulong, CachedRetainer>(cache);
            removed = candidate.Remove(retainerId);
            if (removed)
            {
                cache = candidate;
                Revision++;
            }
            try
            {
                store.SaveAfterInvalidation(candidate);
            }
            catch (Exception exception)
            {
                persistenceError = exception;
            }
        }
        if (removed)
            Changed?.Invoke();
        return persistenceError is null
            ? new(removed, true, null)
            : new(removed, false, persistenceError.Message);
    }

    private static CachedBag AddBag(CachedRetainer retainer, string container, DateTime observedAtUtc)
    {
        var bag = new CachedBag
        {
            BagName = container,
            Location = container,
            ObservedAtUtc = observedAtUtc,
        };
        retainer.Bags.Add(bag);
        if (!retainer.ObservedSources.Contains(container, StringComparer.Ordinal))
            retainer.ObservedSources.Add(container);
        return bag;
    }

    private static CachedRetainer Copy(CachedRetainer source) => new()
    {
        RetainerId = source.RetainerId,
        RetainerName = source.RetainerName,
        Owner = source.Owner with { },
        ObservedAtUtc = source.ObservedAtUtc,
        Gil = source.Gil,
        GilObservedAtUtc = source.GilObservedAtUtc,
        ListingsObservedAtUtc = source.ListingsObservedAtUtc,
        RequestedSources = [.. source.RequestedSources],
        ObservedSources = [.. source.ObservedSources],
        Bags = source.Bags.Select(Copy).ToList(),
        Listings = source.Listings.Select(Copy).ToList(),
    };

    private static CachedBag Copy(CachedBag source) => new()
    {
        BagName = source.BagName,
        Location = source.Location,
        ObservedAtUtc = source.ObservedAtUtc,
        Items = source.Items.Select(Copy).ToList(),
    };

    private static CachedItem Copy(CachedItem source) => new()
    {
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        ItemType = source.ItemType,
        Quantity = source.Quantity,
        IsHq = source.IsHq,
        Condition = source.Condition,
        ConditionPercent = source.ConditionPercent,
        ContainerKey = source.ContainerKey,
        SlotIndex = source.SlotIndex,
        Equipped = source.Equipped,
    };

    private static CachedMarketListing Copy(CachedMarketListing source) => new()
    {
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        ItemType = source.ItemType,
        Quantity = source.Quantity,
        IsHq = source.IsHq,
        Condition = source.Condition,
        ConditionPercent = source.ConditionPercent,
        ContainerKey = source.ContainerKey,
        SlotIndex = source.SlotIndex,
        UnitPrice = source.UnitPrice,
        ListedAtUtc = source.ListedAtUtc,
    };
}
