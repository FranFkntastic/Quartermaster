using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;

namespace RQ.UI;

/// <summary>
/// One courier line in a destination's matrix. When Targeted, Assigned is the container the line
/// lands in at this destination (null is bags); an untargeted line goes elsewhere today (Elsewhere).
/// </summary>
internal sealed record CourierPlacementRow(
    Guid Key,
    string Label,
    int Send,
    string From,
    bool Targeted,
    ulong? Assigned,
    int BagsHeld,
    IReadOnlyDictionary<ulong, int> Holdings,
    string? Elsewhere = null);

/// <summary>
/// Courier Placement for one destination character: rows are courier lines, columns are that
/// character's bags then retainers in game order, and a cell shows the stack already there. A line
/// lands in exactly one container, so a click moves its single highlight; the owning surface persists it.
/// </summary>
internal sealed class CourierPlacementMatrix
{
    private static readonly Vector4 AssignedColor = new(.18f, .36f, .47f, 1f);
    private static readonly Vector4 HoverCellColor = new(.14f, .19f, .23f, 1f);
    private static readonly Vector4 HoldColor = new(.42f, .42f, .42f, 1f);
    private static readonly Vector4 AmberColor = new(1f, .7f, .3f, 1f);
    private const string BagsLabel = "Bags";

    private readonly string surfaceId;
    private readonly AgentBridgeUiReviewRegistry registry;

    public CourierPlacementMatrix(string surfaceId, AgentBridgeUiReviewRegistry registry)
    {
        this.surfaceId = surfaceId;
        this.registry = registry;
    }

    /// <param name="assign">Lands a line at this destination in a container (null is bags); false when the plan refused it.</param>
    public void Draw(
        IReadOnlyList<CourierPlacementRow> rows,
        IReadOnlyList<PlacementMatrixColumn> retainers,
        int? bagFreeSlots,
        string destination,
        Func<Guid, ulong?, bool> assign)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail();
        var wide = avail.X >= 1300f * scale;
        var nameWidth = (wide ? 260f : 200f) * scale;
        var sendWidth = Math.Max(48f * scale, ImGui.CalcTextSize("Send").X);
        var fromWidth = (wide ? 200f : 150f) * scale;
        var cellWidth = (wide ? 64f : 50f) * scale;
        var lineHeight = ImGui.GetTextLineHeight();
        var containers = retainers
            .Select(column => (Id: (ulong?)column.RetainerId, column.Name, column.FreeSlots))
            .Prepend((Id: null, Name: BagsLabel, FreeSlots: bagFreeSlots))
            .ToArray();
        var longest = containers.Max(container => ImGui.CalcTextSize(container.Name).X);
        var headerHeight = Math.Clamp((longest * MathF.Sin(PlacementMatrixDrawing.HeaderAngle)) + lineHeight + (8f * scale), 3f * lineHeight, 140f * scale);
        var spacerWidth = Math.Clamp(longest * MathF.Cos(PlacementMatrixDrawing.HeaderAngle), 40f * scale, 140f * scale);
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), 23f * scale);

        var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter;
        if (!ImGui.BeginTable($"##{surfaceId}.matrix", 3 + containers.Length + 1, flags, new Vector2(0f, Math.Max(120f * scale, avail.Y))))
            return;
        try
        {
            ImGui.TableSetupScrollFreeze(3, 2);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, nameWidth);
            ImGui.TableSetupColumn("Send", ImGuiTableColumnFlags.WidthFixed, sendWidth);
            ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, fromWidth);
            foreach (var container in containers)
                ImGui.TableSetupColumn($"##container-{container.Id?.ToString() ?? "bags"}", ImGuiTableColumnFlags.WidthFixed, cellWidth);
            ImGui.TableSetupColumn("##spacer", ImGuiTableColumnFlags.WidthFixed, spacerWidth);

            DrawHeader(containers.Select(container => container.Name).ToArray(), headerHeight, nameWidth + sendWidth + fromWidth, lineHeight);
            DrawFreeSlotsRow(containers.Select(container => container.FreeSlots).ToArray());

            if (rows.Count == 0)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled("No lines.");
                return;
            }
            DalamudVirtualizedRows.Draw(rows, (_, index) => DrawRow(rows[index], containers, rowHeight, destination, assign));
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void DrawHeader(IReadOnlyList<string> names, float headerHeight, float frozenWidth, float lineHeight)
    {
        var style = ImGui.GetStyle();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, headerHeight);
        ImGui.TableSetColumnIndex(0);
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var tableLeft = ImGui.GetCursorScreenPos().X - style.CellPadding.X;
        var labelOffset = headerHeight - lineHeight - (style.CellPadding.Y * 2f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + labelOffset);
        ImGui.TextUnformatted("Item");
        ImGui.TableSetColumnIndex(1);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + labelOffset);
        PlacementMatrixDrawing.RightAligned("Send", null);
        ImGui.TableSetColumnIndex(2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + labelOffset);
        ImGui.TextUnformatted("From");

        var frozenRight = tableLeft + frozenWidth + (style.CellPadding.X * 6f);
        var clipMin = new Vector2(frozenRight, rowTop);
        var clipMax = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X, rowTop + headerHeight);
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        for (var index = 0; index < names.Count; index++)
        {
            ImGui.TableSetColumnIndex(3 + index);
            var cell = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var pivot = new Vector2(cell.X + (width / 2f) - (4f * ImGuiHelpers.GlobalScale), rowTop + headerHeight - (6f * ImGuiHelpers.GlobalScale));
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(clipMin, clipMax, false);
            PlacementMatrixDrawing.AddRotatedText(drawList, pivot, -PlacementMatrixDrawing.HeaderAngle, color, names[index], lineHeight);
            drawList.PopClipRect();
            ImGui.Dummy(new Vector2(width, headerHeight - (style.CellPadding.Y * 2f)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(names[index]);
        }
    }

    private static void DrawFreeSlotsRow(IReadOnlyList<int?> freeSlots)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(2);
        var disabled = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        PlacementMatrixDrawing.RightAligned("Free slots", disabled);
        for (var index = 0; index < freeSlots.Count; index++)
        {
            ImGui.TableSetColumnIndex(3 + index);
            var free = freeSlots[index];
            PlacementMatrixDrawing.RightAligned(free?.ToString("N0") ?? "—", free == 0 ? AmberColor : disabled);
        }
    }

    private void DrawRow(
        CourierPlacementRow row,
        IReadOnlyList<(ulong? Id, string Name, int? FreeSlots)> containers,
        float rowHeight,
        string destination,
        Func<Guid, ulong?, bool> assign)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.PushID(row.Key.ToString("N"));

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        if (row.Targeted)
            ImGui.TextUnformatted(row.Label);
        else
            ImGui.TextDisabled(row.Label);
        if (ImGui.IsItemHovered() && row.Elsewhere is { } elsewhere)
            ImGui.SetTooltip($"→ {elsewhere}");

        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
        PlacementMatrixDrawing.RightAligned(row.Send.ToString("N0"), null);

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(row.From);
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(row.From).X > ImGui.GetItemRectSize().X)
            ImGui.SetTooltip(row.From);

        for (var index = 0; index < containers.Count; index++)
        {
            ImGui.TableSetColumnIndex(3 + index);
            DrawCell(row, containers[index], rowHeight, destination, assign);
        }
        ImGui.PopID();
    }

    private void DrawCell(
        CourierPlacementRow row,
        (ulong? Id, string Name, int? FreeSlots) container,
        float rowHeight,
        string destination,
        Func<Guid, ulong?, bool> assign)
    {
        var style = ImGui.GetStyle();
        var width = ImGui.GetContentRegionAvail().X;
        var height = rowHeight - (style.CellPadding.Y * 2f);
        var origin = ImGui.GetCursorScreenPos();
        var key = container.Id?.ToString() ?? "bags";
        var clicked = ImGui.InvisibleButton($"##cell-{key}", new Vector2(Math.Max(1f, width), Math.Max(1f, height)));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var assigned = row.Targeted && row.Assigned == container.Id;
        var held = container.Id is { } retainerId ? row.Holdings.GetValueOrDefault(retainerId) : row.BagsHeld;
        registry.Register(
            $"{surfaceId}.cell.{row.Key:N}.{key}",
            assigned ? $"{row.Label} lands in {destination} · {container.Name}" : $"Send {row.Label} to {destination} · {container.Name}",
            AgentBridgeUiControlKind.Toggle,
            min,
            max,
            enabled: true,
            selected: assigned,
            $"holds={held}",
            arguments: null,
            surfaceId,
            mutating: true,
            completionOperationKind: null,
            _ => assigned
                ? AgentBridgeUiActionResult.Ok($"{row.Label} already lands in {container.Name}.")
                : assign(row.Key, container.Id)
                    ? AgentBridgeUiActionResult.Ok($"{row.Label} now lands in {destination} · {container.Name}.")
                    : AgentBridgeUiActionResult.Fail("The plan refused the placement change."));

        var drawList = ImGui.GetWindowDrawList();
        var cellMin = new Vector2(min.X - style.CellPadding.X, min.Y - style.CellPadding.Y);
        var cellMax = new Vector2(max.X + style.CellPadding.X, max.Y + style.CellPadding.Y);
        if (assigned)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(AssignedColor));
        else if (hovered)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(HoverCellColor));
        if (held > 0)
        {
            var smallScale = .85f;
            var text = held.ToString("N0");
            var size = ImGui.CalcTextSize(text) * smallScale;
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * smallScale,
                new Vector2(origin.X + Math.Max(0f, (width - size.X) / 2f), origin.Y + ((height - size.Y) / 2f)),
                ImGui.GetColorU32(assigned ? new Vector4(1f, 1f, 1f, 1f) : HoldColor), text);
        }

        if (hovered)
        {
            var free = container.FreeSlots is { } slots ? $"{slots:N0} free slot{(slots == 1 ? string.Empty : "s")}" : "free slots unknown";
            ImGui.SetTooltip($"{container.Name} · holds {held:N0} · {free}");
        }
        if (clicked && !assigned)
            assign(row.Key, container.Id);
    }
}
