using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Automation;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task RetainerLiveDriver_PropagatesCancellationIntoSharedSession()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var session = CreateProxy<IRetainerAutomationSession>((method, arguments) =>
        {
            Assert.Equal(nameof(IRetainerAutomationSession.OpenInventoryAsync), method.Name);
            observed = arguments!.OfType<CancellationToken>().Single();
            return CreateCancellableTask(method.ReturnType, observed);
        });
        var driver = new RetainerLiveDriver(session);

        var open = driver.OpenInventoryAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
        Assert.False(open.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RetainerLiveDriver_PreservesSharedFailureCodeForProductDiagnostics()
    {
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.OpenInventoryAsync) => Task.FromResult(
                RetainerAutomationResult.Failed("RetainerMenuUnavailable", "Retainer command menu is unavailable.")),
            _ => throw new InvalidOperationException($"Unexpected session call: {method.Name}."),
        });
        var driver = new RetainerLiveDriver(session);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.OpenInventoryAsync(CancellationToken.None));

        Assert.Contains("RetainerMenuUnavailable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrystalDeposit_PropagatesCancellationIntoFrameworkWork()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var framework = CreateProxy<IFramework>((method, arguments) =>
        {
            Assert.Equal(nameof(IFramework.RunOnTick), method.Name);
            observed = arguments!.OfType<CancellationToken>().Single();
            return CreateCancellableTask(method.ReturnType, observed);
        });
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var transfer = new DalamudRetainerCrystalTransfer(
            CreateProxy<ISigScanner>(unused),
            CreateProxy<IGameGui>(unused),
            framework,
            CreateProxy<IPluginLog>(unused));

        var deposit = transfer.DepositAsync(
            new DalamudInventoryStack(InventoryType.Crystals, 0, 2, 10),
            10,
            cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
        Assert.False(deposit.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deposit.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ActiveTasks_StopRejectsCallbacksAndWaitIsBounded()
    {
        var tracker = new ActiveTaskTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(tracker.TryRun(() => release.Task));

        tracker.Stop();

        Assert.False(tracker.TryRun(() => Task.CompletedTask));
        Assert.False(tracker.Wait(TimeSpan.FromMilliseconds(20)));
        release.SetResult();
        Assert.True(tracker.Wait(TimeSpan.FromSeconds(1)));
    }


    [Fact]
    public void AutomaticRetrievalQueue_WaitsForBusyAndStableMatchingOwnerWithoutDuplicateStarts()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var automatic = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var manual = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 200, ItemName = "Log", TargetQuantity = 10 }]);
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == automatic.OperationId);
            operation.ExecuteImmediately = true;
            operation.Revision++;
        });

        var reloadedRepository = TestData.Repository(directory.Path);
        var reloadedJournal = new OperationJournal(reloadedRepository);
        var executor = new RecordingRetrievalExecutor { CanStart = false };
        var owner = new OwnerScope();
        using var queue = new AutomaticRetrievalQueue(reloadedJournal, executor, () => owner);

        queue.Tick();
        executor.CanStart = true;
        queue.Tick();
        owner = new OwnerScope { LocalContentId = 77, HomeWorldId = 406 };
        queue.Tick();
        Assert.Equal(0, executor.Starts);

        owner = TestData.Owner;
        queue.Tick();
        queue.Tick();
        Assert.Equal(1, executor.Starts);
        Assert.Equal(automatic.OperationId, queue.ActiveOperationId);

        reloadedRepository.Mutate(StateChangeKind.Operations, state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == automatic.OperationId);
            operation.Status = OperationStatuses.Succeeded;
            operation.Revision++;
        });
        executor.Complete();
        queue.Tick();
        queue.Tick();

        Assert.Equal(1, executor.Starts);
        Assert.Equal(OperationStatuses.Accepted, reloadedJournal.Get(manual.OperationId)!.Status);
    }

    [Fact]
    public void AutomaticRetrievalQueue_StartsSubmittedEphemeralRequestOnNextTick()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var framework = new TestWorkQueue();
        var request = TestData.Request() with { ExecuteImmediately = true };
        var submissions = new ShortageSubmissionService("provider-1", repository, framework, () => TestData.Owner);
        var executor = new RecordingRetrievalExecutor { CanStart = true };
        using var automatic = new AutomaticRetrievalQueue(new OperationJournal(repository), executor, () => TestData.Owner);

        submissions.Submit(TestData.Json(request));
        framework.Drain();
        automatic.Tick();

        Assert.Equal(1, executor.Starts);
        Assert.Equal(request.OperationId, automatic.ActiveOperationId);
        Assert.Empty(repository.Snapshot().PlanItems);
        Assert.Empty(repository.Snapshot().RestockPlans);
        Assert.Empty(repository.Snapshot().StowagePlans);
    }

    [Fact]
    public void AutomaticRetrievalQueue_CancelsAndWaitsForActiveExecution()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            var persisted = state.Operations.Single(candidate => candidate.OperationId == operation.OperationId);
            persisted.ExecuteImmediately = true;
            persisted.Revision++;
        });
        var executor = new RecordingRetrievalExecutor { CanStart = true };
        var queue = new AutomaticRetrievalQueue(journal, executor, () => TestData.Owner);
        queue.Tick();

        Assert.True(queue.CancelAndWait(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, executor.CancelCalls);
        queue.Dispose();
    }

    [Fact]
    public void AutomaticRetrievalQueue_RunsDepositAndRestoresAutoRetainerSuppression()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 2, ItemName = "Fire Shard", TargetQuantity = 100 }],
            OperationKinds.Deposit);
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            var persisted = state.Operations.Single(candidate => candidate.OperationId == operation.OperationId);
            persisted.ExecuteImmediately = true;
            persisted.Revision++;
        });
        var executor = new RecordingRetrievalExecutor { CanStart = true };
        var autoRetainer = new FakeAutoRetainerIpc();
        using var queue = new AutomaticRetrievalQueue(journal, executor, () => TestData.Owner, new AutoRetainerSuppression(autoRetainer));

        queue.Tick();

        Assert.Equal(1, executor.DepositStarts);
        Assert.True(autoRetainer.IsSuppressed);
        executor.Complete();
        queue.Tick();

        Assert.False(autoRetainer.IsSuppressed);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
    }

    [Fact]
    public void AutoRetainerSuppression_NestedScopesRestoreOnceAfterLastRelease()
    {
        var ipc = new FakeAutoRetainerIpc();
        var suppression = new AutoRetainerSuppression(ipc);
        var outer = suppression.Acquire();
        var inner = suppression.Acquire();

        inner.Dispose();

        Assert.True(ipc.IsSuppressed);
        Assert.Equal([true], ipc.SuppressionChanges);

        outer.Dispose();

        Assert.False(ipc.IsSuppressed);
        Assert.Equal([true, false], ipc.SuppressionChanges);
    }

    [Fact]
    public void AutoRetainerSuppression_OutOfOrderReleaseRestoresOnlyAtZeroHolders()
    {
        var ipc = new FakeAutoRetainerIpc();
        var suppression = new AutoRetainerSuppression(ipc);
        var first = suppression.Acquire();
        var second = suppression.Acquire();

        first.Dispose();

        Assert.True(ipc.IsSuppressed);

        second.Dispose();

        Assert.False(ipc.IsSuppressed);
        Assert.Equal([true, false], ipc.SuppressionChanges);
    }

    [Fact]
    public void AutoRetainerSuppression_ForeignSuppressionIsNeverCleared()
    {
        var ipc = new FakeAutoRetainerIpc { IsSuppressed = true };
        var suppression = new AutoRetainerSuppression(ipc);

        suppression.Acquire().Dispose();

        Assert.True(ipc.IsSuppressed);
        Assert.Empty(ipc.SuppressionChanges);
    }

    [Fact]
    public void AutoRetainerSuppression_RestoreFailureSurfacesOnLastReleasingScope()
    {
        var ipc = new ThrowingAutoRetainerIpc();
        var suppression = new AutoRetainerSuppression(ipc);
        var outer = suppression.Acquire();
        var inner = suppression.Acquire();

        inner.Dispose();
        Assert.Null(inner.RestoreFailure);

        outer.Dispose();
        Assert.Equal("AutoRetainer IPC went away.", outer.RestoreFailure);
        Assert.Equal([true], ipc.SuppressionChanges);
    }

    private sealed class ThrowingAutoRetainerIpc : IAutoRetainerIpc
    {
        public bool IsAvailable => true;
        public bool IsBusy => false;
        public bool IsSuppressed { get; private set; }
        public List<bool> SuppressionChanges { get; } = [];
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void SetSuppressed(bool suppressed)
        {
            if (!suppressed)
                throw new InvalidOperationException("AutoRetainer IPC went away.");
            IsSuppressed = suppressed;
            SuppressionChanges.Add(suppressed);
        }
        public void Dispose() { }
    }

    [Fact]
    public void AutoRetainerSuppression_FailedRestoreIsRetriedByNextScopeLifecycle()
    {
        var ipc = new FlakyUnsuppressAutoRetainerIpc();
        var suppression = new AutoRetainerSuppression(ipc);
        var first = suppression.Acquire();

        first.Dispose();

        Assert.NotNull(first.RestoreFailure);
        Assert.True(ipc.IsSuppressed);

        var second = suppression.Acquire();
        second.Dispose();

        Assert.Null(second.RestoreFailure);
        Assert.False(ipc.IsSuppressed);
        Assert.Equal([true, false], ipc.SuppressionChanges);
    }

    [Fact]
    public async Task AutomaticRetrievalQueue_CancelTimeoutKeepsAutoRetainerSuppressedUntilMovementEnds()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(
            TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            var persisted = state.Operations.Single(candidate => candidate.OperationId == operation.OperationId);
            persisted.ExecuteImmediately = true;
            persisted.Revision++;
        });
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 10)) });
        var driver = new IndefiniteDriver();
        var ipc = new FakeAutoRetainerIpc();
        var suppression = new AutoRetainerSuppression(ipc);
        var coordinator = new TransferCoordinator(
            journal,
            driver,
            new RetainerCacheRepository(store),
            () => TestData.Owner,
            () => new Dictionary<uint, int>(),
            autoRetainerSuppression: suppression);
        using var queue = new AutomaticRetrievalQueue(journal, coordinator, () => TestData.Owner, suppression);

        queue.Tick();
        await driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(queue.CancelAndWait(TimeSpan.FromMilliseconds(100)));
        Assert.True(ipc.IsSuppressed);

        driver.Release();

        Assert.True(queue.CancelAndWait(TimeSpan.FromSeconds(2)));
        Assert.False(ipc.IsSuppressed);
        Assert.Equal([true, false], ipc.SuppressionChanges);
    }

    private sealed class FlakyUnsuppressAutoRetainerIpc : IAutoRetainerIpc
    {
        private bool unsuppressThrows = true;
        public bool IsAvailable => true;
        public bool IsBusy => false;
        public bool IsSuppressed { get; private set; }
        public List<bool> SuppressionChanges { get; } = [];
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void SetSuppressed(bool suppressed)
        {
            if (!suppressed && unsuppressThrows)
            {
                unsuppressThrows = false;
                throw new InvalidOperationException("AutoRetainer IPC went away.");
            }
            IsSuppressed = suppressed;
            SuppressionChanges.Add(suppressed);
        }
        public void Dispose() { }
    }

    private sealed class IndefiniteDriver : IRetainerTransferDriver
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RequireRetainerListAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task;
        }
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([new(InventoryType.RetainerPage1, 0, 100, 10)]);
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
            Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified."));
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
        public void Release() => release.TrySetResult();
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ConfigurableDispatchProxy>();
        ((ConfigurableDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object CreateCancellableTask(Type taskType, CancellationToken cancellationToken)
    {
        var resultType = taskType.GetGenericArguments().Single();
        return typeof(AutomationTests)
            .GetMethod(nameof(CreateCancellableTaskCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType)
            .Invoke(null, [cancellationToken])!;
    }

    private static object CreateCompletedTask(Type taskType, object? result)
    {
        if (taskType == typeof(Task))
            return Task.CompletedTask;
        var resultType = taskType.GetGenericArguments().Single();
        return typeof(Task)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult))
            .MakeGenericMethod(resultType)
            .Invoke(null, [result])!;
    }

    private static async Task<T> CreateCancellableTaskCore<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return default!;
    }

    private sealed class RecordingRetrievalExecutor : IRetrievalOperationExecutor
    {
        private readonly TaskCompletionSource<TransferExecutionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanStart { get; set; }
        public int Starts { get; private set; }
        public int DepositStarts { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default)
        {
            Starts++;
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task<TransferExecutionResult> ExecuteDepositAsync(string operationId, CancellationToken cancellationToken = default)
        {
            DepositStarts++;
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void CancelActive() => CancelCalls++;
        public void Complete() => completion.TrySetResult(new(true, "Automatic retrieval completed."));
    }

    private sealed class FakeAutoRetainerIpc : IAutoRetainerIpc
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsBusy { get; set; }
        public bool IsSuppressed { get; set; }
        public List<bool> SuppressionChanges { get; } = [];
        public AutoRetainerIpcCallbacks? Callbacks { get; private set; }
        public void Register(AutoRetainerIpcCallbacks callbacks) => Callbacks = callbacks;
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void SetSuppressed(bool suppressed)
        {
            IsSuppressed = suppressed;
            SuppressionChanges.Add(suppressed);
        }
        public void Dispose() => Callbacks = null;
    }
}

public class ConfigurableDispatchProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
