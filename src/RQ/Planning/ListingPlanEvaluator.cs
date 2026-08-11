using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;

namespace RQ.Planning;

public enum ListingCoverageState
{
    Satisfied,
    ReadyOnAssignedRetainer,
    ReadyOnPlayer,
    Retrievable,
    Missing,
    Unknown,
}

public readonly record struct ListingItemKey(uint ItemId, ItemQualityPolicy Quality);

public sealed record ListingAssignmentEvaluation(
    ListingPlanAssignment Assignment,
    int ExactListings,
    int UnknownPriceListings,
    int WrongPriceListings,
    int WrongShapeListings,
    int WrongRetainerListings);

public sealed record ListingPlanItemEvaluation(
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int DesiredUnits,
    FieldEvidence<int> ListedUnits,
    FieldEvidence<int> NeedUnits,
    int PlayerUnits,
    FieldEvidence<int> RetainerUnits,
    FieldEvidence<int> ImmediatelyListableUnits,
    FieldEvidence<int> MovementNeedUnits,
    FieldEvidence<int> OtherRetainerUnits,
    FieldEvidence<int> RetrievableUnits,
    FieldEvidence<int> MissingUnits,
    ListingCoverageState Coverage,
    IReadOnlyList<ListingAssignmentEvaluation> Assignments,
    IReadOnlyList<ListingRow> PhysicalListings,
    IReadOnlyList<ListingRow> UnmanagedPhysicalListings)
{
    public bool IsPlanned => Assignments.Count != 0;
    public int PriceExceptions => Assignments.Sum(assignment => assignment.WrongPriceListings);
    public int ShapeExceptions => Assignments.Sum(assignment => assignment.WrongShapeListings);
    public int RetainerExceptions => Assignments.Sum(assignment => assignment.WrongRetainerListings);
}

public sealed record ListingPlanEvaluation(
    Guid? PlanId,
    long PlanRevision,
    IReadOnlyList<ListingPlanItemEvaluation> Items)
{
    public ListingPlanItemEvaluation? Find(uint itemId, ItemQualityPolicy quality) =>
        Items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == quality);
}

public sealed record TransferPlanListingContribution(
    TransferPlanListingLink Link,
    FieldEvidence<int> Quantity);

public static class ListingPlanEvaluator
{
    public static ListingPlanEvaluation Evaluate(ListingPlan? plan, BrowserProjection projection, string? scopeKey = null)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scopeKey) ? BrowserScope.AllKey : scopeKey;
        var scope = projection.Scopes.FirstOrDefault(candidate => candidate.Key == normalizedScope);
        var scopeRetainerId = scope?.RetainerId;
        var assignments = plan?.Assignments
            .Where(assignment => assignment.Enabled && scope?.Kind != BrowserScopeKind.Player &&
                                 (scopeRetainerId is null || assignment.RetainerId == scopeRetainerId))
            .ToArray() ?? [];
        var scopedListings = projection.GetListings(normalizedScope);
        var keys = assignments.Select(assignment => (assignment.ItemId, assignment.Quality))
            .Concat(scopedListings.Select(listing =>
                (ItemId: listing.ItemId, Quality: listing.Quality == FfxivItemQuality.HQ
                    ? ItemQualityPolicy.HqOnly
                    : ItemQualityPolicy.NqOnly)))
            .Distinct()
            .ToArray();
        var listingComplete = projection.RetainerListingsCompleteByScope.GetValueOrDefault(normalizedScope);
        var inventoryComplete = projection.RetainerInventoryCompleteByScope.GetValueOrDefault(BrowserScope.AllKey);
        var items = keys.Select(key => EvaluateItem(key.ItemId, key.Quality, assignments, projection, scopedListings, listingComplete, inventoryComplete))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Quality)
            .ToArray();
        return new(plan?.Id, plan?.Revision ?? 0, items);
    }

    public static IReadOnlyList<TransferPlanListingContribution> Contributions(
        QuartermasterState state,
        Guid stowagePlanId,
        ListingPlanEvaluation evaluation) =>
        state.TransferPlanListingLinks
            .Where(link => link.StowagePlanId == stowagePlanId && link.ListingPlanId == evaluation.PlanId)
            .Select(link => new TransferPlanListingContribution(
                link,
                evaluation.Find(link.ItemId, link.Quality)?.MovementNeedUnits ??
                Evidence.Known(0)))
            .ToArray();

    public static FieldEvidence<int> EffectiveTarget(
        int independentTarget,
        FieldEvidence<int> listingContribution) =>
        listingContribution.IsKnown
            ? Evidence.Known(checked(Math.Max(0, independentTarget) + listingContribution.Value))
            : Evidence.Unknown<int>(listingContribution.UnknownReason ?? "Listing demand is unknown.");

    public static IReadOnlyList<TargetPlanItem> ComposeRules(
        QuartermasterState state,
        BrowserProjection projection,
        OwnerScope owner,
        Guid stowagePlanId)
    {
        var evaluation = Evaluate(ListingPlanCatalog.OwnerPlan(state, owner), projection);
        var contributions = Contributions(state, stowagePlanId, evaluation)
            .ToDictionary(contribution => (contribution.Link.ItemId, contribution.Link.Quality));
        return state.PlanItems
            .Where(rule => rule.StowagePlanId == stowagePlanId)
            .Select(rule =>
            {
                var copy = StowagePlanCatalog.CopyRule(rule, stowagePlanId, newIdentity: false);
                if (contributions.TryGetValue((rule.ItemId, rule.Quality), out var contribution) && contribution.Quantity.IsKnown)
                    copy.TargetQuantity = checked(Math.Max(0, rule.TargetQuantity) + contribution.Quantity.Value);
                return copy;
            })
            .ToArray();
    }

    public static IReadOnlyList<TargetPlanItem> ComposeOwnerRules(
        QuartermasterState state,
        BrowserProjection projection,
        OwnerScope owner)
    {
        var enabledPlanIds = state.StowagePlans
            .Where(plan => plan.Enabled && plan.Owner.Matches(owner))
            .Select(plan => plan.Id)
            .ToHashSet();
        return enabledPlanIds.SelectMany(planId => ComposeRules(state, projection, owner, planId)).ToArray();
    }

    public static bool HasUnknownLinkedDemand(
        QuartermasterState state,
        BrowserProjection projection,
        OwnerScope owner,
        Guid stowagePlanId)
    {
        var evaluation = Evaluate(ListingPlanCatalog.OwnerPlan(state, owner), projection);
        return Contributions(state, stowagePlanId, evaluation).Any(contribution => !contribution.Quantity.IsKnown);
    }

    private static ListingPlanItemEvaluation EvaluateItem(
        uint itemId,
        ItemQualityPolicy quality,
        IReadOnlyList<ListingPlanAssignment> allAssignments,
        BrowserProjection projection,
        IReadOnlyList<ListingRow> scopedListings,
        bool listingComplete,
        bool inventoryComplete)
    {
        var assignments = allAssignments
            .Where(assignment => assignment.ItemId == itemId && assignment.Quality == quality)
            .OrderBy(assignment => assignment.RetainerId)
            .ThenBy(assignment => assignment.Id)
            .ToArray();
        var physical = scopedListings
            .Where(listing => listing.ItemId == itemId && ListingQuality(listing) == quality)
            .OrderBy(listing => listing.RetainerId)
            .ThenBy(listing => listing.SlotIndex)
            .ToArray();
        var desiredUnits = assignments.Sum(assignment => assignment.DesiredUnits);
        var listed = listingComplete
            ? Evidence.Known(physical.Sum(listing => listing.Quantity))
            : Evidence.Unknown<int>("Listings have not been observed for every retainer.");
        var need = listed.IsKnown
            ? Evidence.Known(Math.Max(0, desiredUnits - listed.Value))
            : Evidence.Unknown<int>(listed.UnknownReason!);
        var itemName = assignments.Select(assignment => assignment.ItemName)
            .Concat(physical.Select(listing => listing.ItemName))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {itemId}";
        var targetQuality = quality == ItemQualityPolicy.HqOnly ? FfxivItemQuality.HQ : FfxivItemQuality.NQ;
        // Listing scope determines the assignment/physical comparison, but
        // coverage always searches all accessible stock. A retainer-focused
        // deficit may be ready on the player or retrievable from a sibling.
        var stock = projection.GetItems(BrowserScope.AllKey).FirstOrDefault(item => item.ItemId == itemId)?.Stacks ?? [];
        var playerUnits = stock.Where(stack => stack.ScopeKind == BrowserScopeKind.Player && stack.Quality == targetQuality)
            .Sum(stack => stack.Quantity);
        var retainerQuantity = stock.Where(stack => stack.ScopeKind == BrowserScopeKind.Retainer && stack.Quality == targetQuality)
            .Sum(stack => stack.Quantity);
        var retainerUnits = inventoryComplete
            ? Evidence.Known(retainerQuantity)
            : Evidence.Unknown<int>("Retainer inventory has not been observed for every retainer.");
        var assignedInventoryComplete = assignments
            .Select(assignment => BrowserScope.RetainerKey(assignment.RetainerId))
            .Distinct(StringComparer.Ordinal)
            .All(scope => projection.RetainerInventoryCompleteByScope.GetValueOrDefault(scope));
        var immediatelyListable = assignedInventoryComplete
            ? Evidence.Known(assignments
                .GroupBy(assignment => assignment.RetainerId)
                .Sum(retainer =>
                {
                    var desired = retainer.Sum(assignment => assignment.DesiredUnits);
                    var alreadyListed = physical.Where(listing => listing.RetainerId == retainer.Key).Sum(listing => listing.Quantity);
                    var available = stock.Where(stack => stack.ScopeKind == BrowserScopeKind.Retainer &&
                                                         stack.RetainerId == retainer.Key && stack.Quality == targetQuality)
                        .Sum(stack => stack.Quantity);
                    return Math.Min(Math.Max(0, desired - alreadyListed), available);
                }))
            : Evidence.Unknown<int>("Assigned-retainer inventory has not been observed completely.");
        var movementNeed = need.IsKnown && immediatelyListable.IsKnown
            ? Evidence.Known(Math.Max(0, need.Value - immediatelyListable.Value))
            : Evidence.Unknown<int>(need.UnknownReason ?? immediatelyListable.UnknownReason ?? "Listing movement demand is unknown.");
        var otherRetainerUnits = retainerUnits.IsKnown && immediatelyListable.IsKnown
            ? Evidence.Known(Math.Max(0, retainerUnits.Value - immediatelyListable.Value))
            : Evidence.Unknown<int>(retainerUnits.UnknownReason ?? immediatelyListable.UnknownReason ?? "Other-retainer inventory is unknown.");
        FieldEvidence<int> retrievable;
        FieldEvidence<int> missing;
        ListingCoverageState coverage;
        if (!need.IsKnown)
        {
            retrievable = Evidence.Unknown<int>(need.UnknownReason!);
            missing = Evidence.Unknown<int>(need.UnknownReason!);
            coverage = ListingCoverageState.Unknown;
        }
        else if (need.Value == 0)
        {
            retrievable = Evidence.Known(0);
            missing = Evidence.Known(0);
            coverage = ListingCoverageState.Satisfied;
        }
        else if (immediatelyListable.IsKnown && immediatelyListable.Value >= need.Value)
        {
            retrievable = Evidence.Known(0);
            missing = Evidence.Known(0);
            coverage = ListingCoverageState.ReadyOnAssignedRetainer;
        }
        else if (!movementNeed.IsKnown || !otherRetainerUnits.IsKnown)
        {
            retrievable = Evidence.Unknown<int>(retainerUnits.UnknownReason!);
            if (movementNeed.IsKnown && playerUnits >= movementNeed.Value)
            {
                missing = Evidence.Known(0);
                coverage = ListingCoverageState.ReadyOnPlayer;
            }
            else
            {
                missing = Evidence.Unknown<int>(retainerUnits.UnknownReason!);
                coverage = ListingCoverageState.Unknown;
            }
        }
        else
        {
            var remainingAfterPlayer = Math.Max(0, movementNeed.Value - playerUnits);
            retrievable = Evidence.Known(Math.Min(remainingAfterPlayer, otherRetainerUnits.Value));
            missing = Evidence.Known(Math.Max(0, remainingAfterPlayer - otherRetainerUnits.Value));
            coverage = missing.Value > 0
                ? ListingCoverageState.Missing
                : retrievable.Value > 0
                    ? ListingCoverageState.Retrievable
                    : ListingCoverageState.ReadyOnPlayer;
        }

        var matches = MatchAssignments(assignments, physical);
        return new(
            itemId,
            itemName,
            quality,
            desiredUnits,
            listed,
            need,
            playerUnits,
            retainerUnits,
            immediatelyListable,
            movementNeed,
            otherRetainerUnits,
            retrievable,
            missing,
            coverage,
            matches.Assignments,
            physical,
            matches.UnmanagedListings);
    }

    private sealed record ListingMatchResult(
        IReadOnlyList<ListingAssignmentEvaluation> Assignments,
        IReadOnlyList<ListingRow> UnmanagedListings);

    private static ListingMatchResult MatchAssignments(
        IReadOnlyList<ListingPlanAssignment> assignments,
        IReadOnlyList<ListingRow> physical)
    {
        var consumed = new bool[physical.Count];
        var exact = new int[assignments.Count];
        var unknownPrice = new int[assignments.Count];
        var wrongPrice = new int[assignments.Count];
        var wrongShape = new int[assignments.Count];
        var wrongRetainer = new int[assignments.Count];

        // Reserve every exact match before classifying exceptions. Otherwise an
        // earlier sibling row could steal a later row's exact listing as a
        // price exception when one item/retainer intentionally has two prices.
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            exact[index] = Consume(assignment.ListingCount, physical, consumed, listing =>
                listing.RetainerId == assignment.RetainerId &&
                listing.Quantity == assignment.QuantityPerListing &&
                listing.UnitPrice.IsKnown && listing.UnitPrice.Value == assignment.UnitPrice);
        }
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            unknownPrice[index] = Consume(assignment.ListingCount - exact[index], physical, consumed, listing =>
                listing.RetainerId == assignment.RetainerId &&
                listing.Quantity == assignment.QuantityPerListing &&
                !listing.UnitPrice.IsKnown);
        }
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            wrongPrice[index] = Consume(assignment.ListingCount - exact[index] - unknownPrice[index], physical, consumed, listing =>
                listing.RetainerId == assignment.RetainerId &&
                listing.Quantity == assignment.QuantityPerListing &&
                listing.UnitPrice.IsKnown);
        }
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            wrongShape[index] = Consume(assignment.ListingCount - exact[index] - unknownPrice[index] - wrongPrice[index], physical, consumed, listing =>
                listing.RetainerId == assignment.RetainerId);
        }
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            wrongRetainer[index] = Consume(
                assignment.ListingCount - exact[index] - unknownPrice[index] - wrongPrice[index] - wrongShape[index],
                physical,
                consumed,
                listing => listing.RetainerId != assignment.RetainerId);
        }
        var evaluations = assignments.Select((assignment, index) => new ListingAssignmentEvaluation(
            assignment,
            exact[index],
            unknownPrice[index],
            wrongPrice[index],
            wrongShape[index],
            wrongRetainer[index])).ToArray();
        var unmanaged = physical.Where((_, index) => !consumed[index]).ToArray();
        return new(evaluations, unmanaged);
    }

    private static int Consume(
        int limit,
        IReadOnlyList<ListingRow> rows,
        bool[] consumed,
        Func<ListingRow, bool> predicate)
    {
        var count = 0;
        for (var index = 0; index < rows.Count && count < limit; index++)
        {
            if (consumed[index] || !predicate(rows[index]))
                continue;
            consumed[index] = true;
            count++;
        }
        return count;
    }

    private static ItemQualityPolicy ListingQuality(ListingRow listing) =>
        listing.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
}
