using RQ;
using RQ.Domain;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class PersistenceTests
{
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
}
