using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Automation;
using RQ.Domain;
using RQ.Operations;

namespace RQ.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task RetainerLiveDriver_PropagatesCancellationIntoFrameworkWork()
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
        var driver = new RetainerLiveDriver(
            framework,
            CreateProxy<IGameGui>(unused),
            CreateProxy<IDataManager>(unused),
            CreateProxy<IPluginLog>(unused),
            CreateProxy<IObjectTable>(unused),
            CreateProxy<ITargetManager>(unused),
            CreateProxy<ISigScanner>(unused));

        var open = driver.OpenInventoryAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
        Assert.False(open.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(2)));
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
        repository.Mutate(state => state.Operations.Single(operation => operation.OperationId == automatic.OperationId).ExecuteImmediately = true);

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

        reloadedRepository.Mutate(state => state.Operations.Single(operation => operation.OperationId == automatic.OperationId).Status = OperationStatuses.Succeeded);
        executor.Complete();
        queue.Tick();
        queue.Tick();

        Assert.Equal(1, executor.Starts);
        Assert.Equal(OperationStatuses.Accepted, reloadedJournal.Get(manual.OperationId)!.Status);
    }

    [Fact]
    public void AutomaticRetrievalQueue_CancelsAndWaitsForActiveExecution()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        repository.Mutate(state => state.Operations.Single(candidate => candidate.OperationId == operation.OperationId).ExecuteImmediately = true);
        var executor = new RecordingRetrievalExecutor { CanStart = true };
        var queue = new AutomaticRetrievalQueue(journal, executor, () => TestData.Owner);
        queue.Tick();

        Assert.True(queue.CancelAndWait(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, executor.CancelCalls);
        queue.Dispose();
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
        public int CancelCalls { get; private set; }

        public Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default)
        {
            Starts++;
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void CancelActive() => CancelCalls++;
        public void Complete() => completion.TrySetResult(new(true, "Automatic retrieval completed."));
    }
}

public class ConfigurableDispatchProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
