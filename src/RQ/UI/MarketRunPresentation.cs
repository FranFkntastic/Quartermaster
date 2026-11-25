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
        Id = draft.PlanId, Revision = draft.SourceRevision, Owner = draft.Owner, Name = draft.Name,
        Assignments = draft.Assignments.Select(ListingPlanCatalog.Copy).ToList(),
        Reserves = draft.Reserves.Select(ListingPlanCatalog.Copy).ToList(),
    };

    internal static string Number(FieldEvidence<int> value) => value.IsKnown ? value.Value.ToString("N0") : "—";
    // Units already listed where the plan wants them; listings on other retainers do not count. Null when unobserved.
    internal static int? InPlaceUnits(MarketRunEvaluation run, MarketRunItem item)
    {
        long units = 0;
        foreach (var row in run.Retainers.SelectMany(r => r.Assignments)
                     .Where(a => a.Assignment.ItemId == item.ItemId && a.Assignment.Quality == item.Quality))
        {
            if (!row.ShortListings.IsKnown) return null;
            units += (long)(row.PlannedListings - row.ShortListings.Value) * Math.Max(0, row.Assignment.QuantityPerListing);
        }
        return (int)Math.Min(units, int.MaxValue);
    }

    internal static string PullLabel(MarketRunPullListing pull) =>
        $"Pull · {pull.Listing.ItemName} {(pull.Listing.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ ? "HQ" : "NQ")}";
}
