using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Runtime;

public sealed record QuartermasterRuntimeSnapshot(
    long Revision,
    long StockRevision,
    long PlanningRevision,
    long ListingsRevision,
    long OperationsRevision,
    DateTime CapturedAtUtc,
    OwnerScope Owner,
    PlayerStorageCapture PlayerStorage,
    IReadOnlyDictionary<ulong, CachedRetainer> Retainers,
    QuartermasterState State,
    BrowserProjection Browser,
    RetrievalPlan Retrieval,
    ElementalDepositPlan Deposit,
    IReadOnlyList<StowageEvaluation> Stowage);

public sealed class QuartermasterRuntimeSnapshotSource
{
    private readonly object gate = new();
    private readonly InventoryScanner scanner;
    private readonly PlayerInventoryCacheRepository playerInventory;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly Func<OwnerScope> currentOwner;
    private QuartermasterRuntimeSnapshot? current;
    private long revision;
    private long stockRevision;
    private long planningRevision;
    private long listingsRevision;
    private long operationsRevision;

    public QuartermasterRuntimeSnapshotSource(
        InventoryScanner scanner,
        PlayerInventoryCacheRepository playerInventory,
        RetainerCacheRepository cache,
        StateRepository state,
        Func<OwnerScope> currentOwner)
    {
        this.scanner = scanner;
        this.playerInventory = playerInventory;
        this.cache = cache;
        this.state = state;
        this.currentOwner = currentOwner;
    }

    public QuartermasterRuntimeSnapshot Current => Volatile.Read(ref current)
        ?? throw new InvalidOperationException("Quartermaster runtime snapshot has not been initialized.");

    public QuartermasterRuntimeSnapshot Refresh() => Refresh(RuntimeDomain.All);

    public QuartermasterRuntimeSnapshot Refresh(
        RuntimeDomain domains,
        PlayerInventoryCacheChange? playerChange = null)
    {
        lock (gate)
        {
            var existing = current;
            var owner = currentOwner();
            if (existing is null || domains == RuntimeDomain.All || !existing.Owner.Matches(owner))
                return RefreshAll(owner);
            if (domains == RuntimeDomain.None)
                return existing;

            var capturedAtUtc = DateTime.UtcNow;
            // Operation history is read directly by its owner. It receives its
            // own revision without cloning the large plan document into every
            // runtime snapshot during a transfer.
            var stateChanged = (domains & (RuntimeDomain.Plans | RuntimeDomain.Listings)) != 0;
            var retainersChanged = (domains & (RuntimeDomain.RetainerStock | RuntimeDomain.Listings)) != 0;
            var stockChanged = (domains & (RuntimeDomain.PlayerInventory | RuntimeDomain.RetainerStock)) != 0;
            var planningChanged = stockChanged || (domains & RuntimeDomain.Plans) != 0;
            var listingsChanged = (domains & RuntimeDomain.Listings) != 0;
            var operationsChanged = (domains & RuntimeDomain.Operations) != 0;

            var stateSnapshot = stateChanged ? state.Snapshot() : existing.State;
            var retainers = retainersChanged ? cache.Snapshot() : existing.Retainers;
            var playerStorage = existing.PlayerStorage;
            var browser = existing.Browser;

            if ((domains & RuntimeDomain.PlayerInventory) != 0)
            {
                if (playerChange is { IsBaseline: false } change && existing.Owner.Matches(change.Owner))
                {
                    playerStorage = ApplyPlayerStorageChanges(existing.PlayerStorage, change);
                    browser = BrowserProjectionBuilder.ApplyPlayerChanges(browser, change, scanner.ResolveItemMetadata);
                    capturedAtUtc = change.ObservedAtUtc;
                }
                else
                {
                    playerStorage = playerInventory.Snapshot(owner, scanner.RequestedPlayerStorageSources());
                    browser = BrowserProjectionBuilder.RefreshRetainerStock(
                        browser,
                        playerStorage.Bags,
                        retainers,
                        owner,
                        scanner.ResolveItemMetadata);
                }
            }

            if ((domains & RuntimeDomain.RetainerStock) != 0)
            {
                browser = BrowserProjectionBuilder.RefreshRetainerStock(
                    browser,
                    playerStorage.Bags,
                    retainers,
                    owner,
                    scanner.ResolveItemMetadata);
            }

            if (listingsChanged)
                browser = BrowserProjectionBuilder.RefreshListings(browser, retainers, owner, scanner.ResolveItemMetadata);

            var retrieval = existing.Retrieval;
            var deposit = existing.Deposit;
            var stowage = existing.Stowage;
            if (planningChanged)
                (retrieval, deposit, stowage) = BuildPlanning(capturedAtUtc, owner, retainers, stateSnapshot, browser);

            var snapshot = new QuartermasterRuntimeSnapshot(
                Interlocked.Increment(ref revision),
                stockChanged ? Interlocked.Increment(ref stockRevision) : existing.StockRevision,
                planningChanged ? Interlocked.Increment(ref planningRevision) : existing.PlanningRevision,
                listingsChanged ? Interlocked.Increment(ref listingsRevision) : existing.ListingsRevision,
                operationsChanged ? Interlocked.Increment(ref operationsRevision) : existing.OperationsRevision,
                capturedAtUtc,
                owner,
                playerStorage,
                retainers,
                stateSnapshot,
                browser,
                retrieval,
                deposit,
                stowage);
            Volatile.Write(ref current, snapshot);
            return snapshot;
        }
    }

    public QuartermasterRuntimeSnapshot ApplyPlayerInventoryChange(PlayerInventoryCacheChange change) =>
        Refresh(RuntimeDomain.PlayerInventory, change);

    private QuartermasterRuntimeSnapshot RefreshAll(OwnerScope owner)
    {
        var capturedAtUtc = DateTime.UtcNow;
        var playerStorage = playerInventory.Snapshot(owner, scanner.RequestedPlayerStorageSources());
        var retainers = cache.Snapshot();
        var stateSnapshot = state.Snapshot();
        var browser = BrowserProjectionBuilder.Build(playerStorage.Bags, retainers, owner, scanner.ResolveItemMetadata);
        var (retrieval, deposit, stowage) = BuildPlanning(capturedAtUtc, owner, retainers, stateSnapshot, browser);
        var snapshot = new QuartermasterRuntimeSnapshot(
            Interlocked.Increment(ref revision),
            Interlocked.Increment(ref stockRevision),
            Interlocked.Increment(ref planningRevision),
            Interlocked.Increment(ref listingsRevision),
            Interlocked.Increment(ref operationsRevision),
            capturedAtUtc,
            owner,
            playerStorage,
            retainers,
            stateSnapshot,
            browser,
            retrieval,
            deposit,
            stowage);
        Volatile.Write(ref current, snapshot);
        return snapshot;
    }

    private (RetrievalPlan Retrieval, ElementalDepositPlan Deposit, IReadOnlyList<StowageEvaluation> Stowage) BuildPlanning(
        DateTime capturedAtUtc,
        OwnerScope owner,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        QuartermasterState stateSnapshot,
        BrowserProjection browser)
    {
        var playerCounts = browser.Items
            .Where(item => item.PlayerQuantity > 0)
            .ToDictionary(item => item.ItemId, item => item.PlayerQuantity);
        var ownerRules = StowagePlanMigration.OwnerRules(stateSnapshot, owner);
        var retrieval = RestockPlanner.Build(ownerRules, playerCounts, retainers, owner, capturedAtUtc, browser);
        var crystalContainer = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Crystals.ToString();
        var crystalCounts = browser.Items
            .Select(item => new
            {
                item.ItemId,
                Quantity = item.Stacks
                    .Where(stack => stack.ScopeKind == BrowserScopeKind.Player && stack.Storage == crystalContainer)
                    .Sum(stack => stack.Quantity),
            })
            .Where(item => item.Quantity > 0)
            .ToDictionary(item => item.ItemId, item => item.Quantity);
        return (
            retrieval,
            ElementalDepositPlanner.Build(crystalCounts, retainers, owner, scanner.ResolveItemName, capturedAtUtc),
            StowageEvaluator.Build(stateSnapshot, browser, owner));
    }

    private static PlayerStorageCapture ApplyPlayerStorageChanges(
        PlayerStorageCapture current,
        PlayerInventoryCacheChange change)
    {
        var bags = current.Bags.ToDictionary(bag => bag.BagName, StringComparer.Ordinal);
        foreach (var group in change.Slots.GroupBy(slot => slot.ContainerKey, StringComparer.Ordinal))
        {
            var source = bags.GetValueOrDefault(group.Key) ?? new InventoryBag { BagName = group.Key, Location = group.Key };
            var items = source.Items
                .Where(item => item.SlotIndex is not null)
                .ToDictionary(item => item.SlotIndex!.Value);
            foreach (var slot in group)
            {
                if (slot.Current is null)
                    items.Remove(slot.SlotIndex);
                else
                    items[slot.SlotIndex] = Copy(slot.Current);
            }
            bags[group.Key] = new InventoryBag
            {
                BagName = source.BagName,
                Location = source.Location,
                Items = items.Values.OrderBy(item => item.SlotIndex).ToList(),
            };
        }
        return new(
            bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToArray(),
            current.RequestedSources,
            current.ObservedSources.Union(change.Slots.Select(slot => slot.ContainerKey), StringComparer.Ordinal).ToArray());
    }

    private static RQ.Domain.InventoryItem Copy(RQ.Domain.InventoryItem source) => new()
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
