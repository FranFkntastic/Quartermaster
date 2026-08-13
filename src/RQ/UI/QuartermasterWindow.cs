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
    private readonly IDataManager dataManager;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly WorkbenchState workbench = new();
    private readonly TableSelectionModel<uint> stockSelection = new();
    private readonly DalamudTableProjection<StockWorkbenchRow> stockTable;
    private readonly DalamudTableProjection<RestockPlanRow> restockPlanTable;
    private readonly DalamudTableProjection<OperationLine> operationLineTable;
    private readonly BrowserQueryController queries = new();
    private readonly RootConfirmationDialog confirmationDialog = new();
    private readonly OperationHistoryDialog historyDialog;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly RestockPlanEditor restockPlanEditor;
    private readonly TransferPlanEditor transferPlanEditor;
    private readonly TransferPlanWorkspace transferPlanWorkspace;
    private readonly ListingPlanEditor listingPlanEditor;
    private readonly ListingWorkspace listingWorkspace;
    private readonly TransferReviewDialog transferReviewDialog;
    private readonly TransferExecutionController transferExecution;
    private readonly VendorProcurementReviewDialog vendorReviewDialog;
    private StockWorkbenchProjection? stockWorkbenchProjection;
    private long stockSelectionRevision = -1;
    private int stockProjectionBuildCount;
    private WorkbenchView? requestedView;
    private bool clearAgentReviewWindowOverride;
    private int captureCollapseRestoreFramesRemaining;
    private int viewportReopenGuardFramesRemaining;
    private bool viewportReopenGuardNeedsRelease;
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
        TransferReviewDialog? transferReview = null;
        VendorProcurementReviewDialog? vendorReview = null;
        transferPlanWorkspace = new(
            state,
            runtimeSnapshots,
            transfers,
            retainerRefresh,
            vendorProcurement,
            workbench,
            transferPlanEditor,
            transferExecution,
            reviewRegistry,
            (planId, planName) => transferReview?.Request(planId, planName),
            review => vendorReview?.Request(review),
            RequestDeleteStowagePlan);
        transferReviewDialog = transferReview = new(
            () => runtimeSnapshots.Current,
            transferPlanWorkspace.ResolveProjection,
            () => transfers.CanStart,
            () => retainerRefresh.IsRefreshing || retainerRefresh.IsQueued,
            planId => transferExecution.ExecutePlan(planId),
            reviewRegistry);
        vendorReviewDialog = vendorReview = new(vendorProcurement, reviewRegistry, transferPlanWorkspace.ClearVendorStatus);
        stockTable = CreateStockTable();
        restockPlanTable = CreateRestockPlanTable();
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
        var transferWorkspace = transferPlanWorkspace.Snapshot();

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
            transferWorkspace.ProjectionBuildCount,
            transferWorkspace.RenderedRowCount,
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
            transferPlanWorkspace.RequestSelectedTransferReview();
        if (target == "vendor-review")
            transferPlanWorkspace.RequestSelectedVendorReview();
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
                transferPlanWorkspace.Draw(runtime);
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
            transferPlanWorkspace.RequestSelectedTransferReview();
        if (normalizedTarget == "vendor-review")
            transferPlanWorkspace.RequestSelectedVendorReview();
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
