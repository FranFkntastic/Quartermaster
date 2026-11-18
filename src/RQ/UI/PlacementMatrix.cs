using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Domain;
using RQ.Inventory;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>One item row: its current destinations and the units each retainer holds today.</summary>
internal sealed record PlacementMatrixRow<TKey>(
    TKey Key,
    string Slug,
    string Label,
    string Quantity,
    StowageRoutingPolicy Routing,
    IReadOnlyDictionary<ulong, int> Holdings,
    string? Gathering = null);

/// <summary>An owner retainer column; FreeSlots is null while its inventory is unobserved.</summary>
internal sealed record PlacementMatrixColumn(ulong RetainerId, string Name, int? FreeSlots);

internal static class PlacementMatrixData
{
    /// <summary>
    /// Assigned owner retainers in game order, plus any retainer a row already names so no
    /// existing destination is hidden.
    /// </summary>
    public static IReadOnlyList<PlacementMatrixColumn> Columns(
        QuartermasterRuntimeSnapshot runtime,
        IEnumerable<StowageRoutingPolicy> routings)
    {
        var owned = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(runtime.Owner) && retainer.IsCurrentlyAssigned is not false)
            .OrderBy(retainer => retainer.DisplayOrder ?? int.MaxValue)
            .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToList();
        var columns = owned
            .Select(retainer => new PlacementMatrixColumn(
                retainer.RetainerId,
                retainer.RetainerName,
                StowageRouter.FreeInventorySlots(retainer)))
            .ToList();
        var shown = columns.Select(column => column.RetainerId).ToHashSet();
        foreach (var id in routings.SelectMany(routing => routing.PreferredRetainerIds).Distinct())
        {
            if (id == PlacementEdits.AnySpace || !shown.Add(id))
                continue;
            columns.Add(runtime.Retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(runtime.Owner)
                ? new(id, retainer.RetainerName, StowageRouter.FreeInventorySlots(retainer))
                : new(id, $"Retainer {id}", null));
        }
        return columns;
    }

    /// <summary>Units of the item each retainer holds, restricted to the row's quality.</summary>
    public static IReadOnlyDictionary<ulong, int> Holdings(
        QuartermasterRuntimeSnapshot runtime,
        uint itemId,
        ItemQualityPolicy quality)
    {
        var stock = runtime.Browser.FindItem(itemId);
        if (stock is null)
            return new Dictionary<ulong, int>();
        return stock.Stacks
            .Where(stack => stack.ScopeKind == BrowserScopeKind.Retainer && stack.RetainerId is not null)
            .Where(stack => quality switch
            {
                ItemQualityPolicy.NqOnly => stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ,
                ItemQualityPolicy.HqOnly => stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ,
                _ => true,
            })
            .GroupBy(stack => stack.RetainerId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));
    }
}

/// <summary>
/// Placement editor: rows are items, columns are owner retainers then Any space, a cell is the
/// retainer's rank in the item's destination order. Edits are handed to the owning surface,
/// which routes them through its own draft or mutation path.
/// </summary>
internal sealed class PlacementMatrix<TKey>
    where TKey : notnull
{
    private const float HeaderAngle = 48f * MathF.PI / 180f;
    private static readonly Vector4 InfoColor = new(.56f, .70f, .85f, 1f);
    private static readonly Vector4 AmberColor = new(1f, .7f, .3f, 1f);
    private static readonly Vector4 RankColor = new(.18f, .18f, .18f, 1f);
    private static readonly Vector4 FirstRankColor = new(.18f, .36f, .47f, 1f);
    private static readonly Vector4 HoldColor = new(.42f, .42f, .42f, 1f);
    private static readonly Vector4 SelectedRowColor = new(.18f, .23f, .27f, 1f);
    private static readonly Vector4 HoverCellColor = new(.14f, .19f, .23f, 1f);
    private static readonly Vector4 AnySeparatorColor = new(.23f, .23f, .23f, 1f);

    private readonly bool allowConsolidation;
    private readonly string surfaceId;
    private readonly string quantityHeader;
    private readonly AgentBridgeUiReviewRegistry registry;
    private readonly HashSet<TKey> selected = [];
    private TKey? anchor;
    private bool hasAnchor;
    private ulong target = PlacementEdits.AnySpace;
    private int selectionBarLines = 1;

    public PlacementMatrix(string surfaceId, string quantityHeader, AgentBridgeUiReviewRegistry registry, bool allowConsolidation = false)
    {
        this.allowConsolidation = allowConsolidation;
        this.surfaceId = surfaceId;
        this.quantityHeader = quantityHeader;
        this.registry = registry;
    }

    public int SelectedCount => selected.Count;

    public void Select(TKey key)
    {
        selected.Add(key);
        anchor = key;
        hasAnchor = true;
    }

    public void ClearSelection()
    {
        selected.Clear();
        hasAnchor = false;
    }

    /// <param name="allRows">Every row of the surface; selection-bar actions apply to selected rows even when filtered out.</param>
    /// <param name="visibleRows">Rows that pass the surface's filter, in display order.</param>
    /// <param name="apply">Applies changed destinations through the surface's edit path; returns false when refused.</param>
    public void Draw(
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        IReadOnlyList<PlacementMatrixRow<TKey>> visibleRows,
        IReadOnlyList<PlacementMatrixColumn> columns,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply)
    {
        // One row per key: a repeated key would register the same bridge control twice.
        allRows = allRows.DistinctBy(row => row.Key).ToArray();
        visibleRows = visibleRows.DistinctBy(row => row.Key).ToArray();
        var keys = allRows.Select(row => row.Key).ToHashSet();
        selected.RemoveWhere(key => !keys.Contains(key));
        if (columns.All(column => column.RetainerId != target))
            target = PlacementEdits.AnySpace;
        if (selected.Count > 0 && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
            !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape))
            ClearSelection();

        DrawSelectionBar(allRows, visibleRows, columns, apply);
        DrawTable(visibleRows, columns, apply);
    }

    // ---- selection bar -------------------------------------------------------------------

    private void DrawSelectionBar(
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        IReadOnlyList<PlacementMatrixRow<TKey>> visibleRows,
        IReadOnlyList<PlacementMatrixColumn> columns,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply)
    {
        var count = selected.Count;
        var hasSelection = count > 0;
        var background = hasSelection ? Scale(InfoColor, .16f, .96f) : Scale(InfoColor, .09f, .96f);
        var border = hasSelection ? Scale(InfoColor, .65f, 1f) : Scale(InfoColor, .38f, 1f);
        var barHeight = (ImGui.GetFrameHeightWithSpacing() * selectionBarLines) + 10f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 5f));
        if (ImGui.BeginChild($"{surfaceId}.selection-bar", new Vector2(0f, barHeight), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var top = ImGui.GetCursorPosY();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(
                hasSelection ? InfoColor : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
                hasSelection ? $"{count:N0} item{(count == 1 ? string.Empty : "s")} selected" : "No items selected");
            DrawSelectionActions(allRows, visibleRows, columns, apply, hasSelection);
            var lines = (int)MathF.Round((ImGui.GetCursorPosY() - top) / ImGui.GetFrameHeightWithSpacing());
            selectionBarLines = Math.Max(1, lines);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawSelectionActions(
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        IReadOnlyList<PlacementMatrixRow<TKey>> visibleRows,
        IReadOnlyList<PlacementMatrixColumn> columns,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply,
        bool hasSelection)
    {
        UiLayout.SameLineIfFits("Select visible");
        ImGui.BeginDisabled(visibleRows.Count == 0);
        if (ImGui.Button($"Select visible##{surfaceId}"))
            SelectVisible(visibleRows);
        ImGui.EndDisabled();
        RegisterAction("select-visible", "Select every visible placement row", visibleRows.Count > 0,
            $"visible={visibleRows.Count:N0}; selected={selected.Count:N0}", null, _ =>
            {
                SelectVisible(visibleRows);
                return AgentBridgeUiActionResult.Ok($"Selected {visibleRows.Count:N0} visible row(s).");
            }, mutating: false);

        DrawTargetCombo(columns);

        var targetName = TargetName(columns, target);
        Action("make-first", "Make first", primary: true, hasSelection, allRows, apply,
            $"Make {targetName} the first destination of the selected rows",
            routing => PlacementEdits.MakeFirst(routing, target));
        Action("add-fallback", "Add as fallback", primary: false, hasSelection, allRows, apply,
            $"Add {targetName} after the existing destinations of the selected rows",
            routing => PlacementEdits.AddFallback(routing, target));
        Action("remove", "Remove", primary: false, hasSelection, allRows, apply,
            $"Remove {targetName} from the selected rows",
            routing => PlacementEdits.Remove(routing, target));
        if (allowConsolidation)
        {
            var turnOff = allRows.Where(row => selected.Contains(row.Key))
                .All(row => row.Routing.Consolidate && PlacementEdits.Rank(row.Routing, target) == 1);
            Action("consolidate", "Consolidate", primary: false, hasSelection && target != PlacementEdits.AnySpace,
                allRows, apply, $"Toggle consolidation into {targetName}", routing =>
                {
                    var next = turnOff ? routing.Copy() : PlacementEdits.MakeFirst(routing, target);
                    next.Consolidate = !turnOff;
                    return next;
                });
        }
        ActionPerRow("where-it-lives", "Use current holders", hasSelection, allRows, apply,
            "Put the retainers holding each selected item first, most units first",
            row => PlacementEdits.WhereItLives(row.Routing, row.Holdings));
        Action("clear-destinations", "Clear destinations", primary: false, hasSelection, allRows, apply,
            "Clear every destination of the selected rows",
            _ => PlacementEdits.Clear());

        const string clearLabel = "Clear row selection";
        var clearWidth = ImGui.CalcTextSize(clearLabel).X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < clearWidth)
            ImGui.NewLine();
        else
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - clearWidth);
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button($"{clearLabel}##{surfaceId}"))
            ClearSelection();
        ImGui.EndDisabled();
        RegisterAction("clear-row-selection", "Clear placement row selection", hasSelection,
            $"selected={selected.Count:N0}", null, _ =>
            {
                ClearSelection();
                return AgentBridgeUiActionResult.Ok("Cleared placement row selection.");
            }, mutating: false);
    }

    private void Action(
        string id,
        string label,
        bool primary,
        bool hasSelection,
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply,
        string description,
        Func<StowageRoutingPolicy, StowageRoutingPolicy> edit) =>
        ActionPerRow(id, label, hasSelection, allRows, apply, description, row => edit(row.Routing), primary);

    private void ActionPerRow(
        string id,
        string label,
        bool hasSelection,
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply,
        string description,
        Func<PlacementMatrixRow<TKey>, StowageRoutingPolicy> edit,
        bool primary = false)
    {
        UiLayout.SameLineIfFits(label);
        ImGui.BeginDisabled(!hasSelection);
        if (primary)
            UiLayout.PushPrimary();
        var clicked = ImGui.Button($"{label}##{surfaceId}");
        if (primary)
            UiLayout.PopPrimary();
        ImGui.EndDisabled();
        if (clicked)
            ApplyToSelection(allRows, apply, edit);
        RegisterAction(id, description, hasSelection,
            $"selected={selected.Count:N0}; target={target}", null, _ =>
            {
                if (selected.Count == 0)
                    return AgentBridgeUiActionResult.Fail("No placement rows are selected.");
                var changed = ApplyToSelection(allRows, apply, edit);
                return changed < 0
                    ? AgentBridgeUiActionResult.Fail("The surface refused the placement change.")
                    : AgentBridgeUiActionResult.Ok($"{label}: changed {changed:N0} of {selected.Count:N0} selected row(s).");
            });
    }

    /// <summary>Returns the number of rows changed, or -1 when the surface refused the change.</summary>
    private int ApplyToSelection(
        IReadOnlyList<PlacementMatrixRow<TKey>> allRows,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply,
        Func<PlacementMatrixRow<TKey>, StowageRoutingPolicy> edit)
    {
        var changes = allRows
            .Where(row => selected.Contains(row.Key))
            .Select(row => (row.Key, Routing: edit(row)))
            .Where(change => !change.Routing.SameAs(allRows.First(row => Equals(row.Key, change.Key)).Routing))
            .ToArray();
        if (changes.Length == 0)
            return 0;
        return apply(changes) ? changes.Length : -1;
    }

    private void DrawTargetCombo(IReadOnlyList<PlacementMatrixColumn> columns)
    {
        UiLayout.SameLineIfFits(new string('x', 18));
        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo($"##{surfaceId}.target", TargetName(columns, target)))
        {
            foreach (var column in columns)
                if (ImGui.Selectable($"{column.Name}##target-{column.RetainerId}", column.RetainerId == target))
                    target = column.RetainerId;
            if (ImGui.Selectable($"{TransferPresentation.AnySpaceLabel}##target-any", target == PlacementEdits.AnySpace))
                target = PlacementEdits.AnySpace;
            ImGui.EndCombo();
        }

        var names = columns.Select(column => column.Name).Append(TransferPresentation.AnySpaceLabel).Distinct().ToArray();
        registry.Register(
            $"{surfaceId}.target",
            "Choose the destination the selection-bar actions target",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            TargetName(columns, target),
            new AgentBridgeActionArgumentSchema([new("target", AgentBridgeActionArgumentKind.Enum, AllowedValues: names)]),
            surfaceId,
            mutating: false,
            completionOperationKind: null,
            arguments =>
            {
                var requested = arguments!.Value.GetProperty("target").GetString()?.Trim() ?? string.Empty;
                if (string.Equals(requested, TransferPresentation.AnySpaceLabel, StringComparison.OrdinalIgnoreCase))
                {
                    target = PlacementEdits.AnySpace;
                    return AgentBridgeUiActionResult.Ok("Selection-bar actions now target Any space.");
                }
                var match = columns.FirstOrDefault(column => string.Equals(column.Name, requested, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return AgentBridgeUiActionResult.Fail($"{requested} is not a placement destination here.");
                target = match.RetainerId;
                return AgentBridgeUiActionResult.Ok($"Selection-bar actions now target {match.Name}.");
            });
    }

    private void SelectVisible(IReadOnlyList<PlacementMatrixRow<TKey>> visibleRows)
    {
        foreach (var row in visibleRows)
            selected.Add(row.Key);
    }

    // ---- table ---------------------------------------------------------------------------

    private unsafe void DrawTable(
        IReadOnlyList<PlacementMatrixRow<TKey>> rows,
        IReadOnlyList<PlacementMatrixColumn> columns,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var avail = ImGui.GetContentRegionAvail();
        var wide = avail.X >= 1300f * scale;
        var nameWidth = (wide ? 280f : 220f) * scale;
        var quantityWidth = Math.Max(48f * scale, ImGui.CalcTextSize(quantityHeader).X);
        var cellWidth = (wide ? 64f : 50f) * scale;
        var lineHeight = ImGui.GetTextLineHeight();
        var longest = columns.Select(column => ImGui.CalcTextSize(column.Name).X)
            .Append(ImGui.CalcTextSize(TransferPresentation.AnySpaceLabel).X)
            .Max();
        var headerHeight = Math.Clamp((longest * MathF.Sin(HeaderAngle)) + lineHeight + (8f * scale), 3f * lineHeight, 140f * scale);
        var spacerWidth = Math.Clamp(longest * MathF.Cos(HeaderAngle), 40f * scale, 140f * scale);
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), 23f * scale);

        var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter;
        var columnCount = 2 + columns.Count + 2;
        if (!ImGui.BeginTable($"##{surfaceId}.matrix", columnCount, flags, new Vector2(0f, Math.Max(120f * scale, avail.Y))))
            return;
        try
        {
            ImGui.TableSetupScrollFreeze(2, 2);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, nameWidth);
            ImGui.TableSetupColumn(quantityHeader, ImGuiTableColumnFlags.WidthFixed, quantityWidth);
            foreach (var column in columns)
                ImGui.TableSetupColumn($"##retainer-{column.RetainerId}", ImGuiTableColumnFlags.WidthFixed, cellWidth);
            ImGui.TableSetupColumn("##any", ImGuiTableColumnFlags.WidthFixed, cellWidth);
            ImGui.TableSetupColumn("##spacer", ImGuiTableColumnFlags.WidthFixed, spacerWidth);

            DrawNameHeader(columns, headerHeight, nameWidth, quantityWidth, lineHeight);
            DrawFreeSlotsRow(columns);

            if (rows.Count == 0)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled("No matching items.");
                return;
            }

            // Only rows in view are drawn and registered.
            DalamudVirtualizedRows.Draw(rows, (_, index) => DrawRow(rows, index, columns, cellWidth, rowHeight, apply));
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private unsafe void DrawNameHeader(
        IReadOnlyList<PlacementMatrixColumn> columns,
        float headerHeight,
        float nameWidth,
        float quantityWidth,
        float lineHeight)
    {
        var style = ImGui.GetStyle();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, headerHeight);
        ImGui.TableSetColumnIndex(0);
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var tableLeft = ImGui.GetCursorScreenPos().X - style.CellPadding.X;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + headerHeight - lineHeight - (style.CellPadding.Y * 2f));
        ImGui.TextUnformatted("Item");
        ImGui.TableSetColumnIndex(1);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + headerHeight - lineHeight - (style.CellPadding.Y * 2f));
        RightAligned(quantityHeader, null);

        var frozenRight = tableLeft + nameWidth + quantityWidth + (style.CellPadding.X * 4f);
        var clipMin = new Vector2(frozenRight, rowTop);
        var clipMax = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X, rowTop + headerHeight);
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        for (var index = 0; index <= columns.Count; index++)
        {
            ImGui.TableSetColumnIndex(2 + index);
            var name = index < columns.Count ? columns[index].Name : TransferPresentation.AnySpaceLabel;
            var cell = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var pivot = new Vector2(cell.X + (width / 2f) - (4f * ImGuiHelpers.GlobalScale), rowTop + headerHeight - (6f * ImGuiHelpers.GlobalScale));
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(clipMin, clipMax, false);
            AddRotatedText(drawList, pivot, -HeaderAngle, color, name, lineHeight);
            drawList.PopClipRect();
            ImGui.Dummy(new Vector2(width, headerHeight - (style.CellPadding.Y * 2f)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(name);
            if (index == columns.Count)
                DrawAnySeparator(rowTop, headerHeight);
        }
    }

    private void DrawFreeSlotsRow(IReadOnlyList<PlacementMatrixColumn> columns)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        var style = ImGui.GetStyle();
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var disabled = style.Colors[(int)ImGuiCol.TextDisabled];
        RightAligned("Free slots", disabled);
        for (var index = 0; index < columns.Count; index++)
        {
            ImGui.TableSetColumnIndex(2 + index);
            var free = columns[index].FreeSlots;
            RightAligned(free?.ToString("N0") ?? "—", free == 0 ? AmberColor : disabled);
        }
        ImGui.TableSetColumnIndex(2 + columns.Count);
        DrawAnySeparator(rowTop, ImGui.GetTextLineHeight() + (style.CellPadding.Y * 2f));
        RightAligned(columns.Sum(column => column.FreeSlots ?? 0).ToString("N0"), disabled);
    }

    private void DrawRow(
        IReadOnlyList<PlacementMatrixRow<TKey>> rows,
        int index,
        IReadOnlyList<PlacementMatrixColumn> columns,
        float cellWidth,
        float rowHeight,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply)
    {
        var row = rows[index];
        var isSelected = selected.Contains(row.Key);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        if (isSelected)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(SelectedRowColor));
        ImGui.PushID(row.Slug);

        ImGui.TableSetColumnIndex(0);
        var rowTop = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y;
        if (ImGui.Selectable($"{row.Label}##name", isSelected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight - (ImGui.GetStyle().CellPadding.Y * 2f))))
            ClickRow(rows, index);
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(row.Label).X > ImGui.GetItemRectSize().X)
            ImGui.SetTooltip(row.Label);
        registry.Register(
            $"{surfaceId}.row.{row.Slug}",
            isSelected ? $"Unselect {row.Label}" : $"Select {row.Label}",
            AgentBridgeUiControlKind.Select,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: isSelected,
            isSelected ? "selected" : "not selected",
            arguments: null,
            surfaceId,
            mutating: false,
            completionOperationKind: null,
            _ =>
            {
                var next = !selected.Contains(row.Key);
                if (next) selected.Add(row.Key); else selected.Remove(row.Key);
                anchor = row.Key;
                hasAnchor = true;
                return AgentBridgeUiActionResult.Ok(next ? $"Selected {row.Label}." : $"Unselected {row.Label}.");
            });

        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
        RightAligned(row.Quantity, null);

        for (var column = 0; column <= columns.Count; column++)
        {
            ImGui.TableSetColumnIndex(2 + column);
            var retainerId = column < columns.Count ? columns[column].RetainerId : PlacementEdits.AnySpace;
            if (column == columns.Count)
                DrawAnySeparator(rowTop, rowHeight);
            DrawCell(row, retainerId, column < columns.Count ? columns[column] : null, rowHeight, apply);
        }

        ImGui.PopID();
    }

    private void DrawCell(
        PlacementMatrixRow<TKey> row,
        ulong retainerId,
        PlacementMatrixColumn? column,
        float rowHeight,
        Func<IReadOnlyList<(TKey Key, StowageRoutingPolicy Routing)>, bool> apply)
    {
        var style = ImGui.GetStyle();
        var width = ImGui.GetContentRegionAvail().X;
        var height = rowHeight - (style.CellPadding.Y * 2f);
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##cell-{retainerId}", new Vector2(Math.Max(1f, width), Math.Max(1f, height)));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var rank = PlacementEdits.Rank(row.Routing, retainerId);
        var held = retainerId == PlacementEdits.AnySpace ? 0 : row.Holdings.GetValueOrDefault(retainerId);
        var name = column?.Name ?? TransferPresentation.AnySpaceLabel;
        var label = rank > 0 ? $"Remove {name} (#{rank}) from {row.Label}" : $"Add {name} to {row.Label}";
        registry.Register(
            $"{surfaceId}.cell.{row.Slug}.{(retainerId == PlacementEdits.AnySpace ? "any" : retainerId.ToString())}",
            label,
            AgentBridgeUiControlKind.Toggle,
            min,
            max,
            enabled: true,
            selected: rank > 0,
            rank > 0 ? $"rank={rank}; holds={held}" : $"not listed; holds={held}",
            arguments: null,
            surfaceId,
            mutating: true,
            completionOperationKind: null,
            _ => apply([(row.Key, PlacementEdits.Toggle(row.Routing, retainerId))])
                ? AgentBridgeUiActionResult.Ok(rank > 0 ? $"Removed {name} from {row.Label}." : $"Added {name} to {row.Label}.")
                : AgentBridgeUiActionResult.Fail("The surface refused the placement change."));

        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(new Vector2(min.X - style.CellPadding.X, min.Y - style.CellPadding.Y),
                new Vector2(max.X + style.CellPadding.X, max.Y + style.CellPadding.Y), ImGui.GetColorU32(HoverCellColor));

        var badgeText = rank > 0 ? rank.ToString() : string.Empty;
        var holdText = held > 0 ? held.ToString("N0") : string.Empty;
        var smallScale = .85f;
        var badgeDiameter = ImGui.GetTextLineHeight();
        var badgeWidth = rank > 0 ? Math.Max(badgeDiameter, (ImGui.CalcTextSize(badgeText).X * smallScale) + (6f * ImGuiHelpers.GlobalScale)) : 0f;
        var holdWidth = held > 0 ? ImGui.CalcTextSize(holdText).X * smallScale : 0f;
        var gap = rank > 0 && held > 0 ? 3f * ImGuiHelpers.GlobalScale : 0f;
        var total = badgeWidth + gap + holdWidth;
        var x = origin.X + Math.Max(0f, (width - total) / 2f);
        var centerY = origin.Y + (height / 2f);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * smallScale;
        if (rank > 0)
        {
            var badgeMin = new Vector2(x, centerY - (badgeDiameter / 2f));
            var badgeMax = new Vector2(x + badgeWidth, centerY + (badgeDiameter / 2f));
            drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(rank == 1 ? FirstRankColor : RankColor), badgeDiameter / 2f);
            if (allowConsolidation && row.Routing.Consolidate && rank == 1 && column is not null)
                drawList.AddCircle((badgeMin + badgeMax) / 2f, badgeDiameter / 2f,
                    ImGui.GetColorU32(new Vector4(.95f, .76f, .25f, 1f)), 0, 1.5f * ImGuiHelpers.GlobalScale);
            var textSize = ImGui.CalcTextSize(badgeText) * smallScale;
            drawList.AddText(font, fontSize,
                new Vector2(x + ((badgeWidth - textSize.X) / 2f), centerY - (textSize.Y / 2f)),
                ImGui.GetColorU32(rank == 1 ? new Vector4(1f, 1f, 1f, 1f) : InfoColor), badgeText);
            x += badgeWidth + gap;
        }
        if (held > 0)
        {
            var textHeight = ImGui.GetTextLineHeight() * smallScale;
            drawList.AddText(font, fontSize, new Vector2(x, centerY - (textHeight / 2f)), ImGui.GetColorU32(HoldColor), holdText);
        }

        if (hovered)
            ImGui.SetTooltip(Tooltip(row, retainerId, column, rank, held) +
                (allowConsolidation && rank == 1 && column is not null && row.Routing.Consolidate ? $" · {row.Gathering}" : ""));
        if (clicked)
            apply([(row.Key, PlacementEdits.Toggle(row.Routing, retainerId))]);
        else if (allowConsolidation && column is not null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            // Right-click marks this retainer as the row's consolidation target; again on the ringed chit clears it.
            var ringed = row.Routing.Consolidate && rank == 1;
            var next = ringed ? row.Routing.Copy() : PlacementEdits.MakeFirst(row.Routing, retainerId);
            next.Consolidate = !ringed;
            apply([(row.Key, next)]);
        }
    }

    private static string Tooltip(PlacementMatrixRow<TKey> row, ulong retainerId, PlacementMatrixColumn? column, int rank, int held)
    {
        if (column is null)
            return rank > 0
                ? $"{TransferPresentation.AnySpaceLabel} · #{rank} · remove"
                : $"{TransferPresentation.AnySpaceLabel} · add as last";
        var free = column.FreeSlots is { } slots ? $"{slots:N0} free slot{(slots == 1 ? string.Empty : "s")}" : "free slots unknown";
        var action = rank > 0 ? $"#{rank} · remove" : $"add as #{PlacementEdits.RankIfAdded(row.Routing, retainerId)}";
        return $"{column.Name} · holds {held:N0} · {free} · {action}";
    }

    private void ClickRow(IReadOnlyList<PlacementMatrixRow<TKey>> rows, int index)
    {
        var key = rows[index].Key;
        var io = ImGui.GetIO();
        if (io.KeyShift && hasAnchor)
        {
            var anchorIndex = -1;
            for (var candidate = 0; candidate < rows.Count; candidate++)
                if (Equals(rows[candidate].Key, anchor)) { anchorIndex = candidate; break; }
            if (anchorIndex >= 0)
            {
                if (!io.KeyCtrl)
                    selected.Clear();
                for (var candidate = Math.Min(anchorIndex, index); candidate <= Math.Max(anchorIndex, index); candidate++)
                    selected.Add(rows[candidate].Key);
                return;
            }
        }
        if (io.KeyCtrl)
        {
            if (!selected.Remove(key))
                selected.Add(key);
        }
        else
        {
            var only = selected.Count == 1 && selected.Contains(key);
            selected.Clear();
            if (!only)
                selected.Add(key);
        }
        anchor = key;
        hasAnchor = true;
    }

    // ---- drawing helpers -----------------------------------------------------------------

    private static void DrawAnySeparator(float top, float height)
    {
        var x = ImGui.GetCursorScreenPos().X - ImGui.GetStyle().CellPadding.X;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, top), new Vector2(x, top + height),
            ImGui.GetColorU32(AnySeparatorColor), 2f * ImGuiHelpers.GlobalScale);
    }

    private static void RightAligned(string text, Vector4? color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        if (color is { } value)
            ImGui.TextColored(value, text);
        else
            ImGui.TextUnformatted(text);
    }

    /// <summary>Draws text with its bottom-left corner at the pivot, rotated about the pivot.</summary>
    private static unsafe void AddRotatedText(ImDrawListPtr drawList, Vector2 pivot, float radians, uint color, string text, float lineHeight)
    {
        var start = drawList.VtxBuffer.Size;
        drawList.AddText(new Vector2(pivot.X, pivot.Y - lineHeight), color, text);
        var end = drawList.VtxBuffer.Size;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var vertices = drawList.VtxBuffer.Data;
        for (var index = start; index < end; index++)
        {
            var offset = vertices[index].Pos - pivot;
            vertices[index].Pos = pivot + new Vector2((offset.X * cos) - (offset.Y * sin), (offset.X * sin) + (offset.Y * cos));
        }
    }

    private void RegisterAction(
        string id,
        string label,
        bool enabled,
        string? value,
        AgentBridgeActionArgumentSchema? arguments,
        Func<System.Text.Json.JsonElement?, AgentBridgeUiActionResult> invoke,
        bool mutating = true) =>
        registry.Register(
            $"{surfaceId}.{id}",
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected: false,
            value,
            arguments,
            surfaceId,
            mutating,
            completionOperationKind: null,
            invoke);

    private static string TargetName(IReadOnlyList<PlacementMatrixColumn> columns, ulong retainerId) =>
        retainerId == PlacementEdits.AnySpace
            ? TransferPresentation.AnySpaceLabel
            : columns.FirstOrDefault(column => column.RetainerId == retainerId)?.Name ?? $"Retainer {retainerId}";

    private static Vector4 Scale(Vector4 color, float scale, float alpha) =>
        new(color.X * scale, color.Y * scale, color.Z * scale, alpha);
}
