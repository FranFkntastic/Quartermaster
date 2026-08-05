using Franthropy.Observations.V1;
using RQ;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void PlayerInventoryCache_PreservesUnavailableContainersAndClearsObservedEmptyContainers()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "player-inventory-cache.json");
        var repository = new PlayerInventoryCacheRepository(new PlayerInventoryCacheStore(path));
        var firstObservation = new PlayerStorageCapture(
            [
                PlayerBag("Inventory1", (100, 12)),
                PlayerBag("Crystals", (2, 8080)),
            ],
            ["Inventory1", "Crystals"],
            ["Inventory1", "Crystals"]);

        Assert.True(repository.Observe(TestData.Owner, firstObservation, new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(repository.Observe(
            TestData.Owner,
            new PlayerStorageCapture(
                [PlayerBag("Inventory1")],
                ["Inventory1", "Crystals"],
                ["Inventory1"]),
            new DateTime(2026, 7, 26, 12, 0, 1, DateTimeKind.Utc)));
        repository.Flush();

        var restarted = new PlayerInventoryCacheRepository(new PlayerInventoryCacheStore(path));
        var snapshot = restarted.Snapshot(TestData.Owner, ["Inventory1", "Crystals"]);

        Assert.Empty(Assert.Single(snapshot.Bags, bag => bag.BagName == "Inventory1").Items);
        Assert.Equal((uint)8080, Assert.Single(Assert.Single(snapshot.Bags, bag => bag.BagName == "Crystals").Items).Quantity);
        Assert.Equal(["Inventory1"], snapshot.ObservedSources);
    }

    [Fact]
    public void PlayerInventoryCache_IsScopedByStableCharacterIdentity()
    {
        using var directory = new TemporaryDirectory();
        var repository = new PlayerInventoryCacheRepository(
            new PlayerInventoryCacheStore(Path.Combine(directory.Path, "player-inventory-cache.json")));
        repository.Observe(
            TestData.Owner,
            new PlayerStorageCapture([PlayerBag("Inventory1", (100, 12))], ["Inventory1"], ["Inventory1"]),
            DateTime.UtcNow);

        var other = TestData.Owner with { LocalContentId = TestData.Owner.LocalContentId + 1, CharacterName = "Other" };

        Assert.Empty(repository.Snapshot(other, ["Inventory1"]).Bags);
    }

    [Fact]
    public void PlayerInventoryCache_AppliesOnlyNamedSlotChanges()
    {
        using var directory = new TemporaryDirectory();
        var repository = new PlayerInventoryCacheRepository(
            new PlayerInventoryCacheStore(Path.Combine(directory.Path, "player-inventory-cache.json")));
        repository.Observe(
            TestData.Owner,
            new PlayerStorageCapture(
                [PlayerBag("Inventory1", (100, 10), (200, 7)), PlayerBag("Crystals", (2, 99))],
                ["Inventory1", "Crystals"],
                ["Inventory1", "Crystals"]),
            new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc));
        PlayerInventoryCacheChange? observed = null;
        repository.Changed += change => observed = change;
        var owner = new ObservationOwner(TestData.Owner.LocalContentId!.Value, TestData.Owner.HomeWorldId!.Value);
        var scope = new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory);
        var capture = new ObservationCapture(
            2,
            new DateTimeOffset(2026, 8, 5, 10, 0, 1, TimeSpan.Zero),
            new ObservationProvenance("test", "test", "1.0", "test"),
            ObservationEvidence.CompleteAvailable);

        Assert.True(repository.ApplyChanges(
            TestData.Owner,
            [new InventoryChangeBatch(2, scope, capture,
            [
                new InventorySlotChange(0, 0, new InventorySlotValue(100, 10, false), new InventorySlotValue(100, 4, false)),
                new InventorySlotChange(0, 1, new InventorySlotValue(200, 7, false), null),
                new InventorySlotChange(0, 2, null, new InventorySlotValue(300, 5, true)),
            ])],
            itemId => TestData.Metadata(itemId)));

        Assert.NotNull(observed);
        Assert.False(observed!.IsBaseline);
        Assert.Equal([100u, 200u, 300u], observed.AffectedItemIds.Order().ToArray());
        var snapshot = repository.Snapshot(TestData.Owner, ["Inventory1", "Crystals"]);
        var inventory = Assert.Single(snapshot.Bags, bag => bag.BagName == "Inventory1");
        Assert.Collection(
            inventory.Items,
            item => { Assert.Equal(100u, item.ItemId); Assert.Equal(4u, item.Quantity); },
            item => { Assert.Equal(300u, item.ItemId); Assert.Equal(5u, item.Quantity); Assert.True(item.IsHq); });
        Assert.Equal(99u, Assert.Single(Assert.Single(snapshot.Bags, bag => bag.BagName == "Crystals").Items).Quantity);
    }

    [Fact]
    public void RetainerCache_RoundTripsOwnerBagsListingsAndObservationTimes()
    {
        using var directory = new TemporaryDirectory();
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json"));
        var retainer = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 37));
        retainer.Listings.Add(new CachedMarketListing
        {
            ItemId = 100,
            ItemName = "Darksteel Ore",
            Quantity = 2,
            UnitPrice = 44,
            ListedAtUtc = new DateTime(2026, 7, 20, 10, 30, 0, DateTimeKind.Utc),
        });

        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = retainer });
        var loaded = Assert.Single(store.Load().Values);

        Assert.Equal(TestData.Owner, loaded.Owner);
        Assert.Equal(retainer.ObservedAtUtc, loaded.ObservedAtUtc);
        Assert.Equal((uint)37, Assert.Single(Assert.Single(loaded.Bags).Items).Quantity);
        Assert.Equal(retainer.Listings[0].ListedAtUtc, Assert.Single(loaded.Listings).ListedAtUtc);
    }

    [Fact]
    public void ReplaceListings_ReplacesEmptyOrChangedMarketEvidenceWithoutDiscardingInventory()
    {
        using var directory = new TemporaryDirectory();
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json"));
        var retainer = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 37));
        retainer.Listings.Add(new CachedMarketListing { ItemId = 100, Quantity = 2, UnitPrice = 44 });
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = retainer });
        var repository = new RetainerCacheRepository(store);
        var observedAt = new DateTime(2026, 7, 26, 20, 0, 0, DateTimeKind.Utc);

        repository.ReplaceListings(new RetainerListingsObservation(
            10,
            "Eris",
            TestData.Owner,
            observedAt,
            []));

        var updated = Assert.Single(repository.Snapshot().Values);
        Assert.Empty(updated.Listings);
        Assert.Equal(observedAt, updated.ListingsObservedAtUtc);
        Assert.Equal((uint)37, Assert.Single(Assert.Single(updated.Bags).Items).Quantity);
        Assert.Contains("RetainerMarket", updated.ObservedSources);
    }

    [Fact]
    public void ReplaceListings_PublishesOnlySemanticallyChangedItems()
    {
        using var directory = new TemporaryDirectory();
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json"));
        var existing = TestData.Retainer(10, "Eris");
        existing.ListingsObservedAtUtc = new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc);
        existing.ObservedSources.Add("RetainerMarket");
        existing.Listings.AddRange(
        [
            new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 1, UnitPrice = 44 },
            new CachedMarketListing { ItemId = 200, ItemName = "Cobalt Ore", Quantity = 1, UnitPrice = 55 },
            new CachedMarketListing { ItemId = 400, ItemName = "Removed Ore", Quantity = 1, UnitPrice = 66 },
        ]);
        var otherOwner = TestData.Retainer(30, "Other");
        otherOwner.Owner = TestData.Owner with { LocalContentId = 9002, CharacterName = "Other Character" };
        otherOwner.Listings.Add(new CachedMarketListing { ItemId = 300, ItemName = "Mythril Ore" });
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [existing.RetainerId] = existing,
            [otherOwner.RetainerId] = otherOwner,
        });
        var repository = new RetainerCacheRepository(store);
        var receipts = new List<RetainerListingCaptureReceipt>();
        repository.ListingCaptured += receipts.Add;
        var observedAt = new DateTime(2026, 7, 31, 17, 30, 0, DateTimeKind.Utc);

        repository.ReplaceListings(new RetainerListingsObservation(
            10,
            "Eris",
            TestData.Owner,
            observedAt,
            [
                new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 1, UnitPrice = 45 },
                new CachedMarketListing { ItemId = 200, ItemName = "Cobalt Ore", Quantity = 1, UnitPrice = 55 },
                new CachedMarketListing { ItemId = 300, ItemName = "Mythril Ore", Quantity = 1, UnitPrice = 77 },
            ]));

        var receipt = Assert.Single(receipts);
        Assert.False(string.IsNullOrWhiteSpace(receipt.CaptureId));
        Assert.Equal((ulong)10, receipt.RetainerId);
        Assert.Equal(TestData.Owner, receipt.Owner);
        Assert.Equal(observedAt, receipt.CapturedAtUtc);
        Assert.Equal(RetainerListingCaptureReceipt.ChangedListingsV1, receipt.Semantics);
        Assert.True(receipt.ComparisonAvailable);
        Assert.Equal([100u, 300u, 400u], receipt.Items.Select(item => item.ItemId));
        Assert.Equal("Removed Ore", receipt.Items.Single(item => item.ItemId == 400).ItemName);
    }

    [Fact]
    public void ReplaceListings_UsesMultisetStateSoUnchangedDuplicateListingsStayQuiet()
    {
        using var directory = new TemporaryDirectory();
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json"));
        var existing = TestData.Retainer(10, "Eris");
        existing.ListingsObservedAtUtc = new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc);
        existing.ObservedSources.Add("RetainerMarket");
        existing.Listings.AddRange(
        [
            new CachedMarketListing { ItemId = 100, Quantity = 1, IsHq = false, UnitPrice = 44, SlotIndex = 1 },
            new CachedMarketListing { ItemId = 100, Quantity = 1, IsHq = false, UnitPrice = 44, SlotIndex = 2 },
        ]);
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = existing });
        var repository = new RetainerCacheRepository(store);
        RetainerListingCaptureReceipt? receipt = null;
        repository.ListingCaptured += value => receipt = value;

        repository.ReplaceListings(new RetainerListingsObservation(
            10,
            "Eris",
            TestData.Owner,
            new DateTime(2026, 7, 31, 17, 30, 0, DateTimeKind.Utc),
            [
                new CachedMarketListing { ItemId = 100, Quantity = 1, IsHq = false, UnitPrice = 44, SlotIndex = 8 },
                new CachedMarketListing { ItemId = 100, Quantity = 1, IsHq = false, UnitPrice = 44, SlotIndex = 9 },
            ]));

        Assert.NotNull(receipt);
        Assert.True(receipt.ComparisonAvailable);
        Assert.Empty(receipt.Items);
    }

    [Fact]
    public void ReplaceListings_WithoutPreviousMarketEvidenceEstablishesBaselineWithoutChanges()
    {
        using var directory = new TemporaryDirectory();
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json"));
        var repository = new RetainerCacheRepository(store);
        RetainerListingCaptureReceipt? receipt = null;
        repository.ListingCaptured += value => receipt = value;

        repository.ReplaceListings(new RetainerListingsObservation(
            10,
            "Eris",
            TestData.Owner,
            new DateTime(2026, 7, 31, 17, 30, 0, DateTimeKind.Utc),
            [new CachedMarketListing { ItemId = 100, Quantity = 1, UnitPrice = 44 }]));

        Assert.NotNull(receipt);
        Assert.Equal(RetainerListingCaptureReceipt.ChangedListingsV1, receipt.Semantics);
        Assert.False(receipt.ComparisonAvailable);
        Assert.Empty(receipt.Items);
    }

    [Fact]
    public void SaveAfterInvalidation_WhenReplacementFails_DoesNotResurrectStaleEvidence()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "cache.json");
        var store = new RetainerCacheStore(path);
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Stale") });

        store.SaveAfterInvalidation(new Dictionary<ulong, CachedRetainer>());

        Assert.False(File.Exists(path));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void StateRepository_MutationIsDurableAndIncrementsRevision()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 20 }));

        var restarted = TestData.Repository(directory.Path).Snapshot();

        Assert.Equal(1, restarted.Revision);
        Assert.Equal(20, Assert.Single(restarted.PlanItems).TargetQuantity);
    }

    private static InventoryBag PlayerBag(string name, params (uint ItemId, uint Quantity)[] items) => new()
    {
        BagName = name,
        Location = "Inventory",
        Items = items.Select((item, index) => new RQ.Domain.InventoryItem
        {
            ItemId = item.ItemId,
            ItemName = $"Item {item.ItemId}",
            Quantity = item.Quantity,
            ContainerKey = name,
            SlotIndex = index,
        }).ToList(),
    };
}
