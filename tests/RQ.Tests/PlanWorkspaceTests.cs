using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class PlanWorkspaceTests
{
    [Fact]
    public void StowageCatalog_CreatesUniqueNamesAndOnlyOneEnabledPlan()
    {
        var state = new QuartermasterState();

        var first = StowagePlanCatalog.Create(state, TestData.Owner, "Workshop");
        var second = StowagePlanCatalog.Create(state, TestData.Owner, "workshop");

        Assert.True(first.Enabled);
        Assert.False(second.Enabled);
        Assert.Equal("workshop 2", second.Name);
        Assert.Equal("gooseworks-quartermaster-state/v5", state.Schema);
    }

    [Fact]
    public void Draft_IsIndependentAndCancelRequiresNoRepositoryMutation()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var rule = Rule(plan.Id, 100, "Ore", 10);
        var state = new QuartermasterState
        {
            StowagePlans = [plan],
            PlanItems = [rule],
        };

        var draft = StowagePlanCatalog.Draft(state, TestData.Owner, plan.Id);
        draft.Name = "Changed";
        draft.Rules[0].TargetQuantity = 99;
        draft.Rules[0].Routing.PreferredRetainerIds.Add(10);

        Assert.Equal("General", plan.Name);
        Assert.Equal(10, rule.TargetQuantity);
        Assert.Empty(rule.Routing.PreferredRetainerIds);
    }

    [Fact]
    public void NewDraft_DoesNotPersistUntilApplyAndCannotSaveEmptyResidue()
    {
        var state = new QuartermasterState();
        var draft = StowagePlanCatalog.NewDraft(state, TestData.Owner);

        Assert.Empty(state.StowagePlans);
        Assert.False(StowagePlanCatalog.CanApply(state, TestData.Owner, draft));

        draft.Rules.Add(Rule(draft.PlanId, 100, "Ore", 10));
        Assert.True(StowagePlanCatalog.CanApply(state, TestData.Owner, draft));

        var applied = StowagePlanCatalog.Apply(state, TestData.Owner, draft);

        Assert.Equal(draft.PlanId, applied.Id);
        Assert.Single(state.StowagePlans);
        Assert.Equal(applied.Id, Assert.Single(state.PlanItems).StowagePlanId);
    }

    [Fact]
    public void ExistingDraft_ApplyIsDisabledUntilIntentChanges()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var state = new QuartermasterState
        {
            StowagePlans = [plan],
            PlanItems = [Rule(plan.Id, 100, "Ore", 10)],
        };
        var draft = StowagePlanCatalog.Draft(state, TestData.Owner, plan.Id);

        Assert.False(StowagePlanCatalog.CanApply(state, TestData.Owner, draft));

        draft.Rules[0].TargetQuantity = 11;

        Assert.True(StowagePlanCatalog.CanApply(state, TestData.Owner, draft));
    }

    [Fact]
    public void Apply_PersistsOrderedRoutingOnceAndDisablesOtherPlan()
    {
        var first = new StowagePlan { Owner = TestData.Owner, Name = "First", Enabled = true };
        var second = new StowagePlan { Owner = TestData.Owner, Name = "Second", Enabled = false };
        var state = new QuartermasterState
        {
            StowagePlans = [first, second],
            PlanItems = [Rule(second.Id, 100, "Ore", 10)],
        };
        var draft = StowagePlanCatalog.Draft(state, TestData.Owner, second.Id);
        draft.Enabled = true;
        draft.Rules[0].Routing = new StowageRoutingPolicy
        {
            Mode = StowageRoutingMode.HomeFirst,
            PreferredRetainerIds = [20, 10],
            Overflow = StowageOverflowPolicy.HoldOnPlayer,
        };

        var applied = StowagePlanCatalog.Apply(state, TestData.Owner, draft);

        Assert.True(applied.Enabled);
        Assert.False(first.Enabled);
        Assert.Equal(2, first.Revision);
        Assert.Equal(2, applied.Revision);
        var route = Assert.Single(state.PlanItems).Routing;
        Assert.Equal([20UL, 10UL], route.PreferredRetainerIds);
        Assert.Equal(StowageOverflowPolicy.HoldOnPlayer, route.Overflow);
    }

    [Fact]
    public void Apply_RejectsAStaleDraftWithoutPartialChanges()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var state = new QuartermasterState { StowagePlans = [plan] };
        var draft = StowagePlanCatalog.Draft(state, TestData.Owner, plan.Id);
        draft.Name = "Stale edit";
        plan.Revision++;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StowagePlanCatalog.Apply(state, TestData.Owner, draft));

        Assert.Contains("changed after the editor opened", error.Message);
        Assert.Equal("General", plan.Name);
    }

    [Fact]
    public void ItemGroup_AddMissingCopiesIdentityWithoutCreatingLiveLinks()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var state = new QuartermasterState();
        var group = ItemGroupCatalog.Create(
            state,
            "@Crystals",
            [
                Rule(plan.Id, 8, "Fire Crystal", 5000),
                Rule(plan.Id, 9, "Ice Crystal", 5000),
            ]);
        var duplicateName = ItemGroupCatalog.Create(
            state,
            "crystals",
            [Rule(plan.Id, 10, "Wind Crystal", 5000)]);
        var draft = new StowagePlanDraft
        {
            PlanId = plan.Id,
            Rules = [Rule(plan.Id, 8, "Fire Crystal", 100)],
        };

        var added = ItemGroupCatalog.AddMissing(group, draft);
        group.Items.Single(item => item.ItemId == 9).ItemName = "Edited group name";

        Assert.Equal(1, added);
        var addedRule = draft.Rules.Single(rule => rule.ItemId == 9);
        Assert.Equal("Ice Crystal", addedRule.ItemName);
        Assert.Equal(0, addedRule.TargetQuantity);
        Assert.False(addedRule.Enabled);
        Assert.Equal("@Crystals", $"@{group.Name}");
        Assert.Equal("crystals 2", duplicateName.Name);
    }

    [Fact]
    public void ItemGroup_SelectMatchesOnlyItemAndQualityIdentities()
    {
        var plan = new StowagePlan { Owner = TestData.Owner };
        var state = new QuartermasterState();
        var groupRule = Rule(plan.Id, 100, "Ore", 10);
        groupRule.Quality = ItemQualityPolicy.NqOnly;
        var group = ItemGroupCatalog.Create(
            state,
            "Metals",
            [groupRule]);
        var matching = Rule(plan.Id, 100, "Ore", 99);
        matching.Quality = ItemQualityPolicy.NqOnly;
        var otherQuality = Rule(plan.Id, 100, "Ore", 99);
        otherQuality.Quality = ItemQualityPolicy.HqOnly;
        var draft = new StowagePlanDraft
        {
            PlanId = plan.Id,
            Rules = [matching, otherQuality, Rule(plan.Id, 200, "Log", 1)],
        };

        var selected = ItemGroupCatalog.MatchingRuleIds(group, draft);

        Assert.Equal([matching.Id], selected);
    }

    [Fact]
    public void ItemGroup_AddAndSelectAlsoWorksForRestockDrafts()
    {
        var state = new QuartermasterState();
        var source = Rule(Guid.NewGuid(), 100, "Ore", 10);
        var group = ItemGroupCatalog.Create(state, "Metals", [source]);
        var draft = RestockPlanCatalog.NewDraft(state, TestData.Owner);

        var added = ItemGroupCatalog.AddMissing(group, draft);
        var selected = ItemGroupCatalog.MatchingItemIds(group, draft);

        Assert.Equal(1, added);
        var item = Assert.Single(draft.Items);
        Assert.Equal((uint)100, item.ItemId);
        Assert.False(item.Enabled);
        Assert.Equal([item.Id], selected);
    }

    [Fact]
    public void ItemGroupDraft_NewDoesNotPersistAndRejectsEmptySave()
    {
        var state = new QuartermasterState();
        var draft = ItemGroupCatalog.NewDraft(state);

        Assert.Empty(state.ItemGroups);
        Assert.False(ItemGroupCatalog.CanApply(state, draft));

        draft.Items.Add(new ItemGroupItem { ItemId = 100, ItemName = "Ore" });

        Assert.True(ItemGroupCatalog.CanApply(state, draft));
        var applied = ItemGroupCatalog.Apply(state, draft);

        Assert.Equal(draft.GroupId, applied.Id);
        Assert.Single(state.ItemGroups);
    }

    [Fact]
    public void ItemGroupDraft_ApplyIsAtomicAndDeduplicatesIdentity()
    {
        var state = new QuartermasterState();
        var group = ItemGroupCatalog.Create(state, "Metals", [Rule(Guid.NewGuid(), 100, "Ore", 1)]);
        var draft = ItemGroupCatalog.Draft(state, group.Id);
        draft.Name = "Workshop metals";
        draft.Items.Add(new ItemGroupItem { ItemId = 100, ItemName = "Duplicate ore" });
        draft.Items.Add(new ItemGroupItem { ItemId = 100, ItemName = "HQ ore", Quality = ItemQualityPolicy.HqOnly });

        var applied = ItemGroupCatalog.Apply(state, draft);

        Assert.Equal("Workshop metals", applied.Name);
        Assert.Equal(2, applied.Revision);
        Assert.Equal(2, applied.Items.Count);
        Assert.Contains(applied.Items, item => item.ItemId == 100 && item.Quality == ItemQualityPolicy.Any);
        Assert.Contains(applied.Items, item => item.ItemId == 100 && item.Quality == ItemQualityPolicy.HqOnly);
    }

    [Fact]
    public void ItemGroupDraft_RejectsStaleApplyAndDelete()
    {
        var state = new QuartermasterState();
        var group = ItemGroupCatalog.Create(state, "Metals", [Rule(Guid.NewGuid(), 100, "Ore", 1)]);
        var draft = ItemGroupCatalog.Draft(state, group.Id);
        draft.Name = "Changed";
        group.Revision++;

        var applyError = Assert.Throws<InvalidOperationException>(() => ItemGroupCatalog.Apply(state, draft));
        var deleteError = Assert.Throws<InvalidOperationException>(() =>
            ItemGroupCatalog.Delete(state, group.Id, draft.SourceRevision));

        Assert.Contains("changed after the editor opened", applyError.Message);
        Assert.Contains("changed after the editor opened", deleteError.Message);
        Assert.Equal("Metals", group.Name);
        Assert.Single(state.ItemGroups);
    }

    [Fact]
    public void ItemGroupDraft_DeleteDoesNotMutateExistingPlans()
    {
        var stowage = new StowagePlan { Owner = TestData.Owner };
        var stowageRule = Rule(stowage.Id, 100, "Ore", 10);
        var restock = new RestockPlan
        {
            Owner = TestData.Owner,
            Items = [new RestockPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }],
        };
        var state = new QuartermasterState
        {
            StowagePlans = [stowage],
            PlanItems = [stowageRule],
            RestockPlans = [restock],
        };
        var group = ItemGroupCatalog.Create(state, "Metals", [stowageRule]);

        ItemGroupCatalog.Delete(state, group.Id, group.Revision);

        Assert.Empty(state.ItemGroups);
        Assert.Equal(10, Assert.Single(state.PlanItems).TargetQuantity);
        Assert.Equal(10, Assert.Single(Assert.Single(state.RestockPlans).Items).TargetQuantity);
    }

    [Fact]
    public void ItemGroupDraft_AddMissingCopiesPlanSelectionWithoutLiveLinks()
    {
        var state = new QuartermasterState();
        var source = new RestockPlanItem
        {
            ItemId = 100,
            ItemName = "Ore",
            Quality = ItemQualityPolicy.NqOnly,
        };
        var draft = ItemGroupCatalog.NewDraft(state);

        var added = ItemGroupCatalog.AddMissing(draft, [source, source]);
        source.ItemName = "Changed source";

        Assert.Equal(1, added);
        var member = Assert.Single(draft.Items);
        Assert.Equal("Ore", member.ItemName);
        Assert.Equal(ItemQualityPolicy.NqOnly, member.Quality);
    }

    [Fact]
    public void Repository_RoundTripsItemGroups()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => ItemGroupCatalog.Create(
            state,
            "Workshop metals",
            [Rule(Guid.NewGuid(), 100, "Ore", 1)]));

        var reloaded = TestData.Repository(directory.Path).Snapshot();

        var group = Assert.Single(reloaded.ItemGroups);
        Assert.Equal("Workshop metals", group.Name);
        Assert.Equal((uint)100, Assert.Single(group.Items).ItemId);
        Assert.Equal("gooseworks-quartermaster-state/v5", reloaded.Schema);
    }

    private static TargetPlanItem Rule(Guid planId, uint itemId, string itemName, int target) => new()
    {
        StowagePlanId = planId,
        ItemId = itemId,
        ItemName = itemName,
        TargetQuantity = target,
    };
}
