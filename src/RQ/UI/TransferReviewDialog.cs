using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Domain;
using RQ.Runtime;

namespace RQ.UI;

internal sealed class TransferReviewDialog
{
    private readonly Func<QuartermasterRuntimeSnapshot> runtimeSnapshot;
    private readonly Func<QuartermasterRuntimeSnapshot, StowagePlan, TransferWorkbenchProjection> resolveProjection;
    private readonly Func<bool> transfersCanStart;
    private readonly Func<bool> retainerRefreshBusy;
    private readonly Action<Guid> executePlan;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudTableProjection<TransferReviewRow> table = CreateTable();
    private readonly DalamudTableProjection<TransferWorkbenchRow> courierTable = CreateCourierTable();
    private readonly CourierSession courier;
    private TransferReviewRequest? request;
    private bool openRequested;

    public TransferReviewDialog(
        Func<QuartermasterRuntimeSnapshot> runtimeSnapshot,
        Func<QuartermasterRuntimeSnapshot, StowagePlan, TransferWorkbenchProjection> resolveProjection,
        Func<bool> transfersCanStart,
        Func<bool> retainerRefreshBusy,
        Action<Guid> executePlan,
        AgentBridgeUiReviewRegistry reviewRegistry,
        CourierSession courier)
    {
        this.courier = courier ?? throw new ArgumentNullException(nameof(courier));
        this.runtimeSnapshot = runtimeSnapshot ?? throw new ArgumentNullException(nameof(runtimeSnapshot));
        this.resolveProjection = resolveProjection ?? throw new ArgumentNullException(nameof(resolveProjection));
        this.transfersCanStart = transfersCanStart ?? throw new ArgumentNullException(nameof(transfersCanStart));
        this.retainerRefreshBusy = retainerRefreshBusy ?? throw new ArgumentNullException(nameof(retainerRefreshBusy));
        this.executePlan = executePlan ?? throw new ArgumentNullException(nameof(executePlan));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public void Request(Guid planId, string planName)
    {
        request = new(planId, planName);
        openRequested = true;
    }

    public void Clear()
    {
        request = null;
        openRequested = false;
    }

    public TransferReviewDialogState CaptureState() => new(request, openRequested);

    public string? CaptureWindowName => request is { } current
        ? $"Execute {current.PlanName}##RQTransferReview"
        : null;

    public void RestoreState(TransferReviewDialogState state)
    {
        request = state.Request;
        openRequested = state.OpenRequested;
    }

    public void Draw()
    {
        if (request is not { } currentRequest)
            return;

        var popup = CaptureWindowName!;
        if (openRequested)
        {
            ImGui.SetNextWindowSize(
                new Vector2(Math.Min(860, ImGui.GetMainViewport().WorkSize.X - 80), 520),
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

        var runtime = runtimeSnapshot();
        var plan = runtime.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == currentRequest.PlanId && candidate.Owner.Matches(runtime.Owner));
        if (plan is null)
        {
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), "This Transfer Plan no longer exists.");
            if (ImGui.Button("Close"))
            {
                Clear();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        var projection = resolveProjection(runtime, plan);
        if (projection.Courier is { } courierPlan)
        {
            DrawCourier(runtime, plan, projection, courierPlan);
            ImGui.EndPopup();
            if (!open)
                Clear();
            return;
        }
        var movements = projection.Movements;

        ImGui.TextUnformatted($"{movements:N0} movements");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.52f, .79f, .94f, 1f), $"Retrieve {projection.Retrieval.NeededQuantity:N0}");
        ImGui.SameLine();
        ImGui.TextColored(
            new Vector4(.53f, .83f, .64f, 1f),
            projection.HasUnknownListingDemand ? "Stow —" : $"Stow {projection.Deposit.RequestedQuantity:N0}");
        ImGui.Separator();

        var reviewRows = projection.Rows
            .Select(row => new TransferReviewRow(
                row.Rule,
                row.Line,
                row.PlayerQuantity,
                row.Difference,
                row.ListingContribution,
                runtime))
            .SelectMany(row => row.Line is { Moves.Count: > 0 } line
                ? (line.Action == RQ.Planning.StowageAction.Move ? Enumerable.Empty<TransferReviewRow>() : [row])
                    .Concat(line.Moves.Select(move => row with { Move = move }))
                : [row])
            .ToArray();
        if (table.Begin(
                "RQTransferReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y - 48)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            table.DrawClippedRows(
                reviewRows,
                (row, index) => table.DrawRow(row, id: row.Move is null ? $"transfer-review:{row.Rule.Id}" : $"transfer-review:{row.Rule.Id}:move:{index}"));
            table.End();
        }

        if (ImGui.Button("Back"))
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.back",
            "Return to the Transfer Plan without executing",
            true,
            Clear,
            "No inventory movement");
        var availability = TransferExecutionPolicy.ForExplicitRun(
            projection.HasMovement || projection.HasUnknownListingDemand,
            runtime.Owner.HasStableIdentity,
            transfersCanStart(),
            retainerRefreshBusy());
        ImGui.SameLine();
        ImGui.TextDisabled(
            projection.HasUnknownListingDemand
                ? "Listing demand will be verified before any movement."
                : availability.BlockReason ??
                  "Balanced items remain in the plan but require no movement.");
        var canExecute = availability.CanExecute;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - 110));
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button("Execute plan"))
        {
            executePlan(plan.Id);
            Clear();
            ImGui.CloseCurrentPopup();
        }
        if (!canExecute)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.execute",
            "Execute the reviewed Transfer Plan",
            canExecute,
            () =>
            {
                executePlan(plan.Id);
                Clear();
            },
            canExecute ? $"{movements:N0} movements" : availability.BlockReason);

        ImGui.EndPopup();
        if (!open)
            Clear();
    }

    /// <summary>A plan with courier lines runs as a courier run: its review lists what goes where.</summary>
    private void DrawCourier(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan plan,
        TransferWorkbenchProjection projection,
        RQ.Courier.CourierPlan courierPlan)
    {
        var rows = projection.Rows.Where(row => row.Rule.Courier is not null && row.Rule.Enabled).ToArray();
        var units = rows.Sum(row => row.Projection.Send);
        ImGui.TextUnformatted($"{courierPlan.Trips.Length:N0} trip{(courierPlan.Trips.Length == 1 ? string.Empty : "s")}");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(TransferPresentation.SendColor, $"Send {units:N0}");
        ImGui.Separator();

        if (courierTable.Begin(
                "RQCourierReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y - 48)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            courierTable.DrawClippedRows(rows, (row, _) => courierTable.DrawRow(row, id: $"courier-review:{row.Rule.Id}"));
            courierTable.End();
        }

        if (ImGui.Button("Back"))
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.back",
            "Return to the Transfer Plan without executing",
            true,
            Clear,
            "No inventory movement");
        var mixed = projection.Rows.Any(row=>row.Rule.Enabled && row.Rule.Courier==null);
        var gate = courier.StartGate(runtime, courierPlan);
        var block = mixed
            ? "Mixed execution unavailable: ordinary transfers have no completion receipt for the courier."
            : gate.Status;
        var canStart = !mixed && gate.CanStart;
        if (block is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(block);
        }
        // An active or unsettled run is handled on the run pane; Start is not offered meanwhile.
        if (!mixed && !gate.Offer)
            return;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - 110));
        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Execute plan"))
        {
            courier.Start(runtime, plan);
            Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.review.execute",
            "Start the reviewed courier run",
            canStart,
            () =>
            {
                courier.Start(runtimeSnapshot(), plan);
                Clear();
            },
            block ?? (canStart ? $"{courierPlan.Trips.Length:N0} courier trips" : "Lines refused"));
    }

    private static DalamudTableProjection<TransferWorkbenchRow> CreateCourierTable() => new(
    [
        new(
            "Item",
            1.3f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new(
            "From",
            1f,
            row => CourierSession.Side(row.Runtime, row.Rule.Courier!.SourceCharacter, row.Rule.Courier.SourceRetainerId),
            Flags: ImGuiTableColumnFlags.WidthStretch),
        new(
            "To",
            1f,
            row => CourierSession.Side(row.Runtime, row.Rule.Courier!.Character, row.Rule.Courier.RetainerId),
            Flags: ImGuiTableColumnFlags.WidthStretch),
        new(
            "Units",
            72,
            row => row.Rule.TargetQuantity.ToString("N0"),
            row => row.Rule.TargetQuantity),
        new(
            "Outcome",
            1f,
            row => row.CourierRefusal is { } refusal ? CourierSession.RefusalLabel(refusal.Code) : $"Send {row.Projection.Send:N0}",
            Flags: ImGuiTableColumnFlags.WidthStretch,
            TextColor: row => row.CourierRefusal is null ? TransferPresentation.SendColor : new Vector4(1f, .45f, .4f, 1f)),
    ]);

    private static DalamudTableProjection<TransferReviewRow> CreateTable() => new(
    [
        new(
            "Item",
            1.3f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new(
            "Player / target",
            120,
            row => row.ListingContribution.IsKnown
                ? $"{row.PlayerQuantity:N0} / {row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity:N0}"
                : $"{row.PlayerQuantity:N0} / —"),
        new(
            "Diff",
            72,
            row => row.ListingContribution.IsKnown ? TransferPresentation.SignedQuantity(row.Difference) : "—",
            row => row.Difference,
            TextColor: row => TransferPresentation.ActionColor(row.Line?.Action)),
        new(
            "Planned movement",
            1.2f,
            TransferPresentation.ReviewOutcome,
            row => TransferPresentation.ReviewOutcome(row),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: row => TransferPresentation.ActionColor(row.Line?.Action)),
    ]);
}

internal sealed record TransferReviewDialogState(
    TransferReviewRequest? Request,
    bool OpenRequested);
