using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Windows;
using RQ.Courier;
using RQ.Domain;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The Courier plan table: one row per item with its sources, then its destinations, listed under it.
/// Each place may take what it should hold after the run; the moves follow from those numbers (see
/// <see cref="CourierDistribution"/>).
/// </summary>
internal sealed partial class TransferPlanWorkspace
{
    private const string DestinationPickerPopup = "courier-destination-picker";
    private const string SourcePickerPopup = "courier-source-picker";
    private float courierWhoWidth;
    private static readonly string ConfirmIcon = FontAwesomeIcon.Check.ToIconString();
    private static readonly string DismissIcon = FontAwesomeIcon.Times.ToIconString();
    private readonly DalamudExpandableRows courierExpansion = new();
    /// <summary>Lines drawn at least once: a line opens the first time it is shown, then keeps the user's choice.</summary>
    private readonly HashSet<Guid> courierSeen = [];
    private readonly Dictionary<Guid, string> courierKeys = [];
    /// <summary>The Target input being typed in and its text, so a half-typed number survives redraws.</summary>
    private string? afterEditKey;
    private string afterEditText = string.Empty;
    /// <summary>A source picker the agent bridge asked to open; it opens on the line's next draw.</summary>
    private Guid? sourcePickerOpenRequest;
    /// <summary>A destination picker the agent bridge asked to open; it opens on the line's next draw.</summary>
    private Guid? pickerOpenRequest;
    private OwnerScope? pickerCharacter;
    private (bool Chosen, ulong? Retainer) pickerContainer;
    private bool pickerClose;

    private DalamudTableProjection<TransferWorkbenchRow> CreateCourierTable() => new(
    [
        new(
            "Item",
            1.4f,
            row => row.Rule.ItemName,
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
            Draw: DrawCourierItemCell,
            Id: "item"),
        new(
            "Has",
            76,
            row => (row.Distribution?.Total ?? 0).ToString("N0"),
            row => row.Distribution?.Total ?? 0,
            Draw: row => RightAligned((row.Distribution?.Total ?? 0).ToString("N0"), null),
            Id: "has"),
        new(
            "Target",
            88,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort,
            Draw: _ => { },
            Id: "after"),
        new(
            "Moves",
            .55f,
            CourierOutcome,
            row => row.Distribution?.Moving ?? 0,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: DrawCourierMoves,
            Id: "moves"),
        new(
            "##line-menu",
            52,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize |
                   ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.NoHide,
            Draw: DrawLineMenu,
            Id: "line-menu"),
    ], DalamudTableSelection<TransferWorkbenchRow>.Multi(selection, row => row.Rule.Id));

    private string CourierKey(Guid ruleId)
    {
        if (!courierKeys.TryGetValue(ruleId, out var key))
            courierKeys[ruleId] = key = ruleId.ToString("N");
        return key;
    }

    private bool CourierExpanded(Guid ruleId) => !courierSeen.Contains(ruleId) || courierExpansion.IsExpanded(CourierKey(ruleId));

    /// <summary>
    /// Draws the item rows clipped to the viewport. An open row has its places under it, so the
    /// clipper runs over each stretch of closed rows and open rows draw directly with their places.
    /// </summary>
    private int DrawCourierRows(IReadOnlyList<TransferWorkbenchRow> rows)
    {
        var table = courierWorkbenchTable;
        var rendered = 0;
        var start = 0;
        for (var index = 0; index <= rows.Count; index++)
        {
            if (index < rows.Count && !CourierExpanded(rows[index].Rule.Id))
                continue;
            if (index > start)
            {
                var offset = start;
                rendered += table.DrawClippedRows(new RowRange<TransferWorkbenchRow>(rows, offset, index - offset),
                    (_, local) => DrawRow(rows, offset + local));
            }
            if (index < rows.Count)
            {
                var row = rows[index];
                DrawRow(rows, index);
                if (CourierExpanded(row.Rule.Id))
                    DrawCourierPlaces(row);
                rendered++;
            }
            start = index + 1;
        }
        return rendered;
    }

    private void DrawCourierItemCell(TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        var key = CourierKey(rule.Id);
        if (courierSeen.Add(rule.Id))
            courierExpansion.SetExpanded(key, true);
        var cell = ImGui.GetCursorPos();
        var style = ImGui.GetStyle();
        var open = courierExpansion.DrawToggle(key);
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.line.expand.{rule.Id:N}",
            $"{(open ? "Collapse" : "Expand")} the places of {rule.ItemName}",
            true,
            () => courierExpansion.SetExpanded(key, !courierExpansion.IsExpanded(key)),
            open ? "Expanded" : "Collapsed");
        ImGui.SetCursorPos(cell);
        // The selection target goes after the expander, which keeps its clicks.
        if (drawingRows && drawingRowKeys is { } keys && (uint)drawingRowIndex < (uint)keys.Length && keys[drawingRowIndex] == rule.Id)
            DalamudTableSelectionRenderer.DrawRow(selection, keys, drawingRowIndex, $"##courier-select:{key}",
                new Vector2(0, ImGui.GetFrameHeight()));
        ImGui.SetCursorPos(new Vector2(cell.X + CourierIndent(), cell.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(rule.ItemName);
        if (rule.Quality != ItemQualityPolicy.Any)
            Tag(TransferPresentation.QualityLabel(rule.Quality));
        if (rule.Enabled && row.Distribution is { Moving: > 0 } distribution)
            Tag(CourierSession.RouteSummary(row.Runtime, distribution));
        if (!string.IsNullOrWhiteSpace(rule.Notes))
        {
            Tag("note");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rule.Notes);
        }
        if (lineErrors.TryGetValue(rule.Id, out var error))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ErrorColor, error);
            ImGui.PopTextWrapPos();
        }

        static void Tag(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(text);
        }
    }

    /// <summary>Where a line's name starts, past its expander; its places line up under it.</summary>
    private static float CourierIndent()
    {
        var style = ImGui.GetStyle();
        return ImGui.CalcTextSize(">").X + (style.FramePadding.X * 2f) + style.ItemInnerSpacing.X;
    }

    private static string CourierOutcome(TransferWorkbenchRow row) =>
        !row.Rule.Enabled ? "Paused"
        : row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code) ? CourierSession.RefusalLabel(refusal.Code)
        : row.Distribution is { Problem: not CourierProblem.None } ? CourierSession.ProblemLabel(row.Distribution)
        : row.Distribution is { Moving: > 0 and var moving } ? moving.ToString("N0")
        : "In place";

    private void DrawCourierMoves(TransferWorkbenchRow row)
    {
        if (!row.Rule.Enabled)
            RightAligned("Paused", QuartermasterPalette.Dim);
        else if (row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code))
        {
            RightAligned(CourierSession.RefusalLabel(refusal.Code), ErrorColor);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(courier.RefusalDetail(row.Runtime, refusal));
        }
        else if (row.Distribution is { Problem: not CourierProblem.None } problem)
            RightAligned(CourierSession.ProblemLabel(problem), AmberColor);
        else if (row.Distribution is { Moving: > 0 and var moving })
            RightAligned(moving.ToString("N0"), null);
        else
            RightAligned("In place", QuartermasterPalette.Green);
    }

    private static void RightAligned(string text, Vector4? color)
    {
        ImGui.AlignTextToFramePadding();
        var offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        if (color is { } value)
            ImGui.TextColored(value, text);
        else
            ImGui.TextUnformatted(text);
    }

    /// <summary>
    /// A line's sources then its destinations as rows of the plan table, each list followed by the row
    /// that adds to it. A line without a destination offers one suggested destination above that row.
    /// </summary>
    private void DrawCourierPlaces(TransferWorkbenchRow row)
    {
        if (row.Distribution is not { } allocation)
            return;
        var rule = row.Rule;
        var style = ImGui.GetStyle();
        var indent = CourierIndent();
        var roleWidth = Math.Max(ImGui.CalcTextSize("From").X, ImGui.CalcTextSize("To").X) + style.ItemSpacing.X * 2f;
        var whoWidth = 0f;
        foreach (var spot in allocation.Places)
            whoWidth = Math.Max(whoWidth, ImGui.CalcTextSize(CourierSession.CharacterLabel(spot.Character)).X);
        if (allocation.Suggestion is { } offered)
            whoWidth = Math.Max(whoWidth, ImGui.CalcTextSize(CourierSession.CharacterLabel(offered.Character)).X);
        // The widest name seen so far, so containers line up across items rather than per item.
        courierWhoWidth = whoWidth = Math.Max(courierWhoWidth, whoWidth);
        var layout = new PlaceLayout(indent, roleWidth, whoWidth);
        var sources = allocation.SourceCount;
        ImGui.PushID(CourierKey(rule.Id));
        for (var index = 0; index < sources; index++)
            DrawCourierPlace(row, allocation, allocation.Places[index], index, index == 0 ? "From" : null, layout);
        DrawAddPlace(row, allocation, PlaceRole.Source, sources == 0 ? "From" : null, layout);
        for (var index = sources; index < allocation.Places.Length; index++)
            DrawCourierPlace(row, allocation, allocation.Places[index], index, index == sources ? "To" : null, layout);
        if (allocation.Suggestion is { } suggestion)
            DrawCourierPlace(row, allocation, suggestion, allocation.Places.Length, "To", layout, suggested: true);
        DrawAddPlace(row, allocation, PlaceRole.Destination, allocation.Places.Length == sources && allocation.Suggestion is null ? "To" : null, layout);
        ImGui.PopID();
    }

    /// <summary>Where a place row's role, character and container start, so every row of a line lines up.</summary>
    private readonly record struct PlaceLayout(float Indent, float RoleWidth, float WhoWidth);

    /// <summary>Starts a place row and draws its role, if any, in the item column; returns where the role cell ends.</summary>
    private float BeginPlaceRow(TransferWorkbenchRow row, string? role, PlaceLayout layout)
    {
        ImGui.TableNextRow();
        if (!row.Rule.Enabled)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(PausedRowColor));
        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorPosX() + layout.Indent;
        ImGui.SetCursorPosX(start);
        ImGui.AlignTextToFramePadding();
        if (role is not null)
        {
            ImGui.TextColored(QuartermasterPalette.Dim, role);
            ImGui.SameLine(start + layout.RoleWidth);
        }
        else
            ImGui.SetCursorPosX(start + layout.RoleWidth);
        return start + layout.RoleWidth;
    }

    /// <summary>
    /// One listed place: character, container, what it has, its target and its move. A suggested
    /// destination is drawn dimmed with its target locked and offers to be confirmed or dismissed.
    /// </summary>
    private void DrawCourierPlace(TransferWorkbenchRow row, CourierAllocation allocation, CourierSpot spot, int index, string? role,
        PlaceLayout layout, bool suggested = false)
    {
        var runtime = row.Runtime;
        var style = ImGui.GetStyle();
        ImGui.PushID(index);
        var start = BeginPlaceRow(row, role, layout);
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        ImGui.TextColored(suggested ? QuartermasterPalette.Dim : spot.Character.Matches(runtime.Owner) ? QuartermasterPalette.Info : QuartermasterPalette.Dim,
            CourierSession.CharacterLabel(spot.Character));
        ImGui.SameLine(start + layout.WhoWidth + style.ItemSpacing.X * 2f);
        if (suggested)
            ImGui.TextDisabled(CourierSession.ContainerLabel(runtime, spot.Retainer));
        else
            ImGui.TextUnformatted(CourierSession.ContainerLabel(runtime, spot.Retainer));

        ImGui.TableSetColumnIndex(1);
        RightAligned(spot.Has?.ToString("N0") ?? "—", spot.Has is null || suggested ? QuartermasterPalette.Dim : null);
        ImGui.TableSetColumnIndex(2);
        if (suggested)
        {
            var locked = string.Empty;
            ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##target", spot.End.ToString("N0"), ref locked, 16, ImGuiInputTextFlags.ReadOnly);
            ImGui.EndDisabled();
        }
        else
            DrawTargetInput(row, allocation, spot);
        ImGui.TableSetColumnIndex(3);
        var delta = spot.Delta;
        RightAligned(delta < 0 ? $"−{-delta:N0}" : delta > 0 ? $"+{delta:N0}" : "—",
            suggested || delta == 0 ? QuartermasterPalette.Dim : delta < 0 ? QuartermasterPalette.Amber : QuartermasterPalette.Green);
        ImGui.TableSetColumnIndex(4);
        if (suggested)
            DrawSuggestionChoice(row, spot);
        else
            DrawRemovePlace(row, spot, rowTop, rowTop + ImGui.GetFrameHeight() + (style.CellPadding.Y * 2f));
        ImGui.PopID();
    }

    private static string PlaceKey(Guid ruleId, CourierSpot spot) =>
        $"{ruleId:N}.{spot.Character.LocalContentId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.{spot.Retainer?.ToString(CultureInfo.InvariantCulture) ?? "bags"}";

    private static string PlaceLabel(QuartermasterRuntimeSnapshot runtime, CourierSpot spot) =>
        $"{CourierSession.CharacterLabel(spot.Character)} · {CourierSession.ContainerLabel(runtime, spot.Retainer)}";

    /// <summary>The line a change starts from: the stored one, or a line in the older shape as it was solved.</summary>
    private static CourierLine EditableLine(TransferWorkbenchRow row, TargetPlanItem draftRule) =>
        draftRule.Courier is { HasLegacyPlaces: false } line ? line : row.Distribution?.Line ?? draftRule.Courier ?? new CourierLine();

    private bool MutateCourierLine(TransferWorkbenchRow row, Func<CourierLine, CourierLine> change) =>
        MutateRules(row.Owner, row.PlanId, [row.Rule.Id], draftRule => draftRule.Courier = change(EditableLine(row, draftRule)));

    /// <summary>
    /// What a place should hold after the run: a whole number, up to what it has for a source and up to
    /// the line's total for a destination, or empty to leave it to the line's other places. Empty shows
    /// the place's computed end state.
    /// </summary>
    private void DrawTargetInput(TransferWorkbenchRow row, CourierAllocation allocation, CourierSpot spot)
    {
        var rule = row.Rule;
        var key = PlaceKey(rule.Id, spot);
        var limit = !spot.Destination && spot.Has is { } has ? has : allocation.Total;
        var text = afterEditKey == key ? afterEditText : spot.Typed?.ToString("N0") ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##target", spot.End.ToString("N0"), ref text, 16,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemActive())
        {
            afterEditKey = key;
            afterEditText = text;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Set(Parse(text));
        if (ImGui.IsItemDeactivated() && afterEditKey == key)
            afterEditKey = null;

        reviewRegistry.RegisterLastAction(
            $"quartermaster.courier.place.target.{key}",
            $"Set how many {rule.ItemName} {PlaceLabel(row.Runtime, spot)} holds after the run; omit quantity to clear",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            spot.Typed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            new AgentBridgeActionArgumentSchema([new("quantity", AgentBridgeActionArgumentKind.Integer, Required: false, Minimum: 0)]),
            arguments =>
            {
                if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } value || !value.TryGetProperty("quantity", out var quantity))
                    return Set(null) ? AgentBridgeUiActionResult.Ok("Cleared.") : AgentBridgeUiActionResult.Fail("Not changed.");
                if (!quantity.TryGetInt64(out var requested) || requested < 0)
                    return AgentBridgeUiActionResult.Fail("quantity must be a whole number of at least 0.");
                var clamped = (int)Math.Min(requested, limit);
                return Set(clamped) ? AgentBridgeUiActionResult.Ok($"Set to {clamped:N0}.") : AgentBridgeUiActionResult.Fail("Not changed.");
            });

        int? Parse(string value)
        {
            var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0)
                return null;
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? (int)Math.Min(number, limit)
                : limit;
        }

        bool Set(int? target) =>
            target == spot.Typed ||
            MutateCourierLine(row, line => line.WithTarget(spot.Character, spot.Retainer, target));
    }

    private void DrawRemovePlace(TransferWorkbenchRow row, CourierSpot spot, float rowTop, float rowBottom)
    {
        var rule = row.Rule;
        void Remove() => MutateCourierLine(row, line => line.Without(spot.Character, spot.Retainer));
        if (DalamudRowRemoveButton.Draw("remove-place", DalamudRowRemoveButton.IsRowHovered(rowTop, rowBottom)))
            Remove();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.place.remove.{PlaceKey(rule.Id, spot)}",
            $"Remove {PlaceLabel(row.Runtime, spot)} as a {(spot.Destination ? "destination" : "source")} of {rule.ItemName}",
            true,
            Remove,
            "Available");
    }

    /// <summary>Confirms the suggested destination as a destination of the line, or dismisses it for good.</summary>
    private void DrawSuggestionChoice(TransferWorkbenchRow row, CourierSpot spot)
    {
        var rule = row.Rule;
        void Confirm() => MutateCourierLine(row, line => line.WithDestination(spot.Character, spot.Retainer));
        void Dismiss() => MutateCourierLine(row, line => line with { SuggestionDismissed = true });
        ImGui.PushFont(UiBuilder.IconFont);
        var confirmed = ImGui.SmallButton($"{ConfirmIcon}##confirm-suggestion");
        ImGui.PopFont();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.suggestion.confirm.{rule.Id:N}",
            $"Add {PlaceLabel(row.Runtime, spot)} as a destination of {rule.ItemName}",
            true,
            Confirm,
            PlaceLabel(row.Runtime, spot));
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.PushFont(UiBuilder.IconFont);
        var dismissed = ImGui.SmallButton($"{DismissIcon}##dismiss-suggestion");
        ImGui.PopFont();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.suggestion.dismiss.{rule.Id:N}",
            $"Dismiss {PlaceLabel(row.Runtime, spot)} as the suggested destination of {rule.ItemName}",
            true,
            Dismiss,
            PlaceLabel(row.Runtime, spot));
        if (confirmed)
            Confirm();
        else if (dismissed)
            Dismiss();
    }

    /// <summary>Which list of a line a picker adds to.</summary>
    private enum PlaceRole
    {
        Source,
        Destination,
    }

    /// <summary>
    /// Containers a line can add as a source or destination, by character: none already listed on it.
    /// Sources put the characters and containers holding the item first, most units first.
    /// </summary>
    private (OwnerScope Character, CourierContainer[] Containers)[] PlaceChoices(TransferWorkbenchRow row, CourierAllocation allocation, PlaceRole role)
    {
        var choices = courier.DestinationCharacters(row.Runtime)
            .Select(character => (Character: character, Containers: courier.Containers(row.Runtime, character, row.Rule.ItemId, row.Rule.Quality)
                .Where(container => !allocation.Places.Any(spot => spot.Retainer == container.Retainer && spot.Character.Matches(character)))
                .ToArray()))
            .Where(choice => choice.Containers.Length > 0);
        if (role == PlaceRole.Source)
            choices = choices
                .Select(choice => (choice.Character, Containers: choice.Containers.OrderByDescending(container => container.Has ?? 0).ToArray()))
                .OrderByDescending(choice => choice.Containers.Sum(container => (long)(container.Has ?? 0)));
        return choices.ToArray();
    }

    /// <summary>The row that adds a source or a destination, opening the place picker for that role.</summary>
    private void DrawAddPlace(TransferWorkbenchRow row, CourierAllocation allocation, PlaceRole role, string? roleLabel, PlaceLayout layout)
    {
        var choices = PlaceChoices(row, allocation, role);
        if (choices.Length == 0)
            return;
        var rule = row.Rule;
        var source = role == PlaceRole.Source;
        BeginPlaceRow(row, roleLabel, layout);
        var label = source ? "+ Add source…" : "+ Add destination…";
        ImGui.PushStyleColor(ImGuiCol.Text, QuartermasterPalette.Dim);
        var clicked = ImGui.Selectable($"{label}##add-{(source ? "source" : "destination")}", false, ImGuiSelectableFlags.None,
            new Vector2(ImGui.CalcTextSize(label).X, 0));
        ImGui.PopStyleColor();
        var ruleId = rule.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.{(source ? "source" : "destination")}.add.{ruleId:N}",
            $"Choose a {(source ? "source" : "destination")} for {rule.ItemName}",
            true,
            () =>
            {
                if (source)
                    sourcePickerOpenRequest = ruleId;
                else
                    pickerOpenRequest = ruleId;
            },
            "Available");
        var requested = source ? sourcePickerOpenRequest == ruleId : pickerOpenRequest == ruleId;
        if (clicked || requested)
        {
            if (source)
                sourcePickerOpenRequest = null;
            else
                pickerOpenRequest = null;
            pickerCharacter = null;
            pickerContainer = default;
            pickerClose = false;
            ImGui.OpenPopup(source ? SourcePickerPopup : DestinationPickerPopup);
        }
        DrawPlacePicker(row, role, choices);
    }

    /// <summary>
    /// Picks a source or destination container: a character, then one of its containers with its free
    /// space and units of the item, beside everything the highlighted container holds.
    /// </summary>
    private void DrawPlacePicker(TransferWorkbenchRow row, PlaceRole role, (OwnerScope Character, CourierContainer[] Containers)[] choices)
    {
        var source = role == PlaceRole.Source;
        if (!DalamudOwnedPopup.BeginOpaquePopup(source ? SourcePickerPopup : DestinationPickerPopup))
            return;
        // Destination rows keep their original IDs; source rows mirror them under the source prefix.
        var ids = source ? "quartermaster.courier.source.picker" : "quartermaster.courier.picker";
        var roleName = source ? "source" : "destination";
        if (pickerClose)
        {
            pickerClose = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        var rule = row.Rule;
        var scale = ImGuiHelpers.GlobalScale;
        var current = choices.FirstOrDefault(choice => pickerCharacter is { } chosen && choice.Character.Matches(chosen));
        if (current.Character is null)
        {
            current = choices[0];
            pickerCharacter = current.Character;
            pickerContainer = default;
        }
        var highlighted = (pickerContainer.Chosen ? current.Containers.FirstOrDefault(container => container.Retainer == pickerContainer.Retainer) : null)
                          ?? current.Containers[0];
        void Choose(OwnerScope character)
        {
            pickerCharacter = character;
            pickerContainer = default;
        }
        void Add(OwnerScope character, ulong? retainer)
        {
            MutateCourierLine(row, line => source ? line.WithSource(character, retainer) : line.WithDestination(character, retainer));
            pickerClose = true;
        }

        var height = 260f * scale;
        if (ImGui.BeginChild("##picker-containers", new Vector2(330f * scale, height)))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowBgAlpha(1f);
            if (ImGui.BeginCombo("##picker-character", CourierSession.CharacterLabel(current.Character)))
            {
                // The picker re-raises itself above its owner every frame, which would bury this list under it.
                ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
                foreach (var choice in choices)
                {
                    var character = choice.Character;
                    var picked = character.Matches(current.Character);
                    if (ImGui.Selectable($"{CourierSession.CharacterLabel(character)}##picker:{character.LocalContentId}", picked))
                        Choose(character);
                    reviewRegistry.RegisterLastSelectable($"{ids}.character.{character.LocalContentId}",
                        $"Show the containers of {character.CharacterName}", true, picked, () => Choose(character), picked ? "Selected" : "Available");
                }
                ImGui.EndCombo();
            }
            else
            {
                // A closed selector still offers its characters to the agent bridge, on the selector's own row.
                foreach (var choice in choices)
                {
                    var character = choice.Character;
                    var picked = character.Matches(current.Character);
                    reviewRegistry.RegisterLastSelectable($"{ids}.character.{character.LocalContentId}",
                        $"Show the containers of {character.CharacterName}", true, picked, () => Choose(character), picked ? "Selected" : "Available");
                }
            }
            if (ImGui.BeginTable("##picker-container-table", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Container", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Free slots", ImGuiTableColumnFlags.WidthFixed, 104f * scale);
                ImGui.TableSetupColumn("Has", ImGuiTableColumnFlags.WidthFixed, 64f * scale);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                foreach (var container in current.Containers)
                {
                    var character = current.Character;
                    var retainer = container.Retainer;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var isHighlighted = ReferenceEquals(container, highlighted);
                    if (ImGui.Selectable($"{container.Name}##picker-container:{retainer?.ToString(CultureInfo.InvariantCulture) ?? "bags"}", isHighlighted,
                            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        pickerContainer = (true, retainer);
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            Add(character, retainer);
                    }
                    reviewRegistry.RegisterLastSelectable(
                        $"{ids}.container.{character.LocalContentId}.{retainer?.ToString(CultureInfo.InvariantCulture) ?? "bags"}",
                        $"Highlight {character.CharacterName} · {container.Name}",
                        true,
                        isHighlighted,
                        () => pickerContainer = (true, retainer),
                        container.Free is { } free ? $"{free} free" : "Not observed");
                    ImGui.TableNextColumn();
                    FreeSlotsBar.Draw(container.Capacity, container.Free);
                    ImGui.TableNextColumn();
                    RightAlignedLine(container.Has is > 0 and var has ? has.ToString("N0") : "—", container.Has is > 0 ? null : QuartermasterPalette.Dim);
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##picker-contents", new Vector2(250f * scale, height)))
        {
            var title = highlighted.Stacks is { } stacks
                ? $"{highlighted.Name} · {stacks:N0} stack{(stacks == 1 ? string.Empty : "s")}"
                : highlighted.Name;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(title);
            const string addLabel = "Add##picker-add";
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - Franthropy.Dalamud.UI.Layout.DalamudUiLayout.ButtonWidth("Add")));
            var character = current.Character;
            var retainer = highlighted.Retainer;
            if (ImGui.Button(addLabel))
                Add(character, retainer);
            reviewRegistry.RegisterLastButton($"{ids}.add",
                $"Add {character.CharacterName} · {highlighted.Name} as a {roleName} of {rule.ItemName}", true,
                () => Add(character, retainer), $"{character.CharacterName} · {highlighted.Name}");
            ImGui.Separator();
            if (ImGui.BeginTable("##picker-content-table", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 64f * scale);
                foreach (var (name, quantity) in highlighted.Contents)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(name);
                    ImGui.TableNextColumn();
                    RightAlignedLine(quantity.ToString("N0"), null);
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    /// <summary>Right-aligned text in a plain (non-frame) row, as in the picker's tables.</summary>
    private static void RightAlignedLine(string text, Vector4? color)
    {
        var offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        if (color is { } value)
            ImGui.TextColored(value, text);
        else
            ImGui.TextUnformatted(text);
    }
}

/// <summary>A container's free space at a glance: a small fill bar, then "N free"; a dash when never observed.</summary>
internal static class FreeSlotsBar
{
    private static readonly Vector4 Red = new(1f, .45f, .4f, 1f);
    private static readonly Vector4 FullChip = new(.16f, .03f, .03f, 1f);

    /// <summary>Under this many free slots a container reads as filling up (amber).</summary>
    internal const int Tight = 75;

    /// <summary>Under this many free slots a container reads as nearly full (red).</summary>
    internal const int Low = 20;

    internal static Vector4 Color(int free) =>
        free < Low ? Red : free < Tight ? QuartermasterPalette.Amber : QuartermasterPalette.Green;

    internal static string Text(int free) => $"{free:N0} free";

    /// <summary>
    /// Paints "N free" with its top-left at <paramref name="min"/> without taking layout space: amber when
    /// filling up, red when nearly full, red on a dark chip when full, else <paramref name="plenty"/>.
    /// </summary>
    internal static void PaintText(Vector2 min, int free, Vector4? plenty = null)
    {
        var text = Text(free);
        var draw = ImGui.GetWindowDrawList();
        if (free <= 0)
        {
            var pad = new Vector2(3f, 0f) * ImGuiHelpers.GlobalScale;
            draw.AddRectFilled(min - pad, min + ImGui.CalcTextSize(text) + pad, ImGui.GetColorU32(FullChip), 2f * ImGuiHelpers.GlobalScale);
        }
        var color = free < Tight ? Color(free) : plenty ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        draw.AddText(min, ImGui.GetColorU32(color), text);
    }

    internal static void Draw(int capacity, int? free, bool withText = true)
    {
        if (free is not { } left)
        {
            ImGui.TextDisabled("—");
            return;
        }
        var size = new Vector2(40f, 6f) * ImGuiHelpers.GlobalScale;
        var line = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        Paint(new Vector2(origin.X, origin.Y + ((line - size.Y) / 2f)), size, capacity, left);
        ImGui.Dummy(new Vector2(size.X, line));
        if (!withText)
            return;
        ImGui.SameLine();
        var start = ImGui.GetCursorScreenPos();
        PaintText(start, left);
        ImGui.Dummy(ImGui.CalcTextSize(Text(left)));
    }

    /// <summary>Paints the bar at <paramref name="min"/> without taking layout space: the used share filled.</summary>
    internal static void Paint(Vector2 min, Vector2 size, int capacity, int free)
    {
        var used = capacity <= 0 ? 1f : Math.Clamp((capacity - free) / (float)capacity, 0f, 1f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(ImGuiCol.FrameBg));
        draw.AddRectFilled(min, new Vector2(min.X + (size.X * used), min.Y + size.Y), ImGui.GetColorU32(Color(free)));
    }
}
