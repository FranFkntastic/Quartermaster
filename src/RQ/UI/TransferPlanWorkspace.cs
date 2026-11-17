using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.UI.Inputs;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record TransferPlanWorkspaceSnapshot(int ProjectionBuildCount, int RenderedRowCount);

/// <summary>
/// Owns the Transfer Plan workspace: the only plan editor. Every edit applies to the saved
/// plan through <see cref="StowagePlanCatalog"/>; there is no separate draft or modal.
/// </summary>
internal sealed class TransferPlanWorkspace
{
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);
    private static readonly Vector4 AmberColor = new(1f, .7f, .3f, 1f);
    private static readonly Vector4 VendorColor = new(.92f, .72f, .35f, 1f);
    private static readonly Vector4 PausedRowColor = new(.13f, .15f, .17f, .42f);
    private const string AddSuggestionsPopup = "##RQPlanAddSuggestions";
    private const ImGuiWindowFlags SuggestionWindowFlags =
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.AlwaysAutoResize;
    private const int FocusGiveUpFrames = 120;

    private sealed record RemovedLines(
        Guid PlanId,
        string Label,
        IReadOnlyList<TargetPlanItem> Rules,
        IReadOnlyList<TransferPlanListingLink> Links);

    private sealed record AddSuggestion(
        string Label,
        ItemChoice? Item,
        ItemGroup? Group,
        GameCatalogCategory? Category = null,
        GameCatalogCraft? Craft = null,
        int CraftCount = 1);

    /// <summary>One line to add: Quantity null adds a balanced line; a quantity sets a new line's target or raises an existing one.</summary>
    private sealed record PlanAddEntry(uint ItemId, string Name, ItemQualityPolicy Quality, int? Quantity = null);

    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly TransferCoordinator transfers;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly WorkbenchState workbench;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly TransferExecutionController transferExecution;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Func<GameCatalogSnapshot?> gameCatalog;
    private readonly Func<GilVendorCatalog>? vendorCatalogSource;
    private readonly Action<Guid, string> requestTransferReview;
    private readonly Action<TransferVendorProcurementReview> requestVendorReview;
    private readonly Action<StowagePlan, OwnerScope> requestDeletePlan;
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private readonly TableSelectionModel<Guid> selection = new();
    private readonly Dictionary<Guid, string> lineErrors = [];
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private int transferProjectionBuildCount;
    private int renderedTransferRowCount;
    private object? dismissedVendorRun;
    private string vendorStatus = string.Empty;

    private Guid? renamingPlanId;
    private string renameText = string.Empty;
    private bool renameFocusRequested;

    private string bulkTarget = string.Empty;
    private ItemQualityPolicy? bulkQuality;
    private readonly PlacementMatrix<Guid> placement;
    private bool showPlacement;

    private string addText = string.Empty;
    private string cachedItemSearch = string.Empty;
    private IReadOnlyList<ItemChoice> cachedItemChoices = [];

    private Guid? focusTargetRuleId;
    private int focusTargetFrames;
    private Guid? noteRuleId;
    private string noteBuffer = string.Empty;
    private RemovedLines? lastRemoved;
    private Guid[]? pendingSelection;
    private int pendingSelectionFrames;

    public TransferPlanWorkspace(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        TransferCoordinator transfers,
        RetainerRefreshCoordinator retainerRefresh,
        TransferVendorProcurementService vendorProcurement,
        WorkbenchState workbench,
        ItemGroupWorkspace itemGroupWorkspace,
        TransferExecutionController transferExecution,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Func<GameCatalogSnapshot?> gameCatalog,
        Func<GilVendorCatalog>? vendorCatalogSource,
        Action<Guid, string> requestTransferReview,
        Action<TransferVendorProcurementReview> requestVendorReview,
        Action<StowagePlan, OwnerScope> requestDeletePlan)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.transfers = transfers;
        this.retainerRefresh = retainerRefresh;
        this.vendorProcurement = vendorProcurement;
        this.workbench = workbench;
        this.itemGroupWorkspace = itemGroupWorkspace;
        this.transferExecution = transferExecution;
        this.reviewRegistry = reviewRegistry;
        this.searchItems = searchItems;
        this.gameCatalog = gameCatalog;
        this.vendorCatalogSource = vendorCatalogSource;
        this.requestTransferReview = requestTransferReview;
        this.requestVendorReview = requestVendorReview;
        this.requestDeletePlan = requestDeletePlan;
        transferWorkbenchTable = CreateTable();
        placement = new PlacementMatrix<Guid>("quartermaster.transfer.placement", "Stow", reviewRegistry);
    }

    public TransferPlanWorkspaceSnapshot Snapshot() =>
        new(transferProjectionBuildCount, renderedTransferRowCount);

    public void ClearVendorStatus() => vendorStatus = string.Empty;

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    /// <summary>
    /// Keeps an owner's Unsaved plan present and the selection pointing at one of the owner's plans.
    /// </summary>
    public void EnsureSelection(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        if (!owner.HasStableIdentity)
            return;
        var selectedId = workbench.SelectedStowagePlanId;
        var (hasScratch, selectedValid) = state.Read(document => (
            document.StowagePlans.Any(plan => plan.IsEphemeral && plan.Owner.Matches(owner)),
            selectedId is { } id && document.StowagePlans.Any(plan => plan.Id == id && plan.Owner.Matches(owner))));
        if (!hasScratch)
            state.Mutate(document => StowagePlanCatalog.EnsureScratch(document, owner));
        if (!selectedValid)
            workbench.SelectedStowagePlanId = state.Read(document =>
                document.StowagePlans.First(plan => plan.IsEphemeral && plan.Owner.Matches(owner)).Id);
    }

    /// <summary>Adds stock-browser selections to the selected plan (or the Unsaved plan).</summary>
    public void AddStock(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items, bool stow, ulong? retainerId = null)
    {
        if (!runtime.Owner.HasStableIdentity)
            return;
        EnsureSelection(runtime);
        var planId = workbench.SelectedStowagePlanId!.Value;
        state.Mutate(document =>
        {
            var draft = StowagePlanCatalog.Draft(document, runtime.Owner, planId);
            foreach (var item in items)
                StowagePlanCatalog.SetStockRule(draft, item.ItemId, item.ItemName, item.PlayerQuantity, stow, retainerId);
            StowagePlanCatalog.Apply(document, runtime.Owner, draft);
        });
    }

    /// <summary>Adds balanced lines for items not already in the selected plan and selects every affected line.</summary>
    public void AddItems(QuartermasterRuntimeSnapshot runtime, IEnumerable<(uint ItemId, string Name)> items) =>
        AddToSelectedPlan(runtime, items.Select(item => new PlanAddEntry(item.ItemId, item.Name, ItemQualityPolicy.Any)).ToArray());

    /// <summary>Adds a craft's materials × count: new lines target the amount, existing lines have it added.</summary>
    public void AddMaterials(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<CraftMaterial> materials, int count) =>
        AddToSelectedPlan(runtime, MaterialEntries(materials, count));

    private static PlanAddEntry[] MaterialEntries(IReadOnlyList<CraftMaterial> materials, int count)
    {
        var multiplier = Math.Clamp(count, 1, CraftQuery.MaxCount);
        return materials
            .Select(material => new PlanAddEntry(
                material.ItemId,
                material.Name,
                ItemQualityPolicy.Any,
                (int)Math.Min(int.MaxValue, (long)material.Quantity * multiplier)))
            .ToArray();
    }

    private void AddToSelectedPlan(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<PlanAddEntry> entries)
    {
        if (!runtime.Owner.HasStableIdentity || entries.Count == 0)
            return;
        EnsureSelection(runtime);
        if (workbench.SelectedStowagePlanId is not { } planId)
            return;
        AddLines(runtime, planId, entries, selectAffected: true);
    }

    /// <summary>
    /// Adds lines without duplicating items already planned, then (for multi-item adds) makes the
    /// affected lines the plan's row selection so the bulk bar applies.
    /// </summary>
    private IReadOnlyList<Guid> AddLines(
        QuartermasterRuntimeSnapshot runtime,
        Guid planId,
        IReadOnlyList<PlanAddEntry> entries,
        bool selectAffected)
    {
        try
        {
            var affected = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, runtime.Owner, planId);
                var ids = new List<Guid>();
                foreach (var entry in entries)
                {
                    // An Any-quality add treats any line for the item as already present.
                    var existing = draft.Rules.FirstOrDefault(rule => rule.ItemId == entry.ItemId && rule.Quality == entry.Quality) ??
                                   (entry.Quality == ItemQualityPolicy.Any
                                       ? draft.Rules.FirstOrDefault(rule => rule.ItemId == entry.ItemId)
                                       : null);
                    if (existing is null)
                    {
                        // New lines start balanced (target = carried) so they move nothing until a target is set.
                        var player = TransferPlanEvaluation.PlayerQuantity(
                            runtime.Browser,
                            new TargetPlanItem { ItemId = entry.ItemId, Quality = entry.Quality });
                        existing = StowagePlanCatalog.AddBalancedRule(draft, entry.ItemId, entry.Name, entry.Quality, player);
                        if (entry.Quantity is { } quantity)
                            existing.TargetQuantity = Math.Max(0, quantity);
                    }
                    else if (entry.Quantity is { } quantity)
                    {
                        existing.TargetQuantity = (int)Math.Min(int.MaxValue, (long)existing.TargetQuantity + Math.Max(0, quantity));
                    }
                    if (!ids.Contains(existing.Id))
                        ids.Add(existing.Id);
                }
                StowagePlanCatalog.Apply(document, runtime.Owner, draft);
                return ids;
            });
            workbench.SelectedStowagePlanId = planId;
            showPlacement = false;
            if (selectAffected)
            {
                // Applied once the plan projection includes the new lines (see ApplyPendingSelection).
                selection.Clear();
                pendingSelection = affected.ToArray();
                pendingSelectionFrames = 0;
            }
            transferExecution.ClearInlineError();
            return affected;
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
            return [];
        }
    }

    private void ApplyPendingSelection(TransferWorkbenchProjection projection)
    {
        if (pendingSelection is not { } pending)
            return;
        var ruleIds = projection.Rules.Select(rule => rule.Id).ToHashSet();
        if (!pending.All(ruleIds.Contains))
        {
            if (++pendingSelectionFrames > FocusGiveUpFrames)
                pendingSelection = null;
            return;
        }
        selection.Clear();
        foreach (var id in pending)
            selection.SetSelected(id, true);
        pendingSelection = null;
    }

    /// <summary>Raises the selected plan's target for a listing shortfall and focuses its row.</summary>
    public void PrepareListingTarget(QuartermasterRuntimeSnapshot runtime, ListingPlanItemEvaluation demand)
    {
        if (!runtime.Owner.HasStableIdentity)
            return;
        EnsureSelection(runtime);
        if (workbench.SelectedStowagePlanId is not { } planId)
            return;
        if (!demand.MovementNeedUnits.IsKnown)
        {
            transferExecution.ReportInlineError("Listing observations are unavailable; the target was not changed.");
            return;
        }
        var need = Math.Max(0, demand.MovementNeedUnits.Value);
        try
        {
            var ruleId = state.Mutate(document =>
            {
                if (document.TransferPlanListingLinks.Any(link =>
                        link.StowagePlanId == planId && link.ItemId == demand.ItemId && link.Quality == demand.Quality))
                    throw new InvalidOperationException("This item already uses a live listing-demand link in this plan.");
                var draft = StowagePlanCatalog.Draft(document, runtime.Owner, planId);
                if (draft.Rules.Any(rule => rule.ItemId == demand.ItemId && rule.Quality == ItemQualityPolicy.Any))
                    throw new InvalidOperationException("This item has an Any-quality target. Choose NQ or HQ on its line before preparing exact-quality listings.");
                var rule = draft.Rules.FirstOrDefault(candidate => candidate.ItemId == demand.ItemId && candidate.Quality == demand.Quality);
                if (rule is null)
                {
                    rule = new TargetPlanItem
                    {
                        StowagePlanId = planId,
                        ItemId = demand.ItemId,
                        ItemName = demand.ItemName,
                        Quality = demand.Quality,
                        TargetQuantity = need,
                    };
                    draft.Rules.Add(rule);
                }
                rule.TargetQuantity = Math.Max(rule.TargetQuantity, need);
                rule.Enabled = true;
                StowagePlanCatalog.Apply(document, runtime.Owner, draft);
                return rule.Id;
            });
            RequestTargetFocus(ruleId);
            workbench.View = WorkbenchView.Stowage;
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private DalamudTableProjection<TransferWorkbenchRow> CreateTable() => new(
    [
        new(
            "##select",
            22,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize |
                   ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.NoHide,
            Draw: DrawSelectCell,
            Id: "select"),
        new(
            "Item",
            1.4f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
            Draw: DrawItemCell,
            Id: "item"),
        new(
            "On player",
            72,
            row => row.PlayerQuantity.ToString("N0"),
            row => row.PlayerQuantity,
            Draw: row =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(row.PlayerQuantity.ToString("N0"));
            },
            Id: "player"),
        new(
            "Target",
            118,
            TransferTargetText,
            row => row.ListingContribution.IsKnown ? row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity : row.Rule.TargetQuantity,
            Draw: DrawTransferTarget,
            Id: "target",
            HeaderTooltip: "Desired player quantity with its signed delta from current player stock."),
        new(
            "Outcome",
            1.3f,
            OutcomeText,
            OutcomeText,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: DrawOutcome,
            Id: "outcome"),
        new(
            "##line-menu",
            52,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize |
                   ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.NoHide,
            Draw: DrawLineMenu,
            Id: "line-menu"),
    ]);

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        var projection = selected is null ? null : ResolveProjection(runtime, selected);

        DrawHeader(runtime, selected, projection);

        if (selected is null || projection is null)
        {
            transferExecution.ClearInlineErrorContext();
            ImGui.Spacing();
            ImGui.TextUnformatted("No Transfer Plans yet.");
            return;
        }
        transferExecution.EnsureInlineErrorContext(owner, selected.Id);

        var recovery = runtime.State.TransferPlanRecovery;
        var hasCurrentRecovery = recovery is not null &&
            recovery.Owner.Matches(owner) &&
            recovery.PlanId == selected.Id &&
            recovery.PlanRevision == selected.Revision;
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

        ImGui.Separator();
        if (showPlacement)
        {
            DrawPlacement(runtime, owner, selected, projection);
            return;
        }
        ApplyPendingSelection(projection);
        selection.Retain(projection.Rules.Select(rule => rule.Id));
        if (selection.Count >= 2)
            DrawBulkBar(owner, selected, projection);
        else
            DrawSummary(projection);
        UiLayout.SameLineIfFits("Placement");
        if (ImGui.Button("Placement##RQTransferPlacement"))
            OpenPlacement(null);
        reviewRegistry.RegisterLastButton("quartermaster.transfer.placement.open", "Edit placement for this plan's lines", true,
            () => OpenPlacement(null), $"lines={projection.Rules.Count:N0}");
        DrawUndoStrip(owner, selected);
        DrawTable(runtime, selected, projection);
    }

    // ---- Placement ------------------------------------------------------------------------

    private void OpenPlacement(Guid? ruleId)
    {
        showPlacement = true;
        placement.ClearSelection();
        if (ruleId is { } id)
            placement.Select(id);
    }

    private void DrawPlacement(
        QuartermasterRuntimeSnapshot runtime,
        OwnerScope owner,
        StowagePlan plan,
        TransferWorkbenchProjection projection)
    {
        if (ImGui.Button("Return to plan##RQTransferPlacementReturn"))
            showPlacement = false;
        reviewRegistry.RegisterLastButton("quartermaster.transfer.placement.return", "Return to the plan lines", true,
            () => showPlacement = false, "Ready");
        var rows = projection.Rows
            .Select(row => new PlacementMatrixRow<Guid>(
                row.Rule.Id,
                row.Rule.Id.ToString("N"),
                row.Rule.Quality == ItemQualityPolicy.Any ? row.Rule.ItemName : $"{row.Rule.ItemName} {QualityLabel(row.Rule.Quality)}",
                (row.Line?.DepositQuantity ?? 0).ToString("N0"),
                row.Rule.Routing,
                PlacementMatrixData.Holdings(runtime, row.Rule.ItemId, row.Rule.Quality)))
            .ToArray();
        var columns = PlacementMatrixData.Columns(runtime, rows.Select(row => row.Routing));
        placement.Draw(rows, rows, columns, changes =>
        {
            var routing = changes.ToDictionary(change => change.Key, change => change.Routing);
            return MutateRules(owner, plan.Id, routing.Keys.ToArray(),
                rule => rule.Routing = routing[rule.Id].Copy());
        });
    }

    // ---- Header -------------------------------------------------------------------------

    private void DrawHeader(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan? selected,
        TransferWorkbenchProjection? projection)
    {
        var owner = runtime.Owner;
        var style = ImGui.GetStyle();
        float ButtonWidth(string label) =>
            ImGui.CalcTextSize(label.Split("##")[0]).X + (style.FramePadding.X * 2f);

        var movements = projection?.Movements ?? 0;
        var executeLabel = movements > 0
            ? $"Execute plan · {movements:N0} move{(movements == 1 ? string.Empty : "s")}###RQTransferExecute"
            : "Execute plan###RQTransferExecute";
        var vendor = projection?.Vendor;
        var maximumGil = vendor?.MaximumGil ?? 0;
        var buyLabel = maximumGil > 0
            ? $"Buy from vendor · {maximumGil:N0} gil###RQTransferVendorBuy"
            : "Buy from vendor###RQTransferVendorBuy";
        const string menuLabel = "…##RQTransferPlanMenu";
        var trailing = ButtonWidth(menuLabel) + ButtonWidth(executeLabel) + ButtonWidth(buyLabel) +
                       (style.ItemSpacing.X * 3f);
        var selectorWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - trailing);

        if (selected is not null && renamingPlanId == selected.Id)
            DrawRenameInput(owner, selected, selectorWidth);
        else
            DrawPlanSelector(runtime, selected, selectorWidth);

        ImGui.SameLine();
        DrawPlanMenu(runtime, selected, projection, menuLabel);

        var availability = TransferExecutionPolicy.ForExplicitRun(
            projection is not null && (projection.HasMovement || projection.HasUnknownListingDemand),
            owner.HasStableIdentity,
            transfers.CanStart,
            retainerRefresh.IsRefreshing || retainerRefresh.IsQueued);
        var canExecute = selected is not null && availability.CanExecute;
        UiLayout.SameLineIfFits(executeLabel);
        ImGui.BeginDisabled(!canExecute);
        if (ImGui.Button(executeLabel) && selected is not null)
            requestTransferReview(selected.Id, selected.Name);
        ImGui.EndDisabled();
        if (!canExecute && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) &&
            !string.IsNullOrWhiteSpace(availability.BlockReason))
            ImGui.SetTooltip(availability.BlockReason);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.execute",
            "Review and execute the selected Transfer Plan",
            canExecute,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentPlan = ResolveSelectedStowagePlan(current.State, current.Owner);
                if (currentPlan is not null)
                    requestTransferReview(currentPlan.Id, currentPlan.Name);
            },
            canExecute
                ? projection!.HasUnknownListingDemand ? "Listing demand will be verified first" : $"{movements:N0} movements"
                : availability.BlockReason);

        var canReviewVendor = vendor is not null &&
                              projection is not null &&
                              vendor.CanStart &&
                              !projection.HasUnknownListingDemand &&
                              !vendorProcurement.HasActiveRun &&
                              transfers.CanStart &&
                              !retainerRefresh.IsRefreshing &&
                              !retainerRefresh.IsQueued;
        var vendorBlockReason = vendorProcurement.HasActiveRun
            ? "A vendor run is already active"
            : projection?.HasUnknownListingDemand == true
                ? "Listing demand must be verified first"
                : "No reviewed vendor-purchasable shortfall";
        UiLayout.SameLineIfFits(buyLabel);
        ImGui.BeginDisabled(!canReviewVendor);
        if (ImGui.Button(buyLabel) && vendor is not null)
            requestVendorReview(vendor);
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
                requestVendorReview(ResolveProjection(current, currentPlan).Vendor);
            },
            canReviewVendor
                ? $"{vendor!.ApprovedQuantity:N0} units · maximum {vendor.MaximumGil:N0} gil"
                : vendorBlockReason);
    }

    private void DrawPlanSelector(QuartermasterRuntimeSnapshot runtime, StowagePlan? selected, float width)
    {
        var owner = runtime.Owner;
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo("##RQTransferPlan", selected?.Name ?? "Choose a Transfer Plan"))
            return;
        var plans = runtime.State.StowagePlans
            .Where(plan => plan.IsEphemeral && plan.Owner.Matches(owner))
            .Concat(StowagePlanCatalog.OwnerPlans(runtime.State, owner));
        foreach (var plan in plans)
        {
            if (!ImGui.Selectable($"{plan.Name}##transfer:{plan.Id}", selected?.Id == plan.Id))
                continue;
            if (selected?.Id != plan.Id)
            {
                selection.Clear();
                renamingPlanId = null;
            }
            workbench.SelectedStowagePlanId = plan.Id;
        }
        ImGui.EndCombo();
    }

    private void DrawRenameInput(OwnerScope owner, StowagePlan plan, float width)
    {
        ImGui.SetNextItemWidth(width);
        if (renameFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            renameFocusRequested = false;
        }
        var committed = ImGui.InputTextWithHint(
            "##RQTransferPlanRename",
            plan.IsEphemeral ? "Name this plan" : "Plan name",
            ref renameText,
            80,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var deactivated = ImGui.IsItemDeactivated();
        var canPromote = plan.IsEphemeral && !string.IsNullOrWhiteSpace(renameText);
        if (plan.IsEphemeral)
            reviewRegistry.RegisterLastButton(
                "quartermaster.transfer.scratch.promote",
                "Save as a named plan",
                canPromote,
                () => CommitRename(owner, plan),
                canPromote ? "Available" : "Name required");
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            renamingPlanId = null;
            return;
        }
        if (committed || deactivated)
            CommitRename(owner, plan);
    }

    private void StartRename(Guid planId, string initialName)
    {
        renamingPlanId = planId;
        renameText = initialName;
        renameFocusRequested = true;
    }

    private void CommitRename(OwnerScope owner, StowagePlan plan)
    {
        var name = renameText.Trim();
        renamingPlanId = null;
        if (name.Length == 0 || (!plan.IsEphemeral && string.Equals(name, plan.Name, StringComparison.Ordinal)))
            return;
        try
        {
            // Naming the Unsaved plan promotes it to a saved plan (the same transition as
            // "Save as"); a fresh Unsaved plan replaces it and the renamed plan stays selected.
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                if (plan.IsEphemeral)
                    return StowagePlanCatalog.PromoteScratch(document, owner, plan.Id, name).Id;
                var draft = StowagePlanCatalog.Draft(document, owner, plan.Id);
                draft.Name = name;
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private void DrawPlanMenu(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan? selected,
        TransferWorkbenchProjection? projection,
        string menuLabel)
    {
        var owner = runtime.Owner;
        var stable = owner.HasStableIdentity;
        if (ImGui.Button(menuLabel))
            ImGui.OpenPopup("RQTransferPlanMenu");
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plan.new",
            "Create a new Transfer Plan",
            stable,
            () => NewPlan(runtimeSnapshots.Current.Owner),
            stable ? "Available" : "Owner identity unavailable");
        if (selected?.IsEphemeral == true)
        {
            var planId = selected.Id;
            reviewRegistry.RegisterLastButton(
                "quartermaster.transfer.scratch.save-as",
                "Name the unsaved plan",
                true,
                () => StartRename(planId, string.Empty),
                "Available");
            var hasLines = projection?.Rules.Count > 0;
            reviewRegistry.RegisterLastButton(
                "quartermaster.transfer.scratch.clear",
                "Clear the unsaved plan",
                hasLines,
                () => ClearPlan(runtimeSnapshots.Current.Owner, planId),
                hasLines ? "Available" : "Plan is empty");
        }

        if (!ImGui.BeginPopup("RQTransferPlanMenu"))
            return;
        if (MenuItem("New plan", stable))
            NewPlan(owner);
        if (selected is not null)
        {
            if (MenuItem(selected.IsEphemeral ? "Save as…" : "Rename"))
                StartRename(selected.Id, selected.IsEphemeral ? string.Empty : selected.Name);
            if (MenuItem("Duplicate", stable))
                DuplicatePlan(owner, selected);
            if (MenuItem("Clear", projection?.Rules.Count > 0))
                ClearPlan(owner, selected.Id);
            var selectedRules = projection?.Rules.Where(rule => selection.IsSelected(rule.Id)).ToArray() ?? [];
            if (MenuItem(
                    selectedRules.Length > 0 ? $"Save {selectedRules.Length:N0} selected as @group…" : "Save selected as @group…",
                    selectedRules.Length > 0))
            {
                itemGroupWorkspace.OpenNewWorkspaceDraft(selectedRules);
                workbench.View = WorkbenchView.ItemGroups;
            }
            if (!selected.IsEphemeral)
            {
                ImGui.Separator();
                if (MenuItem("Delete plan"))
                    requestDeletePlan(selected, owner);
            }
        }
        ImGui.EndPopup();
    }

    private static bool MenuItem(string label, bool enabled = true)
    {
        if (!enabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.MenuItem(label);
        if (!enabled)
            ImGui.EndDisabled();
        return clicked && enabled;
    }

    private void NewPlan(OwnerScope owner)
    {
        if (!owner.HasStableIdentity)
            return;
        try
        {
            var (id, name) = state.Mutate(document =>
            {
                var plan = StowagePlanCatalog.Create(document, owner);
                return (plan.Id, plan.Name);
            });
            workbench.SelectedStowagePlanId = id;
            selection.Clear();
            StartRename(id, name);
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private void DuplicatePlan(OwnerScope owner, StowagePlan source)
    {
        try
        {
            var (id, name) = state.Mutate(document =>
            {
                var plan = StowagePlanCatalog.Duplicate(document, owner, source.Id);
                return (plan.Id, plan.Name);
            });
            workbench.SelectedStowagePlanId = id;
            selection.Clear();
            StartRename(id, name);
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private void ClearPlan(OwnerScope owner, Guid planId)
    {
        var ruleIds = state.Read(document => document.PlanItems
            .Where(rule => rule.StowagePlanId == planId)
            .Select(rule => rule.Id)
            .ToArray());
        RemoveRules(owner, planId, ruleIds);
    }

    // ---- Summary / bulk / undo ------------------------------------------------------------

    private static void DrawSummary(TransferWorkbenchProjection projection)
    {
        var totals = TransferWorkbenchPresentation.Totals(projection.Rows.Select(row => row.Projection));
        var first = true;
        void Clause(string text, Vector4? color)
        {
            if (!first)
            {
                ImGui.SameLine(0, 0);
                var width = ImGui.CalcTextSize($" · {text}").X;
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
                else
                {
                    ImGui.TextDisabled(" · ");
                    ImGui.SameLine(0, 0);
                }
            }
            first = false;
            if (color is { } value)
                ImGui.TextColored(value, text);
            else
                ImGui.TextUnformatted(text);
        }

        ImGui.AlignTextToFramePadding();
        Clause($"{totals.Lines:N0} line{(totals.Lines == 1 ? string.Empty : "s")}", null);
        if (projection.HasUnknownListingDemand)
        {
            Clause("Verifying listing demand…", AmberColor);
            return;
        }
        if (totals.Retrieve > 0)
            Clause($"Retrieve {totals.Retrieve:N0}", TransferPresentation.ActionColor(StowageAction.Retrieve));
        if (totals.Stow > 0)
            Clause($"Stow {totals.Stow:N0}", TransferPresentation.ActionColor(StowageAction.Deposit));
        if (totals.Vendor > 0)
            Clause($"Vendor {totals.Vendor:N0}", VendorColor);
        if (totals.Short > 0)
            Clause($"{totals.Short:N0} short", ErrorColor);
        if (totals.NoRoom > 0)
            Clause($"no room for {totals.NoRoom:N0}", ErrorColor);
        if (totals.StockUnverified)
            Clause("Retainer stock unverified", AmberColor);
    }

    private void DrawBulkBar(OwnerScope owner, StowagePlan plan, TransferWorkbenchProjection projection)
    {
        var selectedIds = projection.Rules
            .Where(rule => selection.IsSelected(rule.Id))
            .Select(rule => rule.Id)
            .ToArray();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{selectedIds.Length:N0} selected");

        ImGui.SameLine();
        ImGui.TextDisabled("Target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        if (ImGui.InputTextWithHint("##RQBulkTarget", "—", ref bulkTarget, 9, ImGuiInputTextFlags.CharsDecimal))
            bulkTarget = new string(bulkTarget.Where(char.IsAsciiDigit).ToArray());

        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(84);
        if (ImGui.BeginCombo("##RQBulkQuality", bulkQuality is { } quality ? QualityLabel(quality) : "—"))
        {
            if (ImGui.Selectable("—##bulk-quality", bulkQuality is null))
                bulkQuality = null;
            foreach (var value in Enum.GetValues<ItemQualityPolicy>())
                if (ImGui.Selectable(QualityLabel(value), bulkQuality == value))
                    bulkQuality = value;
            ImGui.EndCombo();
        }

        int? target = bulkTarget.Length > 0 &&
                      int.TryParse(bulkTarget, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        var canApply = target.HasValue || bulkQuality.HasValue;
        UiLayout.SameLineIfFits("Apply");
        ImGui.BeginDisabled(!canApply);
        if (ImGui.Button("Apply##RQBulk"))
        {
            var applyQuality = bulkQuality;
            if (MutateRules(owner, plan.Id, selectedIds, rule =>
                {
                    if (target is { } value)
                        rule.TargetQuantity = Math.Max(0, value);
                    if (applyQuality is { } q)
                        rule.Quality = q;
                }))
            {
                bulkTarget = string.Empty;
                bulkQuality = null;
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Remove##RQBulk"))
            RemoveRules(owner, plan.Id, selectedIds);
        ImGui.SameLine();
        if (ImGui.Button("Clear##RQBulk"))
        {
            selection.Clear();
            bulkTarget = string.Empty;
            bulkQuality = null;
        }
    }

    private static void SameLineIfRoom(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine();
    }

    private void DrawUndoStrip(OwnerScope owner, StowagePlan plan)
    {
        if (lastRemoved is not { } removed || removed.PlanId != plan.Id)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(removed.Label);
        ImGui.SameLine(0, 0);
        ImGui.TextDisabled(" · ");
        ImGui.SameLine(0, 0);
        if (ImGui.SmallButton("Undo##RQTransferUndo"))
            UndoRemoval(owner, removed);
        ImGui.SameLine();
        if (ImGui.SmallButton("x##RQTransferUndoDismiss"))
            lastRemoved = null;
    }

    // ---- Table ----------------------------------------------------------------------------

    private void DrawTable(QuartermasterRuntimeSnapshot runtime, StowagePlan plan, TransferWorkbenchProjection projection)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        renderedTransferRowCount = 0;
        if (!transferWorkbenchTable.Begin(
                "RQTransferWorkbenchV4",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags,
                    FreezeRows: 1)))
            return;

        IReadOnlyList<TransferWorkbenchRow> rows;
        unsafe
        {
            rows = transferWorkbenchTable.Apply(projection.Rows, ImGui.TableGetSortSpecs());
        }

        var focusPending = focusTargetRuleId is { } focusId && rows.Any(row => row.Rule.Id == focusId);
        if (focusTargetRuleId is not null && !focusPending && ++focusTargetFrames > FocusGiveUpFrames)
            focusTargetRuleId = null;
        if (focusPending)
        {
            // Draw every row this frame so the new line's target input exists to take focus.
            for (var index = 0; index < rows.Count; index++)
                DrawRow(rows, index);
            renderedTransferRowCount = rows.Count;
        }
        else
        {
            renderedTransferRowCount = transferWorkbenchTable.DrawClippedRows(rows, (_, index) => DrawRow(rows, index));
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.TableNextColumn())
            DrawAddInput(runtime, plan);
        transferWorkbenchTable.End();
    }

    private void DrawRow(IReadOnlyList<TransferWorkbenchRow> rows, int index)
    {
        var row = rows[index];
        ImGui.PushID(row.Rule.Id.ToString());
        transferWorkbenchTable.DrawSaneRow(
            rows,
            index,
            $"transfer:{row.Rule.Id}",
            row.Rule.Enabled ? null : PausedRowColor,
            selectable: false);
        ImGui.PopID();
    }

    private void DrawSelectCell(TransferWorkbenchRow row)
    {
        var selected = selection.IsSelected(row.Rule.Id);
        if (ImGui.Checkbox("##select", ref selected))
            selection.SetSelected(row.Rule.Id, selected);
    }

    private void DrawItemCell(TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(rule.ItemName);
        if (rule.Quality != ItemQualityPolicy.Any)
            Tag(QualityLabel(rule.Quality));
        var routeSummary = TransferPresentation.RouteSummary(rule.Routing, row.Runtime.Retainers, row.Owner);
        if (routeSummary.Length > 0)
            Tag($"→ {routeSummary}");
        if (!string.IsNullOrWhiteSpace(rule.Notes))
        {
            Tag("note");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rule.Notes);
        }
        if (!rule.Enabled)
            Tag("paused");
        if (lineErrors.TryGetValue(rule.Id, out var error))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ErrorColor, error);
            ImGui.PopTextWrapPos();
        }

        static void Tag(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(text);
        }
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
        ImGui.SetNextItemWidth(Math.Min(66, Math.Max(32, ImGui.GetContentRegionAvail().X * .55f)));
        if (focusTargetRuleId == row.Rule.Id)
        {
            ImGui.SetScrollHereY(.5f);
            ImGui.SetKeyboardFocusHere();
            focusTargetRuleId = null;
        }
        if (DalamudGroupedIntegerInput.InputGroupedInt("##target", ref target, 0, 0))
        {
            var independent = Math.Max(0, target - listingShortfall);
            MutateRules(row.Owner, row.PlanId, [row.Rule.Id], draftRule => draftRule.TargetQuantity = independent);
        }
        var targetHovered = ImGui.IsItemHovered();
        if (ImGui.GetContentRegionAvail().X > 52) ImGui.SameLine();
        if (!row.ListingContribution.IsKnown)
            ImGui.TextColored(AmberColor, "(+?)");
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
            return;
        var contribution = row.ListingContribution.IsKnown
            ? row.ListingContribution.Value.ToString("N0")
            : "?";
        ImGui.TextDisabled($"Plan +{contribution}");
        ImGui.TextDisabled($"Independent {row.Rule.TargetQuantity:N0}");
        ImGui.SameLine();
        if (!ImGui.SmallButton("Unlink##transfer-listing"))
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

    private static string OutcomeText(TransferWorkbenchRow row)
    {
        var projection = row.Projection;
        return projection.Kind switch
        {
            TransferLineKind.Paused => "Paused",
            TransferLineKind.VerifyListing => "Verify listing shortfall",
            TransferLineKind.Balanced => "Balanced",
            TransferLineKind.Stow => string.Join(" · ", new[]
            {
                projection.Stow > 0 ? $"Stow {projection.Stow:N0}" : null,
                projection.NoRoom > 0 ? $"no room for {projection.NoRoom:N0}" : null,
                projection.KeptInBags > 0 ? $"stays in bags {projection.KeptInBags:N0}" : null,
            }.Where(part => part is not null)),
            _ => string.Join(" · ", new[]
            {
                projection.FromSaddlebag > 0 ? $"Saddlebag {projection.FromSaddlebag:N0}" : null,
                projection.FromRetainers > 0 ? $"Retainers {projection.FromRetainers:N0}" : null,
                projection.Vendor > 0 ? $"Vendor {projection.Vendor:N0}" : null,
                projection.Short > 0 ? $"{projection.Short:N0} short" : null,
            }.Where(part => part is not null)),
        };
    }

    private void DrawOutcome(TransferWorkbenchRow row)
    {
        var projection = row.Projection;
        ImGui.AlignTextToFramePadding();
        var first = true;
        void Part(string text, Vector4 color)
        {
            if (!first)
            {
                ImGui.SameLine(0, 0);
                ImGui.TextDisabled(" · ");
                ImGui.SameLine(0, 0);
            }
            first = false;
            ImGui.TextColored(color, text);
        }

        var retrieveColor = TransferPresentation.ActionColor(StowageAction.Retrieve);
        switch (projection.Kind)
        {
            case TransferLineKind.Paused:
                ImGui.TextDisabled("Paused");
                return;
            case TransferLineKind.VerifyListing:
                ImGui.TextColored(AmberColor, "Verify listing shortfall");
                return;
            case TransferLineKind.Balanced:
                ImGui.TextColored(TransferPresentation.ActionColor(null), "Balanced");
                return;
            case TransferLineKind.Stow:
                if (projection.Stow > 0)
                    Part($"Stow {projection.Stow:N0}", TransferPresentation.ActionColor(StowageAction.Deposit));
                if (projection.NoRoom > 0)
                    Part($"no room for {projection.NoRoom:N0}", ErrorColor);
                if (projection.KeptInBags > 0)
                    Part($"stays in bags {projection.KeptInBags:N0}", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                return;
        }

        if (projection.FromSaddlebag > 0)
            Part($"Saddlebag {projection.FromSaddlebag:N0}", retrieveColor);
        if (projection.FromRetainers > 0)
            Part($"Retainers {projection.FromRetainers:N0}", retrieveColor);
        if (projection.Short > 0)
            Part($"{projection.Short:N0} short", ErrorColor);
        if (projection.StockUnverified)
            Part("stock unverified", AmberColor);
        DrawVendorChip(row, ref first);
    }

    private void DrawVendorChip(TransferWorkbenchRow row, ref bool first)
    {
        var projection = row.Projection;
        if (projection.Kind != TransferLineKind.Retrieve ||
            projection.Missing <= 0 ||
            projection.StockUnverified ||
            row.Rule.Quality != ItemQualityPolicy.Any)
            return;
        var allowed = row.Rule.AllowVendorPurchase;
        var line = (allowed ? row.VendorLine : null) ?? row.VendorPreview;
        if (line is null || line.Candidates.Count == 0)
            return;
        var candidate = line.SelectedCandidate ?? line.Candidates[0];
        var quantity = allowed && projection.Vendor > 0 ? projection.Vendor : projection.Missing;

        if (!first)
            ImGui.SameLine();
        first = false;
        if (allowed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.55f, .40f, .16f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.65f, .48f, .2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.72f, .54f, .24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, .94f, .82f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, VendorColor);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.3f, .24f, .12f, .6f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.4f, .3f, .14f, .8f));
            ImGui.PushStyleColor(ImGuiCol.Text, VendorColor);
            ImGui.PushStyleColor(ImGuiCol.Border, VendorColor);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        var clicked = ImGui.SmallButton($"Vendor {quantity:N0} · {candidate.Offer.UnitPriceGil:N0} ea##vendor-chip");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(line.Message))
            ImGui.SetTooltip(line.Message);
        if (clicked)
            MutateRules(row.Owner, row.PlanId, [row.Rule.Id], rule =>
            {
                rule.AllowVendorPurchase = !allowed;
                if (allowed)
                    rule.PreferredVendorNpcId = null;
            });
    }

    private void DrawLineMenu(TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        if (ImGui.SmallButton("…##line-menu"))
        {
            noteRuleId = rule.Id;
            noteBuffer = rule.Notes;
            ImGui.OpenPopup("line-menu");
        }
        DrawHoverRemove(row);
        if (!ImGui.BeginPopup("line-menu"))
            return;
        Guid[] ids = [rule.Id];
        ImGui.Dummy(new Vector2(280, 0));

        ImGui.TextDisabled("Quality");
        var firstQuality = true;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
        {
            if (!firstQuality)
                ImGui.SameLine();
            firstQuality = false;
            if (ImGui.RadioButton(QualityLabel(quality), rule.Quality == quality) && rule.Quality != quality)
                MutateRules(row.Owner, row.PlanId, ids, draftRule => draftRule.Quality = quality);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Vendor");
        var vendorSupported = rule.Quality == ItemQualityPolicy.Any;
        ImGui.BeginDisabled(!vendorSupported);
        if (ImGui.Selectable("Retainers only", !rule.AllowVendorPurchase, ImGuiSelectableFlags.DontClosePopups))
            MutateRules(row.Owner, row.PlanId, ids, draftRule =>
            {
                draftRule.AllowVendorPurchase = false;
                draftRule.PreferredVendorNpcId = null;
            });
        if (ImGui.Selectable(
                "Cheapest reachable",
                rule.AllowVendorPurchase && rule.PreferredVendorNpcId is null,
                ImGuiSelectableFlags.DontClosePopups))
            MutateRules(row.Owner, row.PlanId, ids, draftRule =>
            {
                draftRule.AllowVendorPurchase = true;
                draftRule.PreferredVendorNpcId = null;
            });
        foreach (var offer in vendorCatalogSource?.Invoke().FindOffers(rule.ItemId) ?? [])
        {
            var npcId = offer.NpcId;
            if (ImGui.Selectable(
                    $"{offer.NpcName} · {offer.UnitPriceGil:N0} ea##offer:{offer.NpcId}:{offer.ShopId}:{offer.ShopRowIndex}",
                    rule.AllowVendorPurchase && rule.PreferredVendorNpcId == npcId,
                    ImGuiSelectableFlags.DontClosePopups))
                MutateRules(row.Owner, row.PlanId, ids, draftRule =>
                {
                    draftRule.AllowVendorPurchase = true;
                    draftRule.PreferredVendorNpcId = npcId;
                });
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        var summary = TransferPresentation.RouteSummary(rule.Routing, row.Runtime.Retainers, row.Owner);
        if (ImGui.Selectable(summary.Length == 0 ? "Placement" : $"Placement · {summary}"))
            OpenPlacement(rule.Id);

        ImGui.Separator();
        ImGui.TextDisabled("Note");
        if (noteRuleId != rule.Id)
        {
            noteRuleId = rule.Id;
            noteBuffer = rule.Notes;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##note", ref noteBuffer, 160);
        if (ImGui.IsItemDeactivatedAfterEdit() && !string.Equals(noteBuffer.Trim(), rule.Notes, StringComparison.Ordinal))
        {
            var note = noteBuffer.Trim();
            MutateRules(row.Owner, row.PlanId, ids, draftRule => draftRule.Notes = note);
        }

        ImGui.Separator();
        var enabled = rule.Enabled;
        if (ImGui.Selectable(enabled ? "Pause line" : "Resume line"))
            MutateRules(row.Owner, row.PlanId, ids, draftRule => draftRule.Enabled = !enabled);
        if (ImGui.Selectable("Remove"))
            RemoveRules(row.Owner, row.PlanId, ids);
        ImGui.EndPopup();
    }

    private void DrawHoverRemove(TransferWorkbenchRow row)
    {
        // The line menu button was just drawn in this row; its rect is the row's vertical extent.
        var padding = ImGui.GetStyle().CellPadding.Y;
        var rowTop = ImGui.GetItemRectMin().Y - padding;
        var rowBottom = ImGui.GetItemRectMax().Y + padding;
        ImGui.SameLine();
        var mouseY = ImGui.GetMousePos().Y;
        var rowHovered = ImGui.IsWindowHovered() && mouseY >= rowTop && mouseY < rowBottom;
        var ruleId = row.Rule.Id;
        var owner = row.Owner;
        var planId = row.PlanId;
        if (rowHovered)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            var clicked = ImGui.SmallButton("×##remove-line");
            ImGui.PopStyleColor(2);
            if (clicked)
                RemoveRules(owner, planId, [ruleId]);
        }
        else
        {
            ImGui.Dummy(new Vector2(
                ImGui.CalcTextSize("×").X + (ImGui.GetStyle().FramePadding.X * 2f),
                ImGui.GetTextLineHeight()));
        }
        reviewRegistry.RegisterLastButton(
            $"quartermaster.transfer.line.remove.{ruleId:N}",
            $"Remove {row.Rule.ItemName} from the plan",
            true,
            () => RemoveRules(owner, planId, [ruleId]),
            "Available");
    }

    // ---- Add path -------------------------------------------------------------------------

    private void DrawAddInput(QuartermasterRuntimeSnapshot runtime, StowagePlan plan)
    {
        ImGui.SetNextItemWidth(-1);
        var entered = ImGui.InputTextWithHint(
            "##RQPlanAddItem",
            "+ Add item, @group or #category…",
            ref addText,
            96,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var active = ImGui.IsItemActive();
        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        var suggestions = AddSuggestions(runtime.State, addText);
        if (entered && suggestions.Count > 0)
        {
            AddSuggestionToPlan(runtime, plan, suggestions[0]);
            suggestions = [];
        }
        else if (active && suggestions.Count > 0)
        {
            ImGui.OpenPopup(AddSuggestionsPopup);
        }

        ImGui.SetNextWindowPos(new Vector2(inputMin.X, inputMax.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(Math.Clamp(inputMax.X - inputMin.X, 240f, 620f), 0),
            new Vector2(620f, 360f));
        if (!ImGui.BeginPopup(AddSuggestionsPopup, SuggestionWindowFlags))
            return;
        if (suggestions.Count == 0)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        for (var index = 0; index < suggestions.Count; index++)
        {
            if (!ImGui.Selectable($"{suggestions[index].Label}##add-suggestion:{index}", index == 0))
                continue;
            AddSuggestionToPlan(runtime, plan, suggestions[index]);
            ImGui.CloseCurrentPopup();
            break;
        }
        ImGui.EndPopup();
    }

    private IReadOnlyList<AddSuggestion> AddSuggestions(QuartermasterState document, string text)
    {
        var needle = text.Trim();
        if (needle.Length == 0)
            return [];
        var groups = ItemGroupCatalog.All(document);
        var catalog = gameCatalog();
        if (needle.StartsWith('#'))
        {
            var categoryNeedle = needle[1..].Trim();
            return catalog?.Categories
                .Where(category => categoryNeedle.Length == 0 || category.Name.Contains(categoryNeedle, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(CategorySuggestion)
                .ToArray() ?? [];
        }
        if (needle.StartsWith('>'))
        {
            var (craftNeedle, count) = CraftQuery.Parse(needle[1..]);
            if (craftNeedle.Length == 0 || catalog is null)
                return [];
            return catalog.Crafts
                .Where(craft => craft.Item.Name.Contains(craftNeedle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(craft => craft.Item.Name.StartsWith(craftNeedle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(craft => craft.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(craft => new AddSuggestion(
                    $">{craft.Item.Name} · {craft.Materials.Count:N0} material{(craft.Materials.Count == 1 ? string.Empty : "s")}",
                    null,
                    null,
                    Craft: craft,
                    CraftCount: count))
                .ToArray();
        }
        if (needle.StartsWith('@'))
        {
            var groupNeedle = needle[1..].Trim();
            return groups
                .Where(group => groupNeedle.Length == 0 || group.Name.Contains(groupNeedle, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(GroupSuggestion)
                .ToArray();
        }
        if (needle.Length < 2)
            return [];
        if (!string.Equals(needle, cachedItemSearch, StringComparison.Ordinal))
        {
            cachedItemSearch = needle;
            cachedItemChoices = searchItems(needle, 8);
        }
        return cachedItemChoices
            .Select(choice => new AddSuggestion(choice.Label, choice, null))
            .Concat(catalog?.Categories
                .Where(category => category.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .Select(CategorySuggestion) ?? [])
            .Concat(groups
                .Where(group => group.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .Select(GroupSuggestion))
            .ToArray();

        static AddSuggestion GroupSuggestion(ItemGroup group) =>
            new($"@{group.Name} · {group.Items.Count:N0} item{(group.Items.Count == 1 ? string.Empty : "s")}", null, group);

        static AddSuggestion CategorySuggestion(GameCatalogCategory category) =>
            new($"#{category.Name} · {category.Items.Count:N0} item{(category.Items.Count == 1 ? string.Empty : "s")}", null, null, category);
    }

    private void AddSuggestionToPlan(QuartermasterRuntimeSnapshot runtime, StowagePlan plan, AddSuggestion suggestion)
    {
        addText = string.Empty;
        if (suggestion.Item is { } item)
        {
            var added = AddLines(runtime, plan.Id, [new PlanAddEntry(item.ItemId, item.Name, ItemQualityPolicy.Any)], selectAffected: false);
            if (added.Count > 0)
                RequestTargetFocus(added[0]);
            return;
        }
        PlanAddEntry[] entries = suggestion switch
        {
            { Group: { } group } => group.Items.Select(member => new PlanAddEntry(member.ItemId, member.ItemName, member.Quality)).ToArray(),
            { Category: { } category } => category.Items.Select(member => new PlanAddEntry(member.ItemId, member.Name, ItemQualityPolicy.Any)).ToArray(),
            { Craft: { } craft } => MaterialEntries(craft.Materials, suggestion.CraftCount),
            _ => [],
        };
        if (entries.Length > 0)
            AddLines(runtime, plan.Id, entries, selectAffected: true);
    }

    private void RequestTargetFocus(Guid ruleId)
    {
        focusTargetRuleId = ruleId;
        focusTargetFrames = 0;
    }

    // ---- Mutations ------------------------------------------------------------------------

    private bool MutateRules(
        OwnerScope owner,
        Guid planId,
        IReadOnlyCollection<Guid> ruleIds,
        Action<TargetPlanItem> update)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                foreach (var rule in draft.Rules.Where(rule => ruleIds.Contains(rule.Id)))
                    update(rule);
                var duplicate = draft.Rules
                    .GroupBy(rule => (rule.ItemId, rule.Quality))
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                    throw new InvalidOperationException(
                        $"{duplicate.First().ItemName} ({QualityLabel(duplicate.Key.Quality)}) is already in this plan.");
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            foreach (var id in ruleIds)
                lineErrors.Remove(id);
            transferExecution.ClearInlineError();
            return true;
        }
        catch (Exception exception)
        {
            if (ruleIds.Count == 1)
                lineErrors[ruleIds.First()] = exception.Message;
            else
                transferExecution.ReportInlineError(exception.Message);
            return false;
        }
    }

    private void RemoveRules(OwnerScope owner, Guid planId, IReadOnlyCollection<Guid> ruleIds)
    {
        if (ruleIds.Count == 0)
            return;
        try
        {
            RemovedLines? removed = null;
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                var rules = draft.Rules
                    .Where(rule => ruleIds.Contains(rule.Id))
                    .Select(rule => StowagePlanCatalog.CopyRule(rule, planId, false))
                    .ToArray();
                var linksBefore = document.TransferPlanListingLinks
                    .Where(link => link.StowagePlanId == planId)
                    .ToArray();
                draft.Rules.RemoveAll(rule => ruleIds.Contains(rule.Id));
                var plan = StowagePlanCatalog.Apply(document, owner, draft);
                var dropped = linksBefore
                    .Where(link => !document.TransferPlanListingLinks.Contains(link))
                    .Select(CopyLink)
                    .ToArray();
                var label = rules.Length == 1
                    ? $"Removed {rules[0].ItemName}"
                    : $"Removed {rules.Length:N0} lines";
                removed = new RemovedLines(planId, label, rules, dropped);
                return plan.Id;
            });
            lastRemoved = removed;
            foreach (var id in ruleIds)
            {
                selection.SetSelected(id, false);
                lineErrors.Remove(id);
            }
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private void UndoRemoval(OwnerScope owner, RemovedLines removed)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, removed.PlanId);
                foreach (var rule in removed.Rules)
                {
                    if (!draft.Rules.Any(existing =>
                            existing.Id == rule.Id ||
                            (existing.ItemId == rule.ItemId && existing.Quality == rule.Quality)))
                        draft.Rules.Add(StowagePlanCatalog.CopyRule(rule, removed.PlanId, false));
                }
                var plan = StowagePlanCatalog.Apply(document, owner, draft);
                foreach (var link in removed.Links)
                {
                    if (document.TransferPlanListingLinks.Any(existing => existing.Id == link.Id))
                        continue;
                    if (document.PlanItems.Any(rule => rule.StowagePlanId == plan.Id && rule.Enabled &&
                                                       rule.ItemId == link.ItemId && rule.Quality == link.Quality))
                        document.TransferPlanListingLinks.Add(CopyLink(link));
                }
                return plan.Id;
            });
            lastRemoved = null;
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private static TransferPlanListingLink CopyLink(TransferPlanListingLink link) => new()
    {
        Id = link.Id,
        StowagePlanId = link.StowagePlanId,
        ListingPlanId = link.ListingPlanId,
        ItemId = link.ItemId,
        Quality = link.Quality,
    };

    // ---- Notices / vendor run -------------------------------------------------------------

    private void DrawVendorRunStatus()
    {
        var run = vendorProcurement.ActiveRun;
        if (run is null || ReferenceEquals(run, dismissedVendorRun))
            return;

        ImGui.TextUnformatted($"Vendor buy · {run.Phase}");
        if (!vendorProcurement.IsRunning && run.Phase != GilVendorBuyPhase.Paused)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss##vendor-result")) dismissedVendorRun = run;
        }
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

        if (ShowVendorMessage(run.Phase, run.Message))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(run.Message);
            ImGui.PopStyleColor();
        }
        if (!string.IsNullOrWhiteSpace(vendorStatus))
            DrawWrappedStatus(vendorStatus, ErrorColor);
        if (!string.IsNullOrWhiteSpace(vendorProcurement.CoordinationWarning))
            DrawWrappedStatus(vendorProcurement.CoordinationWarning, ListingWorkspace.Color(ListingStateTone.Info));
    }

    internal static bool ShowVendorMessage(GilVendorBuyPhase phase, string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        !(phase == GilVendorBuyPhase.Completed && string.Equals(message.Trim(), "Vendor buy completed.", StringComparison.OrdinalIgnoreCase));

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
            : ErrorColor;
        var background = isProgress
            ? new Vector4(.06f, .13f, .17f, .92f)
            : new Vector4(.17f, .08f, .08f, .92f);

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

    // ---- Projection -----------------------------------------------------------------------

    public TransferWorkbenchProjection ResolveProjection(
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
        // What each line would get from a vendor if buying were on: drives the vendor chip
        // with the same planner, catalog, and access eligibility as the real review.
        var vendorPreview = vendorProcurement.BuildReview(
            runtime,
            plan,
            effectiveRules.Select(rule =>
            {
                var copy = StowagePlanCatalog.CopyRule(rule, plan.Id, false);
                copy.AllowVendorPurchase = true;
                return copy;
            }).ToArray(),
            retrieval);
        var deposit = TransferPlanEvaluation.BuildSurplusBatch(runtime, stowage);
        var evaluated = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var retrievalLines = retrieval.Lines.ToDictionary(line => line.PlanItemId);
        var vendorLines = vendor.Lines.ToDictionary(line => line.RuleId);
        var vendorPreviewLines = vendorPreview.Lines.ToDictionary(line => line.RuleId);
        var rows = rules
            .Select(rule =>
            {
                evaluated.TryGetValue(rule.Id, out var line);
                retrievalLines.TryGetValue(rule.Id, out var retrievalLine);
                vendorLines.TryGetValue(rule.Id, out var vendorLine);
                vendorPreviewLines.TryGetValue(rule.Id, out var vendorPreviewLine);
                var routedDepositQuantity = deposit.Routes
                    .Where(route => route.Request.SourceRuleId == rule.Id)
                    .Sum(route => route.RoutedQuantity);
                var stock = runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId);
                var playerQuantity = StowageEvaluator.PlayerQuantity(rule, stock);
                var accessibleStorageQuantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    stock,
                    rule.Quality,
                    runtime.Retainers,
                    runtime.Owner);
                listingContributions.TryGetValue((rule.ItemId, rule.Quality), out var listingContribution);
                var listingQuantity = listingContribution?.Quantity ?? Evidence.Known(0);
                var difference = (line?.DesiredPlayerQuantity ?? rule.TargetQuantity) - playerQuantity;
                var lineProjection = TransferWorkbenchPresentation.Project(
                    rule.Enabled,
                    listingQuantity.IsKnown,
                    line?.Action ?? StowageAction.None,
                    Math.Abs(difference),
                    accessibleStorageQuantity,
                    routedDepositQuantity,
                    line?.SaddlebagQuantity ?? 0,
                    vendorLine?.IsReady == true ? vendorLine.ApprovedQuantity : 0,
                    retrievalLine?.Status == PlanLineStatus.StockUnverified,
                    rule.Routing.AnySpace || rule.Routing.PreferredRetainerIds.Count > 0);
                return new TransferWorkbenchRow(
                    rule,
                    line,
                    retrievalLine,
                    vendorLine,
                    routedDepositQuantity,
                    playerQuantity,
                    accessibleStorageQuantity,
                    difference,
                    listingQuantity,
                    listingContribution?.Link,
                    runtime.Owner,
                    plan.Id,
                    runtime,
                    lineProjection,
                    vendorPreviewLine);
            })
            .ToArray();
        // Count lines that will actually move something, from the same projection the rows show.
        var movements = rows.Count(row => row.Projection.Retrieve > 0 || row.Projection.Stow > 0);
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

    public void RequestSelectedTransferReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        requestTransferReview(plan.Id, plan.Name);
    }

    public void RequestSelectedVendorReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        requestVendorReview(ResolveProjection(current, plan).Vendor);
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };

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
}
