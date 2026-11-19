using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using RQ.Courier;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The courier run bar above the plan pane: the owner client's run (phases, trip, per-line
/// progress, Stop/Resume) and, on a carrier client, what the carrier is doing and holding.
/// Renders nothing while neither side has anything to show.
/// </summary>
internal sealed class CourierRunPane(CourierSession courier, AgentBridgeUiReviewRegistry reviewRegistry)
{
    private static float BarHeight => (ImGui.GetTextLineHeight() * 2.4f) + 4;
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var drew = DrawOwner(runtime.State.Courier.Carrier?.CharacterName);
        drew |= DrawCarrier();
        if (courier.Error is { Length: > 0 } error)
        {
            ImGui.TextColored(ErrorColor, error);
            drew = true;
        }
        if (drew)
            ImGui.Separator();
    }

    private bool DrawOwner(string? carrier)
    {
        var run = courier.Run;
        if (courier.IsActive)
        {
            DrawRunning(run);
            DrawLines(run);
            return true;
        }
        if (run is null || run.Run == Guid.Empty || run.State == "Idle")
            return false;
        if (courier.IsUnresolved)
        {
            DrawUnresolved(run, carrier);
            if (run.CanClear)
                DrawClear(run, false);
            else if (!run.CanResume && run.Unverified is { } step)
                DrawCustody("##RQCourierUnverified", step.Rows);
            else if (!run.CanResume && !run.Custody.IsEmpty)
                DrawCustody("##RQCourierRunCustody", run.Custody);
            DrawLines(run);
            return true;
        }
        DrawLastOutcome(run, carrier);
        if (run.CanClear)
            DrawClear(run, true);
        return true;
    }

    private static string StateLabel(string state) => state switch
    {
        "Indeterminate" => "Unverified",
        _ => CourierSession.Words(state),
    };

    private void DrawRunning(CourierSnapshot? run)
    {
        var height = BarHeight;
        if (ImGui.Button("Stop##RQCourierStop", new Vector2(ImGui.CalcTextSize("Stop").X + 40, height)))
            courier.Stop();
        reviewRegistry.RegisterLastButton("quartermaster.courier.run.stop", "Stop the courier run at the next safe point", true,
            courier.Stop, run is null ? "Starting" : CourierSession.PhaseLabel(run.OwnerPhase));
        ImGui.SameLine(0, 16);
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var status = run is null || run.State != "Running"
            ? "Starting"
            : $"{CourierSession.PhaseLabel(run.OwnerPhase)} · Carrier: {CourierSession.PhaseLabel(run.CarrierPhase)}";
        var counts = run is { Trips: > 0 } ? $"Trip {Math.Max(1, run.Trip):N0} of {run.Trips:N0}" : string.Empty;
        var countsWidth = counts.Length == 0 ? 0 : ImGui.CalcTextSize(counts).X;
        var line = ImGui.GetTextLineHeight();
        var top = origin.Y + ((height - line - 10) / 2);
        var draw = ImGui.GetWindowDrawList();
        ImGui.PushClipRect(origin, new Vector2(origin.X + width - countsWidth - 12, origin.Y + height), true);
        draw.AddText(new Vector2(origin.X, top), ImGui.GetColorU32(ImGuiCol.Text), status);
        ImGui.PopClipRect();
        if (counts.Length > 0)
            draw.AddText(new Vector2(origin.X + width - countsWidth, top), ImGui.GetColorU32(ImGuiCol.TextDisabled), counts);
        var moved = run?.Lines.Sum(progress => progress.Moved) ?? 0;
        var total = moved + (run?.Lines.Sum(progress => Math.Max(0, progress.Remaining)) ?? 0);
        if (total > 0)
        {
            var barTop = top + line + 4;
            draw.AddRectFilled(new Vector2(origin.X, barTop), new Vector2(origin.X + width, barTop + 6), ImGui.GetColorU32(SegmentBar.Empty));
            var fraction = Math.Clamp((float)moved / total, 0, 1);
            if (fraction > 0)
                draw.AddRectFilled(new Vector2(origin.X, barTop), new Vector2(origin.X + (width * fraction), barTop + 6),
                    ImGui.GetColorU32(UiLayout.PrimaryActive));
        }
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>What the run still holds, then a plain Clear: the table is what Clear acknowledges.</summary>
    private void DrawClear(CourierSnapshot run, bool sameLine)
    {
        var rows = courier.ClearLedger.IsEmpty ? run.Custody : courier.ClearLedger;
        if (!rows.IsEmpty)
            DrawCustody("##RQCourierRunCustody", rows);
        else if (sameLine)
            ImGui.SameLine(0, 16);
        void Clear() => courier.Clear(run.Run, rows);
        if (ImGui.SmallButton("Clear##RQCourierClear"))
            Clear();
        var units = rows.Sum(row => row.Quantity);
        reviewRegistry.RegisterLastButton("quartermaster.courier.run.clear", "Clear the courier run", true, Clear,
            units > 0 ? $"{units:N0} held by {string.Join(", ", rows.Select(row => row.Holder).Distinct())}" : "Nothing held");
    }

    private void DrawUnresolved(CourierSnapshot run, string? carrier)
    {
        var height = BarHeight;
        if (run.CanResume)
        {
            if (UiLayout.PrimaryButton("Resume##RQCourierResume", new Vector2(ImGui.CalcTextSize("Resume").X + 40, height)))
                courier.Resume();
            reviewRegistry.RegisterLastButton("quartermaster.courier.run.resume", "Resume the courier run from its journal", true,
                courier.Resume, $"{run.State}: {run.Code}");
            ImGui.SameLine(0, 16);
        }
        ImGui.BeginGroup();
        var top = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(top + Math.Max(0, (height - (ImGui.GetTextLineHeight() * 2) - ImGui.GetStyle().ItemSpacing.Y) / 2));
        var amber = ListingWorkspace.Color(ListingStateTone.Amber);
        if (!run.CanResume && !run.CanClear && run.Unverified is { } step)
        {
            ImGui.TextColored(amber, CourierSession.UnverifiedLabel(step));
        }
        else if (!run.CanResume && !run.CanClear && !run.Custody.IsEmpty)
        {
            ImGui.TextColored(amber, $"{run.Custody[0].Holder} not connected");
        }
        else
        {
            ImGui.TextColored(amber, StateLabel(run.State));
            var outcome = CourierSession.OutcomeLabel(run.Code, carrier);
            ImGui.TextDisabled(run.Trips > 0 ? $"{outcome} · Trip {Math.Max(1, run.Trip):N0} of {run.Trips:N0}" : outcome);
        }
        ImGui.EndGroup();
    }

    /// <summary>The finished run's outcome; the line counts sit in the tooltip.</summary>
    private static void DrawLastOutcome(CourierSnapshot run, string? carrier)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Last courier run");
        ImGui.SameLine();
        var color = run.State == "Completed"
            ? ListingWorkspace.Color(ListingStateTone.Green)
            : ListingWorkspace.Color(ListingStateTone.Amber);
        ImGui.TextColored(color, run.State == "Completed" ? "Completed" : $"{StateLabel(run.State)} · {CourierSession.OutcomeLabel(run.Code, carrier)}");
        if (!ImGui.IsItemHovered())
            return;
        var lines = run.Lines.Select(progress => $"{progress.Name}: {progress.Moved:N0} moved" +
            (progress.Remaining > 0 ? $", {progress.Remaining:N0} left" : string.Empty));
        var refusals = run.Refusals.Select(refusal => $"{refusal.Name}: {CourierSession.RefusalLabel(refusal.Code)}");
        var text = string.Join("\n", lines.Concat(refusals));
        if (text.Length > 0)
            ImGui.SetTooltip(text);
    }

    private static void DrawLines(CourierSnapshot? run)
    {
        if (run is null || run.Lines.IsEmpty)
            return;
        var rows = Math.Min(run.Lines.Length, 6);
        var height = (rows + 1) * ImGui.GetFrameHeight();
        if (!ImGui.BeginTable("##RQCourierRunLines", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, height)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Moved", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Remaining", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGui.TableHeadersRow();
        foreach (var progress in run.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(progress.Name);
            ImGui.TableNextColumn();
            ImGui.TextColored(TransferPresentation.SendColor, progress.Moved.ToString("N0"));
            ImGui.TableNextColumn();
            if (progress.Remaining > 0)
                ImGui.TextUnformatted(progress.Remaining.ToString("N0"));
            else
                ImGui.TextDisabled("—");
        }
        ImGui.EndTable();
    }

    private static void DrawCustody(string id, IReadOnlyList<CustodyRow> custody)
    {
        if (custody.Count == 0)
            return;
        var height = (Math.Min(custody.Count, 6) + 1) * ImGui.GetFrameHeight();
        if (!ImGui.BeginTable(id, 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, height)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Holder", ImGuiTableColumnFlags.WidthStretch, .6f);
        ImGui.TableHeadersRow();
        foreach (var row in custody.OrderBy(row => row.Item, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Quality, StringComparer.Ordinal))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Item);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Quality);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Holder);
        }
        ImGui.EndTable();
    }

    // ---- Carrier side ----------------------------------------------------------------------

    private bool DrawCarrier()
    {
        if (courier.Carried is not { } view || (view.Active is null && view.Custody.IsEmpty && !view.Paused && !view.Unconfirmed))
            return false;
        var height = BarHeight;
        if (view.Active is not null)
        {
            if (ImGui.Button("Stop##RQCourierCarrierStop", new Vector2(ImGui.CalcTextSize("Stop").X + 40, height)))
                courier.StopCarrier();
            reviewRegistry.RegisterLastButton("quartermaster.courier.carrier.stop", "Stop carrying on this client", true,
                courier.StopCarrier, CourierSession.PhaseLabel(view.Active.Value.ToString()));
            ImGui.SameLine(0, 16);
        }
        ImGui.BeginGroup();
        var top = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(top + Math.Max(0, (height - (ImGui.GetTextLineHeight() * 2) - ImGui.GetStyle().ItemSpacing.Y) / 2));
        ImGui.TextUnformatted(view.Active is { } command ? $"Carrier: {CourierSession.PhaseLabel(command.ToString())}" : "Carrier");
        var units = view.Custody.Sum(shipment => shipment.Units);
        var state = view.Unconfirmed ? "Hand-off unconfirmed" : view.Paused ? "Paused" : null;
        if (state is not null)
            ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Amber), units > 0 ? $"{state} · holding {units:N0}" : state);
        else
            ImGui.TextDisabled(units > 0 ? $"Holding {units:N0}" : "Holding nothing");
        ImGui.EndGroup();
        if (!view.Custody.IsEmpty)
            DrawCustody(view);
        return true;
    }

    private static void DrawCustody(CarrierView view)
    {
        var rows = view.Custody
            .GroupBy(shipment => (shipment.Item, shipment.Hq))
            .Select(group => (group.First().Name, group.Key.Hq, Units: group.Sum(shipment => shipment.Units)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var height = (Math.Min(rows.Length, 6) + 1) * ImGui.GetFrameHeight();
        if (!ImGui.BeginTable("##RQCourierCustody", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, height)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Holding", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableHeadersRow();
        foreach (var (name, hq, units) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(hq ? $"{name} HQ" : name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(units.ToString("N0"));
        }
        ImGui.EndTable();
    }
}
