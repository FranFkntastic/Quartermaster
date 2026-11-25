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
    public async Task RepriceReselectsListingThatMovesIntoPreviouslyUsedSlot()
    {
        var plan = Plan(2); var fake = new Driver();
        fake.Live[10] = [new(0, 1, 2, false, 10), new(1, 1, 2, false, 20)];
        var reprices = 0;
        fake.OnReprice = () =>
        {
            if (++reprices != 1) return;
            fake.OnListings = () =>
            {
                fake.OnListings = null;
                fake.Live[10].RemoveAt(0); // The first repriced listing sells.
                fake.Live[10] = fake.Live[10].Select((l, i) => l with { SlotIndex = i }).ToList();
            };
        };
        var executor = Create(plan, fake);
        await executor.RunAsync(plan, Evaluation(plan));
        var receipts = executor.Result!.Mutations.Where(m => m.Intent.Kind == "Reprice").ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, r => Assert.Equal(0, r.Intent.Listing!.SlotIndex));
        Assert.All(fake.Live[10], l => Assert.Equal(50u, l.UnitPrice));
        Assert.Equal("Completed", executor.Result.State);
    }

    [Fact]
    public async Task IndeterminatePost_IsNotRetriedAndStopsBeforeNextRetainer()
    {
        var plan = Plan(2); plan.Assignments.Add(new() { ItemId = 1, ItemName = "Hull", RetainerId = 11, RetainerName = "Beta", QuantityPerListing = 2, UnitPrice = 50 });
        var fake = new Driver { Outcome = MarketMutationOutcome.Indeterminate };
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal([10ul], fake.Posts);
        Assert.Single(executor.Result!.NeedsCheck);
        Assert.Equal("Stopped", executor.Result.State);
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
            var executor = new MarketRunExecutor(fake, new(), _ => plan, store.Save);
            await executor.RunAsync(plan, Evaluation(plan));
            Assert.Equal("Completed", store.Load()!.State);
            store.Clear(); Assert.Null(store.Load());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static MarketRunExecutor Create(ListingPlan plan, Driver fake) => new(fake, new(), _ => plan, _ => { });

    // A market run for one plan must never move stock of items outside it, even when those items have placement
    // homes and sit loose in the bags or in a retainer that is not their home: from the user's side that looks like
    // the run randomly shuffling unrelated inventory.
    [Fact]
    public async Task MarketRun_StowsAndRelocatesOnlyItsOwnPlanItems()
    {
        var plan = Plan();
        var owner = plan.Owner;
        ItemPlacementRule Home(uint itemId, string name, ulong retainerId) => new()
        {
            Owner = owner, ItemId = itemId, ItemName = name, Quality = ItemQualityPolicy.NqOnly, Homes = [new(owner, retainerId)],
        };
        ItemPlacementRule[] placements = [Home(1, "Hull", 10), Home(2, "Bow", 11)];
        (uint, bool, int)[] bags = [(1, false, 5), (2, false, 7)];
        var retainers = new Dictionary<ulong, CachedRetainer>
        {
            [12] = new()
            {
                RetainerId = 12, RetainerName = "Gamma", Owner = owner,
                Bags = [new() { BagName = "RetainerPage1", Items = [new() { ItemId = 1, Quantity = 3 }, new() { ItemId = 2, Quantity = 4 }] }],
            },
        };
        var evaluation = Evaluation(plan);

        static void OnlyPlanItem(IReadOnlyList<StowageDepositRequest>? requests)
        {
            Assert.NotNull(requests);
            Assert.Equal([1U], requests.Select(request => request.ItemId).Distinct());
        }
        OnlyPlanItem(MarketRunPlanner.StowRequests(plan, placements, bags, owner));
        OnlyPlanItem(MarketRunPlanner.ProjectedStowRequests(plan, placements, evaluation, bags, owner));
        OnlyPlanItem(MarketRunPlanner.RelocationRequests(plan, placements, retainers, owner));

        var fake = new Driver();
        await Create(plan, fake).RunAsync(plan, evaluation, ownerPlacements: placements);
        Assert.Equal([1U], Assert.IsAssignableFrom<IReadOnlyList<ItemPlacementRule>>(fake.Stowed).Select(rule => rule.ItemId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorityLostWhilePostPending_NoNewReadsOrCleanupAfterSettlement(bool unload)
    {
        var plan = Plan(); var current = plan; var fake = new Driver();
        var pending = new TaskCompletionSource<MarketMutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.PendingPost = pending.Task;
        var executor = new MarketRunExecutor(fake, new(), _ => current, _ => { });
        var run = executor.RunAsync(plan, Evaluation(plan));
        Assert.Single(fake.Posts);
        var scansAtPost = fake.ListingScans;
        if (unload) executor.CancelAndWait(TimeSpan.Zero);
        else current = new ListingPlan { Owner = plan.Owner with { LocalContentId = 999 } };
        pending.SetResult(MarketMutationOutcome.Committed);
        await run;
        Assert.Equal(scansAtPost, fake.ListingScans);
        Assert.Equal(0, fake.Returns);
        Assert.Equal(0, fake.Closes);
        Assert.False(executor.Result!.ListingSummaryComplete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorityLostWhileRepricePending_PreservesUnknownWithoutRecoveryScan(bool unload)
    {
        var plan = Plan(); var current = plan; var fake = new Driver();
        fake.Live[10] = [new(0, 1, 2, false, 10)];
        var pending = new TaskCompletionSource<MarketMutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.PendingReprice = pending.Task;
        var executor = new MarketRunExecutor(fake, new(), _ => current, _ => { });
        var run = executor.RunAsync(plan, Evaluation(plan));
        var scansAtReprice = fake.ListingScans;
        if (unload) executor.CancelAndWait(TimeSpan.Zero);
        else current = new ListingPlan { Owner = plan.Owner with { LocalContentId = 999 } };
        pending.SetResult(MarketMutationOutcome.Indeterminate);
        await run;
        Assert.Equal(scansAtReprice, fake.ListingScans);
        Assert.Equal(MarketMutationOutcome.Indeterminate, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Contains(10ul, executor.Result.NeedsCheck);
        Assert.Equal(0, fake.Returns); Assert.Equal(0, fake.Closes);
    }

    [Fact]
    public async Task OwnerChangesDuringRepriceRecovery_ResultIsNotAccepted()
    {
        var plan = Plan(); var current = plan; var fake = new Driver { Outcome = MarketMutationOutcome.Indeterminate };
        fake.Live[10] = [new(0, 1, 2, false, 10)];
        fake.OnReprice = () => fake.OnListings = () => current = new ListingPlan { Owner = plan.Owner with { LocalContentId = 999 } };
        var executor = new MarketRunExecutor(fake, new(), _ => current, _ => { });
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(MarketMutationOutcome.Indeterminate, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Contains(10ul, executor.Result.NeedsCheck);
        Assert.Equal(0, fake.Returns); Assert.Equal(0, fake.Closes);
    }

    [Fact]
    public async Task SeveralPulls_ReSelectCompactedLiveSlots_ProtectPlannedListings()
    {
        var plan = Plan(4); var fake = new Driver { Compact = true };
        fake.Live[10] = [new(0, 1, 2, false, 50), .. Enumerable.Range(1, 19).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, (uint)i))];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        var pulls = executor.Result!.Mutations.Where(m => m.Intent.Kind == "Pull").ToArray();
        Assert.Equal(3, pulls.Length);
        Assert.All(pulls, p => { Assert.Equal(2u, p.Intent.Listing!.ItemId); Assert.Equal("Bow", p.Intent.ItemName); });
        Assert.Equal(4, fake.Live[10].Count(l => l.ItemId == 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsettledCall_HoldsLeaseUntilCompletion(bool cleanupThrows)
    {
        var plan = Plan(); var fake = new Driver(); var automation = new AutomationLease();
        var pending = new TaskCompletionSource<MarketMutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.PendingPost = pending.Task;
        var ipc = new AutoRetainer();
        var executor = new MarketRunExecutor(fake, automation, _ => plan, r => { if (cleanupThrows && r.State != "Running") throw new IOException("cleanup"); }, new AutoRetainerSuppression(ipc), deadlines: new MarketRunDeadline(TimeSpan.FromMilliseconds(5)));
        if (cleanupThrows) await Assert.ThrowsAsync<IOException>(() => executor.RunAsync(plan, Evaluation(plan)));
        else await executor.RunAsync(plan, Evaluation(plan));
        Assert.True(automation.IsHeld); Assert.True(ipc.IsSuppressed); Assert.Equal("Stopped", executor.Result!.State);
        pending.SetResult(MarketMutationOutcome.Committed);
        for (var n = 0; n < 100 && automation.IsHeld; n++) await Task.Delay(5);
        Assert.False(automation.IsHeld);
        Assert.False(ipc.IsSuppressed);
    }

    [Fact]
    public async Task DisposedExecutor_CannotOverwriteNewRunRecord()
    {
        var plan = Plan(); var fake = new Driver(); var writes = 0;
        var pending = new TaskCompletionSource<MarketMutationOutcome>(); fake.PendingPost = pending.Task;
        var executor = new MarketRunExecutor(fake, new(), _ => plan, _ => writes++);
        var run = executor.RunAsync(plan, Evaluation(plan));
        executor.CancelAndWait(TimeSpan.Zero);
        var atDispose = writes;
        pending.SetResult(MarketMutationOutcome.Committed);
        await run;
        Assert.Equal(atDispose, writes); Assert.False(executor.CanStart);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("unload")]
    [InlineData("owner")]
    [InlineData("scanowner")]
    [InlineData("scanunload")]
    public async Task GatherAuthorityLoss_StopsRetrievalWithoutCorruptingJournalOrCache(string mode)
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
                RequestedSources = DalamudRetainerInventory.OrdinaryItemContainers.Select(type => type.ToString()).ToList(),
                ObservedSources = DalamudRetainerInventory.OrdinaryItemContainers.Select(type => type.ToString()).ToList(),
                Bags = [new() { BagName = "RetainerPage1", ObservedAtUtc = DateTime.UtcNow,
                    Items = [new() { ItemId = 1, ItemName = "Hull", Quantity = 20 }] }] });
            var playerCache = new PlayerInventoryCacheRepository(new PlayerInventoryCacheStore(Path.Combine(directory, "player.json")));
            var player = new List<DalamudInventoryStack> { new(InventoryType.Inventory1, 0, 1, 3) };
            var source = new DalamudInventoryStack(InventoryType.RetainerPage1, 0, 1, 20);
            var sources = mode == "lost" ? new List<DalamudInventoryStack> { source with { Quantity = 4 }, source with { SlotIndex = 1, Quantity = 16 } } : [source];
            var transferred = 0;
            var authority = true;
            var currentOwner = plan.Owner;
            var callsAfterAuthorityLoss = 0;
            var closes = 0;
            IDisposable? lease = null;
            var automation = new AutomationLease();
            var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
            ((SessionProxy)(object)session).Handler = (method, args) =>
            {
                if (!authority || !currentOwner.Matches(plan.Owner) || !automation.IsHeld) callsAfterAuthorityLoss++;
                if (method.Name == nameof(IRetainerAutomationSession.CloseRetainerAsync)) closes++;
                if (mode != "lost" || transferred == 0) Assert.True(automation.IsHeld);
                switch (method.Name)
                {
                    case nameof(IRetainerAutomationSession.ScanPlayerInventoryAsync):
                        return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(player.ToArray());
                    case nameof(IRetainerAutomationSession.ScanRetainerAsync):
                        if (mode is "scanowner" or "scanunload")
                        {
                            if (mode == "scanowner") currentOwner = plan.Owner with { LocalContentId = 999 };
                            else authority = false;
                            return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([source with { Quantity = 19 }]);
                        }
                        return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(sources.Where(s => s.Quantity > 0).ToArray());
                    case nameof(IRetainerAutomationSession.RetrieveAsync):
                        var quantity = (int)args![1]!;
                        transferred += quantity;
                        source = source with { Quantity = source.Quantity - quantity };
                        var selected = (DalamudInventoryStack)args[0]!;
                        var index = sources.FindIndex(s => s.SlotIndex == selected.SlotIndex);
                        sources[index] = sources[index] with { Quantity = sources[index].Quantity - quantity };
                        player[0] = player[0] with { Quantity = player[0].Quantity + quantity };
                        if (mode == "lost") lease!.Dispose();
                        if (mode == "unload") authority = false;
                        if (mode == "owner") currentOwner = plan.Owner with { LocalContentId = 999 };
                        return Task.FromResult(new RetainerRetrievalResult(true, quantity, "Retrieved", "Verified"));
                    default:
                        return Task.FromResult(RetainerAutomationResult.Succeeded("Ready", "Ready"));
                }
            };
            var adapter = new MarketRunLiveDriver(session, new RetainerLiveDriver(session), journal, cache, playerCache, automation,
                () => currentOwner, _ => "Hull");
            Assert.True(automation.TryAcquire("market test", out lease));
            using (lease)
            {
                Task Gather() => adapter.GatherAsync([new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { }, () => authority, CancellationToken.None);
                if (mode is "scanowner" or "scanunload")
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(Gather);
                    Assert.Equal(20u, cache.Snapshot()[10].Bags.SelectMany(b => b.Items).Single(i => i.ItemId == 1).Quantity);
                }
                else
                {
                    await Assert.ThrowsAsync<MarketGatherIndeterminateException>(Gather);
                    Assert.Equal(OperationStatuses.Indeterminate, Assert.Single(journal.History(plan.Owner, 10)).Status);
                }
                Assert.Equal(0, callsAfterAuthorityLoss);
                Assert.Equal(0, closes);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    public class SessionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class AutoRetainer : IAutoRetainerIpc
    {
        public bool IsAvailable => true;
        public bool IsBusy => false;
        public bool IsSuppressed { get; private set; }
        public void SetSuppressed(bool value) => IsSuppressed = value;
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void Dispose() { }
    }
    private sealed class Driver : IMarketRunDriver
    {
        public Task<IReadOnlyList<string>> StowAsync(Guid planId, IReadOnlyList<ItemPlacementRule> planPlacements,
            IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken token,
            Action<MarketMutationReceipt> receiptChanged, Action<string> reportChanged,
            bool reorganizeRetainers)
        { Stowed = planPlacements; check(); return Task.FromResult<IReadOnlyList<string>>([]); }
        public IReadOnlyList<ItemPlacementRule>? Stowed;
        public Dictionary<ulong, List<RetainerMarketListingTarget>> Live = new() { [10] = [], [11] = [] };
        public List<DalamudInventoryStack> Stock = [new(InventoryType.Inventory1, 0, 1, 10)];
        public List<ulong> Posts = [];
        public int Returns;
        public int ListingScans;
        public Action? OnListings;
        public int Closes;
        public bool Compact;
        public Task<MarketMutationOutcome>? PendingPost;
        public Task<MarketMutationOutcome>? PendingReprice;
        public Action? OnPost, OnReprice;
        public MarketMutationOutcome Outcome = MarketMutationOutcome.Committed;
        private ulong active;
        public string ItemName(uint id) => id == 1 ? "Hull" : "Bow";
        public Task RequireRetainerListAsync(CancellationToken t) => Task.CompletedTask;
        public Task OpenAsync(ulong id, string name, CancellationToken t) { active = id; return Task.CompletedTask; }
        public Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken t)
        {
            ListingScans++;
            OnListings?.Invoke();
            return Live[active].Any(l => l.UnitPrice is null or 0 or > 999999999)
            ? Task.FromException<IReadOnlyList<RetainerMarketListingTarget>>(new InvalidOperationException("Invalid market price"))
            : Task.FromResult<IReadOnlyList<RetainerMarketListingTarget>>(Live[active].ToArray());
        }
        public Task<MarketPlayerInventory> PlayerAsync(CancellationToken t) => Task.FromResult(new MarketPlayerInventory(Stock.ToArray(), 100));
        public async Task<MarketMutationResult> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken t)
        { OnReprice?.Invoke(); if (PendingReprice is not null) return new(await PendingReprice);
            Live[active][Live[active].IndexOf(listing)] = listing with { UnitPrice = price }; return new(Outcome); }
        public Task<MarketMutationResult> PullAsync(RetainerMarketListingTarget listing, CancellationToken t)
        {
            if (Outcome == MarketMutationOutcome.Committed)
            {
                Live[active].Remove(listing);
                if (Compact) Live[active] = Live[active].Select((l, i) => l with { SlotIndex = i }).ToList();
            }
            return Task.FromResult(new MarketMutationResult(Outcome));
        }
        public async Task<MarketMutationResult> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken t)
        {
            Posts.Add(active); OnPost?.Invoke();
            if (PendingPost is not null) return new(await PendingPost);
            if (Outcome == MarketMutationOutcome.Committed)
            {
                Stock[Stock.IndexOf(source)] = source with { Quantity = source.Quantity - quantity };
                Live[active].Add(new(Enumerable.Range(0, 20).First(i => Live[active].All(l => l.SlotIndex != i)), source.ItemId, quantity, source.IsHighQuality, price));
            }
            return new(Outcome);
        }
        public Task ReturnAsync(CancellationToken t) { Returns++; return Task.CompletedTask; }
        public Task CloseListAsync(CancellationToken t) { Closes++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken t, Action<MarketMutationReceipt>? receiptChanged = null)
        { check(); return Task.FromResult<IReadOnlyList<string>>([]); }
    }
}
