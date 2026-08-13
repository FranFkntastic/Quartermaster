using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RQ.Domain;
using RQ.Operations;

namespace RQ.UI;

internal sealed class OperationHistoryDialog(OperationJournal journal)
{
    private bool openRequested;
    private bool closeRequested;
    private bool capturePresentationActive;

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
        const string popup = "Transfer history##RQ";
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
        if (!ImGui.BeginPopup(popup))
        {
            closeRequested = false;
            return;
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
                ImGui.TextWrapped(operation.Message);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }
}
