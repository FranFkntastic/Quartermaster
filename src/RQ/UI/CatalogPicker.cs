using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Domain;
using RQ.Inventory;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The stock browser's "Every item in the game" scope: the game catalog grouped by item
/// category. Selection is the stock browser's shared item selection.
/// </summary>
internal sealed class CatalogPicker
{
    public const string ScopeLabel = "Every item in the game";

    /// <param name="KeyIndex">The item row's index in the displayed item keys; -1 for category rows.</param>
    private readonly record struct PickerRow(GameCatalogCategory Category, GameCatalogItem? Item, uint[] VisibleIds, int KeyIndex);

    private readonly record struct RowsKey(
        GameCatalogSnapshot Catalog,
        string Search,
        bool Owned,
        int LevelMin,
        int LevelMax,
        long HaveRevision,
        int ExpansionVersion);

    private readonly GameItemCatalog catalog;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly TableSelectionModel<uint> selection;
    private readonly HashSet<uint> expanded = [];
    private readonly HashSet<uint> searchCollapsed = [];
    private readonly Dictionary<uint, int> have = [];
    private string search = string.Empty;
    private string lastSearch = string.Empty;
    private bool owned;
    private int levelMin = 1;
    private int levelMax = 999;
    private bool levelsInitialized;
    private long haveRevision = -1;
    private int expansionVersion;
    private RowsKey? rowsKey;
    private IReadOnlyList<PickerRow> rows = [];
    private uint[] displayedIds = [];
    private bool allExpanded;

    public CatalogPicker(GameItemCatalog catalog, AgentBridgeUiReviewRegistry reviewRegistry, TableSelectionModel<uint> selection)
    {
        this.catalog = catalog;
        this.reviewRegistry = reviewRegistry;
        this.selection = selection;
    }

    public int VisibleCount { get; private set; }

    /// <summary>Every item matching the search, Owned, and level filters, regardless of category collapse.</summary>
    public VisibleItemIds Visible { get; } = new();

    /// <summary>Whether search, Owned, or the level range currently narrows the catalog.</summary>
    public bool NarrowingActive { get; private set; }

    public void DrawSearch(float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##RQCatalogSearch", "Search items", ref search, 96);
        reviewRegistry.RegisterLastAction(
            "quartermaster.catalog.search",
            "Search the game item catalog",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            search,
            new AgentBridgeActionArgumentSchema(
                [new("query", AgentBridgeActionArgumentKind.String, Required: false)]),
            arguments =>
            {
                search = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
                         value.TryGetProperty("query", out var queryValue)
                    ? queryValue.GetString() ?? string.Empty
                    : string.Empty;
                return AgentBridgeUiActionResult.Ok("Catalog search updated.");
            });
    }

    /// <summary>Draws the Owned and level narrowing row and resolves the rows the table will show.</summary>
    public void DrawFilters(QuartermasterRuntimeSnapshot runtime)
    {
        var snapshot = catalog.TryGet();
        if (snapshot is not null && !levelsInitialized)
        {
            levelMin = snapshot.MinItemLevel;
            levelMax = snapshot.MaxItemLevel;
            levelsInitialized = true;
        }
        RefreshHave(runtime);
        DrawNarrowing(snapshot);
        if (snapshot is not null)
            ResolveRows(snapshot);
        else
        {
            rows = [];
            displayedIds = [];
            rowsKey = null;
            VisibleCount = 0;
            NarrowingActive = false;
            Visible.Set(rows, []);
        }
    }

    /// <param name="drawContextMenu">Draws a catalog row's context menu items, or returns false when it has none.</param>
    public void DrawTable(Func<GameCatalogItem, bool> hasContextMenu, Action<GameCatalogItem> drawContextMenu)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        var height = Math.Max(120f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RQCatalogPicker", 3, flags, new Vector2(0, height)))
            return;
        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableHeadersRow();
            var rowHeight = ImGui.GetFrameHeight();
            var visible = rows;
            DalamudVirtualizedRows.Draw(visible, (row, _) =>
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                if (row.Item is { } item)
                    DrawItemRow(item, row.KeyIndex, hasContextMenu, drawContextMenu);
                else
                    DrawCategoryRow(row);
            });
            DalamudTableSelectionRenderer.EndRows(selection);
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void RefreshHave(QuartermasterRuntimeSnapshot runtime)
    {
        if (haveRevision == runtime.Revision)
            return;
        haveRevision = runtime.Revision;
        have.Clear();
        var accessibleRetainers = TransferWorkbenchPresentation.AccessibleRetainerIds(runtime.Retainers, runtime.Owner);
        foreach (var item in runtime.Browser.GetItems(BrowserScope.AllKey))
        {
            var quantity = item.PlayerQuantity + item.SaddlebagQuantity +
                           TransferWorkbenchPresentation.AccessibleStorageQuantity(
                               item,
                               ItemQualityPolicy.Any,
                               accessibleRetainers);
            if (quantity > 0)
                have[item.ItemId] = quantity;
        }
    }

    private void DrawNarrowing(GameCatalogSnapshot? snapshot)
    {
        ImGui.Checkbox("Owned##RQCatalogOwned", ref owned);
        reviewRegistry.RegisterLastButton(
            "quartermaster.catalog.owned",
            "Show only owned catalog items",
            true,
            () => owned = !owned,
            owned ? "On" : "Off");

        var floor = snapshot?.MinItemLevel ?? 0;
        var ceiling = snapshot?.MaxItemLevel ?? 999;
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Lv");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(44);
        ImGui.InputInt("##RQCatalogLevelMin", ref levelMin, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
            levelMin = Math.Clamp(levelMin, floor, ceiling);
        ImGui.SameLine();
        ImGui.TextDisabled("–");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(44);
        ImGui.InputInt("##RQCatalogLevelMax", ref levelMax, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
            levelMax = Math.Clamp(levelMax, Math.Clamp(levelMin, floor, ceiling), ceiling);

        var label = allExpanded ? "Collapse all###RQCatalogExpand" : "Expand all###RQCatalogExpand";
        var width = ImGui.CalcTextSize(allExpanded ? "Collapse all" : "Expand all").X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width));
        if (ImGui.Button(label))
            SetAllExpanded(!allExpanded);
        reviewRegistry.RegisterLastButton(
            "quartermaster.catalog.expand-all",
            allExpanded ? "Collapse every catalog category" : "Expand every catalog category",
            true,
            () => SetAllExpanded(!allExpanded),
            allExpanded ? "Expanded" : "Collapsed");
    }

    private void SetAllExpanded(bool open)
    {
        var categoryIds = rows.Where(row => row.Item is null).Select(row => row.Category.Id).ToArray();
        if (SearchActive)
        {
            if (open)
                searchCollapsed.Clear();
            else
                searchCollapsed.UnionWith(categoryIds);
        }
        else if (open)
        {
            expanded.UnionWith(categoryIds);
        }
        else
        {
            expanded.Clear();
        }
        expansionVersion++;
    }

    private bool SearchActive => search.Trim().Length > 0;

    private bool IsOpen(uint categoryId) =>
        SearchActive ? !searchCollapsed.Contains(categoryId) : expanded.Contains(categoryId);

    private void ToggleOpen(uint categoryId)
    {
        if (SearchActive)
        {
            if (!searchCollapsed.Remove(categoryId))
                searchCollapsed.Add(categoryId);
        }
        else if (!expanded.Remove(categoryId))
        {
            expanded.Add(categoryId);
        }
        expansionVersion++;
    }

    private void Expand(uint categoryId)
    {
        if (SearchActive ? searchCollapsed.Remove(categoryId) : expanded.Add(categoryId))
            expansionVersion++;
    }

    private void ResolveRows(GameCatalogSnapshot snapshot)
    {
        var needle = search.Trim();
        if (!string.Equals(needle, lastSearch, StringComparison.Ordinal))
        {
            // A new search starts with every matching category expanded.
            lastSearch = needle;
            searchCollapsed.Clear();
            expansionVersion++;
        }
        var min = Math.Clamp(levelMin, snapshot.MinItemLevel, snapshot.MaxItemLevel);
        var max = Math.Clamp(levelMax, min, snapshot.MaxItemLevel);
        NarrowingActive = needle.Length > 0 || owned || min > snapshot.MinItemLevel || max < snapshot.MaxItemLevel;
        var key = new RowsKey(snapshot, needle, owned, min, max, owned ? haveRevision : -1, expansionVersion);
        if (rowsKey == key)
            return;

        var built = new List<PickerRow>();
        var displayed = new List<uint>();
        var matching = new List<uint>();
        var everyCategoryOpen = true;
        foreach (var category in snapshot.Categories)
        {
            var categoryMatches = needle.Length == 0 || category.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var visible = category.Items
                .Where(item =>
                    item.ItemLevel >= min && item.ItemLevel <= max &&
                    (!owned || have.ContainsKey(item.ItemId)) &&
                    (categoryMatches || item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (visible.Length == 0)
                continue;
            var ids = visible.Select(item => item.ItemId).ToArray();
            matching.AddRange(ids);
            built.Add(new PickerRow(category, null, ids, -1));
            if (!IsOpen(category.Id))
            {
                everyCategoryOpen = false;
                continue;
            }
            foreach (var item in visible)
            {
                built.Add(new PickerRow(category, item, [], displayed.Count));
                displayed.Add(item.ItemId);
            }
        }
        rows = built;
        rowsKey = key;
        displayedIds = displayed.ToArray();
        // Cached with the rows: scanning ~50k expanded rows every frame for this label was the
        // picker's main steady per-frame cost.
        allExpanded = built.Count > 0 && everyCategoryOpen;
        VisibleCount = matching.Count;
        Visible.Set(built, matching.ToArray());
    }

    private void DrawCategoryRow(PickerRow row)
    {
        var category = row.Category;
        var ids = row.VisibleIds;
        var selectedCount = CountSelected(ids);
        var all = selectedCount == ids.Length;
        var mixed = selectedCount > 0 && !all;

        ImGui.TableNextColumn();
        ImGui.PushID($"catalog-category:{category.Id}");
        var check = all;
        if (mixed)
            ImGuiP.PushItemFlag(ImGuiItemFlags.MixedValue, true);
        var toggled = ImGui.Checkbox("##category", ref check);
        if (mixed)
            ImGuiP.PopItemFlag();
        if (toggled)
            ToggleCategory(category.Id, ids, all);
        reviewRegistry.RegisterLastButton(
            $"quartermaster.catalog.category.{category.Id}",
            $"Select every visible {category.Name} item",
            true,
            () => ToggleCategory(category.Id, ids, CountSelected(ids) == ids.Length),
            all ? "Selected" : mixed ? $"{selectedCount:N0} of {ids.Length:N0} selected" : "Available");

        ImGui.SameLine();
        var open = IsOpen(category.Id);
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        ImGui.TreeNodeEx(
            $"{category.Name}##node",
            ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (ImGui.IsItemToggledOpen())
            ToggleOpen(category.Id);
        reviewRegistry.RegisterLastButton(
            $"quartermaster.catalog.expand.{category.Id}",
            $"{(open ? "Collapse" : "Expand")} {category.Name}",
            true,
            () => ToggleOpen(category.Id),
            open ? "Expanded" : "Collapsed");
        ImGui.PopID();

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        RightAlignedDisabled(selectedCount > 0 ? $"{selectedCount:N0} / {ids.Length:N0}" : $"{ids.Length:N0}");
    }

    private int CountSelected(uint[] ids)
    {
        if (selection.Count == 0)
            return 0;
        var count = 0;
        foreach (var id in ids)
            if (selection.IsSelected(id))
                count++;
        return count;
    }

    private void ToggleCategory(uint categoryId, uint[] ids, bool allSelected)
    {
        foreach (var id in ids)
            selection.SetSelected(id, !allSelected);
        Expand(categoryId);
    }

    private void DrawItemRow(
        GameCatalogItem item,
        int keyIndex,
        Func<GameCatalogItem, bool> hasContextMenu,
        Action<GameCatalogItem> drawContextMenu)
    {
        ImGui.TableNextColumn();
        ImGui.PushID($"catalog-item:{item.ItemId}");
        var cursor = ImGui.GetCursorPos();
        DalamudTableSelectionRenderer.DrawRow(
            selection,
            displayedIds,
            keyIndex,
            "##row",
            new Vector2(0, ImGui.GetFrameHeight()));
        var selected = selection.IsSelected(item.ItemId);
        reviewRegistry.RegisterLastSelectable(
            $"quartermaster.stock.select.{item.ItemId}",
            $"Select {item.Name} for stock actions",
            true,
            selected,
            () => selection.SetSelected(item.ItemId, !selection.IsSelected(item.ItemId)),
            selected ? "Selected" : "Available");
        if (hasContextMenu(item) && ImGui.BeginPopupContextItem("##context"))
        {
            drawContextMenu(item);
            ImGui.EndPopup();
        }
        // Line the name up under its category's label (past the category checkbox and arrow).
        ImGui.SetCursorPos(cursor + new Vector2(
            ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X + ImGui.GetTreeNodeToLabelSpacing(),
            0));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(item.Name);
        ImGui.PopID();

        ImGui.TableNextColumn();
        DrawCellHitTarget(item.ItemId, keyIndex, "lv");
        RightAlignedDisabled(item.ItemLevel.ToString("N0"));
        ImGui.TableNextColumn();
        DrawCellHitTarget(item.ItemId, keyIndex, "have");
        if (have.TryGetValue(item.ItemId, out var quantity))
            RightAligned(quantity.ToString("N0"));
        else
            RightAlignedDisabled("—");
    }

    /// <summary>Makes a non-name cell select the row too, matching stock rows' whole-row target.</summary>
    private void DrawCellHitTarget(uint itemId, int keyIndex, string column)
    {
        var cursor = ImGui.GetCursorPos();
        DalamudTableSelectionRenderer.DrawRow(
            selection,
            displayedIds,
            keyIndex,
            $"##row-{column}:{itemId}",
            new Vector2(0, ImGui.GetFrameHeight()));
        ImGui.SetCursorPos(cursor);
    }

    private static void RightAligned(string text)
    {
        ImGui.AlignTextToFramePadding();
        var offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextUnformatted(text);
    }

    private static void RightAlignedDisabled(string text)
    {
        ImGui.AlignTextToFramePadding();
        var offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextDisabled(text);
    }
}
