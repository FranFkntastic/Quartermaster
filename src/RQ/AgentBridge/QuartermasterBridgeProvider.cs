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
    string Owner,
    bool OwnerScopeAvailable,
    int ObservedRetainerCount,
    DateTimeOffset? OldestRetainerObservedAtUtc,
    int PlanLineCount,
    int EnabledPlanLineCount,
    int StowagePlanCount,
    Guid? SelectedStowagePlanId,
    string? SelectedStowagePlanName,
    bool StowageEditorOpen,
    int RestockPlanCount,
    Guid? SelectedRestockPlanId,
    string? SelectedRestockPlanName,
    int SelectedRestockNeededQuantity,
    string? CurrentOperationId,
    string? CurrentOperationStatus,
    bool RefreshAvailable,
    bool RefreshActive,
    string RefreshStatus,
    bool TransferActive);

public sealed class QuartermasterBridgeProvider
{
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("stock", "Stock and plans", "open-main-window", "stock", 10),
        new("restock", "Reusable Restock Plans", "open-main-window", "restock", 20),
        new("stowage", "Stowage Plans and Quick Deposit", "open-main-window", "stowage", 30),
        new("listings", "Retainer listings", "open-main-window", "listings", 40),
        new("activity", "Operations and receipts", "open-main-window", "activity", 50),
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
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string id) => reviewRegistry.Review(id);
    public AgentBridgeUiControlInvocation InvokeControl(string id, long frameId, System.Text.Json.JsonElement? arguments = null) =>
        reviewRegistry.Invoke(id, frameId, arguments);
    public bool TryOpenMainWindow(string target)
    {
        if (target.Trim().ToLowerInvariant() is not ("stock" or "stock-and-plan" or "restock" or "stowage" or "listings" or "operation" or "activity"))
            return false;
        openMainWindow(target);
        return true;
    }
    public void CloseMainWindow() => closeMainWindow();
}
