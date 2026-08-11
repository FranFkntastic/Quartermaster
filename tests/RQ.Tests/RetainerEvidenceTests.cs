using FFXIVClientStructs.FFXIV.Client.Game;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

public sealed class RetainerEvidenceTests
{
    [Fact]
    public void Complete_roster_keeps_classless_members_and_retires_only_current_projection()
    {
        using var directory = new TemporaryDirectory();
        var repository = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")));
        repository.Upsert(new CachedRetainer
        {
            RetainerId = 999,
            RetainerName = "Former",
            Owner = TestData.Owner,
            Bags = [new CachedBag { BagName = InventoryType.RetainerPage1.ToString() }],
        });
        var roster = Enumerable.Range(1, 9)
            .Select(index => new RetainerRosterProjectionEntry(
                checked((ulong)index),
                $"Retainer {index}",
                index - 1,
                IsUiAccessible: null,
                ClassJobId: checked((byte)(index == 9 ? 0 : 1)),
                Level: checked((byte)(index == 9 ? 0 : 100)),
                MarketItemCount: 0,
                IsGameAvailable: index != 9))
            .ToArray();

        repository.ReconcileRoster(TestData.Owner, roster, new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc));

        var snapshot = repository.Snapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.True(snapshot[999].IsCurrentlyAssigned is false);
        var classless = snapshot[9];
        Assert.True(classless.IsCurrentlyAssigned is true);
        Assert.Equal((byte)0, classless.ClassJobId);
        Assert.Null(classless.IsUiAccessible);
        Assert.True(classless.IsGameAvailable is false);
        Assert.Equal(8, classless.DisplayOrder);

        var inaccessibleAt = new DateTime(2026, 8, 11, 12, 1, 0, DateTimeKind.Utc);
        repository.ObserveUiAccessibility(TestData.Owner, 9, false, inaccessibleAt);
        repository.ReconcileRoster(TestData.Owner, roster, inaccessibleAt.AddMinutes(1));
        Assert.False(repository.Snapshot()[9].IsUiAccessible);
        Assert.Equal(inaccessibleAt, repository.Snapshot()[9].UiAccessibilityObservedAtUtc);

        var browser = BrowserProjectionBuilder.Build([], snapshot, TestData.Owner);
        Assert.Equal(9, browser.Scopes.Count(scope => scope.Kind == BrowserScopeKind.Retainer));
        Assert.DoesNotContain(browser.Scopes, scope => scope.RetainerId == 999);
    }

    [Fact]
    public void Complete_inventory_receipt_names_exact_observed_domains()
    {
        using var directory = new TemporaryDirectory();
        var repository = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")));
        RetainerEvidenceReceipt? receipt = null;
        repository.EvidenceAccepted += observed => receipt = observed;
        var observed = InventoryScanner.RequiredRetainerContainers
            .Append(InventoryType.RetainerCrystals)
            .Select(container => container.ToString())
            .ToArray();

        repository.ReplaceInventoryObservation(
            1,
            TestData.Owner,
            new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc),
            [],
            observed,
            observed);

        Assert.NotNull(receipt);
        Assert.Equal(RetainerEvidenceDomain.Inventory | RetainerEvidenceDomain.Crystals, receipt!.Domains);
    }

    [Fact]
    public void Evidence_receipts_preserve_the_source_capture_session()
    {
        using var directory = new TemporaryDirectory();
        var repository = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")));
        var receipts = new List<RetainerEvidenceReceipt>();
        repository.EvidenceAccepted += receipts.Add;

        repository.ReplaceGilObservation(2, TestData.Owner, DateTime.UtcNow, 10);
        repository.ReplaceGilObservation(1, TestData.Owner, DateTime.UtcNow.AddMilliseconds(1), 20, "source-session");

        Assert.Empty(receipts[0].EvidenceSessionId);
        Assert.Equal("source-session", receipts[1].EvidenceSessionId);
    }

    [Fact]
    public void Older_shared_observations_cannot_roll_back_cache_truth()
    {
        using var directory = new TemporaryDirectory();
        var repository = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")));
        var newer = new DateTime(2026, 8, 11, 12, 5, 0, DateTimeKind.Utc);
        var older = newer.AddMinutes(-1);
        var sources = InventoryScanner.RequiredRetainerContainers.Select(container => container.ToString()).ToArray();
        repository.ReplaceInventoryObservation(
            1,
            TestData.Owner,
            newer,
            [new CachedBag { BagName = "RetainerPage1", Items = [new CachedItem { ItemId = 100, Quantity = 9 }] }],
            sources,
            sources);
        repository.ReplaceGilObservation(1, TestData.Owner, newer, 500);

        repository.ReplaceInventoryObservation(1, TestData.Owner, older, [], sources, sources);
        repository.ReplaceGilObservation(1, TestData.Owner, older, 0);

        var retainer = repository.Snapshot()[1];
        Assert.Equal(newer, retainer.ObservedAtUtc);
        Assert.Equal((uint)9, Assert.Single(Assert.Single(retainer.Bags).Items).Quantity);
        Assert.Equal((ulong)500, retainer.Gil);
        Assert.Equal(newer, retainer.GilObservedAtUtc);
    }
}
