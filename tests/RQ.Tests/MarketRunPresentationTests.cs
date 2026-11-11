using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;
using RQ.UI;

namespace RQ.Tests;

public sealed class MarketRunPresentationTests
{
    [Fact]
    public void PlanViewUsesCurrentDraftAndDoesNotAliasMutableRows()
    {
        var draft = new ListingPlanDraft { PlanId = Guid.NewGuid(), SourceRevision = 8,
            Assignments = [new() { ItemId = 1, ListingCount = 3 }],
            BaselineAssignments = [new() { ItemId = 2 }],
            Reserves = [new() { ItemId = 1, ReserveUnits = 50 }],
            BaselineReserves = [new() { ItemId = 2 }] };
        draft.Reserves[0].Location.PreferredRetainerIds.Add(10);
        var plan = MarketRunPresentation.PlanView(draft);
        Assert.Equal(draft.PlanId, plan.Id);
        Assert.Equal(8, plan.Revision);
        Assert.Equal(3, Assert.Single(plan.Assignments).ListingCount);
        Assert.Equal(50, Assert.Single(plan.Reserves).ReserveUnits);
        draft.Assignments[0].ListingCount = 9;
        draft.Reserves[0].Location.PreferredRetainerIds.Clear();
        Assert.Equal(3, plan.Assignments[0].ListingCount);
        Assert.Single(plan.Reserves[0].Location.PreferredRetainerIds);
    }

    [Fact]
    public void DisabledReserveOnlyRuleRemainsEditableWithoutCraftDemand()
    {
        var draft = new ListingPlanDraft { Reserves = [new() { ItemId = 1, ItemName = "Hull", Enabled = false, ReserveUnits = 20 }] };
        var run = new MarketRunEvaluation(null, 0, [], [], []);
        var row = Assert.Single(MarketRunPresentation.DisplayItems(run, draft));
        Assert.Equal(0, row.CraftUnits.Value);
        Assert.False(row.Reserve!.Enabled);
        Assert.False(row.SpareUnits.IsKnown);
    }

    [Fact]
    public void MissingStockDoesNotRenderGuessedTotals()
    {
        var unknown = Evidence.Unknown<int>("Not observed");
        var run = new MarketRunEvaluation(null, 0, [],
            [new(1, "Hull", ItemQualityPolicy.NqOnly, 5, unknown, unknown, unknown, unknown, null, 0, unknown, unknown)], []);
        Assert.Equal("Retainer stock not yet observed", MarketRunPresentation.Header(run));
        Assert.Equal("—", MarketRunPresentation.Number(unknown));
    }

    [Fact]
    public void EmptyCompleteRunHasZeroTotals() =>
        Assert.Equal("Refill 0 · Short 0 · Craft 0", MarketRunPresentation.Header(new(null, 0, [], [], [])));
}
