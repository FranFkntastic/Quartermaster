using Dalamud.Bindings.ImGui;

namespace RQ.UI;

internal static class UiLayout
{
    /// <summary>Continues the current row when a button with this label still fits; otherwise wraps.</summary>
    internal static void SameLineIfFits(string label)
    {
        ImGui.SameLine();
        var needed = ImGui.CalcTextSize(label.Split("##")[0]).X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (ImGui.GetContentRegionAvail().X < needed) ImGui.NewLine();
    }
}
