using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class PlannerTests
{
    [Fact]
    public void Build_ComputesTargetNeedAndRanksFreshLargeCandidates()
    {
        var rows = new[] { new TargetPlanItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 80 } };
        var first = TestData.Retainer(10, "Old Large", (100, "Darksteel Ore", 40));
        first.ObservedAtUtc = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        first.Bags[0].ObservedAtUtc = first.ObservedAtUtc;
        var second = TestData.Retainer(11, "Fresh Large", (100, "Darksteel Ore", 40));
        second.ObservedAtUtc = new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Utc);
        second.Bags[0].ObservedAtUtc = second.ObservedAtUtc;
        var small = TestData.Retainer(12, "Small", (100, "Darksteel Ore", 15));

        var plan = RestockPlanner.Build(rows, new Dictionary<uint, int> { [100] = 20 }, new Dictionary<ulong, CachedRetainer>
        {
            [10] = first,
            [11] = second,
            [12] = small,
        }, TestData.Owner, new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc));

        var line = Assert.Single(plan.Lines);
        Assert.Equal(60, line.NeededQuantity);
        Assert.Equal(95, line.CachedRetainerQuantity);
        Assert.Equal(PlanLineStatus.Ready, line.Status);
        Assert.Equal(["Fresh Large", "Old Large", "Small"], line.Candidates.Select(candidate => candidate.RetainerName));
    }

    [Fact]
    public void Build_UsesOldestRelevantItemBagEvidenceForCandidate()
    {
        var retainer = TestData.Retainer(10, "Eris", (100, "Ore", 3));
        retainer.ObservedAtUtc = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        retainer.Bags[0].ObservedAtUtc = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        retainer.Bags.Add(new CachedBag
        {
            BagName = "RetainerPage2",
            ObservedAtUtc = new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Utc),
            Items = [new CachedItem { ItemId = 100, ItemName = "Ore", Quantity = 4 }],
        });
        retainer.Bags.Add(new CachedBag
        {
            BagName = "RetainerPage3",
            ObservedAtUtc = new DateTime(2026, 7, 21, 9, 0, 0, DateTimeKind.Utc),
            Items = [new CachedItem { ItemId = 200, ItemName = "Log", Quantity = 99 }],
        });

        var plan = RestockPlanner.Build(
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 7 }],
            new Dictionary<uint, int>(),
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner,
            retainer.ObservedAtUtc);

        var candidate = Assert.Single(Assert.Single(plan.Lines).Candidates);
        Assert.Equal(7, candidate.CachedQuantity);
        Assert.Equal(retainer.Bags[0].ObservedAtUtc, candidate.ObservedAtUtc);
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(plan.Lines).OldestEvidenceAge);
    }

    [Fact]
    public void Build_FailsClosedToOwnerScopeAndReportsPartialCoverage()
    {
        var current = TestData.Retainer(10, "Current", (100, "Ore", 15));
        var other = TestData.Retainer(11, "Other", (100, "Ore", 999));
        other.Owner = new OwnerScope { LocalContentId = 99, HomeWorldId = 406, CharacterName = "Other", HomeWorldName = "Maduin" };

        var plan = RestockPlanner.Build(
            [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 100 }],
            new Dictionary<uint, int> { [100] = 20 },
            new Dictionary<ulong, CachedRetainer> { [10] = current, [11] = other },
            TestData.Owner,
            new DateTime(2026, 7, 21, 13, 30, 0, DateTimeKind.Utc));

        var line = Assert.Single(plan.Lines);
        Assert.Equal(15, line.CachedRetainerQuantity);
        Assert.Equal(65, line.MissingQuantity);
        Assert.Equal(PlanLineStatus.Partial, line.Status);
        Assert.Equal("Current", Assert.Single(line.Candidates).RetainerName);
    }

    [Fact]
    public void Build_IgnoresDisabledRowsAndMarksSatisfiedTargets()
    {
        var plan = RestockPlanner.Build(
            [
                new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10, Enabled = false },
                new TargetPlanItem { ItemId = 200, ItemName = "Log", TargetQuantity = 10 },
            ],
            new Dictionary<uint, int> { [200] = 12 },
            new Dictionary<ulong, CachedRetainer>(),
            TestData.Owner,
            DateTime.UtcNow);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(200U, line.ItemId);
        Assert.Equal(PlanLineStatus.Satisfied, line.Status);
        Assert.Equal(0, line.NeededQuantity);
    }

    [Fact]
    public void ElementalDeposit_UsesOwnerScopedCrystalCapacity()
    {
        var retainer = TestData.Retainer(10, "Eris");
        retainer.Bags.Add(new CachedBag
        {
            BagName = "RetainerCrystals",
            Items = [new CachedItem { ItemId = 2, ItemName = "Fire Shard", Quantity = 9_900 }],
        });

        var plan = ElementalDepositPlanner.Build(
            new Dictionary<uint, int> { [2] = 500 },
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner,
            _ => "Fire Shard",
            DateTime.UtcNow);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(99, line.Capacity);
        Assert.Equal(99, line.PlannedQuantity);
        Assert.Equal(401, line.RemainingQuantity);
    }

    [Fact]
    public void ElementalDeposit_ExcludesRetainersWithoutObservedCrystalCapacity()
    {
        var plan = ElementalDepositPlanner.Build(
            new Dictionary<uint, int> { [2] = 500 },
            new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Unknown") },
            TestData.Owner,
            _ => "Fire Shard",
            DateTime.UtcNow);

        Assert.Empty(plan.Candidates);
        Assert.False(plan.CanRun);
        Assert.Equal(0, Assert.Single(plan.Lines).PlannedQuantity);
        Assert.Equal(1, plan.UnknownCrystalCacheCount);
    }
}
