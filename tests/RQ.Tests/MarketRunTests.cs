using System.Text.Json;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class MarketRunTests
{
    [Fact]
    public void Capacity_FreeSlotsSuffice_NoPull()
    {
        var run = MarketRunPlanner.Evaluate(Plan([Assignment(1, "Hull", 10, "Alpha", 2, 1, 100)]),
            Projection([Listing(2, "Bow", 10, "Alpha", 1, 10)], [Player(1, "Hull", 2)]));
        var retainer = Assert.Single(run.Retainers);
        Assert.Equal(1, retainer.ObservedListingCount.Value);
        Assert.Equal(19, retainer.FreeSlots.Value);
        Assert.Equal(0, retainer.PullListings.Value);
        Assert.Empty(retainer.ListingsToPull);
        Assert.Equal(2, retainer.RefillListings.Value);
    }

    [Fact]
    public void Capacity_PullsOnlyTheRoomNeeded_ProtectsWrongPriceRightShape()
    {
        var planned = Listing(1, "Hull", 10, "Alpha", 2, 999);
        var unknownPrice = planned with { UnitPrice = Evidence.Unknown<decimal>("Not observed"), SlotIndex = 1 };
        var surplus = Listing(1, "Hull", 10, "Alpha", 2, 500);
        var wrongShape = Listing(1, "Hull", 10, "Alpha", 3, 1);
        var offPlan = Enumerable.Range(0, 16).Select(i => Listing(2, "Bow", 10, "Alpha", 1, (uint)(10 + i)));
        var observed = new[] { planned, unknownPrice, surplus, wrongShape }.Concat(offPlan).ToArray();
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 2, 2, 100), Assignment(3, "Prow", 10, "Alpha", 2, 1, 20)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(3, "Prow", 2)]));
        var retainer = Assert.Single(run.Retainers);
        Assert.Equal(2, retainer.PullListings.Value);
        Assert.Equal(0, retainer.BlockedBySlotsListings.Value);
        Assert.Same(wrongShape, retainer.ListingsToPull[0].Listing);
        Assert.DoesNotContain(retainer.ListingsToPull, p => ReferenceEquals(p.Listing, planned) || ReferenceEquals(p.Listing, unknownPrice));
        Assert.Empty(MarketRunPlanner.SelectListingsToPull(observed, plan.Assignments, 0));
        Assert.Contains(MarketRunPlanner.SelectListingsToPull(observed, plan.Assignments, 20), p => ReferenceEquals(p.Listing, surplus));
    }

    [Fact]
    public void Capacity_AllUnplannedPulled_RemainderBlocked_StockGoesToLaterRetainer()
    {
        var plan = Plan([
            Assignment(1, "Hull", 10, "Alpha", 19, 1, 100),
            Assignment(2, "Bow", 10, "Alpha", 3, 2, 100),
            Assignment(2, "Bow", 11, "Beta", 1, 2, 100)],
            [Reserve(2, "Bow", 2)]);
        var observed = Enumerable.Range(0, 19).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100))
            .Append(Listing(3, "Prow", 10, "Alpha", 1, 1)).ToArray();
        var run = MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(2, "Bow", 8)], retainerIds: [10, 11]));
        var alpha = run.Retainers[0];
        Assert.Single(alpha.ListingsToPull);
        Assert.Equal(1, alpha.PullListings.Value);
        Assert.Equal(2, alpha.BlockedBySlotsListings.Value);
        Assert.Equal(1, alpha.RefillListings.Value);
        Assert.Equal(0, alpha.UnfilledListings.Value);
        Assert.Equal(2, alpha.Assignments[1].BlockedBySlotsListings.Value);
        Assert.Equal(1, run.Retainers[1].RefillListings.Value);
        var item = run.Find(2, ItemQualityPolicy.NqOnly)!;
        Assert.Equal(4, item.RefillUnits.Value);
        Assert.Equal(4, item.ReserveAfterRun.Value);
        Assert.Equal(0, item.UnfilledUnits.Value);
        Assert.Empty(run.CraftQueue);
    }

    [Fact]
    public void Capacity_AcrossItems_UsesPlanOrderNotItemGrouping()
    {
        var plan = Plan([
            Assignment(1, "Hull", 10, "Alpha", 19, 1, 100),
            Assignment(2, "Bow", 10, "Alpha", 1, 1, 100),
            Assignment(1, "Hull", 10, "Alpha", 1, 2, 100)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(
            Enumerable.Range(0, 19).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100)).ToArray(),
            [Player(1, "Hull", 2), Player(2, "Bow", 1)]));
        Assert.Equal([0, 1, 0], run.Retainers[0].Assignments.Select(a => a.RefillListings.Value));
        Assert.Equal([0, 0, 1], run.Retainers[0].Assignments.Select(a => a.BlockedBySlotsListings.Value));
        Assert.Empty(run.CraftQueue);
    }

    [Fact]
    public void Capacity_ExactlyEnoughFreeSlots_RefillsWithoutPulling()
    {
        var observed = Enumerable.Range(0, 17).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100))
            .Append(Listing(3, "Prow", 10, "Alpha", 1, 1)).ToArray();
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 17, 1, 100), Assignment(2, "Bow", 10, "Alpha", 2, 1, 100)]);
        var retainer = Assert.Single(MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(2, "Bow", 2)])).Retainers);
        Assert.Equal(2, retainer.FreeSlots.Value);
        Assert.Equal(2, retainer.RefillListings.Value);
        Assert.Equal(0, retainer.PullListings.Value);
        Assert.Equal(0, retainer.BlockedBySlotsListings.Value);
        Assert.Empty(retainer.ListingsToPull);
    }

    [Fact]
    public void Capacity_FullRetainer_PullsOneUnplannedForOneRefill()
    {
        var unplanned = Listing(3, "Prow", 10, "Alpha", 1, 1);
        var observed = Enumerable.Range(0, 19).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100)).Append(unplanned).ToArray();
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 19, 1, 100), Assignment(2, "Bow", 10, "Alpha", 1, 1, 100)]);
        var retainer = Assert.Single(MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(2, "Bow", 1)])).Retainers);
        Assert.Equal(0, retainer.FreeSlots.Value);
        Assert.Equal(1, retainer.RefillListings.Value);
        Assert.Equal(0, retainer.BlockedBySlotsListings.Value);
        Assert.Same(unplanned, Assert.Single(retainer.ListingsToPull).Listing);
    }

    [Fact]
    public void Capacity_PulledUnitsCountTowardThatItemsStockAfterTheRun()
    {
        var observed = Enumerable.Range(0, 19).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100))
            .Append(Listing(3, "Prow", 10, "Alpha", 5, 1)).ToArray();
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 19, 1, 100), Assignment(2, "Bow", 10, "Alpha", 1, 1, 100)],
            [Reserve(3, "Prow", 7)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(2, "Bow", 1)]));
        Assert.Single(Assert.Single(run.Retainers).ListingsToPull);
        var prow = run.Find(3, ItemQualityPolicy.NqOnly)!;
        Assert.Equal(5, prow.ReserveAfterRun.Value);
        Assert.Equal(2, prow.CraftUnits.Value);
        Assert.Equal(2, Assert.Single(run.CraftQueue).Quantity);
    }

    [Fact]
    public void Capacity_FullOfPlannedListings_BlocksTheRefillAndPullsNothing()
    {
        var observed = Enumerable.Range(0, 20).Select(i => Listing(1, "Hull", 10, "Alpha", 1, 100)).ToArray();
        var plan = Plan([Assignment(1, "Hull", 10, "Alpha", 20, 1, 100), Assignment(2, "Bow", 10, "Alpha", 1, 1, 100)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(observed, [Player(2, "Bow", 1)]));
        var retainer = Assert.Single(run.Retainers);
        Assert.Equal(0, retainer.RefillListings.Value);
        Assert.Equal(1, retainer.BlockedBySlotsListings.Value);
        Assert.Empty(retainer.ListingsToPull);
        Assert.Empty(run.CraftQueue);
    }

    [Fact]
    public void Capacity_UnknownEarlierAssignment_LeavesLaterOnesOnThatRetainerUnknown()
    {
        // Alpha is unobserved, so how much Prow stock reaches Beta is unknown. Beta has one slot
        // left; the Prow listing ahead of Bow in plan order may or may not take it.
        var plan = Plan([
            Assignment(3, "Prow", 10, "Alpha", 1, 1, 50),
            Assignment(1, "Hull", 11, "Beta", 19, 1, 100),
            Assignment(3, "Prow", 11, "Beta", 1, 1, 50),
            Assignment(2, "Bow", 11, "Beta", 1, 1, 100)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection(
            Enumerable.Range(0, 19).Select(i => Listing(1, "Hull", 11, "Beta", 1, 100)).ToArray(),
            [Player(3, "Prow", 1)], retainerIds: [10, 11],
            listingComplete: new Dictionary<string, bool>
            {
                [BrowserScope.AllKey] = false,
                [BrowserScope.RetainerKey(10)] = false,
                [BrowserScope.RetainerKey(11)] = true,
            }));
        var beta = run.Retainers.Single(retainer => retainer.RetainerId == 11);
        Assert.Equal(0, beta.Assignments[0].RefillListings.Value);
        Assert.False(beta.Assignments[1].RefillListings.IsKnown);
        Assert.False(beta.Assignments[2].RefillListings.IsKnown);
        Assert.False(beta.Assignments[2].BlockedBySlotsListings.IsKnown);
        Assert.False(beta.PullListings.IsKnown);
        Assert.True(run.Find(1, ItemQualityPolicy.NqOnly)!.CraftUnits.IsKnown);
        Assert.False(run.Find(2, ItemQualityPolicy.NqOnly)!.CraftUnits.IsKnown);
        Assert.False(run.Find(3, ItemQualityPolicy.NqOnly)!.CraftUnits.IsKnown);
        Assert.Empty(run.CraftQueue);
        Assert.False(run.IsComplete);
    }

    [Fact]
    public void Capacity_ObservedRetainerAheadOfAnUnobservedOne_KeepsItsOwnNumbers()
    {
        var plan = Plan([
            Assignment(3, "Prow", 10, "Alpha", 1, 1, 50),
            Assignment(2, "Bow", 10, "Alpha", 1, 1, 100),
            Assignment(3, "Prow", 11, "Beta", 1, 1, 50)]);
        var run = MarketRunPlanner.Evaluate(plan, Projection([], [Player(3, "Prow", 1)], retainerIds: [10, 11],
            listingComplete: new Dictionary<string, bool>
            {
                [BrowserScope.AllKey] = false,
                [BrowserScope.RetainerKey(10)] = true,
                [BrowserScope.RetainerKey(11)] = false,
            }));
        var alpha = run.Retainers.Single(retainer => retainer.RetainerId == 10);
        Assert.Equal([1, 0], alpha.Assignments.Select(a => a.RefillListings.Value));
        Assert.Equal(1, alpha.Assignments[1].UnfilledListings.Value);
        Assert.False(run.Find(3, ItemQualityPolicy.NqOnly)!.CraftUnits.IsKnown);
        Assert.Equal([(uint)2], run.CraftQueue.Select(line => line.ItemId));
    }

    [Fact]
    public void Capacity_PullOrder_UnknownThenTotalValueThenSlotThenObservation()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Listing((uint)(i + 1), "Item", 10, "Alpha", 1, 1000)).ToArray();
        rows[0] = rows[0] with { UnitPrice = Evidence.Unknown<decimal>("Not observed") };
        rows[1] = rows[1] with { Quantity = 2, UnitPrice = Evidence.Known(5m), SlotIndex = 5 };
        rows[2] = rows[2] with { UnitPrice = Evidence.Known(10m), SlotIndex = 3 };
        rows[3] = rows[3] with { UnitPrice = Evidence.Known(10m), SlotIndex = 3 };
        rows[4] = rows[4] with { UnitPrice = Evidence.Known(10m) };
        var pulls = MarketRunPlanner.SelectListingsToPull(rows, [], 5);
        Assert.Equal([0, 2, 3, 1, 4], pulls.Select(p => p.ObservationIndex));
    }

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
        Assert.False(alpha.ObservedListingCount.IsKnown);
        Assert.False(alpha.FreeSlots.IsKnown);
        Assert.False(alpha.PullListings.IsKnown);
        Assert.False(alpha.BlockedBySlotsListings.IsKnown);
        Assert.False(alpha.Assignments.Single().BlockedBySlotsListings.IsKnown);
        Assert.Empty(alpha.ListingsToPull);
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

    [Fact]
    public void CraftExport_WritesSavedJobShapeAndSeparatesLinesWithoutWorkshopProject()
    {
        MarketRunCraftLine[] lines =
        [
            new(1, "Bow", ItemQualityPolicy.NqOnly, 3),
            new(2, "Potion", ItemQualityPolicy.NqOnly, 4),
            new(3, "Hull", ItemQualityPolicy.HqOnly, 2),
            new(4, "Rudder", ItemQualityPolicy.NqOnly, 12_000),
        ];
        var projects = new Dictionary<uint, uint> { [1] = 501, [3] = 503, [4] = 504 };

        var result = MarketRunCraftExport.Export("  " + new string('x', 140) + "  ", lines,
            id => projects.TryGetValue(id, out var project) ? project : null, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal([(uint)2, (uint)3], result.WithoutWorkshopProject.Select(line => line.ItemId));
        Assert.Equal([(uint)4], result.Capped.Select(line => line.ItemId));
        using var document = JsonDocument.Parse(result.Json!);
        var root = document.RootElement;
        Assert.Equal("marketmafioso.workshop-saved-job", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(128, root.GetProperty("name").GetString()!.Length);
        Assert.Equal(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), root.GetProperty("exportedAt").GetDateTime());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([501u, 504u], items.Select(item => item.GetProperty("workshopItemId").GetUInt32()));
        Assert.Equal(["Bow", "Rudder"], items.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal([3, 9999], items.Select(item => item.GetProperty("quantity").GetInt32()));
    }

    [Fact]
    public void CraftExport_WithNoWorkshopLines_HasNoJsonAndDefaultsTheName()
    {
        var none = MarketRunCraftExport.Export("job", [new(2, "Potion", ItemQualityPolicy.NqOnly, 4)], _ => null, DateTime.UnixEpoch);
        Assert.Null(none.Json);
        Assert.Single(none.WithoutWorkshopProject);

        var named = MarketRunCraftExport.Export("   ", [new(1, "Bow", ItemQualityPolicy.NqOnly, 1)], _ => 7, DateTime.UnixEpoch);
        using var document = JsonDocument.Parse(named.Json!);
        Assert.Equal(MarketRunCraftExport.DefaultJobName, document.RootElement.GetProperty("name").GetString());
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
