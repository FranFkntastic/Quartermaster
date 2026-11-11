using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Lumina.Excel.Sheets;
using RQ.Automation;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

public sealed class QuartermasterWindow : Window
{
    private const string MainWindowName = "Quartermaster###RQMain";

    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly IDataManager dataManager;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly WorkbenchState workbench = new();
    private readonly RootConfirmationDialog confirmationDialog = new();
    private readonly OperationHistoryDialog historyDialog;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly RestockPlanEditor restockPlanEditor;
    private readonly StockWorkspace stockWorkspace;
    private readonly TransferPlanEditor transferPlanEditor;
    private readonly TransferPlanWorkspace transferPlanWorkspace;
    private readonly ListingPlanDraftSession listingSession;
    private readonly Dictionary<uint, bool?> listability = [];
    private readonly ListingWorkspace listingWorkspace;
    private readonly MarketRunPane marketRunPane;
    private readonly TransferReviewDialog transferReviewDialog;
    private readonly TransferExecutionController transferExecution;
    private readonly VendorProcurementReviewDialog vendorReviewDialog;
    private WorkbenchView? requestedView;
    private WorkbenchView? deferredView;
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
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<GilVendorCatalog>? vendorCatalogSource,
        Func<uint, uint?> resolveWorkshopProject,
        string configDirectory,
        RQ.Interop.MarketMafiosoSavedJobClient marketImporter)
        : base(MainWindowName, ImGuiWindowFlags.NoScrollbar)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.dataManager = dataManager;
        this.reviewRegistry = reviewRegistry;
        marketRunPane = new(resolveWorkshopProject, configDirectory, marketImporter);
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
            () => requestedView = WorkbenchView.Stowage,
            vendorCatalogSource);
        restockPlanEditor = restockEditor = new(
            state,
            workbench,
            itemGroupWorkspace,
            reviewRegistry,
            SearchItems,
            transferPlanEditor.Close,
            () => requestedView = WorkbenchView.Restock);
        listingSession = new(state, ResolveListingMaxStack, id => dataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.CanBeHq == true);
        transferExecution = new(state, runtimeSnapshots, journal, transfers, retainerRefresh);
        stockWorkspace = new(
            state,
            runtimeSnapshots,
            retainerRefresh,
            transferExecution,
            configuration,
            saveConfiguration,
            workbench,
            itemGroupWorkspace,
            transferPlanEditor,
            listingSession,
            (runtime, key) => { listingWorkspace!.Focus(runtime, key); deferredView = WorkbenchView.Listings; },
            reviewRegistry,
            () => deferredView = WorkbenchView.Stock);
        listingWorkspace = new(
            workbench,
            listingNavigation,
            reviewRegistry,
            listingSession,
            SearchListableItems,
            Listability,
            retainerRefresh,
            marketRunPane);
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

    public bool StockBrowserVisible =>
        IsOpen && workbench.View is not (WorkbenchView.Listings or WorkbenchView.Activity);

    private double WindowDrawMilliseconds { get; set; }
    private double ContentDrawMilliseconds { get; set; }
    private double PlanDrawMilliseconds { get; set; }
    private double ReviewFinalizeMilliseconds { get; set; }
    public AgentBridgeCaptureRegion? AgentCaptureRegion { get; private set; }

    public string AgentCaptureWindowName => ActiveCapturePresentationTarget() switch
    {
        "activity" => historyDialog.CaptureWindowName ?? MainWindowName,
        "transfer-review" => transferReviewDialog.CaptureWindowName ?? MainWindowName,
        "vendor-review" => vendorReviewDialog.CaptureWindowName ?? MainWindowName,
        _ => MainWindowName,
    };

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
        var stock = stockWorkspace.Snapshot();

        var itemGroups = itemGroupWorkspace.Snapshot(
            IsOpen && workbench.View == WorkbenchView.ItemGroups);
        return new QuartermasterUiSnapshot(
            IsOpen,
            workbench.View is WorkbenchView.Listings or WorkbenchView.Activity
                ? workbench.View.ToString().ToLowerInvariant()
                : "transfer",
            workbench.ItemFilterState.Expression,
            stock.VisibleCount,
            stock.RenderedRowCount,
            stock.ProjectionBuildCount,
            stock.TableApplyCount,
            transferWorkspace.ProjectionBuildCount,
            transferWorkspace.RenderedRowCount,
            WindowDrawMilliseconds,
            ContentDrawMilliseconds,
            stock.DrawMilliseconds,
            PlanDrawMilliseconds,
            ReviewFinalizeMilliseconds,
            "mixed",
            IsOpen && (restockEditor.IsOpen || transferEditor.IsOpen),
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
            IsOpen && transferEditor.IsOpen);
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
        requestedView ??= deferredView;
        deferredView = null;
        var runtime = runtimeSnapshots.Current;
        workbench.EnsureScope(runtime.Browser);
        listingSession.Ensure(runtime);
        transferPlanEditor.Ensure(runtime);
        if (requestedView == WorkbenchView.Activity)
        { historyDialog.RequestOpen(); requestedView = null; }
        ImGui.TextUnformatted(runtime.Owner.HasStableIdentity
            ? $"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName}" : "Owner scope unavailable");
        ImGui.SameLine();
        if (ImGui.SmallButton("History")) historyDialog.RequestOpen();
        reviewRegistry.RegisterLastButton("quartermaster.history.open", "Open transfer history", true,
            historyDialog.RequestOpen, "Recent Quartermaster operations");
        historyDialog.Draw(runtime.Owner);

        if (ImGui.BeginTabBar("RQViews"))
        {
            var marketOpen = ImGui.BeginTabItem($"Market ({runtime.Browser.Listings.Count:N0})###RQMarketTab",
                TabFlags(requestedView == WorkbenchView.Listings));
            RegisterWorkspaceControl("listings", WorkbenchView.Listings);
            if (marketOpen)
            {
                workbench.View = WorkbenchView.Listings;
                DrawMarketTab(runtime);
                ImGui.EndTabItem();
            }
            var stockOpen = ImGui.BeginTabItem("Stock & plans",
                TabFlags(requestedView is WorkbenchView.Stock or WorkbenchView.Restock or WorkbenchView.Stowage or WorkbenchView.ItemGroups));
            RegisterWorkspaceControl("stock", WorkbenchView.Stock);
            RegisterWorkspaceControl("transfer-plans", WorkbenchView.Stowage);
            if (stockOpen)
            {
                if (requestedView == WorkbenchView.ItemGroups) workbench.View = WorkbenchView.ItemGroups;
                else if (requestedView.HasValue || workbench.View != WorkbenchView.ItemGroups) workbench.View = WorkbenchView.Stowage;
                if (ImGui.BeginTable("RQStockTransferWorkbench", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
                {
                    ImGui.TableSetupColumn("Stock", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableSetupColumn("Transfer plan", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.BeginChild("RQSourcePane", Vector2.Zero, false)) stockWorkspace.Draw(runtime);
                    ImGui.EndChild();
                    ImGui.TableNextColumn();
                    if (ImGui.BeginChild("RQPlanPane", Vector2.Zero, false))
                    {
                        var started = Stopwatch.GetTimestamp();
                        var transferSelected = workbench.View != WorkbenchView.ItemGroups;
                        if (ImGui.Selectable("Transfer plan##RQPlannerMode", transferSelected, ImGuiSelectableFlags.None, new Vector2(112, 0))) workbench.View = WorkbenchView.Stowage;
                        reviewRegistry.RegisterLastButton("quartermaster.workspace.transfer", "Show Transfer Plan workspace", true, () => workbench.View = WorkbenchView.Stowage, "Available");
                        ImGui.SameLine();
                        if (ImGui.Selectable("Item groups##RQPlannerMode", !transferSelected, ImGuiSelectableFlags.None, new Vector2(104, 0))) workbench.View = WorkbenchView.ItemGroups;
                        reviewRegistry.RegisterLastButton("quartermaster.workspace.item-groups", "Show Item Groups workspace", true, () => workbench.View = WorkbenchView.ItemGroups, "Available");
                        ImGui.Separator();
                        if (workbench.View == WorkbenchView.ItemGroups) itemGroupWorkspace.DrawWorkspace(runtime.State);
                        else transferPlanWorkspace.Draw(runtime);
                        PlanDrawMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    ImGui.EndChild();
                    ImGui.EndTable();
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
        transferPlanEditor.Draw(runtime);
        transferReviewDialog.Draw();
        vendorReviewDialog.Draw();
        confirmationDialog.Draw();
    }

    private static ImGuiTabItemFlags TabFlags(bool requested) =>
        requested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    private void DrawMarketTab(QuartermasterRuntimeSnapshot runtime)
    {
        var showSave = runtime.Owner.HasStableIdentity && (listingSession.IsDirty || listingSession.ConflictCount > 0 ||
            listingSession.MergedElsewhere || listingSession.Error is not null);
        var stripHeight = showSave ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y : 0;
        if (ImGui.BeginChild("RQMarketBody", new Vector2(0, -stripHeight), false))
            listingWorkspace.Draw(runtime);
        ImGui.EndChild();
        if (showSave) DrawListingSaveStrip(runtime);
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

    private void RequestDeleteStowagePlan(StowagePlan plan, OwnerScope owner)
    {
        if (plan.IsEphemeral) return;
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
                    return (Guid?)StowagePlanCatalog.EnsureScratch(document, owner).Id;
                });
                transferPlanEditor.Forget(planId);
            });
    }

    private void ClosePlanEditors()
    {
        restockPlanEditor.Close();
        transferPlanEditor.Close();
        transferReviewDialog.Clear();
        vendorReviewDialog.Clear();
    }








    private void DrawListingSaveStrip(QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.Separator();
        var count = listingSession.ChangeCount;
        ImGui.TextUnformatted($"{count} unsaved Listing Plan change{(count == 1 ? string.Empty : "s")}");
        var message = listingSession.Error ?? (listingSession.ConflictCount > 0
            ? $"Listing Plan changed elsewhere · {listingSession.ConflictCount} conflicts resolved using your draft"
            : listingSession.MergedElsewhere ? "Listing Plan changed elsewhere · merged into your draft" : null);
        var buttonWidth = ImGui.CalcTextSize("DiscardSave Listing Plan").X + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X;
        var buttonX = ImGui.GetContentRegionMax().X - buttonWidth;
        if (message is not null)
        {
            ImGui.SameLine();
            var available = Math.Max(0, buttonX - ImGui.GetCursorPosX() - ImGui.GetStyle().ItemSpacing.X);
            var display = message;
            while (display.Length > 0 && ImGui.CalcTextSize(display + "…").X > available) display = display[..^1];
            if (display.Length < message.Length) display += "…";
            ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Amber), display);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(message);
        }
        ImGui.SameLine(buttonX);
        if (ImGui.SmallButton("Discard")) listingSession.Discard(runtime);
        reviewRegistry.RegisterLastButton("quartermaster.listings.discard", "Discard the Listing Plan draft", true,
            () => listingSession.Discard(runtimeSnapshots.Current), "Discard unsaved changes");
        ImGui.SameLine();
        var canSave = listingSession.Issues(runtime).Count == 0;
        ImGui.BeginDisabled(!canSave);
        if (ImGui.SmallButton("Save Listing Plan")) listingSession.Save(runtime);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton("quartermaster.listings.save", "Save the Listing Plan draft", canSave,
            () => listingSession.Save(runtimeSnapshots.Current), canSave ? "Ready" : listingSession.SaveBlockedTooltip(runtime));
        if (!canSave && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(listingSession.SaveBlockedTooltip(runtime));
    }

    private int ResolveListingMaxStack(BrowserProjection browser, uint itemId)
    {
        var observed = browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize;
        if (observed is > 0) return checked((int)Math.Min(observed.Value, int.MaxValue));
        var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item is { } value && value.StackSize > 0 ? checked((int)value.StackSize) : 1;
    }

    private bool? Listability(uint itemId)
    {
        if (listability.TryGetValue(itemId, out var result)) return result;
        var row = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return listability[itemId] = row is { } item ? !item.IsUntradable && item.ItemSearchCategory.RowId > 0 : null;
    }

    private IReadOnlyList<ItemChoice> SearchListableItems(string search, int limit)
    {
        var needle = search.Trim();
        var id = needle.StartsWith('#') && uint.TryParse(needle.AsSpan(1), out var parsed) ? parsed : (uint?)null;
        return dataManager.GetExcelSheet<Item>().Where(row => !row.IsUntradable && row.ItemSearchCategory.RowId > 0)
            .Select(row => new { row.RowId, Name = row.Name.ExtractText() })
            .Where(i => !string.IsNullOrWhiteSpace(i.Name) && (id.HasValue ? i.RowId == id.Value : i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.RowId)
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Select(i => new ItemChoice(i.RowId, i.Name, g.Count() > 1 ? $"{i.Name} · #{i.RowId}" : i.Name)))
            .Take(limit).ToArray();
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

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    public void Tick() => transferExecution.Tick();

    public void CancelActiveTransfer() => transferExecution.CancelActive();

    public bool CancelAndWaitForActiveTransfer(TimeSpan timeout) =>
        transferExecution.CancelAndWait(timeout);

}
