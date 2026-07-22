using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Operations;

namespace RQ.Automation;

/// <summary>
/// Maps Quartermaster operation semantics onto the product-neutral Franthropy retainer session.
/// Planning, authorization, persistence, and receipts remain in Quartermaster.
/// </summary>
public sealed class RetainerLiveDriver : IRetainerTransferDriver
{
    private readonly IRetainerAutomationSession session;

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

    internal RetainerLiveDriver(IRetainerAutomationSession session) => this.session = session;

    public async Task RequireRetainerListAsync(CancellationToken cancellationToken) =>
        RequireSuccess(await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false));

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
    {
        var result = await session.RetrieveAsync(stack, quantity, cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Transferred, result.Code, result.Message);
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(
        IReadOnlySet<uint> itemIds,
        CancellationToken cancellationToken) =>
        session.ScanPlayerCrystalsAsync(itemIds, cancellationToken);

    public Task<RetainerCrystalTransferResult> DepositCrystalAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken) =>
        session.DepositCrystalAsync(stack, quantity, cancellationToken);

    public async Task CloseRetainerAsync(CancellationToken cancellationToken) =>
        RequireSuccess(await session.CloseRetainerAsync(cancellationToken).ConfigureAwait(false));

    public void CancelActive() => session.CancelActive();

    private static void RequireSuccess(RetainerAutomationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException($"{result.Code}: {result.Message}");
    }
}
