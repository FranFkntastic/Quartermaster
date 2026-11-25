using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Windows;
using RQ.Domain;
using RQ.Operations;
using Franthropy.Dalamud.AgentBridge;

namespace RQ.UI;

internal sealed class OperationHistoryDialog(OperationJournal journal, AgentBridgeUiReviewRegistry reviewRegistry)
{
    private const string PopupLabel = "Transfer history##RQ";

    /// <summary>Cancels an accepted requested operation by request id (the requester cancel path); returns the outcome.</summary>
    public Func<string, string>? CancelRequestedOperation { get; set; }
    private readonly Dictionary<string, string> cancelOutcomes = new(StringComparer.Ordinal);

    private bool openRequested;
    private bool closeRequested;
    private bool capturePresentationActive;

    public string? CaptureWindowName { get; private set; }

    public void RequestOpen() => openRequested = true;

    public void BeginCapturePresentation() => capturePresentationActive = true;

    public void RestoreCapturePresentation()
    {
        if (capturePresentationActive)
            closeRequested = true;
        capturePresentationActive = false;
    }

    public void Draw(OwnerScope owner)
    {
        const string popup = PopupLabel;
        if (openRequested || capturePresentationActive)
        {
            if (!ImGui.IsPopupOpen(popup))
            {
                ImGui.SetNextWindowPos(
                    ImGui.GetWindowPos() + (ImGui.GetWindowSize() * 0.5f),
                    ImGuiCond.Appearing,
                    new Vector2(0.5f, 0.5f));
                ImGui.SetNextWindowSize(
                    new Vector2(430, Math.Min(620, ImGui.GetMainViewport().WorkSize.Y - 80)),
                    ImGuiCond.Appearing);
                ImGui.OpenPopup(popup);
            }
            openRequested = false;
        }
        if (!DalamudOwnedPopup.BeginOpaquePopup(popup))
        {
            CaptureWindowName = null;
            closeRequested = false;
            return;
        }
        unsafe
        {
            CaptureWindowName = Marshal.PtrToStringUTF8((nint)ImGuiP.GetCurrentWindow().Name);
        }
        if (closeRequested)
        {
            closeRequested = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("Transfer history");
        ImGui.Separator();
        var operations = journal.History(owner, 30);
        if (operations.Count == 0)
        {
            ImGui.TextDisabled("No Quartermaster operations yet.");
            ImGui.EndPopup();
            return;
        }

        if (ImGui.BeginChild(
                "RQHistoryRows",
                new Vector2(410, Math.Min(540, ImGui.GetContentRegionAvail().Y)),
                false))
        {
            foreach (var operation in operations)
            {
                var succeeded = operation.Status == OperationStatuses.Succeeded;
                var failed = operation.Status is OperationStatuses.Failed or OperationStatuses.Indeterminate;
                ImGui.TextColored(
                    failed
                        ? new Vector4(1f, .45f, .45f, 1f)
                        : succeeded
                            ? new Vector4(.53f, .83f, .64f, 1f)
                            : new Vector4(.69f, .74f, .77f, 1f),
                    operation.Status);
                ImGui.SameLine();
                ImGui.TextDisabled(operation.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                ImGui.TextUnformatted(operation.SourcePlanName ?? "Quartermaster transfer");
                if (operation.Status == OperationStatuses.Accepted && operation.ExecuteImmediately &&
                    operation.RequestId is { Length: > 0 } requestId && CancelRequestedOperation is { } cancel)
                {
                    ImGui.SameLine();
                    void Cancel() => cancelOutcomes[requestId] = cancel(requestId);
                    if (ImGui.SmallButton($"Cancel##RQHistoryCancel{operation.OperationId}"))
                        Cancel();
                    reviewRegistry.RegisterLastButton(
                        $"quartermaster.history.cancel.{operation.OperationId}",
                        $"Cancel requested operation {operation.OperationId}",
                        true,
                        Cancel,
                        operation.Status);
                }
                ImGui.TextWrapped(operation.Message);
                if (cancelOutcomes.TryGetValue(operation.RequestId, out var outcome))
                    ImGui.TextDisabled(outcome);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }
}
