using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using Lumina.Excel.Sheets;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

public sealed class QuartermasterWindow : Window
{
    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly IDataManager dataManager;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly WorkbenchState workbench = new();
    private readonly TableSelectionModel<uint> stockSelection = new();
    private readonly DalamudTableProjection<StockWorkbenchRow> stockTable;
    private readonly DalamudTableProjection<RestockPlanRow> restockPlanTable;
    private readonly DalamudTableProjection<RestockPlanItem> restockDraftTable;
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private readonly DalamudTableProjection<StowageDraftRow> stowageDraftTable;
    private readonly DalamudTableProjection<OperationLine> operationLineTable;
    private readonly BrowserQueryController queries = new();
    private readonly RootConfirmationDialog confirmationDialog = new();
    private readonly OperationHistoryDialog historyDialog;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly ListingWorkspace listingWorkspace;
    private readonly TransferReviewDialog transferReviewDialog;
    private readonly VendorProcurementReviewDialog vendorReviewDialog;
    private StockWorkbenchProjection? stockWorkbenchProjection;
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private long stockSelectionRevision = -1;
    private int stockProjectionBuildCount;
    private int transferProjectionBuildCount;
    private string transferStatus = "No transfer has run.";
    private WorkbenchView? requestedView;
    private Task? activeTransferTask;
    private bool clearAgentReviewWindowOverride;
    private int captureCollapseRestoreFramesRemaining;
    private int viewportReopenGuardFramesRemaining;
    private bool viewportReopenGuardNeedsRelease;
    private StowagePlanDraft? stowageDraft;
    private readonly TableSelectionModel<Guid> selectedStowageRuleIds = new();
    private Guid? activeStowageRuleId;
    private Guid? selectedStowageItemGroupId;
    private string stowageItemSearch = string.Empty;
    private string stowageRuleFilter = string.Empty;
    private ItemChoice? selectedStowageChoice;
    private string stowageEditorError = string.Empty;
    private int bulkQuality = -1;
    private int bulkRoutingMode = -1;
    private int bulkOverflow = -1;
    private int bulkStowageTarget = -1;
    private bool requestStowageEditorOpen;
    private bool stowageEditorVisible;
    private RestockPlanDraft? restockDraft;
    private readonly TableSelectionModel<Guid> selectedRestockItemIds = new();
    private Guid? activeRestockItemId;
    private Guid? selectedRestockItemGroupId;
    private string restockItemSearch = string.Empty;
    private string restockItemFilter = string.Empty;
    private ItemChoice? selectedRestockChoice;
    private string restockEditorError = string.Empty;
    private int bulkRestockQuality = -1;
    private int bulkRestockTarget = -1;
    private int bulkRestockNoteMode = -1;
    private bool requestRestockEditorOpen;
    private bool restockEditorVisible;
    private string inlineTransferError = string.Empty;
    private OwnerScope? inlineTransferErrorOwner;
    private Guid? inlineTransferErrorPlanId;
    private string vendorStatus = string.Empty;
    private WorkbenchView? capturePreviousView;
    private TransferReviewDialogState? capturePreviousTransferReviewState;
    private VendorProcurementReviewDialogState? capturePreviousVendorReviewState;
    private PendingTransferPlanRecovery? pendingTransferPlanRecovery;
    private ListingPlanDraft? listingPlanDraft;
    private bool requestListingPlanEditorOpen;
    private bool listingPlanEditorVisible;
    private ListingItemKey? listingPlanEditorFocus;
    private ulong? listingPlanEditorRetainerFilter;
    private Guid? listingPlanEditorAssignmentFilter;
    private string listingPlanEditorFilter = string.Empty;
    private readonly Dictionary<Guid, string> listingPlanPriceText = [];
    private string listingPlanItemSearch = string.Empty;
    private ItemChoice? selectedListingPlanChoice;
    private string listingPlanEditorError = string.Empty;
    private IReadOnlyList<ListingPlanValidationIssue> listingPlanEditorConflicts = [];

    public QuartermasterWindow(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        OperationJournal journal,
        TransferCoordinator transfers,
        RetainerRefreshCoordinator retainerRefresh,
        ListingNavigationCoordinator listingNavigation,
        TransferVendorProcurementService vendorProcurement,
        IDataManager dataManager,
        PluginConfiguration configuration,
        System.Action saveConfiguration,
        AgentBridgeUiReviewRegistry reviewRegistry)
        : base("Quartermaster###RQMain", ImGuiWindowFlags.NoScrollbar)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.journal = journal;
        this.transfers = transfers;
        this.retainerRefresh = retainerRefresh;
        this.vendorProcurement = vendorProcurement;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.reviewRegistry = reviewRegistry;
        historyDialog = new(journal);
        itemGroupWorkspace = new(
            state,
            reviewRegistry,
            confirmationDialog,
            SearchItems,
            (origin, groupId) =>
            {
                if (origin == ItemGroupEditorOrigin.Restock)
                    selectedRestockItemGroupId = groupId;
                else
                    selectedStowageItemGroupId = groupId;
            },
            groupId =>
            {
                if (selectedRestockItemGroupId == groupId)
                    selectedRestockItemGroupId = null;
                if (selectedStowageItemGroupId == groupId)
                    selectedStowageItemGroupId = null;
            });
        listingWorkspace = new(
            workbench,
            listingNavigation,
            reviewRegistry,
            FocusStockFromListings,
            OpenListingPlanEditor,
            stockSelection.Clear);
        transferReviewDialog = new(
            () => runtimeSnapshots.Current,
            ResolveTransferWorkbenchProjection,
            () => transfers.CanStart,
            () => retainerRefresh.IsRefreshing || retainerRefresh.IsQueued,
            planId => ExecuteTransferPlan(planId),
            reviewRegistry);
        vendorReviewDialog = new(vendorProcurement, reviewRegistry, () => vendorStatus = string.Empty);
        stockTable = CreateStockTable();
        restockPlanTable = CreateRestockPlanTable();
        restockDraftTable = CreateRestockDraftTable();
        transferWorkbenchTable = CreateTransferWorkbenchTable();
        stowageDraftTable = CreateStowageDraftTable();
        operationLineTable = CreateOperationLineTable();
        captureTransactions = new(
            () => IsOpen,
            value => IsOpen = value,
            () => Collapsed == true,
            value =>
            {
                captureCollapseRestoreFramesRemaining = 0;
                Collapsed = value;
                CollapsedCondition = ImGuiCond.Always;
            },
            RestoreCaptureCollapseState,
            beginPresentation: BeginCapturePresentation,
            restorePresentation: RestoreCapturePresentation);
        BgAlpha = 1f;
        Size = new Vector2(1280, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(980, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    private DalamudTableProjection<StockWorkbenchRow> CreateStockTable() => new(
    [
        new(
            "Item",
            1.8f,
            row => row.Item.ItemName,
            row => row.Item.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "item"),
        new(
            "Player",
            80,
            row => row.Item.PlayerQuantity.ToString("N0"),
            row => row.Item.PlayerQuantity,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "player"),
        new(
            "Retainers",
            84,
            row => row.AccessibleRetainerQuantity.ToString("N0"),
            row => row.AccessibleRetainerQuantity,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "retainers",
            HeaderTooltip: "Current matching quantity in accessible retainer storage."),
        new(
            "Listing shortfall",
            138,
            row => StockListingShortfall(row.ListingDemand),
            row => StockListingShortfall(row.ListingDemand),
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "listing-shortfall",
            HeaderTooltip: "Units still needed by linked Listing Plans."),
        new(
            "Target",
            80,
            row => row.Rule?.TargetQuantity.ToString("N0") ?? "—",
            row => row.Rule?.TargetQuantity ?? -1,
            TextColor: row => row.Rule is null
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : null,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "target"),
        new(
            "Plan state",
            1f,
            StockPlanState,
            row => StockPlanState(row),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: row => row.Rule is null
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : TransferPresentation.ActionColor(row.Line?.Action),
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "plan-state"),
    ]);

    private DalamudTableProjection<RestockPlanRow> CreateRestockPlanTable() => new(
    [
        new(
            "State",
            44,
            row => row.Item.Enabled ? "On" : "Off",
            TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new(
            "Item",
            1.8f,
            row => row.Item.ItemName,
            row => row.Item.ItemName,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: DrawRestockPlanItemLink),
        new(
            "Have / goal",
            112,
            row => $"{row.Line?.PlayerQuantity ?? 0:N0} / {row.Item.TargetQuantity:N0}"),
        new(
            "Need / stored",
            104,
            row => $"{row.Line?.NeededQuantity ?? 0:N0} / {row.Line?.CachedRetainerQuantity ?? 0:N0}"),
        new(
            "Notes",
            1.2f,
            row => row.Item.Notes,
            row => row.Item.Notes,
            ImGuiTableColumnFlags.WidthStretch),
    ]);

    private DalamudTableProjection<RestockPlanItem> CreateRestockDraftTable() => new(
    [
        new(
            "Item",
            1.5f,
            item => item.ItemName,
            item => item.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new("Rule", 52, item => item.Enabled ? "On" : "Off", Draw: item =>
            item.Enabled = DrawRuleToggle($"restock{item.Id}", item.Enabled)),
        new("Target", 80, item => item.TargetQuantity.ToString("N0"), Draw: DrawRestockTarget),
        new("Quality", 112, item => QualityLabel(item.Quality), Draw: DrawRestockDraftQuality),
        new(
            "Note",
            1f,
            item => item.Notes,
            item => item.Notes,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: DrawRestockNote),
        new(
            "Item group",
            .8f,
            RestockItemGroups,
            item => RestockItemGroups(item),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new("", 28, _ => string.Empty, Draw: DrawRestockDraftRemove),
    ]);

    private DalamudTableProjection<TransferWorkbenchRow> CreateTransferWorkbenchTable() => new(
    [
        new(
            "Item",
            1.5f,
            row => $"{row.Rule.ItemName} {QualityLabel(row.Rule.Quality)}",
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
            Draw: row =>
            {
                ImGui.TextUnformatted(row.Rule.ItemName);
                ImGui.SameLine();
                ImGui.TextDisabled(QualityLabel(row.Rule.Quality));
            },
            Id: "item"),
        new(
            "On player",
            64,
            row => row.PlayerQuantity.ToString("N0"),
            row => row.PlayerQuantity,
            Id: "player"),
        new(
            "Target",
            184,
            TransferTargetText,
            row => row.ListingContribution.IsKnown ? row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity : row.Rule.TargetQuantity,
            Draw: DrawTransferTarget,
            Id: "target",
            HeaderTooltip: "Desired player quantity with its signed delta from current player stock."),
        new(
            "Accessible storage",
            128,
            row => row.AccessibleStorageQuantity.ToString("N0"),
            row => row.AccessibleStorageQuantity,
            Id: "accessible-storage",
            HeaderTooltip: "Current matching quantity in accessible retainer storage."),
        new(
            "Outcome",
            156,
            row => TransferOutcome(row).Text,
            row => TransferOutcome(row).Text,
            Draw: DrawTransferOutcome,
            Id: "outcome",
            HeaderTooltip: "Executable result under the stock and capacity evidence currently available."),
        new(
            "Vendor",
            148,
            VendorProcurementText,
            row => row.VendorLine?.ApprovedQuantity ?? 0,
            Draw: DrawVendorProcurement,
            Id: "vendor",
            HeaderTooltip: "Reviewed ordinary-gil coverage for the shortage left after accessible retainer stock."),
        new(
            "Route",
            1.1f,
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawInlineTransferRoute(row.Owner, row.PlanId, row.Rule, row.Runtime),
            Id: "route"),
        new(
            "Listing shortfall",
            118,
            TransferListingShortfall,
            row => row.ListingContribution.IsKnown ? row.ListingContribution.Value : -1,
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
            Id: "listing-shortfall",
            HeaderTooltip: "Units still needed by the linked Listing Plan."),
        new("##remove", 28, _ => string.Empty, Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, Draw: row =>
        {
            if (ImGui.SmallButton($"X##remove-transfer:{row.Rule.Id}"))
                RemoveTransferRule(row.Owner, row.PlanId, row.Rule.Id);
        }, Id: "remove"),
    ]);

    private DalamudTableProjection<StowageDraftRow> CreateStowageDraftTable() => new(
    [
        new(
            "Item",
            1.5f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new("Rule", 52, row => row.Rule.Enabled ? "On" : "Off", Draw: row =>
            row.Rule.Enabled = DrawRuleToggle($"stowage{row.Rule.Id}", row.Rule.Enabled)),
        new("Player target", 92, row => row.Rule.TargetQuantity.ToString("N0"), Draw: DrawStowageTarget),
        new("Quality", 112, row => QualityLabel(row.Rule.Quality), Draw: row => DrawDraftQuality(row.Rule)),
        new(
            "Vendor",
            72,
            row => row.Rule.AllowVendorPurchase ? "Allowed" : "Off",
            Draw: row => DrawVendorPurchaseToggle(row.Rule)),
        new(
            "Now",
            92,
            StowageDraftOutcome,
            TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new(
            "Destination",
            1.1f,
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner),
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawStowageRouteCombo(row.Rule, row.Runtime)),
        new("Overflow", 112, row => TransferPresentation.OverflowLabel(row.Rule.Routing.Overflow), Draw: row => DrawStowageOverflowCombo(row.Rule)),
        new("", 28, _ => string.Empty, Draw: DrawStowageDraftRemove),
    ]);

    private static DalamudTableProjection<OperationLine> CreateOperationLineTable() => new(
    [
        new("Item", 1f, line => line.ItemName, line => line.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Target", 80, line => line.TargetQuantity.ToString("N0"), line => line.TargetQuantity),
        new("Submitted shortage", 120, line => line.ShortageQuantity.ToString("N0"), line => line.ShortageQuantity),
        new("Transferred", 90, line => line.TransferredQuantity.ToString("N0"), line => line.TransferredQuantity),
        new("Remaining", 90, line => Math.Max(0, line.ShortageQuantity - line.TransferredQuantity).ToString("N0"), line => Math.Max(0, line.ShortageQuantity - line.TransferredQuantity)),
    ]);

    public bool StockBrowserVisible =>
        IsOpen && workbench.View is not (WorkbenchView.Listings or WorkbenchView.Activity);

    private int VisibleStockCount { get; set; }
    private int RenderedStockRowCount { get; set; }
    private int RenderedTransferRowCount { get; set; }
    private double WindowDrawMilliseconds { get; set; }
    private double ContentDrawMilliseconds { get; set; }
    private double StockDrawMilliseconds { get; set; }
    private double PlanDrawMilliseconds { get; set; }
    private double ReviewFinalizeMilliseconds { get; set; }
    public AgentBridgeCaptureRegion? AgentCaptureRegion { get; private set; }

    internal QuartermasterUiSnapshot CreateUiSnapshot()
    {
        var runtime = runtimeSnapshots.Current;
        var document = state.Snapshot();
        var selectedPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
        var selectedEvaluation = selectedPlan is null
            ? null
            : StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, runtime.Owner, selectedPlan.Id);
        var stowageEditorOpen = stowageDraft is not null && (requestStowageEditorOpen || stowageEditorVisible);
        var restockEditorOpen = restockDraft is not null && (requestRestockEditorOpen || restockEditorVisible);

        var itemGroups = itemGroupWorkspace.Snapshot(
            IsOpen && workbench.View == WorkbenchView.ItemGroups);
        return new QuartermasterUiSnapshot(
            IsOpen,
            workbench.View is WorkbenchView.Listings or WorkbenchView.Activity
                ? workbench.View.ToString().ToLowerInvariant()
                : "transfer",
            workbench.ItemFilterState.Expression,
            VisibleStockCount,
            RenderedStockRowCount,
            stockProjectionBuildCount,
            stockTable.ApplyCount,
            transferProjectionBuildCount,
            RenderedTransferRowCount,
            WindowDrawMilliseconds,
            ContentDrawMilliseconds,
            StockDrawMilliseconds,
            PlanDrawMilliseconds,
            ReviewFinalizeMilliseconds,
            "mixed",
            restockEditorOpen || stowageEditorOpen,
            (restockDraft is not null && RestockPlanCatalog.HasChanges(document, runtime.Owner, restockDraft)) ||
            (stowageDraft is not null && StowagePlanCatalog.HasChanges(document, runtime.Owner, stowageDraft)),
            itemGroups.SelectedGroupId,
            itemGroups.SelectedGroupName,
            itemGroups.WorkspaceEditorOpen,
            itemGroups.HasUnsavedChanges,
            selectedPlan?.Id,
            selectedPlan?.Name,
            selectedEvaluation?.RetrieveQuantity ?? 0,
            selectedEvaluation?.DepositQuantity ?? 0,
            stowageEditorOpen);
    }

    public override void Draw()
    {
        var drawStarted = Stopwatch.GetTimestamp();
        ClearAgentReviewWindowOverride();
        reviewRegistry.BeginFrame();
        try
        {
            var viewport = ImGui.GetWindowViewport();
            var windowPosition = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            if (windowSize.X > 0f && windowSize.Y > 0f && viewport.Size.X > 0f && viewport.Size.Y > 0f)
            {
                AgentCaptureRegion = new AgentBridgeCaptureRegion(
                    windowPosition,
                    windowSize,
                    viewport.ID,
                    viewport.Pos,
                    viewport.Size,
                    DateTimeOffset.UtcNow);
            }
            var contentStarted = Stopwatch.GetTimestamp();
            DrawContent();
            ContentDrawMilliseconds = Stopwatch.GetElapsedTime(contentStarted).TotalMilliseconds;
        }
        finally
        {
            var reviewStarted = Stopwatch.GetTimestamp();
            var frame = reviewRegistry.EndFrame();
            if (ActiveCapturePresentationTarget() is { } target)
                captureTransactions.MarkRendered(target, frame.FrameId);
            ReviewFinalizeMilliseconds = Stopwatch.GetElapsedTime(reviewStarted).TotalMilliseconds;
            WindowDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
        }
    }

    public override void PreDraw()
    {
        ReleaseRestoredCaptureCollapseOverride();
        ApplyViewportReopenGuard();
    }

    private void ApplyViewportReopenGuard()
    {
        if (viewportReopenGuardFramesRemaining > 0)
        {
            ApplyWindowClass(ImGuiViewportFlags.NoAutoMerge);
            viewportReopenGuardFramesRemaining--;
            return;
        }

        if (!viewportReopenGuardNeedsRelease)
            return;

        ApplyWindowClass(ImGuiViewportFlags.None);
        viewportReopenGuardNeedsRelease = false;
    }

    private static void ApplyWindowClass(ImGuiViewportFlags viewportFlags)
    {
        var windowClass = new ImGuiWindowClass
        {
            ParentViewportId = uint.MaxValue,
            ViewportFlagsOverrideSet = viewportFlags,
            DockingAllowUnclassed = 1,
        };
        ImGui.SetNextWindowClass(ref windowClass);
    }

    private void RestoreCaptureCollapseState(bool wasOpen, bool wasCollapsed)
    {
        if (!wasOpen)
        {
            captureCollapseRestoreFramesRemaining = 0;
            Collapsed = null;
            CollapsedCondition = ImGuiCond.None;
            return;
        }

        Collapsed = wasCollapsed;
        CollapsedCondition = ImGuiCond.Always;
        captureCollapseRestoreFramesRemaining = 2;
    }

    private void ReleaseRestoredCaptureCollapseOverride()
    {
        if (captureCollapseRestoreFramesRemaining <= 0)
            return;

        captureCollapseRestoreFramesRemaining--;
        if (captureCollapseRestoreFramesRemaining > 0)
            return;

        Collapsed = null;
        CollapsedCondition = ImGuiCond.None;
    }

    public AgentBridgeUiCaptureTransactionHandle BeginAgentCapturePresentation(string target) =>
        captureTransactions.Begin(target);

    public AgentBridgeUiCaptureTransactionResult CompleteAgentCapturePresentation(string transactionId) =>
        captureTransactions.Complete(transactionId);

    public AgentBridgeUiCaptureTransactionResult CancelAgentCapturePresentation(string transactionId) =>
        captureTransactions.Cancel(transactionId);

    private string? ActiveCapturePresentationTarget()
    {
        foreach (var target in new[] { "transfer", "transfer-review", "vendor-review", "item-groups", "listings", "activity" })
            if (captureTransactions.ShouldPresent(target))
                return target;
        return null;
    }

    private void BeginCapturePresentation()
    {
        capturePreviousView = workbench.View;
        capturePreviousTransferReviewState = transferReviewDialog.CaptureState();
        capturePreviousVendorReviewState = vendorReviewDialog.CaptureState();
        var target = ActiveCapturePresentationTarget();
        if (target == "activity")
            historyDialog.BeginCapturePresentation();
        requestedView = target switch
        {
            "listings" => WorkbenchView.Listings,
            "item-groups" => WorkbenchView.ItemGroups,
            "activity" => WorkbenchView.Activity,
            _ => WorkbenchView.Stowage,
        };
        if (target == "transfer-review")
            RequestSelectedTransferReview();
        if (target == "vendor-review")
            RequestSelectedVendorReview();
    }

    private void RestoreCapturePresentation()
    {
        if (capturePreviousView is { } previous)
            requestedView = previous;
        capturePreviousView = null;
        if (capturePreviousTransferReviewState is { } previousTransferReviewState)
            transferReviewDialog.RestoreState(previousTransferReviewState);
        capturePreviousTransferReviewState = null;
        if (capturePreviousVendorReviewState is { } previousVendorReviewState)
            vendorReviewDialog.RestoreState(previousVendorReviewState);
        capturePreviousVendorReviewState = null;
        historyDialog.RestoreCapturePresentation();
    }

    private void DrawContent()
    {
        var runtime = runtimeSnapshots.Current;
        workbench.EnsureScope(runtime.Browser);
        if (requestedView is { } requested)
        {
            if (requested == WorkbenchView.Activity)
            {
                historyDialog.RequestOpen();
                requestedView = WorkbenchView.Stowage;
            }
            if (requested == WorkbenchView.Listings)
                CloseStowageEditor();
            CloseRestockEditor();
        }

        ImGui.TextUnformatted(runtime.Owner.HasStableIdentity ? $"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName}" : "Owner scope unavailable");
        var historyWidth = ImGui.CalcTextSize("History").X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - historyWidth));
        if (ImGui.SmallButton("History"))
            historyDialog.RequestOpen();
        reviewRegistry.RegisterLastButton(
            "quartermaster.history.open",
            "Open transfer history",
            true,
            historyDialog.RequestOpen,
            "Recent Quartermaster operations");
        historyDialog.Draw(runtime.Owner);

        if (ImGui.BeginTabBar("RQViews"))
        {
            var plansRequested = requestedView is WorkbenchView.Stock or WorkbenchView.Restock or WorkbenchView.Stowage or WorkbenchView.ItemGroups;
            var stockOpen = ImGui.BeginTabItem("Stock & plans", plansRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            reviewRegistry.RegisterLastButton(
                "quartermaster.workspace.stock",
                "Show stock and plans",
                true,
                () => requestedView = WorkbenchView.Stock,
                workbench.View is not (WorkbenchView.Listings or WorkbenchView.Activity) ? "Selected" : "Available");
            if (stockOpen)
            {
                if (requestedView == WorkbenchView.ItemGroups)
                    workbench.View = WorkbenchView.ItemGroups;
                else if (requestedView is WorkbenchView.Stock or WorkbenchView.Restock or WorkbenchView.Stowage ||
                         workbench.View is WorkbenchView.Listings or WorkbenchView.Activity)
                    workbench.View = WorkbenchView.Stowage;
                DrawStockAndPlans(runtime);
                ImGui.EndTabItem();
            }
            var listingsOpen = ImGui.BeginTabItem($"Listings ({runtime.Browser.Listings.Count:N0})", requestedView == WorkbenchView.Listings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            RegisterWorkspaceControl("listings", WorkbenchView.Listings);
            if (listingsOpen)
            {
                workbench.View = WorkbenchView.Listings;
                listingWorkspace.Draw(runtime);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
        DrawStowageEditorModal(runtime);
        DrawListingPlanEditorModal(runtime);
        transferReviewDialog.Draw();
        vendorReviewDialog.Draw();
        confirmationDialog.Draw();
    }

    private void DrawStockAndPlans(QuartermasterRuntimeSnapshot runtime)
    {
        var flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQStockAndPlans", 2, flags, new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Stock", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Plans", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQStockPane", Vector2.Zero, false))
            DrawStock(runtime);
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQPlanPane", Vector2.Zero, false))
        {
            var transferSelected = workbench.View != WorkbenchView.ItemGroups;
            if (ImGui.Selectable("Transfer plan##RQPlannerMode", transferSelected, ImGuiSelectableFlags.None, new Vector2(112, 0)))
                workbench.View = WorkbenchView.Stowage;
            reviewRegistry.RegisterLastButton(
                "quartermaster.workspace.transfer",
                "Show Transfer Plan workspace",
                true,
                () => workbench.View = WorkbenchView.Stowage,
                transferSelected ? "Selected" : "Available");
            ImGui.SameLine();
            if (ImGui.Selectable("Item groups##RQPlannerMode", !transferSelected, ImGuiSelectableFlags.None, new Vector2(104, 0)))
                workbench.View = WorkbenchView.ItemGroups;
            reviewRegistry.RegisterLastButton(
                "quartermaster.workspace.item-groups",
                "Show Item Groups workspace",
                true,
                () => workbench.View = WorkbenchView.ItemGroups,
                transferSelected ? "Available" : "Selected");
            ImGui.SameLine();
            ImGui.TextDisabled("Stock stays available while you plan.");
            ImGui.Separator();
            var planStarted = Stopwatch.GetTimestamp();
            if (workbench.View == WorkbenchView.ItemGroups)
                itemGroupWorkspace.DrawWorkspace(runtime.State);
            else
            {
                workbench.View = WorkbenchView.Stowage;
                DrawStowageWorkspace(runtime);
            }
            PlanDrawMilliseconds = Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
        }
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void RegisterWorkspaceControl(string id, WorkbenchView view) =>
        reviewRegistry.RegisterLastButton(
            $"quartermaster.workspace.{id}",
            $"Show {view} workspace",
            true,
            () => requestedView = view,
            workbench.View == view ? "Selected" : "Available");

    public void OpenReviewSurface(string target)
    {
        var normalizedTarget = target.Trim().ToLowerInvariant();
        var view = normalizedTarget switch
        {
            "listings" => WorkbenchView.Listings,
            "item-groups" or "groups" => WorkbenchView.ItemGroups,
            "operation" or "activity" => WorkbenchView.Activity,
            _ => WorkbenchView.Stowage,
        };
        if (view is WorkbenchView.Listings or WorkbenchView.Activity)
            CloseStowageEditor();
        CloseRestockEditor();
        requestedView = view;
        if (normalizedTarget == "transfer-review")
            RequestSelectedTransferReview();
        if (normalizedTarget == "vendor-review")
            RequestSelectedVendorReview();
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        clearAgentReviewWindowOverride = true;
    }

    public void CloseReviewSurface()
    {
        ClosePlanEditors();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    public override void OnOpen()
    {
        // Keep the first two active frames out of ImGui's auto-merge scan. This gives a
        // platform viewport retired by an earlier close one full frame to become inactive,
        // without changing where the user placed the window or disabling later docking.
        viewportReopenGuardFramesRemaining = 2;
        viewportReopenGuardNeedsRelease = true;
    }

    public override void OnClose()
    {
        ClosePlanEditors();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    private void ClearAgentReviewWindowOverride()
    {
        if (!clearAgentReviewWindowOverride)
            return;
        Collapsed = null;
        CollapsedCondition = ImGuiCond.None;
        clearAgentReviewWindowOverride = false;
    }

    private void DrawStock(QuartermasterRuntimeSnapshot runtime)
    {
        var drawStarted = Stopwatch.GetTimestamp();
        var projection = runtime.Browser;
        var availableItems = StockItemsWithListingDemand(runtime, workbench.ScopeKey);
        var queryProjection = new BrowserProjection
        {
            Scopes = projection.Scopes,
            Items = availableItems,
            Listings = projection.Listings,
            Owner = projection.Owner,
            RetainerInventoryCompleteByScope = projection.RetainerInventoryCompleteByScope,
            RetainerListingsCompleteByScope = projection.RetainerListingsCompleteByScope,
        };
        DrawStockToolbar(projection, availableItems);
        var result = queries.QueryItems(
            queryProjection,
            workbench.ItemFilterState.Expression,
            BrowserScope.AllKey,
            workbench.ItemFilterState.IsInputActive,
            runtime.Revision);
        var visibleItems = ListingPlanPresentation.ApplyStockItemFocus(result.Items, workbench.FocusedStockItemId);
        VisibleStockCount = visibleItems.Count;
        if (!result.Filter.IsValid)
            ImGui.TextColored(
                new Vector4(1f, .65f, .25f, 1f),
                result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        if (stockSelectionRevision != runtime.Revision)
        {
            stockSelection.Retain(availableItems.Select(item => item.ItemId));
            stockSelectionRevision = runtime.Revision;
        }
        var selectedPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
        var sourceRows = ResolveStockWorkbenchProjection(runtime, visibleItems, selectedPlan);
        DrawStockSelectionBar(runtime, availableItems);
        DrawTableColumnsToolbar(stockTable, "RQStockColumns", "Visible stock");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var tableHeight = Math.Max(180, ImGui.GetContentRegionAvail().Y);
        RenderedStockRowCount = 0;
        if (stockTable.Begin(
                "RQStockWorkbenchV4",
                new DalamudTableLayout(
                    new Vector2(0, tableHeight),
                    flags,
                    FreezeRows: 1)))
        {
            IReadOnlyList<StockWorkbenchRow> rows;
            unsafe
            {
                rows = stockTable.Apply(sourceRows, ImGui.TableGetSortSpecs());
            }
            var rowKeys = rows.Select(row => row.Item.ItemId).ToArray();
            RenderedStockRowCount = stockTable.DrawClippedRows(
                rows,
                (row, rowIndex) =>
                {
                    stockTable.DrawSelectableRow(
                        row,
                        stockSelection,
                        rowKeys,
                        rowIndex,
                        $"##stock-row:{row.Item.ItemId}");
                    var stockSelected = stockSelection.IsSelected(row.Item.ItemId);
                    reviewRegistry.RegisterLastButton(
                        $"quartermaster.stock.select.{row.Item.ItemId}",
                        $"Select {row.Item.ItemName} for stock actions",
                        true,
                        () => stockSelection.SetSelected(
                            row.Item.ItemId,
                            !stockSelection.IsSelected(row.Item.ItemId)),
                        stockSelected ? "Selected" : "Available");
                });
            DalamudTableSelectionRenderer.EndRows(stockSelection);
            stockTable.End();
        }
        StockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
    }

    private static IReadOnlyList<StockGroup> StockItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, string scopeKey)
    {
        var stock = runtime.Browser.GetItems(scopeKey);
        var plan = ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner);
        if (plan is null)
            return stock;

        var existing = stock.Select(item => item.ItemId).ToHashSet();
        var planned = plan.Assignments
            .Where(assignment => assignment.Enabled && !existing.Contains(assignment.ItemId))
            .Where(assignment => ScopeIncludesAssignment(runtime, scopeKey, assignment))
            .GroupBy(assignment => assignment.ItemId)
            .Select(group => new StockGroup(
                group.Key,
                group.Select(assignment => assignment.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {group.Key}",
                []))
            .ToArray();
        if (planned.Length == 0)
            return stock;

        return stock.Concat(planned)
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToArray();

        static bool ScopeIncludesAssignment(QuartermasterRuntimeSnapshot snapshot, string selectedScopeKey, ListingPlanAssignment assignment)
        {
            var scope = snapshot.Browser.Scopes.FirstOrDefault(candidate => candidate.Key == selectedScopeKey);
            return scope?.Kind != BrowserScopeKind.Retainer || scope.RetainerId == assignment.RetainerId;
        }
    }

    private IReadOnlyList<StockWorkbenchRow> ResolveStockWorkbenchProjection(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<StockGroup> queryItems,
        StowagePlan? selectedPlan)
    {
        if (stockWorkbenchProjection is { } cached &&
            cached.RuntimeRevision == runtime.Revision &&
            cached.PlanId == selectedPlan?.Id &&
            cached.QueryItems.Select(item => item.ItemId).SequenceEqual(queryItems.Select(item => item.ItemId)))
            return cached.Rows;

        var rules = selectedPlan is null
            ? new Dictionary<uint, TargetPlanItem>()
            : runtime.State.PlanItems
                .Where(rule => rule.StowagePlanId == selectedPlan.Id)
                .GroupBy(rule => rule.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
        var evaluated = selectedPlan is null
            ? new Dictionary<Guid, StowageEvaluationLine>()
            : runtime.Stowage
                .FirstOrDefault(plan => plan.PlanId == selectedPlan.Id)?
                .Lines.ToDictionary(line => line.RuleId) ?? [];
        var listingEvaluation = ListingPlanEvaluator.Evaluate(
            ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner),
            runtime.Browser);
        var rows = queryItems
            .Select(item =>
            {
                rules.TryGetValue(item.ItemId, out var rule);
                evaluated.TryGetValue(rule?.Id ?? Guid.Empty, out var line);
                var demand = listingEvaluation.Items.Where(candidate => candidate.ItemId == item.ItemId).ToArray();
                var accessibleRetainerQuantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    item,
                    ItemQualityPolicy.Any,
                    runtime.Retainers,
                    runtime.Owner);
                return new StockWorkbenchRow(item, accessibleRetainerQuantity, rule, line, demand);
            })
            .ToArray();
        stockWorkbenchProjection = new(runtime.Revision, selectedPlan?.Id, queryItems, rows);
        stockProjectionBuildCount++;
        return rows;
    }

    private void DrawStockToolbar(BrowserProjection projection, IReadOnlyList<StockGroup> sourceItems)
    {
        var context = BrowserQueryController.CreateItemContext(sourceItems, projection.Owner);
        var trailingWidth = 180f;
        var filterBeforeDraw = workbench.ItemFilterState.Expression;
        DalamudFilterAutocompleteRenderer.Draw(
            "RQStockWorkbench",
            "Search accessible stock by item name",
            context,
            workbench.ItemFilterState,
            Math.Max(220, ImGui.GetContentRegionAvail().X - trailingWidth));
        if (!string.Equals(filterBeforeDraw, workbench.ItemFilterState.Expression, StringComparison.Ordinal))
            workbench.FocusedStockItemId = null;
        reviewRegistry.RegisterLastAction(
            "quartermaster.stock.search",
            "Search accessible stock",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            workbench.ItemFilterState.Expression,
            new AgentBridgeActionArgumentSchema(
                [new("query", AgentBridgeActionArgumentKind.String, Required: false)]),
            arguments =>
            {
                var query = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
                            value.TryGetProperty("query", out var queryValue)
                    ? queryValue.GetString()
                    : string.Empty;
                workbench.ItemFilterState.SetExpression(query);
                workbench.FocusedStockItemId = null;
                return AgentBridgeUiActionResult.Ok("Stock search updated.");
            });

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        var selectedScope = projection.Scopes.First(scope => scope.Key == workbench.ScopeKey);
        if (ImGui.BeginCombo("##RQStockScope", selectedScope.Label))
        {
            foreach (var scope in projection.Scopes)
            {
                if (ImGui.Selectable($"{scope.Label}##stock-scope:{scope.Key}", scope.Key == workbench.ScopeKey))
                {
                    workbench.ScopeKey = scope.Key;
                    workbench.FocusedStockItemId = null;
                    stockSelection.Clear();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("…##RQStockOptions"))
            ImGui.OpenPopup("RQStockOptions");
        if (ImGui.BeginPopup("RQStockOptions"))
        {
            ImGui.TextDisabled("Included player storage");
            var includeCrystals = configuration.IncludeCrystals;
            var includeEquipped = configuration.IncludeEquipped;
            var includeArmoury = configuration.IncludeArmoury;
            var includeSaddlebag = configuration.IncludeSaddlebag;
            var changed = false;
            changed |= ImGui.Checkbox("Crystals", ref includeCrystals);
            changed |= ImGui.Checkbox("Equipped", ref includeEquipped);
            changed |= ImGui.Checkbox("Armoury", ref includeArmoury);
            changed |= ImGui.Checkbox("Saddlebags", ref includeSaddlebag);
            if (changed)
            {
                configuration.IncludeCrystals = includeCrystals;
                configuration.IncludeEquipped = includeEquipped;
                configuration.IncludeArmoury = includeArmoury;
                configuration.IncludeSaddlebag = includeSaddlebag;
                saveConfiguration();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (!retainerRefresh.CanStart)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Refresh retainers"))
            retainerRefresh.Start();
        if (!retainerRefresh.CanStart)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.refresh-retainers",
            "Refresh retainers",
            retainerRefresh.CanStart,
            () => retainerRefresh.Start(),
            retainerRefresh.Status);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh all Quartermaster retainer inventory caches.");
        if (retainerRefresh.CanCancel)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel refresh"))
                retainerRefresh.Cancel();
        }
        else if (retainerRefresh.HasRecovery)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Retry refresh"))
                retainerRefresh.Retry();
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss##RetainerRefreshRecovery"))
                DismissRetainerRefreshRecovery();
        }
        if (retainerRefresh.IsRefreshing || retainerRefresh.HasRecovery ||
            retainerRefresh.Results.Any(result => result.Outcome == "NotAccessible"))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(retainerRefresh.Status);
            if (ImGui.IsItemHovered() && retainerRefresh.Results.Count != 0)
            {
                ImGui.BeginTooltip();
                foreach (var result in retainerRefresh.Results)
                    ImGui.TextUnformatted($"{result.RetainerName}: {result.Outcome}");
                ImGui.EndTooltip();
            }
        }
    }

    private void DrawStockSelectionBar(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> availableItems)
    {
        var selected = availableItems
            .Where(item => stockSelection.IsSelected(item.ItemId))
            .ToArray();
        if (selected.Length != stockSelection.Count)
        {
            stockSelection.Retain(availableItems.Select(item => item.ItemId));
            selected = availableItems
                .Where(item => stockSelection.IsSelected(item.ItemId))
                .ToArray();
        }

        ImGui.TextUnformatted($"{selected.Length:N0} selected");
        ImGui.SameLine();
        var canPlan = selected.Length > 0 && runtime.Owner.HasStableIdentity;
        if (!canPlan)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add to plan"))
        {
            UpsertSelectedStockRules(runtime, selected, stowCarried: false);
            stockSelection.Clear();
        }
        if (!canPlan)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.add-selected-to-plan",
            "Add selected stock to the current Transfer Plan",
            canPlan,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentItems = StockItemsWithListingDemand(current, workbench.ScopeKey)
                    .Where(item => stockSelection.IsSelected(item.ItemId))
                    .ToArray();
                UpsertSelectedStockRules(current, currentItems, stowCarried: false);
                stockSelection.Clear();
            },
            $"{selected.Length:N0} selected");

        ImGui.SameLine();
        if (selected.Length == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add to item group"))
        {
            AddStockItemsToItemGroup(runtime, selected);
            stockSelection.Clear();
        }
        if (selected.Length == 0)
            ImGui.EndDisabled();

        ImGui.SameLine();
        var carried = selected.Where(item => item.PlayerQuantity > 0).ToArray();
        if (carried.Length == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Stow carried"))
        {
            UpsertSelectedStockRules(runtime, carried, stowCarried: true);
            stockSelection.Clear();
        }
        if (carried.Length == 0)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (selected.Length == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Clear"))
        {
            stockSelection.Clear();
            workbench.FocusedStockItemId = null;
        }
        if (selected.Length == 0)
            ImGui.EndDisabled();

        if (selected.Length == 1)
        {
            var listingPlan = ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner);
            var demand = ListingPlanEvaluator.Evaluate(listingPlan, runtime.Browser).Items
                .Where(item => item.ItemId == selected[0].ItemId && item.IsPlanned)
                .ToArray();
            if (listingPlan is not null && demand.Length > 0)
            {
                ImGui.Separator();
                ImGui.TextUnformatted(selected[0].ItemName);
                ImGui.SameLine();
                if (demand.All(item => item.Quality != workbench.SelectedStockListingQuality))
                    workbench.SelectedStockListingQuality = demand[0].Quality;
                ImGui.SetNextItemWidth(62);
                if (ImGui.BeginCombo("##stocklistingquality", QualityLabel(workbench.SelectedStockListingQuality)))
                {
                    foreach (var item in demand)
                        if (ImGui.Selectable(QualityLabel(item.Quality), item.Quality == workbench.SelectedStockListingQuality))
                            workbench.SelectedStockListingQuality = item.Quality;
                    ImGui.EndCombo();
                }
                var selectedDemand = demand.Single(item => item.Quality == workbench.SelectedStockListingQuality);
                ImGui.TextDisabled(StockListingShortfall([selectedDemand]));
                if (ImGui.SmallButton("Edit assignments##stock"))
                    OpenListingPlanEditor(runtime, new(selectedDemand.ItemId, selectedDemand.Quality));
                ImGui.SameLine();
                var transferPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
                var linked = transferPlan is not null && runtime.State.TransferPlanListingLinks.Any(link =>
                    link.StowagePlanId == transferPlan.Id && link.ListingPlanId == listingPlan.Id &&
                    link.ItemId == selectedDemand.ItemId && link.Quality == selectedDemand.Quality);
                if (linked)
                    ImGui.TextDisabled($"Linked to {transferPlan!.Name}");
                else
                {
                    var destination = transferPlan?.Name ?? "new Transfer Plan";
                    if (ImGui.SmallButton($"Include in {destination}"))
                        SetListingDemandLink(runtime, listingPlan, transferPlan, selectedDemand, linked: true);
                }
            }
        }
    }

    private void DrawTableColumnsToolbar<TRow>(
        DalamudTableProjection<TRow> table,
        string id,
        string context)
    {
        ImGui.TextDisabled(context);
        ImGui.SameLine();
        var buttonWidth = ImGui.CalcTextSize("Columns").X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonWidth));
        table.DrawColumnMenuButton(id);
        reviewRegistry.RegisterLastButton(
            $"quartermaster.{id}.columns",
            $"Manage {context.ToLowerInvariant()} columns",
            true,
            table.RequestColumnMenu,
            "Available");
    }

    private void SetListingDemandLink(
        QuartermasterRuntimeSnapshot runtime,
        ListingPlan listingPlan,
        StowagePlan? selectedTransferPlan,
        ListingPlanItemEvaluation demand,
        bool linked)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var transferPlan = selectedTransferPlan is null
                    ? StowagePlanCatalog.Create(document, runtime.Owner, "Transfer plan")
                    : document.StowagePlans.Single(plan => plan.Id == selectedTransferPlan.Id && plan.Owner.Matches(runtime.Owner));
                if (!linked)
                {
                    ListingPlanCatalog.Unlink(document, runtime.Owner, transferPlan.Id, listingPlan.Id, demand.ItemId, demand.Quality);
                    return transferPlan.Id;
                }
                if (document.PlanItems.Any(rule => rule.StowagePlanId == transferPlan.Id && rule.ItemId == demand.ItemId &&
                                                   rule.Quality == ItemQualityPolicy.Any && rule.Enabled))
                    throw new InvalidOperationException($"{demand.ItemName} has an Any-quality Transfer Plan target. Choose NQ or HQ there before linking exact listing demand.");
                if (document.PlanItems.All(rule => rule.StowagePlanId != transferPlan.Id || rule.ItemId != demand.ItemId || rule.Quality != demand.Quality))
                {
                    document.PlanItems.Add(new TargetPlanItem
                    {
                        StowagePlanId = transferPlan.Id,
                        ItemId = demand.ItemId,
                        ItemName = demand.ItemName,
                        Quality = demand.Quality,
                        TargetQuantity = 0,
                    });
                }
                ListingPlanCatalog.Link(document, runtime.Owner, transferPlan.Id, listingPlan.Id, demand.ItemId, demand.Quality);
                return transferPlan.Id;
            });
            inlineTransferError = string.Empty;
        }
        catch (Exception exception)
        {
            inlineTransferError = exception.Message;
        }
    }

    private void DrawStockRowContextMenu(StockWorkbenchRow row)
    {
        var runtime = runtimeSnapshots.Current;
        var item = row.Item;

        if (!runtime.Owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.MenuItem("Add to plan"))
            UpsertSelectedStockRules(runtime, [item], stowCarried: false);
        if (!runtime.Owner.HasStableIdentity)
            ImGui.EndDisabled();

        if (ImGui.MenuItem("Add to item group"))
            AddStockItemsToItemGroup(runtime, [item]);

        if (item.PlayerQuantity == 0 || !runtime.Owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.MenuItem("Stow carried"))
            UpsertSelectedStockRules(runtime, [item], stowCarried: true);
        if (item.PlayerQuantity == 0 || !runtime.Owner.HasStableIdentity)
            ImGui.EndDisabled();
    }

    private void AddStockItemsToItemGroup(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<StockGroup> items)
    {
        if (items.Count == 0)
            return;

        workbench.View = WorkbenchView.ItemGroups;
        itemGroupWorkspace.AddStockItems(runtime.State, items);
    }

    private void UpsertSelectedStockRules(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<StockGroup> items,
        bool stowCarried)
    {
        if (!runtime.Owner.HasStableIdentity || items.Count == 0)
            return;
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var plan = ResolveSelectedStowagePlan(document, runtime.Owner)
                           ?? StowagePlanCatalog.Create(document, runtime.Owner, "Transfer plan");
                var draft = StowagePlanCatalog.Draft(document, runtime.Owner, plan.Id);
                foreach (var item in items)
                {
                    var rule = draft.Rules.FirstOrDefault(candidate =>
                        candidate.ItemId == item.ItemId &&
                        candidate.Quality == ItemQualityPolicy.Any);
                    if (rule is null)
                    {
                        draft.Rules.Add(new TargetPlanItem
                        {
                            StowagePlanId = plan.Id,
                            ItemId = item.ItemId,
                            ItemName = item.ItemName,
                            TargetQuantity = stowCarried ? 0 : item.PlayerQuantity,
                            Quality = ItemQualityPolicy.Any,
                            Enabled = true,
                        });
                    }
                    else
                    {
                        rule.Enabled = true;
                        if (stowCarried)
                            rule.TargetQuantity = 0;
                    }
                }
                return StowagePlanCatalog.Apply(document, runtime.Owner, draft).Id;
            });
            inlineTransferError = string.Empty;
        }
        catch (Exception exception)
        {
            inlineTransferError = exception.Message;
        }
    }

    private void DrawRestockPlan(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = RestockPlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedRestockPlan(runtime.State, owner);
        if (selected is null && plans.Count > 0)
        {
            selected = plans[0];
            workbench.SelectedRestockPlanId = selected.Id;
        }

        ImGui.SetNextItemWidth(Math.Max(170, ImGui.GetContentRegionAvail().X - 230));
        if (ImGui.BeginCombo("##RQRestockPlan", selected?.Name ?? "Choose a Restock Plan"))
        {
            foreach (var plan in plans)
                if (ImGui.Selectable($"{plan.Name}##restock{plan.Id}", selected?.Id == plan.Id))
                    workbench.SelectedRestockPlanId = plan.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Edit plan") && selected is not null)
            OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), owner, selected.Id));
        if (selected is null)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.restock.edit",
            "Open the selected Restock Plan editor",
            selected is not null,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentPlan = ResolveSelectedRestockPlan(state.Snapshot(), current.Owner);
                if (currentPlan is not null)
                    OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), current.Owner, currentPlan.Id));
            },
            selected?.Name ?? "No plan selected");
        ImGui.SameLine();
        if (ImGui.Button("Manage..."))
            ImGui.OpenPopup("RQRestockPlanManage");
        if (ImGui.BeginPopup("RQRestockPlanManage"))
        {
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("New plan"))
                OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();

            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Duplicate plan") && selected is not null)
                OpenRestockEditor(RestockPlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
            if (selected is null)
                ImGui.EndDisabled();

            var canCreateFromStowage = owner.HasStableIdentity &&
                                       StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Create from Stowage") && canCreateFromStowage)
                OpenRestockEditor(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
            if (!canCreateFromStowage)
                ImGui.EndDisabled();

            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Delete plan") && selected is not null)
                RequestDeleteRestockPlan(selected, owner);
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        selected = ResolveSelectedRestockPlan(state.Snapshot(), owner);
        if (selected is null)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("No Restock Plans yet.");
            ImGui.TextDisabled("Create one here, then add items from Stock or by name.");
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Button("New Restock Plan"))
                OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            reviewRegistry.RegisterLastButton(
                "quartermaster.restock.new",
                "Open a new Restock Plan draft",
                owner.HasStableIdentity,
                () => OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), runtimeSnapshots.Current.Owner)),
                owner.HasStableIdentity ? "Nothing is saved until Apply" : "Owner unavailable");
            ImGui.SameLine();
            var canCreateFromStowage = StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Button("Create from Stowage") && canCreateFromStowage)
                OpenRestockEditor(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
            if (!canCreateFromStowage)
                ImGui.EndDisabled();
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();
            return;
        }

        var evaluation = BuildRestockEvaluation(runtime, selected);
        ImGui.TextUnformatted(selected.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(selected.Enabled ? "Enabled" : "Disabled");
        ImGui.SameLine();
        ImGui.TextDisabled($"{selected.Items.Count:N0} items | need {evaluation.NeededQuantity:N0} | stored {evaluation.CoveredQuantity:N0}");
        var canExecute = selected.Enabled &&
                         evaluation.NeededQuantity > 0 &&
                         owner.HasStableIdentity &&
                         transfers.CanStart &&
                         !retainerRefresh.IsRefreshing &&
                         !retainerRefresh.IsQueued;
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Retrieve missing ({evaluation.NeededQuantity:N0})"))
        {
            var operation = journal.CreateRestock(owner, selected);
            StartTransfer(transfers.ExecuteRetrievalAsync(operation.OperationId));
        }
        if (!canExecute)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(transferStatus);

        var lines = evaluation.Lines.ToDictionary(line => line.PlanItemId);
        var rows = selected.Items
            .Select(item =>
            {
                lines.TryGetValue(item.Id, out var line);
                return new RestockPlanRow(item, line, selected.Id, owner);
            })
            .ToArray();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (restockPlanTable.Begin(
                "RQRestockPlanTable",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(220, ImGui.GetContentRegionAvail().Y)),
                    flags)))
        {
            foreach (var row in rows)
                restockPlanTable.DrawRow(row, id: $"restock-plan:{row.Item.Id}");
            restockPlanTable.End();
        }
    }

    private void OpenRestockEditor(RestockPlanDraft draft)
    {
        CloseStowageEditor();
        itemGroupWorkspace.CloseEditor();
        restockDraft = draft;
        selectedRestockItemIds.Clear();
        activeRestockItemId = draft.Items.FirstOrDefault()?.Id;
        selectedRestockItemGroupId = null;
        restockItemSearch = string.Empty;
        restockItemFilter = string.Empty;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
        bulkRestockQuality = -1;
        bulkRestockTarget = -1;
        bulkRestockNoteMode = -1;
        requestRestockEditorOpen = true;
        workbench.View = WorkbenchView.Restock;
        requestedView = WorkbenchView.Restock;
    }

    private void DrawRestockEditorModal(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Restock Plan##RQRestockEditor";
        restockEditorVisible = false;
        if (requestRestockEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestRestockEditorOpen = false;
        }
        if (restockDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1160, available.X - 48), Math.Min(700, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            CloseRestockEditor();
            return;
        }
        restockEditorVisible = true;
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Restock))
        {
            itemGroupWorkspace.DrawEditor(
                selectedRestockItemIds.Count,
                groupDraft => ItemGroupCatalog.AddMissing(
                    groupDraft,
                    restockDraft?.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)) ?? []));
            ImGui.EndPopup();
            return;
        }

        var draft = restockDraft;
        ImGui.TextUnformatted("Edit plan");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.55f, .8f, .95f, 1f), "<- Restock");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##restockdraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        var planEnabled = draft.Enabled;
        if (ImGui.Checkbox("Enabled##restockdraft", ref planEnabled))
            draft.Enabled = planEnabled;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedRestockItemIds.Count:N0} selected of {draft.Items.Count:N0} items");

        DrawRestockEditorToolbar(draft);
        DrawRestockEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(restockEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), restockEditorError);

        if (ImGui.BeginChild("RQRestockItemTable", new Vector2(0, -42), true))
            DrawRestockDraftItems(draft);
        ImGui.EndChild();

        var snapshot = state.Snapshot();
        var canApply = RestockPlanCatalog.CanApply(snapshot, runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Items.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Items.Count:N0} items | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##restockeditor"))
        {
            CloseRestockEditor();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.restock.editor.cancel",
            "Discard the open Restock Plan draft",
            true,
            CloseRestockEditor,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##restockeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    RestockPlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedRestockPlanId = appliedId;
                CloseRestockEditor();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                restockEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawRestockEditorToolbar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawRestockDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var groups = ItemGroupCatalog.All(state.Snapshot());
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedRestockItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##restockgroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##restockgroup{group.Id}", selectedGroup?.Id == group.Id))
                    selectedRestockItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group##restock") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeRestockItemId ??= draft.Items.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##restock"))
            itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Restock, selectedRestockItemGroupId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.restock",
            "Open Item Groups from the Restock Plan editor",
            true,
            () => itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Restock, selectedRestockItemGroupId),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##restockitemfilter", "Filter plan items", ref restockItemFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible##restock"))
        {
            foreach (var item in FilteredRestockDraftItems(draft))
                selectedRestockItemIds.SetSelected(item.Id, true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection##restock"))
            selectedRestockItemIds.Clear();
        ImGui.SameLine();
        if (selectedRestockItemIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group##restock"))
            itemGroupWorkspace.OpenNewEditor(
                ItemGroupEditorOrigin.Restock,
                draft.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)));
        if (selectedRestockItemIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftAdd(RestockPlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQRestockItemName", "Add item by name", ref restockItemSearch, 96))
            selectedRestockChoice = null;
        if (restockItemSearch.Trim().Length >= 2 && selectedRestockChoice is null)
        {
            foreach (var choice in SearchItems(restockItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##restockchoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedRestockChoice = choice;
                    restockItemSearch = choice.Name;
                }
        }
        if (selectedRestockChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##restockdraft"))
        {
            var choice = selectedRestockChoice;
            var existing = draft.Items.FirstOrDefault(item =>
                item.ItemId == choice.ItemId && item.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new RestockPlanItem
                {
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 1,
                };
                draft.Items.Add(existing);
            }
            activeRestockItemId = existing.Id;
            selectedRestockItemIds.Clear();
            selectedRestockItemIds.SetSelected(existing.Id, true);
            selectedRestockChoice = null;
            restockItemSearch = string.Empty;
        }
    }










    private void DrawRestockEditorBulkBar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedRestockItemIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##restockbulktarget", ref bulkRestockTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##restockbulkquality", ref bulkRestockQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Note");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(125);
        if (ImGui.BeginCombo("##restockbulknote", bulkRestockNoteMode < 0 ? "Leave unchanged" : "Clear notes"))
        {
            if (ImGui.Selectable("Leave unchanged", bulkRestockNoteMode < 0))
                bulkRestockNoteMode = -1;
            if (ImGui.Selectable("Clear notes", bulkRestockNoteMode == 0))
                bulkRestockNoteMode = 0;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var canApply = selectedRestockItemIds.Count > 0 &&
                       (bulkRestockQuality >= 0 || bulkRestockTarget >= 0 || bulkRestockNoteMode >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##restockbulk"))
        {
            foreach (var item in draft.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)))
            {
                if (bulkRestockQuality >= 0)
                    item.Quality = (ItemQualityPolicy)bulkRestockQuality;
                if (bulkRestockTarget >= 0)
                    item.TargetQuantity = bulkRestockTarget;
                if (bulkRestockNoteMode == 0)
                    item.Notes = string.Empty;
            }
            bulkRestockQuality = bulkRestockTarget = bulkRestockNoteMode = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedRestockItemIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##restockbulk"))
        {
            draft.Items.RemoveAll(item => selectedRestockItemIds.IsSelected(item.Id));
            selectedRestockItemIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftItems(RestockPlanDraft draft)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!restockDraftTable.Begin(
                "RQRestockDraftItems",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags)))
            return;
        var filtered = FilteredRestockDraftItems(draft);
        selectedRestockItemIds.Retain(draft.Items.Select(item => item.Id));
        if (filtered.Count == 0)
            restockDraftTable.DrawMessageRow(draft.Items.Count == 0
                ? "No items yet. Search by name above or add a selected Stock item."
                : "No items match this filter.");
        var rowKeys = filtered.Select(item => item.Id).ToArray();
        for (var index = 0; index < filtered.Count; index++)
        {
            var item = filtered[index];
            restockDraftTable.DrawSelectableRow(
                item,
                selectedRestockItemIds,
                rowKeys,
                index,
                $"##selectrestock:{item.Id}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedRestockItemIds);
        restockDraftTable.End();
    }

    private static string StockPlanState(StockWorkbenchRow row) =>
        row.Rule is null
            ? "Not in plan"
            : !row.Rule.Enabled
                ? "Off"
                : row.Line?.Action switch
                {
                    StowageAction.Retrieve => $"Retrieve {row.Line.RetrieveQuantity:N0}",
                    StowageAction.Deposit => $"Stow {row.Line.DepositQuantity:N0}",
                    _ => "On target",
                };

    private static string StockListingShortfall(IReadOnlyList<ListingPlanItemEvaluation> demand)
    {
        if (demand.Count == 0 || demand.All(item => !item.IsPlanned))
            return "—";
        return string.Join(" · ", demand.Where(item => item.IsPlanned).Select(item =>
        {
            var quality = QualityLabel(item.Quality);
            if (!item.NeedUnits.IsKnown)
                return $"{quality} unknown";
            return $"{quality} {item.NeedUnits.Value:N0}";
        }));
    }

    private void DrawRestockPlanItemLink(RestockPlanRow row)
    {
        if (ImGui.Selectable($"{row.Item.ItemName}##restockrow{row.Item.Id}"))
        {
            OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), row.Owner, row.PlanId));
            activeRestockItemId = row.Item.Id;
            selectedRestockItemIds.Clear();
            selectedRestockItemIds.SetSelected(row.Item.Id, true);
        }
        ImGui.TextDisabled(QualityLabel(row.Item.Quality));
    }


    private static void DrawRestockTarget(RestockPlanItem item)
    {
        var target = item.TargetQuantity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##draftrestocktarget{item.Id}", ref target))
            item.TargetQuantity = Math.Max(0, target);
    }

    private static void DrawRestockNote(RestockPlanItem item)
    {
        var note = item.Notes;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText($"##draftrestocknote{item.Id}", ref note, 160))
            item.Notes = note;
    }

    private string RestockItemGroups(RestockPlanItem item)
    {
        var matchingGroups = ItemGroupCatalog.All(state.Snapshot())
            .Where(group => group.Items.Any(member =>
                member.ItemId == item.ItemId && member.Quality == item.Quality))
            .Select(group => $"@{group.Name}")
            .Take(2)
            .ToArray();
        return matchingGroups.Length == 0 ? "Ungrouped" : string.Join(", ", matchingGroups);
    }

    private void DrawRestockDraftRemove(RestockPlanItem item)
    {
        if (!ImGui.SmallButton($"X##draftrestockremove{item.Id}"))
            return;
        restockDraft?.Items.Remove(item);
        selectedRestockItemIds.SetSelected(item.Id, false);
        if (activeRestockItemId == item.Id)
            activeRestockItemId = restockDraft?.Items.FirstOrDefault()?.Id;
    }

    private static string TransferTargetText(TransferWorkbenchRow row) =>
        TransferWorkbenchPresentation.Target(
            row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity,
            row.PlayerQuantity,
            row.ListingContribution.IsKnown);

    private void DrawTransferTarget(TransferWorkbenchRow row)
    {
        var listingShortfall = row.ListingContribution.IsKnown ? row.ListingContribution.Value : 0;
        var target = row.ListingContribution.IsKnown
            ? row.Rule.TargetQuantity + listingShortfall
            : row.Rule.TargetQuantity;
        ImGui.SetNextItemWidth(66);
        if (ImGui.InputInt($"##target:{row.Rule.Id}", ref target, 0))
            UpdateTransferRule(row.Owner, row.PlanId, row.Rule.Id, draftRule =>
                draftRule.TargetQuantity = Math.Max(0, target - listingShortfall));
        var targetHovered = ImGui.IsItemHovered();
        ImGui.SameLine();
        if (!row.ListingContribution.IsKnown)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "(+?)");
        else
            ImGui.TextColored(
                TransferPresentation.ActionColor(row.Line?.Action),
                $"({TransferPresentation.SignedQuantity(row.Difference)})");
        if (targetHovered || ImGui.IsItemHovered())
            ImGui.SetTooltip(row.ListingContribution.IsKnown
                ? $"Target {row.Rule.TargetQuantity + listingShortfall:N0}: independent {row.Rule.TargetQuantity:N0} + Listing Plan {listingShortfall:N0}; current player stock {row.PlayerQuantity:N0}."
                : $"Independent target {row.Rule.TargetQuantity:N0}; Listing Plan demand is not yet known.");
        if (row.ListingLink is not null)
            DrawTransferSource(row);
    }

    private void DrawTransferSource(TransferWorkbenchRow row)
    {
        if (row.ListingLink is null)
        {
            ImGui.TextDisabled("Independent");
            return;
        }
        var contribution = row.ListingContribution.IsKnown
            ? row.ListingContribution.Value.ToString("N0")
            : "?";
        ImGui.TextDisabled($"Plan +{contribution}");
        ImGui.TextDisabled($"Independent {row.Rule.TargetQuantity:N0}");
        ImGui.SameLine();
        if (!ImGui.SmallButton($"Unlink##transfer-listing:{row.ListingLink.Id}"))
            return;
        try
        {
            state.Mutate(document => ListingPlanCatalog.Unlink(
                document,
                row.Owner,
                row.PlanId,
                row.ListingLink.ListingPlanId,
                row.ListingLink.ItemId,
                row.ListingLink.Quality));
            inlineTransferError = string.Empty;
        }
        catch (Exception exception)
        {
            inlineTransferError = exception.Message;
        }
    }

    private static string TransferListingShortfall(TransferWorkbenchRow row) =>
        row.ListingLink is null
            ? "—"
            : row.ListingContribution.IsKnown
                ? row.ListingContribution.Value.ToString("N0")
                : "Unknown";

    private static TransferOutcomePresentation TransferOutcome(TransferWorkbenchRow row)
    {
        if (!row.Rule.Enabled)
            return new("Off");
        if (!row.ListingContribution.IsKnown)
            return new("Verify listing shortfall");
        var action = row.Line?.Action ?? StowageAction.None;
        return TransferWorkbenchPresentation.Outcome(
            action,
            Math.Abs(row.Difference),
            row.AccessibleStorageQuantity,
            row.RoutedDepositQuantity);
    }

    private static void DrawTransferOutcome(TransferWorkbenchRow row)
    {
        var outcome = TransferOutcome(row);
        var primaryColor = !row.Rule.Enabled
            ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
            : !row.ListingContribution.IsKnown
                ? new Vector4(1f, .7f, .3f, 1f)
                : TransferPresentation.ActionColor(row.Line?.Action);
        ImGui.TextColored(primaryColor, outcome.Primary);
        if (string.IsNullOrWhiteSpace(outcome.Constraint))
            return;
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"· {outcome.Constraint}");
    }

    private static string VendorProcurementText(TransferWorkbenchRow row)
    {
        var line = row.VendorLine;
        if (!row.Rule.AllowVendorPurchase)
            return "Off";
        if (line is null)
            return "Not needed";
        return line.IsReady
            ? $"Buy {line.ApprovedQuantity:N0} · {line.SelectedCandidate!.Offer.UnitPriceGil:N0} ea"
            : line.State switch
            {
                TransferVendorProcurementState.ExactQualityUnsupported => "Any quality required",
                TransferVendorProcurementState.OfferNotCataloged => "No gil vendor",
                _ => "Vendor unavailable",
            };
    }

    private static void DrawVendorProcurement(TransferWorkbenchRow row)
    {
        var text = VendorProcurementText(row);
        ImGui.TextColored(
            row.VendorLine?.IsReady == true
                ? new Vector4(.92f, .72f, .35f, 1f)
                : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            text);
        if (row.VendorLine is { } line && ImGui.IsItemHovered())
            ImGui.SetTooltip(line.Message);
    }

    private static void DrawStowageTarget(StowageDraftRow row)
    {
        var target = row.Rule.TargetQuantity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##drafttarget{row.Rule.Id}", ref target))
            row.Rule.TargetQuantity = Math.Max(0, target);
    }

    private static string StowageDraftOutcome(StowageDraftRow row)
    {
        var player = PlayerQuantity(row.Runtime.Browser, row.Rule);
        return !row.Rule.Enabled
            ? "Off"
            : player < row.Rule.TargetQuantity
                ? $"Retrieve {row.Rule.TargetQuantity - player:N0}"
                : player > row.Rule.TargetQuantity
                    ? $"Stow {player - row.Rule.TargetQuantity:N0}"
                    : "Balanced";
    }

    private void DrawStowageDraftRemove(StowageDraftRow row)
    {
        if (!ImGui.SmallButton($"X##draftremove{row.Rule.Id}"))
            return;
        stowageDraft?.Rules.Remove(row.Rule);
        selectedStowageRuleIds.SetSelected(row.Rule.Id, false);
        if (activeStowageRuleId == row.Rule.Id)
            activeStowageRuleId = stowageDraft?.Rules.FirstOrDefault()?.Id;
    }

    private static void DrawRestockDraftQuality(RestockPlanItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftrestockquality{item.Id}", QualityLabel(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private IReadOnlyList<RestockPlanItem> FilteredRestockDraftItems(RestockPlanDraft draft)
    {
        var filter = restockItemFilter.Trim();
        return draft.Items
            .Where(item => filter.Length == 0 || item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }

    private void RequestDeleteRestockPlan(RestockPlan plan, OwnerScope owner)
    {
        var planId = plan.Id;
        confirmationDialog.Request(
            $"restock-plan:{planId}",
            "Delete Restock Plan?",
            $"Delete \"{plan.Name}\"? This cannot be undone.",
            "Delete plan",
            () =>
            {
                workbench.SelectedRestockPlanId = state.Mutate(document =>
                {
                    document.RestockPlans.RemoveAll(candidate => candidate.Id == planId && candidate.Owner.Matches(owner));
                    return RestockPlanCatalog.OwnerPlans(document, owner).FirstOrDefault()?.Id;
                });
            });
    }

    private void RequestDeleteStowagePlan(StowagePlan plan, OwnerScope owner)
    {
        var planId = plan.Id;
        confirmationDialog.Request(
            $"transfer-plan:{planId}",
            "Delete Transfer Plan?",
            $"Delete \"{plan.Name}\" and all of its target rules? This cannot be undone.",
            "Delete plan",
            () =>
            {
                workbench.SelectedStowagePlanId = state.Mutate(document =>
                {
                    document.PlanItems.RemoveAll(rule => rule.StowagePlanId == planId);
                    document.TransferPlanListingLinks.RemoveAll(link => link.StowagePlanId == planId);
                    document.StowagePlans.RemoveAll(candidate => candidate.Id == planId && candidate.Owner.Matches(owner));
                    StowagePlanCatalog.ClearStaleRecovery(document);
                    return StowagePlanCatalog.OwnerPlans(document, owner).FirstOrDefault()?.Id;
                });
            });
    }

    private void AddStockSelectionToRestock(
        QuartermasterRuntimeSnapshot runtime,
        StockGroup stock,
        int target,
        RestockPlan? selectedPlan)
    {
        var snapshot = state.Snapshot();
        var draft = selectedPlan is null
            ? RestockPlanCatalog.NewDraft(snapshot, runtime.Owner)
            : RestockPlanCatalog.Draft(snapshot, runtime.Owner, selectedPlan.Id);
        var item = draft.Items.FirstOrDefault(candidate =>
            candidate.ItemId == stock.ItemId && candidate.Quality == ItemQualityPolicy.Any);
        if (item is null)
        {
            item = new RestockPlanItem
            {
                ItemId = stock.ItemId,
                ItemName = stock.ItemName,
                TargetQuantity = target,
            };
            draft.Items.Add(item);
        }
        else
        {
            item.ItemName = stock.ItemName;
            item.TargetQuantity = target;
            item.Enabled = true;
        }
        workbench.SelectedRestockPlanId = selectedPlan?.Id;
        OpenRestockEditor(draft);
        activeRestockItemId = item.Id;
        selectedRestockItemIds.SetSelected(item.Id, true);
        workbench.ClearSelection();
    }

    private void AddStockSelectionToStowage(
        QuartermasterRuntimeSnapshot runtime,
        StockGroup stock,
        int target,
        StowagePlan? selectedPlan)
    {
        var snapshot = state.Snapshot();
        var draft = selectedPlan is null
            ? StowagePlanCatalog.NewDraft(snapshot, runtime.Owner)
            : StowagePlanCatalog.Draft(snapshot, runtime.Owner, selectedPlan.Id);
        var rule = draft.Rules.FirstOrDefault(candidate =>
            candidate.ItemId == stock.ItemId && candidate.Quality == ItemQualityPolicy.Any);
        if (rule is null)
        {
            rule = new TargetPlanItem
            {
                StowagePlanId = draft.PlanId,
                ItemId = stock.ItemId,
                ItemName = stock.ItemName,
                TargetQuantity = target,
            };
            draft.Rules.Add(rule);
        }
        else
        {
            rule.ItemName = stock.ItemName;
            rule.TargetQuantity = target;
            rule.Enabled = true;
        }
        workbench.SelectedStowagePlanId = selectedPlan?.Id;
        OpenStowageEditor(draft);
        activeStowageRuleId = rule.Id;
        selectedStowageRuleIds.SetSelected(rule.Id, true);
        workbench.ClearSelection();
    }

    private void CloseRestockEditor()
    {
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Restock))
            itemGroupWorkspace.CloseEditor();
        restockDraft = null;
        requestRestockEditorOpen = false;
        restockEditorVisible = false;
        selectedRestockItemIds.Clear();
        activeRestockItemId = null;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
    }

    private void CloseStowageEditor()
    {
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage))
            itemGroupWorkspace.CloseEditor();
        stowageDraft = null;
        requestStowageEditorOpen = false;
        stowageEditorVisible = false;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = null;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
    }

    private void ClosePlanEditors()
    {
        CloseRestockEditor();
        CloseStowageEditor();
        CloseListingPlanEditor();
        transferReviewDialog.Clear();
        vendorReviewDialog.Clear();
    }

    private void DrawStowageWorkspace(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = StowagePlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null && plans.Count > 0)
        {
            selected = plans[0];
            workbench.SelectedStowagePlanId = selected.Id;
        }

        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 330));
        if (ImGui.BeginCombo("##RQTransferPlan", selected?.Name ?? "Choose a Transfer Plan"))
        {
            foreach (var plan in plans)
            {
                if (ImGui.Selectable($"{plan.Name}##transfer:{plan.Id}", selected?.Id == plan.Id))
                {
                    workbench.SelectedStowagePlanId = plan.Id;
                    selected = plan;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (!owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.Button("New"))
            OpenStowageEditor(StowagePlanCatalog.NewDraft(state.Snapshot(), owner));
        if (!owner.HasStableIdentity)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate") && selected is not null)
            OpenStowageEditor(StowagePlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
        if (selected is null)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("…##RQTransferPlanMenu"))
            ImGui.OpenPopup("RQTransferPlanMenu");
        if (ImGui.BeginPopup("RQTransferPlanMenu"))
        {
            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Rename or edit details") && selected is not null)
                OpenStowageEditor(selected.Id, owner);
            if (ImGui.Selectable("Delete plan") && selected is not null)
                RequestDeleteStowagePlan(selected, owner);
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null)
        {
            ClearInlineTransferErrorContext();
            ImGui.Spacing();
            ImGui.TextUnformatted("No Transfer Plans yet.");
            ImGui.TextDisabled("Create one, then select stock on the left or add items by name.");
            return;
        }
        EnsureInlineTransferErrorContext(owner, selected.Id);

        var projection = ResolveTransferWorkbenchProjection(runtime, selected);
        var ownerRules = projection.Rules;
        var retrieval = projection.Retrieval;
        var surplusBatch = projection.Deposit;
        var movements = projection.Movements;
        var hasMovement = projection.HasMovement;
        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement || projection.HasUnknownListingDemand,
            owner.HasStableIdentity,
            transfers.CanStart,
            retainerRefresh.IsRefreshing || retainerRefresh.IsQueued);
        var canExecute = availability.CanExecute;

        ImGui.SameLine();
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button("Execute plan"))
            transferReviewDialog.Request(selected.Id, selected.Name);
        if (!canExecute)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.execute",
            "Review and execute the selected Transfer Plan",
            canExecute,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentPlan = ResolveSelectedStowagePlan(current.State, current.Owner);
                if (currentPlan is not null)
                    transferReviewDialog.Request(currentPlan.Id, currentPlan.Name);
            },
            canExecute
                ? projection.HasUnknownListingDemand ? "Listing demand will be verified first" : $"{movements:N0} movements"
                : availability.BlockReason);

        var vendor = projection.Vendor;
        var recovery = runtime.State.TransferPlanRecovery;
        var hasCurrentRecovery = recovery is not null &&
            recovery.Owner.Matches(owner) &&
            recovery.PlanId == selected.Id &&
            recovery.PlanRevision == selected.Revision;

        ImGui.Separator();
        ImGui.TextUnformatted($"{ownerRules.Count:N0} items");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextUnformatted(projection.HasUnknownListingDemand ? "Movements pending verification" : $"{movements:N0} movements");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.52f, .79f, .94f, 1f), projection.HasUnknownListingDemand ? "Retrieve —" : $"Retrieve {retrieval.NeededQuantity:N0}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.53f, .83f, .64f, 1f), projection.HasUnknownListingDemand ? "Stow —" : $"Stow {surplusBatch.RequestedQuantity:N0}");
        if (!projection.HasUnknownListingDemand && vendor.ApprovedQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.92f, .72f, .35f, 1f), $"Vendor {vendor.ApprovedQuantity:N0} · max {vendor.MaximumGil:N0} gil");
        }
        var remainingShort = Math.Max(0, retrieval.MissingQuantity - vendor.ApprovedQuantity);
        if (!projection.HasUnknownListingDemand && remainingShort > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"{remainingShort:N0} short");
        }
        if (!projection.HasUnknownListingDemand && surplusBatch.RemainingQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"No room for {surplusBatch.RemainingQuantity:N0}");
        }
        var canReviewVendor = vendor.CanStart &&
                              !projection.HasUnknownListingDemand &&
                              !vendorProcurement.HasActiveRun &&
                              transfers.CanStart &&
                              !retainerRefresh.IsRefreshing &&
                              !retainerRefresh.IsQueued;
        if (vendor.Lines.Count > 0)
        {
            if (!canReviewVendor)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Review vendor buy ({vendor.ApprovedQuantity:N0})"))
                vendorReviewDialog.Request(vendor);
            if (!canReviewVendor)
                ImGui.EndDisabled();
            reviewRegistry.RegisterLastButton(
                "quartermaster.vendor.review",
                "Review vendor coverage for the selected Transfer Plan",
                canReviewVendor,
                () =>
                {
                    var current = runtimeSnapshots.Current;
                    var currentPlan = ResolveSelectedStowagePlan(current.State, current.Owner);
                    if (currentPlan is null)
                        return;
                    var currentProjection = ResolveTransferWorkbenchProjection(current, currentPlan);
                    vendorReviewDialog.Request(currentProjection.Vendor);
                },
                canReviewVendor
                    ? $"{vendor.ApprovedQuantity:N0} units · maximum {vendor.MaximumGil:N0} gil"
                    : vendorProcurement.HasActiveRun
                        ? "A vendor run is already active"
                        : projection.HasUnknownListingDemand
                            ? "Listing demand must be verified first"
                            : "No reviewed vendor-purchasable shortfall");
        }
        var planProgress = hasCurrentRecovery && retainerRefresh.IsRefreshing
            ? retainerRefresh.Status
            : string.Empty;
        var planNotice = !string.IsNullOrWhiteSpace(inlineTransferError)
            ? inlineTransferError
            : hasCurrentRecovery && !string.IsNullOrWhiteSpace(recovery!.FailureMessage)
                ? recovery.FailureMessage
                : hasCurrentRecovery && !retainerRefresh.IsRefreshing
                    ? "Retainer evidence refresh did not complete. Retry plan to continue."
                    : string.Empty;
        DrawTransferPlanNotice(selected, hasCurrentRecovery, planProgress, planNotice);
        DrawVendorRunStatus();
        DrawTableColumnsToolbar(transferWorkbenchTable, "RQTransferColumns", "Plan quantities use the latest accessible stock.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 4;
        IReadOnlyList<TransferWorkbenchRow> transferRows = projection.Rows;
        RenderedTransferRowCount = 0;
        if (transferWorkbenchTable.Begin(
                "RQTransferWorkbenchV2",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    flags)))
        {
            unsafe
            {
                transferRows = transferWorkbenchTable.Apply(transferRows, ImGui.TableGetSortSpecs());
            }
            RenderedTransferRowCount = transferWorkbenchTable.DrawClippedRows(
                transferRows,
                (row, _) =>
                {
                    transferWorkbenchTable.DrawRow(
                        row,
                        row.Rule.Enabled ? null : new Vector4(.38f, .12f, .14f, .42f),
                        id: $"transfer:{row.Rule.Id}");
                });
            transferWorkbenchTable.End();
        }

        ImGui.TextDisabled(
            availability.BlockReason ??
            "Balanced items stay visible and are skipped during execution.");
    }

    private void DrawVendorRunStatus()
    {
        var run = vendorProcurement.ActiveRun;
        if (run is null)
            return;

        ImGui.TextUnformatted($"Vendor buy · {run.Phase}");
        if (run.Receipts.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(.53f, .83f, .64f, 1f),
                $"{run.Receipts.Sum(receipt => receipt.Quantity):N0} bought · {run.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil)):N0} gil");
        }

        if (vendorProcurement.IsRunning)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Pause##RQVendorRun"))
                vendorProcurement.Pause();
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##RQVendorRun"))
                vendorProcurement.Stop();
        }
        else if (run.Phase == GilVendorBuyPhase.Paused)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Resume##RQVendorRun"))
            {
                if (!vendorProcurement.Resume(out var error))
                    vendorStatus = error;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##RQVendorRun"))
                vendorProcurement.Stop();
        }

        if (!string.IsNullOrWhiteSpace(run.Message))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(run.Message);
            ImGui.PopStyleColor();
        }
        if (!string.IsNullOrWhiteSpace(vendorStatus))
            DrawWrappedStatus(vendorStatus, new Vector4(1f, .4f, .4f, 1f));
        if (!string.IsNullOrWhiteSpace(vendorProcurement.CoordinationWarning))
            DrawWrappedStatus(vendorProcurement.CoordinationWarning, new Vector4(1f, .4f, .4f, 1f));
    }

    private static void DrawWrappedStatus(string message, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }

    private void DrawTransferPlanNotice(
        StowagePlan plan,
        bool hasCurrentRecovery,
        string progress,
        string notice)
    {
        var isProgress = !string.IsNullOrWhiteSpace(progress);
        if (!isProgress && string.IsNullOrWhiteSpace(notice))
            return;

        var title = isProgress
            ? "Refreshing retainer stock"
            : hasCurrentRecovery
                ? "Retainer refresh stopped"
                : "Plan couldn't continue";
        var body = isProgress
            ? progress
            : hasCurrentRecovery
                ? $"{notice} The plan is still intact; Retry recalculates remaining work from current evidence."
                : notice;
        var accent = isProgress
            ? new Vector4(.52f, .79f, .94f, 1f)
            : new Vector4(1f, .4f, .4f, 1f);
        var background = isProgress
            ? new Vector4(.06f, .13f, .17f, .92f)
            : new Vector4(.16f, .07f, .08f, .92f);

        var flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable("RQTransferPlanNotice", 2, flags))
            return;
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(background));
        ImGui.TableNextColumn();
        ImGui.TextColored(accent, title);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(220, ImGui.GetContentRegionAvail().X));
        ImGui.TextWrapped(body);
        ImGui.PopTextWrapPos();

        ImGui.TableNextColumn();
        if (isProgress)
        {
            if (retainerRefresh.CanCancel && ImGui.Button("Cancel##TransferPlanRecovery"))
                retainerRefresh.Cancel();
        }
        else if (hasCurrentRecovery)
        {
            if (ImGui.Button("Retry plan##TransferPlanRecovery"))
                RetryTransferPlanRecovery(plan);
            ImGui.SameLine();
            if (ImGui.Button("Dismiss##TransferPlanRecovery"))
                DismissTransferPlanRecovery();
        }
        else if (ImGui.Button("Dismiss##TransferPlanNotice"))
        {
            inlineTransferError = string.Empty;
        }
        ImGui.EndTable();
    }

    private TransferWorkbenchProjection ResolveTransferWorkbenchProjection(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan plan)
    {
        if (transferWorkbenchProjection is { } cached &&
            cached.RuntimeRevision == runtime.Revision &&
            cached.PlanId == plan.Id)
            return cached;

        var rules = runtime.State.PlanItems
            .Where(rule => rule.StowagePlanId == plan.Id)
            .OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectiveRules = ListingPlanEvaluator.ComposeRules(runtime.State, runtime.Browser, runtime.Owner, plan.Id);
        var listingEvaluation = ListingPlanEvaluator.Evaluate(ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner), runtime.Browser);
        var listingContributions = ListingPlanEvaluator.Contributions(runtime.State, plan.Id, listingEvaluation)
            .ToDictionary(contribution => (contribution.Link.ItemId, contribution.Link.Quality));
        var stowage = StowageEvaluator.BuildPlan(
            runtime.State,
            runtime.Browser,
            runtime.Owner,
            plan.Id);
        var retrieval = BuildTransferRetrievalEvaluation(runtime, effectiveRules);
        var vendor = vendorProcurement.BuildReview(runtime, plan, effectiveRules, retrieval);
        var deposit = BuildSurplusBatch(runtime, stowage);
        var evaluated = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var retrievalLines = retrieval.Lines.ToDictionary(line => line.PlanItemId);
        var vendorLines = vendor.Lines.ToDictionary(line => line.RuleId);
        var movements = evaluated.Values.Count(line =>
            line.Action is StowageAction.Retrieve or StowageAction.Deposit);
        var rows = rules
            .Select(rule =>
            {
                evaluated.TryGetValue(rule.Id, out var line);
                retrievalLines.TryGetValue(rule.Id, out var retrievalLine);
                vendorLines.TryGetValue(rule.Id, out var vendorLine);
                var routedDepositQuantity = deposit.Routes
                    .Where(route => route.Request.SourceRuleId == rule.Id)
                    .Sum(route => route.RoutedQuantity);
                var playerQuantity = StowageEvaluator.PlayerQuantity(
                    rule,
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId));
                var accessibleStorageQuantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId),
                    rule.Quality,
                    runtime.Retainers,
                    runtime.Owner);
                listingContributions.TryGetValue((rule.ItemId, rule.Quality), out var listingContribution);
                return new TransferWorkbenchRow(
                    rule,
                    line,
                    retrievalLine,
                    vendorLine,
                    routedDepositQuantity,
                    playerQuantity,
                    accessibleStorageQuantity,
                    (line?.DesiredPlayerQuantity ?? rule.TargetQuantity) - playerQuantity,
                    listingContribution?.Quantity ?? Evidence.Known(0),
                    listingContribution?.Link,
                    runtime.Owner,
                    plan.Id,
                    runtime);
            })
            .ToArray();
        transferWorkbenchProjection = new(
            runtime.Revision,
            plan.Id,
            rules,
            stowage,
            retrieval,
            vendor,
            deposit,
            movements,
            TransferExecutionPolicy.HasMovement(retrieval.NeededQuantity, deposit),
            ListingPlanEvaluator.HasUnknownLinkedDemand(runtime.State, runtime.Browser, runtime.Owner, plan.Id),
            rows);
        transferProjectionBuildCount++;
        return transferWorkbenchProjection;
    }

    private void RequestSelectedTransferReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        transferReviewDialog.Request(plan.Id, plan.Name);
    }

    private void RequestSelectedVendorReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        vendorReviewDialog.Request(ResolveTransferWorkbenchProjection(current, plan).Vendor);
    }

    private void DrawInlineTransferRoute(
        OwnerScope owner,
        Guid planId,
        TargetPlanItem rule,
        QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##inline-route:{rule.Id}",
                TransferPresentation.RouteSummary(rule.Routing, runtime.Retainers, owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
        {
            if (ImGui.Selectable(TransferPresentation.RoutingModeLabel(mode), rule.Routing.Mode == mode))
                UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    draftRule.Routing.Mode = mode);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Fallback");
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
        {
            if (ImGui.Selectable(TransferPresentation.OverflowLabel(overflow), rule.Routing.Overflow == overflow))
                UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    draftRule.Routing.Overflow = overflow);
        }

        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        if (ownerRetainers.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Preferred first");
            foreach (var retainer in ownerRetainers)
            {
                var preferred = rule.Routing.PreferredRetainerIds.Contains(retainer.RetainerId);
                if (ImGui.Selectable(
                        $"{retainer.RetainerName}##inline-preferred:{rule.Id}:{retainer.RetainerId}",
                        preferred))
                {
                    UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    {
                        if (!draftRule.Routing.PreferredRetainerIds.Remove(retainer.RetainerId))
                            draftRule.Routing.PreferredRetainerIds.Add(retainer.RetainerId);
                        draftRule.Routing.Mode = StowageRoutingMode.HomeFirst;
                    });
                }
            }
        }
        ImGui.EndCombo();
    }

    private void UpdateTransferRule(
        OwnerScope owner,
        Guid planId,
        Guid ruleId,
        Action<TargetPlanItem> update)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                var draftRule = draft.Rules.Single(rule => rule.Id == ruleId);
                update(draftRule);
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            inlineTransferError = string.Empty;
        }
        catch (Exception exception)
        {
            inlineTransferError = exception.Message;
        }
    }

    private void RemoveTransferRule(OwnerScope owner, Guid planId, Guid ruleId)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                draft.Rules.RemoveAll(rule => rule.Id == ruleId);
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            inlineTransferError = string.Empty;
        }
        catch (Exception exception)
        {
            inlineTransferError = exception.Message;
        }
    }

    private void OpenStowageEditor(Guid planId, OwnerScope owner) =>
        OpenStowageEditor(StowagePlanCatalog.Draft(state.Snapshot(), owner, planId));

    private void OpenStowageEditor(StowagePlanDraft draft)
    {
        CloseRestockEditor();
        itemGroupWorkspace.CloseEditor();
        stowageDraft = draft;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = stowageDraft.Rules.FirstOrDefault()?.Id;
        selectedStowageItemGroupId = null;
        stowageItemSearch = string.Empty;
        stowageRuleFilter = string.Empty;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
        bulkQuality = -1;
        bulkRoutingMode = -1;
        bulkOverflow = -1;
        bulkStowageTarget = -1;
        requestStowageEditorOpen = true;
        workbench.View = WorkbenchView.Stowage;
        requestedView = WorkbenchView.Stowage;
    }

    private void DrawStowageEditorModal(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Transfer Plan##RQStowageEditor";
        stowageEditorVisible = false;
        if (requestStowageEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestStowageEditorOpen = false;
        }
        if (stowageDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1220, available.X - 48), Math.Min(720, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            CloseStowageEditor();
            return;
        }
        stowageEditorVisible = true;
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage))
        {
            itemGroupWorkspace.DrawEditor(
                selectedStowageRuleIds.Count,
                groupDraft => ItemGroupCatalog.AddMissing(
                    groupDraft,
                    stowageDraft?.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)) ?? []));
            ImGui.EndPopup();
            return;
        }

        var draft = stowageDraft;
        ImGui.TextUnformatted("Edit Transfer Plan");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##stowagedraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedStowageRuleIds.Count:N0} selected of {draft.Rules.Count:N0} rules");

        DrawStowageEditorToolbar(draft);
        DrawStowageEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(stowageEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), stowageEditorError);

        if (ImGui.BeginChild("RQStowageRuleTable", new Vector2(0, -42), true))
            DrawStowageDraftRules(draft, runtime);
        ImGui.EndChild();

        var canApply = StowagePlanCatalog.CanApply(state.Snapshot(), runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Rules.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Rules.Count:N0} rules | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##stowageeditor"))
        {
            CloseStowageEditor();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.editor.cancel",
            "Discard the open Transfer Plan draft",
            true,
            CloseStowageEditor,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##stowageeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    StowagePlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedStowagePlanId = appliedId;
                CloseStowageEditor();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                stowageEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawStowageEditorToolbar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawStowageDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var snapshot = state.Snapshot();
        var groups = ItemGroupCatalog.All(snapshot);
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedStowageItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##stowagegroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##group{group.Id}", selectedGroup?.Id == group.Id))
                    selectedStowageItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeStowageRuleId ??= draft.Rules.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##stowage"))
            itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Stowage, selectedStowageItemGroupId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.transfer",
            "Open Item Groups from the Transfer Plan editor",
            true,
            () => itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Stowage, selectedStowageItemGroupId),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##stowagerulefilter", "Filter plan items", ref stowageRuleFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible"))
        {
            foreach (var rule in FilteredDraftRules(draft))
                selectedStowageRuleIds.SetSelected(rule.Id, true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection"))
            selectedStowageRuleIds.Clear();
        ImGui.SameLine();
        if (selectedStowageRuleIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group"))
            itemGroupWorkspace.OpenNewEditor(
                ItemGroupEditorOrigin.Stowage,
                draft.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)));
        if (selectedStowageRuleIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawStowageDraftAdd(StowagePlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQStowageItemName", "Add item by name", ref stowageItemSearch, 96))
            selectedStowageChoice = null;
        if (stowageItemSearch.Trim().Length >= 2 && selectedStowageChoice is null)
        {
            foreach (var choice in SearchItems(stowageItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##stowagechoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedStowageChoice = choice;
                    stowageItemSearch = choice.Name;
                }
        }
        if (selectedStowageChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##stowagedraft"))
        {
            var choice = selectedStowageChoice;
            var existing = draft.Rules.FirstOrDefault(rule =>
                rule.ItemId == choice.ItemId && rule.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new TargetPlanItem
                {
                    StowagePlanId = draft.PlanId,
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 0,
                };
                draft.Rules.Add(existing);
            }
            activeStowageRuleId = existing.Id;
            selectedStowageRuleIds.Clear();
            selectedStowageRuleIds.SetSelected(existing.Id, true);
            selectedStowageChoice = null;
            stowageItemSearch = string.Empty;
        }
    }

    private void DrawStowageEditorBulkBar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedStowageRuleIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Player target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##stowagebulktarget", ref bulkStowageTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkquality", ref bulkQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Destination");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkdestination", ref bulkRoutingMode, Enum.GetValues<StowageRoutingMode>(), TransferPresentation.RoutingModeLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Overflow");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkoverflow", ref bulkOverflow, Enum.GetValues<StowageOverflowPolicy>(), TransferPresentation.OverflowLabel);
        ImGui.SameLine();
        var canApply = selectedStowageRuleIds.Count > 0 &&
                       (bulkStowageTarget >= 0 || bulkQuality >= 0 || bulkRoutingMode >= 0 || bulkOverflow >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##stowagebulk"))
        {
            foreach (var rule in draft.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)))
            {
                if (bulkStowageTarget >= 0)
                    rule.TargetQuantity = bulkStowageTarget;
                if (bulkQuality >= 0)
                    rule.Quality = (ItemQualityPolicy)bulkQuality;
                if (bulkRoutingMode >= 0)
                    rule.Routing.Mode = (StowageRoutingMode)bulkRoutingMode;
                if (bulkOverflow >= 0)
                    rule.Routing.Overflow = (StowageOverflowPolicy)bulkOverflow;
            }
            bulkStowageTarget = bulkQuality = bulkRoutingMode = bulkOverflow = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedStowageRuleIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##stowagebulk"))
        {
            draft.Rules.RemoveAll(rule => selectedStowageRuleIds.IsSelected(rule.Id));
            selectedStowageRuleIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private static void DrawOptionalEnumCombo<T>(
        string label,
        ref int selected,
        IReadOnlyList<T> values,
        Func<T, string> display)
        where T : struct, Enum
    {
        ImGui.SetNextItemWidth(155);
        var preview = selected < 0 ? "No change" : display((T)Enum.ToObject(typeof(T), selected));
        if (!ImGui.BeginCombo(label, preview))
            return;
        if (ImGui.Selectable("No change", selected < 0))
            selected = -1;
        foreach (var value in values)
        {
            var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (ImGui.Selectable(display(value), selected == numeric))
                selected = numeric;
        }
        ImGui.EndCombo();
    }

    private void DrawStowageDraftRules(StowagePlanDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!stowageDraftTable.Begin(
                "RQStowageDraftRules",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags)))
            return;
        var filtered = FilteredDraftRules(draft);
        selectedStowageRuleIds.Retain(draft.Rules.Select(rule => rule.Id));
        if (filtered.Count == 0)
            stowageDraftTable.DrawMessageRow(draft.Rules.Count == 0
                ? "No rules yet. Search by name above or add a selected Stock item."
                : "No rules match this filter.");
        var rows = filtered.Select(rule => new StowageDraftRow(rule, runtime)).ToArray();
        var rowKeys = rows.Select(row => row.Rule.Id).ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            stowageDraftTable.DrawSelectableRow(
                row,
                selectedStowageRuleIds,
                rowKeys,
                index,
                $"##selectstowage:{row.Rule.Id}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedStowageRuleIds);
        stowageDraftTable.End();
    }

    private void DrawDraftQuality(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftquality{rule.Id}", QualityLabel(rule.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), rule.Quality == quality))
                rule.Quality = quality;
        ImGui.EndCombo();
    }

    private static void DrawVendorPurchaseToggle(TargetPlanItem rule)
    {
        var supported = rule.Quality == ItemQualityPolicy.Any;
        if (!supported)
            ImGui.BeginDisabled();
        var allowed = rule.AllowVendorPurchase;
        if (ImGui.Checkbox($"##vendor-purchase:{rule.Id}", ref allowed))
            rule.AllowVendorPurchase = allowed;
        if (!supported)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(supported
                ? "Allow an ordinary-gil vendor to cover the shortage left after accessible retainer stock. Every run is reviewed first."
                : "Vendor purchasing currently requires Any quality so live reconciliation cannot count the wrong quality.");
    }

    private static bool DrawRuleToggle(string id, bool enabled)
    {
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            enabled ? new Vector4(.16f, .34f, .24f, 1f) : new Vector4(.13f, .15f, .17f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            enabled ? new Vector4(.2f, .43f, .3f, 1f) : new Vector4(.22f, .25f, .28f, 1f));
        if (ImGui.SmallButton($"{(enabled ? "On" : "Off")}##rule{id}"))
            enabled = !enabled;
        ImGui.PopStyleColor(2);
        return enabled;
    }

    private void DrawStowageRouteCombo(TargetPlanItem rule, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##draftdestination{rule.Id}",
                TransferPresentation.RouteSummary(rule.Routing, runtime.Retainers, runtime.Owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
            if (ImGui.Selectable(TransferPresentation.RoutingModeLabel(mode), rule.Routing.Mode == mode))
                rule.Routing.Mode = mode;

        ImGui.Separator();
        ImGui.TextDisabled("Preferred retainers - in order");
        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        for (var index = 0; index < rule.Routing.PreferredRetainerIds.Count; index++)
        {
            var retainerId = rule.Routing.PreferredRetainerIds[index];
            var retainer = ownerRetainers.FirstOrDefault(candidate => candidate.RetainerId == retainerId);
            ImGui.PushID($"inline-route{rule.Id}:{retainerId}:{index}");
            ImGui.TextUnformatted(retainer?.RetainerName ?? $"Retainer {retainerId}");
            ImGui.SameLine();
            if (index > 0 && ImGui.SmallButton("Up"))
                (rule.Routing.PreferredRetainerIds[index - 1], rule.Routing.PreferredRetainerIds[index]) =
                    (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index - 1]);
            ImGui.SameLine();
            if (index + 1 < rule.Routing.PreferredRetainerIds.Count && ImGui.SmallButton("Down"))
                (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index + 1]) =
                    (rule.Routing.PreferredRetainerIds[index + 1], rule.Routing.PreferredRetainerIds[index]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                rule.Routing.PreferredRetainerIds.RemoveAt(index);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        var available = ownerRetainers
            .Where(retainer => !rule.Routing.PreferredRetainerIds.Contains(retainer.RetainerId))
            .ToArray();
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo($"##addinlinepreferred{rule.Id}", "Add preferred retainer"))
        {
            foreach (var retainer in available)
                if (ImGui.Selectable($"{retainer.RetainerName}##addinline{retainer.RetainerId}"))
                    rule.Routing.PreferredRetainerIds.Add(retainer.RetainerId);
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Note");
        var notes = rule.Notes;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText($"##draftstowagenote{rule.Id}", ref notes, 160))
            rule.Notes = notes;
        ImGui.EndCombo();
    }

    private static void DrawStowageOverflowCombo(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftoverflow{rule.Id}", TransferPresentation.OverflowLabel(rule.Routing.Overflow)))
            return;
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
            if (ImGui.Selectable(TransferPresentation.OverflowLabel(overflow), rule.Routing.Overflow == overflow))
                rule.Routing.Overflow = overflow;
        ImGui.EndCombo();
    }

    private IReadOnlyList<TargetPlanItem> FilteredDraftRules(StowagePlanDraft draft)
    {
        var filter = stowageRuleFilter.Trim();
        return draft.Rules
            .Where(rule => filter.Length == 0 || rule.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.ItemId)
            .ToArray();
    }

    private void FocusStockFromListings(QuartermasterRuntimeSnapshot runtime, ListingGroupView group)
    {
        stockSelection.Clear();
        stockSelection.SetSelected(group.ItemId, true);
        workbench.SelectedStockListingQuality = group.Quality;
        workbench.ScopeKey = BrowserScope.AllKey;
        workbench.ItemFilterState.SetExpression(string.Empty);
        workbench.FocusedStockItemId = group.ItemId;
        requestedView = WorkbenchView.Stock;
        workbench.View = WorkbenchView.Stowage;
    }

    private void OpenListingPlanEditor(QuartermasterRuntimeSnapshot runtime, ListingItemKey? focus)
    {
        listingPlanDraft = ListingPlanCatalog.Draft(state.Snapshot(), runtime.Owner, runtime.Browser);
        listingPlanEditorFocus = focus;
        listingPlanEditorRetainerFilter = null;
        listingPlanEditorAssignmentFilter = null;
        listingPlanEditorFilter = string.Empty;
        ResetListingPlanPriceText(listingPlanDraft.Assignments);
        listingPlanItemSearch = string.Empty;
        selectedListingPlanChoice = null;
        listingPlanEditorError = string.Empty;
        listingPlanEditorConflicts = [];
        requestListingPlanEditorOpen = true;
    }

    private void CloseListingPlanEditor()
    {
        listingPlanDraft = null;
        requestListingPlanEditorOpen = false;
        listingPlanEditorVisible = false;
        listingPlanEditorFocus = null;
        listingPlanEditorRetainerFilter = null;
        listingPlanEditorAssignmentFilter = null;
        listingPlanPriceText.Clear();
        listingPlanEditorError = string.Empty;
        listingPlanEditorConflicts = [];
        selectedListingPlanChoice = null;
    }

    private void DrawListingPlanEditorModal(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Listing Plan##RQListingPlanEditor";
        listingPlanEditorVisible = false;
        if (requestListingPlanEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestListingPlanEditorOpen = false;
        }
        if (listingPlanDraft is null)
            return;
        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(new(Math.Min(1320, available.X - 48), Math.Min(720, available.Y - 48)), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            if (listingPlanEditorVisible)
                CloseListingPlanEditor();
            return;
        }
        listingPlanEditorVisible = true;
        var draft = listingPlanDraft;
        ImGui.TextUnformatted("Edit Listing Plan");
        ImGui.SameLine();
        ImGui.TextDisabled($"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName} · one active plan · {draft.Assignments.Count:N0} assignments");
        if (draft.IncompleteListingRetainerIds.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), $"· {draft.IncompleteListingRetainerIds.Count:N0} retainers not seeded");
        }
        DrawListingPlanEditorToolbar(draft, runtime);

        var validation = ListingPlanCatalog.Validate(
            draft.Assignments,
            itemId => ResolveListingMaxStack(runtime.Browser, itemId),
            runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner)).Select(retainer => retainer.RetainerId).ToHashSet());
        var overfullRetainers = draft.Assignments.Where(assignment => assignment.Enabled)
            .GroupBy(assignment => assignment.RetainerId)
            .Select(group => new
            {
                RetainerId = group.Key,
                RetainerName = runtime.Retainers.Values.FirstOrDefault(retainer => retainer.RetainerId == group.Key)?.RetainerName ??
                               group.Select(assignment => assignment.RetainerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                               $"Retainer {group.Key}",
                Slots = group.Sum(assignment => assignment.ListingCount),
            })
            .Where(row => row.Slots > 20)
            .OrderBy(row => row.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (overfullRetainers.Length > 0)
        {
            ImGui.TextColored(new Vector4(1f, .45f, .35f, 1f), "Planned capacity needs attention:");
            foreach (var retainer in overfullRetainers)
            {
                if (ImGui.SmallButton($"Fix {retainer.RetainerName} {retainer.Slots:N0} / 20##capacity:{retainer.RetainerId}"))
                {
                    listingPlanEditorFocus = null;
                    listingPlanEditorRetainerFilter = retainer.RetainerId;
                    listingPlanEditorAssignmentFilter = null;
                    listingPlanEditorFilter = retainer.RetainerName;
                }
            }
        }
        else
        {
            var fullest = draft.Assignments.Where(assignment => assignment.Enabled)
                .GroupBy(assignment => assignment.RetainerId)
                .Select(group => new
                {
                    RetainerName = runtime.Retainers.Values.FirstOrDefault(retainer => retainer.RetainerId == group.Key)?.RetainerName ??
                                   group.Select(assignment => assignment.RetainerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                                   $"Retainer {group.Key}",
                    Slots = group.Sum(assignment => assignment.ListingCount),
                })
                .OrderByDescending(row => row.Slots)
                .FirstOrDefault();
            ImGui.TextDisabled(fullest is null ? "No planned slots yet." : $"Highest planned capacity: {fullest.RetainerName} {fullest.Slots:N0} / 20");
        }
        var repairIssues = validation.Concat(listingPlanEditorConflicts)
            .Where(issue => issue.AssignmentId is not null && issue.Field != "RetainerCapacity")
            .GroupBy(issue => new { issue.AssignmentId, issue.Field })
            .Select(group => group.First())
            .ToArray();
        if (repairIssues.Length > 0 && ImGui.BeginTable(
                "RQListingPlanRepairs",
                2,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Issue", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 58);
            foreach (var issue in repairIssues)
            {
                var assignment = draft.Assignments.FirstOrDefault(candidate => candidate.Id == issue.AssignmentId);
                if (assignment is null)
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(
                    new Vector4(1f, .45f, .35f, 1f),
                    $"{assignment.ItemName} · {ListingIssueFieldLabel(issue.Field)}: {issue.Message}");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Fix##listing-issue:{assignment.Id}:{issue.Field}"))
                {
                    listingPlanEditorFocus = null;
                    listingPlanEditorRetainerFilter = null;
                    listingPlanEditorAssignmentFilter = assignment.Id;
                    listingPlanEditorFilter = assignment.ItemName;
                }
            }
            ImGui.EndTable();
        }
        var transitionConflict = ListingPlanPresentation.CapacityTransitionConflict(draft, runtime.Browser);
        if (transitionConflict is not null)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), transitionConflict);
        if (!string.IsNullOrWhiteSpace(listingPlanEditorError))
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), listingPlanEditorError);
        if (listingPlanEditorConflicts.Count > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, .7f, .3f, 1f),
                $"{listingPlanEditorConflicts.Count:N0} concurrent field changes were rebased. Your values remain highlighted; edit them or Save again to keep them.");
        }

        if (ImGui.BeginChild("RQListingPlanRows", new Vector2(0, Math.Max(230, ImGui.GetContentRegionAvail().Y - 60)), false))
            DrawListingPlanRows(draft, runtime, validation.Concat(listingPlanEditorConflicts).ToArray());
        ImGui.EndChild();
        ImGui.Separator();
        if (validation.Count == 0)
            ImGui.TextDisabled("Changes apply together; current listings never rewrite this plan automatically.");
        else
            ImGui.TextColored(
                new Vector4(1f, .4f, .4f, 1f),
                $"{validation[0].Message} · {validation.Count:N0} field{(validation.Count == 1 ? string.Empty : "s")} need attention.");
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - 176));
        if (ImGui.Button("Cancel##listingplan"))
        {
            CloseListingPlanEditor();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (validation.Count != 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save Listing Plan"))
        {
            try
            {
                state.Mutate(document => ListingPlanCatalog.Apply(
                    document,
                    runtime.Owner,
                    draft,
                    itemId => ResolveListingMaxStack(runtime.Browser, itemId),
                    runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner)).Select(retainer => retainer.RetainerId).ToHashSet(),
                    DateTime.UtcNow));
                CloseListingPlanEditor();
                ImGui.CloseCurrentPopup();
            }
            catch (ListingPlanConflictException exception)
            {
                var canonical = state.Snapshot().ListingPlans.Single(plan =>
                    plan.Id == draft.PlanId && plan.Owner.Matches(runtime.Owner));
                draft.Assignments = exception.RebasedAssignments.Select(ListingPlanCatalog.Copy).ToList();
                draft.BaselineAssignments = canonical.Assignments.Select(ListingPlanCatalog.Copy).ToList();
                draft.SourceRevision = canonical.Revision;
                ResetListingPlanPriceText(draft.Assignments);
                listingPlanEditorConflicts = exception.Conflicts;
                listingPlanEditorError = string.Empty;
            }
            catch (InvalidOperationException exception)
            {
                listingPlanEditorError = exception.Message;
            }
        }
        if (validation.Count != 0)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawListingPlanEditorToolbar(ListingPlanDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.Separator();
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputTextWithHint("##listingplanitem", "Add an item by name", ref listingPlanItemSearch, 96))
            selectedListingPlanChoice = null;
        if (listingPlanItemSearch.Trim().Length >= 2 && selectedListingPlanChoice is null)
        {
            foreach (var choice in SearchItems(listingPlanItemSearch, 5))
                if (ImGui.Selectable($"{choice.Label}##listingplanchoice{choice.ItemId}", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedListingPlanChoice = choice;
                    listingPlanItemSearch = choice.Name;
                }
        }
        ImGui.SameLine();
        if (selectedListingPlanChoice is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add item") && selectedListingPlanChoice is { } selectedChoice)
        {
            var retainer = runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner))
                .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var observedPrice = runtime.Browser.Listings.FirstOrDefault(listing => listing.ItemId == selectedChoice.ItemId && listing.UnitPrice.IsKnown)?.UnitPrice.Value;
            var assignment = new ListingPlanAssignment
            {
                ItemId = selectedChoice.ItemId,
                ItemName = selectedChoice.Name,
                RetainerId = retainer?.RetainerId ?? 0,
                RetainerName = retainer?.RetainerName ?? "Missing retainer",
                UnitPrice = observedPrice is null ? 0 : checked((int)observedPrice.Value),
            };
            draft.Assignments.Add(assignment);
            listingPlanPriceText[assignment.Id] = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
            listingPlanEditorFocus = new(selectedChoice.ItemId, ItemQualityPolicy.NqOnly);
            selectedListingPlanChoice = null;
            listingPlanItemSearch = string.Empty;
        }
        if (selectedListingPlanChoice is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Add current unplanned listings"))
        {
            var observed = ListingPlanCatalog.Draft(new QuartermasterState(), runtime.Owner, runtime.Browser).Assignments;
            foreach (var assignment in observed)
            {
                var exists = draft.Assignments.Any(current => current.ItemId == assignment.ItemId && current.Quality == assignment.Quality &&
                    current.RetainerId == assignment.RetainerId && current.QuantityPerListing == assignment.QuantityPerListing && current.UnitPrice == assignment.UnitPrice);
                if (!exists)
                {
                    var imported = ListingPlanCatalog.Copy(assignment);
                    draft.Assignments.Add(imported);
                    listingPlanPriceText[imported.Id] = imported.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
                }
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190);
        if (ImGui.InputTextWithHint("##listingplanfilter", "Filter assignments", ref listingPlanEditorFilter, 80))
        {
            listingPlanEditorFocus = null;
            listingPlanEditorRetainerFilter = null;
            listingPlanEditorAssignmentFilter = null;
        }
        ImGui.SameLine();
        if ((listingPlanEditorFocus is not null || listingPlanEditorRetainerFilter is not null || listingPlanEditorAssignmentFilter is not null) &&
            ImGui.SmallButton("All assignments"))
        {
            listingPlanEditorFocus = null;
            listingPlanEditorRetainerFilter = null;
            listingPlanEditorAssignmentFilter = null;
            listingPlanEditorFilter = string.Empty;
        }
    }

    private void DrawListingPlanRows(
        ListingPlanDraft draft,
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<ListingPlanValidationIssue> issues)
    {
        var rows = draft.Assignments
            .Where(assignment => listingPlanEditorAssignmentFilter is { } assignmentId
                ? assignment.Id == assignmentId
                : listingPlanEditorFocus is { } focus
                    ? assignment.ItemId == focus.ItemId && assignment.Quality == focus.Quality
                    : listingPlanEditorRetainerFilter is { } retainerId
                        ? assignment.RetainerId == retainerId
                        : listingPlanEditorFilter.Length == 0 ||
                          assignment.ItemName.Contains(listingPlanEditorFilter, StringComparison.OrdinalIgnoreCase) ||
                          assignment.RetainerName.Contains(listingPlanEditorFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(assignment => assignment.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(assignment => assignment.Quality)
            .ThenBy(assignment => assignment.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retainers = runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!ImGui.BeginTable("RQListingPlanAssignments", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Qty each", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableHeadersRow();
        foreach (var assignment in rows)
        {
            ImGui.PushID(assignment.Id.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); var enabled = assignment.Enabled; if (ImGui.Checkbox("##on", ref enabled)) assignment.Enabled = enabled;
            ImGui.TableNextColumn();
            var itemIssue = FindListingIssue(issues, assignment.Id, nameof(assignment.ItemId)) ??
                            FindListingIssue(issues, assignment.Id, "Assignment");
            if (itemIssue is null) ImGui.TextUnformatted(assignment.ItemName); else ImGui.TextColored(new Vector4(1f, .5f, .4f, 1f), assignment.ItemName);
            if (itemIssue is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(itemIssue.Message);
            ImGui.TableNextColumn();
            var qualityIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.Quality));
            DrawListingQuality(assignment);
            PopListingIssue(qualityIssue);
            ImGui.TableNextColumn();
            var retainerIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.RetainerId));
            DrawListingRetainer(assignment, retainers);
            PopListingIssue(retainerIssue);
            ImGui.TableNextColumn();
            var countIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.ListingCount));
            var count = assignment.ListingCount; ImGui.SetNextItemWidth(-1); if (ImGui.InputInt("##count", ref count, 0)) assignment.ListingCount = count;
            PopListingIssue(countIssue);
            ImGui.TableNextColumn();
            var quantityIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.QuantityPerListing));
            var quantity = assignment.QuantityPerListing; ImGui.SetNextItemWidth(-1); if (ImGui.InputInt("##quantity", ref quantity, 0)) assignment.QuantityPerListing = quantity;
            PopListingIssue(quantityIssue);
            ImGui.TableNextColumn();
            var priceIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.UnitPrice));
            DrawListingUnitPrice(assignment);
            PopListingIssue(priceIssue);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(assignment.DesiredUnits.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Remove"))
            {
                draft.Assignments.Remove(assignment);
                listingPlanPriceText.Remove(assignment.Id);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawListingUnitPrice(ListingPlanAssignment assignment)
    {
        if (!listingPlanPriceText.TryGetValue(assignment.Id, out var text))
            text = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##price", ref text, 13, ImGuiInputTextFlags.CharsDecimal))
        {
            listingPlanPriceText[assignment.Id] = text;
            assignment.UnitPrice = TryParseListingUnitPrice(text, out var parsed) ? parsed : 0;
        }
        if (ImGui.IsItemActivated() && TryParseListingUnitPrice(text, out var activated))
            listingPlanPriceText[assignment.Id] = activated.ToString(CultureInfo.InvariantCulture);
        if (ImGui.IsItemDeactivatedAfterEdit() && TryParseListingUnitPrice(text, out var committed))
            listingPlanPriceText[assignment.Id] = committed.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static bool TryParseListingUnitPrice(string text, out int value) =>
        int.TryParse(
            text.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private void ResetListingPlanPriceText(IEnumerable<ListingPlanAssignment> assignments)
    {
        listingPlanPriceText.Clear();
        foreach (var assignment in assignments)
            listingPlanPriceText[assignment.Id] = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static ListingPlanValidationIssue? FindListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field) =>
        issues.FirstOrDefault(issue => issue.AssignmentId == assignmentId && issue.Field == field);

    private static string ListingIssueFieldLabel(string field) => field switch
    {
        nameof(ListingPlanAssignment.ItemId) => "Item",
        nameof(ListingPlanAssignment.Quality) => "Quality",
        nameof(ListingPlanAssignment.RetainerId) => "Retainer",
        nameof(ListingPlanAssignment.ListingCount) => "Listings",
        nameof(ListingPlanAssignment.QuantityPerListing) => "Qty each",
        nameof(ListingPlanAssignment.UnitPrice) => "Unit price",
        "Assignment" => "Assignment",
        _ => field,
    };

    private static ListingPlanValidationIssue? PushListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field)
    {
        var capacityIssue = field is nameof(ListingPlanAssignment.RetainerId) or nameof(ListingPlanAssignment.ListingCount)
            ? FindListingIssue(issues, assignmentId, "RetainerCapacity")
            : null;
        var issue = FindListingIssue(issues, assignmentId, field) ??
                    capacityIssue ??
                    FindListingIssue(issues, assignmentId, "Assignment");
        if (issue is not null)
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.45f, .12f, .1f, .72f));
        return issue;
    }

    private static void PopListingIssue(ListingPlanValidationIssue? issue)
    {
        if (issue is null)
            return;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(issue.Message);
        ImGui.PopStyleColor();
    }

    private static void DrawListingQuality(ListingPlanAssignment assignment)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##quality", QualityLabel(assignment.Quality)))
            return;
        foreach (var quality in new[] { ItemQualityPolicy.NqOnly, ItemQualityPolicy.HqOnly })
            if (ImGui.Selectable(QualityLabel(quality), assignment.Quality == quality))
                assignment.Quality = quality;
        ImGui.EndCombo();
    }

    private static void DrawListingRetainer(ListingPlanAssignment assignment, IReadOnlyList<CachedRetainer> retainers)
    {
        ImGui.SetNextItemWidth(-1);
        var current = retainers.FirstOrDefault(retainer => retainer.RetainerId == assignment.RetainerId);
        var label = current?.RetainerName ?? $"Missing retainer · ID {assignment.RetainerId}";
        if (!ImGui.BeginCombo("##retainer", label))
            return;
        foreach (var retainer in retainers)
            if (ImGui.Selectable($"{retainer.RetainerName}##{retainer.RetainerId}", assignment.RetainerId == retainer.RetainerId))
            {
                assignment.RetainerId = retainer.RetainerId;
                assignment.RetainerName = retainer.RetainerName;
            }
        ImGui.EndCombo();
    }

    private int ResolveListingMaxStack(BrowserProjection browser, uint itemId)
    {
        var observed = browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize;
        if (observed is > 0)
            return checked((int)Math.Min(observed.Value, int.MaxValue));
        var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item is { } value && value.StackSize > 0 ? checked((int)value.StackSize) : 1;
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };

    private static string QualityChoiceLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ only",
        ItemQualityPolicy.HqOnly => "HQ only",
        _ => "Any quality",
    };

    private StowageDepositBatch BuildSurplusBatch(
        QuartermasterRuntimeSnapshot runtime,
        StowageEvaluation? evaluation)
    {
        if (evaluation is null)
            return new(runtime.CapturedAtUtc, []);
        var requests = new List<StowageDepositRequest>();
        foreach (var line in evaluation.Lines.Where(line => line.DepositQuantity > 0))
        {
            var stock = runtime.Browser.Items.FirstOrDefault(item => item.ItemId == line.ItemId);
            var nq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player &&
                    stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var hq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player &&
                    stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var remaining = line.DepositQuantity;
            if (line.Quality != ItemQualityPolicy.HqOnly)
            {
                var quantity = Math.Min(remaining, nq);
                if (quantity > 0)
                    requests.Add(new(line.PlanId, line.RuleId, line.ItemId, line.ItemName, false, quantity, CopyRouting(line.Routing)));
                remaining -= quantity;
            }
            if (line.Quality != ItemQualityPolicy.NqOnly)
            {
                var quantity = Math.Min(remaining, hq);
                if (quantity > 0)
                    requests.Add(new(line.PlanId, line.RuleId, line.ItemId, line.ItemName, true, quantity, CopyRouting(line.Routing)));
            }
        }
        return StowageRouter.BuildBatch(
            requests,
            runtime.Retainers,
            runtime.Owner,
            itemId => ResolveMaxStack(runtime.Browser, itemId),
            runtime.CapturedAtUtc);
    }

    private static int ResolveMaxStack(BrowserProjection browser, uint itemId) =>
        checked((int)Math.Clamp(browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize ?? 999, 1, int.MaxValue));

    private static int PlayerQuantity(BrowserProjection browser, TargetPlanItem rule) =>
        browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId)?.Stacks
            .Where(stack =>
                stack.ScopeKind == BrowserScopeKind.Player &&
                (rule.Quality == ItemQualityPolicy.Any ||
                 rule.Quality == ItemQualityPolicy.HqOnly &&
                 stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ ||
                 rule.Quality == ItemQualityPolicy.NqOnly &&
                 stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ))
            .Sum(stack => stack.Quantity) ?? 0;

    private static StowageRoutingPolicy CopyRouting(StowageRoutingPolicy? routing) => new()
    {
        Mode = routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
        Overflow = routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
        PreferredRetainerIds = routing?.PreferredRetainerIds.ToList() ?? [],
    };

    private void ExecuteTransferPlan(Guid planId, bool allowCapacityRecovery = true)
    {
        var current = runtimeSnapshots.Current;
        var plan = current.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(current.Owner));
        if (plan is null)
            return;
        var rules = current.State.PlanItems
            .Where(rule => rule.StowagePlanId == plan.Id)
            .ToArray();
        if (ListingPlanEvaluator.HasUnknownLinkedDemand(current.State, current.Browser, current.Owner, plan.Id))
        {
            if (allowCapacityRecovery && retainerRefresh.StartForPlan(out var refreshRunId))
            {
                PersistTransferPlanRecovery(plan, refreshRunId);
                pendingTransferPlanRecovery = new(planId, refreshRunId);
                transferStatus = "Verifying listing demand before executing the plan.";
                return;
            }
            inlineTransferError = retainerRefresh.Status.Length == 0
                ? "Listing demand could not be verified."
                : retainerRefresh.Status;
            transferStatus = inlineTransferError;
            return;
        }
        rules = ListingPlanEvaluator.ComposeRules(current.State, current.Browser, current.Owner, plan.Id).ToArray();
        var currentStowage = StowageEvaluator.BuildPlan(
            current.State,
            current.Browser,
            current.Owner,
            plan.Id);
        var currentRetrieval = BuildTransferRetrievalEvaluation(current, rules);
        var currentBatch = BuildSurplusBatch(current, currentStowage);
        if (allowCapacityRecovery && TransferExecutionPolicy.RequiresCapacityRecovery(currentBatch))
        {
            if (retainerRefresh.StartForPlan(out var refreshRunId))
            {
                PersistTransferPlanRecovery(plan, refreshRunId);
                pendingTransferPlanRecovery = new(planId, refreshRunId);
                transferStatus = "Refreshing retainer capacity before executing the plan.";
                return;
            }
            inlineTransferError = retainerRefresh.Status;
            return;
        }
        if (!allowCapacityRecovery && currentBatch.RequestedQuantity > 0 && currentBatch.PlannedQuantity == 0)
        {
            inlineTransferError = "No owner retainer has capacity for the items to stow.";
            transferStatus = inlineTransferError;
            return;
        }
        var retrievalOperationId = currentRetrieval.NeededQuantity > 0
            ? journal.CreateTransferRetrieval(current.Owner, plan, rules).OperationId
            : null;
        var depositOperationId = currentBatch.PlannedQuantity > 0
            ? journal.CreateTransferDeposit(current.Owner, plan, currentBatch).OperationId
            : null;
        StartTransfer(transfers.ExecutePlanAsync(retrievalOperationId, depositOperationId));
    }

    public void Tick()
    {
        if (pendingTransferPlanRecovery is not { } pending ||
            !string.Equals(retainerRefresh.LastCompletedRunId, pending.RefreshRunId, StringComparison.Ordinal))
            return;
        pendingTransferPlanRecovery = null;
        if (retainerRefresh.LastRunSucceeded != true)
        {
            inlineTransferError = retainerRefresh.Status;
            transferStatus = retainerRefresh.Status;
            state.Mutate(StateChangeKind.Recovery, document =>
            {
                if (document.TransferPlanRecovery is { } recovery &&
                    recovery.PlanId == pending.PlanId &&
                    string.Equals(recovery.RefreshRunId, pending.RefreshRunId, StringComparison.Ordinal))
                    recovery.FailureMessage = retainerRefresh.Status;
            });
            return;
        }
        var currentState = state.Snapshot();
        var recovery = currentState.TransferPlanRecovery;
        var currentPlan = currentState.StowagePlans.FirstOrDefault(plan =>
            plan.Id == pending.PlanId && plan.Owner.Matches(runtimeSnapshots.Current.Owner));
        if (recovery is null || currentPlan is null ||
            recovery.PlanId != currentPlan.Id ||
            recovery.PlanRevision != currentPlan.Revision ||
            !string.Equals(recovery.RefreshRunId, pending.RefreshRunId, StringComparison.Ordinal))
        {
            state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
            inlineTransferError = currentPlan is null
                ? "The pending Transfer Plan no longer exists."
                : "The Transfer Plan changed while evidence was refreshed; run the current plan when ready.";
            transferStatus = inlineTransferError;
            return;
        }
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
        ExecuteTransferPlan(pending.PlanId, false);
    }

    private void PersistTransferPlanRecovery(StowagePlan plan, string refreshRunId) =>
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = new()
        {
            Owner = runtimeSnapshots.Current.Owner with { },
            PlanId = plan.Id,
            PlanRevision = plan.Revision,
            RefreshRunId = refreshRunId,
            RequestedAtUtc = DateTime.UtcNow,
        });

    private void DismissRetainerRefreshRecovery()
    {
        retainerRefresh.DismissRecovery();
    }

    private void RetryTransferPlanRecovery(StowagePlan plan)
    {
        if (!retainerRefresh.StartForPlan(out var refreshRunId))
        {
            inlineTransferError = retainerRefresh.Status;
            return;
        }
        PersistTransferPlanRecovery(plan, refreshRunId);
        pendingTransferPlanRecovery = new(plan.Id, refreshRunId);
        inlineTransferError = string.Empty;
        transferStatus = "Refreshing retainer evidence before retrying the plan.";
    }

    private void DismissTransferPlanRecovery()
    {
        pendingTransferPlanRecovery = null;
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
        inlineTransferError = string.Empty;
    }

    private void EnsureInlineTransferErrorContext(OwnerScope owner, Guid planId)
    {
        if (inlineTransferErrorPlanId == planId && inlineTransferErrorOwner?.Matches(owner) == true)
            return;
        inlineTransferError = string.Empty;
        inlineTransferErrorOwner = owner with { };
        inlineTransferErrorPlanId = planId;
    }

    private void ClearInlineTransferErrorContext()
    {
        inlineTransferError = string.Empty;
        inlineTransferErrorOwner = null;
        inlineTransferErrorPlanId = null;
    }

    private void DrawOperation(OwnerScope owner)
    {
        var operation = journal.Current(owner);
        if (operation is null)
        {
            ImGui.TextDisabled("No operation has been accepted or run.");
            return;
        }
        ImGui.TextUnformatted($"{operation.Kind} | {operation.Status}");
        if (!string.IsNullOrWhiteSpace(operation.SourcePlanName))
            ImGui.TextDisabled($"From Transfer Plan: {operation.SourcePlanName}");
        ImGui.TextWrapped(operation.Message);
        if (operation.Status == OperationStatuses.Accepted)
        {
            var canExecute = transfers.CanStart && !retainerRefresh.IsRefreshing && !retainerRefresh.IsQueued;
            if (!canExecute)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Execute this operation##{operation.OperationId}"))
                StartTransfer(operation.Kind == OperationKinds.Retrieval
                    ? transfers.ExecuteRetrievalAsync(operation.OperationId)
                    : transfers.ExecuteDepositAsync(operation.OperationId));
            if (!canExecute)
                ImGui.EndDisabled();
        }
        if (operationLineTable.Begin(
                "RQOperationLines",
                DalamudTableLayout.FitContent(
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)))
        {
            foreach (var line in operation.Lines)
                operationLineTable.DrawRow(line, id: $"operation-line:{line.ItemId}:{line.SourceRuleId}");
            operationLineTable.End();
        }
    }

    private IReadOnlyList<ItemChoice> SearchItems(string search, int limit)
    {
        var needle = search.Trim();
        var matches = dataManager.GetExcelSheet<Item>()
            .Select(row => new { row.RowId, Name = row.Name.ExtractText() })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RowId)
            .Take(limit * 2)
            .ToArray();
        return matches
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select((item, index) => new ItemChoice(
                item.RowId,
                item.Name,
                group.Count() > 1 ? $"{item.Name} ({index + 1})" : item.Name)))
            .Take(limit)
            .ToArray();
    }

    private RestockPlan? ResolveSelectedRestockPlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedRestockPlanId is { } selectedId
            ? document.RestockPlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    private static RetrievalPlan BuildRestockEvaluation(
        QuartermasterRuntimeSnapshot runtime,
        RestockPlan plan)
        => BuildTransferRetrievalEvaluation(runtime, RestockPlanCatalog.ToExecutionRows(plan));

    private static RetrievalPlan BuildTransferRetrievalEvaluation(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<TargetPlanItem> rules)
    {
        var playerCounts = runtime.PlayerStorage.Bags
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        return RestockPlanner.Build(
            rules,
            playerCounts,
            runtime.Retainers,
            runtime.Owner,
            runtime.CapturedAtUtc,
            runtime.Browser);
    }

    private void StartTransfer(Task<TransferExecutionResult> transfer) => _ = ObserveTransferAsync(transfer);

    private async Task ObserveTransferAsync(Task<TransferExecutionResult> transfer)
    {
        activeTransferTask = transfer;
        try
        {
            var result = await transfer;
            transferStatus = result.Message;
        }
        catch (Exception exception)
        {
            transferStatus = $"Transfer failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(activeTransferTask, transfer))
                activeTransferTask = null;
        }
    }

    public void CancelActiveTransfer()
    {
        if (activeTransferTask is not null || transfers.IsRunning)
        {
            try { transfers.CancelActive(); }
            catch { }
        }
    }

    public bool CancelAndWaitForActiveTransfer(TimeSpan timeout)
    {
        var task = Volatile.Read(ref activeTransferTask);
        if (task is null && !transfers.IsRunning)
            return true;
        try { transfers.CancelActive(); }
        catch { }
        if (task is null)
            return !transfers.IsRunning;
        try { return task.Wait(timeout); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException)) { return true; }
    }

}
