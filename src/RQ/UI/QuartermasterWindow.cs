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
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private readonly DalamudTableProjection<OperationLine> operationLineTable;
    private readonly BrowserQueryController queries = new();
    private readonly RootConfirmationDialog confirmationDialog = new();
    private readonly OperationHistoryDialog historyDialog;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly RestockPlanEditor restockPlanEditor;
    private readonly TransferPlanEditor transferPlanEditor;
    private readonly ListingPlanEditor listingPlanEditor;
    private readonly ListingWorkspace listingWorkspace;
    private readonly TransferReviewDialog transferReviewDialog;
    private readonly TransferExecutionController transferExecution;
    private readonly VendorProcurementReviewDialog vendorReviewDialog;
    private StockWorkbenchProjection? stockWorkbenchProjection;
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private long stockSelectionRevision = -1;
    private int stockProjectionBuildCount;
    private int transferProjectionBuildCount;
    private WorkbenchView? requestedView;
    private bool clearAgentReviewWindowOverride;
    private int captureCollapseRestoreFramesRemaining;
    private int viewportReopenGuardFramesRemaining;
    private bool viewportReopenGuardNeedsRelease;
    private string vendorStatus = string.Empty;
    private WorkbenchView? capturePreviousView;
    private TransferReviewDialogState? capturePreviousTransferReviewState;
    private VendorProcurementReviewDialogState? capturePreviousVendorReviewState;

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
        RestockPlanEditor? restockEditor = null;
        TransferPlanEditor? transferEditor = null;
        itemGroupWorkspace = new(
            state,
            reviewRegistry,
            confirmationDialog,
            SearchItems,
            (origin, groupId) =>
            {
                if (origin == ItemGroupEditorOrigin.Restock)
                    restockEditor?.SelectItemGroup(groupId);
                else
                    transferEditor?.SelectItemGroup(groupId);
            },
            groupId =>
            {
                restockEditor?.ClearItemGroup(groupId);
                transferEditor?.ClearItemGroup(groupId);
            });
        transferPlanEditor = transferEditor = new(
            state,
            workbench,
            itemGroupWorkspace,
            reviewRegistry,
            SearchItems,
            () => restockEditor?.Close(),
            () => requestedView = WorkbenchView.Stowage);
        restockPlanEditor = restockEditor = new(
            state,
            workbench,
            itemGroupWorkspace,
            reviewRegistry,
            SearchItems,
            transferPlanEditor.Close,
            () => requestedView = WorkbenchView.Restock);
        listingPlanEditor = new(state, dataManager, SearchItems);
        listingWorkspace = new(
            workbench,
            listingNavigation,
            reviewRegistry,
            FocusStockFromListings,
            listingPlanEditor.Open,
            stockSelection.Clear);
        transferExecution = new(state, runtimeSnapshots, journal, transfers, retainerRefresh);
        transferReviewDialog = new(
            () => runtimeSnapshots.Current,
            ResolveTransferWorkbenchProjection,
            () => transfers.CanStart,
            () => retainerRefresh.IsRefreshing || retainerRefresh.IsQueued,
            planId => transferExecution.ExecutePlan(planId),
            reviewRegistry);
        vendorReviewDialog = new(vendorProcurement, reviewRegistry, () => vendorStatus = string.Empty);
        stockTable = CreateStockTable();
        restockPlanTable = CreateRestockPlanTable();
        transferWorkbenchTable = CreateTransferWorkbenchTable();
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
        var transferEditor = transferPlanEditor.Snapshot(document, runtime.Owner);
        var restockEditor = restockPlanEditor.Snapshot(document, runtime.Owner);

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
            restockEditor.IsOpen || transferEditor.IsOpen,
            restockEditor.HasUnsavedChanges ||
            transferEditor.HasUnsavedChanges,
            itemGroups.SelectedGroupId,
            itemGroups.SelectedGroupName,
            itemGroups.WorkspaceEditorOpen,
            itemGroups.HasUnsavedChanges,
            selectedPlan?.Id,
            selectedPlan?.Name,
            selectedEvaluation?.RetrieveQuantity ?? 0,
            selectedEvaluation?.DepositQuantity ?? 0,
            transferEditor.IsOpen);
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
                transferPlanEditor.Close();
            restockPlanEditor.Close();
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
        restockPlanEditor.Draw(runtime);
        transferPlanEditor.Draw(runtime);
        listingPlanEditor.Draw(runtime);
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
            transferPlanEditor.Close();
        restockPlanEditor.Close();
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
                retainerRefresh.DismissRecovery();
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
                    listingPlanEditor.Open(runtime, new(selectedDemand.ItemId, selectedDemand.Quality));
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
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
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
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
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
            restockPlanEditor.Open(RestockPlanCatalog.Draft(state.Snapshot(), owner, selected.Id));
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
                    restockPlanEditor.Open(RestockPlanCatalog.Draft(state.Snapshot(), current.Owner, currentPlan.Id));
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
                restockPlanEditor.Open(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();

            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Duplicate plan") && selected is not null)
                restockPlanEditor.Open(RestockPlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
            if (selected is null)
                ImGui.EndDisabled();

            var canCreateFromStowage = owner.HasStableIdentity &&
                                       StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Create from Stowage") && canCreateFromStowage)
                restockPlanEditor.Open(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
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
                restockPlanEditor.Open(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            reviewRegistry.RegisterLastButton(
                "quartermaster.restock.new",
                "Open a new Restock Plan draft",
                owner.HasStableIdentity,
                () => restockPlanEditor.Open(RestockPlanCatalog.NewDraft(state.Snapshot(), runtimeSnapshots.Current.Owner)),
                owner.HasStableIdentity ? "Nothing is saved until Apply" : "Owner unavailable");
            ImGui.SameLine();
            var canCreateFromStowage = StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Button("Create from Stowage") && canCreateFromStowage)
                restockPlanEditor.Open(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
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
            transferExecution.Start(transfers.ExecuteRetrievalAsync(operation.OperationId));
        }
        if (!canExecute)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(transferExecution.Status);

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
            restockPlanEditor.Open(RestockPlanCatalog.Draft(state.Snapshot(), row.Owner, row.PlanId));
            restockPlanEditor.FocusItem(row.Item.Id);
        }
        ImGui.TextDisabled(QualityLabel(row.Item.Quality));
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
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
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
        restockPlanEditor.Open(draft);
        restockPlanEditor.FocusItem(item.Id);
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
        transferPlanEditor.Open(draft);
        workbench.ClearSelection();
    }

    private void ClosePlanEditors()
    {
        restockPlanEditor.Close();
        transferPlanEditor.Close();
        listingPlanEditor.Close();
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
            transferPlanEditor.Open(StowagePlanCatalog.NewDraft(state.Snapshot(), owner));
        if (!owner.HasStableIdentity)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate") && selected is not null)
            transferPlanEditor.Open(StowagePlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
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
                transferPlanEditor.Open(selected.Id, owner);
            if (ImGui.Selectable("Delete plan") && selected is not null)
                RequestDeleteStowagePlan(selected, owner);
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null)
        {
            transferExecution.ClearInlineErrorContext();
            ImGui.Spacing();
            ImGui.TextUnformatted("No Transfer Plans yet.");
            ImGui.TextDisabled("Create one, then select stock on the left or add items by name.");
            return;
        }
        transferExecution.EnsureInlineErrorContext(owner, selected.Id);

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
        var planNotice = !string.IsNullOrWhiteSpace(transferExecution.InlineError)
            ? transferExecution.InlineError
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
                transferExecution.RetryRecovery(plan);
            ImGui.SameLine();
            if (ImGui.Button("Dismiss##TransferPlanRecovery"))
                transferExecution.DismissRecovery();
        }
        else if (ImGui.Button("Dismiss##TransferPlanNotice"))
        {
            transferExecution.ClearInlineError();
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
        var deposit = TransferPlanEvaluation.BuildSurplusBatch(runtime, stowage);
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
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
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
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
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
                transferExecution.Start(operation.Kind == OperationKinds.Retrieval
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

    public void Tick() => transferExecution.Tick();

    public void CancelActiveTransfer() => transferExecution.CancelActive();

    public bool CancelAndWaitForActiveTransfer(TimeSpan timeout) =>
        transferExecution.CancelAndWait(timeout);

}
