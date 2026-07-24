using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RQ.UI;

/// <summary>
/// Hosts destructive confirmations at the root window level. Call <see cref="Request"/>
/// from any child, table, or popup, then call <see cref="Draw"/> once after those
/// containers have ended so the modal can never capture input without rendering.
/// </summary>
internal sealed class RootConfirmationDialog
{
    private ConfirmationRequest? pending;
    private bool openRequested;
    private string error = string.Empty;

    public void Request(
        string id,
        string title,
        string message,
        string confirmLabel,
        Action confirm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        ArgumentNullException.ThrowIfNull(confirm);

        pending = new(id, title, message, confirmLabel, confirm);
        openRequested = true;
        error = string.Empty;
    }

    public void Draw()
    {
        if (pending is not { } request)
            return;

        var popupId = $"{request.Title}##RQConfirm:{request.Id}";
        if (openRequested)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.WorkPos + (viewport.WorkSize * 0.5f),
                ImGuiCond.Appearing,
                new Vector2(0.5f, 0.5f));
            ImGui.OpenPopup(popupId);
            openRequested = false;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open)
                Clear();
            return;
        }

        ImGui.TextWrapped(request.Message);
        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), error);

        if (ImGui.Button($"Cancel##RQConfirmCancel:{request.Id}"))
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{request.ConfirmLabel}##RQConfirmAccept:{request.Id}"))
        {
            try
            {
                request.Confirm();
                Clear();
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }

        ImGui.EndPopup();
        if (!open)
            Clear();
    }

    private void Clear()
    {
        pending = null;
        openRequested = false;
        error = string.Empty;
    }

    private sealed record ConfirmationRequest(
        string Id,
        string Title,
        string Message,
        string ConfirmLabel,
        Action Confirm);
}
