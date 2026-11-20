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

    /// <summary>Courier sends: units leaving for another character.</summary>
    public static readonly Vector4 SendColor = new(.78f, .68f, .94f, 1f);

    /// <summary>"NQ"/"HQ" for a restricted quality, <paramref name="anyLabel"/> otherwise.</summary>
    /// <param name="anyLabel">Text for <see cref="ItemQualityPolicy.Any"/>.</param>
    /// <param name="restrictedSuffix">Appended to NQ and HQ, e.g. " only" for a quality picker.</param>
    public static string QualityLabel(ItemQualityPolicy quality, string anyLabel = "Any", string restrictedSuffix = "") => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ" + restrictedSuffix,
        ItemQualityPolicy.HqOnly => "HQ" + restrictedSuffix,
        _ => anyLabel,
    };

    public static string SignedQuantity(int quantity) =>
        quantity > 0
            ? $"+{quantity:N0}"
            : quantity.ToString("N0", CultureInfo.CurrentCulture);

    private static string RetainerName(TransferReviewRow row, ulong id) =>
        row.Runtime.Retainers.GetValueOrDefault(id)?.RetainerName ?? $"Retainer {id}";

    public static string ReviewOutcome(TransferReviewRow row) =>
        row.Move is { } move
            ? $"{RetainerName(row, move.SourceRetainerId)} → {RetainerName(row, move.DestinationRetainerId)} · {move.Quantity:N0} {(move.IsHighQuality ? "HQ" : "NQ")}"
            : !row.ListingContribution.IsKnown
            ? "Verify listing demand"
            : row.Line?.Action switch
            {
                StowageAction.Move when row.Line.MissingPlacementEvidence is not null => "Verify retainer stock",
                StowageAction.Move when row.Line.MoveQuantity == 0 => string.Join("; ", row.Line.MoveLimits.Select(l => l.Message)),
                StowageAction.Move => $"Move {row.Line.MoveQuantity:N0}",
                StowageAction.Retrieve => Math.Min(row.Line.RetrieveQuantity, row.Line.SaddlebagQuantity) is > 0 and var fromSaddlebag
                    ? $"Retrieve {row.Line.RetrieveQuantity:N0} · {fromSaddlebag:N0} saddlebag"
                    : $"Retrieve {row.Line.RetrieveQuantity:N0}",
                StowageAction.Deposit => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner) is { Length: > 0 } route
                    ? $"Stow {row.Line.DepositQuantity:N0} · {route}"
                    : $"Stays in bags {row.Line.DepositQuantity:N0}",
                _ => "On target · skip",
            };

    /// <summary>Destinations in order ("Eris → Skrrt → Any space"); empty when there are none.</summary>
    public static string RouteSummary(
        StowageRoutingPolicy routing,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var names = routing.PreferredRetainerIds
            .Distinct()
            .Select(id => retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(owner)
                ? retainer.RetainerName
                : $"Retainer {id}")
            .ToList();
        if (routing.AnySpace)
            names.Add(AnySpaceLabel);
        return string.Join(" → ", names);
    }

    public const string AnySpaceLabel = "Any space";
}
