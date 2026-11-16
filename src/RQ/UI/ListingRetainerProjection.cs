using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal enum ListingSlotSource { Planned, Observed, Free }
/// <summary>Stable slot identity: assignment + ordinal, an off-plan listing's game slot, or a free-row ordinal.</summary>
internal readonly record struct ListingSlotKey(ListingSlotSource Source, Guid AssignmentId, int Index)
{
    public static ListingSlotKey Planned(Guid assignmentId, int ordinal) => new(ListingSlotSource.Planned, assignmentId, ordinal);
    public static ListingSlotKey Observed(int slotIndex) => new(ListingSlotSource.Observed, Guid.Empty, slotIndex);
    public static ListingSlotKey Free(int ordinal) => new(ListingSlotSource.Free, Guid.Empty, ordinal);
}
internal enum ListingSlotListed { None, Match, UnknownPrice, PriceDiffers, ShapeDiffers, NotListed, Other }
/// <summary>One retainer market slot. Number is 1-based for capacity rows and 0 for excluded plan rows.</summary>
internal sealed record ListingSlotView(ListingSlotKey Key, int Number, ListingPlanAssignment? Assignment,
    ListingRow? Listing, ListingSlotListed Listed, bool Continuation, bool GroupStart);
internal enum ListingStateTone { Dim, Amber, Green, Info }
internal enum ListingStateAction { None, FocusStock }
internal sealed record ListingStateView(string Text, ListingStateTone Tone, int Rank,
    ListingStateAction Action = ListingStateAction.None, int Amount = 0);
internal sealed record ListingPlanRowView(ListingPlanAssignment Assignment, ListingStateView State, int UnlistedCount);
internal sealed record ListingObservedGroupView(ListingMatchKind Kind, Guid? AssignmentId, uint ItemId,
    string ItemName, ItemQualityPolicy Quality, int Quantity, FieldEvidence<decimal> UnitPrice,
    IReadOnlyList<ListingRow> Listings, ListingStateView State);
internal sealed record ListingRetainerRow(ulong RetainerId, string RetainerName, bool Available,
    bool ListingsObserved, IReadOnlyList<ListingSlotView> Slots, int PlannedSlots, int UnplannedSlots,
    ListingStateView State, IReadOnlyList<ListingPlanRowView> PlanRows,
    IReadOnlyList<ListingObservedGroupView> ObservedGroups);

internal static class ListingRetainerProjection
{
    public static bool Complete(ListingPlanAssignment a) => a.ListingCount > 0 && a.QuantityPerListing > 0 && a.UnitPrice > 0;
    public static ItemQualityPolicy Quality(ListingRow row) => row.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;

    public static IReadOnlyList<ListingRetainerRow> Build(ListingPlanDraft draft, BrowserProjection browser,
        IReadOnlySet<ulong> currentRetainerIds, Func<uint, bool?> listability,
        IReadOnlySet<uint>? savedDemandItemIds = null)
    {
        var scopes = browser.Scopes.Where(s => s.Kind == BrowserScopeKind.Retainer && s.RetainerId.HasValue).ToList();
        var known = scopes.Select(s => s.RetainerId!.Value).ToHashSet();
        foreach (var a in draft.Assignments)
            if (known.Add(a.RetainerId)) scopes.Add(new(BrowserScope.RetainerKey(a.RetainerId), a.RetainerName, BrowserScopeKind.Retainer, a.RetainerId));
        var destinations = browser.Items.Select(i => i.ItemId).Concat(savedDemandItemIds ?? new HashSet<uint>()).ToHashSet();
        var rows = scopes.Select(scope => BuildRetainer(draft, browser, scope, currentRetainerIds.Contains(scope.RetainerId!.Value),
            listability, destinations)).ToArray();
        ApplySharedCoverage(rows, draft, browser, currentRetainerIds, listability);
        return rows;
    }

    private static void ApplySharedCoverage(ListingRetainerRow[] rows, ListingPlanDraft draft, BrowserProjection browser,
        IReadOnlySet<ulong> currentRetainerIds, Func<uint, bool?> listability)
    {
        var enabled = draft.Assignments.Where(a => a.Enabled && Complete(a) &&
            currentRetainerIds.Contains(a.RetainerId) && listability(a.ItemId) is not false).ToList();
        var shared = enabled.GroupBy(a => new ListingItemKey(a.ItemId, a.Quality))
            .Where(g => g.Select(a => a.RetainerId).Distinct().Skip(1).Any()).Select(g => g.Key).ToHashSet();
        if (shared.Count == 0) return;
        var coverage = ListingPlanEvaluator.Evaluate(new ListingPlan { Owner = draft.Owner, Assignments = enabled }, browser);
        var carried = new HashSet<ListingItemKey>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var plans = row.PlanRows.Select(p =>
            {
                var key = new ListingItemKey(p.Assignment.ItemId, p.Assignment.Quality);
                if (!shared.Contains(key) || p.State.Rank is < 1 or > 4) return p;
                var item = coverage.Find(key.ItemId, key.Quality)!;
                ListingStateView state;
                // Local matching and stock already held on the destination remain local.
                // Movement/shortage is one full-draft fact, never a second claim on shared stock.
                if (carried.Add(key))
                {
                    var rank = item.Coverage switch
                    {
                        ListingCoverageState.Missing => 1, ListingCoverageState.Retrievable => 2,
                        ListingCoverageState.ReadyOnPlayer => 3, ListingCoverageState.Unknown => 4, _ => 7,
                    };
                    var amount = rank switch
                    {
                        1 => item.MissingUnits.Value, 2 => item.RetrievableUnits.Value,
                        3 => item.MovementNeedUnits.Value, _ => item.NeedUnits.IsKnown ? item.NeedUnits.Value : p.State.Amount,
                    };
                    state = rank == 7 ? ObservedState(ListingMatchKind.ShapeDiffers) :
                        new(AmountText(rank, amount), ListingStateTone.Amber, rank, p.State.Action, amount);
                }
                else state = p.UnlistedCount > 0
                    ? new($"{p.UnlistedCount:N0} unlisted", ListingStateTone.Dim, 11)
                    : row.ObservedGroups.Where(g => g.AssignmentId == p.Assignment.Id).Select(g => g.State)
                        .OrderBy(s => s.Rank).FirstOrDefault() ?? ObservedState(ListingMatchKind.ShapeDiffers);
                return p with { State = state };
            }).ToArray();
            rows[i] = row with
            {
                PlanRows = plans,
                State = row.Available && row.ListingsObserved
                    ? Summary(plans.Select(p => p.State).Concat(row.ObservedGroups.Select(g => g.State))) : row.State,
            };
        }
    }

    private static ListingRetainerRow BuildRetainer(ListingPlanDraft draft, BrowserProjection browser, BrowserScope scope,
        bool available, Func<uint, bool?> listability, IReadOnlySet<uint> destinations)
    {
        var id = scope.RetainerId!.Value;
        var observed = browser.RetainerListingsCompleteByScope.GetValueOrDefault(scope.Key);
        var assignments = draft.Assignments.Where(a => a.RetainerId == id).ToArray();
        var enabled = assignments.Where(a => a.Enabled && Complete(a) && listability(a.ItemId) is not false).ToArray();
        var plan = new ListingPlan { Owner = draft.Owner, Assignments = enabled.Select(ListingPlanCatalog.Copy).ToList() };
        var evaluation = ListingPlanEvaluator.Evaluate(plan, browser, scope.Key);
        var matches = evaluation.Items.SelectMany(i => i.Matches).ToArray();
        var reserved = new HashSet<ListingRow>();
        var incomplete = assignments.Where(a => a.Enabled && !Complete(a) && a.ListingCount > 0).ToArray();
        var reservations = incomplete.ToDictionary(a => a.Id, _ => new List<ListingRow>());
        // Reserve unknown/known-price siblings consistently across all rows before the broad pass.
        foreach (var preferred in new[] { true, false })
        foreach (var a in incomplete)
        foreach (var m in matches.Where(m => m.Kind == ListingMatchKind.Unplanned).OrderBy(m => m.Listing.SlotIndex))
        {
            var row = m.Listing;
            if (reservations[a.Id].Count >= a.ListingCount || reserved.Contains(row) || row.ItemId != a.ItemId || Quality(row) != a.Quality ||
                (a.QuantityPerListing > 0 && row.Quantity != a.QuantityPerListing)) continue;
            if (preferred && (a.UnitPrice == 0 ? row.UnitPrice.IsKnown : !row.UnitPrice.IsKnown || row.UnitPrice.Value != a.UnitPrice)) continue;
            reserved.Add(row);
            reservations[a.Id].Add(row);
        }

        var planRows = new List<ListingPlanRowView>();
        foreach (var a in assignments)
        {
            var item = evaluation.Find(a.ItemId, a.Quality);
            var e = item?.Assignments.FirstOrDefault(e => e.Assignment.Id == a.Id);
            var siblings = enabled.Where(b => b.ItemId == a.ItemId && b.Quality == a.Quality).ToArray();
            var coverageRow = siblings.FirstOrDefault(b => (item?.Assignments.FirstOrDefault(e => e.Assignment.Id == b.Id)?.ExactListings ?? 0) < b.ListingCount) ?? siblings.FirstOrDefault();
            var state = State(a, available, observed, listability(a.ItemId), item, e, coverageRow?.Id == a.Id, siblings, browser, destinations);
            planRows.Add(new(a, state, e is null ? a.ListingCount : Remaining(e)));
        }
        var groups = matches.Where(m => m.Kind != ListingMatchKind.Exact && !reserved.Contains(m.Listing))
            .GroupBy(m => new { m.Kind, m.AssignmentId, m.Listing.ItemId, m.Listing.Quality, m.Listing.Quantity,
                Known = m.Listing.UnitPrice.IsKnown, Price = m.Listing.UnitPrice.IsKnown ? m.Listing.UnitPrice.Value : 0 })
            .Select(g => new ListingObservedGroupView(g.Key.Kind, g.Key.AssignmentId, g.Key.ItemId,
                g.First().Listing.ItemName, Quality(g.First().Listing), g.Key.Quantity, g.First().Listing.UnitPrice,
                g.Select(m => m.Listing).ToArray(), ObservedState(g.Key.Kind))).ToArray();
        var unplanned = groups.Where(g => g.Kind == ListingMatchKind.Unplanned).Sum(g => g.Listings.Count);
        var slots = Slots(assignments, observed ? enabled : [], matches, observed ? reservations : new Dictionary<Guid, List<ListingRow>>(), observed ? groups : []);
        var summary = !available ? new ListingStateView("Retainer unavailable", ListingStateTone.Amber, 0) :
            !observed ? new("Listings unknown", ListingStateTone.Dim, 9) : Summary(planRows.Select(r => r.State).Concat(groups.Select(g => g.State)));
        return new(id, scope.Label, available, observed, slots, assignments.Where(a => a.Enabled).Sum(a => a.ListingCount),
            unplanned, summary, planRows, groups);
    }

    // Projects the plan onto market slots in plan order: enabled assignments, off-plan
    // listings, free slots, then excluded assignments outside the 20-slot capacity.
    // A slot's listing is the evaluator's match for its assignment, never a second match pass.
    // Without a complete observation nothing is evaluated, so planned slots stay unknown, not "Not listed".
    private static IReadOnlyList<ListingSlotView> Slots(IReadOnlyList<ListingPlanAssignment> assignments,
        IReadOnlyList<ListingPlanAssignment> evaluated, IReadOnlyList<ListingMatch> matches,
        IReadOnlyDictionary<Guid, List<ListingRow>> reservations, IReadOnlyList<ListingObservedGroupView> groups)
    {
        var byAssignment = matches.Where(m => m.AssignmentId is not null && m.Kind is not (ListingMatchKind.Unplanned or ListingMatchKind.WrongRetainer))
            .GroupBy(m => m.AssignmentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Kind).ThenBy(m => m.Listing.SlotIndex ?? int.MaxValue).ToArray());
        var evaluatedIds = evaluated.Select(a => a.Id).ToHashSet();
        var rows = new List<(ListingSlotKey Key, ListingPlanAssignment? A, ListingRow? L, ListingSlotListed Listed, Guid Group, bool Capacity)>();
        foreach (var a in assignments.Where(a => a.Enabled))
            for (var n = 0; n < a.ListingCount; n++)
            {
                ListingRow? listing = null;
                var listed = ListingSlotListed.None;
                if (evaluatedIds.Contains(a.Id))
                {
                    var own = byAssignment.GetValueOrDefault(a.Id) ?? [];
                    (listing, listed) = n < own.Length ? (own[n].Listing, Listed(own[n].Kind)) : (null, ListingSlotListed.NotListed);
                }
                else if (reservations.TryGetValue(a.Id, out var held))
                    (listing, listed) = n < held.Count ? (held[n], Listed(Reserved(a, held[n]))) : (null, ListingSlotListed.NotListed);
                rows.Add((ListingSlotKey.Planned(a.Id, n), a, listing, listed, a.Id, true));
            }
        var offPlan = groups.Where(g => g.Kind == ListingMatchKind.Unplanned).SelectMany(g => g.Listings).ToArray();
        var ownRows = Math.Max(0, MarketRunPlanner.ListingCapacity - rows.Count);
        for (var i = 0; i < offPlan.Length && i < ownRows; i++)
            rows.Add((ListingSlotKey.Observed(offPlan[i].SlotIndex ?? 1000 + i), null, offPlan[i], ListingSlotListed.Other, Guid.Empty, true));
        // Off-plan listings beyond free capacity physically occupy planned-but-unlisted slots.
        using (var overflow = offPlan.Skip(ownRows).GetEnumerator())
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Listed is ListingSlotListed.NotListed or ListingSlotListed.None && overflow.MoveNext())
                    rows[i] = rows[i] with { L = overflow.Current, Listed = ListingSlotListed.Other };
        for (var free = 0; rows.Count < MarketRunPlanner.ListingCapacity; free++)
            rows.Add((ListingSlotKey.Free(free), null, null, ListingSlotListed.None, Guid.Empty, true));
        foreach (var a in assignments.Where(a => !a.Enabled))
            for (var n = 0; n < a.ListingCount; n++)
                rows.Add((ListingSlotKey.Planned(a.Id, n), a, null, ListingSlotListed.None, a.Id, false));
        var number = 0;
        return rows.Select((r, i) =>
        {
            var same = i > 0 && rows[i - 1].Group == r.Group && rows[i - 1].Capacity == r.Capacity;
            return new ListingSlotView(r.Key, r.Capacity ? ++number : 0, r.A, r.L, r.Listed, same && r.A is not null, i > 0 && !same);
        }).ToArray();
    }

    private static ListingSlotListed Listed(ListingMatchKind kind) => kind switch
    {
        ListingMatchKind.Exact => ListingSlotListed.Match, ListingMatchKind.UnknownPrice => ListingSlotListed.UnknownPrice,
        ListingMatchKind.PriceDiffers => ListingSlotListed.PriceDiffers, _ => ListingSlotListed.ShapeDiffers,
    };

    // The reservation pass already required item, quality and any planned units; it only names the difference.
    private static ListingMatchKind Reserved(ListingPlanAssignment a, ListingRow row) =>
        !row.UnitPrice.IsKnown ? ListingMatchKind.UnknownPrice :
        a.UnitPrice != row.UnitPrice.Value ? ListingMatchKind.PriceDiffers :
        a.QuantityPerListing != row.Quantity ? ListingMatchKind.ShapeDiffers : ListingMatchKind.Exact;

    private static int Remaining(ListingAssignmentEvaluation e) => Math.Max(0, e.Assignment.ListingCount - e.ExactListings - e.UnknownPriceListings - e.WrongPriceListings - e.WrongShapeListings);
    private static ListingStateView State(ListingPlanAssignment a, bool available, bool observed, bool? listable,
        ListingPlanItemEvaluation? item, ListingAssignmentEvaluation? e, bool carrier, IReadOnlyList<ListingPlanAssignment> siblings,
        BrowserProjection browser, IReadOnlySet<uint> destinations)
    {
        if (!a.Enabled) return new("Excluded", ListingStateTone.Dim, int.MaxValue);
        if (listable is false) return new("Not sellable", ListingStateTone.Dim, int.MaxValue);
        if (!available) return new("Retainer unavailable", ListingStateTone.Amber, 0);
        var unset = new[] { a.ListingCount == 0 ? "listings" : null, a.QuantityPerListing == 0 ? "quantity" : null, a.UnitPrice == 0 ? "price" : null }.OfType<string>().ToArray();
        if (unset.Length > 0) return new(unset.Length == 1 ? $"Needs {unset[0]}" : "Incomplete", ListingStateTone.Dim, 10);
        if (!observed) return new("Listings unknown", ListingStateTone.Dim, 9);
        if (e is null || item is null) return new("Incomplete", ListingStateTone.Dim, 10);
        if (e.ExactListings >= a.ListingCount) return new("On plan", ListingStateTone.Green, 12);
        var remaining = Remaining(e);
        var slotUnits = item.Assignments.Sum(v => Remaining(v) * v.Assignment.QuantityPerListing);
        if (carrier)
        {
            var action = destinations.Contains(a.ItemId) ? ListingStateAction.FocusStock : ListingStateAction.None;
            var coverage = item.Coverage;
            var amount = coverage switch
            {
                ListingCoverageState.Missing => item.MissingUnits.Value,
                ListingCoverageState.Retrievable => item.RetrievableUnits.Value,
                ListingCoverageState.ReadyOnPlayer => item.MovementNeedUnits.Value,
                _ => item.NeedUnits.IsKnown ? item.NeedUnits.Value : slotUnits,
            };
            var rank = coverage switch { ListingCoverageState.Missing => 1, ListingCoverageState.Retrievable => 2, ListingCoverageState.ReadyOnPlayer => 3, ListingCoverageState.Unknown => 4, _ => 99 };
            if (rank <= 3 || rank == 4 && (item.NeedUnits.IsKnown || remaining > 0))
                return new(AmountText(rank, amount), ListingStateTone.Amber, rank, action, amount);
        }
        if (remaining > 0)
        {
            var quality = a.Quality == ItemQualityPolicy.HqOnly ? FfxivItemQuality.HQ : FfxivItemQuality.NQ;
            var held = browser.Items.FirstOrDefault(i => i.ItemId == a.ItemId)?.Stacks
                .Where(s => s.RetainerId == a.RetainerId && s.Quality == quality).Sum(s => s.Quantity) ?? 0;
            if (item.Coverage == ListingCoverageState.ReadyOnAssignedRetainer)
                return held >= slotUnits ? new(AmountText(5, remaining), ListingStateTone.Amber, 5, Amount: remaining) : new("Shape differs", ListingStateTone.Info, 7);
            if (item.Coverage == ListingCoverageState.Satisfied) return new("Shape differs", ListingStateTone.Info, 7);
            return new($"{remaining:N0} unlisted", ListingStateTone.Dim, 11);
        }
        if (e.WrongPriceListings > 0) return ObservedState(ListingMatchKind.PriceDiffers);
        if (e.WrongShapeListings > 0) return ObservedState(ListingMatchKind.ShapeDiffers);
        return ObservedState(ListingMatchKind.UnknownPrice);
    }

    private static ListingStateView ObservedState(ListingMatchKind kind) => kind switch
    {
        ListingMatchKind.PriceDiffers => new("Price differs", ListingStateTone.Info, 6),
        ListingMatchKind.ShapeDiffers => new("Shape differs", ListingStateTone.Info, 7),
        ListingMatchKind.UnknownPrice => new("Price unknown", ListingStateTone.Dim, 8),
        _ => new("Unplanned", ListingStateTone.Dim, 11),
    };

    private static string AmountText(int rank, int amount) => rank switch
    {
        1 => $"Short {amount:N0} units", 2 => $"From retainers: {amount:N0}", 3 => $"On player: {amount:N0}",
        4 => "Stock unverified", _ => $"{amount:N0} ready to list",
    };

    internal static string Tooltip(ListingStateView state) => state.Rank switch
    {
        0 => "This planned retainer is not in the currently assigned owner roster. Its saved intent is preserved.",
        1 => "Units still missing after the planner accounts for observed listings and known stock. Shared item/quality coverage is counted once across the draft, not once per retainer.",
        2 => $"{state.Amount:N0} item units retrievable from observed retainer stock. This is aggregate item/quality coverage, not a count at this destination or a live retrieval guarantee.",
        3 => $"{state.Amount:N0} item units in observed player inventory for movement. This is aggregate item/quality coverage, not stock on this retainer.",
        4 => $"{state.Amount:N0} required item units come from planned listing demand; source coverage is not fully observed. Unknown stock is not zero.",
        5 => "Number of missing listing slots backed by observed stock on the assigned retainer. Quantity per listing is measured in item units.",
        6 => "Observed unit price differs from the saved or draft plan price. This compares against your plan, not competitors; no undercut assessment.",
        7 => "Observed listing quantity does not match the planned stack shape; aggregate units alone do not prove a matching listing.",
        8 => "The observation did not establish the unit price. No price is inferred.",
        9 => "No complete listing observation for this retainer. Planned cells remain visible but do not prove occupancy.",
        10 => "The draft still needs a positive listing count, units per listing, and unit price before execution.",
        11 => "Unlisted counts are planned listing slots not matched by observations. Unplanned listings are observed listings outside the plan; neither is a count of inventory units.",
        12 => "Observed listings match planned item, quality, units per listing and unit price. Observations are not a fresh live market scan.",
        _ => "Excluded or non-sellable draft rows do not add listing demand.",
    };

    private static ListingStateView Summary(IEnumerable<ListingStateView> entries)
    {
        var ranked = entries.Where(s => s.Rank != int.MaxValue).ToArray();
        if (ranked.Length == 0) return new("—", ListingStateTone.Dim, int.MaxValue);
        var rank = ranked.Min(s => s.Rank);
        var same = ranked.Where(s => s.Rank == rank).ToArray();
        var state = same[0] with { Action = ListingStateAction.None };
        if (rank is >= 1 and <= 5)
        {
            var amount = same.Sum(s => s.Amount);
            return state with { Text = AmountText(rank, amount), Amount = amount };
        }
        if (rank == 10 && same.Select(s => s.Text).Distinct().Count() > 1) return state with { Text = "Incomplete" };
        return rank == 11 ? state with { Text = "Unplanned" } : state;
    }
}
