using System.Globalization;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal readonly record struct TransferOutcomePresentation(string Primary, string? Constraint = null, string? Source = null)
{
    public string Text => string.Join(" · ", new[] { Primary, Source, Constraint }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

internal static class TransferWorkbenchPresentation
{
    public static int AccessibleStorageQuantity(
        StockGroup? stock,
        ItemQualityPolicy quality,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var accessibleRetainerIds = retainers.Values
            .Where(retainer =>
                retainer.Owner.Matches(owner) &&
                retainer.IsCurrentlyAssigned is not false &&
                retainer.IsUiAccessible is not false)
            .Select(retainer => retainer.RetainerId)
            .ToHashSet();
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

    public static TransferOutcomePresentation Outcome(
        StowageAction action,
        int requestedQuantity,
        int accessibleStorageQuantity,
        int routedDepositQuantity = 0,
        int saddlebagQuantity = 0)
    {
        requestedQuantity = Math.Max(0, requestedQuantity);
        return action switch
        {
            StowageAction.Retrieve => Retrieval(requestedQuantity, accessibleStorageQuantity, saddlebagQuantity),
            StowageAction.Deposit => Deposit(requestedQuantity, routedDepositQuantity),
            _ => new("On target"),
        };
    }

    private static TransferOutcomePresentation Retrieval(int requested, int accessible, int saddlebag)
    {
        var fromSaddlebag = Math.Min(requested, Math.Max(0, saddlebag));
        var retrievable = fromSaddlebag + Math.Min(requested - fromSaddlebag, Math.Max(0, accessible));
        var missing = requested - retrievable;
        var source = fromSaddlebag > 0 ? $"{fromSaddlebag:N0} saddlebag" : null;
        return missing switch
        {
            <= 0 => new($"Retrieve {retrievable:N0}", Source: source),
            _ => new($"Retrieve {retrievable:N0}", $"short {missing:N0}", source),
        };
    }

    private static TransferOutcomePresentation Deposit(int requested, int routed)
    {
        var stowable = Math.Min(requested, Math.Max(0, routed));
        var withoutCapacity = requested - stowable;
        return withoutCapacity switch
        {
            <= 0 => new($"Stow {stowable:N0}"),
            _ when stowable == 0 => new("Stow 0", $"no room for {withoutCapacity:N0}"),
            _ => new($"Stow {stowable:N0}", $"no room for {withoutCapacity:N0}"),
        };
    }

    private static string Signed(int quantity) => quantity > 0
        ? $"+{quantity.ToString("N0", CultureInfo.CurrentCulture)}"
        : quantity.ToString("N0", CultureInfo.CurrentCulture);
}
