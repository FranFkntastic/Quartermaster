using System.Numerics;
using Franthropy.Dalamud.Automation.Vendors;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class TransferVendorProcurementTests
{
    [Fact]
    public void Builds_review_only_for_uncovered_vendor_enabled_shortage()
    {
        var plan = Plan();
        var rule = Rule(plan, ItemQualityPolicy.Any);
        var planner = Planner(Offer(100, 12, 700, "Ironmonger's Guild"));
        var retrieval = new RetrievalPlan(
            DateTime.UtcNow,
            [new(rule.Id, 100, "Iron Ore", rule.Quality, 50, 10, 40, 25, 15, [], PlanLineStatus.Partial, null)]);

        var review = planner.Build(TestData.Owner, plan, 42, [rule], retrieval);

        var line = Assert.Single(review.Lines);
        Assert.True(line.IsReady);
        Assert.Equal(15, line.ApprovedQuantity);
        Assert.Equal(180UL, line.MaximumGil);
        Assert.Equal(50, line.TargetTotalQuantity);
        Assert.Single(review.Stops);
        Assert.Equal(15, Assert.Single(review.ToBuyPlan().Lines).ApprovedQuantity);
    }

    [Theory]
    [InlineData(ItemQualityPolicy.NqOnly)]
    [InlineData(ItemQualityPolicy.HqOnly)]
    public void Exact_quality_rules_fail_closed(ItemQualityPolicy quality)
    {
        var plan = Plan();
        var rule = Rule(plan, quality);
        var retrieval = new RetrievalPlan(
            DateTime.UtcNow,
            [new(rule.Id, 100, "Iron Ore", quality, 10, 0, 10, 0, 10, [], PlanLineStatus.NoCachedStock, null)]);

        var review = Planner(Offer(100, 12, 700, "Ironmonger's Guild"))
            .Build(TestData.Owner, plan, 1, [rule], retrieval);

        Assert.Equal(TransferVendorProcurementState.ExactQualityUnsupported, Assert.Single(review.Lines).State);
        Assert.False(review.CanStart);
    }

    [Fact]
    public void Chooses_eligible_offer_before_cheaper_unavailable_offer()
    {
        var plan = Plan();
        var rule = Rule(plan, ItemQualityPolicy.Any);
        var cheap = Offer(100, 10, 701, "Locked Vendor");
        var accessible = Offer(100, 12, 702, "Reachable Vendor");
        var catalog = GilVendorCatalog.Create([cheap, accessible]);
        var planner = new TransferVendorProcurementPlanner(
            catalog,
            offer => offer.NpcId == cheap.NpcId
                ? new(GilVendorAccessState.Unavailable, "Locked", "That vendor is unavailable.")
                : new(GilVendorAccessState.Probeable, "RouteReady", "The route can be probed."));
        var retrieval = new RetrievalPlan(
            DateTime.UtcNow,
            [new(rule.Id, 100, "Iron Ore", rule.Quality, 10, 0, 10, 0, 10, [], PlanLineStatus.NoCachedStock, null)]);

        var review = planner.Build(TestData.Owner, plan, 1, [rule], retrieval);

        Assert.Equal(accessible.NpcId, Assert.Single(review.Lines).SelectedCandidate!.Offer.NpcId);
    }

    [Fact]
    public void Locked_shop_never_enters_the_executable_vendor_plan()
    {
        var plan = Plan();
        var rule = Rule(plan, ItemQualityPolicy.Any);
        var locked = Offer(100, 12, 700, "Locked Vendor");
        var planner = new TransferVendorProcurementPlanner(
            GilVendorCatalog.Create([locked]),
            _ => new(
                GilVendorAccessState.Unavailable,
                "ShopLocked",
                "Locked Vendor's shop is not unlocked for this character."));
        var retrieval = new RetrievalPlan(
            DateTime.UtcNow,
            [new(rule.Id, 100, "Iron Ore", rule.Quality, 10, 0, 10, 0, 10, [], PlanLineStatus.NoCachedStock, null)]);

        var review = planner.Build(TestData.Owner, plan, 1, [rule], retrieval);

        var refused = Assert.Single(review.Lines);
        Assert.Equal(TransferVendorProcurementState.VendorUnavailable, refused.State);
        Assert.False(review.CanStart);
        Assert.Empty(review.Stops);
        Assert.Empty(review.ToBuyPlan().Lines);
        Assert.Empty(review.ToBuyPlan().Stops);
    }

    private static TransferVendorProcurementPlanner Planner(params GilVendorOffer[] offers) => new(
        GilVendorCatalog.Create(offers),
        _ => new(GilVendorAccessState.Probeable, "RouteReady", "The route can be probed."));

    private static StowagePlan Plan() => new()
    {
        Owner = TestData.Owner,
        Name = "Raid restock",
        Revision = 7,
    };

    private static TargetPlanItem Rule(StowagePlan plan, ItemQualityPolicy quality) => new()
    {
        StowagePlanId = plan.Id,
        ItemId = 100,
        ItemName = "Iron Ore",
        TargetQuantity = 50,
        Quality = quality,
        AllowVendorPurchase = true,
    };

    private static GilVendorOffer Offer(uint itemId, uint price, uint npcId, string npcName) => new(
        itemId,
        "Iron Ore",
        1,
        price,
        500,
        0,
        npcId,
        npcName,
        129,
        Vector3.Zero,
        [2]);
}
