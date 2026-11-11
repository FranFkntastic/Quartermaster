using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Side pane of the Market tab. It summarises the market run across retainers.
/// </summary>
internal sealed class MarketRunPane
{
    /// <param name="runtime">Current runtime snapshot.</param>
    /// <param name="session">Shared Listing Plan draft session (its draft is non-null when this is called).</param>
    /// <param name="retainers">Projection of every retainer row, unfiltered by the Market search box.</param>
    /// <param name="selectedRetainerId">Retainer currently expanded in the list, if any.</param>
    public void Draw(
        QuartermasterRuntimeSnapshot runtime,
        ListingPlanDraftSession session,
        IReadOnlyList<ListingRetainerRow> retainers,
        ulong? selectedRetainerId)
    {
    }
}
