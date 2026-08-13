using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal static class ListingPlanPresentation
{
    public static IReadOnlyList<StockGroup> ApplyStockItemFocus(
        IReadOnlyList<StockGroup> items,
        uint? focusedItemId) =>
        focusedItemId is { } itemId
            ? items.Where(item => item.ItemId == itemId).ToArray()
            : items;

    public static string? CapacityTransitionConflict(ListingPlanDraft draft, BrowserProjection browser)
    {
        var plan = new ListingPlan
        {
            Owner = draft.Owner,
            Assignments = draft.Assignments.Select(ListingPlanCatalog.Copy).ToList(),
        };
        foreach (var retainer in draft.Assignments
                     .Where(assignment => assignment.Enabled && assignment.RetainerId != 0)
                     .GroupBy(assignment => assignment.RetainerId))
        {
            var scopeKey = BrowserScope.RetainerKey(retainer.Key);
            if (!browser.RetainerListingsCompleteByScope.GetValueOrDefault(scopeKey))
                continue;
            var planned = retainer.Sum(assignment => assignment.ListingCount);
            var unmanaged = ListingPlanEvaluator.Evaluate(plan, browser, scopeKey).Items
                .SelectMany(item => item.UnmanagedPhysicalListings)
                .Count();
            if (planned + unmanaged <= 20)
                continue;

            var retainerName = retainer
                                   .Select(assignment => assignment.RetainerName)
                                   .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                               browser.Scopes.FirstOrDefault(scope => scope.Key == scopeKey)?.Label ??
                               $"Retainer {retainer.Key}";
            return $"Current transition: {retainerName} has {planned + unmanaged:N0} occupied ({unmanaged:N0} outside this plan); Save remains available.";
        }
        return null;
    }
}
