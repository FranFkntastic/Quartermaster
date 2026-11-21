using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
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
    /// <summary>The viewed character's container: <see cref="BrowserScope.AllKey"/>, its bags or one of its retainers.</summary>
    private string viewedScopeKey = BrowserScope.AllKey;
    private (BrowserProjection Stock, string ScopeKey, IReadOnlyList<StockGroup> Items, Dictionary<uint, StockGroup> ById)? viewedItems;
    /// <summary>The viewed character is on another service account (a sibling profile's cache); resolved each frame.</summary>
    private bool viewedExternal;

    // ---- Other service accounts ----
    /// <summary>Muted amber for stock that sits on another service account and must be traded by hand.</summary>
    private static readonly Vector4 TradeTint = new(.86f, .68f, .42f, .9f);
    private const string ExternalNodeTooltip = "Other service account — items must be traded directly";
    private const string ExternalLocationTooltip = "On another service account — must be traded directly";
    private const string ExternalOnlyTooltip = "Only on another service account — trade required";
    private const string ExternalMenuLabel = "Other account — trade in game";

    // ---- Scope rail ----
    private bool railOpen = true;
    /// <summary>Character nodes the user opened or closed, by content ID; the logged-in one starts open.</summary>
    private readonly Dictionary<ulong, bool> railExpanded = [];
    private RailCounts? railCounts;
    private const string RailCatalogLabel = "Item Catalog";

    private const string AccountScopeLabel = "All accessible";
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
            // Another local character's saddlebag is never observed here; another account's client observes its own.
            row => viewedCharacter is null || viewedExternal ? row.Item.SaddlebagQuantity.ToString("N0") : "—",
            row => row.Item.SaddlebagQuantity,
            TextColor: row => viewedCharacter is not null && !viewedExternal || row.Item.SaddlebagQuantity == 0 ? Disabled() : null,
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
            HeaderTooltip: "Units on your other observed characters and their retainers, including other service accounts' cached stock."),
        new(
            "Locations",
            1.2f,
            row => row.Account?.LocationSummary ?? "—",
            row => (row.Account?.Locations.Count ?? 0, row.Account?.CharacterCount ?? 0),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row =>
            {
                if (row.Account is null) ImGui.TextDisabled("—");
                else if (row.Account.OnlyExternal) ImGui.TextColored(TradeTint, row.Account.LocationSummary);
                else ImGui.TextUnformatted(row.Account.LocationSummary);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Account is { OnlyExternal: true } ? $"{ExternalOnlyTooltip}\n{LocationsTooltip(row)}" : LocationsTooltip(row));
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
        HeaderTooltip: "The open plan's target, or what its courier lines move.");

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
        : row.Sends > 0 ? $"Move {row.Sends:N0}"
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
        // The workspace always draws inside its own child, so the table keeps one ID (and saved
        // layout) whether the rail is open or not.
        if (railOpen)
        {
            timings.Section("stock rail");
            var railWidth = RailWidth * ImGuiHelpers.GlobalScale;
            if (ImGui.BeginChild("##RQStockRail", new Vector2(railWidth, 0), false))
                DrawScopeRail(runtime);
            ImGui.EndChild();
            ImGui.SameLine();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var x = max.X + (ImGui.GetStyle().ItemSpacing.X / 2f);
            ImGui.GetWindowDrawList().AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), ImGui.GetColorU32(ImGuiCol.Separator), 1);
        }
        if (ImGui.BeginChild("##RQStockMain", Vector2.Zero, false))
            DrawWorkspace(runtime, timings);
        ImGui.EndChild();
        drawingRows = null;
        stockDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
    }

    private void DrawWorkspace(QuartermasterRuntimeSnapshot runtime, FrameTimings timings)
    {
        viewedExternal = false;
        if (catalogMode)
        {
            timings.Section("stock catalog");
            DrawCatalog(runtime);
            return;
        }
        timings.Section("stock filter");
        var viewed = ViewedStock(runtime);
        viewedExternal = viewed is not null && courier.IsExternal(runtime, viewedCharacter!);
        var account = courier.AccountStock(runtime);
        PruneExpansion(account);
        var accountMode = IsAccountMode;
        var projection = viewed ?? runtime.Browser;
        var availableItems = viewed is not null ? ViewedItems(viewed).Items : accountMode
            ? GetAccountItemsWithListingDemand(runtime, account)
            : GetStockItemsWithListingDemand(runtime, workbench.ScopeKey);
        // The toolbar runs first and updates IsInputActive. Use the same editing
        // snapshot for both cache lookups so a focus transition cannot rebuild twice.
        var isEditing = workbench.ItemFilterState.IsInputActive;
        var scopeKey = viewed is not null ? viewedScopeKey : accountMode ? AccountQueryScope : workbench.ScopeKey;
        var queryAccount = accountMode ? account : null;
        DrawStockToolbar(runtime, availableItems, scopeKey, isEditing, queryAccount);
        var result = queries.QueryItems(
            projection,
            workbench.ItemFilterState.Expression,
            scopeKey,
            isEditing,
            runtime.PlanningRevision,
            availableItems,
            queryAccount);
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
            : ResolveViewedRows(visibleItems, viewed, account);
        timings.Section("stock table");
        if (viewedExternal)
            DrawExternalBanner(runtime);
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
    }

    private static readonly string TradeIcon = FontAwesomeIcon.ExchangeAlt.ToIconString();

    /// <summary>One muted strip over another service account's stock: visible here, moved only by trading in game.</summary>
    private void DrawExternalBanner(QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(TradeTint, TradeIcon);
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(TradeTint, "Other service account (local cache) — items here must be traded directly.");
        if (ImGui.IsItemHovered() && viewedCharacter is { } viewed)
            ImGui.SetTooltip($"Read from {courier.ExternalProfile(runtime, viewed) ?? "another XIVLauncher profile"}'s Quartermaster cache; it updates when that client observes its stock.\nQuartermaster cannot send, stow or plan from another service account.");
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
            $"{(IsAccountMode ? location.Label : location.Container)}: {location.Quantity:N0}{QualitySuffix(location)}" +
            (IsAccountMode && location.IsExternalAccount ? " · other account" : string.Empty)));
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
    /// order), then other characters by name. Each row is one hit target whose right-click menu
    /// holds the actions that start from that container.
    /// </summary>
    private void DrawLocations(QuartermasterRuntimeSnapshot runtime, StockWorkbenchRow row)
    {
        var locations = ViewLocations(row).ToArray();
        if (locations.Length == 0)
        {
            ImGui.TextDisabled("No observed stock.");
            return;
        }
        if (!ImGui.BeginTable("##stock-locations", 4, DalamudNestedTableFlags.Child))
            return;
        try
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            var previousCharacter = -1;
            foreach (var location in locations)
            {
                ImGui.TableNextRow();
                ImGui.PushID(LocationToken(location));
                try
                {
                    ImGui.TableNextColumn();
                    // Each character is named once, on its first location; the row target spans every column.
                    var named = location.CharacterOrder != previousCharacter;
                    previousCharacter = location.CharacterOrder;
                    var tint = named && location.IsCurrent ? QuartermasterPalette.Info
                        : named && location.IsExternalAccount ? TradeTint
                        : (Vector4?)null;
                    if (tint is { } color)
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                    // Another account's character carries its trade hint beside its name.
                    ImGui.Selectable(!named ? string.Empty
                        : location.IsExternalAccount ? $"{location.Character.CharacterName} · Trade"
                        : location.Character.CharacterName, false,
                        ImGuiSelectableFlags.SpanAllColumns);
                    if (tint is not null)
                        ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(location.IsExternalAccount
                            ? $"{ExternalLocationTooltip}{(location.ExternalProfileLabel is { } profile ? $" ({profile})" : string.Empty)}."
                            : named && location.IsCurrent ? "Logged in here. Right-click for actions." : "Right-click for actions.");
                    DrawLocationMenu(runtime, row, location);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(location.Container);
                    ImGui.TableNextColumn();
                    if (location.HqQuantity == 0) ImGui.TextDisabled(QualityLabel(location));
                    else ImGui.TextUnformatted(QualityLabel(location));
                    ImGui.TableNextColumn();
                    var quantity = location.Quantity.ToString("N0");
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(quantity).X));
                    ImGui.TextUnformatted(quantity);
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

    /// <summary>
    /// A location row's right-click menu: send from exactly that container, and for the logged-in
    /// character, stow from its bags or add its storage to the plan.
    /// </summary>
    private void DrawLocationMenu(QuartermasterRuntimeSnapshot runtime, StockWorkbenchRow row, StockLocation location)
    {
        var itemId = row.Item.ItemId;
        var control = $"quartermaster.stock.location.{itemId}.{LocationToken(location)}";
        if (location.IsExternalAccount)
        {
            DrawExternalLocationMenu(row, location, control);
            return;
        }
        var identified = runtime.Owner.HasStableIdentity;
        // Courier lines move bags and retainer stock; the saddlebag is not a courier source.
        var canSend = location.Kind is BrowserScopeKind.Player or BrowserScopeKind.Retainer &&
                      transferPlans.HasCourierDestinations(runtime, location.Character);
        var current = location.IsCurrent && identified ? runtime.Browser.FindItem(itemId) : null;
        var canStow = location.Kind == BrowserScopeKind.Player && current is { PlayerQuantity: > 0 };
        var canPlan = location.Kind is BrowserScopeKind.Saddlebag or BrowserScopeKind.Retainer && current is not null;

        reviewRegistry.RegisterLastButton($"{control}.menu", $"Open actions for {row.Item.ItemName} in {location.Label}", true,
            () => pendingLocationPopup = $"menu:{control}", "Available");
        if (canSend)
            reviewRegistry.RegisterLastButton($"{control}.send", $"Send {row.Item.ItemName} from {location.Label}", identified,
                () => pendingLocationPopup = $"send:{control}", identified ? "Available" : "Log in to plan courier lines");
        if (canStow)
            reviewRegistry.RegisterLastButton($"{control}.stow", $"Stow carried {row.Item.ItemName}", true,
                () => pendingLocationPopup = $"stow:{control}", "Available");
        // With a Courier plan open, the add sends from exactly this container.
        StockGroup PlanItem(QuartermasterRuntimeSnapshot at) =>
            transferPlans.CourierPlanOpen(at) && LocationItem(at, itemId, location) is { } here ? here : current!;
        if (canPlan)
            reviewRegistry.RegisterLastButton($"{control}.add-to-plan", $"Add {row.Item.ItemName} to the current plan", true,
                () => UpsertSelectedStockRules(runtimeSnapshots.Current, [PlanItem(runtimeSnapshots.Current)], stowCarried: false), "Available");

        if (pendingLocationPopup is { } pending && pending.Length == control.Length + 5 && pending.EndsWith(control, StringComparison.Ordinal))
        {
            var popup = pending.StartsWith("menu:", StringComparison.Ordinal) ? "##location-menu"
                : pending.StartsWith("send:", StringComparison.Ordinal) ? "##location-send"
                : "##location-stow";
            ImGui.OpenPopup(popup);
            pendingLocationPopup = null;
        }

        if (ImGui.BeginPopupContextItem("##location-menu"))
        {
            ImGui.TextDisabled($"{location.Character.CharacterName} · {location.Container}");
            ImGui.Separator();
            if (canSend)
            {
                if (ImGui.BeginMenu("Send to", identified))
                {
                    if (LocationItem(runtime, itemId, location) is { } source)
                        DrawSendDestinations(runtime, [source], location.Character, $"{control}.send");
                    ImGui.EndMenu();
                }
                if (!identified && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Log in to plan courier lines");
            }
            if (canStow && ImGui.BeginMenu("Stow carried"))
            {
                DrawStowDestinations(runtime, [current!]);
                ImGui.EndMenu();
            }
            if (canPlan)
            {
                if (ImGui.MenuItem("Add to plan"))
                    UpsertSelectedStockRules(runtime, [PlanItem(runtime)], stowCarried: false);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(transferPlans.CourierPlanOpen(runtime)
                        ? "Add to the open Courier plan"
                        : "Add to the open Transfer plan, then set a target to retrieve");
            }
            if (!canSend && !canStow && !canPlan)
                ImGui.TextDisabled("Nothing to do from here");
            ImGui.EndPopup();
        }

        // The agent bridge cannot hover a submenu open, so its send and stow open these instead.
        if (canSend && DalamudOwnedPopup.BeginOpaquePopup("##location-send"))
        {
            ImGui.TextDisabled($"From {location.Label}");
            if (LocationItem(runtime, itemId, location) is { } source)
                DrawSendDestinations(runtime, [source], location.Character, $"{control}.send");
            ImGui.EndPopup();
        }
        if (canStow && DalamudOwnedPopup.BeginOpaquePopup("##location-stow"))
        {
            DrawStowDestinations(runtime, [current!]);
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Another service account's location: courier, stow and plan actions would refuse it
    /// (OutsideClientRoster), so its menu only says how it moves.
    /// </summary>
    private void DrawExternalLocationMenu(StockWorkbenchRow row, StockLocation location, string control)
    {
        reviewRegistry.RegisterLastButton($"{control}.menu", $"Open actions for {row.Item.ItemName} in {location.Label}", true,
            () => pendingLocationPopup = $"menu:{control}", "Other service account; trade in game");
        if (pendingLocationPopup is { } pending && pending.Length == control.Length + 5 && pending.EndsWith(control, StringComparison.Ordinal))
        {
            if (pending.StartsWith("menu:", StringComparison.Ordinal))
                ImGui.OpenPopup("##location-menu");
            pendingLocationPopup = null;
        }
        if (!ImGui.BeginPopupContextItem("##location-menu"))
            return;
        ImGui.TextDisabled($"{location.Character.CharacterName} · {location.Container}");
        ImGui.Separator();
        ImGui.BeginDisabled();
        ImGui.MenuItem(ExternalMenuLabel);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ExternalLocationTooltip);
        ImGui.EndPopup();
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
    /// bags or retainers. Another service account's characters never send; they trade in game.
    /// </summary>
    private static IReadOnlyList<StockLocation> SendSources(AccountStockItem? item) =>
        item is null ? [] : item.Locations
            .Where(location => location.Kind is BrowserScopeKind.Player or BrowserScopeKind.Retainer && location.Quantity > 0 &&
                               !location.IsExternalAccount)
            .GroupBy(location => location.CharacterOrder)
            .OrderBy(group => group.Key)
            .Select(group => group.First())
            .ToArray();

    /// <summary>
    /// The source a bulk send uses for an item in the account view: the logged-in character when it
    /// holds some, else the only character that does. Null when several other characters hold it.
    /// Another service account's holdings are never a source.
    /// </summary>
    private static OwnerScope? DefaultSendSource(AccountStockItem? item)
    {
        if (item is null)
            return null;
        // Locations run logged-in character first; this runs per selected item per frame, so no LINQ.
        StockLocation? first = null;
        foreach (var location in item.Locations)
        {
            if (location.Kind is not (BrowserScopeKind.Player or BrowserScopeKind.Retainer) || location.Quantity <= 0 ||
                location.IsExternalAccount)
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
        // Another service account's character stays viewable even when it is the carrier.
        var stock = courier.ExternalStock(runtime, character) ??
                    (!character.Matches(runtime.Owner) && !CourierSession.IsCarrier(runtime, character)
                        ? courier.Stock(runtime, character)
                        : null);
        if (stock is not null)
        {
            // A retainer that left the character takes its scope with it.
            if (viewedScopeKey != BrowserScope.AllKey && !stock.Scopes.Any(scope => scope.Key == viewedScopeKey))
                viewedScopeKey = BrowserScope.AllKey;
            return stock;
        }
        SetViewedCharacter(null);
        return null;
    }

    /// <summary>A character's browsable stock: another service account's cached stock, else this client's cached stock.</summary>
    private BrowserProjection? ViewStock(QuartermasterRuntimeSnapshot runtime, OwnerScope character) =>
        courier.ExternalStock(runtime, character) ?? courier.Stock(runtime, character);

    /// <summary>The viewed character's items in its chosen container, cached per projection and container.</summary>
    private (BrowserProjection Stock, string ScopeKey, IReadOnlyList<StockGroup> Items, Dictionary<uint, StockGroup> ById) ViewedItems(BrowserProjection stock)
    {
        if (viewedItems is { } cached && ReferenceEquals(cached.Stock, stock) && cached.ScopeKey == viewedScopeKey)
            return cached;
        var items = stock.GetItems(viewedScopeKey);
        var byId = new Dictionary<uint, StockGroup>(items.Count);
        foreach (var item in items)
            byId.TryAdd(item.ItemId, item);
        viewedItems = (stock, viewedScopeKey, items, byId);
        return viewedItems.Value;
    }

    private void SetViewedCharacter(OwnerScope? character)
    {
        if (character is null ? viewedCharacter is null : viewedCharacter?.Matches(character) == true)
            return;
        ClearStockSelection();
        viewedCharacter = character;
        viewedScopeKey = BrowserScope.AllKey;
    }

    /// <summary>Another character's stock: all of it, or one container (its bags or one retainer).</summary>
    private void ViewCharacter(OwnerScope character, string scopeKey = BrowserScope.AllKey)
    {
        SetViewedCharacter(character);
        viewedScopeKey = scopeKey;
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

    /// <summary>
    /// Another character's rows: its stock only, with no plan, target or listing columns of its own.
    /// Retainers resolve against the projection's own cache, so another service account's retainers
    /// are named and counted from its sibling cache.
    /// </summary>
    private IReadOnlyList<StockWorkbenchRow> ResolveViewedRows(IReadOnlyList<StockGroup> queryItems, BrowserProjection viewed, AccountStockProjection account)
    {
        if (viewedRows is { } cached && ReferenceEquals(cached.QueryItems, queryItems) && ReferenceEquals(cached.Account, account))
            return cached.Rows;
        var retainers = TransferWorkbenchPresentation.AccessibleRetainerIds(viewed.RetainerStock, viewedCharacter!);
        var rows = queryItems
            .Select(item => new StockWorkbenchRow(
                item,
                TransferWorkbenchPresentation.AccessibleStorageQuantity(item, ItemQualityPolicy.Any, retainers),
                null,
                null,
                [],
                account.ById.GetValueOrDefault(item.ItemId),
                RetainerHint: RetainerHint(viewed.RetainerStock, item, retainers)))
            .ToArray();
        viewedRows = (queryItems, account, rows);
        return rows;
    }

    /// <summary>"Grungel" when one accessible retainer holds the item, "3 retainers" when several do.</summary>
    private static string RetainerHint(IReadOnlyDictionary<ulong, CachedRetainer> names, StockGroup item, IReadOnlySet<ulong> accessible)
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
            1 => names.GetValueOrDefault(held[0])?.RetainerName is { Length: > 0 } name ? name : $"Retainer {held[0]}",
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
                .ToDictionary(group => group.Key, group => group.Sum(rule => courier.Allocation(runtime, selectedPlan.Owner, rule).Moving));
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
                    accountMode ? string.Empty : RetainerHint(runtime.Retainers, item, accessible));
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

    private void DrawStockToolbar(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> sourceItems, string scopeKey, bool isEditing, AccountStockProjection? account = null)
    {
        var projection = runtime.Browser;
        DrawRailToggle();
        ImGui.SameLine();
        if (catalogMode)
        {
            catalogPicker.DrawSearch(Math.Max(100, ImGui.GetContentRegionAvail().X));
            return;
        }
        var context = queries.EnsureItemContext(sourceItems, viewedCharacter ?? projection.Owner, scopeKey, isEditing, runtime.PlanningRevision, account);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var trailingWidth = SearchReference.ButtonWidth + spacing + DalamudUiLayout.ButtonWidth("…") + spacing;
        var filterBeforeDraw = workbench.ItemFilterState.Expression;
        DalamudFilterAutocompleteRenderer.Draw(
            "RQStockWorkbench",
            SearchHint(runtime),
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

    // ---- Scope rail ----------------------------------------------------------------------------

    private const float RailWidth = 192f;

    /// <summary>"Wei Ning", or a neutral name while the logged-in character is not identified.</summary>
    private static string HereName(QuartermasterRuntimeSnapshot runtime) =>
        string.IsNullOrWhiteSpace(runtime.Owner.CharacterName) ? "This character" : runtime.Owner.CharacterName;

    /// <summary>A character's container as the rail names it: "Bags", "Saddlebag" or the retainer's name.</summary>
    private static string ContainerName(BrowserScope scope) => scope.Kind switch
    {
        BrowserScopeKind.Player => "Bags",
        BrowserScopeKind.Saddlebag => "Saddlebag",
        _ => scope.Label,
    };

    /// <summary>"All accessible", "Wei Ning", "Wei Ning · Saddlebag", "Dredgen Hope · Vesper" or the catalog.</summary>
    private string ActiveScopeLabel(QuartermasterRuntimeSnapshot runtime)
    {
        if (catalogMode)
            return RailCatalogLabel;
        if (viewedCharacter is { } viewed)
            return viewedScopeKey != BrowserScope.AllKey &&
                   ViewStock(runtime, viewed)?.Scopes.FirstOrDefault(scope => scope.Key == viewedScopeKey) is { } container
                ? $"{CourierSession.CharacterLabel(viewed)} · {ContainerName(container)}"
                : CourierSession.CharacterLabel(viewed);
        if (accountWide)
            return AccountScopeLabel;
        var selected = runtime.Browser.Scopes.FirstOrDefault(scope => scope.Key == workbench.ScopeKey);
        return selected is null || selected.Kind == BrowserScopeKind.All
            ? HereName(runtime)
            : $"{HereName(runtime)} · {ContainerName(selected)}";
    }

    private string? searchHintScope;
    private string searchHint = string.Empty;

    /// <summary>The search box names the active scope, so it stays obvious with the rail closed.</summary>
    private string SearchHint(QuartermasterRuntimeSnapshot runtime)
    {
        var scope = ActiveScopeLabel(runtime);
        if (!string.Equals(scope, searchHintScope, StringComparison.Ordinal))
        {
            searchHintScope = scope;
            searchHint = $"Search in {scope}…";
        }
        return searchHint;
    }

    private void ToggleRail() => railOpen = !railOpen;

    private void DrawRailToggle()
    {
        if (ImGuiComponents.IconButton("RQStockRailToggle", railOpen ? FontAwesomeIcon.ChevronLeft : FontAwesomeIcon.ChevronRight))
            ToggleRail();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(railOpen ? "Hide scopes" : "Show scopes");
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.rail.toggle",
            railOpen ? "Hide the stock scope rail" : "Show the stock scope rail",
            true,
            ToggleRail,
            railOpen ? "Open" : "Closed");
    }

    private bool RailExpanded(OwnerScope character, bool byDefault) =>
        railExpanded.TryGetValue(character.LocalContentId ?? 0, out var open) ? open : byDefault;

    private void ToggleRailExpanded(OwnerScope character, bool byDefault) =>
        railExpanded[character.LocalContentId ?? 0] = !RailExpanded(character, byDefault);

    /// <summary>
    /// The scope tree: the account view, the logged-in character and its containers, each other
    /// observed character and its containers, then the catalog. Clicking any node scopes to it.
    /// </summary>
    private void DrawScopeRail(QuartermasterRuntimeSnapshot runtime)
    {
        var account = courier.AccountStock(runtime);
        var counts = ResolveRailCounts(account);

        if (RailNode(AccountScopeLabel, counts.AccountText, IsAccountMode, null, out _))
            ViewAccount();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", account.Characters
                .Select(character => character.Matches(runtime.Owner) ? $"{HereName(runtime)} (logged in)"
                    : account.IsExternal(character) ? $"{character.CharacterName} (other account)"
                    : character.CharacterName)
                .Prepend("Every observed character:")));
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.scope.account",
            "Browse every observed character's stock",
            true,
            ViewAccount,
            IsAccountMode ? "Selected" : "Available");

        DrawHereNode(runtime, counts);

        // Account characters after the first are the other observed characters, by name, then
        // characters on other service accounts.
        var hasExternal = false;
        for (var index = 1; index < account.Characters.Count; index++)
        {
            var character = account.Characters[index];
            if (account.IsExternal(character))
            {
                hasExternal = true;
                continue;
            }
            if (courier.Stock(runtime, character) is not { } stock)
                continue;
            ImGui.PushID(index);
            try { DrawCharacterNode(runtime, character, stock, index, counts, null); }
            finally { ImGui.PopID(); }
        }

        if (hasExternal)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("Other accounts");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Characters on another service account, from that client's Quartermaster cache.\nItems there must be traded directly.");
            for (var index = 1; index < account.Characters.Count; index++)
            {
                var character = account.Characters[index];
                if (!account.IsExternal(character) || courier.ExternalStock(runtime, character) is not { } stock)
                    continue;
                ImGui.PushID(index);
                try { DrawCharacterNode(runtime, character, stock, index, counts, account.ExternalProfiles.GetValueOrDefault(character.LocalContentId!.Value)); }
                finally { ImGui.PopID(); }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (RailNode(RailCatalogLabel, counts.CatalogText(gameCatalog), catalogMode, null, out _))
            EnterCatalog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CatalogPicker.ScopeLabel);
        reviewRegistry.RegisterLastButton(
            "quartermaster.stock.scope.catalog",
            $"Browse {CatalogPicker.ScopeLabel.ToLowerInvariant()}",
            true,
            EnterCatalog,
            catalogMode ? "Selected" : "Available");
    }

    /// <summary>The logged-in character: open by default, with its bags, saddlebag and retainers in game order.</summary>
    private void DrawHereNode(QuartermasterRuntimeSnapshot runtime, RailCounts counts)
    {
        var owner = runtime.Owner;
        var inHere = !catalogMode && viewedCharacter is null && !accountWide;
        var open = RailExpanded(owner, true);
        ImGui.PushID("here");
        try
        {
            var allSelected = inHere && workbench.ScopeKey == BrowserScope.AllKey;
            if (RailNode(HereName(runtime), counts.Text(0, BrowserScope.AllKey), allSelected, open, out var toggled, QuartermasterPalette.Info))
                ViewHereScope(BrowserScope.AllKey);
            if (toggled)
                ToggleRailExpanded(owner, true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Logged in here");
            reviewRegistry.RegisterLastButton(
                "quartermaster.stock.scope.here.all",
                $"Browse all of {HereName(runtime)}'s stock",
                true,
                () => ViewHereScope(BrowserScope.AllKey),
                allSelected ? "Selected" : "Available");
            reviewRegistry.RegisterLastButton(
                "quartermaster.stock.rail.here.expand",
                $"{(open ? "Collapse" : "Expand")} {HereName(runtime)}'s containers",
                true,
                () => ToggleRailExpanded(runtimeSnapshots.Current.Owner, true),
                open ? "Expanded" : "Collapsed");
            if (!open)
                return;
            ImGui.Indent();
            foreach (var scope in runtime.Browser.Scopes)
            {
                if (scope.Kind == BrowserScopeKind.All)
                    continue;
                var key = scope.Key;
                var selected = inHere && key == workbench.ScopeKey;
                if (RailNode(ContainerName(scope), counts.Text(0, key), selected, null, out _,
                        space: courier.ContainerSpace(runtime, owner, scope, external: false)))
                    ViewHereScope(key);
                reviewRegistry.RegisterLastButton(
                    $"quartermaster.stock.scope.here.{key.Replace(':', '.')}",
                    $"Browse {HereName(runtime)} · {ContainerName(scope)}",
                    true,
                    () => ViewHereScope(key),
                    selected ? "Selected" : "Available");
            }
            ImGui.Unindent();
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>
    /// Another observed character: closed by default, with its bags and observed retainers. A
    /// character on another service account (<paramref name="externalProfile"/> set) is tinted, and
    /// also lists its saddlebag when its own client observed one.
    /// </summary>
    private void DrawCharacterNode(
        QuartermasterRuntimeSnapshot runtime,
        OwnerScope character,
        BrowserProjection stock,
        int index,
        RailCounts counts,
        string? externalProfile)
    {
        var external = externalProfile is not null;
        var viewing = !catalogMode && viewedCharacter?.Matches(character) == true;
        var open = RailExpanded(character, false);
        var control = $"quartermaster.stock.scope.character.{character.LocalContentId}";
        var allSelected = viewing && viewedScopeKey == BrowserScope.AllKey;
        if (RailNode(CourierSession.CharacterLabel(character), counts.Text(index, BrowserScope.AllKey), allSelected, open, out var toggled,
                external ? TradeTint : null))
            ViewCharacter(character);
        if (toggled)
            ToggleRailExpanded(character, false);
        if (external && ImGui.IsItemHovered())
            ImGui.SetTooltip($"{ExternalNodeTooltip}\nFrom {externalProfile}'s Quartermaster cache");
        reviewRegistry.RegisterLastButton(
            control,
            $"Browse {character.CharacterName}'s stock",
            true,
            () => ViewCharacter(character),
            allSelected ? "Selected" : "Available");
        reviewRegistry.RegisterLastButton(
            $"quartermaster.stock.rail.character.{character.LocalContentId}.expand",
            $"{(open ? "Collapse" : "Expand")} {character.CharacterName}'s containers",
            true,
            () => ToggleRailExpanded(character, false),
            open ? "Expanded" : "Collapsed");
        if (!open)
            return;
        ImGui.Indent();
        foreach (var scope in stock.Scopes)
        {
            // Another local character's saddlebag is never observed here; another account's client
            // observes its own, so it shows when that client saw any.
            var shown = scope.Kind is BrowserScopeKind.Player or BrowserScopeKind.Retainer ||
                        external && scope.Kind == BrowserScopeKind.Saddlebag && counts.Text(index, scope.Key) != "0";
            if (!shown)
                continue;
            var key = scope.Key;
            var selected = viewing && key == viewedScopeKey;
            if (RailNode(ContainerName(scope), counts.Text(index, key), selected, null, out _, external ? TradeTint : null,
                    courier.ContainerSpace(runtime, character, scope, external)))
                ViewCharacter(character, key);
            reviewRegistry.RegisterLastButton(
                $"{control}.{key.Replace(':', '.')}",
                $"Browse {character.CharacterName} · {ContainerName(scope)}",
                true,
                () => ViewCharacter(character, key),
                selected ? "Selected" : "Available");
        }
        ImGui.Unindent();
    }

    /// <summary>
    /// One rail row: a tree node (a leaf when <paramref name="open"/> is null) with its count of
    /// distinct items drawn right-aligned in muted text, and a container's fill bar left of it when
    /// its free slots are known. Neither is an item, so the node stays the last item for hover
    /// checks and review registration. Returns whether the label (not the chevron) was clicked.
    /// </summary>
    private static bool RailNode(
        string label,
        string? count,
        bool selected,
        bool? open,
        out bool toggled,
        Vector4? color = null,
        (int Capacity, int? Free)? space = null)
    {
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (selected)
            flags |= ImGuiTreeNodeFlags.Selected;
        if (open is { } expanded)
        {
            flags |= ImGuiTreeNodeFlags.OpenOnArrow;
            ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
        }
        else
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (color is { } text)
            ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.TreeNodeEx(label, flags);
        if (color is not null)
            ImGui.PopStyleColor();
        toggled = open is not null && ImGui.IsItemToggledOpen();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left) && !toggled;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var right = max.X - ImGui.GetStyle().FramePadding.X;
        if (!string.IsNullOrEmpty(count))
        {
            var size = ImGui.CalcTextSize(count);
            right -= size.X;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(right, min.Y + ((max.Y - min.Y - size.Y) / 2f)),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                count);
            right -= ImGui.GetStyle().ItemInnerSpacing.X;
        }
        if (space is ({ } capacity, { } free))
        {
            var bar = new Vector2(22f, 4f) * ImGuiHelpers.GlobalScale;
            FreeSlotsBar.Paint(new Vector2(right - bar.X, min.Y + ((max.Y - min.Y - bar.Y) / 2f)), bar, capacity, free);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{free:N0} free");
        }
        return clicked;
    }

    private RailCounts ResolveRailCounts(AccountStockProjection account)
    {
        if (railCounts is { } cached && ReferenceEquals(cached.Account, account))
            return cached;
        return railCounts = RailCounts.Build(account);
    }

    /// <summary>Distinct items per rail node, built once per account projection.</summary>
    private sealed class RailCounts
    {
        private readonly Dictionary<(int Character, string ScopeKey), string> text;
        private string? catalogText;

        private RailCounts(AccountStockProjection account, string accountText, Dictionary<(int Character, string ScopeKey), string> text)
        {
            Account = account;
            AccountText = accountText;
            this.text = text;
        }

        public AccountStockProjection Account { get; }
        public string AccountText { get; }

        /// <summary>A character's count (by its index in the account) for one of its scopes, or "0".</summary>
        public string Text(int character, string scopeKey) => text.GetValueOrDefault((character, scopeKey)) ?? "0";

        /// <summary>The catalog's size once it has loaded.</summary>
        public string? CatalogText(GameItemCatalog catalog) =>
            catalogText ??= catalog.TryGet()?.Items.Count.ToString("N0");

        public static RailCounts Build(AccountStockProjection account)
        {
            var counts = new Dictionary<(int Character, string ScopeKey), int>();
            foreach (var entry in account.ById.Values)
            {
                var previous = -1;
                // Locations are grouped by character, one per container.
                foreach (var location in entry.Locations)
                {
                    if (location.CharacterOrder != previous)
                    {
                        previous = location.CharacterOrder;
                        Increment(counts, (previous, BrowserScope.AllKey));
                    }
                    var scopeKey = location.Kind switch
                    {
                        BrowserScopeKind.Player => BrowserScope.PlayerKey,
                        BrowserScopeKind.Saddlebag => BrowserScope.SaddlebagKey,
                        _ => location.RetainerId is { } retainer ? BrowserScope.RetainerKey(retainer) : null,
                    };
                    if (scopeKey is not null)
                        Increment(counts, (location.CharacterOrder, scopeKey));
                }
            }
            return new RailCounts(account, account.Items.Count.ToString("N0"),
                counts.ToDictionary(entry => entry.Key, entry => entry.Value.ToString("N0")));

            static void Increment(Dictionary<(int Character, string ScopeKey), int> counts, (int Character, string ScopeKey) key) =>
                counts[key] = counts.GetValueOrDefault(key) + 1;
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
        // Another service account's stock cannot even be sent: it is traded in game.
        if (viewedCharacter is null)
            DrawOwnSelectionActions(runtime, hasSelection);
        if (viewedExternal)
            DrawExternalSendBlocked();
        else
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

    /// <summary>Send stays in its place, disabled, so the reason is one hover away.</summary>
    private void DrawExternalSendBlocked()
    {
        DalamudUiLayout.SameLineIfFits("Send to…");
        ImGui.BeginDisabled();
        ImGui.Button("Send to…");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Items on another service account must be traded directly in game.\nQuartermaster cannot send, stow or plan from there.");
        reviewRegistry.RegisterLastButton("quartermaster.stock.send", "Choose another character to send to", false,
            () => { }, "Other service account; trade in game");
    }

    private void DrawSendSelection(QuartermasterRuntimeSnapshot runtime, bool hasSelection)
    {
        // Sending to another character exists only once another character is known.
        if (!transferPlans.HasCourierDestinations(runtime, viewedCharacter))
            return;
        var send = hasSelection ? ResolveSendSelection(runtime) : (null, [], 0, 0);
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
    private (OwnerScope? Source, IReadOnlyList<StockGroup> Items, int Skipped, int External) ResolveSendSelection(QuartermasterRuntimeSnapshot runtime)
    {
        if (!IsAccountMode)
            return (viewedCharacter, ResolveSelection(runtime).Stock.Where(CanSend).ToArray(), 0, 0);
        var account = courier.AccountStock(runtime);
        var resolved = new List<(OwnerScope Source, uint ItemId)>();
        var unresolved = 0;
        var external = 0;
        foreach (var id in stockSelection.SelectedKeys)
        {
            var entry = account.ById.GetValueOrDefault(id);
            if (DefaultSendSource(entry) is { } source)
                resolved.Add((source, id));
            else if (entry is { OnlyExternal: true })
                external++;
            else if (entry is not null)
                unresolved++;
        }
        if (resolved.Count == 0)
            return (null, [], unresolved, external);
        var chosen = resolved.FirstOrDefault(entry => entry.Source.Matches(runtime.Owner)).Source
                     ?? (resolved.All(entry => entry.Source.Matches(resolved[0].Source)) ? resolved[0].Source : null);
        if (chosen is null)
            return (null, [], resolved.Count + unresolved, external);
        var items = resolved
            .Where(entry => entry.Source.Matches(chosen))
            .Select(entry => CharacterItem(runtime, chosen, entry.ItemId))
            .OfType<StockGroup>()
            .Where(CanSend)
            .ToList();
        items.Sort(CompareByName);
        return (chosen, items, resolved.Count + unresolved - items.Count, external);
    }

    private static string SendSelectionTooltip(QuartermasterRuntimeSnapshot runtime, (OwnerScope? Source, IReadOnlyList<StockGroup> Items, int Skipped, int External) send)
    {
        var external = send.External > 0
            ? $"\n{send.External:N0} only on another service account: trade those directly."
            : string.Empty;
        if (send.Source is not { } source)
            return (send.Skipped > 0
                ? "The selected items sit on several other characters. Expand a row to send from one location."
                : send.External > 0 ? "The selected items are only on another service account."
                : "Nothing selected that a character holds in bags or retainers.") + external;
        var from = source.Matches(runtime.Owner) ? HereName(runtime) : source.CharacterName;
        return (send.Skipped > 0
            ? $"Sends {send.Items.Count:N0} items from {from}.\n{send.Skipped:N0} others are on other characters: expand their rows to send from there."
            : $"Sends {send.Items.Count:N0} items from {from}.") + external;
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
        // Another character's scope resolves only against that character's stock: its chosen
        // container first, as the logged-in character's scopes do, then all of it.
        if (!catalogMode && viewedCharacter is { } viewed)
        {
            var stock = ViewStock(runtime, viewed);
            item = (stock is null ? null : ViewedItems(stock).ById.GetValueOrDefault(itemId) ?? stock.FindItem(itemId))!;
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
            if (viewedExternal)
                DrawExternalMenuItem();
            else
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
        // Another service account's units never send; say how they move instead.
        if (row.Account is { ExternalQuantity: > 0 })
            DrawExternalMenuItem();
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

    private static void DrawExternalMenuItem()
    {
        ImGui.BeginDisabled();
        ImGui.MenuItem(ExternalMenuLabel);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ExternalLocationTooltip);
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
            transferPlans.AddCourier(runtime, items, character, retainer);
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
