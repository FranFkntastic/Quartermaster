using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record StockWorkspaceSnapshot(
    int VisibleCount,
    int RenderedRowCount,
    int ProjectionBuildCount,
    int TableApplyCount,
    double DrawMilliseconds);

/// <summary>
/// Owns stock filtering, selection, projection caching, and direct plan actions.
/// </summary>
internal sealed class StockWorkspace
{
    private readonly SearchReference.Button searchHelp = new("quartermaster.stock.searchHelp", "Stock search syntax");
    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly TransferExecutionController transferExecution;
    private readonly PluginConfiguration configuration;
    private readonly Action saveConfiguration;
    private readonly WorkbenchState workbench;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly TransferPlanWorkspace transferPlans;
    private readonly ListingPlanDraftSession listingSession;
    private readonly Action<QuartermasterRuntimeSnapshot, ListingItemKey> showInListings;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action requestStockView;
    private readonly GameItemCatalog gameCatalog;
    private readonly CatalogPicker catalogPicker;
    private readonly BrowserQueryController queries = new();
    private readonly TableSelectionModel<uint> stockSelection = new();
    private readonly DalamudTableProjection<StockWorkbenchRow> stockTable;
    private StockWorkbenchProjection? stockWorkbenchProjection;
    private long availableStockRevision = -1;
    private string? availableStockScopeKey;
    private IReadOnlyList<StockGroup> availableStockItems = [];
    private IReadOnlyList<StockGroup>? focusedStockSource;
    private uint? focusedStockItemId;
    private IReadOnlyList<StockGroup> focusedStockItems = [];
    private long listingEvaluationRevision = -1;
    private ListingPlanEvaluation? stockListingEvaluation;
    private readonly VisibleItemIds stockVisible = new();
    private StockLookup? scopedLookup;
    private StockLookup? allLookup;
    private long prunedRevision = -1;
    private int selectionBarLines = 1;
    private int stockProjectionBuildCount;
    private int visibleStockCount;
    private int renderedStockRowCount;
    private double stockDrawMilliseconds;
    private bool stowRequested;
    private bool catalogMode;

    public StockWorkspace(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        RetainerRefreshCoordinator retainerRefresh,
        TransferExecutionController transferExecution,
        PluginConfiguration configuration,
        Action saveConfiguration,
        WorkbenchState workbench,
        ItemGroupWorkspace itemGroupWorkspace,
        TransferPlanWorkspace transferPlans,
        ListingPlanDraftSession listingSession,
        Action<QuartermasterRuntimeSnapshot, ListingItemKey> showInListings,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action requestStockView,
        GameItemCatalog gameCatalog)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.retainerRefresh = retainerRefresh;
        this.transferExecution = transferExecution;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.workbench = workbench;
        this.itemGroupWorkspace = itemGroupWorkspace;
        this.transferPlans = transferPlans;
        this.listingSession = listingSession;
        this.showInListings = showInListings;
        this.reviewRegistry = reviewRegistry;
        this.requestStockView = requestStockView;
        this.gameCatalog = gameCatalog;
        catalogPicker = new CatalogPicker(gameCatalog, reviewRegistry, stockSelection);
        stockTable = CreateTable();
    }

    public StockWorkspaceSnapshot Snapshot() =>
        new(visibleStockCount, renderedStockRowCount, stockProjectionBuildCount, stockTable.ApplyCount, stockDrawMilliseconds);

    public void ClearSelection() => stockSelection.Clear();


    private DalamudTableProjection<StockWorkbenchRow> CreateTable() => new(
    [
        new(
            "Item",
            1.8f,
            row => row.Item.ItemName,
            row => row.Item.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide,
            Draw: row => { ImGui.TextUnformatted(row.Item.ItemName); if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", row.Item.Stacks.GroupBy(s => s.OwnerName).Select(g => $"{g.Key}: {g.Sum(s => s.Quantity):N0}"))); },
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "item"),
        new(
            "Locations",
            1.5f,
            row => string.Join("; ", row.Item.Stacks.GroupBy(s => s.OwnerName).Select(g => $"{g.Key}: {g.Sum(s => s.Quantity):N0}")),
            Flags: ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide,
            Id: "locations",
            Draw: row => { ImGui.TextUnformatted(string.Join("; ", row.Item.Stacks.GroupBy(s => s.OwnerName).Select(g => $"{g.Key}: {g.Sum(s => s.Quantity):N0}"))); if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", row.Item.Stacks.GroupBy(s => s.OwnerName).Select(g => $"{g.Key}: {g.Sum(s => s.Quantity):N0}"))); }),
        new(
            "Player",
            64,
            row => row.Item.PlayerQuantity.ToString("N0"),
            row => row.Item.PlayerQuantity,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "player"),
        new(
            "Saddlebag",
            72,
            row => row.Item.SaddlebagQuantity.ToString("N0"),
            row => row.Item.SaddlebagQuantity,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "saddlebag"),
        new(
            "Retainers",
            82,
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
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide,
            TextColor: row => row.Rule is null
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : TransferPresentation.ActionColor(row.Line?.Action),
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "plan-state"),
    ], DalamudTableSelection<StockWorkbenchRow>.Multi(stockSelection, row => row.Item.ItemId));


    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var drawStarted = Stopwatch.GetTimestamp();
        PruneSelection(runtime);
        if (catalogMode)
        {
            DrawCatalog(runtime);
            stockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
            return;
        }
        var projection = runtime.Browser;
        var availableItems = GetStockItemsWithListingDemand(runtime, workbench.ScopeKey);
        // The toolbar runs first and updates IsInputActive. Use the same editing
        // snapshot for both cache lookups so a focus transition cannot rebuild twice.
        var isEditing = workbench.ItemFilterState.IsInputActive;
        var scopeKey = workbench.ScopeKey;
        DrawStockToolbar(runtime, availableItems, scopeKey, isEditing);
        var result = queries.QueryItems(
            projection,
            workbench.ItemFilterState.Expression,
            scopeKey,
            isEditing,
            runtime.Revision,
            availableItems);
        if (!ReferenceEquals(focusedStockSource, result.Items) || focusedStockItemId != workbench.FocusedStockItemId)
        {
            focusedStockSource = result.Items;
            focusedStockItemId = workbench.FocusedStockItemId;
            focusedStockItems = ListingPlanPresentation.ApplyStockItemFocus(result.Items, focusedStockItemId);
        }
        var visibleItems = focusedStockItems;
        visibleStockCount = visibleItems.Count;
        if (!stockVisible.IsFor(visibleItems))
            stockVisible.Set(visibleItems, visibleItems.Select(item => item.ItemId).ToArray());
        if (!result.Filter.IsValid)
            ImGui.TextColored(
                new Vector4(1f, .65f, .25f, 1f),
                result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        var selectedPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
        var sourceRows = ResolveStockWorkbenchProjection(runtime, visibleItems, selectedPlan);
        DrawSelectionBar(runtime, stockVisible, canSelectVisible: true);
        DrawSingleStockSelection(runtime);
        DrawTableColumnsToolbar(stockTable, "RQStockColumns", "Visible stock");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var tableHeight = Math.Max(180, ImGui.GetContentRegionAvail().Y);
        renderedStockRowCount = 0;
        if (stockTable.Begin(
                "RQStockWorkbenchV5",
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
            renderedStockRowCount = stockTable.DrawClippedRows(
                rows,
                (row, rowIndex) =>
                {
                    stockTable.DrawSaneRow(rows, rowIndex, $"##stock-row:{row.Item.ItemId}");
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
        stockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
    }

    private IReadOnlyList<StockGroup> GetStockItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, string scopeKey)
    {
        if (availableStockRevision != runtime.Revision || availableStockScopeKey != scopeKey)
        {
            availableStockItems = StockItemsWithListingDemand(runtime, scopeKey);
            availableStockRevision = runtime.Revision;
            availableStockScopeKey = scopeKey;
        }
        return availableStockItems;
    }

    internal static IReadOnlyList<StockGroup> StockItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, string scopeKey)
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
            (ReferenceEquals(cached.QueryItems, queryItems) ||
             cached.QueryItems.Select(item => item.ItemId).SequenceEqual(queryItems.Select(item => item.ItemId))))
            return cached.Rows;

        var rules = selectedPlan is null
            ? new Dictionary<uint, TargetPlanItem>()
            : runtime.State.PlanItems
                .Where(rule => rule.StowagePlanId == selectedPlan.Id)
                .GroupBy(rule => rule.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
        var evaluated = selectedPlan is null
            ? new Dictionary<Guid, StowageEvaluationLine>()
            : StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, runtime.Owner, selectedPlan.Id)?
                .Lines.ToDictionary(line => line.RuleId) ?? [];
        var listingEvaluation = ResolveStockListingEvaluation(runtime);
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

    private ListingPlanEvaluation ResolveStockListingEvaluation(QuartermasterRuntimeSnapshot runtime)
    {
        if (stockListingEvaluation is null || listingEvaluationRevision != runtime.Revision)
        {
            stockListingEvaluation = ListingPlanEvaluator.Evaluate(
                ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner), runtime.Browser);
            listingEvaluationRevision = runtime.Revision;
        }
        return stockListingEvaluation;
    }

    private void DrawStockToolbar(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> sourceItems, string scopeKey, bool isEditing)
    {
        var projection = runtime.Browser;
        if (catalogMode)
        {
            catalogPicker.DrawSearch(Math.Max(100, ImGui.GetContentRegionAvail().X - ScopeComboWidth() - ImGui.GetStyle().ItemSpacing.X));
            ImGui.SameLine();
            DrawScopeCombo(projection);
            return;
        }
        var context = queries.EnsureItemContext(sourceItems, projection.Owner, scopeKey, isEditing, runtime.Revision);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var trailingWidth = SearchReference.ButtonWidth + spacing + ScopeComboWidth() + spacing +
            ImGui.CalcTextSize("…").X + (ImGui.GetStyle().FramePadding.X * 2f) + spacing;
        var filterBeforeDraw = workbench.ItemFilterState.Expression;
        DalamudFilterAutocompleteRenderer.Draw(
            "RQStockWorkbench",
            "Search stock — try ilvl>=690 job:WHM slot:body",
            context,
            workbench.ItemFilterState,
            Math.Max(100, ImGui.GetContentRegionAvail().X - trailingWidth));
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
        searchHelp.Draw(reviewRegistry);
        if (searchHelp.DrawStock(context) is { } example)
        {
            workbench.ItemFilterState.SetExpression(example, example.Length);
            workbench.ItemFilterState.RequestFocus();
            workbench.FocusedStockItemId = null;
        }
        ImGui.SameLine();
        DrawScopeCombo(projection);

        ImGui.SameLine();
        if (ImGui.SmallButton("…##RQStockOptions"))
            ImGui.OpenPopup("RQStockOptions");
        if (ImGui.BeginPopup("RQStockOptions"))
        {
            ImGui.TextDisabled("Included player storage");
            var includeCrystals = configuration.IncludeCrystals;
            var includeEquipped = configuration.IncludeEquipped;
            var includeArmoury = configuration.IncludeArmoury;
            var changed = false;
            changed |= ImGui.Checkbox("Crystals", ref includeCrystals);
            changed |= ImGui.Checkbox("Equipped", ref includeEquipped);
            changed |= ImGui.Checkbox("Armoury", ref includeArmoury);
            if (changed)
            {
                configuration.IncludeCrystals = includeCrystals;
                configuration.IncludeEquipped = includeEquipped;
                configuration.IncludeArmoury = includeArmoury;
                saveConfiguration();
            }
            ImGui.EndPopup();
        }

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
            ImGui.TextDisabled(retainerRefresh.HasRecovery && retainerRefresh.Status == RetainerRefreshCoordinator.NotRunStatus
                ? "Last refresh was interrupted."
                : retainerRefresh.Status);
            if (ImGui.IsItemHovered() && retainerRefresh.Results.Count != 0)
            {
                ImGui.BeginTooltip();
                foreach (var result in retainerRefresh.Results)
                    ImGui.TextUnformatted($"{result.RetainerName}: {result.Outcome}");
                ImGui.EndTooltip();
            }
        }
    }

    private static float ScopeComboWidth() =>
        Math.Max(150, ImGui.CalcTextSize(CatalogPicker.ScopeLabel).X + ImGui.GetFrameHeight() + (ImGui.GetStyle().FramePadding.X * 2f));

    private void DrawScopeCombo(BrowserProjection projection)
    {
        ImGui.SetNextItemWidth(ScopeComboWidth());
        var selectedScope = projection.Scopes.First(scope => scope.Key == workbench.ScopeKey);
        if (ImGui.BeginCombo("##RQStockScope", catalogMode ? CatalogPicker.ScopeLabel : selectedScope.Label))
        {
            foreach (var scope in projection.Scopes)
            {
                if (ImGui.Selectable($"{scope.Label}##stock-scope:{scope.Key}", !catalogMode && scope.Key == workbench.ScopeKey))
                {
                    workbench.ScopeKey = scope.Key;
                    workbench.FocusedStockItemId = null;
                    LeaveCatalog();
                }
            }
            if (ImGui.Selectable($"{CatalogPicker.ScopeLabel}##stock-scope:catalog", catalogMode))
                EnterCatalog();
            ImGui.EndCombo();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.scope.catalog",
            $"Browse {CatalogPicker.ScopeLabel.ToLowerInvariant()}",
            true,
            EnterCatalog,
            catalogMode ? "Selected" : "Available");
    }

    private void EnterCatalog()
    {
        if (catalogMode)
            return;
        catalogMode = true;
        workbench.FocusedStockItemId = null;
    }

    private void LeaveCatalog() => catalogMode = false;

    private void DrawCatalog(QuartermasterRuntimeSnapshot runtime)
    {
        DrawStockToolbar(runtime, [], workbench.ScopeKey, workbench.ItemFilterState.IsInputActive);
        catalogPicker.DrawFilters(runtime);
        DrawSelectionBar(runtime, catalogPicker.Visible, catalogPicker.NarrowingActive);
        catalogPicker.DrawTable(
            item => gameCatalog.TryGet()?.MaterialsFor(item.ItemId) is not null,
            item => DrawMaterialsMenuItem(runtimeSnapshots.Current, item.ItemId));
        visibleStockCount = catalogPicker.VisibleCount;
    }

    /// <summary>Offers a craftable item's direct materials to whichever right-hand tab is open.</summary>
    private void DrawMaterialsMenuItem(QuartermasterRuntimeSnapshot runtime, uint itemId)
    {
        if (gameCatalog.TryGet()?.MaterialsFor(itemId) is not { } materials)
            return;
        if (workbench.View == WorkbenchView.ItemGroups)
        {
            if (ImGui.MenuItem($"Add materials to @{itemGroupWorkspace.TargetGroupName(runtime.State)}"))
                AddStockItemsToItemGroup(runtime, materials.Select(material => new StockGroup(material.ItemId, material.Name, [])).ToArray());
            return;
        }
        ImGui.BeginDisabled(!runtime.Owner.HasStableIdentity);
        if (ImGui.MenuItem("Add materials to plan"))
            transferPlans.AddMaterials(runtime, materials, 1);
        ImGui.EndDisabled();
    }

    /// <summary>
    /// The shared selection bar for every scope. The selection is keyed by item ID and survives
    /// scope switches; actions resolve each selected ID through stock first, then the catalog.
    /// </summary>
    private void DrawSelectionBar(QuartermasterRuntimeSnapshot runtime, VisibleItemIds visible, bool canSelectVisible)
    {
        var count = stockSelection.Count;
        var hasSelection = count > 0;
        var info = ListingWorkspace.Color(ListingStateTone.Info);
        var background = Scale(info, hasSelection ? .16f : .09f, .96f);
        var border = Scale(info, hasSelection ? .65f : .38f, 1f);
        var barHeight = (ImGui.GetFrameHeightWithSpacing() * selectionBarLines) + 10f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 5f));
        if (ImGui.BeginChild("RQStockSelectionBar", new Vector2(0f, barHeight), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var top = ImGui.GetCursorPosY();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(
                hasSelection ? info : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
                hasSelection ? $"{count:N0} item{(count == 1 ? string.Empty : "s")} selected" : "No items selected");
            if (hasSelection && visible.CountSelected(stockSelection) is var visibleSelected && visibleSelected != count)
            {
                var detail = $"{visibleSelected:N0} visible";
                UiLayout.SameLineIfFits(detail);
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(detail);
            }
            DrawSelectionActions(runtime, visible, canSelectVisible, hasSelection);
            var lines = (int)MathF.Round((ImGui.GetCursorPosY() - top) / ImGui.GetFrameHeightWithSpacing());
            selectionBarLines = Math.Max(1, lines);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawSelectionActions(
        QuartermasterRuntimeSnapshot runtime,
        VisibleItemIds visible,
        bool canSelectVisible,
        bool hasSelection)
    {
        var selectVisibleEnabled = canSelectVisible && visible.Ids.Length > 0;
        UiLayout.SameLineIfFits("Select visible");
        ImGui.BeginDisabled(!selectVisibleEnabled);
        if (ImGui.Button("Select visible"))
            SelectVisible();
        ImGui.EndDisabled();
        if (!canSelectVisible && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Narrow the catalog first");
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.select-visible",
            "Select every visible item",
            selectVisibleEnabled,
            SelectVisible,
            $"visible={visible.Ids.Length:N0}; selected={stockSelection.Count:N0}");

        var canPlan = hasSelection && runtime.Owner.HasStableIdentity;
        UiLayout.SameLineIfFits("Add to plan");
        ImGui.BeginDisabled(!canPlan);
        if (ImGui.Button("Add to plan"))
            AddSelectionToPlan(runtime);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.add-selected-to-plan",
            "Add selected items to the current Transfer Plan",
            canPlan,
            () => AddSelectionToPlan(runtimeSnapshots.Current),
            $"{stockSelection.Count:N0} selected");

        UiLayout.SameLineIfFits("Add to item group");
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button("Add to item group"))
            AddSelectionToItemGroup(runtime);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.add-selected-to-item-group",
            "Add selected items to the target Item Group",
            hasSelection,
            () => AddSelectionToItemGroup(runtimeSnapshots.Current),
            $"{stockSelection.Count:N0} selected");

        var canStow = hasSelection && runtime.Owner.HasStableIdentity && SelectionHasCarried(runtime);
        UiLayout.SameLineIfFits("Stow carried");
        ImGui.BeginDisabled(!canStow);
        if (ImGui.Button("Stow carried"))
            stowRequested = true;
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton("quartermaster.stock.stow", "Choose a stow destination", canStow,
            () => stowRequested = true, "Selected carried stock");
        if (stowRequested)
        {
            ImGui.OpenPopup("RQStockStow");
            stowRequested = false;
        }
        if (ImGui.BeginPopup("RQStockStow"))
        {
            DrawStowDestinations(runtime, ResolveSelection(runtime).Stock.Where(item => item.PlayerQuantity > 0).ToArray());
            ImGui.EndPopup();
        }

        UiLayout.SameLineIfFits("Clear");
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button("Clear"))
            ClearStockSelection();
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.clear-selection",
            "Clear the item selection",
            hasSelection,
            ClearStockSelection,
            $"{stockSelection.Count:N0} selected");
    }

    private void SelectVisible()
    {
        if (catalogMode && !catalogPicker.NarrowingActive)
            return;
        var visible = catalogMode ? catalogPicker.Visible : stockVisible;
        foreach (var id in visible.Ids)
            stockSelection.SetSelected(id, true);
    }

    private void ClearStockSelection()
    {
        stockSelection.Clear();
        workbench.FocusedStockItemId = null;
    }

    private void AddSelectionToPlan(QuartermasterRuntimeSnapshot runtime)
    {
        if (!runtime.Owner.HasStableIdentity || stockSelection.Count == 0)
            return;
        var (stock, catalogOnly) = ResolveSelection(runtime);
        if (stock.Count > 0)
            UpsertSelectedStockRules(runtime, stock, stowCarried: false);
        if (catalogOnly.Count > 0)
            transferPlans.AddItems(runtime, catalogOnly.Select(item => (item.ItemId, item.ItemName)));
        stockSelection.Clear();
    }

    private void AddSelectionToItemGroup(QuartermasterRuntimeSnapshot runtime)
    {
        if (stockSelection.Count == 0)
            return;
        var (stock, catalogOnly) = ResolveSelection(runtime);
        var items = stock.Concat(catalogOnly).ToList();
        items.Sort(CompareByName);
        AddStockItemsToItemGroup(runtime, items);
        stockSelection.Clear();
    }

    /// <summary>
    /// Splits the selection into stock items (resolved from the current stock scope first, so a
    /// selection made within one scope adds exactly as that scope shows it, then from All) and
    /// catalog-only items. IDs found in neither are skipped.
    /// </summary>
    private (List<StockGroup> Stock, List<StockGroup> CatalogOnly) ResolveSelection(QuartermasterRuntimeSnapshot runtime)
    {
        var stock = new List<StockGroup>();
        var catalogOnly = new List<StockGroup>();
        var snapshot = gameCatalog.TryGet();
        foreach (var id in stockSelection.SelectedKeys)
        {
            if (TryResolveStock(runtime, id, out var item, out _))
                stock.Add(item);
            else if (snapshot?.Items.GetValueOrDefault(id) is { } catalogItem)
                catalogOnly.Add(new StockGroup(catalogItem.ItemId, catalogItem.Name, []));
        }
        stock.Sort(CompareByName);
        catalogOnly.Sort(CompareByName);
        return (stock, catalogOnly);
    }

    private static int CompareByName(StockGroup left, StockGroup right)
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(left.ItemName, right.ItemName);
        return byName != 0 ? byName : left.ItemId.CompareTo(right.ItemId);
    }

    private bool SelectionHasCarried(QuartermasterRuntimeSnapshot runtime)
    {
        foreach (var id in stockSelection.SelectedKeys)
            if (TryResolveStock(runtime, id, out _, out var carried) && carried)
                return true;
        return false;
    }

    private bool TryResolveStock(QuartermasterRuntimeSnapshot runtime, uint itemId, out StockGroup item, out bool carried)
    {
        var scopeKey = catalogMode ? BrowserScope.AllKey : workbench.ScopeKey;
        var scoped = ResolveLookup(ref scopedLookup, runtime, scopeKey);
        if (scoped.ById.TryGetValue(itemId, out item!))
        {
            carried = scoped.Carried.Contains(itemId);
            return true;
        }
        if (scopeKey != BrowserScope.AllKey)
        {
            var all = ResolveLookup(ref allLookup, runtime, BrowserScope.AllKey);
            if (all.ById.TryGetValue(itemId, out item!))
            {
                carried = all.Carried.Contains(itemId);
                return true;
            }
        }
        item = null!;
        carried = false;
        return false;
    }

    private StockLookup ResolveLookup(ref StockLookup? slot, QuartermasterRuntimeSnapshot runtime, string scopeKey)
    {
        if (slot is { } cached && cached.Revision == runtime.Revision && cached.ScopeKey == scopeKey)
            return cached;
        var items = !catalogMode && scopeKey == workbench.ScopeKey
            ? GetStockItemsWithListingDemand(runtime, scopeKey)
            : StockItemsWithListingDemand(runtime, scopeKey);
        var byId = new Dictionary<uint, StockGroup>(items.Count);
        var carried = new HashSet<uint>();
        foreach (var item in items)
        {
            byId.TryAdd(item.ItemId, item);
            if (item.PlayerQuantity > 0)
                carried.Add(item.ItemId);
        }
        return slot = new StockLookup(runtime.Revision, scopeKey, byId, carried);
    }

    /// <summary>Drops selected IDs that exist neither in stock nor in the game catalog.</summary>
    private void PruneSelection(QuartermasterRuntimeSnapshot runtime)
    {
        if (prunedRevision == runtime.Revision || gameCatalog.TryGet() is not { } snapshot)
            return;
        prunedRevision = runtime.Revision;
        if (stockSelection.Count == 0)
            return;
        List<uint>? stale = null;
        foreach (var id in stockSelection.SelectedKeys)
            if (!snapshot.Items.ContainsKey(id) && !TryResolveStock(runtime, id, out _, out _))
                (stale ??= []).Add(id);
        if (stale is not null)
            foreach (var id in stale)
                stockSelection.SetSelected(id, false);
    }

    private static Vector4 Scale(Vector4 color, float scale, float alpha) =>
        new(color.X * scale, color.Y * scale, color.Z * scale, alpha);

    private void DrawSingleStockSelection(QuartermasterRuntimeSnapshot runtime)
    {
        if (stockSelection.Count != 1 ||
            !TryResolveStock(runtime, stockSelection.SelectedKeys.First(), out var single, out _))
            return;

        var itemId = single.ItemId;
        var navigationQualities = (listingSession.Draft?.Assignments.Where(a => a.ItemId == itemId).Select(a => a.Quality) ?? [])
            .Concat(runtime.Browser.Listings.Where(l => l.ItemId == itemId).Select(ListingRetainerProjection.Quality))
            .Distinct().OrderBy(q => q).ToArray();
        void DrawListingsNavigation()
        {
            if (navigationQualities.Length == 0) return;
            var quality = navigationQualities.Contains(workbench.SelectedStockListingQuality)
                ? workbench.SelectedStockListingQuality : navigationQualities[0];
            if (ImGui.SmallButton("Show in Market")) showInListings(runtime, new(itemId, quality));
        }
        var listingPlan = ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner);
        var demand = ResolveStockListingEvaluation(runtime).Items
            .Where(item => item.ItemId == single.ItemId && item.IsPlanned)
            .ToArray();
        if (listingPlan is not null && demand.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(single.ItemName);
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
            DrawListingsNavigation();
            if (navigationQualities.Length > 0) ImGui.SameLine();
            var transferPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner);
            var linked = transferPlan is not null && runtime.State.TransferPlanListingLinks.Any(link =>
                link.StowagePlanId == transferPlan.Id && link.ListingPlanId == listingPlan.Id &&
                link.ItemId == selectedDemand.ItemId && link.Quality == selectedDemand.Quality);
            if (linked)
                ImGui.TextDisabled($"Linked to {transferPlan!.Name}");
            else if (transferPlan?.IsEphemeral is not true)
            {
                var destination = transferPlan?.Name ?? "Transfer plan";
                if (ImGui.SmallButton($"Prepare target in {destination}"))
                    transferPlans.PrepareListingTarget(runtime, selectedDemand);
            }
        }
        else if (navigationQualities.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(single.ItemName);
            DrawListingsNavigation();
        }
    }

    private void DrawTableColumnsToolbar<TRow>(
        DalamudTableProjection<TRow> table,
        string id,
        string context)
    {

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

        DrawMaterialsMenuItem(runtime, item.ItemId);

        if (item.PlayerQuantity == 0 || !runtime.Owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.BeginMenu("Stow carried"))
        {
            DrawStowDestinations(runtime, [item]);
            ImGui.EndMenu();
        }
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

    private void DrawStowDestinations(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items)
    {
        void Add(ulong? id) { try { transferPlans.AddStock(runtime, items, true, id); stockSelection.Clear(); } catch (Exception exception) { transferExecution.ReportInlineError(exception.Message); } }
        if (ImGui.MenuItem("Any retainer")) Add(null);
        reviewRegistry.RegisterLastButton("quartermaster.stock.stow.any", "Stow in any retainer", items.Count > 0, () => Add(null), "Available");
        foreach (var retainer in runtime.Retainers.Values.Where(r => r.Owner.Matches(runtime.Owner) && r.IsCurrentlyAssigned is not false && r.IsUiAccessible is not false).OrderBy(r => r.RetainerName))
        {
            if (ImGui.MenuItem(retainer.RetainerName)) Add(retainer.RetainerId);
            reviewRegistry.RegisterLastButton($"quartermaster.stock.stow.{retainer.RetainerId}", $"Stow in {retainer.RetainerName}", items.Count > 0,
                () => Add(retainer.RetainerId), "Available");
        }
    }

    private void UpsertSelectedStockRules(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<StockGroup> items,
        bool stowCarried)
    {
        if (!runtime.Owner.HasStableIdentity || items.Count == 0)
            return;
        try { transferPlans.AddStock(runtime, items, stowCarried); transferExecution.ClearInlineError(); }
        catch (Exception exception) { transferExecution.ReportInlineError(exception.Message); }
    }

    private static string StockPlanState(StockWorkbenchRow row) =>
        row.Rule is null
            ? "Not in plan"
            : !row.Rule.Enabled
                ? "Off"
                : row.Line is null ? "Draft target" : row.Line.Action switch
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


    public void FocusFromListings(QuartermasterRuntimeSnapshot runtime, ListingItemKey group)
    {
        LeaveCatalog();
        stockSelection.Clear();
        stockSelection.SetSelected(group.ItemId, true);
        workbench.SelectedStockListingQuality = group.Quality;
        workbench.ScopeKey = BrowserScope.AllKey;
        workbench.ItemFilterState.SetExpression(string.Empty);
        workbench.FocusedStockItemId = group.ItemId;
        requestStockView();
        workbench.View = WorkbenchView.Stowage;
    }


    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };


    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;
}

internal sealed record StockLookup(long Revision, string ScopeKey, Dictionary<uint, StockGroup> ById, HashSet<uint> Carried);

/// <summary>
/// The item IDs a table currently shows, cached against the source they were built from, with
/// a lazily built set so counting selected-and-visible stays O(min(selection, visible)).
/// </summary>
internal sealed class VisibleItemIds
{
    private object? source;
    private HashSet<uint>? set;

    public uint[] Ids { get; private set; } = [];

    public bool IsFor(object candidate) => ReferenceEquals(source, candidate);

    public void Set(object newSource, uint[] ids)
    {
        if (ReferenceEquals(source, newSource))
            return;
        source = newSource;
        Ids = ids;
        set = null;
    }

    public int CountSelected(TableSelectionModel<uint> selection)
    {
        if (selection.Count == 0 || Ids.Length == 0)
            return 0;
        var count = 0;
        if (selection.Count < Ids.Length)
        {
            set ??= [.. Ids];
            foreach (var id in selection.SelectedKeys)
                if (set.Contains(id))
                    count++;
            return count;
        }
        foreach (var id in Ids)
            if (selection.IsSelected(id))
                count++;
        return count;
    }
}
