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
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.FFXIV.Filtering;
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
    private readonly AutoRetainerRefreshService autoRetainer;
    private readonly ListingNavigationCoordinator listingNavigation;
    private readonly IDataManager dataManager;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly WorkbenchState workbench = new();
    private readonly TableSelectionModel<uint> stockSelection = new();
    private readonly TableSelectionModel<uint> listingGroupSelection = new();
    private readonly TableSelectionModel<ListingRowKey> physicalListingSelection = new();
    private readonly DalamudTableProjection<ListingGroupView> listingGroupTable;
    private readonly DalamudTableProjection<ListingRow> physicalListingTable;
    private readonly DalamudTableProjection<StockWorkbenchRow> stockTable;
    private readonly DalamudTableProjection<RestockPlanRow> restockPlanTable;
    private readonly DalamudTableProjection<ItemGroupItem> itemGroupWorkspaceTable;
    private readonly DalamudTableProjection<ItemGroupItem> itemGroupEditorTable;
    private readonly DalamudTableProjection<RestockPlanItem> restockDraftTable;
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private readonly DalamudTableProjection<StowageDraftRow> stowageDraftTable;
    private readonly DalamudTableProjection<TransferReviewRow> transferReviewTable;
    private readonly DalamudTableProjection<OperationLine> operationLineTable;
    private readonly BrowserQueryController queries = new();
    private readonly RootConfirmationDialog confirmationDialog = new();
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
    private bool requestCaptureFocus;
    private StowagePlanDraft? stowageDraft;
    private readonly TableSelectionModel<Guid> selectedStowageRuleIds = new();
    private Guid? activeStowageRuleId;
    private Guid? selectedItemGroupId;
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
    private ItemGroupDraft? itemGroupDraft;
    private WorkbenchView? itemGroupEditorOrigin;
    private readonly TableSelectionModel<ItemGroupItem> selectedItemGroupItems = new();
    private string itemGroupFilter = string.Empty;
    private string itemGroupItemSearch = string.Empty;
    private ItemChoice? selectedItemGroupChoice;
    private ItemQualityPolicy itemGroupAddQuality = ItemQualityPolicy.Any;
    private string itemGroupEditorError = string.Empty;
    private string inlineTransferError = string.Empty;
    private string itemGroupWorkspaceStatus = string.Empty;
    private bool requestDeleteItemGroup;
    private bool requestHistoryOpen;
    private bool requestHistoryClose;
    private bool capturePresentingHistory;
    private bool requestTransferReviewOpen;
    private TransferReviewRequest? transferReview;
    private WorkbenchView? capturePreviousView;
    private TransferReviewRequest? capturePreviousTransferReview;
    private bool capturePreviousTransferReviewOpenRequest;
    private ListingRowKey? focusedListing;

    public QuartermasterWindow(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        OperationJournal journal,
        TransferCoordinator transfers,
        AutoRetainerRefreshService autoRetainer,
        ListingNavigationCoordinator listingNavigation,
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
        this.autoRetainer = autoRetainer;
        this.listingNavigation = listingNavigation;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.reviewRegistry = reviewRegistry;
        listingGroupTable = new(
        [
            new(
                "Item",
                1.45f,
                row => row.ItemName,
                row => row.ItemName,
                ImGuiTableColumnFlags.WidthStretch),
            new(
                "Listed",
                62,
                row => row.Quantity.ToString("N0"),
                row => row.Quantity,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Unlisted",
                72,
                row => row.UnlistedQuantity.IsKnown ? row.UnlistedQuantity.Value.ToString("N0") : "—",
                row => row.UnlistedQuantity.IsKnown ? row.UnlistedQuantity.Value : -1,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Retainers",
                72,
                row => row.RetainerCount.ToString("N0"),
                row => row.RetainerCount,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Price range",
                154,
                ListingPriceRange,
                row => row.HasKnownPrice ? row.LowestPrice : decimal.MinValue,
                Alignment: DalamudTableCellAlignment.Right),
        ]);
        physicalListingTable = new(
        [
            new(
                "Retainer",
                1f,
                row => row.RetainerName,
                row => row.RetainerName,
                ImGuiTableColumnFlags.WidthStretch,
                DrawContextMenu: DrawListingRetainerContextMenu),
            new(
                "Qty",
                62,
                row => row.Quantity.ToString("N0"),
                row => row.Quantity,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Quality",
                78,
                row => row.Quality.ToString(),
                row => row.Quality),
            new(
                "Unit price",
                104,
                ListingUnitPrice,
                row => row.UnitPrice.IsKnown ? row.UnitPrice.Value : decimal.MinValue,
                Alignment: DalamudTableCellAlignment.Right),
        ]);
        stockTable = CreateStockTable();
        restockPlanTable = CreateRestockPlanTable();
        itemGroupWorkspaceTable = CreateItemGroupWorkspaceTable();
        itemGroupEditorTable = CreateItemGroupEditorTable();
        restockDraftTable = CreateRestockDraftTable();
        transferWorkbenchTable = CreateTransferWorkbenchTable();
        stowageDraftTable = CreateStowageDraftTable();
        transferReviewTable = CreateTransferReviewTable();
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
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort,
            DrawContextMenu: DrawStockRowContextMenu),
        new(
            "Player",
            80,
            row => row.Item.PlayerQuantity.ToString("N0"),
            row => row.Item.PlayerQuantity,
            Alignment: DalamudTableCellAlignment.Right,
            DrawContextMenu: DrawStockRowContextMenu),
        new(
            "Stored",
            84,
            row => row.Item.RetainerQuantity.ToString("N0"),
            row => row.Item.RetainerQuantity,
            Alignment: DalamudTableCellAlignment.Right,
            DrawContextMenu: DrawStockRowContextMenu),
        new(
            "Target",
            80,
            row => row.Rule?.TargetQuantity.ToString("N0") ?? "—",
            row => row.Rule?.TargetQuantity ?? -1,
            TextColor: row => row.Rule is null
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : null,
            Alignment: DalamudTableCellAlignment.Right,
            DrawContextMenu: DrawStockRowContextMenu),
        new(
            "Plan state",
            1f,
            StockPlanState,
            row => StockPlanState(row),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: row => row.Rule is null
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : TransferActionColor(row.Line?.Action),
            DrawContextMenu: DrawStockRowContextMenu),
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

    private DalamudTableProjection<ItemGroupItem> CreateItemGroupWorkspaceTable() => new(
    [
        new(
            "Item",
            1.4f,
            item => item.ItemName,
            item => item.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new(
            "Quality",
            145,
            item => QualityChoiceLabel(item.Quality),
            Draw: DrawItemGroupQuality),
        new(
            "##remove",
            62,
            _ => string.Empty,
            Draw: DrawItemGroupWorkspaceRemove),
    ]);

    private DalamudTableProjection<ItemGroupItem> CreateItemGroupEditorTable() => new(
    [
        new(
            "Item",
            1.7f,
            item => item.ItemName,
            item => item.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new(
            "Quality identity",
            180,
            item => QualityChoiceLabel(item.Quality),
            Draw: DrawItemGroupQuality),
        new("", 28, _ => string.Empty, Draw: DrawItemGroupEditorRemove),
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
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row =>
            {
                ImGui.TextUnformatted(row.Rule.ItemName);
                ImGui.SameLine();
                ImGui.TextDisabled(QualityLabel(row.Rule.Quality));
            }),
        new(
            "Player",
            64,
            row => row.PlayerQuantity.ToString("N0"),
            row => row.PlayerQuantity,
            Alignment: DalamudTableCellAlignment.Right),
        new("Target", 82, row => row.Rule.TargetQuantity.ToString("N0"), Draw: DrawTransferTarget),
        new(
            "Diff",
            68,
            row => SignedQuantity(row.Difference),
            row => row.Difference,
            TextColor: row => TransferActionColor(row.Line?.Action),
            Alignment: DalamudTableCellAlignment.Right),
        new(
            "Outcome",
            112,
            TransferOutcome,
            row => TransferOutcome(row),
            TextColor: row => TransferActionColor(row.Line?.Action)),
        new(
            "Route",
            1.1f,
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawInlineTransferRoute(row.Owner, row.PlanId, row.Rule, row.Runtime)),
        new("##remove", 28, _ => string.Empty, Draw: row =>
        {
            if (ImGui.SmallButton($"X##remove-transfer:{row.Rule.Id}"))
                RemoveTransferRule(row.Owner, row.PlanId, row.Rule.Id);
        }),
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
            "Now",
            92,
            StowageDraftOutcome,
            TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new(
            "Destination",
            1.1f,
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner),
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawStowageRouteCombo(row.Rule, row.Runtime)),
        new("Overflow", 112, row => OverflowLabel(row.Rule.Routing.Overflow), Draw: row => DrawStowageOverflowCombo(row.Rule)),
        new("", 28, _ => string.Empty, Draw: DrawStowageDraftRemove),
    ]);

    private DalamudTableProjection<TransferReviewRow> CreateTransferReviewTable() => new(
    [
        new(
            "Item",
            1.3f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new("Player / target", 120, row => $"{row.PlayerQuantity:N0} / {row.Rule.TargetQuantity:N0}"),
        new(
            "Diff",
            72,
            row => SignedQuantity(row.Difference),
            row => row.Difference,
            TextColor: row => TransferActionColor(row.Line?.Action),
            Alignment: DalamudTableCellAlignment.Right),
        new(
            "Planned movement",
            1.2f,
            TransferReviewOutcome,
            row => TransferReviewOutcome(row),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: row => TransferActionColor(row.Line?.Action)),
    ]);

    private static DalamudTableProjection<OperationLine> CreateOperationLineTable() => new(
    [
        new("Item", 1f, line => line.ItemName, line => line.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Target", 80, line => line.TargetQuantity.ToString("N0"), line => line.TargetQuantity, Alignment: DalamudTableCellAlignment.Right),
        new("Submitted shortage", 120, line => line.ShortageQuantity.ToString("N0"), line => line.ShortageQuantity, Alignment: DalamudTableCellAlignment.Right),
        new("Transferred", 90, line => line.TransferredQuantity.ToString("N0"), line => line.TransferredQuantity, Alignment: DalamudTableCellAlignment.Right),
        new("Remaining", 90, line => Math.Max(0, line.ShortageQuantity - line.TransferredQuantity).ToString("N0"), line => Math.Max(0, line.ShortageQuantity - line.TransferredQuantity), Alignment: DalamudTableCellAlignment.Right),
    ]);

    public Guid? SelectedRestockPlanId =>
        SelectedStowagePlanId;

    public string? SelectedRestockPlanName =>
        SelectedStowagePlanName;

    public bool StockBrowserVisible =>
        IsOpen && workbench.View is not (WorkbenchView.Listings or WorkbenchView.Activity);

    public int SelectedRestockNeededQuantity
    {
        get
        {
            var runtime = runtimeSnapshots.Current;
            var plan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
            return plan is null
                ? 0
                : StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, runtime.Owner, plan.Id)?.RetrieveQuantity ?? 0;
        }
    }

    public string CurrentWorkspace => workbench.View is WorkbenchView.Listings or WorkbenchView.Activity
        ? workbench.View.ToString().ToLowerInvariant()
        : "transfer";
    public string StockFilter => workbench.ItemFilterState.Expression;
    public int VisibleStockCount { get; private set; }
    public int RenderedStockRowCount { get; private set; }
    public int StockProjectionBuildCount => stockProjectionBuildCount;
    public int StockTableApplyCount => stockTable.ApplyCount;
    public int TransferProjectionBuildCount => transferProjectionBuildCount;
    public int RenderedTransferRowCount { get; private set; }
    public double WindowDrawMilliseconds { get; private set; }
    public double ContentDrawMilliseconds { get; private set; }
    public double StockDrawMilliseconds { get; private set; }
    public double PlanDrawMilliseconds { get; private set; }
    public double ReviewFinalizeMilliseconds { get; private set; }
    public string CurrentTransferDirection => "mixed";
    public bool StowageEditorOpen => stowageDraft is not null && (requestStowageEditorOpen || stowageEditorVisible);
    public bool RestockEditorOpen => restockDraft is not null && (requestRestockEditorOpen || restockEditorVisible);
    public bool ItemGroupEditorOpen =>
        IsOpen && workbench.View == WorkbenchView.ItemGroups && itemGroupDraft is not null;
    public Guid? SelectedItemGroupId => itemGroupDraft is { IsNew: false } ? itemGroupDraft.GroupId : null;
    public string? SelectedItemGroupName => itemGroupDraft?.Name;
    public bool ItemGroupEditorHasUnsavedChanges =>
        itemGroupDraft is not null && ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft);
    public AgentBridgeCaptureRegion? AgentCaptureRegion { get; private set; }
    public bool PlanEditorHasUnsavedChanges
    {
        get
        {
            var snapshot = state.Snapshot();
            var owner = runtimeSnapshots.Current.Owner;
            return (restockDraft is not null && RestockPlanCatalog.HasChanges(snapshot, owner, restockDraft)) ||
                   (stowageDraft is not null && StowagePlanCatalog.HasChanges(snapshot, owner, stowageDraft));
        }
    }

    public Guid? SelectedStowagePlanId =>
        ResolveSelectedStowagePlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Id;

    public string? SelectedStowagePlanName =>
        ResolveSelectedStowagePlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Name;

    public int SelectedTransferDepositQuantity
    {
        get
        {
            var runtime = runtimeSnapshots.Current;
            var plan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
            return plan is null
                ? 0
                : StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, runtime.Owner, plan.Id)?.DepositQuantity ?? 0;
        }
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

        if (ActiveCapturePresentationTarget() is null)
            return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(16, 16), ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            Vector2.Min(new Vector2(1440, 900), viewport.WorkSize - new Vector2(32, 32)),
            ImGuiCond.Always);
        if (requestCaptureFocus)
        {
            ImGui.SetNextWindowFocus();
            requestCaptureFocus = false;
        }
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
        foreach (var target in new[] { "transfer", "transfer-review", "item-groups", "listings", "activity" })
            if (captureTransactions.ShouldPresentInMainViewport(target))
                return target;
        return null;
    }

    private void BeginCapturePresentation()
    {
        capturePreviousView = workbench.View;
        capturePreviousTransferReview = transferReview;
        capturePreviousTransferReviewOpenRequest = requestTransferReviewOpen;
        requestCaptureFocus = true;
        var target = ActiveCapturePresentationTarget();
        capturePresentingHistory = target == "activity";
        requestedView = target switch
        {
            "listings" => WorkbenchView.Listings,
            "item-groups" => WorkbenchView.ItemGroups,
            "activity" => WorkbenchView.Activity,
            _ => WorkbenchView.Stowage,
        };
        if (target == "transfer-review")
            RequestSelectedTransferReview();
    }

    private void RestoreCapturePresentation()
    {
        if (capturePreviousView is { } previous)
            requestedView = previous;
        capturePreviousView = null;
        transferReview = capturePreviousTransferReview;
        requestTransferReviewOpen = capturePreviousTransferReviewOpenRequest;
        capturePreviousTransferReview = null;
        capturePreviousTransferReviewOpenRequest = false;
        if (capturePresentingHistory)
            requestHistoryClose = true;
        capturePresentingHistory = false;
        requestCaptureFocus = false;
    }

    private void DrawContent()
    {
        var runtime = runtimeSnapshots.Current;
        workbench.EnsureScope(runtime.Browser);
        if (requestedView is { } requested)
        {
            if (requested == WorkbenchView.Activity)
            {
                requestHistoryOpen = true;
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
            requestHistoryOpen = true;
        reviewRegistry.RegisterLastButton(
            "quartermaster.history.open",
            "Open transfer history",
            true,
            () => requestHistoryOpen = true,
            "Recent Quartermaster operations");
        DrawHistoryPopup(runtime.Owner);

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
                DrawListings(runtime.Browser, runtime.Revision);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
        DrawStowageEditorModal(runtime);
        DrawTransferReviewModal();
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
                DrawItemGroupWorkspace(runtime);
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
        DrawStockToolbar(projection);
        var result = queries.QueryItems(
            projection,
            workbench.ItemFilterState.Expression,
            workbench.ScopeKey,
            workbench.ItemFilterState.IsInputActive,
            runtime.Revision);
        VisibleStockCount = result.Items.Count;
        if (!result.Filter.IsValid)
            ImGui.TextColored(
                new Vector4(1f, .65f, .25f, 1f),
                result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        if (stockSelectionRevision != runtime.Revision)
        {
            stockSelection.Retain(projection.Items.Select(item => item.ItemId));
            stockSelectionRevision = runtime.Revision;
        }
        var selectedPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
        var sourceRows = ResolveStockWorkbenchProjection(runtime, result.Items, selectedPlan);
        DrawStockSelectionBar(runtime);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        var tableHeight = Math.Max(180, ImGui.GetContentRegionAvail().Y);
        RenderedStockRowCount = 0;
        if (stockTable.Begin(
                "RQStockWorkbenchV3",
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

    private IReadOnlyList<StockWorkbenchRow> ResolveStockWorkbenchProjection(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<StockGroup> queryItems,
        StowagePlan? selectedPlan)
    {
        if (stockWorkbenchProjection is { } cached &&
            cached.RuntimeRevision == runtime.Revision &&
            cached.PlanId == selectedPlan?.Id &&
            ReferenceEquals(cached.QueryItems, queryItems))
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
        var rows = queryItems
            .Select(item =>
            {
                rules.TryGetValue(item.ItemId, out var rule);
                evaluated.TryGetValue(rule?.Id ?? Guid.Empty, out var line);
                return new StockWorkbenchRow(item, rule, line);
            })
            .ToArray();
        stockWorkbenchProjection = new(runtime.Revision, selectedPlan?.Id, queryItems, rows);
        stockProjectionBuildCount++;
        return rows;
    }

    private void DrawStockToolbar(BrowserProjection projection)
    {
        var sourceItems = projection.GetItems(workbench.ScopeKey);
        var context = BrowserQueryController.CreateItemContext(sourceItems, projection.Owner);
        var trailingWidth = 180f;
        DalamudFilterAutocompleteRenderer.Draw(
            "RQStockWorkbench",
            "Search accessible stock by item name",
            context,
            workbench.ItemFilterState,
            Math.Max(220, ImGui.GetContentRegionAvail().X - trailingWidth));
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
                    workbench.ScopeKey = scope.Key;
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
        if (ImGuiComponents.IconButton("QuartermasterRefresh", FontAwesomeIcon.BookOpen))
            autoRetainer.Start();
        reviewRegistry.RegisterLastButton(
            "quartermaster.refresh-retainers",
            "Refresh retainers",
            autoRetainer.IsAvailable,
            () => autoRetainer.Start(),
            autoRetainer.Status);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh all Quartermaster retainer inventory caches.");
    }

    private void DrawStockSelectionBar(QuartermasterRuntimeSnapshot runtime)
    {
        var selected = runtime.Browser.Items
            .Where(item => stockSelection.IsSelected(item.ItemId))
            .ToArray();
        if (selected.Length != stockSelection.Count)
        {
            stockSelection.Retain(runtime.Browser.Items.Select(item => item.ItemId));
            selected = runtime.Browser.Items
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
                var currentItems = current.Browser.Items
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
            stockSelection.Clear();
        if (selected.Length == 0)
            ImGui.EndDisabled();
    }

    private void DrawStockRowContextMenu(StockWorkbenchRow row)
    {
        var runtime = runtimeSnapshots.Current;
        var item = runtime.Browser.Items.FirstOrDefault(candidate => candidate.ItemId == row.Item.ItemId);
        if (item is null)
        {
            ImGui.BeginDisabled();
            ImGui.MenuItem("Item is no longer available");
            ImGui.EndDisabled();
            return;
        }

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
        EnsureItemGroupDraft(runtime.State);
        if (itemGroupDraft is null)
        {
            itemGroupDraft = ItemGroupCatalog.NewDraft(runtime.State);
            itemGroupEditorOrigin = WorkbenchView.ItemGroups;
        }
        if (itemGroupDraft is null)
            return;

        var added = ItemGroupCatalog.AddMissing(
            itemGroupDraft,
            items.Select(item => new ItemGroupItem
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quality = ItemQualityPolicy.Any,
            }));
        itemGroupWorkspaceStatus = added == 0
            ? "Selected items are already in this group."
            : $"Added {added:N0} selected {(added == 1 ? "item" : "items")}.";
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

    private static Vector4 TransferActionColor(StowageAction? action) => action switch
    {
        StowageAction.Retrieve => new Vector4(.52f, .79f, .94f, 1f),
        StowageAction.Deposit => new Vector4(.53f, .83f, .64f, 1f),
        _ => new Vector4(.69f, .74f, .77f, 1f),
    };

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
                         !autoRetainer.IsRefreshing &&
                         !autoRetainer.IsQueued;
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
        CloseItemGroupEditor();
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
        if (itemGroupDraft is not null && itemGroupEditorOrigin == WorkbenchView.Restock)
        {
            DrawItemGroupEditor(runtime);
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
            OpenItemGroupEditor(WorkbenchView.Restock);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.restock",
            "Open Item Groups from the Restock Plan editor",
            true,
            () => OpenItemGroupEditor(WorkbenchView.Restock),
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
            OpenNewItemGroupFromRestockSelection();
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

    private void DrawItemGroupWorkspace(QuartermasterRuntimeSnapshot runtime)
    {
        EnsureItemGroupDraft(runtime.State);
        var groups = ItemGroupCatalog.All(runtime.State);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "RQItemGroupWorkspace",
                2,
                flags,
                new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;

        ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQItemGroupList", Vector2.Zero, false))
        {
            ImGui.TextUnformatted("Item groups");
            ImGui.TextDisabled("Reusable item-name shorthands");
            ImGui.Separator();
            foreach (var group in groups)
            {
                var selected = itemGroupDraft?.GroupId == group.Id;
                if (ImGui.Selectable(
                        $"{group.Name}##group-workspace:{group.Id}",
                        selected,
                        ImGuiSelectableFlags.None,
                        new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 1.8f)))
                    RequestItemGroupSwitch(group.Id);
                if (ImGui.IsItemVisible())
                {
                    var cursor = ImGui.GetCursorPos();
                    ImGui.SetCursorPosY(cursor.Y - ImGui.GetTextLineHeightWithSpacing());
                    ImGui.Indent();
                    ImGui.TextDisabled($"{group.Items.Count:N0} items");
                    ImGui.Unindent();
                }
            }
            if (groups.Count == 0)
                ImGui.TextDisabled("No item groups yet.");
            ImGui.Separator();
            if (ImGui.Button("New item group", new Vector2(-1, 0)))
                OpenNewItemGroupWorkspace();
            reviewRegistry.RegisterLastButton(
                "quartermaster.item-groups.new",
                "Create a new Item Group draft",
                true,
                OpenNewItemGroupWorkspace,
                "Nothing is saved until Save group");
        }
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQItemGroupDetail", Vector2.Zero, false))
        {
            var draft = itemGroupDraft;
            if (draft is null)
            {
                ImGui.TextUnformatted("Choose an Item Group or create a new one.");
                ImGui.TextDisabled("Selecting stock on the left can then add several items at once.");
            }
            else
            {
                DrawItemGroupWorkspaceDetail(draft, runtime);
            }
        }
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void OpenNewItemGroupWorkspace()
    {
        itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot());
        itemGroupEditorOrigin = WorkbenchView.ItemGroups;
        selectedItemGroupItems.Clear();
        itemGroupItemSearch = string.Empty;
        itemGroupWorkspaceStatus = string.Empty;
    }

    private void DiscardItemGroupWorkspace()
    {
        if (itemGroupDraft is not { } draft)
            return;
        var snapshot = state.Snapshot();
        if (draft.IsNew)
        {
            itemGroupDraft = ItemGroupCatalog.All(snapshot).FirstOrDefault() is { } first
                ? ItemGroupCatalog.Draft(snapshot, first.Id)
                : null;
            selectedItemGroupId = itemGroupDraft?.GroupId;
            selectedItemGroupItems.Clear();
            itemGroupItemSearch = string.Empty;
        }
        else
        {
            LoadItemGroupDraft(draft.GroupId);
        }
        itemGroupWorkspaceStatus = string.Empty;
        itemGroupEditorError = string.Empty;
    }

    private void EnsureItemGroupDraft(QuartermasterState snapshot)
    {
        if (itemGroupDraft is not null)
            return;
        var groups = ItemGroupCatalog.All(snapshot);
        var selected = selectedItemGroupId is { } selectedId
            ? groups.FirstOrDefault(group => group.Id == selectedId)
            : groups.FirstOrDefault();
        if (selected is null)
            return;
        itemGroupDraft = ItemGroupCatalog.Draft(snapshot, selected.Id);
        itemGroupEditorOrigin = WorkbenchView.ItemGroups;
        selectedItemGroupId = selected.Id;
    }

    private void RequestItemGroupSwitch(Guid groupId)
    {
        if (itemGroupDraft?.GroupId == groupId)
            return;
        if (itemGroupDraft is not null &&
            ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft))
        {
            confirmationDialog.Request(
                $"switch-item-group:{groupId}",
                "Discard unsaved Item Group changes?",
                "The selected Item Group will open and the current draft will be discarded.",
                "Discard and switch",
                () => LoadItemGroupDraft(groupId));
            return;
        }
        LoadItemGroupDraft(groupId);
    }

    private void LoadItemGroupDraft(Guid groupId)
    {
        itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        itemGroupEditorOrigin = WorkbenchView.ItemGroups;
        selectedItemGroupId = groupId;
        selectedItemGroupItems.Clear();
        itemGroupItemSearch = string.Empty;
        itemGroupWorkspaceStatus = string.Empty;
        itemGroupEditorError = string.Empty;
    }

    private void DrawItemGroupWorkspaceDetail(
        ItemGroupDraft draft,
        QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 150));
        var name = draft.Name;
        if (ImGui.InputText("##item-group-name", ref name, 80))
            draft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{draft.Items.Count:N0} items");
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##item-group-workspace"))
            RequestDeleteItemGroupWorkspace(draft);

        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 140));
        if (ImGui.InputTextWithHint(
                "##item-group-add",
                "Add an item by name",
                ref itemGroupItemSearch,
                120))
            selectedItemGroupChoice = null;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("##item-group-add-quality", QualityChoiceLabel(itemGroupAddQuality)))
        {
            foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            {
                if (ImGui.Selectable(QualityChoiceLabel(quality), itemGroupAddQuality == quality))
                    itemGroupAddQuality = quality;
            }
            ImGui.EndCombo();
        }

        var matches = SearchItems(itemGroupItemSearch, 6);
        if (!string.IsNullOrWhiteSpace(itemGroupItemSearch) && matches.Count > 0)
        {
            if (ImGui.BeginChild(
                    "RQItemGroupSearchResults",
                    new Vector2(0, Math.Min(150, matches.Count * ImGui.GetTextLineHeightWithSpacing() + 8)),
                    true))
            {
                foreach (var choice in matches)
                {
                    if (!ImGui.Selectable($"{choice.Label}##group-add:{choice.ItemId}"))
                        continue;
                    var added = ItemGroupCatalog.AddMissing(
                        draft,
                        [new ItemGroupItem
                        {
                            ItemId = choice.ItemId,
                            ItemName = choice.Name,
                            Quality = itemGroupAddQuality,
                        }]);
                    itemGroupWorkspaceStatus = added == 0
                        ? $"{choice.Name} is already in this group."
                        : $"Added {choice.Name}.";
                    itemGroupItemSearch = string.Empty;
                    selectedItemGroupChoice = null;
                    break;
                }
            }
            ImGui.EndChild();
        }

        ImGui.Separator();
        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2) + 8;
        if (itemGroupWorkspaceTable.Begin(
                "RQItemGroupMembersWorkbench",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(130, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            foreach (var member in draft.Items.ToArray())
                itemGroupWorkspaceTable.DrawRow(
                    member,
                    id: $"item-group-workspace:{member.ItemId}:{member.GetHashCode()}");
            itemGroupWorkspaceTable.End();
        }

        if (!string.IsNullOrWhiteSpace(itemGroupEditorError))
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), itemGroupEditorError);
        else if (!string.IsNullOrWhiteSpace(itemGroupWorkspaceStatus))
            ImGui.TextDisabled(itemGroupWorkspaceStatus);
        else
            ImGui.TextDisabled("Select stock on the left to add several items at once.");

        var snapshot = state.Snapshot();
        var canApply = ItemGroupCatalog.CanApply(snapshot, draft);
        var hasChanges = ItemGroupCatalog.HasChanges(snapshot, draft);
        ImGui.SameLine();
        if (!hasChanges)
            ImGui.BeginDisabled();
        if (ImGui.Button("Discard"))
            DiscardItemGroupWorkspace();
        if (!hasChanges)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.discard",
            "Discard changes to the current Item Group",
            hasChanges,
            DiscardItemGroupWorkspace,
            hasChanges ? "Unsaved changes will be discarded" : "No unsaved changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save group"))
            SaveItemGroupWorkspace(draft);
        if (!canApply)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the current Item Group",
            canApply,
            () =>
            {
                if (itemGroupDraft is not null)
                    SaveItemGroupWorkspace(itemGroupDraft);
            },
            canApply ? "Valid changes" : "No valid changes");
    }

    private void SaveItemGroupWorkspace(ItemGroupDraft draft)
    {
        try
        {
            var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, draft).Id);
            LoadItemGroupDraft(groupId);
            itemGroupWorkspaceStatus = "Item Group saved.";
        }
        catch (Exception exception)
        {
            itemGroupEditorError = exception.Message;
        }
    }

    private void RequestDeleteItemGroupWorkspace(ItemGroupDraft draft)
    {
        if (draft.IsNew)
        {
            itemGroupDraft = ItemGroupCatalog.All(state.Snapshot()).FirstOrDefault() is { } first
                ? ItemGroupCatalog.Draft(state.Snapshot(), first.Id)
                : null;
            return;
        }
        confirmationDialog.Request(
            $"delete-item-group:{draft.GroupId}",
            $"Delete \"{draft.Name}\"?",
            "Existing Transfer Plan items will not be changed.",
            "Delete group",
            () =>
            {
                state.Mutate(document =>
                    ItemGroupCatalog.Delete(document, draft.GroupId, draft.SourceRevision));
                itemGroupDraft = null;
                selectedItemGroupId = null;
                EnsureItemGroupDraft(state.Snapshot());
            });
    }

    private void OpenItemGroupEditor(WorkbenchView origin)
    {
        itemGroupEditorOrigin = origin;
        var snapshot = state.Snapshot();
        var preferredId = origin == WorkbenchView.Restock
            ? selectedRestockItemGroupId
            : selectedItemGroupId;
        var group = snapshot.ItemGroups.FirstOrDefault(candidate => candidate.Id == preferredId)
                    ?? ItemGroupCatalog.All(snapshot).FirstOrDefault();
        itemGroupDraft = group is null
            ? ItemGroupCatalog.NewDraft(snapshot)
            : ItemGroupCatalog.Draft(snapshot, group.Id);
        ResetItemGroupEditorInput();
    }

    private void OpenNewItemGroupFromRestockSelection()
    {
        if (restockDraft is null)
            return;
        var selected = restockDraft.Items
            .Where(item => selectedRestockItemIds.IsSelected(item.Id))
            .ToArray();
        itemGroupEditorOrigin = WorkbenchView.Restock;
        itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", selected);
        ResetItemGroupEditorInput();
    }

    private void OpenNewItemGroupFromStowageSelection()
    {
        if (stowageDraft is null)
            return;
        var selected = stowageDraft.Rules
            .Where(rule => selectedStowageRuleIds.IsSelected(rule.Id))
            .ToArray();
        itemGroupEditorOrigin = WorkbenchView.Stowage;
        itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", selected);
        ResetItemGroupEditorInput();
    }

    private void ResetItemGroupEditorInput()
    {
        selectedItemGroupItems.Clear();
        itemGroupFilter = string.Empty;
        itemGroupItemSearch = string.Empty;
        selectedItemGroupChoice = null;
        itemGroupAddQuality = ItemQualityPolicy.Any;
        itemGroupEditorError = string.Empty;
        requestDeleteItemGroup = false;
    }

    private void CloseItemGroupEditor()
    {
        itemGroupDraft = null;
        itemGroupEditorOrigin = null;
        selectedItemGroupItems.Clear();
        selectedItemGroupChoice = null;
        itemGroupEditorError = string.Empty;
        requestDeleteItemGroup = false;
    }

    private void DrawItemGroupEditor(QuartermasterRuntimeSnapshot runtime)
    {
        var draft = itemGroupDraft;
        if (draft is null)
            return;
        var hasChanges = ItemGroupCatalog.HasChanges(state.Snapshot(), draft);

        if (ImGui.Button("<- Back to plan##itemgroups"))
        {
            CloseItemGroupEditor();
            return;
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.back",
            "Discard the open Item Group draft and return to the Transfer Plan",
            true,
            CloseItemGroupEditor,
            hasChanges ? "Unsaved Item Group changes will be discarded" : "The Transfer Plan draft remains open");
        ImGui.SameLine();
        ImGui.TextUnformatted("Item groups");
        ImGui.SameLine();
        ImGui.TextDisabled("Reusable across Transfer Plans");
        if (hasChanges)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "Returning to the plan will discard unsaved Item Group changes.");

        if (!string.IsNullOrWhiteSpace(itemGroupEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), itemGroupEditorError);

        var bodyHeight = Math.Max(260, ImGui.GetContentRegionAvail().Y - 42);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQItemGroupEditor", 2, flags, new Vector2(0, bodyHeight)))
        {
            ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 290);
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupList", Vector2.Zero, false))
                DrawItemGroupList(draft);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupMembers", Vector2.Zero, false))
                DrawItemGroupMembers(draft, runtime);
            ImGui.EndChild();
            ImGui.EndTable();
        }

        var snapshot = state.Snapshot();
        var canApply = ItemGroupCatalog.CanApply(snapshot, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Items.Count == 0
            ? "Add at least one item to save this group."
            : canApply ? "Unsaved changes - groups remember item and quality; plans own quantities and routing." : "No unsaved changes.");
        var saveButtonWidth = ImGui.CalcTextSize("Save group").X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(Math.Max(
            ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X,
            ImGui.GetWindowContentRegionMax().X - saveButtonWidth));
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save group##itemgroupeditor"))
        {
            try
            {
                var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, draft).Id);
                if (itemGroupEditorOrigin == WorkbenchView.Restock)
                    selectedRestockItemGroupId = groupId;
                else
                    selectedItemGroupId = groupId;
                itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
            }
            catch (InvalidOperationException exception)
            {
                itemGroupEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the open Item Group draft",
            canApply,
            () =>
            {
                if (itemGroupDraft is null)
                    return;
                var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, itemGroupDraft).Id);
                if (itemGroupEditorOrigin == WorkbenchView.Restock)
                    selectedRestockItemGroupId = groupId;
                else
                    selectedItemGroupId = groupId;
                itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
            },
            canApply ? "Changes are saved together" : "No valid changes");

        DrawDeleteItemGroupPopup();
    }

    private void DrawItemGroupList(ItemGroupDraft draft)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##itemgroupfilter", "Filter groups", ref itemGroupFilter, 80);
        ImGui.Separator();
        var groups = ItemGroupCatalog.All(state.Snapshot())
            .Where(group => itemGroupFilter.Trim().Length == 0 ||
                            group.Name.Contains(itemGroupFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            group.Items.Any(item => item.ItemName.Contains(itemGroupFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var group in groups)
        {
            var preview = string.Join(", ", group.Items.Select(item => item.ItemName).Take(3));
            if (group.Items.Count > 3)
                preview += "...";
            var selected = !draft.IsNew && draft.GroupId == group.Id;
            if (ImGui.Selectable(
                    $"@{group.Name}  ({group.Items.Count:N0})##itemgroup{group.Id}",
                    selected,
                    ImGuiSelectableFlags.AllowDoubleClick))
                TrySwitchItemGroup(group.Id);
            var groupId = group.Id;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.item-groups.select.{group.Id}",
                $"Edit Item Group {group.Name}",
                true,
                () => TrySwitchItemGroup(groupId),
                selected ? "Selected" : $"{group.Items.Count:N0} items");
            if (!string.IsNullOrWhiteSpace(preview))
                ImGui.TextDisabled(preview);
        }
        ImGui.Separator();
        if (ImGui.Button("New item group", new Vector2(-1, 0)))
        {
            if (ItemGroupCatalog.HasChanges(state.Snapshot(), draft))
                itemGroupEditorError = "Save or discard this Item Group before creating another.";
            else
            {
                itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot());
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
            }
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.new",
            "Open a new Item Group draft",
            true,
            () =>
            {
                if (itemGroupDraft is not null &&
                    !ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft))
                    itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot());
            },
            "Nothing is saved until Save group");
    }

    private void TrySwitchItemGroup(Guid groupId)
    {
        if (itemGroupDraft is null || itemGroupDraft.GroupId == groupId)
            return;
        if (ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft))
        {
            itemGroupEditorError = "Save or discard this Item Group before switching.";
            return;
        }
        itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        selectedItemGroupItems.Clear();
        itemGroupItemSearch = string.Empty;
        selectedItemGroupChoice = null;
        itemGroupEditorError = string.Empty;
    }

    private void DrawItemGroupMembers(ItemGroupDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.TextDisabled("Name");
        ImGui.SameLine();
        var name = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##itemgroupname", ref name, 80))
            draft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{draft.Items.Count:N0} items");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100);
        if (draft.IsNew)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete group##itemgroup"))
            requestDeleteItemGroup = true;
        if (draft.IsNew)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Add member");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##itemgroupitemsearch", "Search by item name", ref itemGroupItemSearch, 96))
            selectedItemGroupChoice = null;
        if (itemGroupItemSearch.Trim().Length >= 2 && selectedItemGroupChoice is null)
        {
            foreach (var match in SearchItems(itemGroupItemSearch, 5))
                if (ImGui.Selectable(
                        $"{match.Label}##itemgroupchoice{match.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedItemGroupChoice = match;
                    itemGroupItemSearch = match.Name;
                }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##itemgroupaddquality", QualityChoiceLabel(itemGroupAddQuality)))
        {
            foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
                if (ImGui.Selectable(QualityChoiceLabel(quality), itemGroupAddQuality == quality))
                    itemGroupAddQuality = quality;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var canAddItem = selectedItemGroupChoice is not null;
        if (!canAddItem)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add item##itemgroup") && selectedItemGroupChoice is { } choice)
        {
            ItemGroupCatalog.AddMissing(draft,
            [
                new ItemGroupItem
                {
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    Quality = itemGroupAddQuality,
                },
            ]);
            selectedItemGroupChoice = null;
            itemGroupItemSearch = string.Empty;
        }
        if (!canAddItem)
            ImGui.EndDisabled();

        var planSelectionCount = ItemGroupPlanSelectionCount();
        ImGui.SameLine();
        if (planSelectionCount == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Add {planSelectionCount:N0} selected from plan##itemgroup"))
            AddPlanSelectionToItemGroup(draft);
        if (planSelectionCount == 0)
            ImGui.EndDisabled();

        var hasSelectedMembers = selectedItemGroupItems.Count > 0;
        if (!hasSelectedMembers)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Remove selected ({selectedItemGroupItems.Count:N0})##itemgroup"))
        {
            draft.Items.RemoveAll(selectedItemGroupItems.IsSelected);
            selectedItemGroupItems.Clear();
        }
        if (!hasSelectedMembers)
            ImGui.EndDisabled();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!itemGroupEditorTable.Begin(
                "RQItemGroupMembersTable",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(180, ImGui.GetContentRegionAvail().Y)),
                    flags)))
            return;
        var items = draft.Items.ToArray();
        selectedItemGroupItems.Retain(items);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            itemGroupEditorTable.DrawSelectableRow(
                item,
                selectedItemGroupItems,
                items,
                index,
                $"##selectgroupitem:{item.ItemId}:{item.GetHashCode()}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedItemGroupItems);
        itemGroupEditorTable.End();
    }

    private int ItemGroupPlanSelectionCount() =>
        itemGroupEditorOrigin switch
        {
            WorkbenchView.Restock when restockDraft is not null =>
                restockDraft.Items.Count(item => selectedRestockItemIds.IsSelected(item.Id)),
            WorkbenchView.Stowage when stowageDraft is not null =>
                stowageDraft.Rules.Count(rule => selectedStowageRuleIds.IsSelected(rule.Id)),
            _ => 0,
        };

    private void AddPlanSelectionToItemGroup(ItemGroupDraft draft)
    {
        if (itemGroupEditorOrigin == WorkbenchView.Restock && restockDraft is not null)
            ItemGroupCatalog.AddMissing(
                draft,
                restockDraft.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)));
        else if (itemGroupEditorOrigin == WorkbenchView.Stowage && stowageDraft is not null)
            ItemGroupCatalog.AddMissing(
                draft,
                stowageDraft.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)));
    }

    private void DrawDeleteItemGroupPopup()
    {
        if (requestDeleteItemGroup)
        {
            ImGui.OpenPopup("Delete Item Group##RQ");
            requestDeleteItemGroup = false;
        }
        if (!ImGui.BeginPopupModal("Delete Item Group##RQ", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var draft = itemGroupDraft;
        if (draft is null || draft.IsNew)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted($"Delete \"@{draft.Name}\"?");
        ImGui.TextDisabled("Existing plan items are not changed.");
        if (ImGui.Button("Delete##itemgroupconfirm"))
        {
            try
            {
                state.Mutate(document => ItemGroupCatalog.Delete(document, draft.GroupId, draft.SourceRevision));
                if (selectedRestockItemGroupId == draft.GroupId)
                    selectedRestockItemGroupId = null;
                if (selectedItemGroupId == draft.GroupId)
                    selectedItemGroupId = null;
                var next = ItemGroupCatalog.All(state.Snapshot()).FirstOrDefault();
                itemGroupDraft = next is null
                    ? ItemGroupCatalog.NewDraft(state.Snapshot())
                    : ItemGroupCatalog.Draft(state.Snapshot(), next.Id);
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                itemGroupEditorError = exception.Message;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##itemgroupdelete"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
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

    private static void DrawItemGroupQuality(ItemGroupItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##group-quality:{item.ItemId}:{item.GetHashCode()}",
                QualityChoiceLabel(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private void DrawItemGroupWorkspaceRemove(ItemGroupItem item)
    {
        if (ImGui.SmallButton($"Remove##group-member:{item.ItemId}:{item.GetHashCode()}"))
            itemGroupDraft?.Items.Remove(item);
    }

    private void DrawItemGroupEditorRemove(ItemGroupItem item)
    {
        if (!ImGui.SmallButton($"X##removegroupitem{item.GetHashCode()}"))
            return;
        itemGroupDraft?.Items.Remove(item);
        selectedItemGroupItems.SetSelected(item, false);
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

    private static string SignedQuantity(int quantity) =>
        quantity > 0
            ? $"+{quantity:N0}"
            : quantity.ToString("N0", CultureInfo.CurrentCulture);

    private void DrawTransferTarget(TransferWorkbenchRow row)
    {
        ImGui.SetNextItemWidth(-1);
        var target = row.Rule.TargetQuantity;
        if (ImGui.InputInt($"##target:{row.Rule.Id}", ref target, 0))
            UpdateTransferRule(row.Owner, row.PlanId, row.Rule.Id, draftRule =>
                draftRule.TargetQuantity = Math.Max(0, target));
    }

    private static string TransferOutcome(TransferWorkbenchRow row) =>
        !row.Rule.Enabled
            ? "Off"
            : row.Line?.Action switch
            {
                StowageAction.Retrieve => $"Retrieve {row.Line.RetrieveQuantity:N0}",
                StowageAction.Deposit => $"Stow {row.Line.DepositQuantity:N0}",
                _ => "On target",
            };

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

    private static string TransferReviewOutcome(TransferReviewRow row) =>
        row.Line?.Action switch
        {
            StowageAction.Retrieve => $"Retrieve {row.Line.RetrieveQuantity:N0}",
            StowageAction.Deposit => $"Stow {row.Line.DepositQuantity:N0} · {RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner)}",
            _ => "On target · skip",
        };

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
                    document.StowagePlans.RemoveAll(candidate => candidate.Id == planId && candidate.Owner.Matches(owner));
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
        if (itemGroupEditorOrigin == WorkbenchView.Restock)
            CloseItemGroupEditor();
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
        if (itemGroupEditorOrigin == WorkbenchView.Stowage)
            CloseItemGroupEditor();
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
            ImGui.Spacing();
            ImGui.TextUnformatted("No Transfer Plans yet.");
            ImGui.TextDisabled("Create one, then select stock on the left or add items by name.");
            return;
        }

        var projection = ResolveTransferWorkbenchProjection(runtime, selected);
        var ownerRules = projection.Rules;
        var retrieval = projection.Retrieval;
        var surplusBatch = projection.Deposit;
        var movements = projection.Movements;
        var hasMovement = projection.HasMovement;
        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement,
            owner.HasStableIdentity,
            transfers.CanStart,
            autoRetainer.IsRefreshing || autoRetainer.IsQueued);
        var canExecute = availability.CanExecute;

        ImGui.SameLine();
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button("Execute plan"))
        {
            transferReview = new(selected.Id, selected.Name);
            requestTransferReviewOpen = true;
        }
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
                {
                    transferReview = new(currentPlan.Id, currentPlan.Name);
                    requestTransferReviewOpen = true;
                }
            },
            canExecute ? $"{movements:N0} movements" : availability.BlockReason);

        ImGui.Separator();
        ImGui.TextUnformatted($"{ownerRules.Count:N0} items");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{movements:N0} movements");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.52f, .79f, .94f, 1f), $"Retrieve {retrieval.NeededQuantity:N0}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.53f, .83f, .64f, 1f), $"Stow {surplusBatch.PlannedQuantity:N0}");
        if (!string.IsNullOrWhiteSpace(inlineTransferError))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), inlineTransferError);
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 4;
        var transferRows = projection.Rows;
        RenderedTransferRowCount = 0;
        if (transferWorkbenchTable.Begin(
                "RQTransferWorkbench",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    flags)))
        {
            RenderedTransferRowCount = transferWorkbenchTable.DrawClippedRows(
                transferRows,
                (row, _) =>
                {
                    transferWorkbenchTable.DrawRow(
                        row,
                        row.Rule.Enabled ? null : new Vector4(.38f, .12f, .14f, .42f),
                        id: $"transfer:{row.Rule.Id}");
                    if (row.Line?.Action == StowageAction.Retrieve && row.RetrievalLine?.MissingQuantity > 0)
                        inlineTransferError = $"{row.Rule.ItemName}: {row.RetrievalLine.MissingQuantity:N0} missing from known retainer stock.";
                });
            transferWorkbenchTable.End();
        }

        ImGui.TextDisabled(
            availability.BlockReason ??
            "Balanced items stay visible and are skipped during execution.");
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
        var stowage = runtime.Stowage.FirstOrDefault(candidate => candidate.PlanId == plan.Id);
        var retrieval = BuildTransferRetrievalEvaluation(runtime, rules);
        var deposit = BuildSurplusBatch(runtime, stowage);
        var evaluated = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var retrievalLines = retrieval.Lines.ToDictionary(line => line.PlanItemId);
        var movements = evaluated.Values.Count(line =>
            line.Action is StowageAction.Retrieve or StowageAction.Deposit);
        var rows = rules
            .Select(rule =>
            {
                evaluated.TryGetValue(rule.Id, out var line);
                retrievalLines.TryGetValue(rule.Id, out var retrievalLine);
                var playerQuantity = StowageEvaluator.PlayerQuantity(
                    rule,
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId));
                return new TransferWorkbenchRow(
                    rule,
                    line,
                    retrievalLine,
                    playerQuantity,
                    rule.TargetQuantity - playerQuantity,
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
            deposit,
            movements,
            retrieval.NeededQuantity > 0 || deposit.PlannedQuantity > 0,
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
        transferReview = new(plan.Id, plan.Name);
        requestTransferReviewOpen = true;
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
                RouteSummary(rule.Routing, runtime.Retainers, owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
        {
            if (ImGui.Selectable(RoutingModeLabel(mode), rule.Routing.Mode == mode))
                UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    draftRule.Routing.Mode = mode);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Fallback");
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
        {
            if (ImGui.Selectable(OverflowLabel(overflow), rule.Routing.Overflow == overflow))
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
        CloseItemGroupEditor();
        stowageDraft = draft;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = stowageDraft.Rules.FirstOrDefault()?.Id;
        selectedItemGroupId = null;
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
        if (itemGroupDraft is not null && itemGroupEditorOrigin == WorkbenchView.Stowage)
        {
            DrawItemGroupEditor(runtime);
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
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##stowagegroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##group{group.Id}", selectedGroup?.Id == group.Id))
                    selectedItemGroupId = group.Id;
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
            OpenItemGroupEditor(WorkbenchView.Stowage);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.transfer",
            "Open Item Groups from the Transfer Plan editor",
            true,
            () => OpenItemGroupEditor(WorkbenchView.Stowage),
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
            OpenNewItemGroupFromStowageSelection();
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
        DrawOptionalEnumCombo("##stowagebulkdestination", ref bulkRoutingMode, Enum.GetValues<StowageRoutingMode>(), RoutingModeLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Overflow");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkoverflow", ref bulkOverflow, Enum.GetValues<StowageOverflowPolicy>(), OverflowLabel);
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
                RouteSummary(rule.Routing, runtime.Retainers, runtime.Owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
            if (ImGui.Selectable(RoutingModeLabel(mode), rule.Routing.Mode == mode))
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
        if (!ImGui.BeginCombo($"##draftoverflow{rule.Id}", OverflowLabel(rule.Routing.Overflow)))
            return;
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
            if (ImGui.Selectable(OverflowLabel(overflow), rule.Routing.Overflow == overflow))
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

    private void DrawListings(BrowserProjection projection, long revision)
    {
        var sourceListings = projection.GetListings(workbench.ScopeKey);
        var context = BrowserQueryController.CreateListingContext(sourceListings, projection.Owner);
        DalamudFilterAutocompleteRenderer.Draw(
            "RQListingsWorkbench",
            "Search listed items",
            context,
            workbench.ListingFilterState,
            Math.Max(240, ImGui.GetContentRegionAvail().X - 200));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        var selectedScope = projection.Scopes.First(scope => scope.Key == workbench.ScopeKey);
        if (ImGui.BeginCombo("##RQListingScope", selectedScope.Label))
        {
            foreach (var scope in projection.Scopes)
            {
                if (ImGui.Selectable($"{scope.Label}##listing-scope:{scope.Key}", scope.Key == workbench.ScopeKey))
                    workbench.ScopeKey = scope.Key;
            }
            ImGui.EndCombo();
        }

        var result = queries.QueryListings(
            projection,
            workbench.ListingFilterState.Expression,
            workbench.ScopeKey,
            workbench.ListingFilterState.IsInputActive,
            revision);
        if (!result.Filter.IsValid)
            ImGui.TextColored(
                new Vector4(1f, .65f, .25f, 1f),
                result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        var groups = result.Listings
            .GroupBy(listing => (listing.ItemId, listing.ItemName))
            .Select(group => new ListingGroupView(
                group.Key.ItemId,
                group.Key.ItemName,
                group.Sum(listing => listing.Quantity),
                group.Select(listing => listing.RetainerId).Distinct().Count(),
                projection.GetUnlistedRetainerQuantity(group.Key.ItemId, workbench.ScopeKey),
                group.OrderBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(group => group.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length == 0)
        {
            ImGui.TextDisabled("No listings match this view.");
            var navigationTarget = ResolveEmptyListingNavigationTarget(projection);
            if (navigationTarget is not null)
            {
                if (listingNavigation.IsRunning)
                    ImGui.BeginDisabled();
                if (ImGui.Button($"Open {navigationTarget.RetainerName}'s listings"))
                    _ = listingNavigation.OpenRetainerListingsAsync(navigationTarget);
                if (listingNavigation.IsRunning)
                    ImGui.EndDisabled();
                reviewRegistry.RegisterLastButton(
                    "quartermaster.listings.open-first",
                    $"Open {navigationTarget.RetainerName}'s listings",
                    !listingNavigation.IsRunning,
                    () =>
                    {
                        if (ResolveEmptyListingNavigationTarget(projection) is { } target)
                            _ = listingNavigation.OpenRetainerListingsAsync(target);
                    },
                    listingNavigation.IsRunning ? listingNavigation.Status : "Ready");
            }
            DrawListingNavigationStatus(showRecovery: true);
            return;
        }

        if (workbench.SelectedListingItemId is not { } selectedItemId ||
            groups.All(group => group.ItemId != selectedItemId))
            workbench.SelectedListingItemId = groups[0].ItemId;
        var selected = groups.Single(group => group.ItemId == workbench.SelectedListingItemId);
        listingGroupSelection.Retain(groups.Select(group => group.ItemId));
        listingGroupSelection.SelectOnly(selected.ItemId);
        var listingKeys = selected.Listings.Select(ListingRowKey.From).ToArray();
        physicalListingSelection.Retain(listingKeys);
        if (focusedListing is not { } focusedKey || !listingKeys.Contains(focusedKey))
            focusedListing = listingKeys.Length > 0 ? listingKeys[0] : null;

        if (!ImGui.BeginTable(
                "RQListingsWorkbench",
                2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthStretch, .9f);
        ImGui.TableSetupColumn("Listing detail", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQListingGroups", Vector2.Zero, false))
        {
            if (listingGroupTable.Begin("RQListingGroupRowsV2", ImGui.GetContentRegionAvail().Y))
            {
                listingGroupTable.DrawFilterRow();
                var visibleGroups = listingGroupTable.Apply(groups, ImGui.TableGetSortSpecs());
                var groupKeys = visibleGroups.Select(group => group.ItemId).ToArray();
                for (var groupIndex = 0; groupIndex < visibleGroups.Count; groupIndex++)
                {
                    var group = visibleGroups[groupIndex];
                    if (listingGroupTable.DrawSelectableRow(
                            group,
                            listingGroupSelection,
                            groupKeys,
                            groupIndex,
                            $"##listing-group:{group.ItemId}"))
                    {
                        listingGroupSelection.SelectOnly(group.ItemId);
                        workbench.SelectedListingItemId = group.ItemId;
                        physicalListingSelection.Clear();
                        focusedListing = null;
                    }
                }
                DalamudTableSelectionRenderer.EndRows(listingGroupSelection);
                listingGroupTable.End();
            }
        }
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQListingDetail", Vector2.Zero, false))
        {
            ImGui.TextUnformatted(selected.ItemName);
            var navigationTarget = ResolveListingNavigationTarget(selected.Listings);
            var canOpenListings = navigationTarget is not null && !listingNavigation.IsRunning;
            var openButtonWidth = ImGui.CalcTextSize("Open retainer listings").X +
                                  ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SameLine(Math.Max(
                ImGui.GetCursorPosX(),
                ImGui.GetWindowContentRegionMax().X - openButtonWidth));
            if (!canOpenListings)
                ImGui.BeginDisabled();
            if (ImGui.Button("Open retainer listings") && navigationTarget is not null)
                OpenRetainerListings(navigationTarget);
            if (!canOpenListings)
                ImGui.EndDisabled();
            reviewRegistry.RegisterLastButton(
                "quartermaster.listings.open-first",
                navigationTarget is null
                    ? "Open retainer listings"
                    : $"Open {navigationTarget.RetainerName}'s listings",
                canOpenListings,
                () =>
                {
                    if (ResolveListingNavigationTarget(selected.Listings) is { } target)
                        OpenRetainerListings(target);
                },
                listingNavigation.IsRunning ? listingNavigation.Status : "Ready");
            ImGui.TextDisabled(
                $"{selected.Listings.Count:N0} physical {(selected.Listings.Count == 1 ? "listing" : "listings")} across {selected.RetainerCount:N0} {(selected.RetainerCount == 1 ? "retainer" : "retainers")}");
            ImGui.Separator();
            ImGui.TextDisabled("Total listed");
            ImGui.SameLine();
            ImGui.TextUnformatted(selected.Quantity.ToString("N0"));
            if (selected.HasKnownPrice)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("Lowest");
                ImGui.SameLine();
                ImGui.TextUnformatted($"{selected.LowestPrice:N0} gil");
                ImGui.SameLine();
                ImGui.TextDisabled("Highest");
                ImGui.SameLine();
                ImGui.TextUnformatted($"{selected.HighestPrice:N0} gil");
            }
            ImGui.Separator();
            if (physicalListingSelection.Count > 0)
            {
                ImGui.TextDisabled(
                    $"{physicalListingSelection.Count:N0} selected · Repricing actions will operate on this selection.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear selection"))
                {
                    physicalListingSelection.Clear();
                    focusedListing = null;
                }
            }
            var detailHeight = Math.Max(
                120,
                ImGui.GetContentRegionAvail().Y -
                (!string.IsNullOrWhiteSpace(listingNavigation.Status)
                    ? ImGui.GetTextLineHeightWithSpacing()
                    : 0));
            if (physicalListingTable.Begin("RQPhysicalListings", detailHeight))
            {
                physicalListingTable.DrawFilterRow();
                var visibleListings = physicalListingTable.Apply(
                    selected.Listings,
                    ImGui.TableGetSortSpecs());
                var visibleKeys = visibleListings.Select(ListingRowKey.From).ToArray();
                for (var listingIndex = 0; listingIndex < visibleListings.Count; listingIndex++)
                {
                    var listing = visibleListings[listingIndex];
                    if (physicalListingTable.DrawSelectableRow(
                            listing,
                            physicalListingSelection,
                            visibleKeys,
                            listingIndex,
                            $"##physical-listing:{listing.RetainerId}:{listing.SlotIndex}:{listing.ItemId}"))
                        focusedListing = ListingRowKey.From(listing);
                }
                DalamudTableSelectionRenderer.EndRows(physicalListingSelection);
                physicalListingTable.End();
            }
            DrawListingNavigationStatus();
        }
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private ListingRow? ResolveListingNavigationTarget(IReadOnlyList<ListingRow> listings)
    {
        if (focusedListing is { } key)
        {
            var focused = listings.FirstOrDefault(listing => ListingRowKey.From(listing) == key);
            if (focused is not null)
                return focused;
        }

        var selected = listings.FirstOrDefault(
            listing => physicalListingSelection.IsSelected(ListingRowKey.From(listing)));
        return selected ?? listings.FirstOrDefault();
    }

    private RetainerListingsOpenRequest? ResolveEmptyListingNavigationTarget(BrowserProjection projection)
    {
        var scope = projection.Scopes.FirstOrDefault(
                        candidate => candidate.Kind == BrowserScopeKind.Retainer &&
                                     candidate.Key == workbench.ScopeKey)
                    ?? projection.Scopes.FirstOrDefault(
                        candidate => candidate.Kind == BrowserScopeKind.Retainer);
        return scope?.RetainerId is { } retainerId
            ? new RetainerListingsOpenRequest(retainerId, scope.Label)
            : null;
    }

    private void OpenRetainerListings(ListingRow listing) =>
        _ = listingNavigation.OpenRetainerListingsAsync(
            new(listing.RetainerId, listing.RetainerName));

    private void DrawListingNavigationStatus(bool showRecovery = false)
    {
        if (!string.IsNullOrWhiteSpace(listingNavigation.Status))
            ImGui.TextDisabled(listingNavigation.Status);
        if (!showRecovery &&
            !listingNavigation.Status.StartsWith("Opened ", StringComparison.Ordinal))
            return;

        if (!string.IsNullOrWhiteSpace(listingNavigation.Status))
            ImGui.SameLine();
        if (listingNavigation.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Return to retainer list"))
            _ = listingNavigation.ReturnToRetainerListAsync();
        if (listingNavigation.IsRunning)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.listings.return-to-list",
            "Return to retainer list",
            !listingNavigation.IsRunning,
            () => _ = listingNavigation.ReturnToRetainerListAsync(),
            listingNavigation.IsRunning ? listingNavigation.Status : "Ready");
    }

    private void DrawListingRetainerContextMenu(ListingRow listing)
    {
        if (listingNavigation.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.MenuItem("Open retainer listings"))
            OpenRetainerListings(listing);
        if (listingNavigation.IsRunning)
            ImGui.EndDisabled();
    }

    private static string ListingUnitPrice(ListingRow listing) =>
        listing.UnitPrice.IsKnown ? $"{listing.UnitPrice.Value:N0} gil" : "Unknown";

    private static string ListingPriceRange(ListingGroupView group) =>
        !group.HasKnownPrice
            ? "Unknown"
            : group.LowestPrice == group.HighestPrice
                ? $"{group.LowestPrice:N0} gil"
                : $"{group.LowestPrice:N0}–{group.HighestPrice:N0}";

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

    private void DrawHistoryPopup(OwnerScope owner)
    {
        const string popup = "Transfer history##RQ";
        if (requestHistoryOpen || capturePresentingHistory)
        {
            if (!ImGui.IsPopupOpen(popup))
            {
                ImGui.SetNextWindowSize(
                    new Vector2(430, Math.Min(620, ImGui.GetMainViewport().WorkSize.Y - 80)),
                    ImGuiCond.Appearing);
                ImGui.OpenPopup(popup);
            }
            requestHistoryOpen = false;
        }
        if (!ImGui.BeginPopup(popup))
        {
            requestHistoryClose = false;
            return;
        }
        if (requestHistoryClose)
        {
            requestHistoryClose = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("Transfer history");
        ImGui.Separator();
        var operations = state.Snapshot().Operations
            .Where(operation => operation.Owner.Matches(owner))
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .ThenByDescending(operation => operation.CreatedAtUtc)
            .Take(30)
            .ToArray();
        if (operations.Length == 0)
        {
            ImGui.TextDisabled("No Quartermaster operations yet.");
            ImGui.EndPopup();
            return;
        }

        if (ImGui.BeginChild(
                "RQHistoryRows",
                new Vector2(410, Math.Min(540, ImGui.GetContentRegionAvail().Y)),
                false))
        {
            foreach (var operation in operations)
            {
                var succeeded = operation.Status == OperationStatuses.Succeeded;
                var failed = operation.Status is OperationStatuses.Failed or OperationStatuses.Indeterminate;
                ImGui.TextColored(
                    failed
                        ? new Vector4(1f, .45f, .45f, 1f)
                        : succeeded
                            ? new Vector4(.53f, .83f, .64f, 1f)
                            : new Vector4(.69f, .74f, .77f, 1f),
                    operation.Status);
                ImGui.SameLine();
                ImGui.TextDisabled(operation.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                ImGui.TextUnformatted(operation.SourcePlanName ?? "Quartermaster transfer");
                ImGui.TextWrapped(operation.Message);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private void DrawTransferReviewModal()
    {
        if (transferReview is not { } review)
            return;
        var popup = $"Execute {review.PlanName}##RQTransferReview";
        if (requestTransferReviewOpen)
        {
            ImGui.SetNextWindowSize(
                new Vector2(Math.Min(860, ImGui.GetMainViewport().WorkSize.X - 80), 520),
                ImGuiCond.Appearing);
            ImGui.OpenPopup(popup);
            requestTransferReviewOpen = false;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(popup, ref open, ImGuiWindowFlags.NoScrollbar))
        {
            if (!open)
                transferReview = null;
            return;
        }

        var runtime = runtimeSnapshots.Current;
        var plan = runtime.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == review.PlanId && candidate.Owner.Matches(runtime.Owner));
        if (plan is null)
        {
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), "This Transfer Plan no longer exists.");
            if (ImGui.Button("Close"))
            {
                transferReview = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        var projection = ResolveTransferWorkbenchProjection(runtime, plan);
        var rules = projection.Rules;
        var retrieval = projection.Retrieval;
        var deposit = projection.Deposit;
        var movements = projection.Movements;
        var hasMovement = projection.HasMovement;

        ImGui.TextUnformatted($"{movements:N0} movements");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.52f, .79f, .94f, 1f), $"Retrieve {retrieval.NeededQuantity:N0}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.53f, .83f, .64f, 1f), $"Stow {deposit.PlannedQuantity:N0}");
        ImGui.Separator();

        var reviewRows = projection.Rows
            .Select(row => new TransferReviewRow(
                row.Rule,
                row.Line,
                row.PlayerQuantity,
                row.Difference,
                runtime))
            .ToArray();
        if (transferReviewTable.Begin(
                "RQTransferReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y - 48)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            transferReviewTable.DrawClippedRows(
                reviewRows,
                (row, _) => transferReviewTable.DrawRow(row, id: $"transfer-review:{row.Rule.Id}"));
            transferReviewTable.End();
        }

        if (ImGui.Button("Back"))
        {
            transferReview = null;
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.back",
            "Return to the Transfer Plan without executing",
            true,
            () => transferReview = null,
            "No inventory movement");
        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement,
            runtime.Owner.HasStableIdentity,
            transfers.CanStart,
            autoRetainer.IsRefreshing || autoRetainer.IsQueued);
        ImGui.SameLine();
        ImGui.TextDisabled(
            availability.BlockReason ??
            "Balanced items remain in the plan but require no movement.");
        var canExecute = availability.CanExecute;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - 110));
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button("Execute plan"))
        {
            ExecuteTransferPlan(plan.Id);
            transferReview = null;
            ImGui.CloseCurrentPopup();
        }
        if (!canExecute)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.execute",
            "Execute the reviewed Transfer Plan",
            canExecute,
            () =>
            {
                ExecuteTransferPlan(plan.Id);
                transferReview = null;
            },
            canExecute ? $"{movements:N0} movements" : availability.BlockReason);

        ImGui.EndPopup();
        if (!open)
            transferReview = null;
    }

    private void ExecuteTransferPlan(Guid planId)
    {
        var current = runtimeSnapshots.Current;
        var plan = current.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(current.Owner));
        if (plan is null)
            return;
        var rules = current.State.PlanItems
            .Where(rule => rule.StowagePlanId == plan.Id)
            .ToArray();
        var currentStowage = StowageEvaluator.BuildPlan(
            current.State,
            current.Browser,
            current.Owner,
            plan.Id);
        var currentRetrieval = BuildTransferRetrievalEvaluation(current, rules);
        var currentBatch = BuildSurplusBatch(current, currentStowage);
        var retrievalOperationId = currentRetrieval.NeededQuantity > 0
            ? journal.CreateTransferRetrieval(current.Owner, plan, rules).OperationId
            : null;
        var depositOperationId = currentBatch.PlannedQuantity > 0
            ? journal.CreateTransferDeposit(current.Owner, plan, currentBatch).OperationId
            : null;
        StartTransfer(transfers.ExecutePlanAsync(retrievalOperationId, depositOperationId));
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
            var canExecute = transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
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

    private static string RouteSummary(
        StowageRoutingPolicy routing,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var names = routing.PreferredRetainerIds
            .Select(id => retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(owner)
                ? retainer.RetainerName
                : $"Retainer {id}")
            .ToArray();
        if (names.Length == 0)
            return routing.Mode == StowageRoutingMode.ConsolidateFirst ? "Consolidate anywhere" : "Preferred first";
        var preferred = string.Join(" -> ", names);
        return routing.Overflow == StowageOverflowPolicy.AnyOwnerRetainer
            ? $"{preferred} -> any"
            : preferred;
    }

    private static string RoutingModeLabel(StowageRoutingMode mode) => mode switch
    {
        StowageRoutingMode.HomeFirst => "Preferred retainers first",
        _ => "Consolidate first",
    };

    private static string OverflowLabel(StowageOverflowPolicy overflow) => overflow switch
    {
        StowageOverflowPolicy.HoldOnPlayer => "Keep on player",
        _ => "Any retainer",
    };

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

    private sealed record StockWorkbenchRow(
        StockGroup Item,
        TargetPlanItem? Rule,
        StowageEvaluationLine? Line);

    private sealed record StockWorkbenchProjection(
        long RuntimeRevision,
        Guid? PlanId,
        IReadOnlyList<StockGroup> QueryItems,
        IReadOnlyList<StockWorkbenchRow> Rows);

    private sealed record TransferWorkbenchProjection(
        long RuntimeRevision,
        Guid PlanId,
        IReadOnlyList<TargetPlanItem> Rules,
        StowageEvaluation? Stowage,
        RetrievalPlan Retrieval,
        StowageDepositBatch Deposit,
        int Movements,
        bool HasMovement,
        IReadOnlyList<TransferWorkbenchRow> Rows);

    private sealed record RestockPlanRow(
        RestockPlanItem Item,
        PlanLine? Line,
        Guid PlanId,
        OwnerScope Owner);

    private sealed record TransferWorkbenchRow(
        TargetPlanItem Rule,
        StowageEvaluationLine? Line,
        PlanLine? RetrievalLine,
        int PlayerQuantity,
        int Difference,
        OwnerScope Owner,
        Guid PlanId,
        QuartermasterRuntimeSnapshot Runtime);

    private sealed record StowageDraftRow(
        TargetPlanItem Rule,
        QuartermasterRuntimeSnapshot Runtime);

    private sealed record TransferReviewRow(
        TargetPlanItem Rule,
        StowageEvaluationLine? Line,
        int PlayerQuantity,
        int Difference,
        QuartermasterRuntimeSnapshot Runtime);

    private sealed record TransferReviewRequest(Guid PlanId, string PlanName);

    private sealed record ListingGroupView(
        uint ItemId,
        string ItemName,
        int Quantity,
        int RetainerCount,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> UnlistedQuantity,
        IReadOnlyList<ListingRow> Listings)
    {
        public bool HasKnownPrice => Listings.Any(listing => listing.UnitPrice.IsKnown);
        public decimal LowestPrice => Listings
            .Where(listing => listing.UnitPrice.IsKnown)
            .Select(listing => listing.UnitPrice.Value)
            .DefaultIfEmpty()
            .Min();
        public decimal HighestPrice => Listings
            .Where(listing => listing.UnitPrice.IsKnown)
            .Select(listing => listing.UnitPrice.Value)
            .DefaultIfEmpty()
            .Max();
    }

    private readonly record struct ListingRowKey(
        ulong RetainerId,
        int? SlotIndex,
        uint ItemId,
        int Quantity,
        FfxivItemQuality Quality,
        decimal? UnitPrice)
    {
        public static ListingRowKey From(ListingRow listing) =>
            new(
                listing.RetainerId,
                listing.SlotIndex,
                listing.ItemId,
                listing.Quantity,
                listing.Quality,
                listing.UnitPrice.IsKnown ? listing.UnitPrice.Value : null);
    }

    private sealed record ItemChoice(uint ItemId, string Name, string Label);
}
