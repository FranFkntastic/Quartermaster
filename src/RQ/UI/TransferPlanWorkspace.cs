using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.UI.Feedback;
using Franthropy.Dalamud.UI.Inputs;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Courier;
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
    private readonly Action<Guid?> managePlans;
    private readonly CourierSession courier;
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private readonly DalamudTableProjection<TransferWorkbenchRow> courierWorkbenchTable;
    private DalamudTableProjection<TransferWorkbenchRow>? drawingTable;
    private readonly CourierPlacementMatrix courierPlacement;
    /// <summary>The destination character the Courier Placement view shows; null picks the first targeted one.</summary>
    private OwnerScope? placementDestination;
    /// <summary>A destination picked from Courier Placement's + menu; it keeps the plan's other lines listed until another plan opens.</summary>
    private OwnerScope? placementAddedDestination;
    private readonly TableSelectionModel<Guid> selection = new();
    private Guid[]? drawingRowKeys;
    private IReadOnlyList<TransferWorkbenchRow>? drawingRowKeysSource;
    private int drawingRowIndex;
    private bool drawingRows;
    private TransferWorkbenchProjection? retainedProjection;
    private int retainedSelectionCount = -1;
    private readonly Dictionary<Guid, string> lineErrors = [];
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private int transferProjectionBuildCount;
    private int renderedTransferRowCount;
    private string? dismissedVendorRunId;
    private string vendorStatus = string.Empty;

    private Guid? renamingPlanId;
    private string renameText = string.Empty;
    private bool renameFocusRequested;

    private string bulkTarget = string.Empty;
    private ItemQualityPolicy? bulkQuality;
    private readonly PlacementMatrix<Guid> placement;
    private bool showPlacement;

    private string addText = string.Empty;
    private bool focusAddInput;
    private readonly SearchReference.Button addHelp = new("quartermaster.plan.addHelp", "Plan add syntax");
    private string cachedItemSearch = string.Empty;
    private IReadOnlyList<ItemChoice> cachedItemChoices = [];

    private Guid? focusTargetRuleId;
    private int focusTargetFrames;
    /// <summary>A courier route chooser the agent bridge asked to open; it opens on the line's next draw.</summary>
    private (Guid RuleId, string Side)? pendingCourierPopup;
    private Guid? noteRuleId;
    private string noteBuffer = string.Empty;
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
        Action<Guid?> managePlans,
        CourierSession courier)
    {
        this.courier = courier;
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
        this.managePlans = managePlans;
        transferWorkbenchTable = CreateTable();
        courierWorkbenchTable = CreateCourierTable();
        placement = new PlacementMatrix<Guid>("quartermaster.transfer.placement", "Stow", reviewRegistry, allowConsolidation: true);
        courierPlacement = new CourierPlacementMatrix("quartermaster.courier.placement", reviewRegistry);
    }

    public TransferPlanWorkspaceSnapshot Snapshot() =>
        new(transferProjectionBuildCount, renderedTransferRowCount);

    public void ClearVendorStatus() => vendorStatus = string.Empty;

    /// <summary>Makes a plan the open Transfer Plan and shows the plan workspace.</summary>
    public void OpenPlan(Guid planId)
    {
        SwitchTo(planId);
        workbench.View = WorkbenchView.Stowage;
    }

    /// <summary>Makes a plan the open plan, dropping the previous plan's row selection and views.</summary>
    private void SwitchTo(Guid planId)
    {
        if (workbench.SelectedStowagePlanId != planId)
        {
            selection.Clear();
            renamingPlanId = null;
            showPlacement = false;
            placementDestination = null;
            placementAddedDestination = null;
        }
        workbench.SelectedStowagePlanId = planId;
    }

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    /// <summary>
    /// Keeps an owner's unsaved Transfer and Courier plans present and the selection pointing at one
    /// of the owner's plans.
    /// </summary>
    public void EnsureSelection(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        if (!owner.HasStableIdentity)
            return;
        var selectedId = workbench.SelectedStowagePlanId;
        var (hasScratches, selectedValid) = state.ReadConfiguration(document => (
            StowagePlanCatalog.FindScratch(document, owner) is not null &&
            StowagePlanCatalog.FindScratch(document, owner, StowagePlanKind.Courier) is not null,
            selectedId is { } id && document.StowagePlans.Any(plan => plan.Id == id && plan.Owner.Matches(owner))));
        if (!hasScratches)
            state.Mutate(document =>
            {
                StowagePlanCatalog.EnsureScratch(document, owner);
                StowagePlanCatalog.EnsureScratch(document, owner, StowagePlanKind.Courier);
            });
        if (!selectedValid)
            workbench.SelectedStowagePlanId = state.ReadConfiguration(document =>
                StowagePlanCatalog.FindScratch(document, owner)!.Id);
    }

    /// <summary>
    /// The plan an add of this kind goes to: the open plan when it is of that kind, otherwise the
    /// owner's unsaved plan of the kind, which becomes the open plan. Plans never mix kinds.
    /// </summary>
    private Guid SelectPlanOfKind(QuartermasterRuntimeSnapshot runtime, StowagePlanKind kind)
    {
        EnsureSelection(runtime);
        var owner = runtime.Owner;
        var selectedId = workbench.SelectedStowagePlanId!.Value;
        var planId = state.ReadConfiguration(document =>
            document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))?.Kind == kind
                ? selectedId
                : StowagePlanCatalog.FindScratch(document, owner, kind)?.Id)
            ?? state.Mutate(document => StowagePlanCatalog.EnsureScratch(document, owner, kind).Id);
        SwitchTo(planId);
        return planId;
    }

    /// <summary>Adds stock-browser selections to the open Transfer plan, or the unsaved Transfer plan.</summary>
    public void AddStock(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items, bool stow, ulong? retainerId = null)
    {
        if (!runtime.Owner.HasStableIdentity)
            return;
        var planId = SelectPlanOfKind(runtime, StowagePlanKind.Transfer);
        state.Mutate(document =>
        {
            var draft = StowagePlanCatalog.Draft(document, runtime.Owner, planId);
            foreach (var item in items)
                StowagePlanCatalog.SetStockRule(draft, item.ItemId, item.ItemName, item.PlayerQuantity, stow, retainerId);
            StowagePlanCatalog.Apply(document, runtime.Owner, draft);
        });
    }

    /// <summary>
    /// Adds courier lines that send a character's units of each item (one line per container
    /// holding it) to another character's retainer or bags. The source is the logged-in character
    /// unless <paramref name="source"/> names another. Re-sending from the same container to the
    /// same place replaces that line's quantity. The lines go to the open Courier plan, or the
    /// unsaved Courier plan when a Transfer plan is open.
    /// </summary>
    public void AddCourier(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items, OwnerScope destination, ulong? retainerId, OwnerScope? source = null)
    {
        if (!runtime.Owner.HasStableIdentity || items.Count == 0)
            return;
        var target = destination with { };
        var sourceCharacter = source is null || source.Matches(runtime.Owner) ? null : source with { };
        var holder = sourceCharacter ?? runtime.Owner;
        var sources = items
            .SelectMany(item => item.Stacks
                .Where(stack => stack.Quantity > 0 && stack.ScopeKind switch
                {
                    BrowserScopeKind.Player => CourierSession.PlayerBags.Contains(stack.Storage, StringComparer.Ordinal),
                    BrowserScopeKind.Retainer => stack.RetainerId is { } id && runtime.Retainers.GetValueOrDefault(id)?.Owner.Matches(holder) == true,
                    _ => false,
                })
                .GroupBy(stack => stack.ScopeKind == BrowserScopeKind.Player ? null : stack.RetainerId)
                .Select(group => (item.ItemId, item.ItemName, Source: group.Key, Units: group.Sum(stack => stack.Quantity))))
            .ToArray();
        if (sources.Length == 0)
            return;
        var planId = SelectPlanOfKind(runtime, StowagePlanKind.Courier);
        try
        {
            var affected = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, runtime.Owner, planId);
                var ids = new List<Guid>();
                foreach (var (itemId, name, container, units) in sources)
                {
                    var rule = draft.Rules.FirstOrDefault(candidate => candidate.ItemId == itemId && candidate.Courier is { } route &&
                        (route.SourceCharacter ?? runtime.Owner).Matches(holder) && route.SourceRetainerId == container &&
                        route.Character.Matches(target) && route.RetainerId == retainerId);
                    if (rule is null)
                    {
                        rule = new TargetPlanItem
                        {
                            StowagePlanId = planId,
                            ItemId = itemId,
                            ItemName = name,
                            Courier = new CourierRoute(target, retainerId, container, sourceCharacter),
                        };
                        draft.Rules.Add(rule);
                    }
                    rule.TargetQuantity = units;
                    rule.Enabled = true;
                    ids.Add(rule.Id);
                }
                StowagePlanCatalog.Apply(document, runtime.Owner, draft);
                return ids;
            });
            workbench.SelectedStowagePlanId = planId;
            showPlacement = false;
            selection.Clear();
            pendingSelection = affected.Count > 1 ? affected.ToArray() : null;
            pendingSelectionFrames = 0;
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
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
        AddLines(runtime, SelectPlanOfKind(runtime, StowagePlanKind.Transfer), entries, selectAffected: true);
    }

    /// <summary>
    /// Adds courier lines from a Courier plan's own add box. Each line starts from the plan's last-used
    /// source character (else the logged-in one): its bags when they hold the item, else its first
    /// retainer that does. It goes to the plan's last-used destination character (else the first other
    /// character on the account), into bags; Placement picks the retainer. Send starts at the units the
    /// source holds (at least one) unless the entry names a quantity. A line already on that route is reused.
    /// </summary>
    private IReadOnlyList<Guid> AddCourierLines(
        QuartermasterRuntimeSnapshot runtime,
        Guid planId,
        IReadOnlyList<PlanAddEntry> entries,
        bool selectAffected)
    {
        var owner = runtime.Owner;
        var lastRoute = runtime.State.PlanItems.LastOrDefault(rule => rule.StowagePlanId == planId && rule.Courier is not null)?.Courier;
        var source = lastRoute?.SourceCharacter is { } lastSource && courier.Characters(runtime).Any(known => known.Matches(lastSource))
            ? lastSource
            : owner;
        var destinations = courier.Destinations(runtime, source).ToArray();
        var destination = lastRoute?.Character is { } lastDestination && destinations.FirstOrDefault(known => known.Matches(lastDestination)) is { } kept
            ? kept
            : destinations.FirstOrDefault();
        if (destination is null)
        {
            transferExecution.ReportInlineError("No other character on this account to send to.");
            return [];
        }
        var sourceCharacter = source.Matches(owner) ? null : source with { };
        try
        {
            var affected = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                var ids = new List<Guid>();
                foreach (var entry in entries)
                {
                    var container = CourierSourceContainer(runtime, source, entry.ItemId, entry.Quality);
                    var candidate = new TargetPlanItem
                    {
                        StowagePlanId = planId,
                        ItemId = entry.ItemId,
                        ItemName = entry.Name,
                        Quality = entry.Quality,
                        Courier = new CourierRoute(destination with { }, null, container, sourceCharacter),
                    };
                    var key = StowagePlanCatalog.LineKey(candidate);
                    var rule = draft.Rules.FirstOrDefault(existing => StowagePlanCatalog.LineKey(existing) == key);
                    if (rule is null)
                    {
                        var held = courier.Available(runtime, source, container, entry.ItemId, entry.Quality) ?? 0;
                        candidate.TargetQuantity = Math.Max(1, entry.Quantity ?? held);
                        draft.Rules.Add(rule = candidate);
                    }
                    else if (entry.Quantity is { } quantity)
                    {
                        rule.TargetQuantity = (int)Math.Min(int.MaxValue, (long)rule.TargetQuantity + Math.Max(0, quantity));
                    }
                    rule.Enabled = true;
                    if (!ids.Contains(rule.Id))
                        ids.Add(rule.Id);
                }
                StowagePlanCatalog.Apply(document, owner, draft);
                return ids;
            });
            workbench.SelectedStowagePlanId = planId;
            showPlacement = false;
            if (selectAffected)
            {
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

    /// <summary>Where a character holds an item: bags when they hold it, else the first retainer in game order that does, else bags.</summary>
    private ulong? CourierSourceContainer(QuartermasterRuntimeSnapshot runtime, OwnerScope source, uint itemId, ItemQualityPolicy quality)
    {
        if (courier.Available(runtime, source, null, itemId, quality) is > 0)
            return null;
        return CourierSession.Retainers(runtime, source)
            .FirstOrDefault(retainer => courier.Available(runtime, source, retainer.RetainerId, itemId, quality) is > 0)?.RetainerId;
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
                    var existing = draft.Rules.FirstOrDefault(rule => rule.Courier is null && rule.ItemId == entry.ItemId && rule.Quality == entry.Quality) ??
                                   (entry.Quality == ItemQualityPolicy.Any
                                       ? draft.Rules.FirstOrDefault(rule => rule.Courier is null && rule.ItemId == entry.ItemId)
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
        var planId = SelectPlanOfKind(runtime, StowagePlanKind.Transfer);
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
                if (draft.Rules.Any(rule => rule.Courier is null && rule.ItemId == demand.ItemId && rule.Quality == ItemQualityPolicy.Any))
                    throw new InvalidOperationException("This item has an Any-quality target. Choose NQ or HQ on its line before preparing exact-quality listings.");
                var rule = draft.Rules.FirstOrDefault(candidate => candidate.Courier is null && candidate.ItemId == demand.ItemId && candidate.Quality == demand.Quality);
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
            Id: "player",
            SelectionTargetFraction: 1),
        new(
            "Target",
            118,
            TransferTargetText,
            row => row.ListingContribution.IsKnown ? row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity : row.Rule.TargetQuantity,
            Draw: WithRowSpace(DrawTransferTarget, "target"),
            Id: "target",
            HeaderTooltip: "Desired player quantity with its signed delta from current player stock."),
        new(
            "Outcome",
            1.3f,
            OutcomeText,
            OutcomeText,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: WithRowSpace(DrawOutcome, "outcome"),
            Id: "outcome"),
        new(
            "##line-menu",
            52,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize |
                   ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.NoHide,
            Draw: WithRowSpace(DrawLineMenu, "line-menu"),
            Id: "line-menu"),
    ], DalamudTableSelection<TransferWorkbenchRow>.Multi(selection, row => row.Rule.Id));

    /// <summary>A Courier plan's lines: units to send, where they leave from, where they land, and the run's verdict.</summary>
    private DalamudTableProjection<TransferWorkbenchRow> CreateCourierTable() => new(
    [
        new(
            "Item",
            1.3f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
            Draw: DrawItemCell,
            Id: "item"),
        new(
            "Send",
            84,
            row => row.Rule.TargetQuantity.ToString("N0"),
            row => row.Rule.TargetQuantity,
            Draw: WithRowSpace(DrawCourierQuantity, "send"),
            Id: "send"),
        new(
            "From",
            1f,
            CourierFromText,
            CourierFromText,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: WithRowSpace(DrawCourierFrom, "from"),
            Id: "from"),
        new(
            "To",
            1f,
            CourierToText,
            CourierToText,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: WithRowSpace(DrawCourierTo, "to"),
            Id: "to"),
        new(
            "Status",
            .8f,
            CourierStatusText,
            CourierStatusText,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: WithRowSpace(DrawCourierStatus, "status"),
            Id: "status"),
        new(
            "##line-menu",
            52,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize |
                   ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.NoHide,
            Draw: WithRowSpace(DrawLineMenu, "line-menu"),
            Id: "line-menu"),
    ], DalamudTableSelection<TransferWorkbenchRow>.Multi(selection, row => row.Rule.Id));

    /// <summary>
    /// Cells with widgets get their row-selection target after the widgets, in the space they leave.
    /// ImGui gives the hover to the first item submitted, so a target under a widget would take its clicks.
    /// </summary>
    private Action<TransferWorkbenchRow> WithRowSpace(Action<TransferWorkbenchRow> draw, string cell) => row =>
    {
        draw(row);
        if (!drawingRows || drawingRowKeys is not { } keys || (uint)drawingRowIndex >= (uint)keys.Length || keys[drawingRowIndex] != row.Rule.Id)
            return;
        ImGui.SameLine();
        var width = ImGui.GetContentRegionAvail().X;
        if (width >= 1f)
            DalamudTableSelectionRenderer.DrawRow(
                selection,
                keys,
                drawingRowIndex,
                $"row-space:{cell}",
                new Vector2(width, ImGui.GetFrameHeight()));
    };

    public void Draw(QuartermasterRuntimeSnapshot runtime, FrameTimings timings)
    {
        timings.Section("transfer projection");
        var owner = runtime.Owner;
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        var projection = selected is null ? null : ResolveProjection(runtime, selected);

        timings.Section("transfer controls");
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
            timings.Section("transfer placement");
            if (selected.Kind == StowagePlanKind.Courier)
                DrawCourierPlacement(runtime, owner, selected, projection);
            else
                DrawPlacement(runtime, owner, selected, projection);
            return;
        }
        ApplyPendingSelection(projection);
        // Pruning hashes every line, so only redo it when the lines or the selection changed.
        if (!ReferenceEquals(retainedProjection, projection) || retainedSelectionCount != selection.Count)
        {
            selection.Retain(projection.Rules.Select(rule => rule.Id));
            retainedProjection = projection;
            retainedSelectionCount = selection.Count;
        }
        if (selection.Count >= 2)
            DrawBulkBar(owner, selected, projection);
        else
            DrawSummary(runtime, projection);
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
        var rows = projection.Rows
            .Where(row => row.Rule.Courier is null)
            .Select(row => new PlacementMatrixRow<Guid>(
                row.Rule.Id,
                row.Rule.Id.ToString("N"),
                row.Rule.Quality == ItemQualityPolicy.Any ? row.Rule.ItemName : $"{row.Rule.ItemName} {QualityLabel(row.Rule.Quality)}",
                (row.Line?.DepositQuantity ?? 0).ToString("N0"),
                row.Rule.Routing,
                PlacementMatrixData.Holdings(runtime, row.Rule.ItemId, row.Rule.Quality),
                row.Rule.Routing.Consolidate ? GatherSummary(row.Line) : null))
            .ToArray();
        var columns = PlacementMatrixData.Columns(runtime, rows.Select(row => row.Routing));
        placement.Draw(rows, rows, columns, changes =>
        {
            var routing = changes.ToDictionary(change => change.Key, change => change.Routing);
            return MutateRules(owner, plan.Id, routing.Keys.ToArray(),
                rule => rule.Routing = routing[rule.Id].Copy());
        });
    }

    /// <summary>Opens Courier Placement on a destination character; null picks the plan's first.</summary>
    private void OpenCourierPlacement(OwnerScope? destination)
    {
        showPlacement = true;
        placementDestination = destination;
        placementAddedDestination = null;
    }

    /// <summary>The characters a plan's courier lines deliver to, in line order.</summary>
    private static IReadOnlyList<OwnerScope> CourierDestinations(TransferWorkbenchProjection projection) =>
        projection.Rules
            .Where(rule => rule.Courier is not null)
            .Select(rule => rule.Courier!.Character)
            .DistinctBy(character => (character.LocalContentId, character.HomeWorldId))
            .ToArray();

    /// <summary>
    /// Courier Placement: pick a destination character, then the container each line lands in there.
    /// A character added from the + menu also lists the plan's other lines, so they can be sent to it.
    /// </summary>
    private void DrawCourierPlacement(
        QuartermasterRuntimeSnapshot runtime,
        OwnerScope owner,
        StowagePlan plan,
        TransferWorkbenchProjection projection)
    {
        var targeted = CourierDestinations(projection);
        var destination = placementDestination is { } chosen &&
                          (targeted.Any(character => character.Matches(chosen)) || placementAddedDestination?.Matches(chosen) == true)
            ? chosen
            : targeted.FirstOrDefault() ?? placementAddedDestination;
        DrawDestinationBar(runtime, targeted, destination);
        if (destination is null)
            return;

        var added = placementAddedDestination?.Matches(destination) == true;
        var stock = destination.Matches(owner) ? null : courier.Stock(runtime, destination);
        var rows = projection.Rows
            .Where(row => row.Rule.Courier is { } route &&
                          (route.Character.Matches(destination) || (added && !(route.SourceCharacter ?? owner).Matches(destination))))
            .Select(row =>
            {
                var route = row.Rule.Courier!;
                var targets = route.Character.Matches(destination);
                return new CourierPlacementRow(
                    row.Rule.Id,
                    row.Rule.Quality == ItemQualityPolicy.Any ? row.Rule.ItemName : $"{row.Rule.ItemName} {QualityLabel(row.Rule.Quality)}",
                    row.Rule.TargetQuantity,
                    CourierSession.Side(runtime, route.SourceCharacter ?? owner, route.SourceRetainerId, includeOwner: true),
                    targets,
                    targets ? route.RetainerId : null,
                    PlacementMatrixData.BagHoldings(runtime, row.Rule.ItemId, row.Rule.Quality, destination, stock),
                    PlacementMatrixData.Holdings(runtime, row.Rule.ItemId, row.Rule.Quality, destination, stock),
                    targets ? null : CourierSession.Side(runtime, route.Character, route.RetainerId, includeOwner: true));
            })
            .ToArray();
        var referenced = rows.Where(row => row.Targeted && row.Assigned is not null).Select(row => row.Assigned!.Value);
        var columns = PlacementMatrixData.Columns(runtime, [], destination, referenced);
        var target = destination;
        courierPlacement.Draw(rows, columns, courier.FreeBagSlots(destination), CourierSession.CharacterLabel(destination),
            (ruleId, container) => MutateRules(owner, plan.Id, [ruleId],
                rule => rule.Courier = rule.Courier! with { Character = target with { }, RetainerId = container }));
        foreach (var row in rows)
            if (lineErrors.TryGetValue(row.Key, out var error))
                ImGui.TextColored(ErrorColor, $"{row.Label}: {error}");
    }

    /// <summary>One pill per destination character the plan delivers to, then + for any other known character.</summary>
    private void DrawDestinationBar(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<OwnerScope> targeted, OwnerScope? current)
    {
        var style = ImGui.GetStyle();
        var pills = placementAddedDestination is { } extra && !targeted.Any(character => character.Matches(extra))
            ? targeted.Append(extra).ToArray()
            : targeted;
        var first = true;
        foreach (var character in pills)
        {
            var label = $"{CourierSession.CharacterLabel(character)}##destination:{character.LocalContentId}";
            if (!first)
                UiLayout.SameLineIfFits(label);
            first = false;
            var selectedPill = current?.Matches(character) == true;
            if (selectedPill)
                ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonActive]);
            var pick = character;
            if (ImGui.Button(label))
                placementDestination = pick;
            if (selectedPill)
                ImGui.PopStyleColor();
            reviewRegistry.RegisterLastSelectable(
                $"quartermaster.courier.placement.destination.{character.LocalContentId}",
                $"Place lines going to {character.CharacterName}",
                true,
                selectedPill,
                () => placementDestination = pick,
                selectedPill ? "Selected" : "Available");
        }

        var others = courier.Characters(runtime)
            .Where(character => !CourierSession.IsCarrier(runtime, character) && !pills.Any(shown => shown.Matches(character)))
            .ToArray();
        if (!first)
            ImGui.SameLine();
        ImGui.BeginDisabled(others.Length == 0);
        if (ImGui.Button("+##RQCourierPlacementAdd"))
            ImGui.OpenPopup("RQCourierPlacementAdd");
        ImGui.EndDisabled();
        if (others.Length == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("No other known character");
        void Add(OwnerScope character)
        {
            placementAddedDestination = character;
            placementDestination = character;
        }
        foreach (var other in others)
        {
            var pick = other;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.courier.placement.destination.{other.LocalContentId}",
                $"Place lines going to {other.CharacterName}",
                true,
                () => Add(pick),
                "Available");
        }
        if (ImGui.BeginPopup("RQCourierPlacementAdd"))
        {
            foreach (var other in others)
                if (ImGui.Selectable($"{CourierSession.CharacterLabel(other)}##add:{other.LocalContentId}"))
                    Add(other);
            ImGui.EndPopup();
        }
        ImGui.Separator();
    }

    private static string GatherSummary(StowageEvaluationLine? line)
    {
        if (line is null) return "line paused";
        var parts = new List<string>();
        if (line.Moves.Count > 0)
            parts.Add($"gathers {line.MoveQuantity:N0} from {line.Moves.Select(m => m.SourceRetainerId).Distinct().Count():N0} retainers");
        parts.AddRange(line.MoveLimits.Select(l => l.Message));
        return parts.Count == 0 ? "nothing is elsewhere" : string.Join(" · ", parts);
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

        var isCourier = selected?.Kind == StowagePlanKind.Courier;
        var trips = projection?.Courier?.Trips.Length ?? 0;
        var executeLabel = isCourier
            ? trips > 0
                ? $"Execute courier run · {trips:N0} trip{(trips == 1 ? string.Empty : "s")}###RQTransferExecute"
                : "Execute courier run###RQTransferExecute"
            : projection is { Movements: > 0 }
                ? $"Execute plan · {projection.MovementSummary}###RQTransferExecute"
                : "Execute plan###RQTransferExecute";
        var vendor = isCourier ? null : projection?.Vendor;
        var maximumGil = vendor?.MaximumGil ?? 0;
        var buyLabel = maximumGil > 0
            ? $"Buy from vendor · {maximumGil:N0} gil###RQTransferVendorBuy"
            : "Buy from vendor###RQTransferVendorBuy";
        var lines = projection?.Rules.Count(rule => rule.Courier is null) ?? 0;
        var placed = projection?.Rules.Count(rule => rule.Courier is null && (rule.Routing.AnySpace || rule.Routing.PreferredRetainerIds.Count > 0)) ?? 0;
        var destinationCount = projection is null ? 0 : CourierDestinations(projection).Count;
        var placementLabel = showPlacement
            ? "Back to plan###RQTransferPlacement"
            : isCourier
                ? destinationCount > 0
                    ? $"Placement · {destinationCount:N0} destination{(destinationCount == 1 ? string.Empty : "s")}###RQTransferPlacement"
                    : "Placement###RQTransferPlacement"
                : placed > 0
                    ? $"Placement · {placed:N0} of {lines:N0} line{(lines == 1 ? string.Empty : "s")}###RQTransferPlacement"
                    : "Placement###RQTransferPlacement";
        const string menuLabel = "…##RQTransferPlanMenu";
        // A courier plan offers no Start while a run is active or unsettled; the run pane owns that state.
        CourierStartGate? courierGate = isCourier ? courier.StartGate(runtime, projection?.Courier) : null;
        var offerExecute = courierGate?.Offer != false;
        var trailing = ButtonWidth(menuLabel) + ButtonWidth(placementLabel) + (offerExecute ? ButtonWidth(executeLabel) : 0) +
                       (isCourier ? 0 : ButtonWidth(buyLabel)) + (style.ItemSpacing.X * 4f);
        var selectorWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - trailing);

        if (selected is not null && renamingPlanId == selected.Id)
            DrawRenameInput(owner, selected, selectorWidth);
        else
            DrawPlanSelector(runtime, selected, selectorWidth);

        ImGui.SameLine();
        DrawPlanMenu(runtime, selected, projection, menuLabel);

        var canPlace = selected is not null && projection is not null && (isCourier || lines > 0 || projection.Rules.Count == 0);
        UiLayout.SameLineIfFits(placementLabel);
        ImGui.BeginDisabled(!canPlace);
        var wasOpen = showPlacement;
        if (wasOpen) ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonActive]);
        if (ImGui.Button(placementLabel))
        {
            if (wasOpen) showPlacement = false;
            else if (isCourier) OpenCourierPlacement(null);
            else OpenPlacement(null);
        }
        if (wasOpen) ImGui.PopStyleColor();
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton("quartermaster.transfer.placement.open", "Edit placement for this plan's lines", canPlace,
            () =>
            {
                if (isCourier) OpenCourierPlacement(null);
                else OpenPlacement(null);
            },
            isCourier ? $"destinations={destinationCount:N0}" : $"lines={lines:N0} placed={placed:N0}");

        if (wasOpen)
            reviewRegistry.RegisterLastButton("quartermaster.transfer.placement.return", "Return to the plan lines", true,
                () => showPlacement = false, "Ready");

        var availability = courierGate is { } gate
            ? new TransferExecutionAvailability(gate.CanStart, gate.Status)
            : TransferExecutionPolicy.ForExplicitRun(
                projection is not null && (projection.HasMovement || projection.HasUnknownListingDemand),
                owner.HasStableIdentity,
                transfers.CanStart,
                retainerRefresh.IsRefreshing || retainerRefresh.IsQueued);
        var canExecute = selected is not null && availability.CanExecute;
        if (offerExecute)
        {
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
                    ? projection!.HasUnknownListingDemand ? "Listing demand will be verified first" : projection.MovementSummary
                    : availability.BlockReason);
        }

        // Courier plans buy nothing: they move what characters already hold.
        if (isCourier)
            return;
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
        var stable = owner.HasStableIdentity;
        ImGui.SetNextItemWidth(width);
        var preview = selected is null ? "Choose a plan" : $"{selected.Name} · {PlanKindPresentation.Label(selected.Kind)}";
        if (!ImGui.BeginCombo("##RQTransferPlan", preview))
        {
            RegisterScratchSwitches(runtime, selected, stable);
            return;
        }
        foreach (var kind in PlanKinds)
        {
            ImGui.TextDisabled(PlanKindPresentation.Plural(kind));
            var plans = runtime.State.StowagePlans
                .Where(plan => plan.IsEphemeral && plan.Kind == kind && plan.Owner.Matches(owner))
                .Concat(StowagePlanCatalog.OwnerPlans(runtime.State, owner, kind));
            foreach (var plan in plans)
            {
                PlanKindPresentation.Badge(plan.Kind);
                ImGui.SameLine();
                if (ImGui.Selectable($"{plan.Name}##plan:{plan.Id}", selected?.Id == plan.Id))
                    SwitchTo(plan.Id);
            }
            if (MenuItem($"+ New {PlanKindPresentation.Noun(kind)} plan##new:{kind}", stable))
                NewPlan(owner, kind);
            ImGui.Spacing();
        }
        ImGui.EndCombo();
    }

    private static readonly StowagePlanKind[] PlanKinds = [StowagePlanKind.Transfer, StowagePlanKind.Courier];

    /// <summary>Quick switches to either unsaved plan, offered to the agent bridge on the closed selector.</summary>
    private void RegisterScratchSwitches(QuartermasterRuntimeSnapshot runtime, StowagePlan? selected, bool stable)
    {
        foreach (var kind in PlanKinds)
        {
            var scratchId = StowagePlanCatalog.FindScratch(runtime.State, runtime.Owner, kind)?.Id;
            reviewRegistry.RegisterLastSelectable(
                $"quartermaster.transfer.plan.scratch.{PlanKindPresentation.Key(kind)}",
                $"Open the unsaved {PlanKindPresentation.Noun(kind)} plan",
                stable,
                scratchId is not null && selected?.Id == scratchId,
                () => SelectPlanOfKindScratch(kind),
                stable ? "Available" : "Owner identity unavailable");
        }
    }

    /// <summary>Opens the owner's unsaved plan of a kind, creating it when missing.</summary>
    private void SelectPlanOfKindScratch(StowagePlanKind kind)
    {
        var runtime = runtimeSnapshots.Current;
        if (!runtime.Owner.HasStableIdentity)
            return;
        EnsureSelection(runtime);
        var owner = runtime.Owner;
        var id = state.ReadConfiguration(document => StowagePlanCatalog.FindScratch(document, owner, kind)?.Id)
                 ?? state.Mutate(document => StowagePlanCatalog.EnsureScratch(document, owner, kind).Id);
        SwitchTo(id);
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
            () => NewPlan(runtimeSnapshots.Current.Owner, StowagePlanKind.Transfer),
            stable ? "Available" : "Owner identity unavailable");
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plan.new-courier",
            "Create a new Courier Plan",
            stable,
            () => NewPlan(runtimeSnapshots.Current.Owner, StowagePlanKind.Courier),
            stable ? "Available" : "Owner identity unavailable");
        var managedPlanId = selected is { IsEphemeral: false } ? selected.Id : (Guid?)null;
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.manage",
            "Open the Transfer Plan manager",
            true,
            () => managePlans(managedPlanId),
            "Available");
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
        if (MenuItem("New transfer plan", stable))
            NewPlan(owner, StowagePlanKind.Transfer);
        if (MenuItem("New courier plan", stable))
            NewPlan(owner, StowagePlanKind.Courier);
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
        }
        ImGui.Separator();
        if (MenuItem("Manage plans…"))
            managePlans(selected is { IsEphemeral: false } ? selected.Id : null);
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

    private void NewPlan(OwnerScope owner, StowagePlanKind kind)
    {
        if (!owner.HasStableIdentity)
            return;
        try
        {
            var (id, name) = state.Mutate(document =>
            {
                var plan = StowagePlanCatalog.Create(document, owner, kind: kind);
                return (plan.Id, plan.Name);
            });
            SwitchTo(id);
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
            SwitchTo(id);
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

    private void DrawSummary(QuartermasterRuntimeSnapshot runtime, TransferWorkbenchProjection projection)
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
        if (totals.Send > 0)
            Clause($"Send {totals.Send:N0}", TransferPresentation.SendColor);
        var destinations = CourierDestinations(projection);
        if (destinations.Count == 1)
            Clause($"to {CourierSession.CharacterLabel(destinations[0])}", null);
        else if (destinations.Count > 1)
            Clause($"{destinations.Count:N0} destinations", null);
        if (projection.Courier is { } courierPlan)
        {
            if (courierPlan.Trips.Length > 1)
                Clause($"{courierPlan.Trips.Length:N0} trips", TransferPresentation.SendColor);
            if (courier.StartGate(runtime, courierPlan) is { Status: { } status } gate)
            {
                Clause(status, AmberColor);
                if (gate.OpensSettings)
                {
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsItemClicked())
                        courier.RequestSettings();
                    reviewRegistry.RegisterLastButton("quartermaster.courier.settings.open", status, true,
                        courier.RequestSettings, status);
                }
            }
        }
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
        ImGui.TextDisabled(plan.Kind == StowagePlanKind.Courier ? "Send" : "Target");
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
            var minimum = plan.Kind == StowagePlanKind.Courier ? 1 : 0;
            if (MutateRules(owner, plan.Id, selectedIds, rule =>
                {
                    if (target is { } value)
                        rule.TargetQuantity = Math.Max(minimum, value);
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
        if (workbench.LastRemoved is not { } removed || removed.PlanId != plan.Id)
            return;
        switch (DalamudUndoStrip.Draw("RQTransferUndo", removed.Label))
        {
            case DalamudUndoStripResult.Undo:
                UndoRemoval(owner, removed);
                break;
            case DalamudUndoStripResult.Dismiss:
                workbench.LastRemoved = null;
                break;
        }
    }

    // ---- Table ----------------------------------------------------------------------------

    private void DrawTable(QuartermasterRuntimeSnapshot runtime, StowagePlan plan, TransferWorkbenchProjection projection)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        renderedTransferRowCount = 0;
        var isCourier = plan.Kind == StowagePlanKind.Courier;
        var table = drawingTable = isCourier ? courierWorkbenchTable : transferWorkbenchTable;
        if (!table.Begin(
                isCourier ? "RQCourierWorkbenchV1" : "RQTransferWorkbenchV5",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags,
                    FreezeRows: 1)))
            return;

        IReadOnlyList<TransferWorkbenchRow> rows;
        unsafe
        {
            rows = table.Apply(projection.Rows, ImGui.TableGetSortSpecs());
        }
        // Apply returns the same list until the lines, sort or filters change; reuse the keys with it.
        if (!ReferenceEquals(drawingRowKeysSource, rows) || drawingRowKeys is null)
        {
            drawingRowKeys = rows.Select(row => row.Rule.Id).ToArray();
            drawingRowKeysSource = rows;
        }
        drawingRows = true;

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
            renderedTransferRowCount = table.DrawClippedRows(rows, (_, index) => DrawRow(rows, index));
        }

        ImGui.TableNextRow();
        if (ImGui.TableNextColumn())
            DrawAddInput(runtime, plan);
        drawingRows = false;
        DalamudTableSelectionRenderer.EndRows(selection);
        table.End();
    }

    private void DrawRow(IReadOnlyList<TransferWorkbenchRow> rows, int index)
    {
        var row = rows[index];
        ImGui.PushID(row.Rule.Id.ToString());
        drawingRowIndex = index;
        (drawingTable ?? transferWorkbenchTable).DrawSaneRow(
            rows,
            index,
            $"transfer:{row.Rule.Id}",
            row.Rule.Enabled ? null : PausedRowColor);
        ImGui.PopID();
        var ruleId = row.Rule.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.transfer.line.select.{ruleId:N}",
            $"Select {row.Rule.ItemName} for bulk actions",
            true,
            () => selection.SetSelected(ruleId, !selection.IsSelected(ruleId)),
            selection.IsSelected(ruleId) ? "Selected" : "Available");
    }

    private void DrawItemCell(TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(rule.ItemName);
        if (rule.Quality != ItemQualityPolicy.Any)
            Tag(QualityLabel(rule.Quality));
        // A courier line's route has its own From and To columns.
        if (rule.Courier is null && TransferPresentation.RouteSummary(rule.Routing, row.Runtime.Retainers, row.Owner) is { Length: > 0 } routeSummary)
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
        row.Rule.Courier is not null ? row.Rule.TargetQuantity.ToString("N0") :
        TransferWorkbenchPresentation.Target(
            row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity,
            row.PlayerQuantity,
            row.ListingContribution.IsKnown);

    private void DrawTransferTarget(TransferWorkbenchRow row)
    {
        if (row.Rule.Courier is not null)
        {
            DrawCourierQuantity(row);
            return;
        }
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

    /// <summary>
    /// A courier line's quantity is the number of units to send: at least one, and no more than the
    /// chosen source holds once that source has been observed.
    /// </summary>
    private void DrawCourierQuantity(TransferWorkbenchRow row)
    {
        var route = row.Rule.Courier!;
        var available = courier.Available(row.Runtime, route.SourceCharacter ?? row.Owner, route.SourceRetainerId, row.Rule.ItemId, row.Rule.Quality);
        var units = row.Rule.TargetQuantity;
        ImGui.SetNextItemWidth(Math.Min(66, Math.Max(32, ImGui.GetContentRegionAvail().X * .55f)));
        if (focusTargetRuleId == row.Rule.Id)
        {
            ImGui.SetScrollHereY(.5f);
            ImGui.SetKeyboardFocusHere();
            focusTargetRuleId = null;
        }
        int Set(int requested)
        {
            var clamped = available is int most && most > 0 ? Math.Clamp(requested, 1, most) : Math.Max(1, requested);
            if (clamped != row.Rule.TargetQuantity)
                MutateRules(row.Owner, row.PlanId, [row.Rule.Id], draftRule => draftRule.TargetQuantity = clamped);
            return clamped;
        }
        if (DalamudGroupedIntegerInput.InputGroupedInt("##target", ref units, 0, 0))
            Set(units);
        reviewRegistry.RegisterLastAction(
            $"quartermaster.transfer.line.quantity.{row.Rule.Id:N}",
            $"Set the units of {row.Rule.ItemName} to send",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            row.Rule.TargetQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new AgentBridgeActionArgumentSchema([new("quantity", AgentBridgeActionArgumentKind.Integer)]),
            arguments =>
            {
                if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } value ||
                    !value.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt32(out var requested))
                    return AgentBridgeUiActionResult.Fail("quantity must be a whole number.");
                return AgentBridgeUiActionResult.Ok($"Units set to {Set(requested):N0}.");
            });
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
        var primary = projection.Kind switch
        {
            TransferLineKind.Move => string.Empty,
            TransferLineKind.Paused => "Paused",
            TransferLineKind.VerifyListing => "Verify listing shortfall",
            TransferLineKind.Balanced => "Balanced",
            TransferLineKind.Courier => CourierOutcome(row),
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
        var moves = MoveOutcome(row.Line);
        return moves.Length == 0 ? primary : primary.Length == 0 ? moves : $"{primary} · {moves}";
    }

    private static string MoveOutcome(StowageEvaluationLine? line) => line is null || (line.Moves.Count == 0 && line.MoveLimits.Count == 0) ? string.Empty :
        string.Join(" · ", (line.MoveQuantity > 0 ? new[] { $"Move {line.MoveQuantity:N0}" } : Array.Empty<string>())
            .Concat(line.MoveLimits.Select(l => l.Message)));

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
        if (MoveOutcome(row.Line) is { Length: > 0 } moves)
            Part(moves, retrieveColor);
        switch (projection.Kind)
        {
            case TransferLineKind.Move:
                return;
            case TransferLineKind.Paused:
                ImGui.TextDisabled("Paused");
                return;
            case TransferLineKind.VerifyListing:
                ImGui.TextColored(AmberColor, "Verify listing shortfall");
                return;
            case TransferLineKind.Balanced:
                ImGui.TextColored(TransferPresentation.ActionColor(null), "Balanced");
                return;
            case TransferLineKind.Courier:
                if (row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code))
                {
                    ImGui.TextColored(ErrorColor, CourierSession.RefusalLabel(refusal.Code));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(courier.RefusalDetail(row.Runtime, refusal));
                }
                else if (row.Projection.Send > 0)
                    ImGui.TextColored(TransferPresentation.SendColor, $"Send {row.Projection.Send:N0}");
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
        ImGui.SetNextWindowBgAlpha(1f);
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
        if (rule.Courier is { } lineRoute)
        {
            DrawCourierRoute(row, lineRoute);
            if (ImGui.Selectable($"Placement · {CourierSession.CharacterLabel(lineRoute.Character)}"))
                OpenCourierPlacement(lineRoute.Character);
        }
        else
            DrawStorageSourcing(row);

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

    /// <summary>Vendor sourcing and retainer placement: the same-character line's sections.</summary>
    private void DrawStorageSourcing(TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        Guid[] ids = [rule.Id];
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
    }

    /// <summary>A courier line's source and destination choosers.</summary>
    private void DrawCourierRoute(TransferWorkbenchRow row, CourierRoute route)
    {
        Guid[] ids = [row.Rule.Id];
        var runtime = row.Runtime;
        var source = route.SourceCharacter ?? row.Owner;
        if (ImGui.BeginMenu($"From · {CourierSession.Side(runtime, source, route.SourceRetainerId, includeOwner: true)}###from"))
        {
            void From(ulong? container)
            {
                if (container != route.SourceRetainerId)
                    MutateRules(row.Owner, row.PlanId, ids, draftRule => draftRule.Courier = route with { SourceRetainerId = container });
            }
            if (ImGui.MenuItem("Bags##from", string.Empty, route.SourceRetainerId is null))
                From(null);
            foreach (var retainer in CourierSession.Retainers(runtime, source))
                if (ImGui.MenuItem($"{retainer.RetainerName}##from:{retainer.RetainerId}", string.Empty, route.SourceRetainerId == retainer.RetainerId))
                    From(retainer.RetainerId);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu($"To · {CourierSession.Side(runtime, route.Character, route.RetainerId, includeOwner: true)}###to"))
        {
            DrawCourierDestinations(runtime, (character, container) =>
            {
                if (!character.Matches(route.Character) || container != route.RetainerId)
                    MutateRules(row.Owner, row.PlanId, ids, draftRule => draftRule.Courier = route with { Character = character with { }, RetainerId = container });
            }, route.Character, route.RetainerId, source: route.SourceCharacter);
            ImGui.EndMenu();
        }
    }

    /// <summary>
    /// The courier destination chooser: one menu per character the source can send to, holding its
    /// bags and retainers. Shared by the stock browser and the line menu.
    /// </summary>
    internal void DrawCourierDestinations(
        QuartermasterRuntimeSnapshot runtime,
        Action<OwnerScope, ulong?> choose,
        OwnerScope? current = null,
        ulong? currentRetainer = null,
        string? controlPrefix = null,
        OwnerScope? source = null) =>
        DrawContainerMenus(runtime, courier.Destinations(runtime, source), choose, current, currentRetainer, controlPrefix, "Send to");

    /// <summary>One menu per character holding its bags and retainers in game order; the current pick is checked.</summary>
    private void DrawContainerMenus(
        QuartermasterRuntimeSnapshot runtime,
        IEnumerable<OwnerScope> characters,
        Action<OwnerScope, ulong?> choose,
        OwnerScope? current,
        ulong? currentRetainer,
        string? controlPrefix,
        string verb)
    {
        foreach (var character in characters)
        {
            var key = character.LocalContentId!.Value;
            var here = current?.Matches(character) == true;
            var target = character;
            var open = ImGui.BeginMenu($"{CourierSession.CharacterLabel(character)}##courier:{key}");
            if (controlPrefix is not null)
                reviewRegistry.RegisterLastButton($"{controlPrefix}.{key}.bags", $"{verb} {character.CharacterName} · Bags", true,
                    () => choose(target, null), "Available");
            if (!open)
            {
                // A closed submenu still offers its retainers to the agent bridge, on the submenu's own row.
                if (controlPrefix is not null)
                    foreach (var closed in CourierSession.Retainers(runtime, character))
                    {
                        var closedId = closed.RetainerId;
                        reviewRegistry.RegisterLastButton($"{controlPrefix}.{key}.{closedId}", $"{verb} {character.CharacterName} · {closed.RetainerName}", true,
                            () => choose(target, closedId), "Available");
                    }
                continue;
            }
            if (ImGui.MenuItem($"Bags##courier:{key}:bags", string.Empty, here && currentRetainer is null))
                choose(character, null);
            foreach (var retainer in CourierSession.Retainers(runtime, character))
            {
                if (ImGui.MenuItem($"{retainer.RetainerName}##courier:{key}:{retainer.RetainerId}", string.Empty, here && currentRetainer == retainer.RetainerId))
                    choose(character, retainer.RetainerId);
                var retainerId = retainer.RetainerId;
                if (controlPrefix is not null)
                    reviewRegistry.RegisterLastButton($"{controlPrefix}.{key}.{retainerId}", $"{verb} {character.CharacterName} · {retainer.RetainerName}", true,
                        () => choose(target, retainerId), "Available");
            }
            ImGui.EndMenu();
        }
    }

    internal bool HasCourierDestinations(QuartermasterRuntimeSnapshot runtime, OwnerScope? source = null) =>
        courier.Destinations(runtime, source).Any();

    private static string CourierOutcome(TransferWorkbenchRow row) =>
        row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code)
            ? CourierSession.RefusalLabel(refusal.Code)
            : row.Projection.Send > 0 ? $"Send {row.Projection.Send:N0}" : string.Empty;

    // ---- Courier cells ----------------------------------------------------------------------

    private static string CourierFromText(TransferWorkbenchRow row) =>
        row.Rule.Courier is { } route
            ? CourierSession.Side(row.Runtime, route.SourceCharacter ?? row.Owner, route.SourceRetainerId, includeOwner: true)
            : string.Empty;

    private static string CourierToText(TransferWorkbenchRow row) =>
        row.Rule.Courier is { } route
            ? CourierSession.Side(row.Runtime, route.Character, route.RetainerId, includeOwner: true)
            : string.Empty;

    private static string CourierStatusText(TransferWorkbenchRow row) =>
        row.Rule.Enabled ? CourierOutcome(row) : "Paused";

    /// <summary>A route side drawn as its label; clicking it opens the chooser popup. The agent bridge opens it too.</summary>
    private bool CourierSideButton(TransferWorkbenchRow row, string side, string label, string description)
    {
        ImGui.AlignTextToFramePadding();
        var clicked = ImGui.Selectable($"{label}##{side}", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(label).X, 0));
        var ruleId = row.Rule.Id;
        reviewRegistry.RegisterLastButton($"quartermaster.courier.line.{side}.{ruleId:N}", description, true,
            () => pendingCourierPopup = (ruleId, side), label);
        if (pendingCourierPopup is { } pending && pending.RuleId == ruleId && pending.Side == side)
        {
            pendingCourierPopup = null;
            clicked = true;
        }
        if (clicked)
            ImGui.OpenPopup($"courier-{side}");
        ImGui.SetNextWindowBgAlpha(1f);
        return ImGui.BeginPopup($"courier-{side}");
    }

    private void DrawCourierFrom(TransferWorkbenchRow row)
    {
        if (row.Rule.Courier is not { } route)
            return;
        var source = route.SourceCharacter ?? row.Owner;
        if (!CourierSideButton(row, "from", CourierFromText(row), $"Choose where {row.Rule.ItemName} is sent from"))
        {
            if (ImGui.IsItemHovered() && courier.Available(row.Runtime, source, route.SourceRetainerId, row.Rule.ItemId, row.Rule.Quality) is { } held)
                ImGui.SetTooltip($"Holds {held:N0}");
            return;
        }
        Guid[] ids = [row.Rule.Id];
        var owner = row.Owner;
        var sources = courier.Characters(row.Runtime)
            .Where(character => !CourierSession.IsCarrier(row.Runtime, character) && !character.Matches(route.Character));
        DrawContainerMenus(row.Runtime, sources, (character, container) =>
        {
            var sourceCharacter = character.Matches(owner) ? null : character with { };
            if (!character.Matches(source) || container != route.SourceRetainerId)
                MutateRules(owner, row.PlanId, ids, draftRule => draftRule.Courier = route with { SourceCharacter = sourceCharacter, SourceRetainerId = container });
            ImGui.CloseCurrentPopup();
        }, source, route.SourceRetainerId, $"quartermaster.courier.line.from.{row.Rule.Id:N}", "Send from");
        ImGui.EndPopup();
    }

    private void DrawCourierTo(TransferWorkbenchRow row)
    {
        if (row.Rule.Courier is not { } route)
            return;
        if (!CourierSideButton(row, "to", CourierToText(row), $"Choose where {row.Rule.ItemName} is delivered"))
            return;
        Guid[] ids = [row.Rule.Id];
        var owner = row.Owner;
        DrawCourierDestinations(row.Runtime, (character, container) =>
        {
            if (!character.Matches(route.Character) || container != route.RetainerId)
                MutateRules(owner, row.PlanId, ids, draftRule => draftRule.Courier = route with { Character = character with { }, RetainerId = container });
            ImGui.CloseCurrentPopup();
        }, route.Character, route.RetainerId, $"quartermaster.courier.line.to.{row.Rule.Id:N}", route.SourceCharacter);
        ImGui.EndPopup();
    }

    private void DrawCourierStatus(TransferWorkbenchRow row)
    {
        ImGui.AlignTextToFramePadding();
        if (!row.Rule.Enabled)
            ImGui.TextDisabled("Paused");
        else if (row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code))
        {
            ImGui.TextColored(ErrorColor, CourierSession.RefusalLabel(refusal.Code));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(courier.RefusalDetail(row.Runtime, refusal));
        }
        else if (row.Projection.Send > 0)
            ImGui.TextColored(TransferPresentation.SendColor, $"Send {row.Projection.Send:N0}");
    }

    private void DrawHoverRemove(TransferWorkbenchRow row)
    {
        // The line menu button was just drawn in this row; its rect is the row's vertical extent.
        var rowHovered = DalamudRowRemoveButton.IsLastItemRowHovered();
        ImGui.SameLine();
        var ruleId = row.Rule.Id;
        var owner = row.Owner;
        var planId = row.PlanId;
        if (DalamudRowRemoveButton.Draw("remove-line", rowHovered))
            RemoveRules(owner, planId, [ruleId]);
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
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - SearchReference.ButtonWidth - ImGui.GetStyle().ItemSpacing.X));
        if (focusAddInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusAddInput = false;
        }
        var entered = ImGui.InputTextWithHint(
            "##RQPlanAddItem",
            "+ Add item, @group, #category or >craft…",
            ref addText,
            96,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var active = ImGui.IsItemActive();
        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        ImGui.SetCursorScreenPos(new Vector2(inputMax.X + ImGui.GetStyle().ItemSpacing.X, inputMin.Y));
        addHelp.Draw(reviewRegistry);
        if (addHelp.IsOpen)
        {
            if (addHelp.DrawPlan() is { } prefix)
            {
                addText = prefix;
                focusAddInput = true;
            }
            return;
        }
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
        Func<IReadOnlyList<PlanAddEntry>, bool, IReadOnlyList<Guid>> add = plan.Kind == StowagePlanKind.Courier
            ? (entries, select) => AddCourierLines(runtime, plan.Id, entries, select)
            : (entries, select) => AddLines(runtime, plan.Id, entries, select);
        if (suggestion.Item is { } item)
        {
            var added = add([new PlanAddEntry(item.ItemId, item.Name, ItemQualityPolicy.Any)], false);
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
            add(entries, true);
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
                    .GroupBy(StowagePlanCatalog.LineKey)
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
                removed = new RemovedLines(plan.Id, label, rules, dropped);
                return plan.Id;
            });
            workbench.LastRemoved = removed;
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
            workbench.LastRemoved = null;
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
        if (run is null || run.RunId == dismissedVendorRunId)
            return;

        ImGui.TextUnformatted($"Vendor buy · {run.Phase}");
        if (!vendorProcurement.IsRunning && run.Phase != GilVendorBuyPhase.Paused)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss##vendor-result")) dismissedVendorRunId = run.RunId;
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
        var courierPlan = courier.Plan(runtime, plan);
        if (transferWorkbenchProjection is { } cached &&
            cached.RuntimeRevision == runtime.Revision &&
            cached.PlanId == plan.Id &&
            ReferenceEquals(cached.Courier, courierPlan))
            return cached;
        var courierUnits = courierPlan?.Trips.SelectMany(trip => trip.Manifest)
            .GroupBy(shipment => shipment.Line)
            .ToDictionary(group => group.Key, group => group.Sum(shipment => shipment.Units)) ?? [];
        var courierRefusals = courierPlan?.Refusals.GroupBy(refusal => refusal.Line)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];

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
                var stock = runtime.Browser.FindItem(rule.ItemId);
                var playerQuantity = StowageEvaluator.PlayerQuantity(rule, stock);
                var accessibleStorageQuantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    stock,
                    rule.Quality,
                    runtime.Retainers,
                    runtime.Owner);
                listingContributions.TryGetValue((rule.ItemId, rule.Quality), out var listingContribution);
                var listingQuantity = listingContribution?.Quantity ?? Evidence.Known(0);
                var difference = (line?.DesiredPlayerQuantity ?? rule.TargetQuantity) - playerQuantity;
                var lineProjection = rule.Courier is not null
                    ? rule.Enabled
                        ? new TransferLineProjection(TransferLineKind.Courier, Send: courierUnits.GetValueOrDefault(rule.Id))
                        : new TransferLineProjection(TransferLineKind.Paused)
                    : TransferWorkbenchPresentation.Project(
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
                    vendorPreviewLine,
                    courierRefusals.GetValueOrDefault(rule.Id));
            })
            .ToArray();
        // Count lines that will actually move something, from the same projection the rows show.
        var movements = rows.Where(row => row.Rule.Courier is null).Sum(row =>
            (row.Projection.Retrieve > 0 || row.Projection.Stow > 0 ? 1 : 0) + (row.Line?.Moves.Count ?? 0));
        transferWorkbenchProjection = new(
            runtime.Revision,
            plan.Id,
            rules,
            stowage,
            retrieval,
            vendor,
            deposit,
            movements,
            TransferExecutionPolicy.HasMovement(retrieval.NeededQuantity, deposit) || courierPlan?.Trips.IsEmpty == false,
            listingContributions.Values.Any(contribution => !contribution.Quantity.IsKnown),
            rows,
            courierPlan);
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
