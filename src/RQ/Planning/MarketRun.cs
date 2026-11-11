using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;

namespace RQ.Planning;

/// <summary>
/// One enabled assignment as the market run sees it. Listings with the planned shape but a
/// different or unobserved price already hold the right stock, so they are counted as
/// <see cref="RepriceListings"/> rather than short.
/// </summary>
public sealed record MarketRunAssignment(
    ListingPlanAssignment Assignment,
    int PlannedListings,
    FieldEvidence<int> ExactListings,
    FieldEvidence<int> RepriceListings,
    FieldEvidence<int> ShortListings,
    FieldEvidence<int> RefillListings,
    FieldEvidence<int> UnfilledListings,
    FieldEvidence<int> BlockedBySlotsListings);

public sealed record MarketRunPullListing(ListingRow Listing, int ObservationIndex);

public sealed record MarketRunRetainer(
    ulong RetainerId,
    string RetainerName,
    bool ListingsComplete,
    IReadOnlyList<MarketRunAssignment> Assignments,
    int PlannedListings,
    FieldEvidence<int> ExactListings,
    FieldEvidence<int> ShortListings,
    FieldEvidence<int> RefillListings,
    FieldEvidence<int> UnfilledListings,
    FieldEvidence<int> ObservedListingCount,
    FieldEvidence<int> FreeSlots,
    FieldEvidence<int> PullListings,
    FieldEvidence<int> BlockedBySlotsListings,
    IReadOnlyList<MarketRunPullListing> ListingsToPull);

public sealed record MarketRunItem(
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int PlannedUnits,
    FieldEvidence<int> ListedUnits,
    FieldEvidence<int> SpareUnits,
    FieldEvidence<int> RefillUnits,
    FieldEvidence<int> UnfilledUnits,
    ListingReserveRule? Reserve,
    int ReserveUnits,
    FieldEvidence<int> ReserveAfterRun,
    FieldEvidence<int> CraftUnits);

public sealed record MarketRunCraftLine(uint ItemId, string ItemName, ItemQualityPolicy Quality, int Quantity);

public sealed record MarketRunEvaluation(
    Guid? PlanId,
    long PlanRevision,
    IReadOnlyList<MarketRunRetainer> Retainers,
    IReadOnlyList<MarketRunItem> Items,
    IReadOnlyList<MarketRunCraftLine> CraftQueue)
{
    /// <summary>False when any listing or stock number the run depends on is unknown.</summary>
    public bool IsComplete =>
        Retainers.All(retainer => retainer.ListingsComplete) &&
        Items.All(item => item.CraftUnits.IsKnown);

    public MarketRunItem? Find(uint itemId, ItemQualityPolicy quality) =>
        Items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == quality);
}

/// <summary>
/// Compares the owner's Listing Plan with observed listings and accessible stock, works out which
/// short listings spare stock can refill, and what must be crafted to restore each reserve. The plan
/// is the only authority: items and retainers without enabled plan entries are ignored.
/// </summary>
public static class MarketRunPlanner
{
    public const int ListingCapacity = 20;

    /// <summary>
    /// Selects pulls from one retainer's complete scan. ObservationIndex is relative to that
    /// scan; callers must reselect against live observations rather than reuse stale slot identities.
    /// </summary>
    public static IReadOnlyList<MarketRunPullListing> SelectListingsToPull(
        IReadOnlyList<ListingRow> observedListings,
        IReadOnlyList<ListingPlanAssignment> enabledAssignments,
        int roomNeeded)
    {
        if (roomNeeded <= 0) return [];
        var matches = ListingPlanEvaluator.MatchListings(enabledAssignments, observedListings);
        var planned = matches.Where(match => match.Kind is ListingMatchKind.Exact or ListingMatchKind.UnknownPrice or ListingMatchKind.PriceDiffers)
            .Select(match => match.Listing).ToHashSet(ReferenceEqualityComparer.Instance);
        var needed = Math.Max(0, roomNeeded - (ListingCapacity - observedListings.Count));
        // Pull unknown-price listings first, then lowest unit price × quantity; ties use
        // slot index (missing slots last), then original observation order.
        return observedListings.Select((listing, index) => new MarketRunPullListing(listing, index))
            .Where(row => !planned.Contains(row.Listing))
            .OrderBy(row => row.Listing.UnitPrice.IsKnown ? 1 : 0)
            .ThenBy(row => row.Listing.UnitPrice.IsKnown ? row.Listing.UnitPrice.Value * row.Listing.Quantity : 0)
            .ThenBy(row => row.Listing.SlotIndex ?? int.MaxValue)
            .ThenBy(row => row.ObservationIndex)
            .Take(needed).ToArray();
    }

    public static MarketRunEvaluation Evaluate(ListingPlan? plan, BrowserProjection projection)
    {
        if (plan is null)
            return new(null, 0, [], [], []);

        var listingEvaluation = ListingPlanEvaluator.Evaluate(plan, projection);
        var matchByAssignment = listingEvaluation.Items
            .SelectMany(item => item.Assignments)
            .ToDictionary(match => match.Assignment.Id);

        // Allocation order when spare stock cannot refill every short listing: retainer name
        // (case-insensitive), then retainer id, then the assignment's order in the plan. Earlier
        // assignments are refilled first, one whole listing of QuantityPerListing at a time.
        var assignments = plan.Assignments
            .Select((assignment, index) => (Assignment: assignment, Index: index))
            .Where(entry => entry.Assignment.Enabled)
            .OrderBy(entry => entry.Assignment.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Assignment.RetainerId)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Assignment)
            .ToArray();

        var counted = assignments.ToDictionary(assignment => assignment.Id, assignment =>
            Count(assignment, matchByAssignment.GetValueOrDefault(assignment.Id), ListingsComplete(projection, assignment.RetainerId)));

        var reserves = plan.Reserves
            .Where(reserve => reserve.Enabled)
            .GroupBy(reserve => new ListingItemKey(reserve.ItemId, reserve.Quality))
            .ToDictionary(group => group.Key, group => group.First());
        var keys = assignments.Select(assignment => new ListingItemKey(assignment.ItemId, assignment.Quality))
            .Concat(reserves.Keys)
            .Distinct()
            .ToArray();

        var refills = new Dictionary<Guid, (FieldEvidence<int> Refill, FieldEvidence<int> Unfilled, FieldEvidence<int> Blocked)>();
        var availableSlots = assignments.GroupBy(a => a.RetainerId).ToDictionary(g => g.Key, g =>
        {
            var observed = projection.Listings.Where(l => l.RetainerId == g.Key).ToArray();
            return Math.Max(0, ListingCapacity - observed.Length +
                SelectListingsToPull(observed, g.ToArray(), observed.Length + ListingCapacity).Count);
        });
        var spares = keys.ToDictionary(key => key, key => SpareUnits(projection, key));
        var remainingStock = keys.ToDictionary(key => key, key => spares[key].IsKnown ? spares[key].Value : 0);
        // Allocation is sequential, so one unknown assignment leaves two things unknown for every
        // later assignment: how much of that item's stock is left, and how much slot room its
        // retainer has left. Later assignments that depend on either are unknown too.
        const string unobserved = "Listings have not been observed for every planned retainer.";
        var uncertainStock = keys.Where(key => !spares[key].IsKnown)
            .ToDictionary(key => key, key => spares[key].UnknownReason ?? unobserved);
        var uncertainSlots = new Dictionary<ulong, string>();
        foreach (var assignment in assignments)
        {
            var key = new ListingItemKey(assignment.ItemId, assignment.Quality);
            var shortCount = counted[assignment.Id].Short;
            var reason = !shortCount.IsKnown ? shortCount.UnknownReason ?? unobserved
                : uncertainStock.GetValueOrDefault(key) ?? uncertainSlots.GetValueOrDefault(assignment.RetainerId);
            if (reason is not null)
            {
                uncertainStock.TryAdd(key, reason);
                uncertainSlots.TryAdd(assignment.RetainerId, reason);
                var unknown = Evidence.Unknown<int>(reason);
                refills[assignment.Id] = (unknown, unknown, unknown);
                continue;
            }
            var shortListings = counted[assignment.Id].Short.Value;
            var quantity = Math.Max(0, assignment.QuantityPerListing);
            var stockBacked = quantity == 0 ? 0 : Math.Min(shortListings, remainingStock[key] / quantity);
            var refill = Math.Min(stockBacked, availableSlots[assignment.RetainerId]);
            availableSlots[assignment.RetainerId] -= refill;
            remainingStock[key] -= refill * quantity;
            refills[assignment.Id] = (Evidence.Known(refill), Evidence.Known(shortListings - stockBacked),
                Evidence.Known(stockBacked - refill));
        }
        var items = new List<MarketRunItem>();
        foreach (var key in keys)
        {
            var itemAssignments = assignments.Where(assignment => assignment.ItemId == key.ItemId && assignment.Quality == key.Quality).ToArray();
            var spare = spares[key];
            var refillUnits = 0;
            var unfilledUnits = 0;
            var known = !uncertainStock.ContainsKey(key);
            if (known)
            {
                foreach (var assignment in itemAssignments)
                {
                    var perListing = assignment.QuantityPerListing;
                    var refill = refills[assignment.Id].Refill.Value;
                    refillUnits += refill * Math.Max(0, perListing);
                    unfilledUnits += refills[assignment.Id].Unfilled.Value * Math.Max(0, perListing);
                }
            }

            var unknownReason = uncertainStock.GetValueOrDefault(key) ?? unobserved;
            var reserve = reserves.GetValueOrDefault(key);
            var reserveUnits = reserve?.ReserveUnits ?? 0;
            var reserveAfterRun = known ? Evidence.Known(spare.Value - refillUnits) : Evidence.Unknown<int>(unknownReason);
            var listingItem = listingEvaluation.Find(key.ItemId, key.Quality);
            items.Add(new(
                key.ItemId,
                ItemName(key, itemAssignments, reserve, listingItem, projection),
                key.Quality,
                itemAssignments.Sum(assignment => assignment.DesiredUnits),
                listingItem?.ListedUnits ?? ListedUnits(projection, key),
                spare,
                known ? Evidence.Known(refillUnits) : Evidence.Unknown<int>(unknownReason),
                known ? Evidence.Known(unfilledUnits) : Evidence.Unknown<int>(unknownReason),
                reserve,
                reserveUnits,
                reserveAfterRun,
                known
                    ? Evidence.Known(unfilledUnits + Math.Max(0, reserveUnits - reserveAfterRun.Value))
                    : Evidence.Unknown<int>(unknownReason)));
        }

        var retainers = assignments
            .GroupBy(assignment => assignment.RetainerId)
            .Select(group =>
            {
                var rows = group.Select(assignment =>
                {
                    var count = counted[assignment.Id];
                    var refill = refills[assignment.Id];
                    return new MarketRunAssignment(assignment, assignment.ListingCount, count.Exact, count.Reprice,
                        count.Short, refill.Refill, refill.Unfilled, refill.Blocked);
                }).ToArray();
                var name = group.Select(assignment => assignment.RetainerName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                           $"Retainer {group.Key}";
                var complete = ListingsComplete(projection, group.Key);
                var observed = projection.Listings.Where(l => l.RetainerId == group.Key).ToArray();
                var refillTotal = Sum(rows.Select(row => row.RefillListings));
                var blockedTotal = Sum(rows.Select(row => row.BlockedBySlotsListings));
                var pulls = complete && refillTotal.IsKnown && blockedTotal.IsKnown
                    ? SelectListingsToPull(observed, group.ToArray(), refillTotal.Value + blockedTotal.Value) : [];
                var unknown = Evidence.Unknown<int>($"Listings for {name} have not been observed.");
                return new MarketRunRetainer(
                    group.Key,
                    name,
                    ListingsComplete(projection, group.Key),
                    rows,
                    rows.Sum(row => row.PlannedListings),
                    Sum(rows.Select(row => row.ExactListings)),
                    Sum(rows.Select(row => row.ShortListings)),
                    Sum(rows.Select(row => row.RefillListings)),
                    Sum(rows.Select(row => row.UnfilledListings)),
                    complete ? Evidence.Known(observed.Length) : unknown,
                    complete ? Evidence.Known(ListingCapacity - observed.Length) : unknown,
                    !complete ? unknown : refillTotal.IsKnown && blockedTotal.IsKnown ? Evidence.Known(pulls.Count) : refillTotal,
                    !complete ? unknown : blockedTotal,
                    pulls);
            })
            .ToArray();

        var orderedItems = items
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Quality)
            .ThenBy(item => item.ItemId)
            .ToArray();
        var craftQueue = orderedItems
            .Where(item => item.CraftUnits is { IsKnown: true, Value: > 0 })
            .Select(item => new MarketRunCraftLine(item.ItemId, item.ItemName, item.Quality, item.CraftUnits.Value))
            .ToArray();
        return new(plan.Id, plan.Revision, retainers, orderedItems, craftQueue);
    }

    private static (FieldEvidence<int> Exact, FieldEvidence<int> Reprice, FieldEvidence<int> Short) Count(
        ListingPlanAssignment assignment,
        ListingAssignmentEvaluation? match,
        bool listingsComplete)
    {
        if (!listingsComplete)
        {
            var unknown = Evidence.Unknown<int>($"Listings for {assignment.RetainerName} have not been observed.");
            return (unknown, unknown, unknown);
        }
        var exact = match?.ExactListings ?? 0;
        var reprice = (match?.UnknownPriceListings ?? 0) + (match?.WrongPriceListings ?? 0);
        return (Evidence.Known(exact), Evidence.Known(reprice),
            Evidence.Known(Math.Max(0, assignment.ListingCount - exact - reprice)));
    }

    // Same accessible stock ListingPlanEvaluator counts: player bags plus retainer inventories
    // (listed stock is excluded because listings are not inventory stacks).
    private static FieldEvidence<int> SpareUnits(BrowserProjection projection, ListingItemKey key)
    {
        if (!projection.RetainerInventoryCompleteByScope.GetValueOrDefault(BrowserScope.AllKey))
            return Evidence.Unknown<int>("Retainer inventory has not been observed for every retainer.");
        var quality = key.Quality == ItemQualityPolicy.HqOnly ? FfxivItemQuality.HQ : FfxivItemQuality.NQ;
        var stacks = projection.GetItems(BrowserScope.AllKey).FirstOrDefault(item => item.ItemId == key.ItemId)?.Stacks ?? [];
        return Evidence.Known(stacks
            .Where(stack => stack.ScopeKind is BrowserScopeKind.Player or BrowserScopeKind.Retainer && stack.Quality == quality)
            .Sum(stack => stack.Quantity));
    }

    private static FieldEvidence<int> ListedUnits(BrowserProjection projection, ListingItemKey key)
    {
        if (!projection.RetainerListingsCompleteByScope.GetValueOrDefault(BrowserScope.AllKey))
            return Evidence.Unknown<int>("Listings have not been observed for every retainer.");
        var quality = key.Quality == ItemQualityPolicy.HqOnly ? FfxivItemQuality.HQ : FfxivItemQuality.NQ;
        return Evidence.Known(projection.Listings
            .Where(listing => listing.ItemId == key.ItemId && listing.Quality == quality)
            .Sum(listing => listing.Quantity));
    }

    private static bool ListingsComplete(BrowserProjection projection, ulong retainerId) =>
        projection.RetainerListingsCompleteByScope.GetValueOrDefault(BrowserScope.RetainerKey(retainerId));

    private static string ItemName(
        ListingItemKey key,
        IReadOnlyList<ListingPlanAssignment> assignments,
        ListingReserveRule? reserve,
        ListingPlanItemEvaluation? listingItem,
        BrowserProjection projection) =>
        assignments.Select(assignment => assignment.ItemName)
            .Append(reserve?.ItemName)
            .Append(listingItem?.ItemName)
            .Append(projection.GetItems(BrowserScope.AllKey).FirstOrDefault(item => item.ItemId == key.ItemId)?.ItemName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {key.ItemId}";

    private static FieldEvidence<int> Sum(IEnumerable<FieldEvidence<int>> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            if (!value.IsKnown)
                return Evidence.Unknown<int>(value.UnknownReason ?? "Part of this total is unknown.");
            total += value.Value;
        }
        return Evidence.Known(total);
    }
}
