using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Automation;
using RQ.Domain;
using RQ.Planning;

namespace RQ.UI;

internal sealed class VendorProcurementReviewDialog
{
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action onStartSucceeded;
    private readonly DalamudTableProjection<TransferVendorProcurementLine> table = CreateTable();
    private TransferVendorProcurementReview? review;
    private bool openRequested;
    private string status = string.Empty;

    public VendorProcurementReviewDialog(
        TransferVendorProcurementService vendorProcurement,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action onStartSucceeded)
    {
        this.vendorProcurement = vendorProcurement ?? throw new ArgumentNullException(nameof(vendorProcurement));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.onStartSucceeded = onStartSucceeded ?? throw new ArgumentNullException(nameof(onStartSucceeded));
    }

    public void Request(TransferVendorProcurementReview requestedReview)
    {
        review = requestedReview ?? throw new ArgumentNullException(nameof(requestedReview));
        openRequested = true;
        status = string.Empty;
    }

    public void Clear()
    {
        review = null;
        openRequested = false;
        status = string.Empty;
    }

    public VendorProcurementReviewDialogState CaptureState() => new(review, openRequested, status);

    public void RestoreState(VendorProcurementReviewDialogState state)
    {
        review = state.Review;
        openRequested = state.OpenRequested;
        status = state.Status;
    }

    public void Draw()
    {
        if (review is not { } currentReview)
            return;

        var popup = $"Buy vendor shortfalls for {currentReview.PlanName}##RQVendorReview";
        if (openRequested)
        {
            ImGui.SetNextWindowSize(
                new Vector2(Math.Min(900, ImGui.GetMainViewport().WorkSize.X - 80), 520),
                ImGuiCond.Appearing);
            ImGui.OpenPopup(popup);
            openRequested = false;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(popup, ref open, ImGuiWindowFlags.NoScrollbar))
        {
            if (!open)
                Clear();
            return;
        }

        ImGui.TextUnformatted($"{currentReview.ApprovedQuantity:N0} units · {currentReview.Stops.Count:N0} vendor stops · maximum {currentReview.MaximumGil:N0} gil");
        ImGui.TextDisabled("Only the shortage left after accessible retainer stock is approved. Live inventory, gil, capacity, shop identity, and price are checked again before purchase.");
        ImGui.Separator();
        if (table.Begin(
                "RQVendorReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(280, ImGui.GetContentRegionAvail().Y - 56)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            table.DrawClippedRows(
                currentReview.Lines,
                (line, _) => table.DrawRow(line, id: $"vendor-review:{line.RuleId}"));
            table.End();
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, .4f, .4f, 1f));
            ImGui.TextWrapped(status);
            ImGui.PopStyleColor();
        }
        if (ImGui.Button("Back##RQVendorReview"))
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }
        var canStart = currentReview.CanStart && !vendorProcurement.HasActiveRun;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - 150));
        if (!canStart)
            ImGui.BeginDisabled();
        if (ImGui.Button("Start vendor buy") && TryStart(currentReview))
            ImGui.CloseCurrentPopup();
        if (!canStart)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.vendor.start",
            "Start the reviewed ordinary-gil vendor purchase",
            canStart,
            () =>
            {
                if (review is { } pendingReview)
                    TryStart(pendingReview);
            },
            canStart ? $"Maximum {currentReview.MaximumGil:N0} gil" : "No startable reviewed purchase");

        ImGui.EndPopup();
        if (!open)
            Clear();
    }

    private bool TryStart(TransferVendorProcurementReview currentReview)
    {
        if (!vendorProcurement.TryStart(currentReview, out var error))
        {
            status = error;
            return false;
        }

        onStartSucceeded();
        Clear();
        return true;
    }

    private static DalamudTableProjection<TransferVendorProcurementLine> CreateTable() => new(
    [
        new(
            "Item",
            1.3f,
            line => $"{line.ItemName} {QualityLabel(line.Quality)}",
            line => line.ItemName,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: line =>
            {
                ImGui.TextUnformatted(line.ItemName);
                ImGui.SameLine();
                ImGui.TextDisabled(QualityLabel(line.Quality));
            }),
        new("Buy", 64, line => line.ApprovedQuantity.ToString("N0"), line => line.ApprovedQuantity),
        new(
            "Vendor",
            1.2f,
            line => line.SelectedCandidate?.Offer.NpcName ?? "Unavailable",
            line => line.SelectedCandidate?.Offer.NpcName ?? string.Empty,
            ImGuiTableColumnFlags.WidthStretch),
        new(
            "Unit price",
            92,
            line => line.SelectedCandidate is null ? "—" : $"{line.SelectedCandidate.Offer.UnitPriceGil:N0} gil",
            line => line.SelectedCandidate?.Offer.UnitPriceGil ?? uint.MaxValue),
        new("Maximum", 96, line => line.IsReady ? $"{line.MaximumGil:N0} gil" : "—", line => line.MaximumGil),
        new(
            "Status",
            1.5f,
            line => line.Message,
            line => line.State.ToString(),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: line => line.IsReady
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text]
                : new Vector4(1f, .55f, .35f, 1f)),
    ]);

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };
}

internal sealed record VendorProcurementReviewDialogState(
    TransferVendorProcurementReview? Review,
    bool OpenRequested,
    string Status);
