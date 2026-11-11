using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class MarketRunTests
{
    private static readonly OwnerScope Owner = new()
    {
        LocalContentId = 100,
        HomeWorldId = 93,
        CharacterName = "Wei Ning",
        HomeWorldName = "Siren",
    };

    [Fact]
    public void NoPlan_IsEmptyAndComplete()
    {
        var run = MarketRunPlanner.Evaluate(null, Projection([], [Player(1, "Hull", 50)]));

        Assert.Null(run.PlanId);
        Assert.Empty(run.Retainers);
        Assert.Empty(run.Items);
        Assert.Empty(run.CraftQueue);
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void UnplannedListingsAndRetainers_AreLeftAlone()
    {
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 1, 1, 100)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(
            [Listing(1, "Hull", 10, "Alpha", 1, 100), Listing(2, "Bow", 11, "Beta", 1, 50)],
            [Retainer(2, "Bow", 11, 9)]));

        Assert.Equal([10UL], run.Retainers.Select(retainer => retainer.RetainerId));
        Assert.Equal([(uint)1], run.Items.Select(item => item.ItemId));
        Assert.Empty(run.CraftQueue);
    }

    [Fact]
    public void ShortListings_RefillFromSpareInWholeListingsAndCraftTheRest()
    {
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 4, 5, 100)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(
            [Listing(1, "Hull", 10, "Alpha", 5, 100), Listing(1, "Hull", 10, "Alpha", 5, 120)],
            [Player(1, "Hull", 3), Retainer(1, "Hull", 11, 3)]));

        var assignment = Assert.Single(Assert.Single(run.Retainers).Assignments);
        Assert.Equal(4, assignment.PlannedListings);
        Assert.Equal(1, assignment.ExactListings.Value);
        Assert.Equal(1, assignment.RepriceListings.Value);
        Assert.Equal(2, assignment.ShortListings.Value);
        Assert.Equal(1, assignment.RefillListings.Value);
        Assert.Equal(1, assignment.UnfilledListings.Value);

        var item = Assert.Single(run.Items);
        Assert.Equal(20, item.PlannedUnits);
        Assert.Equal(10, item.ListedUnits.Value);
        Assert.Equal(6, item.SpareUnits.Value);
        Assert.Equal(5, item.RefillUnits.Value);
        Assert.Equal(5, item.UnfilledUnits.Value);
        Assert.Equal(1, item.ReserveAfterRun.Value);
        Assert.Equal(5, item.CraftUnits.Value);
        Assert.Equal([new MarketRunCraftLine(1, "Hull", ItemQualityPolicy.NqOnly, 5)], run.CraftQueue);
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void ScarceSpare_AllocatesByRetainerNameThenPlanOrder()
    {
        var plan = Plan(
        [
            Assignment(1, "Hull", 10, "Zeta", 2, 2, 100),
            Assignment(1, "Hull", 11, "alpha", 1, 2, 100),
            Assignment(1, "Hull", 11, "alpha", 1, 3, 90),
        ]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Player(1, "Hull", 6)], retainerIds: [10, 11]));

        var alpha = run.Retainers.Single(retainer => retainer.RetainerId == 11);
        var zeta = run.Retainers.Single(retainer => retainer.RetainerId == 10);
        Assert.Equal([11UL, 10UL], run.Retainers.Select(retainer => retainer.RetainerId));
        Assert.Equal([1, 1], alpha.Assignments.Select(row => row.RefillListings.Value));
        Assert.Equal(0, zeta.RefillListings.Value);
        Assert.Equal(2, zeta.UnfilledListings.Value);
        var item = Assert.Single(run.Items);
        Assert.Equal(5, item.RefillUnits.Value);
        Assert.Equal(4, item.UnfilledUnits.Value);
        Assert.Equal(1, item.ReserveAfterRun.Value);
    }

    [Fact]
    public void Reserve_ShortfallAfterRefillIsAddedToCraftUnits()
    {
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 2, 5, 100)],
            [Reserve(1, "Hull", 20)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Retainer(1, "Hull", 10, 12)], retainerIds: [10]));

        var item = Assert.Single(run.Items);
        Assert.Equal(10, item.RefillUnits.Value);
        Assert.Equal(0, item.UnfilledUnits.Value);
        Assert.Equal(20, item.ReserveUnits);
        Assert.Equal(2, item.ReserveAfterRun.Value);
        Assert.Equal(18, item.CraftUnits.Value);
        Assert.Equal(18, Assert.Single(run.CraftQueue).Quantity);
    }

    [Fact]
    public void ReserveWithoutAssignment_AppearsWithReserveShortfallOnly()
    {
        var plan = Plan([], [Reserve(2, "Rudder", 10), Reserve(3, "Bow", 4), Disabled(Reserve(4, "Prow", 99))]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Player(2, "Rudder", 3), Player(3, "Bow", 9)]));

        Assert.Empty(run.Retainers);
        Assert.Equal(["Bow", "Rudder"], run.Items.Select(item => item.ItemName));
        Assert.Equal(0, run.Find(2, ItemQualityPolicy.NqOnly)!.PlannedUnits);
        Assert.Equal(7, run.Find(2, ItemQualityPolicy.NqOnly)!.CraftUnits.Value);
        Assert.Equal(0, run.Find(3, ItemQualityPolicy.NqOnly)!.CraftUnits.Value);
        Assert.Equal([new MarketRunCraftLine(2, "Rudder", ItemQualityPolicy.NqOnly, 7)], run.CraftQueue);
    }

    [Fact]
    public void Quality_IsKeptSeparateForSpareAndReserve()
    {
        var plan = Plan([], [Reserve(1, "Hull", 5, ItemQualityPolicy.HqOnly)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Player(1, "Hull", 50), Player(1, "Hull", 2, FfxivItemQuality.HQ)]));

        Assert.Equal(2, Assert.Single(run.Items).SpareUnits.Value);
        Assert.Equal(new MarketRunCraftLine(1, "Hull", ItemQualityPolicy.HqOnly, 3), Assert.Single(run.CraftQueue));
    }

    [Fact]
    public void UnobservedRetainerListings_KeepThatItemUnknownAndOutOfTheQueue()
    {
        var plan = Plan(
        [
            Assignment(1, "Hull", 10, "Alpha", 2, 1, 100),
            Assignment(2, "Bow", 11, "Beta", 2, 1, 100),
        ]);
        var projection = Projection([], [Player(1, "Hull", 0), Player(2, "Bow", 0)], retainerIds: [10, 11],
            listingComplete: new Dictionary<string, bool>
            {
                [BrowserScope.AllKey] = false,
                [BrowserScope.RetainerKey(10)] = false,
                [BrowserScope.RetainerKey(11)] = true,
            });

        var run = MarketRunPlanner.Evaluate(plan, projection);

        var alpha = run.Retainers.Single(retainer => retainer.RetainerId == 10);
        Assert.False(alpha.ListingsComplete);
        Assert.False(alpha.ShortListings.IsKnown);
        Assert.False(alpha.Assignments.Single().RefillListings.IsKnown);
        Assert.False(run.Find(1, ItemQualityPolicy.NqOnly)!.CraftUnits.IsKnown);
        Assert.Equal(2, run.Find(2, ItemQualityPolicy.NqOnly)!.CraftUnits.Value);
        Assert.Equal([(uint)2], run.CraftQueue.Select(line => line.ItemId));
        Assert.False(run.IsComplete);
    }

    [Fact]
    public void UnobservedRetainerInventory_MakesSpareAndCraftUnknown()
    {
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 2, 1, 100)], [Reserve(2, "Rudder", 5)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Player(1, "Hull", 9)], retainerIds: [10], inventoryComplete: false));

        Assert.All(run.Items, item =>
        {
            Assert.False(item.SpareUnits.IsKnown);
            Assert.False(item.RefillUnits.IsKnown);
            Assert.False(item.ReserveAfterRun.IsKnown);
            Assert.False(item.CraftUnits.IsKnown);
        });
        var assignment = run.Retainers.Single().Assignments.Single();
        Assert.Equal(2, assignment.ShortListings.Value);
        Assert.False(assignment.RefillListings.IsKnown);
        Assert.Empty(run.CraftQueue);
        Assert.False(run.IsComplete);
    }

    [Fact]
    public void DisabledAssignments_AreIgnored()
    {
        var disabled = Assignment(1, "Hull", 10, "Alpha", 2, 1, 100);
        disabled.Enabled = false;
        var run = MarketRunPlanner.Evaluate(Plan([disabled]), Projection([], [], retainerIds: [10]));

        Assert.Empty(run.Retainers);
        Assert.Empty(run.Items);
    }

    private static ListingPlan Plan(IReadOnlyList<ListingPlanAssignment> assignments, IReadOnlyList<ListingReserveRule>? reserves = null) => new()
    {
        Owner = Owner,
        Assignments = assignments.ToList(),
        Reserves = reserves?.ToList() ?? [],
    };

    private static ListingPlanAssignment Assignment(uint itemId, string name, ulong retainerId, string retainer, int count, int quantity, int price) => new()
    {
        ItemId = itemId,
        ItemName = name,
        RetainerId = retainerId,
        RetainerName = retainer,
        ListingCount = count,
        QuantityPerListing = quantity,
        UnitPrice = price,
        Quality = ItemQualityPolicy.NqOnly,
    };

    private static ListingReserveRule Reserve(uint itemId, string name, int units, ItemQualityPolicy quality = ItemQualityPolicy.NqOnly) => new()
    {
        ItemId = itemId,
        ItemName = name,
        Quality = quality,
        ReserveUnits = units,
    };

    private static ListingReserveRule Disabled(ListingReserveRule reserve)
    {
        reserve.Enabled = false;
        return reserve;
    }

    private static ListingRow Listing(uint itemId, string name, ulong retainerId, string retainer, int quantity, uint price) => new(
        BrowserScope.RetainerKey(retainerId), retainerId, retainer, null, itemId, name, quantity,
        FfxivItemQuality.NQ, Evidence.Unknown<decimal>("Condition unavailable"),
        Evidence.Known((decimal)price), Evidence.Known((decimal)(price * quantity)), DateTime.UnixEpoch);

    private static StockStack Player(uint itemId, string name, int quantity, FfxivItemQuality quality = FfxivItemQuality.NQ) =>
        new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Bag", 0, itemId, name, quantity, quality, DateTime.UnixEpoch);

    private static StockStack Retainer(uint itemId, string name, ulong retainerId, int quantity) =>
        new(BrowserScope.RetainerKey(retainerId), BrowserScopeKind.Retainer, retainerId, $"Retainer {retainerId}", "Bag", 0, itemId, name, quantity, FfxivItemQuality.NQ, DateTime.UnixEpoch);

    private static BrowserProjection Projection(
        IReadOnlyList<ListingRow> listings,
        IReadOnlyList<StockStack> stacks,
        IReadOnlyList<ulong>? retainerIds = null,
        bool inventoryComplete = true,
        IReadOnlyDictionary<string, bool>? listingComplete = null)
    {
        var ids = (retainerIds ?? [])
            .Concat(listings.Select(listing => listing.RetainerId))
            .Concat(stacks.Where(stack => stack.RetainerId.HasValue).Select(stack => stack.RetainerId!.Value))
            .Distinct()
            .ToArray();
        var scopes = new List<BrowserScope>
        {
            new(BrowserScope.AllKey, "All", BrowserScopeKind.All, null),
            new(BrowserScope.PlayerKey, "Player", BrowserScopeKind.Player, null),
        };
        scopes.AddRange(ids.Select(id => new BrowserScope(BrowserScope.RetainerKey(id), $"Retainer {id}", BrowserScopeKind.Retainer, id)));
        return new BrowserProjection
        {
            Scopes = scopes,
            Items = BrowserProjection.Aggregate(stacks.Where(stack => stack.Quantity > 0)),
            Listings = listings,
            Owner = Owner,
            RetainerInventoryCompleteByScope = scopes.ToDictionary(scope => scope.Key,
                scope => scope.Kind == BrowserScopeKind.Player || inventoryComplete, StringComparer.Ordinal),
            RetainerListingsCompleteByScope = listingComplete ?? scopes.ToDictionary(scope => scope.Key, _ => true, StringComparer.Ordinal),
        };
    }
}
