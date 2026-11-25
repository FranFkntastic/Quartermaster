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

    // Kept because an over-cap deposit is silent: one route per home with an exact target is the only thing that keeps
    // execution from filling a capped retainer to its physical room. Covers the delivered-stow path end to end: the
    // cross-character plan, the persisted operation, the coordinator's deposit and the courier forward of the remainder.
    // A capped home whose stock is unobserved must take nothing (unknown stock is never read as holding 0).
    [Fact]
    public async Task DeliveredStow_CappedLocalStowStopsAtCap_AndForwardsExactRemainderToEachCharacter()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var (first, second) = (Character(7001, "First Alt"), Character(7002, "Second Alt"));
        repository.Mutate(StateChangeKind.Plans, state => state.Placements.Add(new ItemPlacementRule
        {
            Owner = TestData.Owner,
            ItemId = 100, ItemName = "Ore", Quality = ItemQualityPolicy.NqOnly,
            Homes = [new(TestData.Owner, 1, 1000), new(TestData.Owner, 2, 500), new(first, 3, 300), new(second, 4)],
        }));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [1] = ObservedPages(TestData.Retainer(1, "Capped", (100, "Ore", 240))),
            // Capped, but its pages were never observed: it holds an unknown amount and takes nothing.
            [2] = TestData.Retainer(2, "Unobserved"),
            [3] = ObservedPages(TestData.Retainer(3, "First Home"), first),
            [4] = ObservedPages(TestData.Retainer(4, "Second Home"), second),
        });
        var cache = new RetainerCacheRepository(store);
        var bags = Bags(2000);
        var queue = new FrameworkWorkQueue();
        var journal = new OperationJournal(repository);
        var courier = new FakeStowCourier();
        var service = new DeliveredStowService("provider", repository, queue, journal, cache.Snapshot, () => TestData.Owner,
            _ => bags, _ => 999, _ => { }, courier: courier, forwardStorePath: Path.Combine(directory.Path, "forwards.json"));

        var ack = Status(service.Submit(StowRequest("stow-1", 2000)));

        var line = Assert.Single(ack.Lines);
        Assert.Equal(760, line.Planned);
        Assert.Equal(["Capped"], line.Destinations);
        Assert.Equal([(7001UL, 300), (7002UL, 940)], line.Forwards!.Select(forward => (forward.Character.LocalContentId, forward.Wanted)));
        queue.Drain();
        var driver = new CapacityDepositDriver();
        // The bags show every verified deposit, as the game's would.
        driver.ScanPlayerInventory = _ => [new(InventoryType.Inventory1, 0, 100, 2000 - driver.DepositAttempts.Sum(attempt => attempt.Quantity))];
        await new TransferCoordinator(journal, driver, cache, () => TestData.Owner, () => new Dictionary<uint, int>())
            .ExecuteDepositAsync(ack.OperationId!);

        Assert.Equal([1UL], driver.OpenedRetainers.Distinct());
        Assert.Equal(760, driver.DepositAttempts.Sum(attempt => attempt.Quantity));
        Assert.Equal(OperationStatuses.Succeeded, journal.Get(ack.OperationId!)!.Status);

        bags = Bags(2000 - 760);
        service.Tick();

        var groups = Assert.Single(courier.Starts).Groups;
        Assert.Equal([(7001UL, 3UL, 300), (7002UL, 4UL, 940)], groups.SelectMany(group => group.Units.SelectMany(unit =>
            unit.Homes.Select(home => (group.Destination.LocalContentId ?? 0, home.RetainerId, home.Units)))));
    }

    // Kept because a forward reaching several characters is one courier run that must never start twice: a reload,
    // with or without the run's binding saved, adopts or follows it for every destination. A double dispatch would
    // move goods the first run already carries.
    [Fact]
    public void DeliveredStow_MultiCharacterForward_SurvivesReloadWithoutDoubleDispatch()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var (first, second) = (Character(7001, "First Alt"), Character(7002, "Second Alt"));
        repository.Mutate(StateChangeKind.Plans, state => state.Placements.Add(new ItemPlacementRule
        {
            Owner = TestData.Owner,
            ItemId = 100, ItemName = "Ore", Quality = ItemQualityPolicy.NqOnly,
            Homes = [new(first, 3, 200), new(second, 4)],
        }));
        var store = new RetainerCacheStore(Path.Combine(directory.Path, "cache.json"));
        store.Save(new Dictionary<ulong, CachedRetainer>
        {
            [3] = ObservedPages(TestData.Retainer(3, "First Home"), first),
            [4] = ObservedPages(TestData.Retainer(4, "Second Home"), second),
        });
        var cache = new RetainerCacheRepository(store);
        var bags = Bags(500);
        var courier = new FakeStowCourier();
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        void Tick(DeliveredStowService target)
        {
            now += TimeSpan.FromSeconds(2);
            target.Tick();
        }
        var journalPath = Path.Combine(directory.Path, "forwards.json");
        DeliveredStowService Service(string path, FrameworkWorkQueue queue) => new("provider", repository, queue, new OperationJournal(repository),
            cache.Snapshot, () => TestData.Owner, _ => bags, _ => 999, _ => { }, () => now, courier, path);
        var queue = new FrameworkWorkQueue();
        var service = Service(journalPath, queue);
        var request = StowRequest("stow-2", 500);
        Status(service.Submit(request));
        queue.Drain();
        // As journaled before the run started: a reload from this copy has no binding to follow.
        var unbound = Path.Combine(directory.Path, "forwards-unbound.json");
        File.Copy(journalPath, unbound);

        Tick(service);

        var start = Assert.Single(courier.Starts);
        Assert.Equal([(7001UL, 200), (7002UL, 300)], start.Groups.Select(group => (group.Destination.LocalContentId ?? 0, group.Units.Sum(unit => unit.Units))));
        var lines = start.Groups.SelectMany(group => group.Units).ToDictionary(unit => unit.Line, unit => unit.Units);

        foreach (var path in new[] { journalPath, unbound })
        {
            var reloaded = Service(path, new FrameworkWorkQueue());
            var replay = Status(reloaded.Submit(request));
            Assert.Null(replay.ErrorCode);
            courier.Progress = new("Running", string.Empty, string.Empty, new Dictionary<Guid, int>());
            Tick(reloaded);
            courier.Progress = new("Completed", "Succeeded", string.Empty, lines);
            Tick(reloaded);

            Assert.Single(courier.Starts);
            var forwards = Assert.Single(Status(reloaded.Get("stow-2")).Lines).Forwards!;
            Assert.Equal([(7001UL, 200, DeliveredStowService.ForwardDelivered), (7002UL, 300, DeliveredStowService.ForwardDelivered)],
                forwards.Select(forward => (forward.Character.LocalContentId, forward.Delivered ?? 0, forward.Code)));
        }
    }

    private static OwnerScope Character(ulong contentId, string name) =>
        new() { LocalContentId = contentId, HomeWorldId = 406, CharacterName = name, HomeWorldName = "Maduin" };

    private static CachedRetainer ObservedPages(CachedRetainer retainer, OwnerScope? owner = null)
    {
        retainer.Owner = owner ?? retainer.Owner;
        retainer.ObservedSources = Enumerable.Range(1, 7).Select(page => $"RetainerPage{page}").ToList();
        return retainer;
    }

    private static List<InventoryBag> Bags(int ore) =>
        [new InventoryBag { BagName = "Inventory1", Items = [new RQ.Domain.InventoryItem { ItemId = 100, ItemName = "Ore", Quantity = (uint)ore }] }];

    private static string StowRequest(string requestId, int quantity) => JsonSerializer.Serialize(new DeliveredStowRequest
    {
        Schema = DeliveredStowService.RequestSchema,
        ProviderInstanceId = "provider",
        RequestId = requestId,
        Owner = new RequestOwner { LocalContentId = 9001, HomeWorldId = 406, CharacterName = "Current Character" },
        Items = [new DeliveredStowRequestItem { ItemId = 100, ItemName = "Ore", Quantity = quantity }],
        AllowForward = true,
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static DeliveredStowStatus Status(string json) =>
        JsonSerializer.Deserialize<DeliveredStowStatus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>The courier as the delivered stow sees it: records starts; one run per courier request id, adoptable.</summary>
    private sealed class FakeStowCourier : IDeliveredStowCourier
    {
        private readonly Dictionary<string, (Guid Run, Dictionary<Guid, int> Planned)> runs = [];
        public List<(string RequestId, IReadOnlyList<(OwnerScope Destination, IReadOnlyList<DeliveredStowForwardUnit> Units)> Groups)> Starts { get; } = [];
        public DeliveredStowForwardProgress? Progress { get; set; }
        public string? AutomationBlock => null;
        public DeliveredStowForwardRefusal? Blocked() => null;

        public (Guid? Run, DeliveredStowForwardRefusal? Refusal, IReadOnlyDictionary<ulong, DeliveredStowForwardRefusal> Refused) Start(
            string courierRequestId, string requestHash, OwnerScope owner, IReadOnlyList<(OwnerScope Destination, IReadOnlyList<DeliveredStowForwardUnit> Units)> groups)
        {
            Starts.Add((courierRequestId, groups));
            var run = Guid.NewGuid();
            runs[courierRequestId] = (run, groups.SelectMany(group => group.Units).ToDictionary(unit => unit.Line, unit => unit.Units));
            return (run, null, new Dictionary<ulong, DeliveredStowForwardRefusal>());
        }

        public (DeliveredStowAdoptedRun? Run, bool Loading) Adopt(string courierRequestId) =>
            runs.TryGetValue(courierRequestId, out var known) ? (new(known.Run, known.Planned), false) : (null, false);

        public void Forget(Guid run, string courierRequestId) { }

        DeliveredStowForwardProgress? IDeliveredStowCourier.Progress(Guid run) => Progress;
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
