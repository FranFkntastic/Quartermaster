using System.Reflection;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Travel;
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
