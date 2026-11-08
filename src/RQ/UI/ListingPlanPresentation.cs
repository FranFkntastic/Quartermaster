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
}
