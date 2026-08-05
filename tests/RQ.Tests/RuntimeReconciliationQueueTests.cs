using RQ.Domain;
using RQ.Inventory;
using RQ.Runtime;

namespace RQ.Tests;

public sealed class RuntimeReconciliationQueueTests
{
    [Fact]
    public void RepeatedRequests_CoalesceByDomainAndNoticeIdentity()
    {
        var queue = new RuntimeReconciliationQueue();

        queue.Request(RuntimeDomain.Operations, "operation", "op-1");
        queue.Request(RuntimeDomain.Operations, "operation", "op-1");
        queue.Request(RuntimeDomain.Plans, "state");

        var batch = queue.Drain();

        Assert.Equal(RuntimeDomain.Operations | RuntimeDomain.Plans, batch.Domains);
        Assert.Equal(2, batch.Notices.Count);
        Assert.False(queue.Drain().HasWork);
    }

    [Fact]
    public void PlayerSlotBurst_PreservesFirstBeforeAndFinalAfter()
    {
        var queue = new RuntimeReconciliationQueue();
        var first = Item(100, 10);
        var middle = Item(100, 7);
        var final = Item(100, 4);

        queue.Request(Change(new PlayerInventorySlotMutation("Inventory1", 3, first, middle), 1));
        queue.Request(Change(new PlayerInventorySlotMutation("Inventory1", 3, middle, final), 2));

        var change = Assert.IsType<PlayerInventoryCacheChange>(queue.Drain().PlayerInventoryChange);
        var slot = Assert.Single(change.Slots);
        Assert.Same(first, slot.Previous);
        Assert.Same(final, slot.Current);
        Assert.Equal(new DateTime(2026, 8, 5, 12, 0, 2, DateTimeKind.Utc), change.ObservedAtUtc);
    }

    [Fact]
    public void RestrictedDrain_LeavesExpensiveStockWorkQueued()
    {
        var queue = new RuntimeReconciliationQueue();
        queue.Request(RuntimeDomain.RetainerStock | RuntimeDomain.Listings, "cache");
        queue.Request(RuntimeDomain.Operations, "operation", "op-1");

        var duringTransfer = queue.Drain(RuntimeDomain.Listings | RuntimeDomain.Operations);
        var afterTransfer = queue.Drain();

        Assert.Equal(RuntimeDomain.Listings | RuntimeDomain.Operations, duringTransfer.Domains);
        Assert.Equal(RuntimeDomain.RetainerStock, afterTransfer.Domains);
        Assert.Contains(afterTransfer.Notices, notice => notice.Kind == "cache" && notice.Domain == RuntimeDomain.RetainerStock);
    }

    [Fact]
    public void RequestAfterDrain_RemainsForNextCheckpoint()
    {
        var queue = new RuntimeReconciliationQueue();
        queue.Request(RuntimeDomain.Operations, "operation", "op-1");
        Assert.True(queue.Drain().HasWork);

        queue.Request(RuntimeDomain.RetainerStock, "cache");

        Assert.Equal(RuntimeDomain.RetainerStock, queue.Drain().Domains);
    }

    private static PlayerInventoryCacheChange Change(PlayerInventorySlotMutation mutation, int second) => new(
        TestData.Owner,
        new DateTime(2026, 8, 5, 12, 0, second, DateTimeKind.Utc),
        false,
        [mutation]);

    private static InventoryItem Item(uint itemId, uint quantity) => new()
    {
        ItemId = itemId,
        ItemName = $"Item {itemId}",
        Quantity = quantity,
        ContainerKey = "Inventory1",
        SlotIndex = 3,
    };
}
