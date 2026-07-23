using Franthropy.FFXIV.Filtering;
using RQ.Domain;
using RQ.Operations;
using RQ.Planning;

namespace RQ.Tests;

public sealed class StowageTests
{
    [Fact]
    public void Migration_PreservesRuleIdentityAndIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var ruleId = Guid.NewGuid();
        repository.Mutate(state => state.PlanItems.Add(new TargetPlanItem
        {
            Id = ruleId,
            ItemId = 100,
            ItemName = "Darksteel Ore",
            TargetQuantity = 12,
            Notes = "Keep",
            Enabled = false,
        }));

        Assert.True(StowagePlanMigration.EnsureOwnerPlan(repository, TestData.Owner, () => new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(StowagePlanMigration.EnsureOwnerPlan(repository, TestData.Owner));

        var state = repository.Snapshot();
        var plan = Assert.Single(state.StowagePlans);
        var rule = Assert.Single(state.PlanItems);
        Assert.Equal(ruleId, rule.Id);
        Assert.Equal(plan.Id, rule.StowagePlanId);
        Assert.Equal(12, rule.TargetQuantity);
        Assert.Equal("Keep", rule.Notes);
        Assert.False(rule.Enabled);
        Assert.Single(state.StowageMigrations);
    }

    [Theory]
    [InlineData(4, 10, 6, 0, StowageAction.Retrieve)]
    [InlineData(10, 10, 0, 0, StowageAction.None)]
    [InlineData(14, 10, 0, 4, StowageAction.Deposit)]
    public void Evaluation_UsesOneTargetForRetrievalAndSurplus(
        int carried,
        int target,
        int retrieve,
        int deposit,
        StowageAction action)
    {
        var plan = new StowagePlan { Owner = TestData.Owner, Name = "General" };
        var state = new QuartermasterState
        {
            StowagePlans = [plan],
            PlanItems =
            [
                new TargetPlanItem
                {
                    StowagePlanId = plan.Id,
                    ItemId = 100,
                    ItemName = "Ore",
                    TargetQuantity = target,
                },
            ],
        };
        var browser = Browser(carried, false);

        var line = Assert.Single(Assert.Single(StowageEvaluator.Build(state, browser, TestData.Owner)).Lines);

        Assert.Equal(retrieve, line.RetrieveQuantity);
        Assert.Equal(deposit, line.DepositQuantity);
        Assert.Equal(action, line.Action);
    }

    [Fact]
    public void Evaluation_QualityPolicyDoesNotConflateNqAndHq()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var state = new QuartermasterState
        {
            StowagePlans = [plan],
            PlanItems =
            [
                new TargetPlanItem
                {
                    StowagePlanId = plan.Id,
                    ItemId = 100,
                    ItemName = "Ore",
                    TargetQuantity = 5,
                    Quality = ItemQualityPolicy.HqOnly,
                },
            ],
        };
        var browser = Browser(99, false, 2, true);

        var line = Assert.Single(Assert.Single(StowageEvaluator.Build(state, browser, TestData.Owner)).Lines);

        Assert.Equal(2, line.PlayerQuantity);
        Assert.Equal(3, line.RetrieveQuantity);
    }

    [Fact]
    public void Router_ConsolidatesPartialStackBeforePreferredEmptySlots()
    {
        var home = Retainer(10, "Home");
        var partial = Retainer(20, "Partial", new CachedItem
        {
            ItemId = 100,
            ItemName = "Ore",
            Quantity = 95,
        });
        var request = new StowageDepositRequest(
            null,
            null,
            100,
            "Ore",
            false,
            4,
            new StowageRoutingPolicy
            {
                Mode = StowageRoutingMode.ConsolidateFirst,
                PreferredRetainerIds = [home.RetainerId],
            });

        var route = StowageRouter.Route(
            request,
            new Dictionary<ulong, CachedRetainer>
            {
                [home.RetainerId] = home,
                [partial.RetainerId] = partial,
            },
            TestData.Owner,
            99);

        Assert.Equal(partial.RetainerId, Assert.Single(route.Allocations).RetainerId);
    }

    [Fact]
    public void Router_HomeFirstHonorsPreferredOrderAndHoldOverflow()
    {
        var first = Retainer(10, "Zulu");
        var second = Retainer(20, "Alpha");
        var request = new StowageDepositRequest(
            null,
            null,
            100,
            "Ore",
            false,
            4,
            new StowageRoutingPolicy
            {
                Mode = StowageRoutingMode.HomeFirst,
                PreferredRetainerIds = [first.RetainerId, second.RetainerId],
                Overflow = StowageOverflowPolicy.HoldOnPlayer,
            });

        var route = StowageRouter.Route(
            request,
            new Dictionary<ulong, CachedRetainer>
            {
                [first.RetainerId] = first,
                [second.RetainerId] = second,
            },
            TestData.Owner,
            99);

        Assert.Equal(first.RetainerId, Assert.Single(route.Allocations).RetainerId);
        Assert.Equal(0, route.RemainingQuantity);
    }

    [Fact]
    public void Router_LeavesUnroutableRemainderOnPlayer()
    {
        var request = new StowageDepositRequest(
            null,
            null,
            100,
            "Ore",
            false,
            7,
            new StowageRoutingPolicy { Overflow = StowageOverflowPolicy.HoldOnPlayer });

        var route = StowageRouter.Route(
            request,
            new Dictionary<ulong, CachedRetainer> { [10] = Retainer(10, "Storage") },
            TestData.Owner,
            99);

        Assert.Empty(route.Allocations);
        Assert.Equal(7, route.RemainingQuantity);
    }

    [Fact]
    public void DepositBatch_PersistsExactVariantAndDestinationAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository, () => new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc));
        var request = new StowageDepositRequest(null, null, 100, "Ore", true, 4, new StowageRoutingPolicy());
        var batch = new StowageDepositBatch(
            DateTime.UtcNow,
            [
                new StowageRoute(
                    request,
                    [new StowageAllocation(10, "Eris", 4, 20, DateTime.UtcNow)],
                    4,
                    0),
            ]);

        var operation = journal.CreateDeposit(TestData.Owner, batch);

        var line = Assert.Single(operation.Lines);
        Assert.True(line.IsHighQuality);
        Assert.Equal(4, line.TargetQuantity);
        Assert.Equal(4, Assert.Single(operation.DepositCandidates).CapacityByVariant["100:hq"]);
        journal.Transition(operation.OperationId, OperationStatuses.Running, "started", "started");
        Assert.Throws<InvalidOperationException>(() =>
            journal.RecordDepositTransfer(operation.OperationId, 100, true, 10, 5, "verified", "verified"));
    }

    private static BrowserProjection Browser(
        int nq,
        bool nqHq,
        int second = 0,
        bool secondHq = false)
    {
        var stacks = new List<StockStack>();
        if (nq > 0)
            stacks.Add(new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Inventory1", 0, 100, "Ore", nq, nqHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, null));
        if (second > 0)
            stacks.Add(new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Inventory1", 1, 100, "Ore", second, secondHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, null));
        return new BrowserProjection
        {
            Owner = TestData.Owner,
            Scopes = [new(BrowserScope.AllKey, "All", BrowserScopeKind.All, null)],
            Items = [new StockGroup(100, "Ore", stacks)],
            Listings = [],
        };
    }

    private static CachedRetainer Retainer(ulong id, string name, params CachedItem[] items) => new()
    {
        RetainerId = id,
        RetainerName = name,
        Owner = TestData.Owner,
        ObservedAtUtc = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc),
        ObservedSources =
        [
            "RetainerPage1",
            "RetainerPage2",
            "RetainerPage3",
            "RetainerPage4",
            "RetainerPage5",
            "RetainerPage6",
            "RetainerPage7",
        ],
        Bags =
        [
            new CachedBag
            {
                BagName = "RetainerPage1",
                Items = items.ToList(),
            },
        ],
    };
}
