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

    // Kept because a leak here is silent and costly: drawing a restricted shortage from a retainer the requester
    // excluded moves stock reserved for something else, and nothing in a live run flags it. Covers the persisted
    // path end to end: submission, the stored line, the planner's candidates and the coordinator's stack loop.
    [Fact]
    public async Task SourceRestrictedShortage_NeverDrawsFromExcludedRetainer()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        CachedRetainer Observed(CachedRetainer retainer)
        {
            retainer.ObservedSources = Enumerable.Range(1, 7).Select(page => $"RetainerPage{page}").ToList();
            return retainer;
        }
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [10] = Observed(TestData.Retainer(10, "Allowed", (100, "Ore", 10))),
            [20] = Observed(TestData.Retainer(20, "Excluded", (100, "Ore", 50))),
        });
        var cache = new RetainerCacheRepository(store);
        var queue = new FrameworkWorkQueue();
        var submissions = new ShortageSubmissionService("provider", repository, queue, () => TestData.Owner, retainers: cache.Snapshot);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var ack = JsonSerializer.Deserialize<SubmissionAcknowledgement>(submissions.Submit(JsonSerializer.Serialize(new ShortageRequest
        {
            Schema = ShortageSubmissionService.RequestSchema,
            ProviderInstanceId = "provider",
            RequestId = "request-1",
            OperationId = "operation-1",
            SubmittedAtUtc = DateTimeOffset.UtcNow,
            ExecuteImmediately = true,
            Owner = new RequestOwner { LocalContentId = 9001, HomeWorldId = 406, CharacterName = "Current Character" },
            Items = [new ShortageRequestItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 30, ShortageQuantity = 30, SourceRetainerIds = [10] }],
        }, web)), web)!;
        Assert.Null(ack.ErrorCode);
        queue.Drain();
        var driver = new RetrievalDriver(new()
        {
            [10] = [new(InventoryType.RetainerPage1, 0, 100, 10)],
            [20] = [new(InventoryType.RetainerPage1, 0, 100, 50)],
        });
        var coordinator = new TransferCoordinator(new OperationJournal(repository), driver, cache, () => TestData.Owner,
            () => new Dictionary<uint, int>());

        await coordinator.ExecuteRetrievalAsync("operation-1");

        Assert.DoesNotContain(20UL, driver.OpenedRetainers);
        Assert.Equal([(10UL, 10)], driver.Retrieved);
        var operation = new OperationJournal(repository).Get("operation-1")!;
        Assert.Equal(OperationStatuses.PartiallySucceeded, operation.Status);
        var receipts = repository.FullSnapshot().Receipts.Where(receipt => receipt.OperationId == "operation-1").ToArray();
        Assert.Equal(TransferCoordinator.SourceRestrictionShortCode, receipts[^1].Code);
        Assert.Contains(receipts, receipt => receipt.Code == TransferCoordinator.SourceRestrictionShortCode && receipt.ItemId == 100 && receipt.Quantity == 20);
    }

    private sealed class RetrievalDriver(Dictionary<ulong, List<DalamudInventoryStack>> stock) : IRetainerTransferDriver
    {
        private ulong open;
        public List<ulong> OpenedRetainers { get; } = [];
        public List<(ulong RetainerId, int Quantity)> Retrieved { get; } = [];
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken)
        {
            open = candidate.RetainerId;
            OpenedRetainers.Add(candidate.RetainerId);
            return Task.CompletedTask;
        }
        public Task OpenInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(stock.GetValueOrDefault(open, []).ToArray());
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken)
        {
            var stacks = stock[open];
            var index = stacks.FindIndex(candidate => candidate.SlotIndex == stack.SlotIndex && candidate.ItemId == stack.ItemId);
            var left = stacks[index].Quantity - quantity;
            if (left > 0) stacks[index] = stacks[index] with { Quantity = left };
            else stacks.RemoveAt(index);
            Retrieved.Add((open, quantity));
            return Task.FromResult(new RetrievalResult(true, quantity, "TransferVerified", "Verified."));
        }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task CloseRetainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void CancelActive() { }
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

}
