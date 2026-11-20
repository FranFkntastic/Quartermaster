using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Windows;
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
    private readonly CourierSession courier;
    private bool sendRequested;
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
    private OwnerScope? selectionOwner;
    private readonly DalamudSelectionBar selectionBar = new("RQStockSelectionBar");
    private int stockProjectionBuildCount;
    private int visibleStockCount;
    private int renderedStockRowCount;
    private double stockDrawMilliseconds;
    private bool stowRequested;
    private bool catalogMode;
    private bool optionsRequested;
    private readonly CourierSettingsSection courierSettings;
    /// <summary>Another character whose cached stock the browser shows instead of the logged-in one's.</summary>
    private OwnerScope? viewedCharacter;
    private (IReadOnlyList<StockGroup> QueryItems, AccountStockProjection Account, StockWorkbenchRow[] Rows)? viewedRows;

    private const string AccountScopeLabel = "All accessible stock";
    /// <summary>Query identity for the account view; never a <see cref="BrowserScope"/> key.</summary>
    private const string AccountQueryScope = "account";
    /// <summary>The account-wide view (every observed character) rather than one of the logged-in character's scopes.</summary>
    private bool accountWide = true;
    private readonly DalamudTableProjection<StockWorkbenchRow> accountTable;
    private (long Revision, AccountStockProjection Account, IReadOnlyList<StockGroup> Items)? accountItems;
    private readonly DalamudExpandableRows expansion = new();
    /// <summary>Mirror of <see cref="expansion"/> by item ID, so unexpanded rows cost no key strings per frame.</summary>
    private readonly HashSet<uint> expandedItems = [];
    private AccountStockProjection? expansionPrunedFor;
    private readonly Dictionary<uint, string> expansionKeys = [];
    // The row being drawn: the Item cell owns the selection target, so it needs the row's order.
    private IReadOnlyList<StockWorkbenchRow>? drawingRows;
    private IReadOnlyList<StockWorkbenchRow>? keyedRows;
    private uint[] drawingKeys = [];
    private int drawingIndex;
    /// <summary>A location popup the agent bridge asked to open; the detail row opens it in its own ID scope.</summary>
    private string? pendingLocationPopup;

    private bool IsAccountMode => accountWide && viewedCharacter is null && !catalogMode;

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
        GameItemCatalog gameCatalog,
        CourierSession courier)
    {
        this.courier = courier;
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
        courierSettings = new CourierSettingsSection(courier, reviewRegistry);
        stockTable = CreateTable();
        accountTable = CreateAccountTable();
    }

    public StockWorkspaceSnapshot Snapshot() =>
        new(visibleStockCount, renderedStockRowCount, stockProjectionBuildCount, stockTable.ApplyCount, stockDrawMilliseconds);

    public void ClearSelection() => stockSelection.Clear();


    /// <summary>One character's stock: the logged-in character's scopes, or another character's cached stock.</summary>
    private DalamudTableProjection<StockWorkbenchRow> CreateTable() => new(
    [
        ItemColumn(),
        new(
            "Locations",
            1.5f,
            row => LocationsText(row),
            Flags: ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide,
            Id: "locations",
            Draw: row => { ImGui.TextUnformatted(LocationsText(row)); if (ImGui.IsItemHovered()) ImGui.SetTooltip(LocationsTooltip(row)); }),
        new(
            "Player",
            64,
            row => row.Item.PlayerQuantity.ToString("N0"),
            row => row.Item.PlayerQuantity,
            TextColor: row => row.Item.PlayerQuantity == 0 ? Disabled() : null,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "player"),
        new(
            "Saddlebag",
            72,
            // Another character's saddlebag is never observed here.
            row => viewedCharacter is null ? row.Item.SaddlebagQuantity.ToString("N0") : "—",
            row => row.Item.SaddlebagQuantity,
            TextColor: row => viewedCharacter is not null || row.Item.SaddlebagQuantity == 0 ? Disabled() : null,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "saddlebag"),
        new(
            "Retainers",
            132,
            row => string.IsNullOrEmpty(row.RetainerHint) ? row.AccessibleRetainerQuantity.ToString("N0") : $"{row.AccessibleRetainerQuantity:N0} · {row.RetainerHint}",
            row => row.AccessibleRetainerQuantity,
            Draw: DrawRetainerCell,
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "retainers",
            HeaderTooltip: "Units in accessible retainer storage, and which retainer holds them."),
        ListingShortfallColumn(),
        TargetColumn(),
        PlanStateColumn(),
    ], DalamudTableSelection<StockWorkbenchRow>.Multi(stockSelection, row => row.Item.ItemId));

    /// <summary>Every observed character at once: totals split between here and elsewhere, with locations.</summary>
    private DalamudTableProjection<StockWorkbenchRow> CreateAccountTable() => new(
    [
        ItemColumn(),
        new(
            "Total",
            72,
            row => (row.Account?.Total ?? 0).ToString("N0"),
            row => row.Account?.Total ?? 0,
            TextColor: row => row.Account?.Total is > 0 ? null : Disabled(),
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "total",
            HeaderTooltip: "Units across every observed character."),
        new(
            "Here",
            64,
            row => (row.Account?.Here ?? 0).ToString("N0"),
            row => row.Account?.Here ?? 0,
            TextColor: row => row.Account?.Here is > 0 ? null : Disabled(),
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "here",
            HeaderTooltip: "Units on the logged-in character: bags, saddlebag and retainers."),
        new(
            "Other chars",
            84,
            row => (row.Account?.Elsewhere ?? 0).ToString("N0"),
            row => row.Account?.Elsewhere ?? 0,
            TextColor: row => row.Account?.Elsewhere is > 0 ? null : Disabled(),
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "elsewhere",
            HeaderTooltip: "Units on your other observed characters and their retainers."),
        new(
            "Locations",
            1.2f,
            row => row.Account?.LocationSummary ?? "—",
            row => (row.Account?.Locations.Count ?? 0, row.Account?.CharacterCount ?? 0),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row =>
            {
                if (row.Account is null) ImGui.TextDisabled("—");
                else ImGui.TextUnformatted(row.Account.LocationSummary);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(LocationsTooltip(row));
            },
            DrawContextMenu: DrawStockRowContextMenu,
            Id: "locations",
            HeaderTooltip: "Where the item sits. Expand a row for every location."),
        TargetColumn(),
        ListingShortfallColumn(),
        PlanStateColumn(),
    ], DalamudTableSelection<StockWorkbenchRow>.Multi(stockSelection, row => row.Item.ItemId));

    private static Vector4 Disabled() => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

    /// <summary>
    /// The item cell owns its row's selection target instead of the table: the expander is submitted
    /// first so it wins the hover, and the full-cell target behind it takes every other click.
    /// </summary>
    private DalamudTableColumn<StockWorkbenchRow> ItemColumn() => new(
        "Item",
        1.8f,
        row => row.Item.ItemName,
        row => row.Item.ItemName,
        ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide,
        Draw: DrawItemCell,
        Id: "item");

    private DalamudTableColumn<StockWorkbenchRow> ListingShortfallColumn() => new(
        "Listing shortfall",
        138,
        row => StockListingShortfall(row.ListingDemand),
        row => StockListingShortfall(row.ListingDemand),
        ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
        DrawContextMenu: DrawStockRowContextMenu,
        Id: "listing-shortfall",
        HeaderTooltip: "Units still needed by linked Listing Plans.");

    private DalamudTableColumn<StockWorkbenchRow> TargetColumn() => new(
        "Target",
        80,
        StockTarget,
        row => row.Rule?.TargetQuantity ?? (row.Sends > 0 ? row.Sends : -1),
        TextColor: row => row.Rule is null && row.Sends == 0 ? Disabled() : null,
        DrawContextMenu: DrawStockRowContextMenu,
        Id: "target",
        HeaderTooltip: "The open plan's target, or what its courier lines send.");

    private DalamudTableColumn<StockWorkbenchRow> PlanStateColumn() => new(
        "Plan state",
        1f,
        StockPlanState,
        row => StockPlanState(row),
        ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide,
        TextColor: row => row.Rule is null
            ? Disabled()
            : TransferPresentation.ActionColor(row.Line?.Action),
        DrawContextMenu: DrawStockRowContextMenu,
        Id: "plan-state");

    private static string StockTarget(StockWorkbenchRow row) =>
        row.Rule is { } rule ? rule.TargetQuantity.ToString("N0")
        : row.Sends > 0 ? $"Send {row.Sends:N0}"
        : "—";

    private static void DrawRetainerCell(StockWorkbenchRow row)
    {
        if (row.AccessibleRetainerQuantity == 0)
        {
            ImGui.TextDisabled("0");
            return;
        }
        ImGui.TextUnformatted(row.AccessibleRetainerQuantity.ToString("N0"));
        if (string.IsNullOrEmpty(row.RetainerHint))
            return;
        ImGui.SameLine();
        ImGui.TextDisabled(row.RetainerHint);
    }


    public void Draw(QuartermasterRuntimeSnapshot runtime, FrameTimings timings)
    {
        var drawStarted = Stopwatch.GetTimestamp();
        ClearSelectionOnOwnerChange(runtime.Owner);
        PruneSelection(runtime);
        if (catalogMode)
        {
            timings.Section("stock catalog");
            DrawCatalog(runtime);
            stockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
            return;
        }
        timings.Section("stock filter");
        var viewed = ViewedStock(runtime);
        var account = courier.AccountStock(runtime);
        PruneExpansion(account);
        var accountMode = IsAccountMode;
        var projection = viewed ?? runtime.Browser;
        var availableItems = viewed?.Items ?? (accountMode
            ? GetAccountItemsWithListingDemand(runtime, account)
            : GetStockItemsWithListingDemand(runtime, workbench.ScopeKey));
        // The toolbar runs first and updates IsInputActive. Use the same editing
        // snapshot for both cache lookups so a focus transition cannot rebuild twice.
        var isEditing = workbench.ItemFilterState.IsInputActive;
        var scopeKey = viewed is not null ? BrowserScope.AllKey : accountMode ? AccountQueryScope : workbench.ScopeKey;
        var characterBeforeToolbar = viewedCharacter;
        DrawStockToolbar(runtime, availableItems, scopeKey, isEditing);
        if (characterBeforeToolbar != viewedCharacter || accountMode != IsAccountMode)
            return; // The stock projection belongs to the previous character or view.
        var result = queries.QueryItems(
            projection,
            workbench.ItemFilterState.Expression,
            scopeKey,
            isEditing,
            runtime.PlanningRevision,
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
        timings.Section("stock projection");
        var sourceRows = viewed is null
            ? ResolveStockWorkbenchProjection(runtime, visibleItems, selectedPlan, account, accountMode)
            : ResolveViewedRows(runtime, visibleItems, account);
        timings.Section("stock table");
        DrawSelectionBar(runtime, stockVisible, canSelectVisible: true);
        if (viewed is null)
            DrawSingleStockSelection(runtime);
        var table = accountMode ? accountTable : stockTable;
        DrawTableColumnsToolbar(table, "RQStockColumns", "Visible stock");
        // Resizable forces inner vertical borders; keep them out of the body so they do not cut
        // through the full-width location rows.
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.NoBordersInBodyUntilResize |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var tableHeight = Math.Max(180, ImGui.GetContentRegionAvail().Y);
        renderedStockRowCount = 0;
        if (table.Begin(
                // The character table keeps its column ids and order, so it keeps its saved layout.
                accountMode ? "RQStockAccountV1" : "RQStockWorkbenchV5",
                new DalamudTableLayout(
                    new Vector2(0, tableHeight),
                    flags,
                    FreezeRows: 1)))
        {
            IReadOnlyList<StockWorkbenchRow> rows;
            unsafe
            {
                rows = table.Apply(sourceRows, ImGui.TableGetSortSpecs());
            }
            renderedStockRowCount = DrawStockRows(runtime, table, rows);
            DalamudTableSelectionRenderer.EndRows(stockSelection);
            table.End();
        }
        drawingRows = null;
        stockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
    }

    /// <summary>
    /// Draws the rows clipped to the viewport. An expanded row has a variable-height location table
    /// under it, so the clipper runs over each stretch of collapsed rows and expanded rows draw directly.
    /// </summary>
    private int DrawStockRows(QuartermasterRuntimeSnapshot runtime, DalamudTableProjection<StockWorkbenchRow> table, IReadOnlyList<StockWorkbenchRow> rows)
    {
        // Apply returns the same list until the rows, sort or filters change.
        if (!ReferenceEquals(keyedRows, rows))
        {
            drawingKeys = new uint[rows.Count];
            for (var index = 0; index < rows.Count; index++)
                drawingKeys[index] = rows[index].Item.ItemId;
            keyedRows = rows;
        }
        drawingRows = rows;
        if (expandedItems.Count == 0)
            return table.DrawClippedRows(rows, (_, index) => DrawStockRow(table, rows, index));

        var rendered = 0;
        var start = 0;
        for (var index = 0; index <= rows.Count; index++)
        {
            // A row without locations draws no expander, so it never holds a detail row open.
            if (index < rows.Count && !(expandedItems.Contains(rows[index].Item.ItemId) && HasLocations(rows[index])))
                continue;
            if (index > start)
            {
                var offset = start;
                rendered += table.DrawClippedRows(new RowRange<StockWorkbenchRow>(rows, offset, index - offset),
                    (_, local) => DrawStockRow(table, rows, offset + local));
            }
            if (index < rows.Count)
            {
                var row = rows[index];
                DrawStockRow(table, rows, index);
                expansion.DrawDetail(ExpansionKey(row.Item.ItemId), () => DrawLocations(runtime, row));
                rendered++;
            }
            start = index + 1;
        }
        return rendered;
    }

    private void DrawStockRow(DalamudTableProjection<StockWorkbenchRow> table, IReadOnlyList<StockWorkbenchRow> rows, int index)
    {
        drawingIndex = index;
        // The Item cell draws the selection target itself, after its expander.
        table.DrawSaneRow(rows, index, $"##stock-row:{rows[index].Item.ItemId}", selectable: false);
    }

    private void DrawItemCell(StockWorkbenchRow row)
    {
        var itemId = row.Item.ItemId;
        var cell = ImGui.GetCursorPos();
        var style = ImGui.GetStyle();
        var expanderWidth = ImGui.CalcTextSize(">").X + (style.FramePadding.X * 2f);
        if (HasLocations(row))
        {
            var key = ExpansionKey(itemId);
            var open = expansion.DrawToggle(key);
            MirrorExpansion(itemId, open);
            reviewRegistry.RegisterLastButton(
                $"quartermaster.stock.expand.{itemId}",
                $"{(open ? "Collapse" : "Expand")} {row.Item.ItemName} locations",
                true,
                () => SetExpanded(itemId, !expandedItems.Contains(itemId)),
                open ? "Expanded" : "Collapsed");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(open ? "Hide locations" : "Show every location");
        }
        ImGui.SetCursorPos(cell);
        if (drawingRows is not null && drawingIndex < drawingKeys.Length && drawingKeys[drawingIndex] == itemId)
        {
            DalamudTableSelectionRenderer.DrawRow(stockSelection, drawingKeys, drawingIndex, $"##stock-select:{itemId}",
                new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
            var stockSelected = stockSelection.IsSelected(itemId);
            reviewRegistry.RegisterLastButton(
                $"quartermaster.stock.select.{itemId}",
                $"Select {row.Item.ItemName} for stock actions",
                true,
                () => stockSelection.SetSelected(itemId, !stockSelection.IsSelected(itemId)),
                stockSelected ? "Selected" : "Available");
            if (ImGui.BeginPopupContextItem($"##stock-context:{itemId}"))
            {
                DrawStockRowContextMenu(row);
                ImGui.EndPopup();
            }
        }
        ImGui.SetCursorPos(new Vector2(cell.X + expanderWidth + style.ItemInnerSpacing.X, cell.Y));
        ImGui.TextUnformatted(row.Item.ItemName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(LocationsTooltip(row));
    }

    // ---- Locations --------------------------------------------------------------------------

    private string ExpansionKey(uint itemId)
    {
        if (!expansionKeys.TryGetValue(itemId, out var key))
            expansionKeys[itemId] = key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key;
    }

    private void MirrorExpansion(uint itemId, bool open)
    {
        if (open) expandedItems.Add(itemId);
        else expandedItems.Remove(itemId);
    }

    private void SetExpanded(uint itemId, bool open)
    {
        expansion.SetExpanded(ExpansionKey(itemId), open);
        MirrorExpansion(itemId, open);
    }

    /// <summary>Collapses items no character holds any more; the scope or filter hiding a row keeps it open.</summary>
    private void PruneExpansion(AccountStockProjection account)
    {
        if (ReferenceEquals(expansionPrunedFor, account))
            return;
        expansionPrunedFor = account;
        if (expandedItems.Count == 0)
            return;
        foreach (var itemId in expandedItems.Where(id => !account.ById.ContainsKey(id)).ToArray())
            SetExpanded(itemId, false);
    }

    /// <summary>The locations this view covers: every character's in the account view, else the viewed character's.</summary>
    private IEnumerable<StockLocation> ViewLocations(StockWorkbenchRow row)
    {
        if (row.Account is not { } account)
            return [];
        if (IsAccountMode)
            return account.Locations;
        if (viewedCharacter is { } viewed)
            return account.On(viewed);
        return account.Locations.Where(location => location.IsCurrent);
    }

    private bool HasLocations(StockWorkbenchRow row) => ViewLocations(row).Any();

    private string LocationsText(StockWorkbenchRow row)
    {
        var locations = ViewLocations(row).ToArray();
        return locations.Length == 0 ? "—" : string.Join("; ", locations.Select(location => $"{location.Container}: {location.Quantity:N0}"));
    }

    private string LocationsTooltip(StockWorkbenchRow row)
    {
        var locations = ViewLocations(row).ToArray();
        if (locations.Length == 0)
            return row.Item.Stacks.Count == 0 ? $"{row.Item.ItemName}: not in stock" : row.Item.ItemName;
        return string.Join("\n", locations.Select(location =>
            $"{(IsAccountMode ? location.Label : location.Container)}: {location.Quantity:N0}{QualitySuffix(location)}"));
    }

    private static string QualitySuffix(StockLocation location) =>
        location.HqQuantity == 0 ? string.Empty
        : location.NqQuantity == 0 ? " HQ"
        : $" ({location.NqQuantity:N0} NQ · {location.HqQuantity:N0} HQ)";

    private static string QualityLabel(StockLocation location) =>
        location.HqQuantity == 0 ? "NQ"
        : location.NqQuantity == 0 ? "HQ"
        : $"NQ {location.NqQuantity:N0} · HQ {location.HqQuantity:N0}";

    /// <summary>
    /// The expanded breakdown: the logged-in character first (bags, saddlebag, retainers in game
    /// order), then other characters by name, each row with the actions that start from it.
    /// </summary>
    private void DrawLocations(QuartermasterRuntimeSnapshot runtime, StockWorkbenchRow row)
    {
        var locations = ViewLocations(row).ToArray();
        if (locations.Length == 0)
        {
            ImGui.TextDisabled("No observed stock.");
            return;
        }
        var showCharacter = IsAccountMode;
        if (!ImGui.BeginTable("##stock-locations", showCharacter ? 5 : 4, DalamudNestedTableFlags.Child))
            return;
        try
        {
            if (showCharacter)
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            var previousCharacter = -1;
            foreach (var location in locations)
            {
                ImGui.TableNextRow();
                ImGui.PushID(LocationToken(location));
                try
                {
                    if (showCharacter)
                    {
                        ImGui.TableNextColumn();
                        // Each character is named once, on its first location.
                        if (location.CharacterOrder != previousCharacter)
                        {
                            if (location.IsCurrent)
                                ImGui.TextColored(QuartermasterPalette.Info, location.Character.CharacterName);
                            else
                                ImGui.TextUnformatted(location.Character.CharacterName);
                            if (location.IsCurrent && ImGui.IsItemHovered())
                                ImGui.SetTooltip("Logged in here");
                        }
                        previousCharacter = location.CharacterOrder;
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(location.Container);
                    ImGui.TableNextColumn();
                    if (location.HqQuantity == 0) ImGui.TextDisabled(QualityLabel(location));
                    else ImGui.TextUnformatted(QualityLabel(location));
                    ImGui.TableNextColumn();
                    var quantity = location.Quantity.ToString("N0");
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(quantity).X));
                    ImGui.TextUnformatted(quantity);
                    ImGui.TableNextColumn();
                    DrawLocationActions(runtime, row, location);
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.EndTable(); }
    }

    private static string LocationToken(StockLocation location)
    {
        var container = location.Kind switch
        {
            BrowserScopeKind.Player => "bags",
            BrowserScopeKind.Saddlebag => "saddlebag",
            _ => location.RetainerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "retainer",
        };
        return $"{location.Character.LocalContentId ?? 0}.{container}";
    }

    private void DrawLocationActions(QuartermasterRuntimeSnapshot runtime, StockWorkbenchRow row, StockLocation location)
    {
        var itemId = row.Item.ItemId;
        var control = $"quartermaster.stock.location.{itemId}.{LocationToken(location)}";
        var first = true;
        void Gap()
        {
            if (!first) ImGui.SameLine();
            first = false;
        }

        // Courier lines move bags and retainer stock; the saddlebag is not a courier source.
        if (location.Kind is BrowserScopeKind.Player or BrowserScopeKind.Retainer &&
            transferPlans.HasCourierDestinations(runtime, location.Character))
        {
            var canSend = runtime.Owner.HasStableIdentity;
            var popup = $"send:{control}";
            Gap();
            ImGui.BeginDisabled(!canSend);
            if (ImGui.SmallButton("Send to…"))
                ImGui.OpenPopup("##location-send");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canSend ? $"Send {location.Quantity:N0} from {location.Label}" : "Log in to plan courier lines");
            reviewRegistry.RegisterLastButton($"{control}.send", $"Send {row.Item.ItemName} from {location.Label}", canSend,
                () => pendingLocationPopup = popup, "Available");
            if (pendingLocationPopup == popup)
            {
                ImGui.OpenPopup("##location-send");
                pendingLocationPopup = null;
            }
            if (DalamudOwnedPopup.BeginOpaquePopup("##location-send"))
            {
                ImGui.TextDisabled($"From {location.Label}");
                if (LocationItem(runtime, itemId, location) is { } source)
                    DrawSendDestinations(runtime, [source], location.Character, $"{control}.send");
                ImGui.EndPopup();
            }
        }

        if (!location.IsCurrent || !runtime.Owner.HasStableIdentity || runtime.Browser.FindItem(itemId) is not { } current)
            return;
        if (location.Kind == BrowserScopeKind.Player)
        {
            if (current.PlayerQuantity == 0)
                return;
            var popup = $"stow:{control}";
            Gap();
            if (ImGui.SmallButton("Stow…"))
                ImGui.OpenPopup("##location-stow");
            reviewRegistry.RegisterLastButton($"{control}.stow", $"Stow carried {row.Item.ItemName}", true,
                () => pendingLocationPopup = popup, "Available");
            if (pendingLocationPopup == popup)
            {
                ImGui.OpenPopup("##location-stow");
                pendingLocationPopup = null;
            }
            if (DalamudOwnedPopup.BeginOpaquePopup("##location-stow"))
            {
                DrawStowDestinations(runtime, [current]);
                ImGui.EndPopup();
            }
            return;
        }
        Gap();
        if (ImGui.SmallButton("Add to plan"))
            UpsertSelectedStockRules(runtime, [current], stowCarried: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add to the open Transfer plan, then set a target to retrieve");
        reviewRegistry.RegisterLastButton($"{control}.add-to-plan", $"Add {row.Item.ItemName} to the current Transfer Plan", true,
            () => UpsertSelectedStockRules(runtimeSnapshots.Current, [current], stowCarried: false), "Available");
    }

    /// <summary>A character's own stock group for an item: the logged-in projection or that character's cached one.</summary>
    private StockGroup? CharacterItem(QuartermasterRuntimeSnapshot runtime, OwnerScope character, uint itemId) =>
        character.Matches(runtime.Owner)
            ? runtime.Browser.FindItem(itemId)
            : courier.Stock(runtime, character)?.FindItem(itemId);

    /// <summary>The item's stacks in one location only, so a courier line starts from exactly that container.</summary>
    private StockGroup? LocationItem(QuartermasterRuntimeSnapshot runtime, uint itemId, StockLocation location) =>
        CharacterItem(runtime, location.Character, itemId) is { } item
            ? item with
            {
                Stacks = item.Stacks.Where(stack => stack.ScopeKind == location.Kind &&
                    (location.Kind != BrowserScopeKind.Retainer || stack.RetainerId == location.RetainerId)).ToArray(),
            }
            : null;

    /// <summary>
    /// Characters that can send an item in the account view, logged-in first: those holding it in
    /// bags or retainers.
    /// </summary>
    private static IReadOnlyList<StockLocation> SendSources(AccountStockItem? item) =>
        item is null ? [] : item.Locations
            .Where(location => location.Kind is BrowserScopeKind.Player or BrowserScopeKind.Retainer && location.Quantity > 0)
            .GroupBy(location => location.CharacterOrder)
            .OrderBy(group => group.Key)
            .Select(group => group.First())
            .ToArray();

    /// <summary>
    /// The source a bulk send uses for an item in the account view: the logged-in character when it
    /// holds some, else the only character that does. Null when several other characters hold it.
    /// </summary>
    private static OwnerScope? DefaultSendSource(AccountStockItem? item)
    {
        if (item is null)
            return null;
        // Locations run logged-in character first; this runs per selected item per frame, so no LINQ.
        StockLocation? first = null;
        foreach (var location in item.Locations)
        {
            if (location.Kind is not (BrowserScopeKind.Player or BrowserScopeKind.Retainer) || location.Quantity <= 0)
                continue;
            if (first is null)
            {
                if (location.IsCurrent)
                    return location.Character;
                first = location;
            }
            else if (location.CharacterOrder != first.CharacterOrder)
                return null;
        }
        return first?.Character;
    }

    private IReadOnlyList<StockGroup> GetStockItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, string scopeKey)
    {
        if (availableStockRevision != runtime.PlanningRevision || availableStockScopeKey != scopeKey)
        {
            availableStockItems = StockItemsWithListingDemand(runtime, scopeKey);
            availableStockRevision = runtime.PlanningRevision;
            availableStockScopeKey = scopeKey;
        }
        return availableStockItems;
    }

    /// <summary>
    /// The other character's cached stock while that scope is chosen; the scope lapses when the
    /// character logs in here, becomes the carrier, or has nothing observed.
    /// </summary>
    private BrowserProjection? ViewedStock(QuartermasterRuntimeSnapshot runtime)
    {
        if (viewedCharacter is not { } character || catalogMode)
            return null;
        if (!character.Matches(runtime.Owner) && !CourierSession.IsCarrier(runtime, character) &&
            courier.Stock(runtime, character) is { } stock)
            return stock;
        SetViewedCharacter(null);
        return null;
    }

    private void SetViewedCharacter(OwnerScope? character)
    {
        if (character is null ? viewedCharacter is null : viewedCharacter?.Matches(character) == true)
            return;
        ClearStockSelection();
        viewedCharacter = character;
    }

    private void ViewCharacter(OwnerScope character)
    {
        SetViewedCharacter(character);
        workbench.FocusedStockItemId = null;
        LeaveCatalog();
    }

    /// <summary>Every observed character's stock together; the selection survives, as it does between scopes.</summary>
    private void ViewAccount()
    {
        accountWide = true;
        SetViewedCharacter(null);
        workbench.FocusedStockItemId = null;
        LeaveCatalog();
    }

    private void ViewHereScope(string scopeKey)
    {
        accountWide = false;
        workbench.ScopeKey = scopeKey;
        workbench.FocusedStockItemId = null;
        SetViewedCharacter(null);
        LeaveCatalog();
    }

    /// <summary>Another character's rows: its stock only, with no plan, target or listing columns of its own.</summary>
    private IReadOnlyList<StockWorkbenchRow> ResolveViewedRows(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> queryItems, AccountStockProjection account)
    {
        if (viewedRows is { } cached && ReferenceEquals(cached.QueryItems, queryItems) && ReferenceEquals(cached.Account, account))
            return cached.Rows;
        var retainers = TransferWorkbenchPresentation.AccessibleRetainerIds(runtime.Retainers, viewedCharacter!);
        var rows = queryItems
            .Select(item => new StockWorkbenchRow(
                item,
                TransferWorkbenchPresentation.AccessibleStorageQuantity(item, ItemQualityPolicy.Any, retainers),
                null,
                null,
                [],
                account.ById.GetValueOrDefault(item.ItemId),
                RetainerHint: RetainerHint(runtime, item, retainers)))
            .ToArray();
        viewedRows = (queryItems, account, rows);
        return rows;
    }

    /// <summary>"Grungel" when one accessible retainer holds the item, "3 retainers" when several do.</summary>
    private static string RetainerHint(QuartermasterRuntimeSnapshot runtime, StockGroup item, IReadOnlySet<ulong> accessible)
    {
        var held = item.Stacks
            .Where(stack => stack.ScopeKind == BrowserScopeKind.Retainer && stack.Quantity > 0 &&
                            stack.RetainerId is { } id && accessible.Contains(id))
            .Select(stack => stack.RetainerId!.Value)
            .Distinct()
            .ToArray();
        return held.Length switch
        {
            0 => string.Empty,
            1 => CourierSession.ContainerLabel(runtime, held[0]),
            _ => $"{held.Length} retainers",
        };
    }

    /// <summary>Every observed character's stock, plus items the logged-in character's listing plan wants.</summary>
    private IReadOnlyList<StockGroup> GetAccountItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, AccountStockProjection account)
    {
        if (accountItems is not { } cached || cached.Revision != runtime.PlanningRevision || !ReferenceEquals(cached.Account, account))
            accountItems = cached = (runtime.PlanningRevision, account, WithListingDemand(runtime, account.Items, BrowserScope.AllKey));
        return cached.Items;
    }

    internal static IReadOnlyList<StockGroup> StockItemsWithListingDemand(QuartermasterRuntimeSnapshot runtime, string scopeKey) =>
        WithListingDemand(runtime, runtime.Browser.GetItems(scopeKey), scopeKey);

    private static IReadOnlyList<StockGroup> WithListingDemand(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> stock, string scopeKey)
    {
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
        StowagePlan? selectedPlan,
        AccountStockProjection account,
        bool accountMode)
    {
        if (stockWorkbenchProjection is { } cached &&
            cached.PlanningRevision == runtime.PlanningRevision &&
            cached.PlanId == selectedPlan?.Id &&
            ReferenceEquals(cached.Account, account) &&
            cached.AccountMode == accountMode &&
            (ReferenceEquals(cached.QueryItems, queryItems) ||
             cached.QueryItems.Select(item => item.ItemId).SequenceEqual(queryItems.Select(item => item.ItemId))))
            return cached.Rows;

        var rules = selectedPlan is null
            ? new Dictionary<uint, TargetPlanItem>()
            : runtime.State.PlanItems
                .Where(rule => rule.StowagePlanId == selectedPlan.Id && rule.Courier is null)
                .GroupBy(rule => rule.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
        var sends = selectedPlan is null
            ? new Dictionary<uint, int>()
            : runtime.State.PlanItems
                .Where(rule => rule.StowagePlanId == selectedPlan.Id && rule.Courier is not null && rule.Enabled)
                .GroupBy(rule => rule.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(rule => rule.TargetQuantity));
        var accessible = TransferWorkbenchPresentation.AccessibleRetainerIds(runtime.Retainers, runtime.Owner);
        var evaluated = selectedPlan is null
            ? new Dictionary<Guid, StowageEvaluationLine>()
            : StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, runtime.Owner, selectedPlan.Id)?
                .Lines.ToDictionary(line => line.RuleId) ?? [];
        var demandByItem = ResolveStockListingEvaluation(runtime).Items.ToLookup(candidate => candidate.ItemId);
        var rows = queryItems
            .Select(item =>
            {
                rules.TryGetValue(item.ItemId, out var rule);
                evaluated.TryGetValue(rule?.Id ?? Guid.Empty, out var line);
                var demand = demandByItem[item.ItemId].ToArray();
                // The account view's items hold every character's stacks; retainer figures there
                // come from the locations instead.
                var accessibleRetainerQuantity = accountMode ? 0 : TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    item,
                    ItemQualityPolicy.Any,
                    accessible);
                return new StockWorkbenchRow(item, accessibleRetainerQuantity, rule, line, demand,
                    account.ById.GetValueOrDefault(item.ItemId),
                    sends.GetValueOrDefault(item.ItemId),
                    accountMode ? string.Empty : RetainerHint(runtime, item, accessible));
            })
            .ToArray();
        stockWorkbenchProjection = new(runtime.PlanningRevision, selectedPlan?.Id, queryItems, rows, account, accountMode);
        stockProjectionBuildCount++;
        return rows;
    }

    private ListingPlanEvaluation ResolveStockListingEvaluation(QuartermasterRuntimeSnapshot runtime)
    {
        if (stockListingEvaluation is null || listingEvaluationRevision != runtime.PlanningRevision)
        {
            stockListingEvaluation = ListingPlanEvaluator.Evaluate(
                ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner), runtime.Browser);
            listingEvaluationRevision = runtime.PlanningRevision;
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
            DrawScopeCombo(runtime);
            return;
        }
        var context = queries.EnsureItemContext(sourceItems, viewedCharacter ?? projection.Owner, scopeKey, isEditing, runtime.PlanningRevision);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var trailingWidth = SearchReference.ButtonWidth + spacing + ScopeComboWidth() + spacing +
            DalamudUiLayout.ButtonWidth("…") + spacing;
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
        DrawScopeCombo(runtime);

        ImGui.SameLine();
        var openOptions = ImGui.SmallButton("…##RQStockOptions");
        reviewRegistry.RegisterLastButton("quartermaster.stock.options.open", "Open stock and courier options", true,
            () => optionsRequested = true, "Available");
        if (courier.TakeSettingsRequest() | openOptions | optionsRequested)
        {
            ImGui.OpenPopup("RQStockOptions");
            optionsRequested = false;
        }
        if (DalamudOwnedPopup.BeginOpaquePopup("RQStockOptions"))
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
            courierSettings.Draw(runtime);
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

    /// <summary>"Wei Ning", or a neutral name while the logged-in character is not identified.</summary>
    private static string HereName(QuartermasterRuntimeSnapshot runtime) =>
        string.IsNullOrWhiteSpace(runtime.Owner.CharacterName) ? "This character" : runtime.Owner.CharacterName;

    /// <summary>A logged-in character scope as the dropdown lists it: "Wei Ning (all)", "Player", "Grungel".</summary>
    private static string HereScopeLabel(QuartermasterRuntimeSnapshot runtime, BrowserScope scope) =>
        scope.Kind == BrowserScopeKind.All ? $"{HereName(runtime)} (all)" : scope.Label;

    /// <summary>
    /// The scope dropdown: the account view, then the logged-in character's scopes, then other
    /// observed characters, then the catalog, each section under its own header.
    /// </summary>
    private void DrawScopeCombo(QuartermasterRuntimeSnapshot runtime)
    {
        var projection = runtime.Browser;
        var characters = courier.Browsable(runtime).ToArray();
        ImGui.SetNextItemWidth(ScopeComboWidth());
        var selectedScope = projection.Scopes.FirstOrDefault(scope => scope.Key == workbench.ScopeKey) ?? projection.Scopes[0];
        var selectedLabel = catalogMode ? CatalogPicker.ScopeLabel
            : viewedCharacter is { } viewed ? CourierSession.CharacterLabel(viewed)
            : accountWide ? AccountScopeLabel
            : selectedScope.Kind == BrowserScopeKind.All ? HereScopeLabel(runtime, selectedScope)
            : $"{HereName(runtime)} · {selectedScope.Label}";
        if (ImGui.BeginCombo("##RQStockScope", selectedLabel, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable($"{AccountScopeLabel}##stock-scope:account", IsAccountMode))
                ViewAccount();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", courier.AccountStock(runtime).Characters
                    .Select(character => character.Matches(runtime.Owner) ? $"{HereName(runtime)} (logged in)" : character.CharacterName)
                    .Prepend("Every observed character:")));

            ImGui.Separator();
            ImGui.TextDisabled(HereName(runtime));
            ImGui.Indent();
            foreach (var scope in projection.Scopes)
                if (ImGui.Selectable($"{HereScopeLabel(runtime, scope)}##stock-scope:{scope.Key}",
                        !catalogMode && viewedCharacter is null && !accountWide && scope.Key == workbench.ScopeKey))
                    ViewHereScope(scope.Key);
            ImGui.Unindent();

            if (characters.Length > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Other characters");
                ImGui.Indent();
                foreach (var character in characters)
                    if (ImGui.Selectable($"{CourierSession.CharacterLabel(character)}##stock-scope:character:{character.LocalContentId}",
                            !catalogMode && viewedCharacter?.Matches(character) == true))
                        ViewCharacter(character);
                ImGui.Unindent();
            }

            ImGui.Separator();
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
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.scope.account",
            "Browse every observed character's stock",
            true,
            ViewAccount,
            IsAccountMode ? "Selected" : "Available");
        foreach (var scope in projection.Scopes)
        {
            var key = scope.Key;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.stock.scope.here.{key.Replace(':', '.')}",
                $"Browse {HereScopeLabel(runtime, scope)}",
                true,
                () => ViewHereScope(key),
                !catalogMode && viewedCharacter is null && !accountWide && key == workbench.ScopeKey ? "Selected" : "Available");
        }
        foreach (var character in characters)
        {
            var target = character;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.stock.scope.character.{character.LocalContentId}",
                $"Browse {character.CharacterName}'s stock",
                true,
                () => ViewCharacter(target),
                !catalogMode && viewedCharacter?.Matches(character) == true ? "Selected" : "Available");
        }
    }

    private void EnterCatalog()
    {
        if (catalogMode)
            return;
        catalogMode = true;
        SetViewedCharacter(null);
        workbench.FocusedStockItemId = null;
    }

    private void LeaveCatalog() => catalogMode = false;

    private void DrawCatalog(QuartermasterRuntimeSnapshot runtime)
    {
        DrawStockToolbar(runtime, [], workbench.ScopeKey, workbench.ItemFilterState.IsInputActive);
        if (viewedCharacter is not null)
            return;
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
    /// scope switches within a character; actions resolve each selected ID through stock first, then the catalog.
    /// </summary>
    private void DrawSelectionBar(QuartermasterRuntimeSnapshot runtime, VisibleItemIds visible, bool canSelectVisible)
    {
        var count = stockSelection.Count;
        selectionBar.Draw(count, QuartermasterPalette.Info, hasSelection =>
        {
            if (hasSelection && visible.CountSelected(stockSelection) is var visibleSelected && visibleSelected != count)
            {
                var detail = $"{visibleSelected:N0} visible";
                DalamudUiLayout.SameLineIfFits(detail);
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(detail);
            }
            DrawSelectionActions(runtime, visible, canSelectVisible, hasSelection);
        });
    }

    private void DrawSelectionActions(
        QuartermasterRuntimeSnapshot runtime,
        VisibleItemIds visible,
        bool canSelectVisible,
        bool hasSelection)
    {
        var selectVisibleEnabled = canSelectVisible && visible.Ids.Length > 0;
        DalamudUiLayout.SameLineIfFits("Select visible");
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

        // Another character's stock can only be sent; the logged-in character's own actions do not apply.
        if (viewedCharacter is null)
            DrawOwnSelectionActions(runtime, hasSelection);
        DrawSendSelection(runtime, hasSelection);

        DalamudUiLayout.SameLineIfFits("Clear");
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

    private void DrawOwnSelectionActions(QuartermasterRuntimeSnapshot runtime, bool hasSelection)
    {
        var canPlan = hasSelection && runtime.Owner.HasStableIdentity;
        DalamudUiLayout.SameLineIfFits("Add to plan");
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

        DalamudUiLayout.SameLineIfFits("Add to item group");
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
        DalamudUiLayout.SameLineIfFits("Stow carried");
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
        if (DalamudOwnedPopup.BeginOpaquePopup("RQStockStow"))
        {
            DrawStowDestinations(runtime, ResolveSelection(runtime).Stock.Where(item => item.PlayerQuantity > 0).ToArray());
            ImGui.EndPopup();
        }
    }

    private void DrawSendSelection(QuartermasterRuntimeSnapshot runtime, bool hasSelection)
    {
        // Sending to another character exists only once another character is known.
        if (!transferPlans.HasCourierDestinations(runtime, viewedCharacter))
            return;
        var send = hasSelection ? ResolveSendSelection(runtime) : (null, [], 0);
        var canSend = runtime.Owner.HasStableIdentity && send.Items.Count > 0 &&
                      transferPlans.HasCourierDestinations(runtime, send.Source);
        DalamudUiLayout.SameLineIfFits("Send to…");
        ImGui.BeginDisabled(!canSend);
        if (ImGui.Button("Send to…"))
            sendRequested = true;
        ImGui.EndDisabled();
        if (IsAccountMode && hasSelection && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(SendSelectionTooltip(runtime, send));
        reviewRegistry.RegisterLastButton("quartermaster.stock.send", "Choose another character to send to", canSend,
            () => sendRequested = true, send.Source is { } from ? $"From {from.CharacterName}; {send.Items.Count:N0} items" : "Selected owned stock");
        if (sendRequested)
        {
            ImGui.OpenPopup("RQStockSend");
            sendRequested = false;
        }
        if (DalamudOwnedPopup.BeginOpaquePopup("RQStockSend"))
        {
            if (IsAccountMode && send.Source is { } source)
                ImGui.TextDisabled($"From {(source.Matches(runtime.Owner) ? HereName(runtime) : source.CharacterName)}");
            DrawSendDestinations(runtime, send.Items, send.Source, "quartermaster.stock.send");
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// What the selection's Send sends and from whom. Outside the account view, the scope's character
    /// sends what it holds. In the account view each item has a default source (the logged-in
    /// character when it holds some, else the only character that does); the logged-in character's
    /// items go first, else one other character's. Items on other sources are left for their
    /// location rows.
    /// </summary>
    private (OwnerScope? Source, IReadOnlyList<StockGroup> Items, int Skipped) ResolveSendSelection(QuartermasterRuntimeSnapshot runtime)
    {
        if (!IsAccountMode)
            return (viewedCharacter, ResolveSelection(runtime).Stock.Where(CanSend).ToArray(), 0);
        var account = courier.AccountStock(runtime);
        var resolved = new List<(OwnerScope Source, uint ItemId)>();
        var unresolved = 0;
        foreach (var id in stockSelection.SelectedKeys)
        {
            if (DefaultSendSource(account.ById.GetValueOrDefault(id)) is { } source)
                resolved.Add((source, id));
            else if (account.ById.ContainsKey(id))
                unresolved++;
        }
        if (resolved.Count == 0)
            return (null, [], unresolved);
        var chosen = resolved.FirstOrDefault(entry => entry.Source.Matches(runtime.Owner)).Source
                     ?? (resolved.All(entry => entry.Source.Matches(resolved[0].Source)) ? resolved[0].Source : null);
        if (chosen is null)
            return (null, [], resolved.Count + unresolved);
        var items = resolved
            .Where(entry => entry.Source.Matches(chosen))
            .Select(entry => CharacterItem(runtime, chosen, entry.ItemId))
            .OfType<StockGroup>()
            .Where(CanSend)
            .ToList();
        items.Sort(CompareByName);
        return (chosen, items, resolved.Count + unresolved - items.Count);
    }

    private static string SendSelectionTooltip(QuartermasterRuntimeSnapshot runtime, (OwnerScope? Source, IReadOnlyList<StockGroup> Items, int Skipped) send)
    {
        if (send.Source is not { } source)
            return send.Skipped > 0
                ? "The selected items sit on several other characters. Expand a row to send from one location."
                : "Nothing selected that a character holds in bags or retainers.";
        var from = source.Matches(runtime.Owner) ? HereName(runtime) : source.CharacterName;
        return send.Skipped > 0
            ? $"Sends {send.Items.Count:N0} items from {from}.\n{send.Skipped:N0} others are on other characters: expand their rows to send from there."
            : $"Sends {send.Items.Count:N0} items from {from}.";
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
    /// catalog-only items. In the account view, items only other characters hold are catalog-only
    /// here. IDs found nowhere are skipped.
    /// </summary>
    private (List<StockGroup> Stock, List<StockGroup> CatalogOnly) ResolveSelection(QuartermasterRuntimeSnapshot runtime)
    {
        var stock = new List<StockGroup>();
        var catalogOnly = new List<StockGroup>();
        var snapshot = gameCatalog.TryGet();
        var account = IsAccountMode ? courier.AccountStock(runtime) : null;
        foreach (var id in stockSelection.SelectedKeys)
        {
            if (TryResolveStock(runtime, id, out var item, out _))
                stock.Add(item);
            else if (snapshot?.Items.GetValueOrDefault(id) is { } catalogItem)
                catalogOnly.Add(new StockGroup(catalogItem.ItemId, catalogItem.Name, []));
            else if (account?.ById.GetValueOrDefault(id) is { } elsewhere)
                catalogOnly.Add(new StockGroup(id, elsewhere.Item.ItemName, []));
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
        // Another character's scope resolves only against that character's stock.
        if (!catalogMode && viewedCharacter is { } viewed)
        {
            item = courier.Stock(runtime, viewed)?.FindItem(itemId)!;
            carried = item?.PlayerQuantity > 0;
            return item is not null;
        }
        // The account view's own actions (plan, stow) act on the logged-in character's stock.
        var scopeKey = catalogMode || IsAccountMode ? BrowserScope.AllKey : workbench.ScopeKey;
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
        if (slot is { } cached && cached.Revision == runtime.PlanningRevision && cached.ScopeKey == scopeKey)
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
        return slot = new StockLookup(runtime.PlanningRevision, scopeKey, byId, carried);
    }

    /// <summary>
    /// Clears the selection when a different character logs in. An owner without identity (logged
    /// out or between areas) is ignored, so a blip back to the same character keeps the selection.
    /// </summary>
    private void ClearSelectionOnOwnerChange(OwnerScope owner)
    {
        if (!owner.HasStableIdentity && string.IsNullOrWhiteSpace(owner.CharacterName))
            return;
        if (selectionOwner is not null && !selectionOwner.Matches(owner))
            ClearStockSelection();
        selectionOwner = owner;
    }

    /// <summary>Drops selected IDs that exist neither in stock nor in the game catalog.</summary>
    private void PruneSelection(QuartermasterRuntimeSnapshot runtime)
    {
        if (prunedRevision == runtime.PlanningRevision || gameCatalog.TryGet() is not { } snapshot)
            return;
        prunedRevision = runtime.PlanningRevision;
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
            if (ImGui.BeginCombo("##stocklistingquality", TransferPresentation.QualityLabel(workbench.SelectedStockListingQuality)))
            {
                foreach (var item in demand)
                    if (ImGui.Selectable(TransferPresentation.QualityLabel(item.Quality), item.Quality == workbench.SelectedStockListingQuality))
                        workbench.SelectedStockListingQuality = item.Quality;
                ImGui.EndCombo();
            }
            var selectedDemand = demand.Single(item => item.Quality == workbench.SelectedStockListingQuality);
            ImGui.TextDisabled(StockListingShortfall([selectedDemand]));
            DrawListingsNavigation();
            if (navigationQualities.Length > 0) ImGui.SameLine();
            // Listing targets live in Transfer plans; with a Courier plan open they go to the unsaved Transfer plan.
            var transferPlan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner) is { Kind: StowagePlanKind.Transfer } open ? open : null;
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

        var buttonWidth = DalamudUiLayout.ButtonWidth("Columns");
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

        if (viewedCharacter is { } viewed)
        {
            DrawSendMenu(runtime, row.Item, viewed);
            return;
        }

        // The account view's row holds every character's stacks; plan and stow act on the logged-in
        // character's own units only.
        var accountMode = IsAccountMode;
        var item = accountMode ? runtime.Browser.FindItem(row.Item.ItemId) : row.Item;

        if (!runtime.Owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.MenuItem("Add to plan"))
        {
            if (item is not null)
                UpsertSelectedStockRules(runtime, [item], stowCarried: false);
            else
                transferPlans.AddItems(runtime, [(row.Item.ItemId, row.Item.ItemName)]);
        }
        if (!runtime.Owner.HasStableIdentity)
            ImGui.EndDisabled();

        if (ImGui.MenuItem("Add to item group"))
            AddStockItemsToItemGroup(runtime, [row.Item]);

        DrawMaterialsMenuItem(runtime, row.Item.ItemId);

        var canStow = item is { PlayerQuantity: > 0 } && runtime.Owner.HasStableIdentity;
        if (!canStow)
            ImGui.BeginDisabled();
        if (ImGui.BeginMenu("Stow carried"))
        {
            DrawStowDestinations(runtime, [item!]);
            ImGui.EndMenu();
        }
        if (!canStow)
            ImGui.EndDisabled();

        if (!accountMode)
        {
            if (item is not null)
                DrawSendMenu(runtime, item, null);
            return;
        }
        // One holder sends directly; several get a "Send from" menu each.
        var sources = SendSources(row.Account);
        if (sources.Count == 1)
        {
            if (CharacterItem(runtime, sources[0].Character, row.Item.ItemId) is { } held)
                DrawSendMenu(runtime, held, sources[0].Character);
            return;
        }
        foreach (var source in sources)
            if (CharacterItem(runtime, source.Character, row.Item.ItemId) is { } held)
                DrawSendMenu(runtime, held, source.Character,
                    $"Send from {(source.IsCurrent ? HereName(runtime) : source.Character.CharacterName)}##send-from:{source.Character.LocalContentId}");
    }

    /// <summary>A menu of courier destinations for one character's units of an item.</summary>
    private void DrawSendMenu(QuartermasterRuntimeSnapshot runtime, StockGroup item, OwnerScope? source, string label = "Send to")
    {
        if (runtime.Owner.HasStableIdentity && CanSend(item) && transferPlans.HasCourierDestinations(runtime, source) &&
            ImGui.BeginMenu(label))
        {
            DrawSendDestinations(runtime, [item], source);
            ImGui.EndMenu();
        }
    }

    private static bool CanSend(StockGroup item) => item.PlayerQuantity > 0 || item.RetainerQuantity > 0;

    /// <summary>
    /// Adds courier lines for the items' units on <paramref name="source"/> (the logged-in character
    /// when null), one line per container holding them, to the chosen destination.
    /// </summary>
    private void DrawSendDestinations(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items, OwnerScope? source, string? controlPrefix = null)
    {
        transferPlans.DrawCourierDestinations(runtime, (character, retainer) =>
        {
            transferPlans.AddCourier(runtime, items, character, retainer, source);
            stockSelection.Clear();
        }, controlPrefix: controlPrefix, source: source);
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
            var quality = TransferPresentation.QualityLabel(item.Quality);
            if (!item.NeedUnits.IsKnown)
                return $"{quality} unknown";
            return $"{quality} {item.NeedUnits.Value:N0}";
        }));
    }


    public void FocusFromListings(QuartermasterRuntimeSnapshot runtime, ListingItemKey group)
    {
        LeaveCatalog();
        SetViewedCharacter(null);
        accountWide = true;
        stockSelection.Clear();
        stockSelection.SetSelected(group.ItemId, true);
        // Open where it sits; an item no character holds simply has no locations to show.
        SetExpanded(group.ItemId, true);
        workbench.SelectedStockListingQuality = group.Quality;
        workbench.ScopeKey = BrowserScope.AllKey;
        workbench.ItemFilterState.SetExpression(string.Empty);
        workbench.FocusedStockItemId = group.ItemId;
        requestStockView();
        workbench.View = WorkbenchView.Stowage;
    }




    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;
}

internal sealed record StockLookup(long Revision, string ScopeKey, Dictionary<uint, StockGroup> ById, HashSet<uint> Carried);

/// <summary>A window onto part of a list, so the row clipper can run over one stretch of rows.</summary>
internal sealed class RowRange<T>(IReadOnlyList<T> rows, int start, int count) : IReadOnlyList<T>
{
    public int Count => count;

    public T this[int index] => (uint)index < (uint)count ? rows[start + index] : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < count; index++)
            yield return rows[start + index];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

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
