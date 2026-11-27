using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
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
/// The Courier plan table: one row per item with the places taking part listed under it. Each place
/// may take bounds on what it holds after the run, or none to act as a pool; the moves follow from
/// those bounds (see <see cref="CourierDistribution"/>).
/// </summary>
internal sealed partial class TransferPlanWorkspace
{
    private const string PlacePickerPopup = "courier-place-picker";
    private float courierWhoWidth;
    private static readonly string ConfirmIcon = FontAwesomeIcon.Check.ToIconString();
    private static readonly string DismissIcon = FontAwesomeIcon.Times.ToIconString();
    private readonly DalamudExpandableRows courierExpansion = new();
    /// <summary>Lines drawn at least once: a line opens the first time it is shown, then keeps the user's choice.</summary>
    private readonly HashSet<Guid> courierSeen = [];
    private readonly Dictionary<Guid, string> courierKeys = [];
    /// <summary>The Target input being typed in and its text, so a half-typed value survives redraws.</summary>
    private string? afterEditKey;
    private string afterEditText = string.Empty;
    /// <summary>Target text that did not read as bounds, by place: shown until fixed while the stored bounds stay.</summary>
    private readonly Dictionary<string, string> rejectedTargets = [];
    /// <summary>A place picker the agent bridge asked to open; it opens on the line's next draw.</summary>
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

    private bool CourierExpanded(Guid ruleId) => courierExpansion.IsExpanded(CourierKey(ruleId));

    /// <summary>
    /// The first time a line is shown it opens only when it needs the user (a shortfall, surplus with
    /// nowhere to go, nothing to move between, or its own refusal); quiet lines start closed. A line
    /// opened before it was first shown (just added or sent) stays open.
    /// </summary>
    private void EnsureCourierSeen(TransferWorkbenchRow row)
    {
        if (!courierSeen.Add(row.Rule.Id))
            return;
        var key = CourierKey(row.Rule.Id);
        if (!courierExpansion.IsExpanded(key))
            courierExpansion.SetExpanded(key, CourierNeedsUser(row));
    }

    private static bool CourierNeedsUser(TransferWorkbenchRow row) =>
        row.Distribution is { NeedsAttention: true } ||
        row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code);

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
            if (index < rows.Count)
                EnsureCourierSeen(rows[index]);
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
        EnsureCourierSeen(row);
        var cell = ImGui.GetCursorPos();
        var style = ImGui.GetStyle();
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var open = courierExpansion.DrawToggle(key);
        var expanderHovered = ImGui.IsItemHovered();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.line.expand.{rule.Id:N}",
            $"{(open ? "Collapse" : "Expand")} the places of {rule.ItemName}",
            true,
            () => courierExpansion.SetExpanded(key, !courierExpansion.IsExpanded(key)),
            open ? "Expanded" : "Collapsed");
        ImGui.SetCursorPos(cell);
        // The selection target goes after the expander, which keeps its clicks.
        var targetHovered = false;
        if (drawingRows && drawingRowKeys is { } keys && (uint)drawingRowIndex < (uint)keys.Length && keys[drawingRowIndex] == rule.Id)
        {
            DalamudTableSelectionRenderer.DrawRow(selection, keys, drawingRowIndex, $"##courier-select:{key}",
                new Vector2(0, ImGui.GetFrameHeight()));
            targetHovered = ImGui.IsItemHovered();
        }
        // A double-click anywhere on the item row opens or closes it, except on its own buttons or the trailing menu column.
        if (!expanderHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered() &&
            (targetHovered || !ImGui.IsAnyItemHovered()))
        {
            var mouse = ImGui.GetMousePos();
            var menuLeft = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X - 56f;
            if (mouse.X < menuLeft && mouse.Y >= rowTop && mouse.Y < rowTop + ImGui.GetFrameHeight() + (style.CellPadding.Y * 2f))
                courierExpansion.Toggle(key);
        }
        ImGui.SetCursorPos(new Vector2(cell.X + CourierIndent(), cell.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(rule.ItemName);
        if (rule.Quality != ItemQualityPolicy.Any)
            Tag(TransferPresentation.QualityLabel(rule.Quality));
        if (!string.IsNullOrWhiteSpace(rule.Notes))
        {
            Tag("note");
            if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
                ImGui.SetTooltip(rule.Notes);
        }
        if (row.Distribution is { } distribution && (rule.Enabled && distribution.Moving > 0 || !open))
        {
            // Moving lines name their route; a closed line still says where it stands.
            var summary = CourierLineSummary(row, distribution);
            if (summary.Length > 0)
            {
                ImGui.SameLine();
                var shown = ElideCached(rule.Id, summary, ImGui.GetContentRegionAvail().X);
                ImGui.TextDisabled(shown);
                if (shown.Length < summary.Length && ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
                    ImGui.SetTooltip(summary);
            }
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
        : row.Distribution is { } distribution ? CourierSession.Outcome(distribution).Text
        : "In place";

    /// <summary>
    /// What a line's moves come to, as both views word it: paused, its own refusal, the solved outcome,
    /// or in place. A refusal's detail is built only when <paramref name="withDetail"/>, as on hover.
    /// </summary>
    private (string Text, Vector4? Color, string? Detail) CourierVerdict(TransferWorkbenchRow row, bool withDetail)
    {
        if (!row.Rule.Enabled)
            return ("Paused", QuartermasterPalette.Dim, null);
        if (row.CourierRefusal is { } refusal && !CourierSession.IsPlanWide(refusal.Code))
            return (CourierSession.RefusalLabel(refusal.Code), ErrorColor, withDetail ? courier.RefusalDetail(row.Runtime, refusal) : null);
        return row.Distribution is { } distribution
            ? CourierSession.Outcome(distribution)
            : ("In place", QuartermasterPalette.Green, null);
    }

    private void DrawCourierMoves(TransferWorkbenchRow row)
    {
        var (text, color, detail) = CourierVerdict(row, withDetail: false);
        RightAligned(text, color);
        if (ImGui.IsItemHovered() && (detail ?? CourierVerdict(row, withDetail: true).Detail) is { } hover)
            ImGui.SetTooltip(hover);
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
    /// A line's places as rows of the plan table in listed order, then the place it suggests (while it
    /// lists a single place), then the row that adds a place.
    /// </summary>
    private void DrawCourierPlaces(TransferWorkbenchRow row)
    {
        if (row.Distribution is not { } allocation)
            return;
        var rule = row.Rule;
        var whoWidth = 0f;
        foreach (var spot in allocation.Places)
            whoWidth = Math.Max(whoWidth, ImGui.CalcTextSize(CourierSession.CharacterLabel(spot.Character)).X);
        if (allocation.Suggestion is { } offered)
            whoWidth = Math.Max(whoWidth, ImGui.CalcTextSize(CourierSession.CharacterLabel(offered.Character)).X);
        // The widest name seen so far, so containers line up across items rather than per item.
        courierWhoWidth = whoWidth = Math.Max(courierWhoWidth, whoWidth);
        var layout = new PlaceLayout(CourierIndent(), whoWidth);
        ImGui.PushID(CourierKey(rule.Id));
        for (var index = 0; index < allocation.Places.Length; index++)
            DrawCourierPlace(row, allocation.Places[index], index, layout);
        if (allocation.Suggestion is { } suggestion)
            DrawCourierPlace(row, suggestion, allocation.Places.Length, layout, suggested: true);
        DrawAddPlace(row, allocation, layout);
        ImGui.PopID();
    }

    /// <summary>Where a place row's character and container start, so every row of a line lines up.</summary>
    private readonly record struct PlaceLayout(float Indent, float WhoWidth);

    /// <summary>Starts a place row in the item column; returns where its character starts.</summary>
    private float BeginPlaceRow(TransferWorkbenchRow row, PlaceLayout layout)
    {
        ImGui.TableNextRow();
        if (!row.Rule.Enabled)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(PausedRowColor));
        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorPosX() + layout.Indent;
        ImGui.SetCursorPosX(start);
        ImGui.AlignTextToFramePadding();
        return start;
    }

    /// <summary>
    /// One listed place: character, container, what it has, its bounds and its move. The suggested
    /// place is drawn dimmed with its bounds locked and offers to be confirmed or dismissed.
    /// </summary>
    private void DrawCourierPlace(TransferWorkbenchRow row, CourierSpot spot, int index, PlaceLayout layout, bool suggested = false)
    {
        var runtime = row.Runtime;
        var style = ImGui.GetStyle();
        ImGui.PushID(index);
        var start = BeginPlaceRow(row, layout);
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
            DrawTargetInput(row, spot);
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

    /// <summary>The line a change starts from: the stored one, or a line in an older shape as it was solved.</summary>
    private static CourierLine EditableLine(TransferWorkbenchRow row, TargetPlanItem draftRule) =>
        draftRule.Courier is { NeedsResolve: false } line ? line : row.Distribution?.Line ?? draftRule.Courier ?? new CourierLine();

    private bool MutateCourierLine(TransferWorkbenchRow row, Func<CourierLine, CourierLine> change) =>
        MutateRules(row.Owner, row.PlanId, [row.Rule.Id], draftRule => draftRule.Courier = change(EditableLine(row, draftRule)));

    /// <summary>
    /// The bounds a place keeps after the run, typed as text (<see cref="CourierBoundsText"/>): empty
    /// makes it a pool and shows its computed end state. Numbers are never capped by what the line holds,
    /// only to whole units up to <see cref="CourierPlace.Limit"/>. Text that does not read as bounds stays
    /// in the box, outlined, while the stored bounds are kept.
    /// </summary>
    private void DrawTargetInput(TransferWorkbenchRow row, CourierSpot spot)
    {
        var rule = row.Rule;
        var key = PlaceKey(rule.Id, spot);
        var stored = CourierBoundsText.Format(spot.Min, spot.Max);
        var rejected = rejectedTargets.GetValueOrDefault(key);
        var text = afterEditKey == key ? afterEditText : rejected ?? stored;
        ImGui.SetNextItemWidth(-1);
        if (rejected is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Border, QuartermasterPalette.Amber);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        }
        ImGui.InputTextWithHint("##target", spot.End.ToString("N0"), ref text, 32, ImGuiInputTextFlags.AutoSelectAll);
        if (rejected is not null)
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        if (ImGui.IsItemActive())
        {
            afterEditKey = key;
            afterEditText = text;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (CourierBoundsText.TryParse(text, out var min, out var max))
                Set(min, max);
            else
                rejectedTargets[key] = text;
        }
        if (ImGui.IsItemDeactivated() && afterEditKey == key)
            afterEditKey = null;
        if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
            ImGui.SetTooltip(rejected is not null
                ? $"Not a target: {rejected}\n{CourierBoundsText.Describe(spot.Min, spot.Max)} · {CourierBoundsText.Syntax}"
                : $"{CourierBoundsText.Describe(spot.Min, spot.Max)} · {CourierBoundsText.Syntax}");

        reviewRegistry.RegisterLastAction(
            $"quartermaster.courier.place.target.{key}",
            $"Set what {PlaceLabel(row.Runtime, spot)} keeps of {rule.ItemName} after the run: quantity for exactly that many, " +
            "min and/or max for a range; omit all to make it a pool",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            stored,
            new AgentBridgeActionArgumentSchema(
            [
                new("quantity", AgentBridgeActionArgumentKind.Integer, Required: false, Minimum: 0),
                new("min", AgentBridgeActionArgumentKind.Integer, Required: false, Minimum: 0),
                new("max", AgentBridgeActionArgumentKind.Integer, Required: false, Minimum: 0),
            ]),
            arguments =>
            {
                string? error = null;
                int? Argument(string name)
                {
                    if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } value || !value.TryGetProperty(name, out var element) ||
                        element.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return null;
                    if (!element.TryGetInt64(out var number) || number < 0)
                    {
                        error ??= $"{name} must be a whole number of at least 0.";
                        return null;
                    }
                    return (int)Math.Min(number, CourierPlace.Limit);
                }
                var quantity = Argument("quantity");
                var min = Argument("min");
                var max = Argument("max");
                if (error is not null)
                    return AgentBridgeUiActionResult.Fail(error);
                if (quantity is not null && (min is not null || max is not null))
                    return AgentBridgeUiActionResult.Fail("Give quantity, or min and max, not both.");
                var (low, high) = CourierPlace.Normalize(quantity ?? min, quantity ?? max);
                return Set(low, high)
                    ? AgentBridgeUiActionResult.Ok($"{CourierBoundsText.Describe(low, high)}.")
                    : AgentBridgeUiActionResult.Fail("Not changed.");
            });

        bool Set(int? min, int? max)
        {
            rejectedTargets.Remove(key);
            (min, max) = CourierPlace.Normalize(min, max);
            return min == spot.Min && max == spot.Max ||
                MutateCourierLine(row, line => line.WithBounds(spot.Character, spot.Retainer, min, max));
        }
    }

    private void DrawRemovePlace(TransferWorkbenchRow row, CourierSpot spot, float rowTop, float rowBottom)
    {
        var rule = row.Rule;
        void Remove() => MutateCourierLine(row, line => line.Without(spot.Character, spot.Retainer));
        if (DalamudRowRemoveButton.Draw("remove-place", DalamudRowRemoveButton.IsRowHovered(rowTop, rowBottom)))
            Remove();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.place.remove.{PlaceKey(rule.Id, spot)}",
            $"Remove {PlaceLabel(row.Runtime, spot)} from {rule.ItemName}",
            true,
            Remove,
            "Available");
    }

    /// <summary>Lists the suggested place on the line as a pool, or dismisses the suggestion for good.</summary>
    private void DrawSuggestionChoice(TransferWorkbenchRow row, CourierSpot spot)
    {
        var rule = row.Rule;
        const string prefix = "quartermaster.courier.suggestion";
        void Confirm() => MutateCourierLine(row, line => line.WithPlace(spot.Character, spot.Retainer));
        void Dismiss() => MutateCourierLine(row, line => line with { SuggestionDismissed = true });
        ImGui.PushFont(UiBuilder.IconFont);
        var confirmed = ImGui.SmallButton($"{ConfirmIcon}##confirm-suggestion");
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Add {PlaceLabel(row.Runtime, spot)}");
        reviewRegistry.RegisterLastButton(
            $"{prefix}.confirm.{rule.Id:N}",
            $"Add {PlaceLabel(row.Runtime, spot)} to {rule.ItemName} as a pool",
            true,
            Confirm,
            PlaceLabel(row.Runtime, spot));
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.PushFont(UiBuilder.IconFont);
        var dismissed = ImGui.SmallButton($"{DismissIcon}##dismiss-suggestion");
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Dismiss {PlaceLabel(row.Runtime, spot)}");
        reviewRegistry.RegisterLastButton(
            $"{prefix}.dismiss.{rule.Id:N}",
            $"Dismiss {PlaceLabel(row.Runtime, spot)} as the suggested place of {rule.ItemName}",
            true,
            Dismiss,
            PlaceLabel(row.Runtime, spot));
        if (confirmed)
            Confirm();
        else if (dismissed)
            Dismiss();
    }

    /// <summary>
    /// Containers a line can add, by character: none already listed on it. Characters and containers
    /// holding the item come first, most units first.
    /// </summary>
    private (OwnerScope Character, CourierContainer[] Containers)[] PlaceChoices(TransferWorkbenchRow row, CourierAllocation allocation) =>
        courier.DestinationCharacters(row.Runtime)
            .Select(character => (Character: character, Containers: courier.Containers(row.Runtime, character, row.Rule.ItemId, row.Rule.Quality)
                .Where(container => !allocation.Places.Any(spot => spot.Retainer == container.Retainer && spot.Character.Matches(character)))
                .OrderByDescending(container => container.Has ?? 0)
                .ToArray()))
            .Where(choice => choice.Containers.Length > 0)
            .OrderByDescending(choice => choice.Containers.Sum(container => (long)(container.Has ?? 0)))
            .ToArray();

    /// <summary>The row that adds a place, opening the place picker.</summary>
    private void DrawAddPlace(TransferWorkbenchRow row, CourierAllocation allocation, PlaceLayout layout)
    {
        var choices = PlaceChoices(row, allocation);
        if (choices.Length == 0)
            return;
        var rule = row.Rule;
        BeginPlaceRow(row, layout);
        const string label = "+ Add place…";
        ImGui.PushStyleColor(ImGuiCol.Text, QuartermasterPalette.Dim);
        var clicked = ImGui.Selectable($"{label}##add-place", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(label).X, 0));
        ImGui.PopStyleColor();
        var ruleId = rule.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.courier.place.add.{ruleId:N}",
            $"Choose a place for {rule.ItemName}",
            true,
            () => pickerOpenRequest = ruleId,
            "Available");
        if (clicked || pickerOpenRequest == ruleId)
        {
            pickerOpenRequest = null;
            pickerCharacter = null;
            pickerContainer = default;
            pickerClose = false;
            ImGui.OpenPopup(PlacePickerPopup);
        }
        DrawPlacePicker(row, choices);
    }

    /// <summary>The list's place picker: the chosen container joins the line as a pool.</summary>
    private void DrawPlacePicker(TransferWorkbenchRow row, (OwnerScope Character, CourierContainer[] Containers)[] choices) =>
        DrawContainerPicker(PlacePickerPopup, "quartermaster.courier.picker", choices, showHas: true,
            (character, retainer) => MutateCourierLine(row, line => line.WithPlace(character, retainer)),
            (character, name) => $"Add {character.CharacterName} · {name} to {row.Rule.ItemName}");

    /// <summary>
    /// Picks a container: a character, then one of its containers with its free space (and units of the
    /// item when <paramref name="showHas"/>), beside everything the highlighted container holds. Shared by
    /// the list's place picker and the matrix's column and row pickers. Closes itself when nothing is left
    /// to pick.
    /// </summary>
    private void DrawContainerPicker(
        string popup,
        string ids,
        (OwnerScope Character, CourierContainer[] Containers)[] choices,
        bool showHas,
        Action<OwnerScope, ulong?> add,
        Func<OwnerScope, string, string> addDescription)
    {
        if (!DalamudOwnedPopup.BeginOpaquePopup(popup))
            return;
        if (pickerClose || choices.Length == 0)
        {
            pickerClose = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
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
            add(character, retainer);
            pickerClose = true;
        }

        var height = 260f * scale;
        if (ImGui.BeginChild("##picker-containers", new Vector2(330f * scale, height)))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowBgAlpha(1f);
            if (ImGui.BeginCombo("##picker-character", CourierSession.CharacterLabel(current.Character)))
            {
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
            if (ImGui.BeginTable("##picker-container-table", showHas ? 3 : 2,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Container", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Free slots", ImGuiTableColumnFlags.WidthFixed, 104f * scale);
                if (showHas)
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
                    if (showHas)
                    {
                        ImGui.TableNextColumn();
                        RightAlignedLine(container.Has is > 0 and var has ? has.ToString("N0") : "—", container.Has is > 0 ? null : QuartermasterPalette.Dim);
                    }
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
            reviewRegistry.RegisterLastButton($"{ids}.add", addDescription(character, highlighted.Name), true,
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

/// <summary>
/// A courier place's bounds as the Target cell shows and reads them. Blank is a pool; <c>N</c> or
/// <c>=N</c> keeps exactly N (0 drains); <c>A-B</c>, <c>A–B</c> or <c>A..B</c> keeps a range, either
/// way round; <c>N+</c>, <c>&gt;=N</c>, <c>≥N</c>, <c>N-</c> or <c>N..</c> keeps at least N; <c>&lt;=N</c>,
/// <c>≤N</c>, <c>-N</c> or <c>..N</c> keeps at most N. Spaces and group separators are ignored and numbers
/// are clamped to [0, <see cref="CourierPlace.Limit"/>].
/// </summary>
internal static class CourierBoundsText
{
    internal const string Syntax = "blank: pool, N: exact, A-B: range, N+: min, <=N: max";

    private static readonly Regex Exact = new(@"^=?(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Range = new(@"^(\d+)(?:-|\.\.)(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Floor = new(@"^(?:>=(\d+)|(\d+)(?:\+|-|\.\.))$", RegexOptions.CultureInvariant);
    private static readonly Regex Ceiling = new(@"^(?:<=|-|\.\.)(\d+)$", RegexOptions.CultureInvariant);

    internal static string Format(int? min, int? max) => (min, max) switch
    {
        (null, null) => string.Empty,
        ({ } low, { } high) when low == high => Number(low),
        ({ } low, { } high) => $"{Number(low)}-{Number(high)}",
        ({ } low, null) => $"{Number(low)}+",
        (null, { } high) => $"<={Number(high)}",
    };

    internal static string Describe(int? min, int? max) => (min, max) switch
    {
        (null, null) => "Pool",
        (0, 0) => "Drains",
        ({ } low, { } high) when low == high => $"Maintains {Number(low)}",
        ({ } low, { } high) => $"Maintains {Number(low)}–{Number(high)}",
        ({ } low, null) => $"Keeps at least {Number(low)}",
        (null, { } high) => $"Keeps at most {Number(high)}",
    };

    /// <summary>Reads typed bounds; false when the text is not one of the accepted forms.</summary>
    internal static bool TryParse(string text, out int? min, out int? max)
    {
        min = max = null;
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not (',' or '_' or '\'')).ToArray())
            .Replace('–', '-').Replace('—', '-').Replace('−', '-')
            .Replace("≥", ">=", StringComparison.Ordinal).Replace("≤", "<=", StringComparison.Ordinal);
        if (compact.Length == 0)
            return true;
        if (Exact.Match(compact) is { Success: true } exact)
            min = max = Value(exact.Groups[1]);
        else if (Range.Match(compact) is { Success: true } range)
            (min, max) = CourierPlace.Normalize(Value(range.Groups[1]), Value(range.Groups[2]));
        else if (Floor.Match(compact) is { Success: true } floor)
            min = Value(floor.Groups[1].Success ? floor.Groups[1] : floor.Groups[2]);
        else if (Ceiling.Match(compact) is { Success: true } ceiling)
            max = Value(ceiling.Groups[1]);
        else
            return false;
        return true;
    }

    private static int Value(Group digits) =>
        long.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (int)Math.Min(number, CourierPlace.Limit)
            : CourierPlace.Limit;

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
