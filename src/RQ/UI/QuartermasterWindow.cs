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
    private readonly ListingNavigationCoordinator listingNavigation;
    private readonly IDataManager dataManager;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly WorkbenchState workbench = new();
    private readonly TableSelectionModel<uint> stockSelection = new();
    private readonly TableSelectionModel<ListingItemKey> listingGroupSelection = new();
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
    private OwnerScope? inlineTransferErrorOwner;
    private Guid? inlineTransferErrorPlanId;
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
    private PendingTransferPlanRecovery? pendingTransferPlanRecovery;
    private ListingPlanDraft? listingPlanDraft;
    private bool requestListingPlanEditorOpen;
    private bool listingPlanEditorVisible;
    private ListingItemKey? listingPlanEditorFocus;
    private string listingPlanEditorFilter = string.Empty;
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
                row => $"{row.ItemName} {QualityLabel(row.Quality)}",
                row => row.ItemName,
                ImGuiTableColumnFlags.WidthStretch),
            new(
                "Desired",
                62,
                row => row.DesiredUnits.ToString("N0"),
                row => row.DesiredUnits,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Listed",
                72,
                row => EvidenceText(row.ListedUnits),
                row => row.ListedUnits.IsKnown ? row.ListedUnits.Value : -1,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Need",
                72,
                row => EvidenceText(row.NeedUnits),
                row => row.NeedUnits.IsKnown ? row.NeedUnits.Value : -1,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Coverage",
                132,
                ListingCoverageText,
                row => ListingCoverageText(row)),
            new(
                "State",
                118,
                ListingStateText,
                row => ListingStateText(row)),
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
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "item"),
        new(
            "Player",
            80,
            row => row.Item.PlayerQuantity.ToString("N0"),
            row => row.Item.PlayerQuantity,
            Alignment: DalamudTableCellAlignment.Right,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "player"),
        new(
            "Retainers",
            84,
            row => row.Item.RetainerQuantity.ToString("N0"),
            row => row.Item.RetainerQuantity,
            Alignment: DalamudTableCellAlignment.Right,
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
            Alignment: DalamudTableCellAlignment.Right,
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
                : TransferActionColor(row.Line?.Action),
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
            Alignment: DalamudTableCellAlignment.Right,
            Id: "player"),
        new(
            "Target",
            132,
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
            Alignment: DalamudTableCellAlignment.Right,
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
            "Route",
            1.1f,
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            row => RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawInlineTransferRoute(row.Owner, row.PlanId, row.Rule, row.Runtime),
            Id: "route"),
        new(
            "Source",
            92,
            row => row.ListingLink is null ? "Independent" : "Listing Plan",
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
            Draw: DrawTransferSource,
            Id: "source",
            HeaderTooltip: "Independent target or linked Listing Plan contribution."),
        new(
            "Listing shortfall",
            118,
            TransferListingShortfall,
            row => row.ListingContribution.IsKnown ? row.ListingContribution.Value : -1,
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
            Alignment: DalamudTableCellAlignment.Right,
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
        new("Player / target", 120, row => row.ListingContribution.IsKnown ? $"{row.PlayerQuantity:N0} / {row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity:N0}" : $"{row.PlayerQuantity:N0} / —"),
        new(
            "Diff",
            72,
            row => row.ListingContribution.IsKnown ? SignedQuantity(row.Difference) : "—",
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
                DrawListings(runtime);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
        DrawStowageEditorModal(runtime);
        DrawListingPlanEditorModal(runtime);
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
        VisibleStockCount = result.Items.Count;
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
        var sourceRows = ResolveStockWorkbenchProjection(runtime, result.Items, selectedPlan);
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
        var listingEvaluation = ListingPlanEvaluator.Evaluate(
            ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner),
            runtime.Browser);
        var rows = queryItems
            .Select(item =>
            {
                rules.TryGetValue(item.ItemId, out var rule);
                evaluated.TryGetValue(rule?.Id ?? Guid.Empty, out var line);
                var demand = listingEvaluation.Items.Where(candidate => candidate.ItemId == item.ItemId).ToArray();
                return new StockWorkbenchRow(item, rule, line, demand);
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
            stockSelection.Clear();
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
                ImGui.SameLine();
                ImGui.TextDisabled(StockListingShortfall([selectedDemand]));
                ImGui.SameLine();
                if (ImGui.SmallButton("Edit Listing Plan…##stock"))
                    OpenListingPlanEditor(runtime, new(selectedDemand.ItemId, selectedDemand.Quality));
                ImGui.SameLine();
                var transferPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
                var linked = transferPlan is not null && runtime.State.TransferPlanListingLinks.Any(link =>
                    link.StowagePlanId == transferPlan.Id && link.ListingPlanId == listingPlan.Id &&
                    link.ItemId == selectedDemand.ItemId && link.Quality == selectedDemand.Quality);
                if (ImGui.SmallButton(linked ? "Unlink demand" : "Link demand to Transfer Plan"))
                    SetListingDemandLink(runtime, listingPlan, transferPlan, selectedDemand, !linked);
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
                TransferActionColor(row.Line?.Action),
                $"({SignedQuantity(row.Difference)})");
        if (targetHovered || ImGui.IsItemHovered())
            ImGui.SetTooltip(row.ListingContribution.IsKnown
                ? $"Target {row.Rule.TargetQuantity + listingShortfall:N0}: independent {row.Rule.TargetQuantity:N0} + Listing Plan {listingShortfall:N0}; current player stock {row.PlayerQuantity:N0}."
                : $"Independent target {row.Rule.TargetQuantity:N0}; Listing Plan demand is not yet known.");
    }

    private void DrawTransferSource(TransferWorkbenchRow row)
    {
        if (row.ListingLink is null)
        {
            ImGui.TextDisabled("Independent");
            return;
        }
        ImGui.TextDisabled("Listing Plan");
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
                : TransferActionColor(row.Line?.Action);
        ImGui.TextColored(primaryColor, outcome.Primary);
        if (string.IsNullOrWhiteSpace(outcome.Constraint))
            return;
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"· {outcome.Constraint}");
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

    private static string TransferReviewOutcome(TransferReviewRow row) =>
        !row.ListingContribution.IsKnown
            ? "Verify listing demand"
            : row.Line?.Action switch
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
        CloseListingPlanEditor();
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
            canExecute
                ? projection.HasUnknownListingDemand ? "Listing demand will be verified first" : $"{movements:N0} movements"
                : availability.BlockReason);

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
        if (!projection.HasUnknownListingDemand && retrieval.MissingQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"{retrieval.MissingQuantity:N0} short");
        }
        if (!projection.HasUnknownListingDemand && surplusBatch.RemainingQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"No room for {surplusBatch.RemainingQuantity:N0}");
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
        DrawTableColumnsToolbar(transferWorkbenchTable, "RQTransferColumns", "Plan quantities use the latest accessible stock.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 4;
        var transferRows = projection.Rows;
        RenderedTransferRowCount = 0;
        if (transferWorkbenchTable.Begin(
                "RQTransferWorkbenchV2",
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
                });
            transferWorkbenchTable.End();
        }

        ImGui.TextDisabled(
            availability.BlockReason ??
            "Balanced items stay visible and are skipped during execution.");
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
            : $"{notice} The plan is still intact; Retry recalculates remaining work from current evidence.";
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
                var routedDepositQuantity = deposit.Routes
                    .Where(route => route.Request.SourceRuleId == rule.Id)
                    .Sum(route => route.RoutedQuantity);
                var playerQuantity = StowageEvaluator.PlayerQuantity(
                    rule,
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId));
                var accessibleStorageQuantity = AccessibleStorageQuantity(runtime.Browser, rule);
                listingContributions.TryGetValue((rule.ItemId, rule.Quality), out var listingContribution);
                return new TransferWorkbenchRow(
                    rule,
                    line,
                    retrievalLine,
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

    private void FocusStockFromListings(QuartermasterRuntimeSnapshot runtime, ListingItemKey key)
    {
        stockSelection.Clear();
        stockSelection.SetSelected(key.ItemId, true);
        workbench.SelectedStockListingQuality = key.Quality;
        requestedView = WorkbenchView.Stock;
        workbench.View = WorkbenchView.Stowage;
    }

    private void OpenListingPlanEditor(QuartermasterRuntimeSnapshot runtime, ListingItemKey? focus)
    {
        listingPlanDraft = ListingPlanCatalog.Draft(state.Snapshot(), runtime.Owner, runtime.Browser);
        listingPlanEditorFocus = focus;
        listingPlanEditorFilter = string.Empty;
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
        var capacityIssues = validation.Where(issue => issue.Field == "RetainerCapacity").ToArray();
        if (capacityIssues.Length > 0)
            ImGui.TextColored(new Vector4(1f, .45f, .35f, 1f), capacityIssues[0].Message);
        else
        {
            var fullest = draft.Assignments.Where(assignment => assignment.Enabled)
                .GroupBy(assignment => new { assignment.RetainerId, assignment.RetainerName })
                .Select(group => new { group.Key.RetainerName, Slots = group.Sum(assignment => assignment.ListingCount) })
                .OrderByDescending(row => row.Slots)
                .FirstOrDefault();
            ImGui.TextDisabled(fullest is null ? "No planned slots yet." : $"Highest planned capacity: {fullest.RetainerName} {fullest.Slots:N0} / 20");
        }
        var transitionConflict = ListingCapacityTransitionConflict(draft, runtime.Browser);
        if (transitionConflict is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), transitionConflict);
        }
        if (!string.IsNullOrWhiteSpace(listingPlanEditorError))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), listingPlanEditorError);
        }
        if (listingPlanEditorConflicts.Count > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, .7f, .3f, 1f),
                $"{listingPlanEditorConflicts.Count:N0} concurrent field changes were rebased. Your values remain highlighted; edit them or Save again to keep them.");
        }

        if (ImGui.BeginChild("RQListingPlanRows", new Vector2(0, Math.Max(230, ImGui.GetContentRegionAvail().Y - 42)), false))
            DrawListingPlanRows(draft, runtime, validation.Concat(listingPlanEditorConflicts).ToArray());
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.TextDisabled(validation.Count == 0 ? "Changes apply together; current listings never rewrite this plan automatically." : $"{validation.Count:N0} fields need attention before Save.");
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - 176));
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

    internal static string? ListingCapacityTransitionConflict(ListingPlanDraft draft, BrowserProjection browser)
    {
        var plan = new ListingPlan
        {
            Owner = draft.Owner,
            Assignments = draft.Assignments.Select(ListingPlanCatalog.Copy).ToList(),
        };
        foreach (var retainer in draft.Assignments.Where(assignment => assignment.Enabled && assignment.RetainerId != 0)
                     .GroupBy(assignment => new { assignment.RetainerId, assignment.RetainerName }))
        {
            var scopeKey = BrowserScope.RetainerKey(retainer.Key.RetainerId);
            if (!browser.RetainerListingsCompleteByScope.GetValueOrDefault(scopeKey))
                continue;
            var planned = retainer.Sum(assignment => assignment.ListingCount);
            var unmanaged = ListingPlanEvaluator.Evaluate(plan, browser, scopeKey).Items
                .SelectMany(item => item.UnmanagedPhysicalListings)
                .Count();
            if (planned + unmanaged > 20)
                return $"Current transition: {retainer.Key.RetainerName} has {planned + unmanaged:N0} occupied ({unmanaged:N0} outside this plan); Save remains available.";
        }
        return null;
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
            draft.Assignments.Add(new ListingPlanAssignment
            {
                ItemId = selectedChoice.ItemId,
                ItemName = selectedChoice.Name,
                RetainerId = retainer?.RetainerId ?? 0,
                RetainerName = retainer?.RetainerName ?? "Missing retainer",
                UnitPrice = observedPrice is null ? 0 : checked((int)observedPrice.Value),
            });
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
                    draft.Assignments.Add(ListingPlanCatalog.Copy(assignment));
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190);
        ImGui.InputTextWithHint("##listingplanfilter", "Filter assignments", ref listingPlanEditorFilter, 80);
        ImGui.SameLine();
        if (listingPlanEditorFocus is not null && ImGui.SmallButton("All assignments"))
            listingPlanEditorFocus = null;
    }

    private void DrawListingPlanRows(
        ListingPlanDraft draft,
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<ListingPlanValidationIssue> issues)
    {
        var rows = draft.Assignments
            .Where(assignment => listingPlanEditorFocus is { } focus
                ? assignment.ItemId == focus.ItemId && assignment.Quality == focus.Quality
                : listingPlanEditorFilter.Length == 0 || assignment.ItemName.Contains(listingPlanEditorFilter, StringComparison.OrdinalIgnoreCase))
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
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
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
            var price = assignment.UnitPrice; ImGui.SetNextItemWidth(-1); if (ImGui.InputInt("##price", ref price, 0)) assignment.UnitPrice = price;
            PopListingIssue(priceIssue);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(assignment.DesiredUnits.ToString("N0"));
            ImGui.TableNextColumn(); if (ImGui.SmallButton("X")) draft.Assignments.Remove(assignment);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static ListingPlanValidationIssue? FindListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field) =>
        issues.FirstOrDefault(issue => issue.AssignmentId == assignmentId && issue.Field == field);

    private static ListingPlanValidationIssue? PushListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field)
    {
        var issue = FindListingIssue(issues, assignmentId, field) ?? FindListingIssue(issues, assignmentId, "Assignment");
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

    private void DrawListings(QuartermasterRuntimeSnapshot runtime)
    {
        var projection = runtime.Browser;
        var revision = runtime.ListingsRevision;
        var sourceListings = projection.GetListings(workbench.ScopeKey);
        var context = BrowserQueryController.CreateListingContext(sourceListings, projection.Owner);
        DalamudFilterAutocompleteRenderer.Draw(
            "RQListingsWorkbench",
            "Search listed items",
            context,
            workbench.ListingFilterState,
            Math.Max(240, ImGui.GetContentRegionAvail().X - 340));
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
        ImGui.SameLine();
        if (ImGui.Button("Edit Listing Plan…##top"))
            OpenListingPlanEditor(runtime, workbench.SelectedListingItem);

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

        var evaluation = ListingPlanEvaluator.Evaluate(
            ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner),
            projection,
            workbench.ScopeKey);
        var resultKeys = result.Listings.Select(listing => new ListingItemKey(
            listing.ItemId,
            listing.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly)).ToHashSet();
        var hasFilter = !string.IsNullOrWhiteSpace(workbench.ListingFilterState.Expression);
        var groups = evaluation.Items
            .Where(item => !hasFilter || resultKeys.Contains(new(item.ItemId, item.Quality)) ||
                           item.ItemName.Contains(workbench.ListingFilterState.Expression, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ListingGroupView(
                new(item.ItemId, item.Quality),
                item.ItemId,
                item.ItemName,
                item.Quality,
                item.DesiredUnits,
                item.ListedUnits,
                item.NeedUnits,
                item.PlayerUnits,
                item.RetainerUnits,
                item.ImmediatelyListableUnits,
                item.MovementNeedUnits,
                item.OtherRetainerUnits,
                item.RetrievableUnits,
                item.MissingUnits,
                item.Coverage,
                item.Assignments,
                item.PhysicalListings,
                item.UnmanagedPhysicalListings))
            .OrderBy(group => group.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Quality)
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

        if (workbench.SelectedListingItem is not { } selectedItem ||
            groups.All(group => group.Key != selectedItem))
            workbench.SelectedListingItem = groups[0].Key;
        var selected = groups.Single(group => group.Key == workbench.SelectedListingItem);
        listingGroupSelection.Retain(groups.Select(group => group.Key));
        listingGroupSelection.SelectOnly(selected.Key);
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
                var groupKeys = visibleGroups.Select(group => group.Key).ToArray();
                for (var groupIndex = 0; groupIndex < visibleGroups.Count; groupIndex++)
                {
                    var group = visibleGroups[groupIndex];
                    if (listingGroupTable.DrawSelectableRow(
                            group,
                            listingGroupSelection,
                            groupKeys,
                            groupIndex,
                            $"##listing-group:{group.ItemId}:{group.Quality}"))
                    {
                        listingGroupSelection.SelectOnly(group.Key);
                        workbench.SelectedListingItem = group.Key;
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
            ImGui.SameLine();
            ImGui.TextDisabled(QualityLabel(selected.Quality));
            var physicalTarget = ResolveListingNavigationTarget(selected.Listings);
            var assignmentTarget = selected.Assignments.FirstOrDefault()?.Assignment;
            var canOpenListings = (physicalTarget is not null || assignmentTarget is not null) && !listingNavigation.IsRunning;
            ImGui.SameLine();
            if (ImGui.SmallButton("Show stock"))
                FocusStockFromListings(runtime, selected.Key);
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit Listing Plan…"))
                OpenListingPlanEditor(runtime, selected.Key);
            ImGui.SameLine();
            if (!canOpenListings)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton("Open retainer listings"))
            {
                if (physicalTarget is not null)
                    OpenRetainerListings(physicalTarget);
                else if (assignmentTarget is not null)
                    _ = listingNavigation.OpenRetainerListingsAsync(new(assignmentTarget.RetainerId, assignmentTarget.RetainerName));
            }
            if (!canOpenListings)
                ImGui.EndDisabled();
            ImGui.TextDisabled(
                $"{selected.DesiredUnits:N0} desired · {EvidenceText(selected.ListedUnits)} listed · {EvidenceText(selected.NeedUnits)} need · {ListingCoverageText(selected)}");
            if (selected.Assignments.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Listing Plan assignments");
                if (ImGui.BeginTable("RQListingAssignments", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 76);
                    ImGui.TableSetupColumn("Exact", ImGuiTableColumnFlags.WidthFixed, 52);
                    ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 94);
                    ImGui.TableSetupColumn("Exceptions", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    foreach (var assignment in selected.Assignments)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(assignment.Assignment.RetainerName);
                        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{assignment.Assignment.ListingCount:N0} × {assignment.Assignment.QuantityPerListing:N0}");
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(selected.ListedUnits.IsKnown ? assignment.ExactListings.ToString("N0") : "—");
                        ImGui.TableNextColumn(); ImGui.TextUnformatted($"{assignment.Assignment.UnitPrice:N0}");
                        ImGui.TableNextColumn();
                        if (!selected.ListedUnits.IsKnown)
                            ImGui.TextDisabled("—");
                        else
                        {
                            var exceptions = new List<string>();
                            if (assignment.UnknownPriceListings > 0) exceptions.Add($"{assignment.UnknownPriceListings:N0} price unknown");
                            if (assignment.WrongPriceListings > 0) exceptions.Add($"{assignment.WrongPriceListings:N0} price");
                            if (assignment.WrongShapeListings > 0) exceptions.Add($"{assignment.WrongShapeListings:N0} shape");
                            if (assignment.WrongRetainerListings > 0) exceptions.Add($"{assignment.WrongRetainerListings:N0} retainer");
                            ImGui.TextDisabled(exceptions.Count == 0 ? "—" : string.Join(" · ", exceptions));
                        }
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.Separator();
            ImGui.TextDisabled("Current physical listings");
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

    private static string EvidenceText(Franthropy.Filtering.Evaluation.FieldEvidence<int> value) =>
        value.IsKnown ? value.Value.ToString("N0") : "—";

    private static string ListingCoverageText(ListingGroupView group)
    {
        if (group.Coverage == ListingCoverageState.Satisfied &&
            (group.Assignments.Any(assignment => assignment.UnknownPriceListings + assignment.WrongPriceListings +
                                                  assignment.WrongShapeListings + assignment.WrongRetainerListings > 0) ||
             group.UnmanagedListings.Count > 0))
            return "No stock deficit";
        return group.Coverage switch
        {
            ListingCoverageState.Satisfied => "Satisfied",
            ListingCoverageState.ReadyOnAssignedRetainer => $"{group.NeedUnits.Value:N0} on assigned retainer",
            ListingCoverageState.ReadyOnPlayer => $"{EvidenceText(group.MovementNeedUnits)} on player",
            ListingCoverageState.Retrievable => $"{group.RetrievableUnits.Value:N0} / {group.NeedUnits.Value:N0} retrievable",
            ListingCoverageState.Missing => $"{group.MissingUnits.Value:N0} missing",
            _ => "Unknown",
        };
    }

    private static string ListingStateText(ListingGroupView group)
    {
        var price = group.Assignments.Sum(assignment => assignment.WrongPriceListings);
        var shape = group.Assignments.Sum(assignment => assignment.WrongShapeListings);
        var unknownPrice = group.Assignments.Sum(assignment => assignment.UnknownPriceListings);
        var wrongRetainer = group.Assignments.Sum(assignment => assignment.WrongRetainerListings);
        if (!group.ListedUnits.IsKnown)
            return "Listings unknown";
        if (group.NeedUnits.IsKnown && group.NeedUnits.Value > 0)
            return group.Coverage switch
            {
                ListingCoverageState.ReadyOnAssignedRetainer => $"List {group.NeedUnits.Value:N0}",
                ListingCoverageState.ReadyOnPlayer => $"Move {EvidenceText(group.MovementNeedUnits)} from player",
                ListingCoverageState.Retrievable => $"Retrieve {group.RetrievableUnits.Value:N0}",
                ListingCoverageState.Missing => $"Missing {group.MissingUnits.Value:N0}",
                _ => $"Need {group.NeedUnits.Value:N0} · source?",
            };
        if (price > 0)
            return $"{price:N0} wrong price";
        if (shape > 0)
            return $"{shape:N0} wrong shape";
        if (wrongRetainer > 0)
            return $"{wrongRetainer:N0} wrong retainer";
        if (unknownPrice > 0)
            return $"{unknownPrice:N0} price unknown";
        if (group.UnmanagedListings.Count > 0)
            return $"{group.UnmanagedListings.Count:N0} off plan";
        return group.Assignments.Count == 0 ? "Not planned" : "On plan";
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

    private static int AccessibleStorageQuantity(BrowserProjection browser, TargetPlanItem rule) =>
        browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId)?.Stacks
            .Where(stack =>
                stack.ScopeKind == BrowserScopeKind.Retainer &&
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
        var operations = journal.History(owner, 30);
        if (operations.Count == 0)
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
        ImGui.TextColored(new Vector4(.53f, .83f, .64f, 1f), projection.HasUnknownListingDemand ? "Stow —" : $"Stow {deposit.RequestedQuantity:N0}");
        ImGui.Separator();

        var reviewRows = projection.Rows
            .Select(row => new TransferReviewRow(
                row.Rule,
                row.Line,
                row.PlayerQuantity,
                row.Difference,
                row.ListingContribution,
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
            hasMovement || projection.HasUnknownListingDemand,
            runtime.Owner.HasStableIdentity,
            transfers.CanStart,
            retainerRefresh.IsRefreshing || retainerRefresh.IsQueued);
        ImGui.SameLine();
        ImGui.TextDisabled(
            projection.HasUnknownListingDemand
                ? "Listing demand will be verified before any movement."
                : availability.BlockReason ??
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
        StowageEvaluationLine? Line,
        IReadOnlyList<ListingPlanItemEvaluation> ListingDemand);

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
        bool HasUnknownListingDemand,
        IReadOnlyList<TransferWorkbenchRow> Rows);

    private sealed record PendingTransferPlanRecovery(Guid PlanId, string RefreshRunId);

    private sealed record RestockPlanRow(
        RestockPlanItem Item,
        PlanLine? Line,
        Guid PlanId,
        OwnerScope Owner);

    private sealed record TransferWorkbenchRow(
        TargetPlanItem Rule,
        StowageEvaluationLine? Line,
        PlanLine? RetrievalLine,
        int RoutedDepositQuantity,
        int PlayerQuantity,
        int AccessibleStorageQuantity,
        int Difference,
        FieldEvidence<int> ListingContribution,
        TransferPlanListingLink? ListingLink,
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
        FieldEvidence<int> ListingContribution,
        QuartermasterRuntimeSnapshot Runtime);

    private sealed record TransferReviewRequest(Guid PlanId, string PlanName);

    private sealed record ListingGroupView(
        ListingItemKey Key,
        uint ItemId,
        string ItemName,
        ItemQualityPolicy Quality,
        int DesiredUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> ListedUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> NeedUnits,
        int PlayerUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> RetainerUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> ImmediatelyListableUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> MovementNeedUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> OtherRetainerUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> RetrievableUnits,
        Franthropy.Filtering.Evaluation.FieldEvidence<int> MissingUnits,
        ListingCoverageState Coverage,
        IReadOnlyList<ListingAssignmentEvaluation> Assignments,
        IReadOnlyList<ListingRow> Listings,
        IReadOnlyList<ListingRow> UnmanagedListings);

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
