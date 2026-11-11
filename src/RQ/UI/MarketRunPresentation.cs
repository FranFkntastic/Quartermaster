using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal static class MarketRunPresentation
{
    // Disabled reserve-only rules remain reachable for re-enabling or removal, but add no demand.
    internal static IReadOnlyList<MarketRunItem> DisplayItems(MarketRunEvaluation run, ListingPlanDraft draft) =>
        run.Items.Concat(draft.Reserves.Where(r => !r.Enabled && run.Find(r.ItemId, r.Quality) is null)
            .Select(r => new MarketRunItem(r.ItemId, r.ItemName, r.Quality, 0,
                Evidence.Unknown<int>("Reserve disabled"), Evidence.Unknown<int>("Reserve disabled"),
                Evidence.Known(0), Evidence.Known(0), r, 0, Evidence.Unknown<int>("Reserve disabled"), Evidence.Known(0))))
            .OrderBy(i => i.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Quality).ToArray();

    internal static ListingPlan PlanView(ListingPlanDraft draft) => new()
    {
        Id = draft.PlanId, Revision = draft.SourceRevision, Owner = draft.Owner,
        Assignments = draft.Assignments.Select(ListingPlanCatalog.Copy).ToList(),
        Reserves = draft.Reserves.Select(ListingPlanCatalog.Copy).ToList(),
    };

    internal static string Number(FieldEvidence<int> value) => value.IsKnown ? value.Value.ToString("N0") : "—";
    // Units already listed where the plan wants them; listings on other retainers do not count.
    internal static string InPlace(MarketRunEvaluation run, MarketRunItem item)
    {
        long units = 0;
        foreach (var row in run.Retainers.SelectMany(r => r.Assignments)
                     .Where(a => a.Assignment.ItemId == item.ItemId && a.Assignment.Quality == item.Quality))
        {
            if (!row.ShortListings.IsKnown) return "—";
            units += (long)(row.PlannedListings - row.ShortListings.Value) * Math.Max(0, row.Assignment.QuantityPerListing);
        }
        return units.ToString("N0");
    }
    internal static string Header(MarketRunEvaluation run)
    {
        if (!run.IsComplete)
        {
            var missing = run.Retainers.Count(r => !r.ListingsComplete);
            return missing > 0 ? $"{missing} retainers not yet observed" : "Retainer stock not yet observed";
        }
        return $"Refill {run.Retainers.Sum(r => (long)r.RefillListings.Value):N0} · Short {run.Retainers.Sum(r => (long)r.UnfilledListings.Value):N0} · Craft {run.Items.Sum(i => (long)i.CraftUnits.Value):N0}";
    }
}
