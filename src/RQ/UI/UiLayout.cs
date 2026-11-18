using System.Numerics;
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

    internal static readonly Vector4 Primary = new(.48f, .13f, .13f, 1f);
    internal static readonly Vector4 PrimaryHovered = new(.57f, .16f, .16f, 1f);
    internal static readonly Vector4 PrimaryActive = new(.64f, .19f, .19f, 1f);

    /// <summary>The Market tab's primary action colour (Placement's Make first, Start run, Send to MarketMafioso).</summary>
    internal static void PushPrimary()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Primary);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, PrimaryHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, PrimaryActive);
    }

    internal static void PopPrimary() => ImGui.PopStyleColor(3);

    internal static bool PrimaryButton(string label, Vector2 size = default)
    {
        PushPrimary();
        var clicked = ImGui.Button(label, size);
        PopPrimary();
        return clicked;
    }
}
