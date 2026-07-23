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
        var receipts = repository.Snapshot().Receipts.Where(receipt => receipt.OperationId == operation.OperationId).ToArray();
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
        Assert.Contains(repository.Snapshot().Receipts, receipt => receipt.OperationId == operation.OperationId && receipt.Code == "InterruptedByReload");
    }

    [Fact]
    public async Task ExplicitExecute_UsesLiveDriverAndPersistsVerifiedReceipt()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var cache = new RetainerCacheRepository(cacheStore);
        var driver = new SuccessfulDriver();
        var coordinator = new TransferCoordinator(journal, driver, cache, () => TestData.Owner, () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        var completed = journal.Get(operation.OperationId)!;
        Assert.Equal(OperationStatuses.Succeeded, completed.Status);
        Assert.Equal(10, Assert.Single(completed.Lines).TransferredQuantity);
        Assert.True(driver.Calls > 0);
        Assert.Empty(cache.Snapshot());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task RetrievalPlanClearing_RespectsConfiguration(bool clearAsActioned, int expectedPlanRows)
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }));
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, repository.Snapshot().PlanItems);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(cacheStore),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            clearRetrievalPlansAsActioned: () => clearAsActioned);

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(expectedPlanRows, repository.Snapshot().PlanItems.Count);
    }

    [Fact]
    public async Task RetrievalPlanClearing_RemovesAlreadySatisfiedLineButKeepsPartialLine()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => state.PlanItems.AddRange(
        [
            new TargetPlanItem { ItemId = 100, ItemName = "Satisfied Ore", TargetQuantity = 10 },
            new TargetPlanItem { ItemId = 200, ItemName = "Partial Log", TargetQuantity = 5 },
        ]));
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, repository.Snapshot().PlanItems);
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (200, "Partial Log", 2)) });
        var coordinator = new TransferCoordinator(
            journal,
            new PartialDriver(),
            new RetainerCacheRepository(cacheStore),
            () => TestData.Owner,
            () => new Dictionary<uint, int> { [100] = 10 },
            clearRetrievalPlansAsActioned: () => true);

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.PartiallySucceeded, journal.Get(operation.OperationId)!.Status);
        var remaining = Assert.Single(repository.Snapshot().PlanItems);
        Assert.Equal((uint)200, remaining.ItemId);
    }

    [Fact]
    public async Task RetrievalPlanClearing_PreservesEditedAndReaddedRows()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var originalId = Guid.NewGuid();
        repository.Mutate(state => state.PlanItems.Add(new TargetPlanItem
        {
            Id = originalId,
            ItemId = 100,
            ItemName = "Ore",
            TargetQuantity = 10,
        }));
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, repository.Snapshot().PlanItems);
        repository.Mutate(state =>
        {
            state.PlanItems.Single(item => item.Id == originalId).TargetQuantity = 20;
            state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 30 });
        });
        var cacheStore = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        cacheStore.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var coordinator = new TransferCoordinator(
            journal,
            new SuccessfulDriver(),
            new RetainerCacheRepository(cacheStore),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            clearRetrievalPlansAsActioned: () => true);

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal([20, 30], repository.Snapshot().PlanItems.Select(item => item.TargetQuantity).Order());
    }

    [Fact]
    public void RetrievalPlanClearing_AfterIpcPersistenceReloadOnlyRemovesUnchangedAuthorizedRows()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var request = TestData.Request() with
        {
            Items =
            [
                new ShortageRequestItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10, ShortageQuantity = 10 },
                new ShortageRequestItem { ItemId = 200, ItemName = "Log", TargetQuantity = 20, ShortageQuantity = 20 },
            ],
        };
        var submissions = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        submissions.Submit(TestData.Json(request));
        queue.Drain();
        repository.Mutate(state =>
        {
            state.PlanItems.Single(item => item.ItemId == 100).TargetQuantity = 15;
            state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 30 });
        });

        var reloadedRepository = TestData.Repository(directory.Path);
        var reloadedJournal = new OperationJournal(reloadedRepository);
        reloadedJournal.Transition(request.OperationId, OperationStatuses.Running, "start", "start");
        reloadedJournal.ClearSatisfiedRetrievalPlanItems(request.OperationId, new HashSet<uint> { 100, 200 });

        var remaining = reloadedRepository.Snapshot().PlanItems.OrderBy(item => item.TargetQuantity).ToArray();
        Assert.Equal([15, 30], remaining.Select(item => item.TargetQuantity));
        Assert.All(remaining, item => Assert.Equal((uint)100, item.ItemId));
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
                repository.Mutate(state => state.Operations.Single(candidate => candidate.OperationId == operation.OperationId).Owner = nameOnlyOwner);
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
    public void CacheInvalidation_RemovesMemoryWhenPersistenceFails()
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
    }

    [Fact]
    public async Task CancelActive_MarksLiveOperationIndeterminateAndInvalidatesOwnerEvidence()
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
        Assert.Empty(cache.Snapshot());
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
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var cache = new RetainerCacheRepository(store);
        var coordinator = new TransferCoordinator(
            journal,
            new FailedRetrievalDriver(movementMayHaveOccurred: true),
            cache,
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync(operation.OperationId);

        Assert.Equal(OperationStatuses.Indeterminate, journal.Get(operation.OperationId)!.Status);
        Assert.Empty(cache.Snapshot());
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
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteDepositAsync(operation.OperationId);

        Assert.Equal(99, driver.RequestedQuantity);
        Assert.Equal(OperationStatuses.Succeeded, journal.Get(operation.OperationId)!.Status);
        Assert.Equal(99, Assert.Single(journal.Get(operation.OperationId)!.Lines).TransferredQuantity);
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

    private sealed class SuccessfulDriver : IRetainerTransferDriver
    {
        public int Calls { get; private set; }
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenInventoryAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.RetainerPage1, 0, 100, 10)]);
        }
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified.")); }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]); }
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "TransferVerified", "Verified.")); }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public void CancelActive() { Calls++; }
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
        public int RequestedQuantity { get; private set; }
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.Crystals, 0, 2, 500)]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            RequestedQuantity = quantity;
            return Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "verified", "verified"));
        }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
    }
}
