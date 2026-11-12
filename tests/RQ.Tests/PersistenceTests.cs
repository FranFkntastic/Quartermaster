using Franthropy.Observations.V1;
using RQ;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class PersistenceTests
{

    [Fact]
    public async Task ReplaceListings_RetriesFailedCheckpointWriteUntilLatestListingIsDurable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "retainer-cache.json");
        var listingPath = Path.Combine(directory.Path, "retainer-listings-cache.json");
        var initialObservedAt = new DateTime(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);
        using (var initialRepository = new RetainerCacheRepository(new RetainerCacheStore(path)))
        {
            initialRepository.ReplaceListings(new RetainerListingsObservation(
                10,
                "Scrongle",
                TestData.Owner,
                initialObservedAt,
                [new CachedMarketListing { ItemId = 200, ItemName = "Pressure Hull", Quantity = 2 }]));
        }

        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource<RetainerListingPersistenceReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var repository = new RetainerCacheRepository(new RetainerCacheStore(path)))
        {
            repository.ListingPersistenceFailed += _ => failure.TrySetResult();
            repository.ListingPersisted += receipt =>
            {
                if (receipt.ObservedAtUtc > initialObservedAt)
                    persisted.TrySetResult(receipt);
            };
            using (File.Open(listingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                repository.ReplaceListings(new RetainerListingsObservation(
                    10,
                    "Scrongle",
                    TestData.Owner,
                    initialObservedAt.AddSeconds(1),
                    [new CachedMarketListing { ItemId = 200, ItemName = "Pressure Hull", Quantity = 20 }]));
                await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            var receipt = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((ulong)10, receipt.RetainerId);
        }

        using var reloadedStore = new RetainerCacheStore(path);
        Assert.Equal((uint)20, Assert.Single(Assert.Single(reloadedStore.Load().Values).Listings).Quantity);
    }

    [Fact]
    public void Invalidate_RemovesListingCheckpointDurablyBeforeReportingSuccess()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "retainer-cache.json");
        using (var repository = new RetainerCacheRepository(new RetainerCacheStore(path)))
        {
            repository.ReplaceListings(new RetainerListingsObservation(
                10,
                "Eris",
                TestData.Owner,
                new DateTime(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc),
                [new CachedMarketListing { ItemId = 200, ItemName = "Whale-class Pressure Hull", Quantity = 16 }]));
        }

        using (var repository = new RetainerCacheRepository(new RetainerCacheStore(path)))
        {
            Assert.Equal((uint)16, Assert.Single(Assert.Single(repository.Snapshot().Values).Listings).Quantity);
            var result = repository.Invalidate(10);
            Assert.True(result.Removed);
            Assert.True(result.Persisted);
        }

        using var reloadedStore = new RetainerCacheStore(path);
        Assert.Empty(reloadedStore.Load());
    }

}
