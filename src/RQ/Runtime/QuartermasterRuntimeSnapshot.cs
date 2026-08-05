using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Runtime;

public sealed record QuartermasterRuntimeSnapshot(
    long Revision,
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

    public QuartermasterRuntimeSnapshot Refresh()
    {
        lock (gate)
            return RefreshCore();
    }

    public QuartermasterRuntimeSnapshot ApplyPlayerInventoryChange(PlayerInventoryCacheChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            var existing = current;
            if (existing is null || change.IsBaseline || !existing.Owner.Matches(change.Owner))
                return RefreshCore();

            var playerStorage = ApplyPlayerStorageChanges(existing.PlayerStorage, change);
            var browser = BrowserProjectionBuilder.ApplyPlayerChanges(existing.Browser, change, scanner.ResolveItemMetadata);
            var snapshot = BuildSnapshot(
                change.ObservedAtUtc,
                existing.Owner,
                playerStorage,
                existing.Retainers,
                existing.State,
                browser);
            Volatile.Write(ref current, snapshot);
            return snapshot;
        }
    }

    private QuartermasterRuntimeSnapshot RefreshCore()
    {
        var capturedAtUtc = DateTime.UtcNow;
        var owner = currentOwner();
        var playerStorage = playerInventory.Snapshot(owner, scanner.RequestedPlayerStorageSources());
        var retainers = cache.Snapshot();
        var stateSnapshot = state.Snapshot();
        var browser = BrowserProjectionBuilder.Build(playerStorage.Bags, retainers, owner, scanner.ResolveItemMetadata);
        var snapshot = BuildSnapshot(capturedAtUtc, owner, playerStorage, retainers, stateSnapshot, browser);
        Volatile.Write(ref current, snapshot);
        return snapshot;
    }

    private QuartermasterRuntimeSnapshot BuildSnapshot(
        DateTime capturedAtUtc,
        OwnerScope owner,
        PlayerStorageCapture playerStorage,
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
        var deposit = ElementalDepositPlanner.Build(crystalCounts, retainers, owner, scanner.ResolveItemName, capturedAtUtc);
        var stowage = StowageEvaluator.Build(stateSnapshot, browser, owner);
        var snapshot = new QuartermasterRuntimeSnapshot(
            Interlocked.Increment(ref revision),
            capturedAtUtc,
            owner,
            playerStorage,
            retainers,
            stateSnapshot,
            browser,
            retrieval,
            deposit,
            stowage);
        return snapshot;
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
