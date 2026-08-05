using Franthropy.Dalamud.AgentBridge;
using RQ.Domain;

namespace RQ.AgentBridge;

public sealed record QuartermasterBridgeTruth(
    int SchemaVersion,
    string PluginInstanceId,
    int ProcessId,
    string PluginVersion,
    bool MainWindowOpen,
    string CurrentWorkspace,
    string StockFilter,
    int VisibleStockCount,
    int RenderedStockRowCount,
    int StockProjectionBuildCount,
    int StockTableApplyCount,
    int TransferProjectionBuildCount,
    int RenderedTransferRowCount,
    double WindowDrawMilliseconds,
    double ContentDrawMilliseconds,
    double StockDrawMilliseconds,
    double PlanDrawMilliseconds,
    double ReviewFinalizeMilliseconds,
    string TransferDirection,
    bool PlanEditorOpen,
    bool PlanEditorHasUnsavedChanges,
    int ItemGroupCount,
    Guid? SelectedItemGroupId,
    string? SelectedItemGroupName,
    bool ItemGroupEditorOpen,
    bool ItemGroupEditorHasUnsavedChanges,
    string Owner,
    bool OwnerScopeAvailable,
    int ObservedRetainerCount,
    DateTimeOffset? OldestRetainerObservedAtUtc,
    int PlanLineCount,
    int EnabledPlanLineCount,
    int TransferPlanCount,
    Guid? SelectedTransferPlanId,
    string? SelectedTransferPlanName,
    int SelectedTransferRetrieveQuantity,
    int SelectedTransferDepositQuantity,
    bool TransferEditorOpen,
    string? CurrentOperationId,
    string? CurrentOperationStatus,
    bool RefreshAvailable,
    bool RefreshActive,
    string RefreshStatus,
    bool TransferActive,
    bool ListingNavigationActive,
    string ListingNavigationStatus);

public sealed class QuartermasterBridgeProvider
{
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("transfer", "Stock and Transfer Plans", "open-main-window", "transfer", 10),
        new("transfer-review", "Transfer Plan review", "open-main-window", "transfer-review", 20),
        new("item-groups", "Item Groups", "open-main-window", "item-groups", 30),
        new("listings", "Retainer listings", "open-main-window", "listings", 40),
        new("activity", "Operations and receipts", "open-main-window", "activity", 50),
    ];
    private static readonly IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> CaptureSurfaces =
    [
        new("transfer", "Stock and Transfer Plans", 10, IsDefault: true),
        new("transfer-review", "Transfer Plan review", 20),
        new("item-groups", "Item Groups", 30),
        new("listings", "Retainer listings", 40),
        new("activity", "Operations and receipts", 50),
    ];

    private readonly Func<QuartermasterBridgeTruth> createTruth;
    private readonly Action<string> openMainWindow;
    private readonly Action closeMainWindow;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public QuartermasterBridgeProvider(
        Func<QuartermasterBridgeTruth> createTruth,
        Action<string> openMainWindow,
        Action closeMainWindow,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.createTruth = createTruth;
        this.openMainWindow = openMainWindow;
        this.closeMainWindow = closeMainWindow;
        this.reviewRegistry = reviewRegistry;
    }

    public QuartermasterBridgeTruth CreateTruth() => createTruth();
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => ReviewSurfaces;
    public IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> GetCaptureSurfaces() => CaptureSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string id) => reviewRegistry.Review(id);
    public AgentBridgeUiControlInvocation InvokeControl(string id, long frameId, System.Text.Json.JsonElement? arguments = null) =>
        reviewRegistry.Invoke(id, frameId, arguments);
    public bool TryOpenMainWindow(string target)
    {
        if (target.Trim().ToLowerInvariant() is not ("stock" or "stock-and-plan" or "transfer" or "transfer-review" or "restock" or "stowage" or "item-groups" or "groups" or "listings" or "operation" or "activity"))
            return false;
        openMainWindow(target);
        return true;
    }
    public void CloseMainWindow() => closeMainWindow();
}
