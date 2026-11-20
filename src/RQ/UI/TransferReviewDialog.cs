using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Courier;
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
    private readonly DalamudTableProjection<TransferReviewRow> table = CreateTable();
    private readonly DalamudTableProjection<TransferWorkbenchRow> courierTable = CreateCourierTable();
    private readonly CourierSession courier;
    private readonly ReviewModal modal;
    private TransferReviewRequest? request;

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
        modal = new(reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry)), 860, Clear);
    }

    public void Request(Guid planId, string planName)
    {
        request = new(planId, planName);
        modal.OpenRequested = true;
    }

    public void Clear()
    {
        request = null;
        modal.OpenRequested = false;
    }

    public TransferReviewDialogState CaptureState() => new(request, modal.OpenRequested);

    public string? CaptureWindowName => request is { } current
        ? $"Execute {current.PlanName}##RQTransferReview"
        : null;

    public void RestoreState(TransferReviewDialogState state)
    {
        request = state.Request;
        modal.OpenRequested = state.OpenRequested;
    }

    public void Draw()
    {
        if (request is not { } currentRequest)
            return;

        if (!modal.Begin(CaptureWindowName!))
            return;
        try
        {
            DrawBody(currentRequest);
        }
        finally
        {
            modal.End();
        }
    }

    private void DrawBody(TransferReviewRequest currentRequest)
    {
        var runtime = runtimeSnapshot();
        var plan = runtime.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == currentRequest.PlanId && candidate.Owner.Matches(runtime.Owner));
        if (plan is null)
        {
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), "This Transfer Plan no longer exists.");
            if (ImGui.Button("Close"))
                modal.Close();
            return;
        }

        var projection = resolveProjection(runtime, plan);
        // A Courier plan's review is the courier run's; a Transfer plan never holds courier lines.
        if (plan.Kind == StowagePlanKind.Courier)
        {
            DrawCourier(runtime, plan, projection, projection.Courier);
            return;
        }
        var movementSummary = projection.MovementSummary;

        ImGui.TextUnformatted(movementSummary);
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
            .Where(row => row.Rule.Courier is null)
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
        var tableHeight = Math.Max(260, ImGui.GetContentRegionAvail().Y - 48);
        if (table.Begin(
                "RQTransferReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, tableHeight),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            table.DrawClippedRows(
                reviewRows,
                (row, index) => table.DrawRow(row, id: row.Move is null ? $"transfer-review:{row.Rule.Id}" : $"transfer-review:{row.Rule.Id}:move:{index}"));
            table.End();
        }

        modal.DrawBack(
            "Back",
            "quartermaster.transfer.review.back",
            "Return to the Transfer Plan without executing",
            "No inventory movement");
        var availability = TransferExecutionPolicy.ForExplicitRun(
            projection.HasMovement || projection.HasUnknownListingDemand,
            runtime.Owner.HasStableIdentity,
            transfersCanStart(),
            retainerRefreshBusy());
        const string executeLabel = "Execute plan";
        ReviewModal.DrawNote(
            projection.HasUnknownListingDemand
                ? "Listing demand will be verified before any movement."
                : availability.BlockReason ?? "Balanced items remain in the plan but require no movement.",
            executeLabel);
        var canExecute = availability.CanExecute;
        if (modal.DrawPrimary(
                executeLabel,
                canExecute,
                "quartermaster.transfer.review.execute",
                "Execute the reviewed Transfer Plan",
                () =>
                {
                    executePlan(plan.Id);
                    Clear();
                },
                canExecute ? movementSummary : availability.BlockReason))
        {
            executePlan(plan.Id);
            modal.Close();
        }
    }

    /// <summary>A Courier plan runs as a courier run: its review lists the trips and what goes where.</summary>
    private void DrawCourier(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan plan,
        TransferWorkbenchProjection projection,
        CourierPlan? courierPlan)
    {
        var rows = CourierRows(projection);
        var units = rows.Sum(row => row.Projection.Send);
        var trips = courierPlan?.Trips.Length ?? 0;
        ImGui.TextUnformatted($"{trips:N0} trip{(trips == 1 ? string.Empty : "s")}");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextColored(TransferPresentation.SendColor, $"Send {units:N0}");
        ImGui.Separator();

        DrawCourierRows(runtime, courierPlan, rows);

        modal.DrawBack(
            "Back",
            "quartermaster.transfer.review.back",
            "Return to the Courier Plan without executing",
            "No inventory movement");
        const string executeLabel = "Execute courier run";
        var gate = courier.StartGate(runtime, courierPlan);
        var block = gate.Status;
        var canStart = gate.CanStart;
        if (block is not null)
            ReviewModal.DrawNote(block, gate.Offer ? executeLabel : null);
        // An active or unsettled run is handled on the run pane; Start is not offered meanwhile.
        if (!gate.Offer)
            return;
        if (modal.DrawPrimary(
                executeLabel,
                canStart,
                "quartermaster.transfer.review.execute",
                "Start the reviewed courier run",
                () =>
                {
                    courier.Start(runtimeSnapshot(), plan);
                    Clear();
                },
                block ?? (canStart ? $"{trips:N0} courier trips" : "Lines refused")))
        {
            courier.Start(runtime, plan);
            modal.Close();
        }
    }

    private static TransferWorkbenchRow[] CourierRows(TransferWorkbenchProjection projection) =>
        projection.Rows.Where(row => row.Rule.Courier is not null && row.Rule.Enabled).ToArray();

    private void DrawCourierRows(QuartermasterRuntimeSnapshot runtime, CourierPlan? plan, IReadOnlyList<TransferWorkbenchRow> rows)
    {
        var gathers = ConsolidationPreview(runtime, plan, rows);
        if (plan is { Trips.IsEmpty: false })
        {
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();
            var height = Math.Min((plan.Trips.Length + gathers.Count) * lineHeight + ImGui.GetStyle().ScrollbarSize,
                Math.Max(lineHeight, (ImGui.GetContentRegionAvail().Y - 48) * .4f));
            if (ImGui.BeginChild("RQCourierReviewTrips", new Vector2(0, height), false, ImGuiWindowFlags.HorizontalScrollbar))
            {
                for (var i = 0; i < plan.Trips.Length; i++)
                {
                    var trip = plan.Trips[i];
                    var stacks = trip.Manifest.Length;
                    ImGui.TextDisabled($"Trip {i + 1} · {CourierSession.CharacterLabel(trip.Source)} at {trip.Pickup.Label} → " +
                        $"{CourierSession.CharacterLabel(trip.Destination)} at {trip.Dropoff.Label} · {stacks:N0} stack{(stacks == 1 ? string.Empty : "s")}");
                }
                foreach (var (text, limited) in gathers)
                {
                    if (limited)
                        ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), text);
                    else
                        ImGui.TextColored(TransferPresentation.SendColor, text);
                }
            }
            ImGui.EndChild();
        }
        if (!courierTable.Begin(
                "RQCourierReviewRows",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(ImGui.GetFrameHeight(), ImGui.GetContentRegionAvail().Y - 48)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
            return;
        courierTable.DrawClippedRows(rows, (row, _) => courierTable.DrawRow(row, id: $"courier-review:{row.Rule.Id}"));
        courierTable.End();
    }

    /// <summary>
    /// The consolidation each delivering destination runs after its last drop-off, as planned from
    /// today's stock: the moves into each line's rank 1, then anything that limits them. The run
    /// plans the moves again after its deposits, so the delivered units are gathered as well.
    /// </summary>
    private List<(string Text, bool Limited)> ConsolidationPreview(
        QuartermasterRuntimeSnapshot runtime,
        CourierPlan? plan,
        IReadOnlyList<TransferWorkbenchRow> rows)
    {
        var preview = new List<(string, bool)>();
        if (plan is null)
            return preview;
        foreach (var line in plan.Consolidations)
        {
            var rule = rows.FirstOrDefault(row => row.Rule.Id == line.Line)?.Rule;
            if (rule is null)
                continue;
            var into = $"{CourierSession.CharacterLabel(line.Destination)} · {CourierSession.ContainerLabel(runtime, line.Retainer)}";
            var gather = courier.Gather(runtime, rule);
            var moves = gather?.Moves
                .GroupBy(move => move.SourceRetainerId)
                .Select(group => $"{group.Sum(move => move.Quantity):N0} from {CourierSession.ContainerLabel(runtime, group.Key)}")
                .ToArray() ?? [];
            preview.Add(($"Consolidate {line.Name} into {into}" +
                (moves.Length > 0 ? $" · {string.Join(", ", moves)}" : " · delivered units only"), false));
            foreach (var limit in gather?.Limits ?? [])
                preview.Add(($"  {limit.Message}", true));
        }
        return preview;
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
            row => CourierSession.Side(row.Runtime, row.Rule.Courier!.SourceCharacter, row.Rule.Courier.SourceRetainerId, includeOwner: true),
            Flags: ImGuiTableColumnFlags.WidthStretch),
        new(
            "To",
            1f,
            row => CourierSession.Destination(row.Runtime, row.Rule.Courier!.Character, row.Rule.Routing, includeOwner: true),
            Flags: ImGuiTableColumnFlags.WidthStretch),
        new(
            "Send",
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
