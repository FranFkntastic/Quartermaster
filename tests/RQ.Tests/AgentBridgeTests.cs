using RQ.AgentBridge;
using Franthropy.Dalamud.AgentBridge;
using System.Numerics;

namespace RQ.Tests;

public sealed class AgentBridgeTests
{
    [Fact]
    public void Provider_ExposesQuartermasterReviewSurfacesWithoutTransferActions()
    {
        var truth = new QuartermasterBridgeTruth(
            SchemaVersion: 14,
            PluginInstanceId: "provider",
            ProcessId: 42,
            PluginVersion: "1.0",
            MainWindowOpen: false,
            CurrentWorkspace: "transfer",
            StockFilter: string.Empty,
            VisibleStockCount: 0,
            RenderedStockRowCount: 0,
            StockProjectionBuildCount: 0,
            StockTableApplyCount: 0,
            TransferProjectionBuildCount: 0,
            RenderedTransferRowCount: 0,
            WindowDrawMilliseconds: 0,
            ContentDrawMilliseconds: 0,
            StockDrawMilliseconds: 0,
            PlanDrawMilliseconds: 0,
            ReviewFinalizeMilliseconds: 0,
            TransferDirection: "mixed",
            PlanEditorOpen: false,
            PlanEditorHasUnsavedChanges: false,
            ItemGroupCount: 2,
            SelectedItemGroupId: null,
            SelectedItemGroupName: null,
            ItemGroupEditorOpen: false,
            ItemGroupEditorHasUnsavedChanges: false,
            Owner: "Wei Ning @ Maduin",
            OwnerScopeAvailable: true,
            ObservedRetainerCount: 2,
            OldestRetainerObservedAtUtc: DateTimeOffset.UtcNow,
            PlanLineCount: 3,
            EnabledPlanLineCount: 2,
            TransferPlanCount: 1,
            SelectedTransferPlanId: null,
            SelectedTransferPlanName: "General",
            SelectedTransferRetrieveQuantity: 12,
            SelectedTransferDepositQuantity: 4,
            TransferEditorOpen: false,
            CurrentOperationId: "operation",
            CurrentOperationStatus: "accepted",
            RefreshAvailable: true,
            RefreshActive: false,
            RefreshStatus: "Ready",
            TransferActive: false,
            ListingNavigationActive: false,
            ListingNavigationStatus: string.Empty,
            LastListingRefreshRetainerId: null,
            LastListingRefreshCompletedAtUtc: null,
            LastListingObservedToAppliedMilliseconds: null,
            LastListingActionToAppliedMilliseconds: null,
            LastListingPersistedAtUtc: null,
            LastListingObservedToPersistedMilliseconds: null,
            LastListingWriteMilliseconds: null,
            VendorProcurementPhase: "Paused",
            VendorProcurementMessage: "Review current inventory.",
            VendorPurchasedQuantity: 12,
            VendorSpentGil: 2_400);
        var openedTarget = string.Empty;
        var invoked = false;
        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register(
            "quartermaster.refresh-retainers",
            "Refresh retainers",
            AgentBridgeUiControlKind.Button,
            Vector2.Zero,
            Vector2.One,
            enabled: true,
            selected: false,
            "Ready",
            () => invoked = true);
        registry.EndFrame();
        var provider = new QuartermasterBridgeProvider(() => truth, target => openedTarget = target, () => { }, registry);

        Assert.Same(truth, provider.CreateTruth());
        Assert.Equal(["transfer", "transfer-review", "vendor-review", "item-groups", "listings", "activity"], provider.GetReviewSurfaces().Select(surface => surface.Id));
        Assert.Equal(["transfer", "transfer-review", "vendor-review", "item-groups", "listings", "activity"], provider.GetCaptureSurfaces().Select(surface => surface.Id));
        Assert.True(provider.GetCaptureSurfaces().Single(surface => surface.Id == "transfer").IsDefault);
        Assert.All(provider.GetReviewSurfaces(), surface => Assert.Equal("open-main-window", surface.Command));
        Assert.True(provider.TryOpenMainWindow("listings"));
        Assert.Equal("listings", openedTarget);
        Assert.True(provider.TryOpenMainWindow("stowage"));
        Assert.Equal("stowage", openedTarget);
        Assert.True(provider.TryOpenMainWindow("transfer"));
        Assert.Equal("transfer", openedTarget);
        Assert.True(provider.TryOpenMainWindow("transfer-review"));
        Assert.Equal("transfer-review", openedTarget);
        Assert.True(provider.TryOpenMainWindow("vendor-review"));
        Assert.Equal("vendor-review", openedTarget);
        Assert.True(provider.TryOpenMainWindow("item-groups"));
        Assert.Equal("item-groups", openedTarget);
        Assert.True(provider.TryOpenMainWindow("stock-and-plan"));
        Assert.Equal("stock-and-plan", openedTarget);
        Assert.False(provider.TryOpenMainWindow("unknown"));
        Assert.Equal("stock-and-plan", openedTarget);
        var review = provider.ReviewControl("quartermaster.refresh-retainers");
        Assert.NotNull(review.Control);
        Assert.True(provider.InvokeControl("quartermaster.refresh-retainers", review.FrameId).Success);
        Assert.True(invoked);
    }
}
