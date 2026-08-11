using RQ.Domain;
using RQ.Persistence;
using Franthropy.Dalamud.Automation.Inventory;

namespace RQ.Inventory;

public enum RetainerCacheChangeKind
{
    Stock,
    Listings,
    All,
}

[Flags]
public enum RetainerEvidenceDomain
{
    None = 0,
    Inventory = 1,
    Crystals = 2,
    Gil = 4,
    Listings = 8,
}

public sealed record RetainerRosterProjectionEntry(
    ulong RetainerId,
    string RetainerName,
    int DisplayOrder,
    bool? IsUiAccessible,
    byte ClassJobId,
    byte Level,
    byte MarketItemCount,
    bool IsGameAvailable = true);

public sealed record RetainerEvidenceReceipt(
    long Revision,
    ulong RetainerId,
    OwnerScope Owner,
    string EvidenceSessionId,
    RetainerEvidenceDomain Domains,
    DateTime ObservedAtUtc,
    string Code,
    string Message);

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
    IReadOnlyList<CachedMarketListing> Listings,
    string EvidenceSessionId = "");

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

    public event Action<RetainerCacheChangeKind>? Changed;
    public event Action<RetainerListingCaptureReceipt>? ListingCaptured;
    public event Action<RetainerEvidenceReceipt>? EvidenceAccepted;
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
        Changed?.Invoke(RetainerCacheChangeKind.All);
    }

    public void ReconcileRoster(
        OwnerScope owner,
        IReadOnlyList<RetainerRosterProjectionEntry> roster,
        DateTime observedAtUtc)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("A stable owner identity is required.");
        if (roster.Any(entry => entry.RetainerId == 0) ||
            roster.Select(entry => entry.RetainerId).Distinct().Count() != roster.Count)
            throw new InvalidOperationException("The assigned retainer roster is incomplete or duplicated.");

        lock (gate)
        {
            if (cache.Values.Any(retainer =>
                    retainer.Owner.Matches(owner) &&
                    retainer.RosterObservedAtUtc > observedAtUtc))
                return;
            var candidate = cache.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            foreach (var existing in candidate.Values.Where(retainer => retainer.Owner.Matches(owner)))
            {
                existing.IsCurrentlyAssigned = false;
                existing.RosterObservedAtUtc = observedAtUtc;
            }
            foreach (var entry in roster)
            {
                var current = candidate.GetValueOrDefault(entry.RetainerId) ?? new CachedRetainer
                {
                    RetainerId = entry.RetainerId,
                    Owner = owner with { },
                };
                current.RetainerName = entry.RetainerName;
                current.Owner = owner with { };
                current.IsCurrentlyAssigned = true;
                current.DisplayOrder = entry.DisplayOrder;
                if (entry.IsUiAccessible.HasValue &&
                    (!current.UiAccessibilityObservedAtUtc.HasValue || observedAtUtc >= current.UiAccessibilityObservedAtUtc))
                {
                    current.IsUiAccessible = entry.IsUiAccessible;
                    current.UiAccessibilityObservedAtUtc = observedAtUtc;
                }
                current.IsGameAvailable = entry.IsGameAvailable;
                current.ClassJobId = entry.ClassJobId;
                current.Level = entry.Level;
                current.MarketItemCount = entry.MarketItemCount;
                current.RosterObservedAtUtc = observedAtUtc;
                candidate[entry.RetainerId] = current;
            }
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke(RetainerCacheChangeKind.All);
    }

    public void ObserveUiAccessibility(
        OwnerScope owner,
        ulong retainerId,
        bool isAccessible,
        DateTime observedAtUtc)
    {
        if (!owner.HasStableIdentity || retainerId == 0)
            throw new InvalidOperationException("Stable owner and retainer identities are required.");
        lock (gate)
        {
            if (!cache.TryGetValue(retainerId, out var current) || !current.Owner.Matches(owner))
                return;
            if (current.UiAccessibilityObservedAtUtc.HasValue && current.UiAccessibilityObservedAtUtc > observedAtUtc)
                return;
            var updated = Copy(current);
            updated.IsUiAccessible = isAccessible;
            updated.UiAccessibilityObservedAtUtc = observedAtUtc;
            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [retainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke(RetainerCacheChangeKind.All);
    }

    public void ReplaceInventoryObservation(
        ulong retainerId,
        OwnerScope owner,
        DateTime observedAtUtc,
        IReadOnlyList<CachedBag> bags,
        IReadOnlyList<string> requestedSources,
        IReadOnlyList<string> observedSources,
        string evidenceSessionId = "")
    {
        if (retainerId == 0 || !owner.HasStableIdentity)
            throw new InvalidOperationException("Stable owner and retainer identities are required.");

        RetainerEvidenceReceipt receipt;
        lock (gate)
        {
            if (cache.TryGetValue(retainerId, out var existing) &&
                existing.Owner.Matches(owner) &&
                existing.ObservedAtUtc > observedAtUtc)
                return;
            var updated = cache.TryGetValue(retainerId, out var current)
                ? Copy(current)
                : new CachedRetainer { RetainerId = retainerId, Owner = owner with { } };
            updated.Owner = owner with { };
            updated.ObservedAtUtc = observedAtUtc;
            updated.Bags = bags.Select(Copy).ToList();
            updated.RequestedSources = requestedSources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            updated.ObservedSources = observedSources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [retainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            var revision = ++Revision;
            var domains = RetainerEvidenceDomain.None;
            if (InventoryScanner.RequiredRetainerContainers
                .Select(container => container.ToString())
                .All(updated.ObservedSources.Contains))
                domains |= RetainerEvidenceDomain.Inventory;
            if (updated.ObservedSources.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerCrystals.ToString(), StringComparer.Ordinal))
                domains |= RetainerEvidenceDomain.Crystals;
            receipt = new(revision, retainerId, owner with { }, evidenceSessionId, domains, observedAtUtc, "RetainerInventoryObserved", "Retainer inventory evidence was accepted.");
        }
        Changed?.Invoke(RetainerCacheChangeKind.Stock);
        EvidenceAccepted?.Invoke(receipt);
    }

    public void ReplaceGilObservation(
        ulong retainerId,
        OwnerScope owner,
        DateTime observedAtUtc,
        ulong gil,
        string evidenceSessionId = "")
    {
        if (retainerId == 0 || !owner.HasStableIdentity)
            throw new InvalidOperationException("Stable owner and retainer identities are required.");

        RetainerEvidenceReceipt receipt;
        lock (gate)
        {
            if (cache.TryGetValue(retainerId, out var existing) &&
                existing.Owner.Matches(owner) &&
                existing.GilObservedAtUtc > observedAtUtc)
                return;
            var updated = cache.TryGetValue(retainerId, out var current)
                ? Copy(current)
                : new CachedRetainer { RetainerId = retainerId, Owner = owner with { } };
            updated.Owner = owner with { };
            updated.Gil = gil;
            updated.GilObservedAtUtc = observedAtUtc;
            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [retainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            receipt = new(++Revision, retainerId, owner with { }, evidenceSessionId, RetainerEvidenceDomain.Gil, observedAtUtc, "RetainerGilObserved", "Retainer gil evidence was accepted.");
        }
        Changed?.Invoke(RetainerCacheChangeKind.Stock);
        EvidenceAccepted?.Invoke(receipt);
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
        Changed?.Invoke(RetainerCacheChangeKind.Stock);
    }

    public void ReplaceListings(RetainerListingsObservation observation)
    {
        if (observation.RetainerId == 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "A stable retainer identity is required.");
        if (!observation.Owner.HasStableIdentity)
            throw new InvalidOperationException("A stable owner identity is required.");

        RetainerListingCaptureReceipt receipt;
        RetainerEvidenceReceipt evidenceReceipt;
        lock (gate)
        {
            if (cache.TryGetValue(observation.RetainerId, out var existing) &&
                existing.Owner.Matches(observation.Owner) &&
                existing.ListingsObservedAtUtc > observation.ObservedAtUtc)
                return;
            var marketSource = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket.ToString();
            var comparisonAvailable = cache.TryGetValue(observation.RetainerId, out var current) &&
                                      observation.Owner.Matches(current.Owner) &&
                                      current.ListingsObservedAtUtc.HasValue &&
                                      current.ObservedSources.Contains(marketSource, StringComparer.Ordinal);
            var previousListings = comparisonAvailable
                ? current!.Listings.Select(Copy).ToArray()
                : [];
            var updated = current is not null
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
            if (!updated.RequestedSources.Contains(marketSource, StringComparer.Ordinal))
                updated.RequestedSources.Add(marketSource);
            if (!updated.ObservedSources.Contains(marketSource, StringComparer.Ordinal))
                updated.ObservedSources.Add(marketSource);

            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [observation.RetainerId] = updated };
            store.Save(candidate);
            cache = candidate;
            var revision = ++Revision;
            receipt = new RetainerListingCaptureReceipt
            {
                Semantics = RetainerListingCaptureReceipt.ChangedListingsV1,
                ComparisonAvailable = comparisonAvailable,
                CaptureId = Guid.NewGuid().ToString("N"),
                RetainerId = observation.RetainerId,
                Owner = observation.Owner with { },
                CapturedAtUtc = observation.ObservedAtUtc,
                Items = comparisonAvailable
                    ? ChangedListingItems(previousListings, updated.Listings)
                    : [],
            };
            evidenceReceipt = new(
                revision,
                observation.RetainerId,
                observation.Owner with { },
                observation.EvidenceSessionId,
                RetainerEvidenceDomain.Listings,
                observation.ObservedAtUtc,
                "RetainerListingsObserved",
                "Retainer listing evidence was accepted.");
        }
        Changed?.Invoke(RetainerCacheChangeKind.Listings);
        ListingCaptured?.Invoke(receipt);
        EvidenceAccepted?.Invoke(evidenceReceipt);
    }

    private static List<RetainerListingCaptureItem> ChangedListingItems(
        IReadOnlyList<CachedMarketListing> previous,
        IReadOnlyList<CachedMarketListing> current)
    {
        var previousByItem = previous
            .Where(listing => listing.ItemId != 0)
            .GroupBy(listing => listing.ItemId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var currentByItem = current
            .Where(listing => listing.ItemId != 0)
            .GroupBy(listing => listing.ItemId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return previousByItem.Keys
            .Concat(currentByItem.Keys)
            .Distinct()
            .Where(itemId => !SameMarketState(
                previousByItem.GetValueOrDefault(itemId) ?? [],
                currentByItem.GetValueOrDefault(itemId) ?? []))
            .Select(itemId => new RetainerListingCaptureItem
            {
                ItemId = itemId,
                ItemName = (currentByItem.GetValueOrDefault(itemId) ?? [])
                    .Concat(previousByItem.GetValueOrDefault(itemId) ?? [])
                    .Select(listing => listing.ItemName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
    }

    private static bool SameMarketState(
        IReadOnlyList<CachedMarketListing> previous,
        IReadOnlyList<CachedMarketListing> current)
    {
        if (previous.Count != current.Count)
            return false;

        var previousCounts = previous
            .Select(ListingSignature.From)
            .GroupBy(signature => signature)
            .ToDictionary(group => group.Key, group => group.Count());
        var currentCounts = current
            .Select(ListingSignature.From)
            .GroupBy(signature => signature)
            .ToDictionary(group => group.Key, group => group.Count());
        return previousCounts.Count == currentCounts.Count &&
               previousCounts.All(pair => currentCounts.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private readonly record struct ListingSignature(uint Quantity, bool IsHq, uint? UnitPrice)
    {
        public static ListingSignature From(CachedMarketListing listing) =>
            new(listing.Quantity, listing.IsHq, listing.UnitPrice);
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
            Changed?.Invoke(RetainerCacheChangeKind.All);
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
        IsCurrentlyAssigned = source.IsCurrentlyAssigned,
        DisplayOrder = source.DisplayOrder,
        IsUiAccessible = source.IsUiAccessible,
        UiAccessibilityObservedAtUtc = source.UiAccessibilityObservedAtUtc,
        IsGameAvailable = source.IsGameAvailable,
        ClassJobId = source.ClassJobId,
        Level = source.Level,
        MarketItemCount = source.MarketItemCount,
        RosterObservedAtUtc = source.RosterObservedAtUtc,
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
