using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Filtering.Evaluation;
using FFXIVClientStructs.FFXIV.Client.Game;
using RQ.Automation;
using RQ.Domain;
using RQ.Operations;
using RQ.Planning;
using RQ.Persistence;
using RQ.Inventory;
using System.Reflection;

namespace RQ.Tests;

public sealed class MarketRunExecutorTests
{
    private static ListingPlan Plan(int count = 1, int quantity = 2) => new()
    {
        Owner = new() { LocalContentId = 42, HomeWorldId = 1 },
        Assignments = [new() { ItemId = 1, ItemName = "Hull", RetainerId = 10, RetainerName = "Alpha",
            ListingCount = count, QuantityPerListing = quantity, UnitPrice = 50 }],
    };
    private static MarketRunEvaluation Evaluation(ListingPlan plan) => new(plan.Id, plan.Revision,
        plan.Assignments.Select(a => new MarketRunRetainer(a.RetainerId, a.RetainerName, true,
            [new(a, a.ListingCount, Evidence.Known(0), Evidence.Known(0), Evidence.Known(a.ListingCount), Evidence.Known(a.ListingCount), Evidence.Known(0), Evidence.Known(0))],
            a.ListingCount, Evidence.Known(0), Evidence.Known(a.ListingCount), Evidence.Known(a.ListingCount), Evidence.Known(0), Evidence.Known(0), Evidence.Known(20), Evidence.Known(0), Evidence.Known(0), [])).ToArray(),
        [new(1, "Hull", ItemQualityPolicy.NqOnly, 2, Evidence.Known(0), Evidence.Known(2), Evidence.Known(2), Evidence.Known(0), null, 0, Evidence.Known(0), Evidence.Known(0))], []);

    [Fact]
    public async Task PlainRefill_UsesPlanPriceAndLiveStack()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        await executor.RunAsync(plan, Evaluation(plan));
        var post = Assert.Single(executor.Result!.Mutations);
        Assert.Equal("Post", post.Intent.Kind); Assert.Equal(50u, post.Intent.Price);
        Assert.Equal(MarketMutationOutcome.Committed, post.Outcome);
        Assert.Equal("Completed", executor.Result.State);
    }

    [Theory]
    [InlineData(10u, 1)]
    [InlineData(null, 0)]
    public async Task RepriceToPlan_UnknownPriceLeftAlone(uint? price, int reprices)
    {
        var plan = Plan(); var fake = new Driver(); fake.Live[10] = [new(0, 1, 2, false, price)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(reprices, executor.Result!.Mutations.Count(m => m.Intent.Kind == "Reprice"));
        Assert.DoesNotContain(executor.Result.Mutations, m => m.Intent.Kind == "Post");
        if (price is null) Assert.Contains(executor.Result.Results, s => s.Contains("unknown price"));
        else Assert.Equal(50u, fake.Live[10][0].UnitPrice);
    }

    [Fact]
    public async Task PullThenPost_OnlyNeededNumber_LiveDisagreesWithEvaluation()
    {
        var plan = Plan(); var fake = new Driver();
        fake.Live[10] = Enumerable.Range(0, 20).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, (uint)(i + 1))).ToList();
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(["Pull", "Post"], executor.Result!.Mutations.Select(m => m.Intent.Kind));
        Assert.Equal(20, fake.Live[10].Count);
        Assert.Contains(executor.Result.Results, s => s.Contains("player inventory"));
    }

    [Theory]
    [InlineData(MarketMutationOutcome.Indeterminate)]
    [InlineData(MarketMutationOutcome.FailedBeforeSend)]
    public async Task FailedPost_DoesNotRetry_ContinuesNextRetainer(MarketMutationOutcome outcome)
    {
        var plan = Plan(2); plan.Assignments.Add(new() { ItemId = 1, ItemName = "Hull", RetainerId = 11, RetainerName = "Beta", QuantityPerListing = 2, UnitPrice = 50 });
        var fake = new Driver { Outcome = outcome }; fake.Stock = [new(InventoryType.Inventory1, 0, 1, 10)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal([10ul, 11ul], fake.Posts);
        Assert.Equal(outcome == MarketMutationOutcome.Indeterminate ? 2 : 0, executor.Result!.NeedsCheck.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineBeforeSendVsAfterSend(bool sent)
    {
        var plan = Plan(); var fake = new Driver { ThrowAfterSend = sent, ThrowBeforeSend = !sent };
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(sent ? MarketMutationOutcome.Indeterminate : MarketMutationOutcome.FailedBeforeSend, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Equal(sent, executor.Result.NeedsCheck.Contains(10));
    }

    [Fact]
    public async Task CancelDuringDispatchedMutation_FinishesItThenStops()
    {
        var plan = Plan(2); var fake = new Driver(); var executor = Create(plan, fake);
        fake.OnPost = executor.Cancel;
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Single(executor.Result!.Mutations); Assert.Equal("Cancelled", executor.Result.State);
        Assert.Equal(MarketMutationOutcome.Committed, executor.Result.Mutations[0].Outcome);
    }

    [Fact]
    public async Task PlanRevisionChanges_StopsBeforeNextMutation()
    {
        var plan = Plan(2); var fake = new Driver(); var executor = Create(plan, fake);
        fake.OnPost = () => plan.Revision++;
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Single(executor.Result!.Mutations); Assert.Equal("Stopped", executor.Result.State);
        Assert.Contains("Plan changed", executor.Result.Reason);
        Assert.Equal(1, executor.Result.PlanRevision);
    }

    [Fact]
    public async Task SplitStock_IsNotMerged()
    {
        var plan = Plan(); var fake = new Driver { Stock = [new(InventoryType.Inventory1, 0, 1, 1), new(InventoryType.Inventory1, 1, 1, 1)] };
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(fake.Posts); Assert.Contains(executor.Result!.Results, s => s.Contains("split across stacks"));
    }

    [Theory]
    [InlineData(false, MarketMutationOutcome.Unchanged)]
    [InlineData(true, MarketMutationOutcome.Committed)]
    public async Task FailedReprice_IsClassifiedByLiveRescan_NotResent(bool applied, MarketMutationOutcome expected)
    {
        var plan = Plan(); var fake = new Driver { Outcome = MarketMutationOutcome.Indeterminate, ApplyReprice = applied };
        fake.Live[10] = [new(0, 1, 2, false, 10)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(expected, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Empty(executor.Result.NeedsCheck);
    }

    [Fact]
    public async Task FailedRecovery_StopsBeforeNextRetainer()
    {
        var plan = Plan(); plan.Assignments.Add(new() { ItemId = 1, ItemName = "Hull", RetainerId = 11, RetainerName = "Beta", QuantityPerListing = 2, UnitPrice = 50 });
        var fake = new Driver { FailReturn = true, Outcome = MarketMutationOutcome.FailedBeforeSend };
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal([10ul], fake.Posts); Assert.Equal("Stopped", executor.Result!.State);
    }

    [Fact]
    public async Task FullPlayerInventory_BlocksPull()
    {
        var plan = Plan(); var fake = new Driver { FreeSlots = 0 };
        fake.Live[10] = Enumerable.Range(0, 20).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, 10)).ToList();
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(executor.Result!.Mutations); Assert.Contains(executor.Result.Results, s => s.Contains("blocked by inventory space"));
    }

    [Fact]
    public async Task ChangedExactSource_IsNotPosted()
    {
        var plan = Plan(); var fake = new Driver(); var reads = 0;
        fake.OnPlayer = () => { if (++reads == 3) fake.Stock = []; };
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(executor.Result!.Mutations);
    }

    [Fact]
    public async Task IntentDurableBeforeDispatch_InterruptedRecordHasUnknownOutcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-market-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MarketRunStore(Path.Combine(directory, "run.json"));
            var plan = Plan(); var fake = new Driver();
            fake.OnPost = () =>
            {
                var recovered = store.Load()!;
                Assert.Equal("Interrupted", recovered.State);
                Assert.Null(Assert.Single(recovered.Mutations).Outcome);
                Assert.Contains(10ul, recovered.NeedsCheck);
            };
            var executor = new MarketRunExecutor(fake, new(), () => plan, store.Save);
            await executor.RunAsync(plan, Evaluation(plan));
            Assert.Equal("Completed", store.Load()!.State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LeaseHeldAcrossGatherAndMutations_DirtyDraftCannotStart()
    {
        var plan = Plan(); var fake = new Driver(); var lease = new AutomationLease();
        var executor = new MarketRunExecutor(fake, lease, () => plan, _ => { });
        await executor.RunAsync(plan, Evaluation(plan), true); Assert.Empty(fake.Posts);
        fake.OnPost = () => Assert.True(lease.IsHeld);
        fake.OnGather = () => Assert.True(lease.IsHeld);
        await executor.RunAsync(plan, Evaluation(plan)); Assert.False(lease.IsHeld);
    }

    private static MarketRunExecutor Create(ListingPlan plan, Driver fake) => new(fake, new(), () => plan, _ => { });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gather_UsesJournalAndBorrowedLease_RetrievesOnlyShortfall(bool full)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-market-gather-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plan = Plan();
            var state = new StateRepository(new QuartermasterStateStore(Path.Combine(directory, "state.json")));
            var journal = new OperationJournal(state);
            using var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory, "retainers.json")));
            cache.Upsert(new CachedRetainer { RetainerId = 10, RetainerName = "Alpha", Owner = plan.Owner,
                Bags = [new() { BagName = "RetainerPage1", ObservedAtUtc = DateTime.UtcNow,
                    Items = [new() { ItemId = 1, ItemName = "Hull", Quantity = 20 }] }] });
            var playerCache = new PlayerInventoryCacheRepository(new PlayerInventoryCacheStore(Path.Combine(directory, "player.json")));
            var player = new List<DalamudInventoryStack> { new(InventoryType.Inventory1, 0, 1, 3) };
            if (full) player.AddRange(Enumerable.Range(1, 139).Select(i => new DalamudInventoryStack((InventoryType)(i / 35), i % 35, 2, 1)));
            var source = new DalamudInventoryStack(InventoryType.RetainerPage1, 0, 1, 20);
            var transferred = 0;
            var automation = new AutomationLease();
            var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
            ((SessionProxy)(object)session).Handler = (method, args) =>
            {
                Assert.True(automation.IsHeld);
                switch (method.Name)
                {
                    case nameof(IRetainerAutomationSession.ScanPlayerInventoryAsync):
                        return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(player.ToArray());
                    case nameof(IRetainerAutomationSession.ScanRetainerAsync):
                        return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([source]);
                    case nameof(IRetainerAutomationSession.RetrieveAsync):
                        var quantity = (int)args![1]!;
                        transferred += quantity;
                        source = source with { Quantity = source.Quantity - quantity };
                        player[0] = player[0] with { Quantity = player[0].Quantity + quantity };
                        return Task.FromResult(new RetainerRetrievalResult(true, quantity, "Retrieved", "Verified"));
                    default:
                        return Task.FromResult(RetainerAutomationResult.Succeeded("Ready", "Ready"));
                }
            };
            var adapter = new MarketRunLiveDriver(session, new RetainerLiveDriver(session), journal, cache, playerCache, automation,
                () => plan.Owner, _ => "Hull");
            Assert.True(automation.TryAcquire("market test", out var lease));
            using (lease)
            {
                var reports = await adapter.GatherAsync([new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { }, CancellationToken.None);
                Assert.True(automation.IsHeld);
                Assert.Equal(full ? 0 : 7, transferred);
                var operation = Assert.Single(journal.History(plan.Owner, 10));
                Assert.Equal(full ? OperationStatuses.Failed : OperationStatuses.Succeeded, operation.Status);
                Assert.Equal(full ? 0 : 7, Assert.Single(operation.Lines).TransferredQuantity);
                if (full) Assert.Contains(reports, r => r.Contains("inventory space"));
                else Assert.Equal(13u, cache.Snapshot()[10].Bags.SelectMany(b => b.Items).Single(i => i.ItemId == 1).Quantity);
            }
            Assert.False(automation.IsHeld);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Deadline_CancelsPrimitive_WithFiniteBudget()
    {
        var deadline = new MarketRunDeadline(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deadline.RunAsync(async t =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, t);
            return true;
        }));
    }

    public class SessionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
    private sealed class Driver : IMarketRunDriver
    {
        public Dictionary<ulong, List<RetainerMarketListingTarget>> Live = new() { [10] = [], [11] = [] };
        public List<DalamudInventoryStack> Stock = [new(InventoryType.Inventory1, 0, 1, 10)];
        public List<ulong> Posts = [];
        public int FreeSlots = 100;
        public bool ThrowBeforeSend, ThrowAfterSend;
        public bool ApplyReprice = true, FailReturn;
        public Action? OnPost, OnGather, OnPlayer;
        public MarketMutationOutcome Outcome = MarketMutationOutcome.Committed;
        private ulong active;
        public string ItemName(uint id) => id == 1 ? "Hull" : "Bow";
        public Task RequireRetainerListAsync(CancellationToken t) => Task.CompletedTask;
        public Task OpenAsync(ulong id, string name, CancellationToken t) { active = id; return Task.CompletedTask; }
        public Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<RetainerMarketListingTarget>>(Live[active].ToArray());
        public Task<MarketPlayerInventory> PlayerAsync(CancellationToken t) { OnPlayer?.Invoke(); return Task.FromResult(new MarketPlayerInventory(Stock.ToArray(), FreeSlots)); }
        public Task<MarketMutationOutcome> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken t)
        { if (ApplyReprice) Live[active][Live[active].IndexOf(listing)] = listing with { UnitPrice = price }; return Task.FromResult(Outcome); }
        public Task<MarketMutationOutcome> PullAsync(RetainerMarketListingTarget listing, CancellationToken t)
        { Live[active].Remove(listing); return Task.FromResult(Outcome); }
        public Task<MarketMutationOutcome> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken t)
        {
            Posts.Add(active); OnPost?.Invoke();
            if (ThrowBeforeSend) throw new OperationCanceledException();
            if (ThrowAfterSend) throw new RetainerMarketMutationIndeterminateException("Deadline", "After send", null, new OperationCanceledException(), t);
            if (Outcome == MarketMutationOutcome.Committed)
            {
                Stock[Stock.IndexOf(source)] = source with { Quantity = source.Quantity - quantity };
                Live[active].Add(new(Enumerable.Range(0, 20).First(i => Live[active].All(l => l.SlotIndex != i)), source.ItemId, quantity, source.IsHighQuality, price));
            }
            return Task.FromResult(Outcome);
        }
        public Task ReturnAsync(CancellationToken t) => FailReturn ? Task.FromException(new InvalidOperationException("List unreachable")) : Task.CompletedTask;
        public Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, CancellationToken t)
        { OnGather?.Invoke(); check(); return Task.FromResult<IReadOnlyList<string>>([]); }
    }
}
