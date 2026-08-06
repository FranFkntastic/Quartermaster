using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Interop;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

public sealed class OperationTests
{
    [Fact]
    public void Journal_StatusAndReceiptRevisionsAreMonotonic()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository, () => new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc));
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        Assert.Equal(1, operation.Revision);
        Assert.Equal(2, journal.Transition(operation.OperationId, OperationStatuses.Running, "start", "start").Revision);
        journal.RecordTransfer(operation.OperationId, 100, 10, 4, "verified", "verified");
        var complete = journal.Transition(operation.OperationId, OperationStatuses.Succeeded, "done", "done");

        Assert.Equal(4, complete.Revision);
        var receipts = repository.FullSnapshot().Receipts.Where(receipt => receipt.OperationId == operation.OperationId).ToArray();
        Assert.Equal([1L, 2L, 3L, 4L], receipts.Select(receipt => receipt.Revision));
        Assert.Throws<InvalidOperationException>(() => journal.Transition(operation.OperationId, OperationStatuses.Running, "again", "again"));
    }

    [Fact]
    public void StartupReconciliation_FailsInterruptedRunningOperationWithReceipt()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        journal.Transition(operation.OperationId, OperationStatuses.Running, "start", "start");

        var reconciled = new OperationJournal(repository).ReconcileInterruptedOperations();

        Assert.Single(reconciled);
        Assert.Equal(OperationStatuses.Indeterminate, reconciled[0].Status);
        Assert.Contains(repository.FullSnapshot().Receipts, receipt => receipt.OperationId == operation.OperationId && receipt.Code == "InterruptedByReload");
    }

    [Fact]
    public async Task ExplicitExecute_UsesLiveDriverAndPersistsVerifiedReceipt()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state =>
            state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }));
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, repository.Snapshot().PlanItems);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10), (200, "Unrelated Ingot", 7)),
        });
        var cache = new RetainerCacheRepository(cacheStore);
        var driver = new SuccessfulDriver();
        var coordinator = new TransferCoordinator(journal, driver, cache, () => TestData.Owner, () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.Succeeded, completed.Status);
        Assert.Equal(10, Assert.Single(completed.Lines).TransferredQuantity);
        Assert.True(driver.Calls > 0);
        var cachedRetainer = Assert.Single(cache.Snapshot()).Value;
        var remainingItem = Assert.Single(cachedRetainer.Bags.SelectMany(bag => bag.Items));
        Assert.Equal((uint)200, remainingItem.ItemId);
        Assert.Equal((uint)7, remainingItem.Quantity);
        Assert.Empty(journal.PendingCacheInvalidations());
        Assert.Single(repository.Snapshot().PlanItems);

        var publisher = new SnapshotPublisher("provider", repository, cache.Snapshot);
        publisher.Refresh(TestData.Owner, []);
        using var document = JsonDocument.Parse(publisher.GetSnapshot());
        var publishedRetainer = Assert.Single(document.RootElement.GetProperty("retainers").EnumerateArray());
        var publishedItem = Assert.Single(
            publishedRetainer.GetProperty("bags").EnumerateArray()
                .SelectMany(bag => bag.GetProperty("items").EnumerateArray()));
        Assert.Equal((uint)200, publishedItem.GetProperty("itemId").GetUInt32());
        Assert.Equal((uint)7, publishedItem.GetProperty("quantity").GetUInt32());
    }

    [Fact]
    public async Task Retrieval_ReusesRouteScanTotalAndDecrementsItBetweenStacks()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Spruce Log", TargetQuantity = 1998 }]);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(
                10,
                "Eris",
                (100, "Spruce Log", 999),
                (100, "Spruce Log", 999)),
        });
        var driver = new BaselineRecordingDriver();
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(cacheStore),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal([1998, 999], driver.RetainerBaselines);
        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
    }

    [Fact]
    public async Task MixedPlan_DoesNotStartStowageWhenRetrievalFails()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var plan = new StowagePlan { Owner = TestData.Owner, Name = "Workshop supply" };
        var retrieval = journal.CreateTransferRetrieval(
            TestData.Owner,
            plan,
            [new TargetPlanItem { StowagePlanId = plan.Id, ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var deposit = journal.CreateTransferDeposit(
            TestData.Owner,
            plan,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(plan.Id, Guid.NewGuid(), 200, "Ingot", false, 4, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Eris", 4, 20, DateTime.UtcNow)],
                        4,
                        0),
                ]));
        Assert.Equal(plan.Id, retrieval.SourcePlanId);
        Assert.Equal(plan.Id, deposit.SourcePlanId);
        Assert.Equal(plan.Name, retrieval.SourcePlanName);
        Assert.Equal(plan.Name, deposit.SourcePlanName);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var coordinator = new TransferCoordinator(
            journal,
            new FailedRetrievalDriver(false),
            new RetainerCacheRepository(cacheStore),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecutePlanAsync(retrieval.OperationId, deposit.OperationId);

        Assert.Equal(OperationStatuses.Failed, journal.Get(retrieval.OperationId)!.Status);
        Assert.Equal(OperationStatuses.Cancelled, journal.Get(deposit.OperationId)!.Status);
    }

    [Fact]
    public async Task OwnerMismatch_FailsBeforeDriverInteraction()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "cache.json")));
        var driver = new SuccessfulDriver();
        var other = new OwnerScope { LocalContentId = 55, HomeWorldId = 406, CharacterName = "Other", HomeWorldName = "Maduin" };
        var coordinator = new TransferCoordinator(journal, driver, cache, () => other, () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Failed, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(0, driver.Calls);
    }

    [Theory]
    [InlineData(OperationKinds.Retrieval, false, true)]
    [InlineData(OperationKinds.Retrieval, true, false)]
    [InlineData(OperationKinds.Deposit, false, true)]
    [InlineData(OperationKinds.Deposit, true, false)]
    public async Task MovementRequiresStablePersistedAndCurrentOwnerIdentity(string kind, bool operationStable, bool currentStable)
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var nameOnlyOwner = new OwnerScope
        {
            CharacterName = TestData.Owner.CharacterName,
            HomeWorldName = TestData.Owner.HomeWorldName,
        };
        OperationRecord operation;
        if (kind == OperationKinds.Retrieval)
        {
            operation = journal.CreateManual(
                operationStable ? TestData.Owner : nameOnlyOwner,
                [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        }
        else
        {
            operation = journal.CreateDeposit(TestData.Owner, new ElementalDepositPlan(
                DateTime.UtcNow,
                [new ElementalDepositLine(2, "Fire Shard", 10, 10, 10, 0)],
                [new ElementalDepositCandidate(10, "Eris", DateTime.UtcNow, new Dictionary<uint, int> { [2] = 10 }, 10, true)],
                0));
            if (!operationStable)
            {
                repository.Mutate(StateChangeKind.Operations, state =>
                {
                    var persisted = state.Operations.Single(candidate => candidate.OperationId == operation.OperationId);
                    persisted.Owner = nameOnlyOwner;
                    persisted.Revision++;
                });
                operation = journal.Get(operation.OperationId)!;
            }
        }
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var driver = new SuccessfulDriver();
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(cacheStore),
            () => currentStable ? TestData.Owner : nameOnlyOwner,
            () => new Dictionary<uint, int>());

        if (kind == OperationKinds.Retrieval)
            await coordinator.ExecuteRetrievalAsync(operation.OperationId);
        else
            await coordinator.ExecuteDepositAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Failed, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(0, driver.Calls);
    }

    [Fact]
    public async Task Retrieval_RejectsDepositOperationBeforeDriverInteraction()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 2, ItemName = "Fire Shard", TargetQuantity = 10 }],
            OperationKinds.Deposit);
        var driver = new SuccessfulDriver();
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"))),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Failed, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(0, driver.Calls);
    }

    [Fact]
    public async Task SharedAutomationLease_LeavesAcceptedOperationUntouchedWhenBusy()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var automation = new RQ.Automation.AutomationLease();
        Assert.True(automation.TryAcquire("refresh", out var refreshLease));
        using (refreshLease)
        {
            var coordinator = new TransferCoordinator(
                journal,
                new SuccessfulDriver(),
                new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"))),
                () => TestData.Owner,
                () => new Dictionary<uint, int>(),
                automation);

            var result = await coordinator.ExecuteRetrievalAsync(operation.OperationId);

            Assert.False(result.Started);
            Assert.Equal(OperationStatuses.Accepted, journal.Get(operation.OperationId)!.Status);
        }
    }

    [Fact]
    public void CacheInvalidation_RetryPersistsAnEarlierInMemoryRemoval()
    {
        using var directory = new TemporaryDirectory();
        var cacheDirectory = Path.Combine(directory.Path, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var store = new RetainerCacheStore(Path.Combine(cacheDirectory, "retainers.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var cache = new RetainerCacheRepository(store);
        Directory.Delete(cacheDirectory, recursive: true);
        File.WriteAllText(cacheDirectory, "not a directory");

        var result = cache.Invalidate(10);

        Assert.True(result.Removed);
        Assert.False(result.Persisted);
        Assert.Empty(cache.Snapshot());

        File.Delete(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var retry = cache.Invalidate(10);

        Assert.False(retry.Removed);
        Assert.True(retry.Persisted);
        Assert.Empty(new RetainerCacheRepository(store).Snapshot());
    }

    [Fact]
    public async Task CancelActive_BeforeMovementRetainsTrustedOwnerEvidence()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var cache = new RetainerCacheRepository(store);
        var driver = new BlockingDriver();
        var coordinator = new TransferCoordinator(journal, driver, cache, () => TestData.Owner, () => new Dictionary<uint, int>());
        var execution = coordinator.ExecuteRetrievalAsync(operation.OperationId);
        await driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.CancelActive();
        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(OperationStatuses.Indeterminate, journal.Get(operation.OperationId)!.Status);
        Assert.Single(cache.Snapshot());
        Assert.True(driver.Cancelled);
    }

    [Fact]
    public async Task RetrievalFailure_BeforeCommandLeavesCacheTrustedAndMarksOperationFailed()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var cache = new RetainerCacheRepository(store);
        var coordinator = new TransferCoordinator(
            journal,
            new FailedRetrievalDriver(movementMayHaveOccurred: false),
            cache,
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Failed, journal.Get(operation.OperationId)!.Status);
        Assert.Single(cache.Snapshot());
        Assert.Empty(journal.PendingCacheInvalidations());
    }

    [Fact]
    public async Task RetrievalFailure_AfterCommandInvalidatesEvidenceAndMarksOperationIndeterminate()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)),
            [20] = TestData.Retainer(20, "Nyx", (200, "Trusted Ingot", 25)),
        });
        var cache = new RetainerCacheRepository(store);
        var coordinator = new TransferCoordinator(
            journal,
            new FailedRetrievalDriver(movementMayHaveOccurred: true),
            cache,
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Indeterminate, journal.Get(operation.OperationId)!.Status);
        var trustedRetainer = Assert.Single(cache.Snapshot());
        Assert.Equal((ulong)20, trustedRetainer.Key);
        Assert.Equal((uint)25, Assert.Single(trustedRetainer.Value.Bags.SelectMany(bag => bag.Items)).Quantity);
        Assert.Empty(journal.PendingCacheInvalidations());
    }

    [Theory]
    [InlineData(false, "CommandUnavailable", false)]
    [InlineData(false, "SourceSlotChanged", false)]
    [InlineData(false, "TransferPending", true)]
    [InlineData(true, "TransferVerified", true)]
    public void RetrievalResultPolicy_DistinguishesPreCommandFailures(
        bool success,
        string code,
        bool expected) =>
        Assert.Equal(expected, RetainerRetrievalResultPolicy.MovementMayHaveOccurred(success, code));

    [Fact]
    public async Task DepositExecution_UsesOnlyPersistedReviewedAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var plan = new ElementalDepositPlan(
            DateTime.UtcNow,
            [new ElementalDepositLine(2, "Fire Shard", 500, 99, 99, 401)],
            [new ElementalDepositCandidate(10, "Eris", DateTime.UtcNow, new Dictionary<uint, int> { [2] = 99 }, 99, true)],
            0);
        var operation = journal.CreateDeposit(TestData.Owner, plan);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris") });
        var driver = new DepositDriver();
        var cache = new RetainerCacheRepository(store);
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            cache,
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        Assert.Equal(99, driver.RequestedQuantity);
        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(99, Assert.Single(journal.Get(operation.OperationId)!.Lines).TransferredQuantity);
        var cachedItem = Assert.Single(Assert.Single(cache.Snapshot()).Value.Bags.SelectMany(bag => bag.Items));
        Assert.Equal((uint)2, cachedItem.ItemId);
        Assert.Equal((uint)99, cachedItem.Quantity);
    }

    [Fact]
    public void Journal_RejectsTransferAbovePersistedLineAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OperationJournal(TestData.Repository(directory.Path));
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 5 }]);
        journal.Transition(operation.OperationId, OperationStatuses.Running, "start", "start");

        Assert.Throws<InvalidOperationException>(() => journal.RecordTransfer(operation.OperationId, 100, 10, 6, "verified", "verified"));
    }

    [Fact]
    public void CacheInvalidationMarker_PersistsUntilExplicitlyResolved()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OperationJournal(TestData.Repository(directory.Path));
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 5 }]);
        journal.ArmCacheInvalidation(operation.OperationId, 10, TestData.Owner);

        var reloaded = new OperationJournal(TestData.Repository(directory.Path));
        Assert.Equal(10UL, Assert.Single(reloaded.PendingCacheInvalidations()).RetainerId);

        reloaded.ResolveCacheInvalidation(operation.OperationId, 10);
        Assert.Empty(new OperationJournal(TestData.Repository(directory.Path)).PendingCacheInvalidations());
    }

    [Fact]
    public void PendingMutationRecovery_InvalidatesOnlyTheArmedRetainer()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OperationJournal(TestData.Repository(directory.Path));
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 5 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)),
            [20] = TestData.Retainer(20, "Nyx", (200, "Ingot", 25)),
        });
        var cache = new RetainerCacheRepository(store);
        journal.ArmCacheInvalidation(operation.OperationId, 10, TestData.Owner);

        var recovered = RetainerStockMutationPersistence.RecoverPending(journal, cache);

        Assert.Equal(1, recovered);
        var trustedRetainer = Assert.Single(cache.Snapshot());
        Assert.Equal((ulong)20, trustedRetainer.Key);
        Assert.Empty(journal.PendingCacheInvalidations());
    }

    [Fact]
    public async Task Coordination_SuppressesAutoRetainerAndRestoresAfterRetrieval()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var ipc = new FakeAutoRetainerIpc();
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc);

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal([true, false], ipc.SuppressionCalls);
        Assert.False(ipc.Suppressed);
    }

    [Fact]
    public async Task Coordination_WaitsForBusyAutoRetainerBeforeSuppressing()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var ipc = new FakeAutoRetainerIpc { BusyPollsBeforeIdle = 2 };
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc,
            autoRetainerWait: TimeSpan.FromSeconds(5),
            autoRetainerPoll: TimeSpan.FromMilliseconds(10));

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.True(ipc.BusyPolls >= 3);
        Assert.Equal([true, false], ipc.SuppressionCalls);
    }

    [Fact]
    public async Task Coordination_RefusesWhenAutoRetainerStaysBusy()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var driver = new SuccessfulDriver();
        var ipc = new FakeAutoRetainerIpc { AlwaysBusy = true };
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc,
            autoRetainerWait: TimeSpan.FromMilliseconds(150),
            autoRetainerPoll: TimeSpan.FromMilliseconds(20));

        var result = await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.False(result.Started);
        Assert.Contains("remained busy", result.Message);
        Assert.Equal(OperationStatuses.Accepted, journal.Get(operation.OperationId)!.Status);
        Assert.Empty(ipc.SuppressionCalls);
        Assert.Equal(0, driver.Calls);
    }

    [Fact]
    public async Task Coordination_PreservesSuppressionOwnedBySomeoneElse()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var ipc = new FakeAutoRetainerIpc { Suppressed = true };
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc);

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Empty(ipc.SuppressionCalls);
        Assert.True(ipc.Suppressed);
    }

    [Fact]
    public async Task Coordination_RestoreFailureSurfacesInResultMessage()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var ipc = new FakeAutoRetainerIpc { ThrowOnUnsuppress = true };
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc);

        var result = await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.True(result.Started);
        Assert.Contains("could not be restored", result.Message);
        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal([true], ipc.SuppressionCalls);
    }

    [Fact]
    public async Task Coordination_PlanSequenceSuppressesOnceAcrossRetrievalAndDeposit()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var plan = new StowagePlan { Owner = TestData.Owner, Name = "Workshop supply" };
        var retrieval = journal.CreateTransferRetrieval(
            TestData.Owner,
            plan,
            [new TargetPlanItem { StowagePlanId = plan.Id, ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var deposit = journal.CreateTransferDeposit(
            TestData.Owner,
            plan,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(plan.Id, Guid.NewGuid(), 200, "Ingot", false, 4, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Eris", 4, 20, DateTime.UtcNow)],
                        4,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var ipc = new FakeAutoRetainerIpc();
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainer: ipc);

        await coordinator.ExecutePlanAsync(retrieval.OperationId, deposit.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(retrieval.OperationId)!.Status);
        Assert.Equal([true, false], ipc.SuppressionCalls);
    }

    [Fact]
    public async Task DepositClamp_ZeroesLiveCandidateCapacityAndFinishesPartial()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateDeposit(
            TestData.Owner,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(null, null, 4, "Wind Shard", false, 9999, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Skor", 9999, 9999, DateTime.UtcNow)],
                        9999,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Skor") });
        var driver = new CapacityDepositDriver
        {
            PlayerStacks =
            [
                new DalamudInventoryStack(InventoryType.Inventory1, 0, 4, 9999),
                new DalamudInventoryStack(InventoryType.Inventory1, 1, 4, 9999),
            ],
            RetainerStacks = [new DalamudInventoryStack(InventoryType.RetainerPage1, 0, 4, 1)],
        };
        driver.DepositBehavior.Enqueue((_, _) => new RetainerDepositResult(true, 1, "TransferVerified", "Clamped to 1."));
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.PartiallySucceeded, completed.Status);
        Assert.Equal(1, Assert.Single(completed.Lines).TransferredQuantity);
        Assert.Contains("9,998 remain", completed.Message);
        Assert.Single(driver.DepositAttempts);
    }

    [Fact]
    public async Task DepositNoCapacity_SkipsToNextCandidateWithReceipt()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateDeposit(
            TestData.Owner,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(null, null, 4, "Wind Shard", false, 9999, new StowageRoutingPolicy()),
                        [
                            new StowageAllocation(10, "Skor", 9999, 9999, DateTime.UtcNow),
                            new StowageAllocation(20, "Nyx", 9999, 9999, DateTime.UtcNow),
                        ],
                        9999,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Skor"),
            [20] = TestData.Retainer(20, "Nyx"),
        });
        var driver = new CapacityDepositDriver
        {
            PlayerStacks = [new DalamudInventoryStack(InventoryType.Inventory1, 0, 4, 9999)],
            RetainerStacks = [new DalamudInventoryStack(InventoryType.RetainerPage1, 0, 4, 9999)],
        };
        driver.DepositBehavior.Enqueue((_, _) => new RetainerDepositResult(false, 0, "NoCapacity", "Retainer reported no deposit capacity for item 4."));
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.Succeeded, completed.Status);
        Assert.Equal(9999, Assert.Single(completed.Lines).TransferredQuantity);
        Assert.Equal(2, driver.DepositAttempts.Count);
        Assert.Contains(
            repository.FullSnapshot().Receipts,
            receipt => receipt.OperationId == operation.OperationId &&
                       receipt.Code == "DepositSkippedNoCapacity" &&
                       receipt.Message.Contains("Skor"));
    }

    [Fact]
    public async Task DepositCrystalFull_SkipsWithReceiptInsteadOfSilentAbandonment()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateDeposit(
            TestData.Owner,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(null, null, 2, "Fire Shard", false, 9999, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Skor", 9999, 9999, DateTime.UtcNow)],
                        9999,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Skor") });
        var driver = new CapacityDepositDriver
        {
            CrystalStacks = [new DalamudInventoryStack(InventoryType.Crystals, 0, 2, 9999)],
            CrystalBehavior = (_, _) => new RetainerCrystalTransferResult(true, 0, "NoCapacity", "Retainer crystal storage is full for item 2."),
        };
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.Failed, completed.Status);
        Assert.Contains(
            repository.FullSnapshot().Receipts,
            receipt => receipt.OperationId == operation.OperationId && receipt.Code == "DepositSkippedNoCapacity");
    }

    [Fact]
    public async Task DepositNotObserved_ProvenPlayerStackUnchanged_SkipsInsteadOfAborting()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateDeposit(
            TestData.Owner,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(null, null, 5095, "Darksteel Rivets", false, 999, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Skor", 999, 999, DateTime.UtcNow)],
                        999,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Skor") });
        var driver = new CapacityDepositDriver
        {
            PlayerStacks = [new DalamudInventoryStack(InventoryType.Inventory1, 0, 5095, 999)],
        };
        driver.DepositBehavior.Enqueue((_, _) => new RetainerDepositResult(false, 0, "DepositNotObserved", "Deposit neither completed nor opened a numeric quantity popup for item 5095."));
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.Failed, completed.Status);
        Assert.Contains(
            repository.FullSnapshot().Receipts,
            receipt => receipt.OperationId == operation.OperationId && receipt.Code == "DepositSkippedUnobserved");
        Assert.Empty(journal.PendingCacheInvalidations());
    }

    [Fact]
    public async Task DepositNotObserved_PlayerStackDecreased_RemainsIndeterminate()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateDeposit(
            TestData.Owner,
            new StowageDepositBatch(
                DateTime.UtcNow,
                [
                    new StowageRoute(
                        new StowageDepositRequest(null, null, 5095, "Darksteel Rivets", false, 999, new StowageRoutingPolicy()),
                        [new StowageAllocation(10, "Skor", 999, 999, DateTime.UtcNow)],
                        999,
                        0),
                ]));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Skor") });
        var playerStacks = new List<DalamudInventoryStack> { new(InventoryType.Inventory1, 0, 5095, 999) };
        var driver = new CapacityDepositDriver();
        driver.ScanPlayerInventory = _ => playerStacks;
        driver.DepositBehavior.Enqueue((_, _) =>
        {
            playerStacks.Clear();
            return new RetainerDepositResult(false, 0, "DepositNotObserved", "Deposit neither completed nor opened a numeric quantity popup for item 5095.");
        });
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Indeterminate, journal.Get(operation.OperationId)!.Status);
        Assert.DoesNotContain(
            repository.FullSnapshot().Receipts,
            receipt => receipt.OperationId == operation.OperationId && receipt.Code == "DepositSkippedUnobserved");
    }

    private sealed class FakeAutoRetainerIpc : IAutoRetainerIpc
    {
        private int busyPolls;
        public int BusyPollsBeforeIdle { get; set; }
        public bool AlwaysBusy { get; set; }
        public bool Suppressed { get; set; }
        public bool ThrowOnUnsuppress { get; set; }
        public int BusyPolls => busyPolls;
        public List<bool> SuppressionCalls { get; } = [];
        public bool IsAvailable { get; } = true;
        public bool IsBusy => AlwaysBusy || busyPolls++ < BusyPollsBeforeIdle;
        public bool IsSuppressed => Suppressed;
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void SetSuppressed(bool suppressed)
        {
            if (!suppressed && ThrowOnUnsuppress)
                throw new InvalidOperationException("AutoRetainer IPC went away.");
            Suppressed = suppressed;
            SuppressionCalls.Add(suppressed);
        }
        public void Dispose() { }
    }

    private sealed class CapacityDepositDriver : IRetainerTransferDriver
    {
        public List<DalamudInventoryStack> PlayerStacks { get; set; } = [];
        public List<DalamudInventoryStack> CrystalStacks { get; set; } = [];
        public List<DalamudInventoryStack> RetainerStacks { get; set; } = [];
        public Queue<Func<DalamudInventoryStack, int, RetainerDepositResult>> DepositBehavior { get; } = new();
        public Func<DalamudInventoryStack, int, RetainerCrystalTransferResult>? CrystalBehavior { get; set; }
        public Func<IReadOnlySet<uint>, IReadOnlyList<DalamudInventoryStack>>? ScanPlayerInventory { get; set; }
        public List<(uint ItemId, int Quantity)> DepositAttempts { get; } = [];
        public List<(uint ItemId, int Quantity)> CrystalAttempts { get; } = [];
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(RetainerStacks);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult(ScanPlayerInventory?.Invoke(itemIds) ?? PlayerStacks);
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(CrystalStacks);
        public Task<RetainerDepositResult> DepositAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            DepositAttempts.Add((stack.ItemId, quantity));
            return Task.FromResult(DepositBehavior.Count > 0
                ? DepositBehavior.Dequeue()(stack, quantity)
                : new RetainerDepositResult(true, quantity, "TransferVerified", "Verified."));
        }
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            CrystalAttempts.Add((stack.ItemId, quantity));
            return Task.FromResult(CrystalBehavior?.Invoke(stack, quantity) ??
                new RetainerCrystalTransferResult(true, quantity, "TransferVerified", "Verified."));
        }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }

    private sealed class SuccessfulDriver : IRetainerTransferDriver
    {
        private int retainerQuantity = 10;
        public int Calls { get; private set; }
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenInventoryAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(
                retainerQuantity > 0
                    ? [new(InventoryType.RetainerPage1, 0, 100, retainerQuantity)]
                    : []);
        }
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            Calls++;
            retainerQuantity -= quantity;
            return Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified."));
        }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]); }
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "TransferVerified", "Verified.")); }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public void CancelActive() { Calls++; }
    }

    private sealed class BaselineRecordingDriver : IRetainerTransferDriver
    {
        private int remaining = 1998;
        public List<int> RetainerBaselines { get; } = [];

        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(
            IReadOnlySet<uint> itemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(remaining switch
            {
                >= 1998 =>
                [
                    new(InventoryType.RetainerPage1, 0, 100, 999),
                    new(InventoryType.RetainerPage1, 1, 100, 999),
                ],
                >= 999 => [new(InventoryType.RetainerPage1, 1, 100, 999)],
                _ => [],
            });

        public Task<RetrievalResult> RetrieveAsync(
            DalamudInventoryStack stack,
            int quantity,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Coordinator did not supply its route-scan baseline.");

        public Task<RetrievalResult> RetrieveAsync(
            DalamudInventoryStack stack,
            int quantity,
            int retainerVariantQuantityBefore,
            CancellationToken cancellationToken)
        {
            RetainerBaselines.Add(retainerVariantQuantityBefore);
            remaining -= quantity;
            return Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified."));
        }

        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(
            IReadOnlySet<uint> itemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(
            DalamudInventoryStack stack,
            int quantity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }

    private sealed class BlockingDriver : IRetainerTransferDriver
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task RequireRetainerListAsync(CancellationToken cancellationToken) { Started.SetResult(); await release.Task.WaitAsync(cancellationToken); }
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) => Task.FromResult(new RetrievalResult(true, quantity, "verified", "verified"));
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) => Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "verified", "verified"));
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { Cancelled = true; release.TrySetResult(); }
    }

    private sealed class PartialDriver : IRetainerTransferDriver
    {
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.RetainerPage1, 0, 200, 2)]);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
            Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified."));
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }

    private sealed class FailedRetrievalDriver(bool movementMayHaveOccurred) : IRetainerTransferDriver
    {
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(
            IReadOnlySet<uint> itemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.RetainerPage1, 0, 100, 10)]);
        public Task<RetrievalResult> RetrieveAsync(
            DalamudInventoryStack stack,
            int quantity,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RetrievalResult(
                false,
                0,
                movementMayHaveOccurred ? "TransferPending" : "CommandUnavailable",
                "Retrieval failed.",
                movementMayHaveOccurred));
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(
            IReadOnlySet<uint> itemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(
            DalamudInventoryStack stack,
            int quantity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }

    private sealed class DepositDriver : IRetainerTransferDriver
    {
        private int retainerQuantity;
        public int RequestedQuantity { get; private set; }
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(
                retainerQuantity > 0
                    ? [new(InventoryType.RetainerCrystals, 0, 2, retainerQuantity)]
                    : []);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.Crystals, 0, 2, 500)]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            RequestedQuantity = quantity;
            retainerQuantity += quantity;
            return Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "verified", "verified"));
        }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }
}
