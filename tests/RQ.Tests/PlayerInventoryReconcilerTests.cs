using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class PlayerInventoryReconcilerTests
{
    [Fact]
    public void Due_reconciliation_removes_stale_positive_without_polling_between_intervals()
    {
        using var directory = new TemporaryDirectory();
        var repository = new PlayerInventoryCacheRepository(
            new PlayerInventoryCacheStore(Path.Combine(directory.Path, "player-inventory-cache.json")));
        var startedAt = new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);
        repository.Observe(
            TestData.Owner,
            new PlayerStorageCapture(
                [PlayerBag("Inventory3", (5395, 25_982))],
                ["Inventory3"],
                ["Inventory3"]),
            startedAt.AddMinutes(-15));

        var captureCount = 0;
        var reconciler = new PlayerInventoryReconciler(
            repository,
            () => TestData.Owner,
            () =>
            {
                captureCount++;
                return new PlayerStorageCapture(
                    [PlayerBag("Inventory3")],
                    ["Inventory3"],
                    ["Inventory3"]);
            },
            TimeSpan.FromSeconds(1));

        Assert.True(reconciler.ReconcileIfDue(startedAt));
        Assert.Empty(Assert.Single(repository.Snapshot(TestData.Owner, ["Inventory3"]).Bags).Items);
        Assert.False(reconciler.ReconcileIfDue(startedAt.AddMilliseconds(999)));
        Assert.Equal(1, captureCount);
        Assert.False(reconciler.ReconcileIfDue(startedAt.AddSeconds(1)));
        Assert.Equal(2, captureCount);
    }

    [Fact]
    public void Reconciliation_waits_for_stable_identity_without_delaying_the_first_valid_capture()
    {
        using var directory = new TemporaryDirectory();
        var repository = new PlayerInventoryCacheRepository(
            new PlayerInventoryCacheStore(Path.Combine(directory.Path, "player-inventory-cache.json")));
        var owner = new OwnerScope();
        var captureCount = 0;
        var reconciler = new PlayerInventoryReconciler(
            repository,
            () => owner,
            () =>
            {
                captureCount++;
                return new PlayerStorageCapture([], ["Inventory1"], ["Inventory1"]);
            },
            TimeSpan.FromSeconds(1));
        var now = new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(reconciler.ReconcileIfDue(now));
        owner = TestData.Owner;
        Assert.True(reconciler.ReconcileIfDue(now.AddMilliseconds(1)));
        Assert.Equal(1, captureCount);
    }

    private static InventoryBag PlayerBag(string name, params (uint ItemId, uint Quantity)[] items) => new()
    {
        BagName = name,
        Location = name,
        Items = items.Select((item, index) => new InventoryItem
        {
            ItemId = item.ItemId,
            ItemName = $"Item {item.ItemId}",
            Quantity = item.Quantity,
            ContainerKey = name,
            SlotIndex = index,
        }).ToList(),
    };
}
