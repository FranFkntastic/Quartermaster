using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal enum SlotCellKind { Free, Listed, Differs, Planned, PlannedUnknown, Unplanned }
internal readonly record struct SlotCell(SlotCellKind Kind, string Tooltip);
internal enum ListingStateTone { Dim, Amber, Green, Info }
internal enum ListingStateAction { None, FocusStock }
internal sealed record ListingStateView(string Text, ListingStateTone Tone, int Rank,
    ListingStateAction Action = ListingStateAction.None, int Amount = 0);
internal sealed record ListingPlanRowView(ListingPlanAssignment Assignment, ListingStateView State, int MaxStack, int UnlistedCount, int? ListedCount = null);
internal sealed record ListingObservedGroupView(ListingMatchKind Kind, Guid? AssignmentId, uint ItemId,
    string ItemName, ItemQualityPolicy Quality, int Quantity, FieldEvidence<decimal> UnitPrice,
    IReadOnlyList<ListingRow> Listings, ListingStateView State);
internal sealed record ListingRetainerRow(ulong RetainerId, string RetainerName, bool Available,
    bool ListingsObserved, IReadOnlyList<SlotCell> Cells, int PlannedSlots, int UnplannedSlots,
    ListingStateView State, IReadOnlyList<ListingPlanRowView> PlanRows,
    IReadOnlyList<ListingObservedGroupView> ObservedGroups);

internal static class ListingRetainerProjection
{
    public static bool Complete(ListingPlanAssignment a) => a.ListingCount > 0 && a.QuantityPerListing > 0 && a.UnitPrice > 0;
    public static ItemQualityPolicy Quality(ListingRow row) => row.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;

    public static IReadOnlyList<ListingRetainerRow> Build(ListingPlanDraft draft, BrowserProjection browser,
        IReadOnlySet<ulong> currentRetainerIds, Func<uint, int> resolveMaxStack, Func<uint, bool?> listability,
        IReadOnlySet<uint>? savedDemandItemIds = null)
    {
        var scopes = browser.Scopes.Where(s => s.Kind == BrowserScopeKind.Retainer && s.RetainerId.HasValue).ToList();
        var known = scopes.Select(s => s.RetainerId!.Value).ToHashSet();
        foreach (var a in draft.Assignments)
            if (known.Add(a.RetainerId)) scopes.Add(new(BrowserScope.RetainerKey(a.RetainerId), a.RetainerName, BrowserScopeKind.Retainer, a.RetainerId));
        var destinations = browser.Items.Select(i => i.ItemId).Concat(savedDemandItemIds ?? new HashSet<uint>()).ToHashSet();
        var rows = scopes.Select(scope => BuildRetainer(draft, browser, scope, currentRetainerIds.Contains(scope.RetainerId!.Value),
            resolveMaxStack, listability, destinations)).ToArray();
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
        bool available, Func<uint, int> maxStack, Func<uint, bool?> listability, IReadOnlySet<uint> destinations)
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
        var reservationCounts = incomplete.ToDictionary(a => a.Id, _ => 0);
        // Reserve unknown/known-price siblings consistently across all rows before the broad pass.
        foreach (var preferred in new[] { true, false })
        foreach (var a in incomplete)
        foreach (var m in matches.Where(m => m.Kind == ListingMatchKind.Unplanned).OrderBy(m => m.Listing.SlotIndex))
        {
            var row = m.Listing;
            if (reservationCounts[a.Id] >= a.ListingCount || reserved.Contains(row) || row.ItemId != a.ItemId || Quality(row) != a.Quality ||
                (a.QuantityPerListing > 0 && row.Quantity != a.QuantityPerListing)) continue;
            if (preferred && (a.UnitPrice == 0 ? row.UnitPrice.IsKnown : !row.UnitPrice.IsKnown || row.UnitPrice.Value != a.UnitPrice)) continue;
            reserved.Add(row);
            reservationCounts[a.Id]++;
        }

        var planRows = new List<ListingPlanRowView>();
        var cells = new List<SlotCell>();
        foreach (var a in assignments)
        {
            var item = evaluation.Find(a.ItemId, a.Quality);
            var e = item?.Assignments.FirstOrDefault(e => e.Assignment.Id == a.Id);
            var siblings = enabled.Where(b => b.ItemId == a.ItemId && b.Quality == a.Quality).ToArray();
            var coverageRow = siblings.FirstOrDefault(b => (item?.Assignments.FirstOrDefault(e => e.Assignment.Id == b.Id)?.ExactListings ?? 0) < b.ListingCount) ?? siblings.FirstOrDefault();
            var state = State(a, available, observed, listability(a.ItemId), item, e, coverageRow?.Id == a.Id, siblings, browser, destinations);
            planRows.Add(new(a, state, maxStack(a.ItemId), e is null ? a.ListingCount : Remaining(e),
                !observed ? null : e is null ? reservationCounts.GetValueOrDefault(a.Id) :
                e.ExactListings + e.UnknownPriceListings + e.WrongPriceListings + e.WrongShapeListings));
            if (!a.Enabled) continue;
            var exact = e?.ExactListings ?? 0;
            var differs = (e?.UnknownPriceListings ?? 0) + (e?.WrongPriceListings ?? 0) + (e?.WrongShapeListings ?? 0);
            // Only 20 cells are rendered even when a preserved merge is over capacity.
            var visibleCells = Math.Min(a.ListingCount, 20 - cells.Count);
            for (var n = 0; n < visibleCells; n++)
            {
                var kind = !observed ? SlotCellKind.PlannedUnknown : !Complete(a) ? SlotCellKind.Planned :
                    n < exact ? SlotCellKind.Listed : n < exact + differs ? SlotCellKind.Differs : SlotCellKind.Planned;
                var label = kind switch { SlotCellKind.Listed => "listed", SlotCellKind.Differs => "differs", _ => "planned" };
                cells.Add(new(kind, $"{a.ItemName} · {label}"));
            }
        }
        var groups = matches.Where(m => m.Kind != ListingMatchKind.Exact && !reserved.Contains(m.Listing))
            .GroupBy(m => new { m.Kind, m.AssignmentId, m.Listing.ItemId, m.Listing.Quality, m.Listing.Quantity,
                Known = m.Listing.UnitPrice.IsKnown, Price = m.Listing.UnitPrice.IsKnown ? m.Listing.UnitPrice.Value : 0 })
            .Select(g => new ListingObservedGroupView(g.Key.Kind, g.Key.AssignmentId, g.Key.ItemId,
                g.First().Listing.ItemName, Quality(g.First().Listing), g.Key.Quantity, g.First().Listing.UnitPrice,
                g.Select(m => m.Listing).ToArray(), ObservedState(g.Key.Kind))).ToArray();
        var unplanned = groups.Where(g => g.Kind == ListingMatchKind.Unplanned).Sum(g => g.Listings.Count);
        foreach (var group in groups.Where(g => observed && g.Kind == ListingMatchKind.Unplanned))
            for (var n = 0; n < group.Listings.Count && cells.Count < 20; n++) cells.Add(new(SlotCellKind.Unplanned, $"{group.ItemName} · unplanned"));
        while (cells.Count < 20) cells.Add(new(SlotCellKind.Free, "Free"));
        var summary = !available ? new ListingStateView("Retainer unavailable", ListingStateTone.Amber, 0) :
            !observed ? new("Listings unknown", ListingStateTone.Dim, 9) : Summary(planRows.Select(r => r.State).Concat(groups.Select(g => g.State)));
        return new(id, scope.Label, available, observed, cells, assignments.Where(a => a.Enabled).Sum(a => a.ListingCount),
            unplanned, summary, planRows, groups);
    }

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
                return held >= slotUnits ? new($"List {remaining:N0}", ListingStateTone.Amber, 5, Amount: remaining) : new("Shape differs", ListingStateTone.Info, 7);
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
        1 => $"Missing {amount:N0}", 2 => $"Retrieve {amount:N0}", 3 => $"Move {amount:N0} from player",
        4 => $"Need {amount:N0} · source?", _ => $"List {amount:N0}",
    };

    private static ListingStateView Summary(IEnumerable<ListingStateView> entries)
    {
        var ranked = entries.Where(s => s.Rank != int.MaxValue).ToArray();
        if (ranked.Length == 0) return new("—", ListingStateTone.Dim, int.MaxValue);
        var rank = ranked.Min(s => s.Rank);
        var same = ranked.Where(s => s.Rank == rank).ToArray();
        var state = same[0] with { Action = ListingStateAction.None };
        if (rank is >= 1 and <= 5) return state with { Text = AmountText(rank, same.Sum(s => s.Amount)) };
        if (rank == 10 && same.Select(s => s.Text).Distinct().Count() > 1) return state with { Text = "Incomplete" };
        return rank == 11 ? state with { Text = "Unplanned" } : state;
    }
}
