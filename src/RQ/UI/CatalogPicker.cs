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
/// category, with a selection that survives filtering and one add button for the open
/// right-hand tab.
/// </summary>
internal sealed class CatalogPicker
{
    public const string ScopeLabel = "Every item in the game";

    private readonly record struct PickerRow(GameCatalogCategory Category, GameCatalogItem? Item, uint[] VisibleIds);

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
    private readonly HashSet<uint> selection = [];
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
    private bool allExpanded;
    private uint? rangeAnchor;

    public CatalogPicker(GameItemCatalog catalog, AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.catalog = catalog;
        this.reviewRegistry = reviewRegistry;
    }

    public int SelectedCount => selection.Count;

    public int VisibleCount { get; private set; }

    public void ClearSelection() => selection.Clear();

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

    /// <param name="addLabel">The primary button label, e.g. <c>Add 12 to plan</c>.</param>
    /// <param name="canAdd">Whether the destination can accept items right now.</param>
    /// <param name="add">Adds the selected catalog items to the open right-hand destination.</param>
    /// <param name="drawContextMenu">Draws a catalog row's context menu items, or returns false when it has none.</param>
    public void Draw(
        QuartermasterRuntimeSnapshot runtime,
        string addLabel,
        bool canAdd,
        Action<IReadOnlyList<GameCatalogItem>> add,
        Func<GameCatalogItem, bool> hasContextMenu,
        Action<GameCatalogItem> drawContextMenu)
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

        var style = ImGui.GetStyle();
        var footerHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y + style.ItemSpacing.Y + 1f;
        var tableHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - footerHeight);
        var visible = snapshot is null ? [] : ResolveRows(snapshot);
        DrawTable(visible, tableHeight, hasContextMenu, drawContextMenu);
        DrawFooter(snapshot, addLabel, canAdd, add);
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

    private IReadOnlyList<PickerRow> ResolveRows(GameCatalogSnapshot snapshot)
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
        var key = new RowsKey(snapshot, needle, owned, min, max, owned ? haveRevision : -1, expansionVersion);
        if (rowsKey == key)
            return rows;

        var built = new List<PickerRow>();
        var visibleCount = 0;
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
            visibleCount += visible.Length;
            built.Add(new PickerRow(category, null, visible.Select(item => item.ItemId).ToArray()));
            if (!IsOpen(category.Id))
            {
                everyCategoryOpen = false;
                continue;
            }
            foreach (var item in visible)
                built.Add(new PickerRow(category, item, []));
        }
        rows = built;
        rowsKey = key;
        // Cached with the rows: scanning ~50k expanded rows every frame for this label was the
        // picker's main steady per-frame cost.
        allExpanded = built.Count > 0 && everyCategoryOpen;
        VisibleCount = visibleCount;
        return rows;
    }

    private void DrawTable(
        IReadOnlyList<PickerRow> visible,
        float height,
        Func<GameCatalogItem, bool> hasContextMenu,
        Action<GameCatalogItem> drawContextMenu)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
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
            DalamudVirtualizedRows.Draw(visible, (row, _) =>
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                if (row.Item is { } item)
                    DrawItemRow(visible, item, hasContextMenu, drawContextMenu);
                else
                    DrawCategoryRow(row);
            });
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void DrawCategoryRow(PickerRow row)
    {
        var category = row.Category;
        var ids = row.VisibleIds;
        var selectedCount = ids.Count(selection.Contains);
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
            () => ToggleCategory(category.Id, ids, ids.All(selection.Contains)),
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

    private void ToggleCategory(uint categoryId, uint[] ids, bool allSelected)
    {
        if (allSelected)
            selection.ExceptWith(ids);
        else
            selection.UnionWith(ids);
        Expand(categoryId);
    }

    private void DrawItemRow(
        IReadOnlyList<PickerRow> visible,
        GameCatalogItem item,
        Func<GameCatalogItem, bool> hasContextMenu,
        Action<GameCatalogItem> drawContextMenu)
    {
        ImGui.TableNextColumn();
        ImGui.PushID($"catalog-item:{item.ItemId}");
        var selected = selection.Contains(item.ItemId);
        var cursor = ImGui.GetCursorPos();
        if (ImGui.Selectable(
                "##row",
                selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, ImGui.GetFrameHeight())))
        {
            if (ImGui.GetIO().KeyShift && rangeAnchor is { } anchor && anchor != item.ItemId)
                SelectRange(visible, anchor, item.ItemId);
            else
                Toggle(item.ItemId);
            rangeAnchor = item.ItemId;
        }
        reviewRegistry.RegisterLastSelectable(
            $"quartermaster.catalog.select.{item.ItemId}",
            $"Select {item.Name}",
            true,
            selected,
            () => Toggle(item.ItemId),
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
        RightAlignedDisabled(item.ItemLevel.ToString("N0"));
        ImGui.TableNextColumn();
        if (have.TryGetValue(item.ItemId, out var quantity))
            RightAligned(quantity.ToString("N0"));
        else
            RightAlignedDisabled("—");
    }

    private void Toggle(uint itemId)
    {
        if (!selection.Remove(itemId))
            selection.Add(itemId);
    }

    /// <summary>Shift-click: selects every visible item between the last clicked row and this one.</summary>
    private void SelectRange(IReadOnlyList<PickerRow> visible, uint fromId, uint toId)
    {
        int from = -1, to = -1;
        for (var index = 0; index < visible.Count && (from < 0 || to < 0); index++)
        {
            if (visible[index].Item is not { } candidate)
                continue;
            if (candidate.ItemId == fromId)
                from = index;
            if (candidate.ItemId == toId)
                to = index;
        }
        if (from < 0 || to < 0)
        {
            Toggle(toId);
            return;
        }
        for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++)
            if (visible[index].Item is { } item)
                selection.Add(item.ItemId);
    }

    private void DrawFooter(
        GameCatalogSnapshot? snapshot,
        string addLabel,
        bool canAdd,
        Action<IReadOnlyList<GameCatalogItem>> add)
    {
        ImGui.Separator();
        var count = selection.Count;
        if (count > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Info), $"{count:N0} selected");
            ImGui.SameLine();
        }
        var style = ImGui.GetStyle();
        var buttonsWidth = ImGui.CalcTextSize("Clear").X + ImGui.CalcTextSize(addLabel).X +
                           (style.FramePadding.X * 4f) + style.ItemSpacing.X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonsWidth));

        ImGui.BeginDisabled(count == 0);
        if (ImGui.Button("Clear##RQCatalogClear"))
            selection.Clear();
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.catalog.clear",
            "Clear the catalog selection",
            count > 0,
            selection.Clear,
            $"{count:N0} selected");

        ImGui.SameLine();
        var enabled = count > 0 && canAdd && snapshot is not null;
        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button($"{addLabel}###RQCatalogAdd") && snapshot is not null)
            AddSelected(snapshot, add);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.catalog.add",
            "Add the selected catalog items to the open Transfer Plan or Item Group",
            enabled,
            () =>
            {
                if (catalog.TryGet() is { } current)
                    AddSelected(current, add);
            },
            addLabel);
    }

    private void AddSelected(GameCatalogSnapshot snapshot, Action<IReadOnlyList<GameCatalogItem>> add)
    {
        var items = selection
            .Select(id => snapshot.Items.GetValueOrDefault(id))
            .OfType<GameCatalogItem>()
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0)
            return;
        add(items);
        selection.Clear();
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
