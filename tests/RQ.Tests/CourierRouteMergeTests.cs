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

    // A courier line is one item from one source container to one character; where it lands there
    // is its Routing. Lines that come to share that key merge: quantities add up and the
    // destinations union in line order.
    [Fact]
    public void LinesToOneCharacter_CollapseIntoFirstWithSummedQuantityAndUnionedRouting()
    {
        var planId = Guid.NewGuid();
        var existing = Line(planId, 100, 4, new CourierRoute(Alt, 11), enabled: false, to: [77]);
        var other = Line(planId, 200, 9, new CourierRoute(Alt, 11), to: [77]);
        var bags = Line(planId, 100, 3, new CourierRoute(Alt, 11));
        var second = Line(planId, 100, 5, new CourierRoute(Alt, 11), enabled: false, to: [88, 77]);
        second.Routing.AnySpace = true;
        var rules = new List<TargetPlanItem> { existing, other, bags, second };

        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal([existing.Id, other.Id], rules.Select(rule => rule.Id));
        Assert.Equal(12, existing.TargetQuantity);
        Assert.True(existing.Enabled);
        Assert.Equal([77ul, 88ul], existing.Routing.PreferredRetainerIds);
        Assert.True(existing.Routing.AnySpace);
        Assert.False(existing.Routing.Consolidate);
        Assert.Equal(9, other.TargetQuantity);
    }

    [Fact]
    public void UnionRouting_KeepsConsolidateOnlyWithARankOne()
    {
        var consolidating = new StowageRoutingPolicy { PreferredRetainerIds = [88], Consolidate = true };
        var merged = StowagePlanCatalog.UnionRouting(new StowageRoutingPolicy(), consolidating);
        Assert.Equal([88ul], merged.PreferredRetainerIds);
        Assert.True(merged.Consolidate);

        var anyOnly = StowagePlanCatalog.UnionRouting(new StowageRoutingPolicy { Consolidate = true }, new StowageRoutingPolicy { AnySpace = true });
        Assert.Empty(anyOnly.PreferredRetainerIds);
        Assert.True(anyOnly.AnySpace);
        Assert.False(anyOnly.Consolidate);
    }

    [Fact]
    public void DifferentSourceQualityOrCharacter_StaySeparate()
    {
        var planId = Guid.NewGuid();
        var bags = Line(planId, 100, 2, new CourierRoute(Alt, null), to: [77]);
        var retainer = Line(planId, 100, 2, new CourierRoute(Alt, 11), to: [77]);
        var hq = Line(planId, 100, 2, new CourierRoute(Alt, null), to: [77]);
        hq.Quality = ItemQualityPolicy.HqOnly;
        var elsewhere = Line(planId, 100, 2, new CourierRoute(TestData.Owner with { }, null, Alt with { }), to: [77]);
        var rules = new List<TargetPlanItem> { bags, retainer, hq, elsewhere };

        StowagePlanCatalog.MergeCourierDuplicates(rules);

        Assert.Equal(4, rules.Count);
        Assert.All(rules, rule => Assert.Equal(2, rule.TargetQuantity));
    }

    [Fact]
    public void MergedQuantity_SaturatesAtIntMax()
    {
        var planId = Guid.NewGuid();
        var rules = new List<TargetPlanItem>
        {
            Line(planId, 100, int.MaxValue - 1, new CourierRoute(Alt, null), to: [77]),
            Line(planId, 100, 10, new CourierRoute(Alt, null)),
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

    private static TargetPlanItem Line(Guid planId, uint itemId, int quantity, CourierRoute? route = null, bool enabled = true, ulong[]? to = null) => new()
    {
        StowagePlanId = planId,
        ItemId = itemId,
        ItemName = $"Item {itemId}",
        TargetQuantity = quantity,
        Courier = route,
        Enabled = enabled,
        Routing = new() { PreferredRetainerIds = [.. to ?? []] },
    };
}
