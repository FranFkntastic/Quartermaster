using RQ.Domain;
using RQ.Operations;
using RQ.Planning;

namespace RQ.Tests;

public sealed class RestockPlanTests
{
    [Fact]
    public void Catalog_IsOwnerScopedAndNamesAreUniqueCaseInsensitively()
    {
        var state = new QuartermasterState();
        var first = RestockPlanCatalog.Create(state, TestData.Owner, "Workshop");
        var second = RestockPlanCatalog.Create(state, TestData.Owner, "workshop");
        var otherOwner = TestData.Owner with { LocalContentId = 99, CharacterName = "Other" };
        RestockPlanCatalog.Create(state, otherOwner, "Workshop");

        Assert.Equal("Workshop", first.Name);
        Assert.Equal("workshop 2", second.Name);
        Assert.Equal(2, RestockPlanCatalog.OwnerPlans(state, TestData.Owner).Count);
    }

    [Fact]
    public void Duplicate_CopiesIntentWithNewStableIdentities()
    {
        var state = new QuartermasterState();
        var source = RestockPlanCatalog.Create(
            state,
            TestData.Owner,
            "Submersibles",
            [new RestockPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 40, Notes = "Hull" }]);

        var copy = RestockPlanCatalog.Duplicate(state, TestData.Owner, source.Id);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Submersibles copy", copy.Name);
        Assert.Equal(source.Items[0].ItemId, copy.Items[0].ItemId);
        Assert.Equal(source.Items[0].TargetQuantity, copy.Items[0].TargetQuantity);
        Assert.NotEqual(source.Items[0].Id, copy.Items[0].Id);
    }

    [Fact]
    public void CreateFromStowage_IsAnExplicitIndependentCopy()
    {
        var stowage = new StowagePlan { Owner = TestData.Owner, Name = "General" };
        var rule = new TargetPlanItem
        {
            StowagePlanId = stowage.Id,
            ItemId = 100,
            ItemName = "Ore",
            TargetQuantity = 12,
        };
        var state = new QuartermasterState
        {
            StowagePlans = [stowage],
            PlanItems = [rule],
        };

        var restock = RestockPlanCatalog.CreateFromStowage(state, TestData.Owner);
        rule.TargetQuantity = 99;

        Assert.Equal(12, Assert.Single(restock.Items).TargetQuantity);
        Assert.NotEqual(rule.Id, restock.Items[0].Id);
    }

    [Fact]
    public void Repository_RoundTripsPlansAndLines()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        Guid planId = default;
        Guid lineId = default;
        repository.Mutate(state =>
        {
            var plan = RestockPlanCatalog.Create(
                state,
                TestData.Owner,
                "Daily",
                [new RestockPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 20 }]);
            planId = plan.Id;
            lineId = plan.Items[0].Id;
        });

        var reloaded = TestData.Repository(directory.Path).Snapshot();
        var plan = Assert.Single(reloaded.RestockPlans);

        Assert.Equal("gooseworks-quartermaster-state/v3", reloaded.Schema);
        Assert.Equal(planId, plan.Id);
        Assert.Equal(lineId, Assert.Single(plan.Items).Id);
    }

    [Fact]
    public void RestockOperation_SnapshotsPlanIdentityAndNeverConsumesDefinition()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => RestockPlanCatalog.Create(
            state,
            TestData.Owner,
            "Workshop",
            [new RestockPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 20 }]));
        var source = Assert.Single(repository.Snapshot().RestockPlans);
        var journal = new OperationJournal(repository);

        var operation = journal.CreateRestock(TestData.Owner, source);
        repository.Mutate(state =>
        {
            var plan = Assert.Single(state.RestockPlans);
            plan.Items[0].TargetQuantity = 50;
            plan.Revision++;
        });

        Assert.Equal(source.Id, operation.SourcePlanId);
        Assert.Equal(source.Revision, operation.SourcePlanRevision);
        Assert.Equal(source.Name, operation.SourcePlanName);
        Assert.Equal(20, Assert.Single(operation.Lines).TargetQuantity);
        Assert.Equal(50, Assert.Single(repository.Snapshot().RestockPlans).Items[0].TargetQuantity);
    }

    [Fact]
    public void Evaluation_RecomputesNeedFromCurrentPlayerAndRetainerState()
    {
        var plan = new RestockPlan
        {
            Owner = TestData.Owner,
            Items = [new RestockPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 20 }],
        };
        var cache = new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Ore", 12)),
        };

        var first = RestockPlanner.Build(
            RestockPlanCatalog.ToExecutionRows(plan),
            new Dictionary<uint, int> { [100] = 5 },
            cache,
            TestData.Owner,
            DateTime.UtcNow);
        var second = RestockPlanner.Build(
            RestockPlanCatalog.ToExecutionRows(plan),
            new Dictionary<uint, int> { [100] = 18 },
            cache,
            TestData.Owner,
            DateTime.UtcNow);

        Assert.Equal(15, first.NeededQuantity);
        Assert.Equal(2, second.NeededQuantity);
    }
}
