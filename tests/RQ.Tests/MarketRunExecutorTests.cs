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
        if (price is null) Assert.Contains(executor.Result.Results, s => s.Contains("Invalid market price"));
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
        Assert.Equal(outcome == MarketMutationOutcome.Indeterminate ? [10ul] : new[] { 10ul, 11ul }, fake.Posts);
        Assert.Equal(outcome == MarketMutationOutcome.Indeterminate ? 1 : 0, executor.Result!.NeedsCheck.Count);
        Assert.Equal(outcome == MarketMutationOutcome.Indeterminate ? "Stopped" : "Completed", executor.Result.State);
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
    [InlineData(false, MarketMutationOutcome.Indeterminate)]
    [InlineData(true, MarketMutationOutcome.Committed)]
    public async Task FailedReprice_IsClassifiedByLiveRescan_NotResent(bool applied, MarketMutationOutcome expected)
    {
        var plan = Plan(); var fake = new Driver { Outcome = MarketMutationOutcome.Indeterminate, ApplyReprice = applied };
        fake.Live[10] = [new(0, 1, 2, false, 10)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(expected, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Equal(!applied, executor.Result.NeedsCheck.Contains(10));
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
            store.Clear(); Assert.Null(store.Load());
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

    [Fact]
    public async Task PreviousOwnerResult_RemainsBlockedUntilVisibleAcknowledgement()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        var previous = plan.Owner with { LocalContentId = 77, CharacterName = "Other Character", HomeWorldName = "World" };
        var record = new MarketRunRecord { Owner = previous, State = "Interrupted" };
        executor.Restore(record);
        Assert.False(previous.Matches(plan.Owner));
        Assert.Equal("Market run: Other Character @ World", RQ.UI.MarketRunPane.ResultOwnerLabel(previous));
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Same(record, executor.Result);
        executor.Clear();
        Assert.True(executor.CanStart);
    }

    [Fact]
    public async Task UnloadBeforeMutation_NoDispatchAndNoCleanupOnDisposedDriver()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        fake.OnPlayer = () => executor.CancelAndWait(TimeSpan.Zero);
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(fake.Posts);
        Assert.Equal(0, fake.Closes);
        Assert.Equal(0, fake.Returns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorityLostWhilePostPending_NoNewReadsOrCleanupAfterSettlement(bool unload)
    {
        var plan = Plan(); var current = plan; var fake = new Driver();
        var pending = new TaskCompletionSource<MarketMutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.PendingPost = pending.Task;
        var executor = new MarketRunExecutor(fake, new(), () => current, _ => { });
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

    [Fact]
    public async Task OwnerChangesDuringFinalScan_NoReturnOrVerifiedSummary()
    {
        var plan = Plan(); var current = plan; var fake = new Driver();
        fake.OnPost = () => fake.OnListings = () => current = new ListingPlan { Owner = plan.Owner with { LocalContentId = 999 } };
        var executor = new MarketRunExecutor(fake, new(), () => current, _ => { });
        await executor.RunAsync(plan, Evaluation(plan));
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
        var executor = new MarketRunExecutor(fake, new(), () => current, _ => { });
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
        var executor = new MarketRunExecutor(fake, new(), () => current, _ => { });
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(MarketMutationOutcome.Indeterminate, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Contains(10ul, executor.Result.NeedsCheck);
        Assert.Equal(0, fake.Returns); Assert.Equal(0, fake.Closes);
    }

    [Fact]
    public async Task UnloadAtIntentPersistence_StopsBeforeSend()
    {
        var plan = Plan(); var fake = new Driver();
        MarketRunExecutor? executor = null;
        executor = new(fake, new(), () => plan, r =>
        {
            if (r.Mutations.Count > 0) executor!.CancelAndWait(TimeSpan.Zero);
        });
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(fake.Posts);
        Assert.Equal(MarketMutationOutcome.FailedBeforeSend, Assert.Single(executor.Result!.Mutations).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseRunsUnderLeaseAndSuppression_WithoutChangingOutcome(bool fails)
    {
        var plan = Plan(); var fake = new Driver { FailClose = fails }; var lease = new AutomationLease(); var ipc = new AutoRetainer();
        fake.OnClose = () => { Assert.True(lease.IsHeld); Assert.True(ipc.IsSuppressed); };
        var executor = new MarketRunExecutor(fake, lease, () => plan, _ => { }, new(ipc));
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(1, fake.Closes);
        Assert.Equal("Completed", executor.Result!.State);
        Assert.Equal(string.Empty, executor.Result.Reason);
        Assert.Equal(fails, executor.Result.Results.Any(s => s.Contains("Could not close retainer list")));
        Assert.False(lease.IsHeld); Assert.False(ipc.IsSuppressed);
    }

    [Fact]
    public async Task UnsettledClose_KeepsLeaseUntilCompletion()
    {
        var plan = Plan(); var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Driver { PendingClose = pending.Task }; var lease = new AutomationLease();
        var executor = new MarketRunExecutor(fake, lease, () => plan, _ => { }, deadlines: new MarketRunDeadline(TimeSpan.FromMilliseconds(5)));
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.True(lease.IsHeld);
        Assert.Equal("Completed", executor.Result!.State);
        pending.SetResult();
        for (var n = 0; n < 100 && lease.IsHeld; n++) await Task.Delay(5);
        Assert.False(lease.IsHeld);
    }

    [Fact]
    public async Task CancelDuringGather_LeavesShortageUnknownAndClosesList()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        fake.OnGather = executor.Cancel;
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.False(executor.Result!.ListingSummaryComplete);
        Assert.Equal(1, fake.Closes);
    }

    [Fact]
    public async Task FinalScanAcrossAllRetainers_MakesShortageKnown()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.True(executor.Result!.ListingSummaryComplete);
        Assert.Equal(0, executor.Result.StillShort);
    }

    [Fact]
    public async Task RetainerListUsesRouteDeadline_NotMutationDeadline()
    {
        var plan = Plan(); var fake = new Driver(); var routeDeadline = new CountingDeadline();
        var executor = new MarketRunExecutor(fake, new(), () => plan, _ => { }, routeDeadlines: routeDeadline);
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(1, routeDeadline.Calls);
        Assert.True(MarketRunDeadline.RetainerListTimeout > TimeSpan.FromMinutes(2));
    }

    private sealed class CountingDeadline : IMarketRunDeadline
    {
        public int Calls;
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action) { Calls++; return action(CancellationToken.None); }
    }

    [Fact]
    public void RestoredUnresolvedReceipt_BlocksAllSharedAutomationUntilAcknowledged()
    {
        var record = new MarketRunRecord { State = "Interrupted" };
        MarketRunExecutor? executor = null;
        var automation = new AutomationLease(() => executor?.Result?.RequiresAcknowledgement == true ? "Unresolved market run" : null);
        executor = new(new Driver(), automation, () => Plan(), _ => { });
        executor.Restore(record);
        foreach (var holder in new[] { "transfer", "automatic retrieval", "refresh", "listing navigation" })
        {
            Assert.False(automation.TryAcquire(holder, out var lease));
            Assert.Null(lease);
        }
        Assert.NotNull(automation.RefusalReason);
        // A durable receipt is an interlock, not an active lease: Clear must stay reachable.
        Assert.False(automation.IsHeld);
        executor.Clear();
        Assert.Null(automation.RefusalReason);
        Assert.True(automation.TryAcquire("transfer", out var accepted));
        accepted!.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeveralPulls_ReSelectLiveSlots_ProtectPlannedListings(bool compact)
    {
        var plan = Plan(4); var fake = new Driver { Compact = compact };
        fake.Live[10] = [new(0, 1, 2, false, 50), .. Enumerable.Range(1, 19).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, (uint)i))];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        var pulls = executor.Result!.Mutations.Where(m => m.Intent.Kind == "Pull").ToArray();
        Assert.Equal(3, pulls.Length);
        Assert.All(pulls, p => { Assert.Equal(2u, p.Intent.Listing!.ItemId); Assert.Equal("Bow", p.Intent.ItemName); });
        Assert.Equal(4, fake.Live[10].Count(l => l.ItemId == 1));
    }

    [Fact]
    public async Task FullRetainer_SplitStacks_NeverPulls()
    {
        var plan = Plan(); var fake = new Driver { Stock = [new(InventoryType.Inventory1, 0, 1, 1), new(InventoryType.Inventory1, 1, 1, 1)] };
        fake.Live[10] = Enumerable.Range(0, 20).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, 10)).ToList();
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(executor.Result!.Mutations); Assert.Equal(1, executor.Result.SplitStacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullIndeterminate_StopsRun_NoNextRetainer(bool exception)
    {
        var plan = Plan(); plan.Assignments.Add(new() { ItemId = 1, ItemName = "Hull", RetainerId = 11, RetainerName = "Beta", QuantityPerListing = 2, UnitPrice = 50 });
        var fake = new Driver { Outcome = MarketMutationOutcome.Indeterminate, ThrowPull = exception };
        fake.Live[10] = Enumerable.Range(0, 20).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, 10)).ToList();
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal("Stopped", executor.Result!.State); Assert.Equal([10ul], fake.Visited);
        Assert.Single(executor.Result.Mutations); Assert.Contains(10ul, executor.Result.NeedsCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepriceBeforeSendOrIndeterminateException(bool exception)
    {
        var plan = Plan(); var fake = new Driver { ApplyReprice = false, ThrowReprice = exception, Outcome = MarketMutationOutcome.FailedBeforeSend };
        fake.Live[10] = [new(0, 1, 2, false, 10)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal(exception ? MarketMutationOutcome.Indeterminate : MarketMutationOutcome.Unchanged, Assert.Single(executor.Result!.Mutations).Outcome);
        Assert.Equal(exception, executor.Result.NeedsCheck.Contains(10));
    }

    [Fact]
    public async Task InvalidPrice_WholeScanFails_NextRetainerStillProcessed()
    {
        var plan = Plan(); plan.Assignments.Add(new() { ItemId = 1, ItemName = "Hull", RetainerId = 11, RetainerName = "Beta", QuantityPerListing = 2, UnitPrice = 50 });
        var fake = new Driver(); fake.Live[10] = [new(0, 1, 2, false, null)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal([11ul], fake.Posts); Assert.Contains(executor.Result!.Results, s => s.Contains("Invalid market price"));
    }

    [Fact]
    public async Task CancelGather_NoMutation_AndCancelRetainerReturns()
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        fake.OnGather = executor.Cancel;
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal("Cancelled", executor.Result!.State); Assert.Empty(executor.Result.Mutations);
        fake.OnGather = null; fake.OnPost = executor.Cancel;
        await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal("Cancelled", executor.Result!.State); Assert.Equal(1, fake.Returns);
    }

    [Theory]
    [InlineData("Interrupted")]
    [InlineData("NeedsCheck")]
    [InlineData("Unknown")]
    public async Task StartRequiresAcknowledgement(string kind)
    {
        var plan = Plan(); var fake = new Driver(); var executor = Create(plan, fake);
        var record = new MarketRunRecord { State = kind == "Interrupted" ? kind : "Stopped" };
        if (kind == "NeedsCheck") record.NeedsCheck.Add(10);
        if (kind == "Unknown") record.Mutations.Add(new() { Intent = new("Post", 10, "Hull", null, null, 2, 50) });
        executor.Restore(record); Assert.False(executor.CanStart);
        await executor.RunAsync(plan, Evaluation(plan)); Assert.Same(record, executor.Result);
        executor.Clear(); Assert.True(executor.CanStart);
        await executor.RunAsync(plan, Evaluation(plan)); Assert.Single(fake.Posts);
    }

    [Fact]
    public async Task PersistenceFailure_PreventsDispatch_AndReleasesLease()
    {
        var plan = Plan(); var fake = new Driver(); var automation = new AutomationLease();
        var executor = new MarketRunExecutor(fake, automation, () => plan, r => { if (r.Mutations.Count > 0) throw new IOException("disk"); });
        await Assert.ThrowsAsync<IOException>(() => executor.RunAsync(plan, Evaluation(plan)));
        Assert.Empty(fake.Posts); Assert.False(automation.IsHeld); Assert.False(executor.IsRunning);
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
        var executor = new MarketRunExecutor(fake, automation, () => plan, r => { if (cleanupThrows && r.State != "Running") throw new IOException("cleanup"); }, new AutoRetainerSuppression(ipc), deadlines: new MarketRunDeadline(TimeSpan.FromMilliseconds(5)));
        if (cleanupThrows) await Assert.ThrowsAsync<IOException>(() => executor.RunAsync(plan, Evaluation(plan)));
        else await executor.RunAsync(plan, Evaluation(plan));
        Assert.True(automation.IsHeld); Assert.True(ipc.IsSuppressed); Assert.Equal("Stopped", executor.Result!.State);
        pending.SetResult(MarketMutationOutcome.Committed);
        for (var n = 0; n < 100 && automation.IsHeld; n++) await Task.Delay(5);
        Assert.False(automation.IsHeld);
        Assert.False(ipc.IsSuppressed);
    }

    [Fact]
    public async Task AutoRetainerBusy_WaitsBeforeAnyGatherOrGameCall()
    {
        var plan = Plan(); var fake = new Driver(); var ipc = new AutoRetainer { IsBusy = true };
        var gathered = false; fake.OnGather = () => { gathered = true; Assert.True(ipc.IsSuppressed); };
        var executor = new MarketRunExecutor(fake, new(), () => plan, _ => { }, new AutoRetainerSuppression(ipc));
        var run = executor.RunAsync(plan, Evaluation(plan));
        Assert.False(gathered); Assert.Empty(fake.Visited);
        ipc.IsBusy = false; await run;
        Assert.True(gathered); Assert.False(ipc.IsSuppressed);
    }

    [Theory]
    [InlineData("InvalidMarketUnitPrice", MarketMutationOutcome.FailedBeforeSend)]
    [InlineData("RetainerMarketPriceUpdateIndeterminate", MarketMutationOutcome.Indeterminate)]
    [InlineData("UnexpectedRetainerMarketConfirmation", MarketMutationOutcome.Indeterminate)]
    [InlineData("UnknownFutureCode", MarketMutationOutcome.Indeterminate)]
    public void PrimitiveRepriceCodes(string code, MarketMutationOutcome expected) =>
        Assert.Equal(expected, MarketRunLiveDriver.ClassifyReprice(RetainerAutomationResult.Failed(code, code)));

    [Fact]
    public async Task QualityIsNeverMixed()
    {
        var plan = Plan(); var fake = new Driver { Stock = [new(InventoryType.Inventory1, 0, 1, 10, true)] };
        fake.Live[10] = [new(0, 1, 2, true, 10)];
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Empty(executor.Result!.Mutations); Assert.Equal(10u, fake.Live[10][0].UnitPrice);
    }

    [Theory]
    [InlineData("Reprice")]
    [InlineData("Pull")]
    [InlineData("Gather")]
    public async Task PlanRevisionChangeAtEveryBoundary_PreventsNextMutation(string boundary)
    {
        var plan = Plan(2); var fake = new Driver();
        if (boundary == "Reprice") { fake.Live[10] = [new(0, 1, 2, false, 10)]; fake.OnReprice = () => plan.Revision++; }
        if (boundary == "Pull")
        {
            fake.Live[10] = Enumerable.Range(0, 20).Select(i => new RetainerMarketListingTarget(i, 2, 1, false, 10)).ToList();
            fake.OnPull = () => plan.Revision++;
        }
        if (boundary == "Gather") fake.OnGather = () => plan.Revision++;
        var executor = Create(plan, fake); await executor.RunAsync(plan, Evaluation(plan));
        Assert.Equal("Stopped", executor.Result!.State);
        Assert.Equal(boundary == "Gather" ? 0 : 1, executor.Result.Mutations.Count);
        Assert.Empty(fake.Posts);
    }

    [Fact]
    public async Task DisposedExecutor_CannotOverwriteNewRunRecord()
    {
        var plan = Plan(); var fake = new Driver(); var writes = 0;
        var pending = new TaskCompletionSource<MarketMutationOutcome>(); fake.PendingPost = pending.Task;
        var executor = new MarketRunExecutor(fake, new(), () => plan, _ => writes++);
        var run = executor.RunAsync(plan, Evaluation(plan));
        executor.CancelAndWait(TimeSpan.Zero);
        var atDispose = writes;
        pending.SetResult(MarketMutationOutcome.Committed);
        await run;
        Assert.Equal(atDispose, writes); Assert.False(executor.CanStart);
    }

    [Fact]
    public async Task BorrowedLease_RefusesForeignAndDisposedLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-market-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journal = new OperationJournal(new StateRepository(new QuartermasterStateStore(Path.Combine(directory, "state.json"))));
            using var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory, "cache.json")));
            var automation = new AutomationLease(); var foreign = new AutomationLease();
            var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
            ((SessionProxy)(object)session).Handler = (_, _) => throw new Exception("Must not call the game");
            var coordinator = new TransferCoordinator(journal, new RetainerLiveDriver(session), cache, () => Plan().Owner, () => new Dictionary<uint, int>(), automation);
            Assert.True(foreign.TryAcquire("foreign", out var other));
            using (other) await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteRetrievalUnderLeaseAsync("unused", other!, CancellationToken.None));
            Assert.True(automation.TryAcquire("disposed", out var disposed)); disposed!.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteRetrievalUnderLeaseAsync("unused", disposed, CancellationToken.None));
            Assert.False(automation.IsHeld);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("full")]
    [InlineData("partial")]
    [InlineData("cancel")]
    [InlineData("lost")]
    [InlineData("changed")]
    [InlineData("deadline")]
    [InlineData("unload")]
    [InlineData("owner")]
    [InlineData("scanowner")]
    [InlineData("scanunload")]
    public async Task Gather_UsesJournalAndBorrowedLease_RetrievesOnlyShortfall(string mode)
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
            if (mode == "full") player.AddRange(Enumerable.Range(1, 139).Select(i => new DalamudInventoryStack((InventoryType)(i / 35), i % 35, 2, 1)));
            var source = new DalamudInventoryStack(InventoryType.RetainerPage1, 0, 1, 20);
            var split = mode is "partial" or "cancel" or "lost";
            var sources = split ? new List<DalamudInventoryStack> { source with { Quantity = 4 }, source with { SlotIndex = 1, Quantity = 16 } } : [source];
            var transferred = 0;
            var cancel = false;
            var authority = true;
            var currentOwner = plan.Owner;
            var callsAfterAuthorityLoss = 0;
            var closes = 0;
            var sourceReads = 0;
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
                        if (mode == "changed" && ++sourceReads == 2) sources[0] = sources[0] with { Quantity = 19 };
                        return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(sources.Where(s => s.Quantity > 0).ToArray());
                    case nameof(IRetainerAutomationSession.RetrieveAsync):
                        var quantity = (int)args![1]!;
                        transferred += quantity;
                        source = source with { Quantity = source.Quantity - quantity };
                        var selected = (DalamudInventoryStack)args[0]!;
                        var index = sources.FindIndex(s => s.SlotIndex == selected.SlotIndex);
                        sources[index] = sources[index] with { Quantity = sources[index].Quantity - quantity };
                        player[0] = player[0] with { Quantity = player[0].Quantity + quantity };
                        if (mode == "partial") player.AddRange(Enumerable.Range(1, 139).Select(i => new DalamudInventoryStack((InventoryType)(i / 35), i % 35, 2, 1)));
                        if (mode == "cancel") cancel = true;
                        if (mode == "lost") lease!.Dispose();
                        if (mode == "unload") authority = false;
                        if (mode == "owner") currentOwner = plan.Owner with { LocalContentId = 999 };
                        return Task.FromResult(new RetainerRetrievalResult(true, quantity, "Retrieved", "Verified"));
                    default:
                        if (mode == "deadline") return Task.FromCanceled<RetainerAutomationResult>(new CancellationToken(true));
                        return Task.FromResult(RetainerAutomationResult.Succeeded("Ready", "Ready"));
                }
            };
            var adapter = new MarketRunLiveDriver(session, new RetainerLiveDriver(session), journal, cache, playerCache, automation,
                () => currentOwner, _ => "Hull");
            if (mode == "partial")
            {
                var fake = new Driver { Stock = player };
                fake.GatherOverride = async (_, borrowed, check, hasAuthority, token) =>
                {
                    var reports = await adapter.GatherAsync([new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], borrowed, check, hasAuthority, token);
                    fake.GatherInventoryBlockedUnits = adapter.GatherInventoryBlockedUnits;
                    return reports;
                };
                var executor = new MarketRunExecutor(fake, automation, () => plan, _ => { });
                await executor.RunAsync(plan, Evaluation(plan));
                Assert.Equal("Completed", executor.Result!.State);
                Assert.Single(fake.Posts); Assert.Equal(4, transferred);
                Assert.Equal(3, executor.Result.GatherBlockedUnits);
                Assert.Equal(0, executor.Result.NoInventorySpace);
                Assert.Equal(OperationStatuses.PartiallySucceeded, Assert.Single(journal.History(plan.Owner, 10)).Status);
                Assert.False(automation.IsHeld);
                return;
            }
            Assert.True(automation.TryAcquire("market test", out lease));
            using (lease)
            {
                IReadOnlyList<string> reports = [];
                if (mode is "scanowner" or "scanunload")
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GatherAsync(
                        [new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { }, () => authority, CancellationToken.None));
                    Assert.Equal(0, callsAfterAuthorityLoss);
                    Assert.Equal(0, closes);
                    Assert.Equal(20u, cache.Snapshot()[10].Bags.SelectMany(b => b.Items).Single(i => i.ItemId == 1).Quantity);
                    return;
                }
                if (mode is "unload" or "owner" or "lost")
                {
                    await Assert.ThrowsAsync<MarketGatherIndeterminateException>(() => adapter.GatherAsync(
                        [new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { }, () => authority, CancellationToken.None));
                    Assert.Equal(0, callsAfterAuthorityLoss);
                    Assert.Equal(0, closes);
                    Assert.Equal(OperationStatuses.Indeterminate, Assert.Single(journal.History(plan.Owner, 10)).Status);
                    return;
                }
                Task Gather() => adapter.GatherAsync([new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { if (cancel) throw new OperationCanceledException(); }, () => true, CancellationToken.None);
                if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Gather);
                else reports = await adapter.GatherAsync([new(1, "Hull", ItemQualityPolicy.NqOnly, 10)], lease!, () => { }, () => true, CancellationToken.None);
                Assert.Equal(mode != "lost", automation.IsHeld);
                Assert.Equal(mode is "full" or "changed" or "deadline" ? 0 : split ? 4 : 7, transferred);
                var operation = Assert.Single(journal.History(plan.Owner, 10));
                Assert.Equal(mode is "cancel" or "deadline" ? OperationStatuses.Cancelled : mode is "full" or "lost" or "changed" ? OperationStatuses.Failed : mode == "partial" ? OperationStatuses.PartiallySucceeded : OperationStatuses.Succeeded, operation.Status);
                Assert.Equal(transferred, Assert.Single(operation.Lines).TransferredQuantity);
                if (mode is "full" or "partial") Assert.Contains(reports, r => r.Contains("inventory space"));
                if (mode == "normal") Assert.Equal(13u, cache.Snapshot()[10].Bags.SelectMany(b => b.Items).Single(i => i.ItemId == 1).Quantity);
                if (mode == "cancel") Assert.True(closes > 0);
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
    private sealed class AutoRetainer : IAutoRetainerIpc
    {
        public bool IsAvailable => true;
        public bool IsBusy { get; set; }
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
        public int GatherInventoryBlockedUnits { get; set; }
        public Func<IReadOnlyList<MarketGatherTarget>, IDisposable, Action, Func<bool>, CancellationToken, Task<IReadOnlyList<string>>>? GatherOverride;
        public Dictionary<ulong, List<RetainerMarketListingTarget>> Live = new() { [10] = [], [11] = [] };
        public List<DalamudInventoryStack> Stock = [new(InventoryType.Inventory1, 0, 1, 10)];
        public List<ulong> Posts = [];
        public List<ulong> Visited = [];
        public int Returns;
        public int ListingScans;
        public Action? OnListings;
        public int Closes;
        public Action? OnClose;
        public Task? PendingClose;
        public bool FailClose;
        public bool Compact, ThrowPull, ThrowReprice;
        public Task<MarketMutationOutcome>? PendingPost;
        public Task<MarketMutationOutcome>? PendingReprice;
        public int FreeSlots = 100;
        public bool ThrowBeforeSend, ThrowAfterSend;
        public bool ApplyReprice = true, FailReturn;
        public Action? OnPost, OnGather, OnPlayer, OnPull, OnReprice;
        public MarketMutationOutcome Outcome = MarketMutationOutcome.Committed;
        private ulong active;
        public string ItemName(uint id) => id == 1 ? "Hull" : "Bow";
        public Task RequireRetainerListAsync(CancellationToken t) => Task.CompletedTask;
        public Task OpenAsync(ulong id, string name, CancellationToken t) { active = id; Visited.Add(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken t)
        {
            ListingScans++;
            OnListings?.Invoke();
            return Live[active].Any(l => l.UnitPrice is null or 0 or > 999999999)
            ? Task.FromException<IReadOnlyList<RetainerMarketListingTarget>>(new InvalidOperationException("Invalid market price"))
            : Task.FromResult<IReadOnlyList<RetainerMarketListingTarget>>(Live[active].ToArray());
        }
        public Task<MarketPlayerInventory> PlayerAsync(CancellationToken t) { OnPlayer?.Invoke(); return Task.FromResult(new MarketPlayerInventory(Stock.ToArray(), FreeSlots)); }
        public Task<MarketMutationOutcome> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken t)
        { OnReprice?.Invoke(); if (PendingReprice is not null) return PendingReprice;
            if (ThrowReprice) throw new RetainerMarketMutationIndeterminateException("Unknown", "After send", null, new OperationCanceledException(), t);
            if (ApplyReprice) Live[active][Live[active].IndexOf(listing)] = listing with { UnitPrice = price }; return Task.FromResult(Outcome); }
        public Task<MarketMutationOutcome> PullAsync(RetainerMarketListingTarget listing, CancellationToken t)
        {
            OnPull?.Invoke();
            if (ThrowPull) throw new RetainerMarketMutationIndeterminateException("Unknown", "After send", null, new OperationCanceledException(), t);
            if (Outcome == MarketMutationOutcome.Committed)
            {
                Live[active].Remove(listing);
                if (Compact) Live[active] = Live[active].Select((l, i) => l with { SlotIndex = i }).ToList();
            }
            return Task.FromResult(Outcome);
        }
        public Task<MarketMutationOutcome> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken t)
        {
            Posts.Add(active); OnPost?.Invoke();
            if (PendingPost is not null) return PendingPost;
            if (ThrowBeforeSend) throw new OperationCanceledException();
            if (ThrowAfterSend) throw new RetainerMarketMutationIndeterminateException("Deadline", "After send", null, new OperationCanceledException(), t);
            if (Outcome == MarketMutationOutcome.Committed)
            {
                Stock[Stock.IndexOf(source)] = source with { Quantity = source.Quantity - quantity };
                Live[active].Add(new(Enumerable.Range(0, 20).First(i => Live[active].All(l => l.SlotIndex != i)), source.ItemId, quantity, source.IsHighQuality, price));
            }
            return Task.FromResult(Outcome);
        }
        public Task ReturnAsync(CancellationToken t) { Returns++; return FailReturn ? Task.FromException(new InvalidOperationException("List unreachable")) : Task.CompletedTask; }
        public Task CloseListAsync(CancellationToken t)
        {
            Closes++; OnClose?.Invoke();
            return FailClose ? Task.FromException(new InvalidOperationException("Close failed")) : PendingClose ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken t)
        { OnGather?.Invoke(); check(); return GatherOverride?.Invoke(targets, lease, check, hasAuthority, t) ?? Task.FromResult<IReadOnlyList<string>>([]); }
    }
}
