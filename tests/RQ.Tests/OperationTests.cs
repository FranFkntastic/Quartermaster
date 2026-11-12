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
        public List<ulong> OpenedRetainers { get; } = [];
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken)
        {
            OpenedRetainers.Add(candidate.RetainerId);
            return Task.CompletedTask;
        }
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

}
