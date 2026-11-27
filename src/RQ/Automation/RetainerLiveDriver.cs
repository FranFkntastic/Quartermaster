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
        "QuantityPopupAlreadyOpen",
        "InvalidRetainerBaseline",
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

    internal RetainerLiveDriver(IRetainerAutomationSession session, IRetainerBellRoute? bellRoute = null, Action<string>? warn = null)
    {
        this.session = session;
        this.bellRoute = bellRoute;
        this.warn = warn;
    }

    private readonly Action<string>? warn;

    // Opening the list moves nothing; a bell interaction swallowed right after login, a zone change or a previous
    // retainer session is retried until this budget instead of stopping the run on the first timeout.
    private static readonly TimeSpan RetainerListPendingBudget = TimeSpan.FromSeconds(30);

    public async Task<MarketPlayerInventory> ReadPlacementBagsAsync(CancellationToken token)
    {
        var stacks = await session.ScanPlayerInventoryAsync(null, token).ConfigureAwait(false);
        return new(stacks, Math.Max(0, 140 - stacks.Select(s => (s.Container, s.SlotIndex)).Distinct().Count()));
    }

    public async Task RequireRetainerListAsync(CancellationToken cancellationToken)
    {
        var result = await OpenRetainerListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return;
        if (bellRoute is null || !RequiresBellRoute(result.Code))
        {
            RequireListSuccess(result);
            return;
        }

        var route = await bellRoute.EnsureBellInRangeAsync(cancellationToken).ConfigureAwait(false);
        if (!route.Success)
        {
            warn?.Invoke($"[Quartermaster] Retainer list unavailable: {route.Code}: {route.Message}");
            throw new InvalidOperationException($"{route.Code}: {route.Message}");
        }

        RequireListSuccess(await OpenRetainerListPendingAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Opens the retainer list once the character can act, treating a swallowed bell interaction
    /// (<c>RetainerListTimeout</c>) as pending until <see cref="RetainerListPendingBudget"/>.
    /// </summary>
    private async Task<RetainerAutomationResult> OpenRetainerListPendingAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(RetainerListPendingBudget);
        var attempts = 0;
        while (true)
        {
            if (bellRoute is not null)
            {
                var remaining = deadline - DateTime.UtcNow;
                var ready = await bellRoute.WaitUntilPlayerCanActAsync(
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                if (!ready.Success)
                    return RetainerAutomationResult.Failed(ready.Code, ready.Message);
            }
            var result = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
            attempts++;
            if (result.Success || !string.Equals(result.Code, "RetainerListTimeout", StringComparison.Ordinal))
                return result;
            if (DateTime.UtcNow >= deadline)
                return RetainerAutomationResult.Failed(
                    result.Code,
                    $"{result.Message} Gave up after {attempts} attempts over {RetainerListPendingBudget.TotalSeconds:N0}s.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void RequireListSuccess(RetainerAutomationResult result)
    {
        if (!result.Success)
            warn?.Invoke($"[Quartermaster] Retainer list unavailable: {result.Code}: {result.Message}");
        RequireSuccess(result);
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

    public async Task EndRetainerSessionAsync(Func<string?> refusalReason, CancellationToken cancellationToken)
    {
        var result = await session.EnsureRetainerSessionEndedAsync(refusalReason, cancellationToken).ConfigureAwait(false);
        if (result.Code == "RetainerSessionEndedAfterReopen")
            warn?.Invoke($"[Quartermaster] {result.Code}: {result.Message}");
        RequireSuccess(result);
    }

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
