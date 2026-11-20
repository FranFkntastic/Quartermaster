using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Layout;

namespace RQ.UI;

/// <summary>
/// The two-pane manager layout shared by the plan manager and Item Groups: a resizable list
/// (filter box, two-line rows, full-width actions at the bottom) beside a detail pane.
/// </summary>
internal static class MasterDetailPane
{
    internal const float ListWidth = 210f;

    /// <summary>Draws both panes, filling the remaining height of the current region.</summary>
    /// <param name="id">Table ID; the panes' children derive their IDs from it.</param>
    internal static void Draw(string id, Action drawList, Action drawDetail)
    {
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        var height = Math.Max(260, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        if (!ImGui.BeginTable(id, 2, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupColumn("List", ImGuiTableColumnFlags.WidthFixed, ListWidth);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild($"{id}List", Vector2.Zero, false))
            drawList();
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild($"{id}Detail", Vector2.Zero, false))
            drawDetail();
        ImGui.EndChild();
        ImGui.EndTable();
    }

    /// <summary>The list pane's filter box, full width, then a separator.</summary>
    internal static void DrawFilter(string id, string hint, ref string filter)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint(id, hint, ref filter, 80);
        ImGui.Separator();
    }

    /// <summary>Scrolls the list rows above <paramref name="footerButtons"/> full-width buttons and a separator.</summary>
    internal static void DrawRows(string id, int footerButtons, Action drawRows)
    {
        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * footerButtons) + ImGui.GetStyle().ItemSpacing.Y + 2;
        if (ImGui.BeginChild(id, new Vector2(0, -footerHeight), false))
            drawRows();
        ImGui.EndChild();
        ImGui.Separator();
    }

    /// <summary>A full-width action at the bottom of the list pane.</summary>
    internal static bool FooterButton(string label) => ImGui.Button(label, new Vector2(-1, 0));

    /// <summary>
    /// The selectable of a two-line row: <paramref name="title"/>, elided to the pane with the full
    /// text in a tooltip. It is the last item, so callers can register it with the bridge or check
    /// double-clicks, then draw the second line with <see cref="RowDetail"/>.
    /// </summary>
    /// <returns>True when the row was clicked.</returns>
    internal static bool Row(string id, string title, bool selected, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        var display = DalamudUiLayout.ElideText(title, ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().FramePadding.X * 2f));
        var clicked = ImGui.Selectable(
            $"{display}##{id}",
            selected,
            flags,
            new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 1.8f));
        if (display.Length != title.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(title);
        return clicked;
    }

    /// <summary>The second line of the row <see cref="Row"/> just drew, indented inside its selectable.</summary>
    /// <remarks>Call immediately after <see cref="Row"/> (and any registration of it); skipped when the row is clipped.</remarks>
    internal static void RowDetail(Action drawDetail)
    {
        if (!ImGui.IsItemVisible())
            return;
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPosY(cursor.Y - ImGui.GetTextLineHeightWithSpacing());
        ImGui.Indent();
        drawDetail();
        ImGui.Unindent();
    }
}
