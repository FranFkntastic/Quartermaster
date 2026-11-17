using System.Globalization;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal enum TransferLineKind
{
    Paused,
    VerifyListing,
    Retrieve,
    Stow,
    Balanced,
}

internal readonly record struct TransferLineProjection(
    TransferLineKind Kind,
    int FromSaddlebag = 0,
    int FromRetainers = 0,
    int Stow = 0,
    int NoRoom = 0,
    int Missing = 0,
    int Vendor = 0,
    bool StockUnverified = false,
    int KeptInBags = 0)
{
    public int Retrieve => FromSaddlebag + FromRetainers;
    public int Short => Math.Max(0, Missing - Vendor);
}

internal readonly record struct TransferPlanTotals(
    int Lines,
    int Retrieve,
    int Stow,
    int Vendor,
    int Short,
    int NoRoom,
    bool StockUnverified);

internal static class TransferWorkbenchPresentation
{
    public static int AccessibleStorageQuantity(
        StockGroup? stock,
        ItemQualityPolicy quality,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner) =>
        AccessibleStorageQuantity(stock, quality, AccessibleRetainerIds(retainers, owner));

    /// <summary>The owner's retainers whose storage a run can currently reach.</summary>
    public static HashSet<ulong> AccessibleRetainerIds(
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner) =>
        retainers.Values
            .Where(retainer =>
                retainer.Owner.Matches(owner) &&
                retainer.IsCurrentlyAssigned is not false &&
                retainer.IsUiAccessible is not false)
            .Select(retainer => retainer.RetainerId)
            .ToHashSet();

    /// <summary>Use with <see cref="AccessibleRetainerIds"/> when summing many items at once.</summary>
    public static int AccessibleStorageQuantity(
        StockGroup? stock,
        ItemQualityPolicy quality,
        IReadOnlySet<ulong> accessibleRetainerIds)
    {
        return stock?.Stacks
            .Where(stack =>
                stack.ScopeKind == BrowserScopeKind.Retainer &&
                stack.RetainerId is { } retainerId &&
                accessibleRetainerIds.Contains(retainerId) &&
                (quality == ItemQualityPolicy.Any ||
                 quality == ItemQualityPolicy.HqOnly &&
                 stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ ||
                 quality == ItemQualityPolicy.NqOnly &&
                 stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ))
            .Sum(stack => stack.Quantity) ?? 0;
    }

    public static string Target(int desiredPlayerQuantity, int playerQuantity, bool evidenceKnown = true) =>
        evidenceKnown
            ? $"{desiredPlayerQuantity.ToString("N0", CultureInfo.CurrentCulture)} ({Signed(desiredPlayerQuantity - playerQuantity)})"
            : $"{desiredPlayerQuantity.ToString("N0", CultureInfo.CurrentCulture)} (+?)";

    /// <summary>
    /// The single per-line projection the plan rows and the plan summary both read.
    /// </summary>
    public static TransferLineProjection Project(
        bool enabled,
        bool listingDemandKnown,
        StowageAction action,
        int requestedQuantity,
        int accessibleStorageQuantity,
        int routedDepositQuantity,
        int saddlebagQuantity,
        int vendorApprovedQuantity,
        bool stockUnverified,
        bool hasDestinations = true)
    {
        if (!enabled)
            return new(TransferLineKind.Paused);
        if (!listingDemandKnown)
            return new(TransferLineKind.VerifyListing);
        var requested = Math.Max(0, requestedQuantity);
        switch (action)
        {
            case StowageAction.Retrieve:
            {
                var fromSaddlebag = Math.Min(requested, Math.Max(0, saddlebagQuantity));
                var fromRetainers = Math.Min(requested - fromSaddlebag, Math.Max(0, accessibleStorageQuantity));
                var missing = requested - fromSaddlebag - fromRetainers;
                var vendor = Math.Min(missing, Math.Max(0, vendorApprovedQuantity));
                return new(TransferLineKind.Retrieve, fromSaddlebag, fromRetainers, Missing: missing, Vendor: vendor,
                    StockUnverified: stockUnverified);
            }
            case StowageAction.Deposit:
            {
                // No destinations is an ordinary choice: the surplus stays in bags, not "no room".
                if (!hasDestinations)
                    return new(TransferLineKind.Stow, KeptInBags: requested);
                var stow = Math.Min(requested, Math.Max(0, routedDepositQuantity));
                return new(TransferLineKind.Stow, Stow: stow, NoRoom: requested - stow);
            }
            default:
                return new(TransferLineKind.Balanced);
        }
    }

    public static TransferPlanTotals Totals(IEnumerable<TransferLineProjection> lines)
    {
        var count = 0;
        int retrieve = 0, stow = 0, vendor = 0, shortQuantity = 0, noRoom = 0;
        var unverified = false;
        foreach (var line in lines)
        {
            count++;
            retrieve += line.Retrieve;
            stow += line.Stow;
            vendor += line.Vendor;
            shortQuantity += line.Short;
            noRoom += line.NoRoom;
            unverified |= line.StockUnverified;
        }
        return new(count, retrieve, stow, vendor, shortQuantity, noRoom, unverified);
    }

    private static string Signed(int quantity) => quantity > 0
        ? $"+{quantity.ToString("N0", CultureInfo.CurrentCulture)}"
        : quantity.ToString("N0", CultureInfo.CurrentCulture);
}
