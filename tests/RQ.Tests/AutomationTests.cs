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
    public void AutomationServices_DoNotRegisterCallbacksDuringConstruction()
    {
        using var directory = new TemporaryDirectory();
        var dependencyCalls = 0;
        var unused = new Func<MethodInfo, object?[]?, object?>((_, _) =>
        {
            dependencyCalls++;
            return null;
        });
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        using var refresh = new AutoRetainerRefreshService(
            CreateProxy<IFramework>(unused),
            log,
            captures,
            CreateProxy<IRetainerAutomationSession>(unused),
            new FakeAutoRetainerIpc());

        Assert.Equal(0, dependencyCalls);
    }

    [Fact]
    public void MetadataCatalog_ConcurrentCacheMissesRemainSafe()
    {
        var data = CreateProxy<IDataManager>((_, _) => throw new InvalidOperationException("No test sheet."));
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var catalog = new ItemMetadataCatalog(data, log);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Parallel.For(0, 512, _ =>
        {
            try { Assert.Equal("Item 42", catalog.Resolve(42).Name); }
            catch (Exception exception) { failures.Enqueue(exception); }
        });

        Assert.Empty(failures);
        Assert.Equal("Item 42", catalog.Resolve(42).Name);
    }

    [Fact]
    public void RefreshStart_OpensRetainerListBeforeQueueingAutoRetainerWork()
    {
        using var directory = new TemporaryDirectory();
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var framework = CreateProxy<IFramework>((method, arguments) =>
        {
            Assert.Equal(nameof(IFramework.RunOnTick), method.Name);
            var callback = Assert.IsAssignableFrom<Delegate>(arguments![0]);
            return CreateCompletedTask(method.ReturnType, callback.DynamicInvoke());
        });
        var ensureCalls = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            "get_IsRetainerListReady" => false,
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) => Task.FromResult(
                (++ensureCalls, RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list opened.")).Item2),
            _ => throw new InvalidOperationException($"Unexpected session call: {method.Name}."),
        });
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        using var refresh = new AutoRetainerRefreshService(
            framework,
            log,
            captures,
            session,
            new FakeAutoRetainerIpc(),
            automation: null,
            countAvailableRetainers: () => 2);

        Assert.True(refresh.Start());
        Assert.True(SpinWait.SpinUntil(
            () => refresh.Status == "Retainer refresh queued.",
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, ensureCalls);
        Assert.True(refresh.IsQueued);
    }

    [Fact]
    public void RefreshStart_RecoversIdleExistingRetainerInteraction()
    {
        using var directory = new TemporaryDirectory();
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var framework = CreateProxy<IFramework>((method, arguments) =>
        {
            Assert.Equal(nameof(IFramework.RunOnTick), method.Name);
            var callback = Assert.IsAssignableFrom<Delegate>(arguments![0]);
            return CreateCompletedTask(method.ReturnType, callback.DynamicInvoke());
        });
        var ensureCalls = 0;
        var closeCalls = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            "get_IsRetainerListReady" => false,
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) => Task.FromResult(
                ++ensureCalls == 1
                    ? RetainerAutomationResult.Failed("RetainerInteractionAlreadyOpen", "A retainer is open.")
                    : RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list opened.")),
            nameof(IRetainerAutomationSession.CloseRetainerAsync) => Task.FromResult(
                (++closeCalls, RetainerAutomationResult.Succeeded("RetainerClosed", "Retainer closed.")).Item2),
            _ => throw new InvalidOperationException($"Unexpected session call: {method.Name}."),
        });
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        using var refresh = new AutoRetainerRefreshService(
            framework,
            log,
            captures,
            session,
            new FakeAutoRetainerIpc(),
            automation: null,
            countAvailableRetainers: () => 2);

        Assert.True(refresh.Start());
        Assert.True(SpinWait.SpinUntil(
            () => refresh.Status == "Retainer refresh queued.",
            TimeSpan.FromSeconds(2)));
        Assert.Equal(2, ensureCalls);
        Assert.Equal(1, closeCalls);
        Assert.True(refresh.IsQueued);
    }

    [Fact]
    public void RefreshStart_QueuesBehindBusyAutoRetainerWithoutTouchingRetainerUi()
    {
        using var directory = new TemporaryDirectory();
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        using var refresh = new AutoRetainerRefreshService(
            CreateProxy<IFramework>(unused),
            log,
            captures,
            CreateProxy<IRetainerAutomationSession>(unused),
            new FakeAutoRetainerIpc { IsBusy = true });

        Assert.True(refresh.Start());
        Assert.True(refresh.IsQueued);
        Assert.Equal("Retainer refresh queued behind AutoRetainer.", refresh.Status);
    }

    [Fact]
    public void AutomaticRefresh_StartsOnceBrowserAndRetainerListAreBothVisible()
    {
        using var directory = new TemporaryDirectory();
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var framework = CreateProxy<IFramework>((method, arguments) =>
        {
            Assert.Equal(nameof(IFramework.RunOnTick), method.Name);
            var callback = Assert.IsAssignableFrom<Delegate>(arguments![0]);
            return CreateCompletedTask(method.ReturnType, callback.DynamicInvoke());
        });
        var listReady = false;
        var ensureCalls = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            "get_IsRetainerListReady" => listReady,
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) => Task.FromResult(
                (++ensureCalls, RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.")).Item2),
            _ => throw new InvalidOperationException($"Unexpected session call: {method.Name}."),
        });
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        using var refresh = new AutoRetainerRefreshService(
            framework,
            log,
            captures,
            session,
            new FakeAutoRetainerIpc(),
            automation: null,
            countAvailableRetainers: () => 2);

        refresh.TickAutomatic(stockBrowserVisible: false);
        refresh.TickAutomatic(stockBrowserVisible: true);
        Assert.Equal(0, ensureCalls);

        listReady = true;
        refresh.TickAutomatic(stockBrowserVisible: true);

        Assert.True(SpinWait.SpinUntil(
            () => refresh.Status == "Retainer refresh queued.",
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, ensureCalls);
        Assert.True(refresh.IsQueued);
    }

    [Fact]
    public void AutomaticRefresh_DoesNotQueueSecondPassBehindBusyAutoRetainer()
    {
        using var directory = new TemporaryDirectory();
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var listChecks = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            "get_IsRetainerListReady" => (++listChecks, true).Item2,
            _ => throw new InvalidOperationException($"Unexpected session call: {method.Name}."),
        });
        var log = CreateProxy<IPluginLog>((_, _) => null);
        var scanner = new InventoryScanner(CreateProxy<IDataManager>(unused), log);
        using var captures = new RetainerCaptureService(
            CreateProxy<IAddonLifecycle>(unused),
            log,
            scanner,
            new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json"))),
            () => TestData.Owner);
        var autoRetainer = new FakeAutoRetainerIpc { IsBusy = true };
        using var refresh = new AutoRetainerRefreshService(
            CreateProxy<IFramework>(unused),
            log,
            captures,
            session,
            autoRetainer);

        refresh.TickAutomatic(stockBrowserVisible: true);
        autoRetainer.IsBusy = false;
        refresh.TickAutomatic(stockBrowserVisible: true);

        Assert.Equal(0, listChecks);
        Assert.False(refresh.IsQueued);
        Assert.Equal("Retainer refresh has not run.", refresh.Status);
    }

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
    public void PostprocessRequest_BindsRetainerAndRefreshOwnershipUntilExactCompletion()
    {
        var state = new AutoRetainerPostprocessState();
        Assert.True(state.TryBegin("Eris", true, out var fullRefresh));
        Assert.False(state.TryMarkReady("Not Eris", out _));
        Assert.True(state.TryMarkReady("Eris", out var ready));
        Assert.NotNull(ready);
        Assert.True(ready.PartOfFullRefresh);
        Assert.True(state.TryComplete(ready));

        Assert.True(state.TryBegin("Eris", false, out var piggyback));
        Assert.False(state.TryComplete(ready));
        Assert.True(state.HasOutstanding);
        Assert.False(piggyback.PartOfFullRefresh);
    }

    [Fact]
    public void PostprocessCancellation_ReturnsOnlyReadyOwnedRequestForFinish()
    {
        var state = new AutoRetainerPostprocessState();
        Assert.True(state.TryBegin("Eris", true, out _));
        Assert.Null(state.Cancel());

        Assert.True(state.TryBegin("Eris", false, out _));
        Assert.True(state.TryMarkReady("Eris", out var ready));

        Assert.Same(ready, state.Cancel());
        Assert.False(state.HasOutstanding);
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
        using var queue = new AutomaticRetrievalQueue(journal, executor, () => TestData.Owner, autoRetainer);

        queue.Tick();

        Assert.Equal(1, executor.DepositStarts);
        Assert.True(autoRetainer.IsSuppressed);
        executor.Complete();
        queue.Tick();

        Assert.False(autoRetainer.IsSuppressed);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
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
