using RQ.Courier;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

public sealed class CourierPlanMigrationTests
{
    private static readonly OwnerScope Alt = new()
    {
        LocalContentId = 9002,
        HomeWorldId = 406,
        CharacterName = "Alt Character",
        HomeWorldName = "Maduin",
    };

    [Fact]
    public void MixedSavedPlan_SplitsCourierLinesIntoSiblingCourierPlan()
    {
        var state = new QuartermasterState();
        var plan = Plan("Weekly", ephemeral: false, enabled: true);
        state.StowagePlans.Add(plan);
        var ordinary = Line(plan.Id, 100, 12);
        var toAlt = Line(plan.Id, 200, 5, new CourierRoute(Alt, 77, null));
        var toAltBags = Line(plan.Id, 300, 3, new CourierRoute(Alt, null, 55), enabled: false);
        state.PlanItems.AddRange([ordinary, toAlt, toAltBags]);

        Assert.True(StowagePlanMigration.SeparateCourierLines(state));

        Assert.Equal(StowagePlanKind.Transfer, plan.Kind);
        Assert.True(plan.Enabled);
        var sibling = Assert.Single(state.StowagePlans, candidate => candidate.Kind == StowagePlanKind.Courier);
        Assert.Equal("Weekly (Courier)", sibling.Name);
        Assert.False(sibling.IsEphemeral);
        Assert.False(sibling.Enabled);
        Assert.True(sibling.Owner.Matches(TestData.Owner));

        // Lines move as they are: same ids, quantities, routes and paused state.
        Assert.Equal([ordinary.Id], state.PlanItems.Where(rule => rule.StowagePlanId == plan.Id).Select(rule => rule.Id));
        var moved = state.PlanItems.Where(rule => rule.StowagePlanId == sibling.Id).OrderBy(rule => rule.ItemId).ToArray();
        Assert.Equal([toAlt.Id, toAltBags.Id], moved.Select(rule => rule.Id));
        Assert.Equal([5, 3], moved.Select(rule => rule.TargetQuantity));
        Assert.Equal(new CourierRoute(Alt, 77, null), moved[0].Courier);
        Assert.False(moved[1].Enabled);
        Assert.All(state.StowagePlans, candidate =>
            Assert.All(state.PlanItems.Where(rule => rule.StowagePlanId == candidate.Id),
                rule => Assert.True(StowagePlanCatalog.Holds(candidate.Kind, rule))));
    }

    [Fact]
    public void CourierOnlySavedPlan_BecomesCourierPlanInPlace()
    {
        var state = new QuartermasterState();
        var plan = Plan("Alt supplies", ephemeral: false, enabled: true);
        state.StowagePlans.Add(plan);
        var line = Line(plan.Id, 200, 5, new CourierRoute(Alt, null, null));
        state.PlanItems.Add(line);
        var revision = plan.Revision;

        Assert.True(StowagePlanMigration.SeparateCourierLines(state));

        var only = Assert.Single(state.StowagePlans);
        Assert.Same(plan, only);
        Assert.Equal(StowagePlanKind.Courier, plan.Kind);
        Assert.Equal("Alt supplies", plan.Name);
        Assert.False(plan.Enabled);
        Assert.True(plan.Revision > revision);
        Assert.Equal(plan.Id, Assert.Single(state.PlanItems).StowagePlanId);
    }

    [Fact]
    public void EphemeralCourierLines_MoveToTheUnsavedCourierPlan()
    {
        var state = new QuartermasterState();
        var scratch = Plan(StowagePlanCatalog.ScratchName(StowagePlanKind.Transfer), ephemeral: true, enabled: false);
        state.StowagePlans.Add(scratch);
        var ordinary = Line(scratch.Id, 100, 4);
        var courierLine = Line(scratch.Id, 200, 6, new CourierRoute(Alt, 77, null));
        state.PlanItems.AddRange([ordinary, courierLine]);

        Assert.True(StowagePlanMigration.SeparateCourierLines(state));

        var courierScratch = StowagePlanCatalog.FindScratch(state, TestData.Owner, StowagePlanKind.Courier);
        Assert.NotNull(courierScratch);
        Assert.True(courierScratch.IsEphemeral);
        Assert.Same(scratch, StowagePlanCatalog.FindScratch(state, TestData.Owner));
        Assert.Equal(ordinary.Id, Assert.Single(state.PlanItems, rule => rule.StowagePlanId == scratch.Id).Id);
        var moved = Assert.Single(state.PlanItems, rule => rule.StowagePlanId == courierScratch.Id);
        Assert.Equal(courierLine.Id, moved.Id);
        Assert.Equal(6, moved.TargetQuantity);
    }

    [Fact]
    public void EphemeralCourierLines_MergeIntoAnExistingCourierScratchLine()
    {
        var state = new QuartermasterState();
        var scratch = Plan(StowagePlanCatalog.ScratchName(StowagePlanKind.Transfer), ephemeral: true, enabled: false);
        var courierScratch = Plan(StowagePlanCatalog.ScratchName(StowagePlanKind.Courier), ephemeral: true, enabled: false);
        courierScratch.Kind = StowagePlanKind.Courier;
        state.StowagePlans.AddRange([scratch, courierScratch]);
        var route = new CourierRoute(Alt, 77, null);
        var existing = Line(courierScratch.Id, 200, 2, route);
        state.PlanItems.AddRange([existing, Line(scratch.Id, 200, 6, route)]);

        Assert.True(StowagePlanMigration.SeparateCourierLines(state));

        var merged = Assert.Single(state.PlanItems);
        Assert.Equal(existing.Id, merged.Id);
        Assert.Equal(courierScratch.Id, merged.StowagePlanId);
        Assert.Equal(8, merged.TargetQuantity);
        Assert.Equal(2, state.StowagePlans.Count);
    }

    [Fact]
    public void SecondRun_ChangesNothing()
    {
        var state = new QuartermasterState();
        var saved = Plan("Weekly", ephemeral: false, enabled: true);
        var scratch = Plan(StowagePlanCatalog.ScratchName(StowagePlanKind.Transfer), ephemeral: true, enabled: false);
        state.StowagePlans.AddRange([saved, scratch]);
        state.PlanItems.AddRange([
            Line(saved.Id, 100, 1),
            Line(saved.Id, 200, 2, new CourierRoute(Alt, null, null)),
            Line(scratch.Id, 300, 3, new CourierRoute(Alt, 77, null)),
        ]);
        Assert.True(StowagePlanMigration.SeparateCourierLines(state));
        var plans = state.StowagePlans.Select(plan => (plan.Id, plan.Kind, plan.Revision)).ToArray();
        var lines = state.PlanItems.Select(rule => (rule.Id, rule.StowagePlanId, rule.TargetQuantity)).ToArray();

        Assert.False(StowagePlanMigration.SeparateCourierLines(state));

        Assert.Equal(plans, state.StowagePlans.Select(plan => (plan.Id, plan.Kind, plan.Revision)));
        Assert.Equal(lines, state.PlanItems.Select(rule => (rule.Id, rule.StowagePlanId, rule.TargetQuantity)));
    }

    [Fact]
    public void Repository_PersistsTheSplitOnLoad()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quartermaster.json");
        var store = new QuartermasterStateStore(path);
        var state = new QuartermasterState();
        var plan = Plan("Weekly", ephemeral: false, enabled: true);
        state.StowagePlans.Add(plan);
        var courierLine = Line(plan.Id, 200, 5, new CourierRoute(Alt, 77, null));
        state.PlanItems.AddRange([Line(plan.Id, 100, 12), courierLine]);
        store.Save(state);

        _ = new StateRepository(store);

        var reloaded = new QuartermasterStateStore(path).Load();
        var sibling = Assert.Single(reloaded.StowagePlans, candidate => candidate.Kind == StowagePlanKind.Courier);
        Assert.Equal(sibling.Id, Assert.Single(reloaded.PlanItems, rule => rule.Id == courierLine.Id).StowagePlanId);
    }

    private static StowagePlan Plan(string name, bool ephemeral, bool enabled) => new()
    {
        Owner = TestData.Owner with { },
        Name = name,
        IsEphemeral = ephemeral,
        Enabled = enabled,
    };

    private static TargetPlanItem Line(Guid planId, uint itemId, int quantity, CourierRoute? route = null, bool enabled = true) => new()
    {
        StowagePlanId = planId,
        ItemId = itemId,
        ItemName = $"Item {itemId}",
        TargetQuantity = quantity,
        Courier = route,
        Enabled = enabled,
    };
}
