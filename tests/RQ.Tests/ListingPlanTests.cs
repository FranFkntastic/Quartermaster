using System.Text.Json;
using System.Text.Json.Nodes;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;
using RQ.UI;

namespace RQ.Tests;

public sealed class ListingPlanTests
{
    private static readonly OwnerScope Owner = new()
    {
        LocalContentId = 100,
        HomeWorldId = 93,
        CharacterName = "Wei Ning",
        HomeWorldName = "Siren",
    };

    [Fact]
    public void FirstDraft_SeedsOnlyListingCompleteRetainersWithoutPersisting()
    {
        var state = new QuartermasterState();
        var projection = Projection(
            [Listing(1, "Bow", 10, "Observed", 1, 100), Listing(2, "Hull", 11, "Unknown", 1, 200)],
            listingComplete: new Dictionary<string, bool>
            {
                [BrowserScope.AllKey] = false,
                [BrowserScope.RetainerKey(10)] = true,
                [BrowserScope.RetainerKey(11)] = false,
            });

        var draft = ListingPlanCatalog.Draft(state, Owner, projection);

        Assert.True(draft.IsNew);
        Assert.Single(draft.Assignments);
        Assert.Equal((uint)1, draft.Assignments[0].ItemId);
        Assert.Equal([11UL], draft.IncompleteListingRetainerIds);
        Assert.Empty(state.ListingPlans);
    }

    [Fact]
    public void DistinctShapesAndPrices_RoundTripWithStableIds()
    {
        var state = new QuartermasterState();
        var first = Assignment(1, "Ore", 10, "Botanist", 2, 99, 1_550);
        var second = Assignment(1, "Ore", 10, "Botanist", 3, 20, 2_100);
        var draft = NewDraft(first, second);

        var saved = ListingPlanCatalog.Apply(state, Owner, draft, _ => 999, new HashSet<ulong> { 10 }, DateTime.UnixEpoch);
        var transfer = new StowagePlan { Owner = Owner };
        state.StowagePlans.Add(transfer);
        state.PlanItems.Add(new TargetPlanItem
        {
            StowagePlanId = transfer.Id,
            ItemId = 1,
            Quality = ItemQualityPolicy.NqOnly,
            TargetQuantity = 7,
        });
        ListingPlanCatalog.Link(state, Owner, transfer.Id, saved.Id, 1, ItemQualityPolicy.NqOnly);
        var json = JsonSerializer.Serialize(state, AtomicDocumentStore<QuartermasterState>.JsonOptions);
        var loaded = JsonSerializer.Deserialize<QuartermasterState>(json, AtomicDocumentStore<QuartermasterState>.JsonOptions)!;

        Assert.Equal(2, loaded.ListingPlans.Single().Assignments.Count);
        Assert.Equal([first.Id, second.Id], loaded.ListingPlans.Single().Assignments.Select(assignment => assignment.Id));
        Assert.Equal([99, 20], loaded.ListingPlans.Single().Assignments.Select(assignment => assignment.QuantityPerListing));
        Assert.Single(loaded.TransferPlanListingLinks);
        Assert.Equal(7, loaded.PlanItems.Single().TargetQuantity);
    }

    [Fact]
    public void LegacyStateWithoutListingFields_LoadsWithUnchangedTransferTargets()
    {
        var transfer = new StowagePlan { Owner = Owner };
        var legacy = new QuartermasterState
        {
            StowagePlans = [transfer],
            PlanItems = [new TargetPlanItem { StowagePlanId = transfer.Id, ItemId = 1, TargetQuantity = 37 }],
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy, AtomicDocumentStore<QuartermasterState>.JsonOptions))!.AsObject();
        json.Remove("listingPlans");
        json.Remove("transferPlanListingLinks");

        var loaded = JsonSerializer.Deserialize<QuartermasterState>(json, AtomicDocumentStore<QuartermasterState>.JsonOptions)!;

        Assert.Empty(loaded.ListingPlans);
        Assert.Empty(loaded.TransferPlanListingLinks);
        Assert.Equal(37, loaded.PlanItems.Single().TargetQuantity);
    }

    [Fact]
    public void ListingRefresh_RebuildsListingDerivedPlanning()
    {
        Assert.True(QuartermasterRuntimeSnapshotSource.RequiresPlanningRefresh(RuntimeDomain.Listings));
        Assert.False(QuartermasterRuntimeSnapshotSource.RequiresPlanningRefresh(RuntimeDomain.Operations));
    }

    [Fact]
    public void CapacityTransition_CountsUnmanagedPhysicalListingsSeparately()
    {
        var draft = NewDraft(Assignment(1, "Ore", 10, "Retainer", 19, 1, 100));
        var unmanaged = Enumerable.Range(0, 20)
            .Select(_ => Listing(2, "Bow", 10, "Retainer", 1, 200))
            .ToArray();

        var conflict = QuartermasterWindow.ListingCapacityTransitionConflict(
            draft,
            Projection(unmanaged, inventoryComplete: true, listingsComplete: true));

        Assert.Contains("39 occupied", conflict);
        Assert.Contains("20 outside this plan", conflict);
    }

    [Fact]
    public void CapacityTransition_CountsWrongRetainerRowsAtTheirCurrentRetainer()
    {
        var draft = NewDraft(
            Assignment(1, "Y", 10, "Retainer A", 20, 1, 100),
            Assignment(2, "X", 11, "Retainer B", 20, 1, 100));
        var currentOnA = Enumerable.Range(0, 20)
            .Select(_ => Listing(2, "X", 10, "Retainer A", 1, 100))
            .ToArray();

        var conflict = QuartermasterWindow.ListingCapacityTransitionConflict(
            draft,
            Projection(currentOnA, inventoryComplete: true, listingsComplete: true));

        Assert.Contains("Retainer A has 40 occupied", conflict);
        Assert.Contains("20 outside this plan", conflict);
    }

    [Fact]
    public void Validation_PreservesInvalidIntentAndSeparatesDisabledMissingRetainer()
    {
        var invalid = Assignment(1, "Bow", 10, "Retainer", 21, 2, 0);
        var issues = ListingPlanCatalog.Validate([invalid], _ => 1, new HashSet<ulong> { 10 });
        Assert.Contains(issues, issue => issue.Field == nameof(ListingPlanAssignment.ListingCount));
        Assert.Contains(issues, issue => issue.Field == nameof(ListingPlanAssignment.QuantityPerListing));
        Assert.Contains(issues, issue => issue.Field == nameof(ListingPlanAssignment.UnitPrice));
        Assert.Equal(21, invalid.ListingCount);
        Assert.Equal(2, invalid.QuantityPerListing);

        invalid.Enabled = false;
        invalid.RetainerId = 999;
        Assert.Empty(ListingPlanCatalog.Validate([invalid], _ => 1, new HashSet<ulong> { 10 }));
    }

    [Fact]
    public void Validation_CapacityNamesRetainerAndIdentifiesEveryAffectedAssignment()
    {
        var first = Assignment(1, "Bow", 10, "Taffy-marauder", 12, 1, 100);
        var second = Assignment(2, "Hull", 10, "Taffy-marauder", 9, 1, 200);

        var issues = ListingPlanCatalog.Validate([first, second], _ => 1, new HashSet<ulong> { 10 })
            .Where(issue => issue.Field == "RetainerCapacity")
            .ToArray();

        Assert.Equal([first.Id, second.Id], issues.Select(issue => issue.AssignmentId));
        Assert.All(issues, issue => Assert.Equal("Taffy-marauder has 21 / 20 planned listing slots.", issue.Message));
    }

    [Fact]
    public void CrossViewItemFocus_UsesStableIdentityWhenDisplayNamesCollide()
    {
        var projection = Projection(
            [],
            [
                new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Bag", 0, 1, "Duplicate Name", 2, FfxivItemQuality.NQ, DateTime.UnixEpoch),
                new(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Bag", 1, 2, "Duplicate Name", 4, FfxivItemQuality.NQ, DateTime.UnixEpoch),
            ]);

        var focused = QuartermasterWindow.ApplyStockItemFocus(projection.GetItems(BrowserScope.AllKey), 2);

        Assert.Equal((uint)2, Assert.Single(focused).ItemId);
    }

    [Fact]
    public void StaleSave_RebasesDifferentFieldsAndReportsSameFieldCollision()
    {
        var assignment = Assignment(1, "Bow", 10, "Retainer", 2, 1, 100);
        var plan = new ListingPlan { Owner = Owner, Assignments = [ListingPlanCatalog.Copy(assignment)] };
        var state = new QuartermasterState { ListingPlans = [plan] };
        var draft = new ListingPlanDraft
        {
            PlanId = plan.Id,
            SourceRevision = plan.Revision,
            Owner = Owner,
            Assignments = [ListingPlanCatalog.Copy(assignment)],
            BaselineAssignments = [ListingPlanCatalog.Copy(assignment)],
        };
        draft.Assignments[0].UnitPrice = 120;
        plan.Assignments[0].ListingCount = 3;
        plan.Revision++;

        var saved = ListingPlanCatalog.Apply(state, Owner, draft, _ => 1, new HashSet<ulong> { 10 }, DateTime.UnixEpoch);
        Assert.Equal(3, saved.Assignments[0].ListingCount);
        Assert.Equal(120, saved.Assignments[0].UnitPrice);

        var collision = new ListingPlanDraft
        {
            PlanId = plan.Id,
            SourceRevision = plan.Revision,
            Owner = Owner,
            Assignments = [ListingPlanCatalog.Copy(plan.Assignments[0])],
            BaselineAssignments = [ListingPlanCatalog.Copy(plan.Assignments[0])],
        };
        collision.Assignments[0].UnitPrice = 130;
        plan.Assignments[0].UnitPrice = 140;
        plan.Revision++;
        var exception = Assert.Throws<ListingPlanConflictException>(() =>
            ListingPlanCatalog.Apply(state, Owner, collision, _ => 1, new HashSet<ulong> { 10 }, DateTime.UnixEpoch));
        Assert.Contains(exception.Conflicts, issue => issue.Field == nameof(ListingPlanAssignment.UnitPrice));
        Assert.Equal(130, collision.Assignments[0].UnitPrice);
        collision.Assignments = exception.RebasedAssignments.Select(ListingPlanCatalog.Copy).ToList();
        collision.BaselineAssignments = plan.Assignments.Select(ListingPlanCatalog.Copy).ToList();
        collision.SourceRevision = plan.Revision;
        var resolved = ListingPlanCatalog.Apply(state, Owner, collision, _ => 1, new HashSet<ulong> { 10 }, DateTime.UnixEpoch);
        Assert.Equal(130, resolved.Assignments.Single().UnitPrice);
    }

    [Fact]
    public void Evaluation_SeparatesExactPriceShapeNeedAndCoverage()
    {
        var plan = new ListingPlan
        {
            Owner = Owner,
            Assignments = [Assignment(1, "Bow", 10, "Retainer", 5, 1, 100)],
        };
        var projection = Projection(
            [Listing(1, "Bow", 10, "Retainer", 1, 100), Listing(1, "Bow", 10, "Retainer", 1, 120), Listing(1, "Bow", 10, "Retainer", 2, 100)],
            [new(BrowserScope.RetainerKey(11), BrowserScopeKind.Retainer, 11, "Other", "Bag", 0, 1, "Bow", 14, FfxivItemQuality.NQ, DateTime.UnixEpoch)],
            inventoryComplete: true,
            listingsComplete: true);

        var item = ListingPlanEvaluator.Evaluate(plan, projection).Items.Single();

        Assert.Equal(5, item.DesiredUnits);
        Assert.Equal(4, item.ListedUnits.Value);
        Assert.Equal(1, item.NeedUnits.Value);
        Assert.Equal(1, item.RetrievableUnits.Value);
        Assert.Equal(0, item.ImmediatelyListableUnits.Value);
        Assert.Equal(1, item.MovementNeedUnits.Value);
        Assert.Equal(ListingCoverageState.Retrievable, item.Coverage);
        Assert.Equal(1, item.Assignments[0].ExactListings);
        Assert.Equal(1, item.Assignments[0].WrongPriceListings);
        Assert.Equal(1, item.Assignments[0].WrongShapeListings);
    }

    [Fact]
    public void Evaluation_KeepsListingAndInventoryUnknownSeparate()
    {
        var plan = new ListingPlan { Owner = Owner, Assignments = [Assignment(1, "Bow", 10, "Retainer", 5, 1, 100)] };
        var unknownInventory = ListingPlanEvaluator.Evaluate(plan, Projection(
            [Listing(1, "Bow", 10, "Retainer", 3, 100)], inventoryComplete: false, listingsComplete: true)).Items.Single();
        Assert.Equal(2, unknownInventory.NeedUnits.Value);
        Assert.False(unknownInventory.RetrievableUnits.IsKnown);

        var unknownListings = ListingPlanEvaluator.Evaluate(plan, Projection(
            [Listing(1, "Bow", 10, "Retainer", 3, 100)], inventoryComplete: true, listingsComplete: false)).Items.Single();
        Assert.False(unknownListings.ListedUnits.IsKnown);
        Assert.False(unknownListings.NeedUnits.IsKnown);
        Assert.True(unknownListings.RetainerUnits.IsKnown);
    }

    [Fact]
    public void Evaluation_ReservesExactMatchesAcrossSiblingPrices()
    {
        var first = Assignment(1, "Ore", 10, "Retainer", 1, 1, 100);
        var second = Assignment(1, "Ore", 10, "Retainer", 1, 1, 200);
        var plan = new ListingPlan { Owner = Owner, Assignments = [first, second] };
        var projection = Projection(
            [Listing(1, "Ore", 10, "Retainer", 1, 200)],
            inventoryComplete: true,
            listingsComplete: true);

        var assignments = ListingPlanEvaluator.Evaluate(plan, projection).Items.Single().Assignments;

        Assert.Equal(0, assignments.Single(row => row.Assignment.Id == first.Id).WrongPriceListings);
        Assert.Equal(1, assignments.Single(row => row.Assignment.Id == second.Id).ExactListings);
    }

    [Fact]
    public void Evaluation_SeparatesAssignedReadinessWrongRetainerAndUnknownPrice()
    {
        var assignment = Assignment(1, "Ore", 10, "Assigned", 2, 1, 100);
        var plan = new ListingPlan { Owner = Owner, Assignments = [assignment] };
        var assignedStock = new StockStack(BrowserScope.RetainerKey(10), BrowserScopeKind.Retainer, 10, "Assigned", "Bag", 0, 1, "Ore", 1, FfxivItemQuality.NQ, DateTime.UnixEpoch);
        var wrongRetainer = ListingPlanEvaluator.Evaluate(plan, Projection(
            [Listing(1, "Ore", 11, "Other", 1, 100), Listing(1, "Ore", 10, "Assigned", 1, null)],
            [assignedStock],
            inventoryComplete: true,
            listingsComplete: true)).Items.Single();

        Assert.Equal(0, wrongRetainer.NeedUnits.Value);
        Assert.Equal(1, wrongRetainer.Assignments.Single().WrongRetainerListings);
        Assert.Equal(1, wrongRetainer.Assignments.Single().UnknownPriceListings);
        Assert.Equal(0, wrongRetainer.Assignments.Single().WrongPriceListings);
        Assert.Empty(wrongRetainer.UnmanagedPhysicalListings);

        var ready = ListingPlanEvaluator.Evaluate(new ListingPlan
        {
            Owner = Owner,
            Assignments = [Assignment(1, "Ore", 10, "Assigned", 2, 1, 100)],
        }, Projection([], [new(BrowserScope.RetainerKey(10), BrowserScopeKind.Retainer, 10, "Assigned", "Bag", 0, 1, "Ore", 2, FfxivItemQuality.NQ, DateTime.UnixEpoch)], true, true)).Items.Single();
        Assert.Equal(2, ready.ImmediatelyListableUnits.Value);
        Assert.Equal(0, ready.MovementNeedUnits.Value);
        Assert.Equal(ListingCoverageState.ReadyOnAssignedRetainer, ready.Coverage);
    }

    [Fact]
    public void RetainerScopedEvaluation_UsesPlayerAndSiblingStockForCoverage()
    {
        var plan = new ListingPlan { Owner = Owner, Assignments = [Assignment(1, "Ore", 10, "Assigned", 2, 1, 100)] };
        var listed = Listing(1, "Ore", 10, "Assigned", 1, 100);
        var player = new StockStack(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Inventory", 0, 1, "Ore", 1, FfxivItemQuality.NQ, DateTime.UnixEpoch);
        var sibling = new StockStack(BrowserScope.RetainerKey(11), BrowserScopeKind.Retainer, 11, "Sibling", "Bag", 0, 1, "Ore", 1, FfxivItemQuality.NQ, DateTime.UnixEpoch);

        var playerCovered = ListingPlanEvaluator.Evaluate(
            plan,
            Projection([listed], [player, sibling], true, true),
            BrowserScope.RetainerKey(10)).Items.Single();
        var siblingCovered = ListingPlanEvaluator.Evaluate(
            plan,
            Projection([listed], [sibling], true, true),
            BrowserScope.RetainerKey(10)).Items.Single();

        Assert.Equal(ListingCoverageState.ReadyOnPlayer, playerCovered.Coverage);
        Assert.Equal(1, playerCovered.PlayerUnits);
        Assert.Equal(ListingCoverageState.Retrievable, siblingCovered.Coverage);
        Assert.Equal(1, siblingCovered.RetrievableUnits.Value);
    }

    [Fact]
    public void Coverage_UsesKnownAssignedAndPlayerStockWhenUnrelatedRetainerIsIncomplete()
    {
        var plan = new ListingPlan { Owner = Owner, Assignments = [Assignment(1, "Ore", 10, "Assigned", 10, 1, 100)] };
        var assigned = new StockStack(BrowserScope.RetainerKey(10), BrowserScopeKind.Retainer, 10, "Assigned", "Bag", 0, 1, "Ore", 5, FfxivItemQuality.NQ, DateTime.UnixEpoch);
        var player = new StockStack(BrowserScope.PlayerKey, BrowserScopeKind.Player, null, "Player", "Inventory", 0, 1, "Ore", 5, FfxivItemQuality.NQ, DateTime.UnixEpoch);
        var unrelated = new StockStack(BrowserScope.RetainerKey(11), BrowserScopeKind.Retainer, 11, "Unknown", "Bag", 0, 2, "Bow", 1, FfxivItemQuality.NQ, DateTime.UnixEpoch);
        var inventoryCompleteness = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [BrowserScope.AllKey] = false,
            [BrowserScope.PlayerKey] = true,
            [BrowserScope.RetainerKey(10)] = true,
            [BrowserScope.RetainerKey(11)] = false,
        };

        var item = ListingPlanEvaluator.Evaluate(plan, Projection(
            [],
            [assigned, player, unrelated],
            listingsComplete: true,
            inventoryCompleteByScope: inventoryCompleteness)).Items.Single(candidate => candidate.ItemId == 1);

        Assert.Equal(5, item.ImmediatelyListableUnits.Value);
        Assert.Equal(5, item.MovementNeedUnits.Value);
        Assert.Equal(ListingCoverageState.ReadyOnPlayer, item.Coverage);
        Assert.Equal(0, item.MissingUnits.Value);
    }

    [Fact]
    public void LinkedContribution_OnlyMovesDemandNotReadyOnAssignedRetainerAndReturnsToBase()
    {
        var listingPlan = new ListingPlan { Owner = Owner, Assignments = [Assignment(1, "Ore", 10, "Assigned", 4, 1, 100)] };
        var transfer = new StowagePlan { Owner = Owner };
        var baseRule = new TargetPlanItem { StowagePlanId = transfer.Id, ItemId = 1, ItemName = "Ore", TargetQuantity = 200, Quality = ItemQualityPolicy.NqOnly };
        var state = new QuartermasterState { ListingPlans = [listingPlan], StowagePlans = [transfer], PlanItems = [baseRule] };
        ListingPlanCatalog.Link(state, Owner, transfer.Id, listingPlan.Id, 1, ItemQualityPolicy.NqOnly);
        var stock = new StockStack(BrowserScope.RetainerKey(10), BrowserScopeKind.Retainer, 10, "Assigned", "Bag", 0, 1, "Ore", 2, FfxivItemQuality.NQ, DateTime.UnixEpoch);

        var needingMovement = ListingPlanEvaluator.ComposeRules(state, Projection([], [stock], true, true), Owner, transfer.Id).Single();
        var fulfilled = ListingPlanEvaluator.ComposeRules(state, Projection(
            [Listing(1, "Ore", 10, "Assigned", 1, 100), Listing(1, "Ore", 10, "Assigned", 1, 100), Listing(1, "Ore", 10, "Assigned", 1, 100), Listing(1, "Ore", 10, "Assigned", 1, 100)],
            [], true, true), Owner, transfer.Id).Single();

        Assert.Equal(202, needingMovement.TargetQuantity);
        Assert.Equal(200, fulfilled.TargetQuantity);
        Assert.Equal(200, baseRule.TargetQuantity);
    }

    [Fact]
    public void Links_AreExactIdempotentReversibleAndDoNotRewriteBaseTarget()
    {
        var listingPlan = new ListingPlan
        {
            Owner = Owner,
            Assignments =
            [
                Assignment(1, "Ore", 10, "Retainer", 2, 1, 100),
                Assignment(1, "Ore", 10, "Retainer", 2, 1, 100, ItemQualityPolicy.HqOnly),
            ],
        };
        var transfer = new StowagePlan { Owner = Owner };
        var baseRule = new TargetPlanItem { StowagePlanId = transfer.Id, ItemId = 1, TargetQuantity = 200, Quality = ItemQualityPolicy.NqOnly };
        var hqBaseRule = new TargetPlanItem { StowagePlanId = transfer.Id, ItemId = 1, TargetQuantity = 0, Quality = ItemQualityPolicy.HqOnly };
        var state = new QuartermasterState { ListingPlans = [listingPlan], StowagePlans = [transfer], PlanItems = [baseRule, hqBaseRule] };

        var nq = ListingPlanCatalog.Link(state, Owner, transfer.Id, listingPlan.Id, 1, ItemQualityPolicy.NqOnly);
        Assert.Same(nq, ListingPlanCatalog.Link(state, Owner, transfer.Id, listingPlan.Id, 1, ItemQualityPolicy.NqOnly));
        ListingPlanCatalog.Link(state, Owner, transfer.Id, listingPlan.Id, 1, ItemQualityPolicy.HqOnly);
        Assert.Equal(2, state.TransferPlanListingLinks.Count);
        Assert.Equal(204, ListingPlanEvaluator.EffectiveTarget(baseRule.TargetQuantity, Evidence.Known(4)).Value);

        Assert.True(ListingPlanCatalog.Unlink(state, Owner, transfer.Id, listingPlan.Id, 1, ItemQualityPolicy.HqOnly));
        Assert.Single(state.TransferPlanListingLinks);
        Assert.Equal(200, baseRule.TargetQuantity);
    }

    private static ListingPlanDraft NewDraft(params ListingPlanAssignment[] assignments) => new()
    {
        PlanId = Guid.NewGuid(),
        IsNew = true,
        Owner = Owner,
        Assignments = assignments.ToList(),
    };

    private static ListingPlanAssignment Assignment(
        uint itemId,
        string name,
        ulong retainerId,
        string retainer,
        int count,
        int quantity,
        int price,
        ItemQualityPolicy quality = ItemQualityPolicy.NqOnly) => new()
    {
        ItemId = itemId,
        ItemName = name,
        RetainerId = retainerId,
        RetainerName = retainer,
        ListingCount = count,
        QuantityPerListing = quantity,
        UnitPrice = price,
        Quality = quality,
    };

    private static ListingRow Listing(uint itemId, string name, ulong retainerId, string retainer, int quantity, uint? price) => new(
        BrowserScope.RetainerKey(retainerId), retainerId, retainer, null, itemId, name, quantity,
        FfxivItemQuality.NQ, Evidence.Unknown<decimal>("Condition unavailable"),
        price is { } knownPrice ? Evidence.Known((decimal)knownPrice) : Evidence.Unknown<decimal>("Price unavailable"),
        price is { } knownTotal ? Evidence.Known((decimal)(knownTotal * quantity)) : Evidence.Unknown<decimal>("Price unavailable"),
        DateTime.UnixEpoch);

    private static BrowserProjection Projection(
        IReadOnlyList<ListingRow> listings,
        IReadOnlyList<StockStack>? stacks = null,
        bool inventoryComplete = true,
        bool listingsComplete = true,
        IReadOnlyDictionary<string, bool>? listingComplete = null,
        IReadOnlyDictionary<string, bool>? inventoryCompleteByScope = null)
    {
        var retainerIds = listings.Select(listing => listing.RetainerId)
            .Concat(stacks?.Where(stack => stack.RetainerId.HasValue).Select(stack => stack.RetainerId!.Value) ?? [])
            .Distinct()
            .ToArray();
        var scopes = new List<BrowserScope>
        {
            new(BrowserScope.AllKey, "All", BrowserScopeKind.All, null),
            new(BrowserScope.PlayerKey, "Player", BrowserScopeKind.Player, null),
        };
        scopes.AddRange(retainerIds.Select(id => new BrowserScope(BrowserScope.RetainerKey(id), $"Retainer {id}", BrowserScopeKind.Retainer, id)));
        var listingMap = listingComplete is null
            ? scopes.ToDictionary(scope => scope.Key, _ => listingsComplete, StringComparer.Ordinal)
            : new Dictionary<string, bool>(listingComplete, StringComparer.Ordinal);
        listingMap.TryAdd(BrowserScope.PlayerKey, true);
        var inventoryMap = inventoryCompleteByScope is null
            ? scopes.ToDictionary(scope => scope.Key, _ => inventoryComplete, StringComparer.Ordinal)
            : new Dictionary<string, bool>(inventoryCompleteByScope, StringComparer.Ordinal);
        inventoryMap[BrowserScope.PlayerKey] = true;
        return new BrowserProjection
        {
            Scopes = scopes,
            Items = BrowserProjection.Aggregate(stacks ?? []),
            Listings = listings,
            Owner = Owner,
            RetainerInventoryCompleteByScope = inventoryMap,
            RetainerListingsCompleteByScope = listingMap,
        };
    }
}
