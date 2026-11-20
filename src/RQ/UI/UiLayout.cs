using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RQ.UI;

/// <summary>Quartermaster's semantic state colours, shared by every surface.</summary>
internal static class QuartermasterPalette
{
    internal static readonly Vector4 Amber = new(1, .7f, .3f, 1);
    internal static readonly Vector4 Green = new(.45f, .78f, .58f, 1);
    internal static readonly Vector4 Info = new(.56f, .70f, .85f, 1);
    internal static readonly Vector4 Run = new(.29f, .62f, .85f, 1);

    internal static Vector4 Dim => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

    internal static Vector4 Tone(ListingStateTone tone) => tone switch
    {
        ListingStateTone.Amber => Amber,
        ListingStateTone.Green => Green,
        ListingStateTone.Info => Info,
        ListingStateTone.Run => Run,
        _ => Dim,
    };
}

internal static class UiLayout
{
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
