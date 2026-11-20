using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Windows;

namespace RQ.UI;

/// <summary>
/// The shell shared by the review modals that gate a run (transfer execution, vendor buys): the
/// open request, first-open sizing, an owned modal with a close button, and a footer of Back, an
/// optional note, and a right-aligned primary action, each registered with the agent bridge.
/// </summary>
/// <remarks>
/// The owning dialog keeps its request and passes the window name each frame. Between
/// <see cref="Begin"/> returning true and <see cref="End"/>, the footer helpers draw into the modal.
/// </remarks>
internal sealed class ReviewModal(AgentBridgeUiReviewRegistry reviewRegistry, float maxWidth, Action clear)
{
    private const float DefaultHeight = 520f;
    private bool open;

    /// <summary>Opens the modal on the next <see cref="Begin"/>; part of the dialog's captured state.</summary>
    public bool OpenRequested { get; set; }

    /// <summary>Begins the modal named <paramref name="windowName"/>, opening it first when requested.</summary>
    /// <returns>True when the modal is showing; pair with <see cref="End"/>. Closing it with its close button clears the dialog.</returns>
    public bool Begin(string windowName)
    {
        if (OpenRequested)
        {
            ImGui.SetNextWindowSize(
                new Vector2(Math.Min(maxWidth, ImGui.GetMainViewport().WorkSize.X - 80), DefaultHeight),
                ImGuiCond.Appearing);
            ImGui.OpenPopup(windowName);
            OpenRequested = false;
        }

        open = true;
        if (DalamudOwnedPopup.BeginOwnedModal(windowName, ref open, ImGuiWindowFlags.NoScrollbar))
            return true;
        if (!open)
            clear();
        return false;
    }

    public void End()
    {
        ImGui.EndPopup();
        if (!open)
            clear();
    }

    /// <summary>Clears the dialog and closes the modal, as after its action ran.</summary>
    public void Close()
    {
        clear();
        ImGui.CloseCurrentPopup();
    }

    /// <summary>The footer's Back button: closes without acting.</summary>
    public void DrawBack(string label, string bridgeId, string description, string bridgeState)
    {
        if (ImGui.Button(label))
            Close();
        reviewRegistry.RegisterLastButton(bridgeId, description, true, clear, bridgeState);
    }

    /// <summary>A muted note after Back, elided to leave room for the primary action labelled <paramref name="primaryLabel"/>, if one follows.</summary>
    public static void DrawNote(string note, string? primaryLabel)
    {
        ImGui.SameLine();
        var reserved = primaryLabel is null ? 0f : DalamudUiLayout.ButtonWidth(primaryLabel) + ImGui.GetStyle().ItemSpacing.X;
        var room = ImGui.GetContentRegionAvail().X - reserved;
        var shown = DalamudUiLayout.ElideText(note, room);
        ImGui.TextDisabled(shown);
        if (shown.Length != note.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(note);
    }

    /// <summary>The footer's primary action, right-aligned on the Back row and disabled while <paramref name="enabled"/> is false.</summary>
    /// <returns>True when the user pressed it this frame.</returns>
    public bool DrawPrimary(
        string label,
        bool enabled,
        string bridgeId,
        string description,
        Action bridgeInvoke,
        string? bridgeState)
    {
        DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth(label));
        ImGui.BeginDisabled(!enabled);
        var pressed = ImGui.Button(label);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(bridgeId, description, enabled, bridgeInvoke, bridgeState);
        return pressed && enabled;
    }
}
