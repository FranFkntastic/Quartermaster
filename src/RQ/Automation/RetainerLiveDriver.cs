using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Operations;

namespace RQ.Automation;

internal static class RetainerRetrievalResultPolicy
{
    private static readonly IReadOnlySet<string> PreMovementFailures = new HashSet<string>(StringComparer.Ordinal)
    {
        "InvalidRequest",
        "InventoryUnavailable",
        "RetainerInventoryUnavailable",
        "SourceSlotChanged",
        "RetainerAgentUnavailable",
        "CommandUnavailable",
        "RetainerIdentityMismatch",
    };

    public static bool MovementMayHaveOccurred(bool success, string code) =>
        success || !PreMovementFailures.Contains(code);
}

/// <summary>
/// Maps Quartermaster operation semantics onto the product-neutral Franthropy retainer session.
/// Planning, authorization, persistence, and receipts remain in Quartermaster.
/// </summary>
public sealed class RetainerLiveDriver : IRetainerTransferDriver
{
    private readonly IRetainerAutomationSession session;
    private readonly IRetainerBellRoute? bellRoute;

    public RetainerLiveDriver(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner)
        : this(new DalamudRetainerAutomationSession(framework, gameGui, dataManager, log, objects, targets, sigScanner))
    {
    }

    internal RetainerLiveDriver(IRetainerAutomationSession session, IRetainerBellRoute? bellRoute = null)
    {
        this.session = session;
        this.bellRoute = bellRoute;
    }

    public async Task RequireRetainerListAsync(CancellationToken cancellationToken)
    {
        var result = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return;
        if (bellRoute is null || !RequiresBellRoute(result.Code))
        {
            RequireSuccess(result);
            return;
        }

        var route = await bellRoute.EnsureBellInRangeAsync(cancellationToken).ConfigureAwait(false);
        if (!route.Success)
            throw new InvalidOperationException($"{route.Code}: {route.Message}");

        RequireSuccess(await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) =>
        RequireSuccess(await session.OpenRetainerAsync(
            new RetainerAutomationTarget(candidate.RetainerId, candidate.RetainerName),
            cancellationToken).ConfigureAwait(false));

    public async Task OpenInventoryAsync(CancellationToken cancellationToken) =>
        RequireSuccess(await session.OpenInventoryAsync(cancellationToken).ConfigureAwait(false));

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(
        IReadOnlySet<uint> itemIds,
        CancellationToken cancellationToken) =>
        session.ScanRetainerAsync(itemIds, cancellationToken);

    public async Task<RetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken)
        => await RetrieveAsync(stack, quantity, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Passes Quartermaster's existing route-scan total into Franthropy so its rare
    /// timeout reconciliation needs only one after-state scan.
    /// </summary>
    public async Task<RetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken)
        => await RetrieveAsync(stack, quantity, (int?)retainerVariantQuantityBefore, cancellationToken).ConfigureAwait(false);

    private async Task<RetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int? retainerVariantQuantityBefore,
        CancellationToken cancellationToken)
    {
        var result = retainerVariantQuantityBefore is { } knownQuantity
            ? await session.RetrieveAsync(stack, quantity, knownQuantity, cancellationToken).ConfigureAwait(false)
            : await session.RetrieveAsync(stack, quantity, cancellationToken).ConfigureAwait(false);
        return new(
            result.Success,
            result.Transferred,
            result.Code,
            result.Message,
            RetainerRetrievalResultPolicy.MovementMayHaveOccurred(result.Success, result.Code));
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(
        IReadOnlySet<uint> itemIds,
        CancellationToken cancellationToken) =>
        session.ScanPlayerCrystalsAsync(itemIds, cancellationToken);

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(
        IReadOnlySet<uint> itemIds,
        CancellationToken cancellationToken) =>
        session.ScanPlayerInventoryAsync(itemIds, cancellationToken);

    public Task<RetainerDepositResult> DepositAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken) =>
        session.DepositAsync(stack, quantity, cancellationToken);

    public Task<RetainerCrystalTransferResult> DepositCrystalAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken) =>
        session.DepositCrystalAsync(stack, quantity, cancellationToken);

    public async Task CloseRetainerAsync(CancellationToken cancellationToken) =>
        RequireSuccess(await session.CloseRetainerAsync(cancellationToken).ConfigureAwait(false));

    public async Task CloseRetainerListAsync(CancellationToken cancellationToken) =>
        RequireSuccess(await session.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false));

    public void CancelActive()
    {
        bellRoute?.Cancel();
        session.CancelActive();
    }

    private static bool RequiresBellRoute(string code) =>
        string.Equals(code, "NoNearbySummoningBell", StringComparison.Ordinal) ||
        string.Equals(code, "NoInteractableSummoningBell", StringComparison.Ordinal);

    private static void RequireSuccess(RetainerAutomationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException($"{result.Code}: {result.Message}");
    }
}
