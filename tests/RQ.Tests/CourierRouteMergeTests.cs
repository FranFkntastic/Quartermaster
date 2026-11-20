using RQ.Courier;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class CourierRouteMergeTests
{
    private static readonly OwnerScope Alt = new()
    {
        LocalContentId = 9002,
        HomeWorldId = 406,
        CharacterName = "Alt Character",
        HomeWorldName = "Maduin",
    };

    [Fact]
    public void ReroutedLines_CollapseIntoFirstWithSummedQuantity()
    {
        var planId = Guid.NewGuid();
        var existing = Line(planId, 100, 4, new CourierRoute(Alt, 77, 11), enabled: false);
        var other = Line(planId, 200, 9, new CourierRoute(Alt, 77, 11));
        var first = Line(planId, 100, 3, new CourierRoute(Alt, null, 11));
        var second = Line(planId, 100, 5, new CourierRoute(Alt, 88, 11), enabled: false);
        var rules = new List<TargetPlanItem> { existing, other, first, second };

        // Bulk "Send here" moves both selected lines onto retainer 77, where the item already goes.
        foreach (var rule in new[] { first, second })
            rule.Courier = rule.Courier! with { RetainerId = 77 };
        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal([existing.Id, other.Id], rules.Select(rule => rule.Id));
        Assert.Equal(12, existing.TargetQuantity);
        Assert.True(existing.Enabled);
        Assert.Equal(9, other.TargetQuantity);
    }

    [Fact]
    public void DifferentSourceOrQuality_StaySeparate()
    {
        var planId = Guid.NewGuid();
        var bags = Line(planId, 100, 2, new CourierRoute(Alt, 77, null));
        var retainer = Line(planId, 100, 2, new CourierRoute(Alt, 77, 11));
        var hq = Line(planId, 100, 2, new CourierRoute(Alt, 77, null));
        hq.Quality = ItemQualityPolicy.HqOnly;
        var rules = new List<TargetPlanItem> { bags, retainer, hq };

        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal(3, rules.Count);
        Assert.All(rules, rule => Assert.Equal(2, rule.TargetQuantity));
    }

    [Fact]
    public void MergedQuantity_SaturatesAtIntMax()
    {
        var planId = Guid.NewGuid();
        var rules = new List<TargetPlanItem>
        {
            Line(planId, 100, int.MaxValue - 1, new CourierRoute(Alt, 77, null)),
            Line(planId, 100, 10, new CourierRoute(Alt, 77, null)),
        };

        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal(int.MaxValue, Assert.Single(rules).TargetQuantity);
    }

    [Fact]
    public void TransferLines_AreNeverMerged()
    {
        var planId = Guid.NewGuid();
        var rules = new List<TargetPlanItem> { Line(planId, 100, 4), Line(planId, 100, 6) };

        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal(2, rules.Count);
        Assert.Equal([4, 6], rules.Select(rule => rule.TargetQuantity));
    }

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
