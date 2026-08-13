using System.Globalization;
using System.Numerics;
using RQ.Domain;
using RQ.Inventory;
using RQ.Planning;

namespace RQ.UI;

internal static class TransferPresentation
{
    public static Vector4 ActionColor(StowageAction? action) => action switch
    {
        StowageAction.Retrieve => new Vector4(.52f, .79f, .94f, 1f),
        StowageAction.Deposit => new Vector4(.53f, .83f, .64f, 1f),
        _ => new Vector4(.69f, .74f, .77f, 1f),
    };

    public static string SignedQuantity(int quantity) =>
        quantity > 0
            ? $"+{quantity:N0}"
            : quantity.ToString("N0", CultureInfo.CurrentCulture);

    public static string ReviewOutcome(TransferReviewRow row) =>
        !row.ListingContribution.IsKnown
            ? "Verify listing demand"
            : row.Line?.Action switch
            {
                StowageAction.Retrieve => $"Retrieve {row.Line.RetrieveQuantity:N0}",
                StowageAction.Deposit => $"Stow {row.Line.DepositQuantity:N0} · {RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner)}",
                _ => "On target · skip",
            };

    public static string RouteSummary(
        StowageRoutingPolicy routing,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var names = routing.PreferredRetainerIds
            .Select(id => retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(owner)
                ? retainer.RetainerName
                : $"Retainer {id}")
            .ToArray();
        if (names.Length == 0)
            return routing.Mode == StowageRoutingMode.ConsolidateFirst ? "Consolidate anywhere" : "Preferred first";
        var preferred = string.Join(" -> ", names);
        return routing.Overflow == StowageOverflowPolicy.AnyOwnerRetainer
            ? $"{preferred} -> any"
            : preferred;
    }

    public static string RoutingModeLabel(StowageRoutingMode mode) => mode switch
    {
        StowageRoutingMode.HomeFirst => "Preferred retainers first",
        _ => "Consolidate first",
    };

    public static string OverflowLabel(StowageOverflowPolicy overflow) => overflow switch
    {
        StowageOverflowPolicy.HoldOnPlayer => "Keep on player",
        _ => "Any retainer",
    };
}
