using Franthropy.Dalamud.AgentBridge;
using RQ.Domain;

namespace RQ.AgentBridge;

public sealed record QuartermasterBridgeTruth(
    int SchemaVersion,
    string PluginInstanceId,
    int ProcessId,
    string PluginVersion,
    bool MainWindowOpen,
    string Owner,
    bool OwnerScopeAvailable,
    int ObservedRetainerCount,
    DateTimeOffset? OldestRetainerObservedAtUtc,
    int PlanLineCount,
    int EnabledPlanLineCount,
    string? CurrentOperationId,
    string? CurrentOperationStatus,
    bool RefreshAvailable,
    bool RefreshActive,
    bool TransferActive);

public sealed class QuartermasterBridgeProvider
{
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("stock-and-plan", "Stock and retrieval plan", "open-main-window", "stock", 10),
        new("listings", "Retainer listings", "open-main-window", "listings", 20),
        new("operation", "Current operation", "open-main-window", "operation", 30),
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
    public AgentBridgeUiControlInvocation InvokeControl(string id, long frameId) => reviewRegistry.Invoke(id, frameId);
    public bool TryOpenMainWindow(string target)
    {
        if (target.Trim().ToLowerInvariant() is not ("stock" or "stock-and-plan" or "listings" or "operation"))
            return false;
        openMainWindow(target);
        return true;
    }
    public void CloseMainWindow() => closeMainWindow();
}
