using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Lists the current character's saved Transfer Plans beside a detail pane for renaming,
/// opening, duplicating, and deleting them. Mirrors the Item Groups workspace layout.
/// </summary>
internal sealed class TransferPlanManager
{
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);
    private static readonly Vector4 ArmedColor = new(.78f, .2f, .2f, 1f);
    private static readonly Vector4 ArmedHoveredColor = new(.86f, .25f, .25f, 1f);
    private static readonly Vector4 ArmedActiveColor = new(.92f, .3f, .3f, 1f);
    private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(15);

    private sealed record DeletedPlan(
        OwnerScope Owner,
        StowagePlan Plan,
        int Index,
        IReadOnlyList<TargetPlanItem> Rules,
        IReadOnlyList<TransferPlanListingLink> Links,
        DateTime DeletedAtUtc);

    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly WorkbenchState workbench;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<QuartermasterRuntimeSnapshot, StowagePlan, TransferWorkbenchProjection> resolveProjection;
    private readonly Action<Guid> openPlan;

    private Guid? selectedPlanId;
    private string filter = string.Empty;
    private Guid? nameBufferPlanId;
    private string nameBuffer = string.Empty;
    private bool focusName;
    private bool nameEditing;
    private Guid? armedPlanId;
    private DateTime armedAtUtc;
    private DeletedPlan? lastDeleted;
    private string error = string.Empty;
    private int lastDrawFrame = -1;

    public TransferPlanManager(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        WorkbenchState workbench,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<QuartermasterRuntimeSnapshot, StowagePlan, TransferWorkbenchProjection> resolveProjection,
        Action<Guid> openPlan)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.workbench = workbench;
        this.reviewRegistry = reviewRegistry;
        this.resolveProjection = resolveProjection;
        this.openPlan = openPlan;
    }

    /// <summary>Selects a plan in the manager, as when arriving from the plan menu.</summary>
    public void Select(Guid? planId)
    {
        if (planId is not null)
            selectedPlanId = planId;
        armedPlanId = null;
        error = string.Empty;
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var frame = ImGui.GetFrameCount();
        if (lastDrawFrame != frame - 1)
        {
            // The manager was hidden since the last frame: drop transient delete/undo state.
            lastDeleted = null;
            armedPlanId = null;
        }
        lastDrawFrame = frame;

        var owner = runtime.Owner;
        var plans = Ordered(runtime.State.StowagePlans.Where(plan => !plan.IsEphemeral && plan.Owner.Matches(owner))).ToArray();
        var lineCounts = runtime.State.PlanItems
            .GroupBy(rule => rule.StowagePlanId)
            .ToDictionary(group => group.Key, group => group.Count());

        if (lastDeleted is { } deleted &&
            (!deleted.Owner.Matches(owner) || DateTime.UtcNow - deleted.DeletedAtUtc > UndoWindow))
            lastDeleted = null;
        if (armedPlanId is not null && (armedPlanId != selectedPlanId || DateTime.UtcNow - armedAtUtc > ArmWindow))
            armedPlanId = null;
        if (selectedPlanId is null || plans.All(plan => plan.Id != selectedPlanId))
        {
            selectedPlanId = plans.FirstOrDefault(plan => plan.Id == workbench.SelectedStowagePlanId)?.Id
                             ?? plans.FirstOrDefault()?.Id;
        }
        var selected = plans.FirstOrDefault(plan => plan.Id == selectedPlanId);

        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "RQTransferPlanManager",
                2,
                flags,
                new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y))))
            return;

        ImGui.TableSetupColumn("Plans", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQTransferPlanManagerList", Vector2.Zero, false))
            DrawList(owner, plans, lineCounts);
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQTransferPlanManagerDetail", Vector2.Zero, false) && selected is not null)
            DrawDetail(runtime, selected);
        ImGui.EndChild();
        ImGui.EndTable();
    }

    // ---- List -----------------------------------------------------------------------------

    private void DrawList(OwnerScope owner, IReadOnlyList<StowagePlan> plans, IReadOnlyDictionary<Guid, int> lineCounts)
    {
        var stable = owner.HasStableIdentity;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##transferplanfilter", "Filter plans", ref filter, 80);
        ImGui.Separator();

        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y + 2;
        if (ImGui.BeginChild("RQTransferPlanManagerRows", new Vector2(0, -footerHeight), false))
        {
            if (lastDeleted is { } deleted && stable)
                DrawUndoRow(deleted);

            var needle = filter.Trim();
            StowagePlanKind? section = null;
            foreach (var plan in plans)
            {
                if (needle.Length > 0 && !plan.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (section != plan.Kind)
                {
                    if (section is not null)
                        ImGui.Spacing();
                    section = plan.Kind;
                    ImGui.TextColored(PlanKindPresentation.Color(plan.Kind), PlanKindPresentation.Plural(plan.Kind));
                }
                DrawPlanRow(plan, lineCounts.GetValueOrDefault(plan.Id));
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (!stable)
            return;
        if (ImGui.Button("+ New transfer plan##transferplanmanager", new Vector2(-1, 0)))
            NewPlan(owner, StowagePlanKind.Transfer);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.new",
            "Create a new Transfer Plan in the plan manager",
            true,
            () => NewPlan(runtimeSnapshots.Current.Owner, StowagePlanKind.Transfer),
            "Available");
        if (ImGui.Button("+ New courier plan##transferplanmanager", new Vector2(-1, 0)))
            NewPlan(owner, StowagePlanKind.Courier);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.new-courier",
            "Create a new Courier Plan in the plan manager",
            true,
            () => NewPlan(runtimeSnapshots.Current.Owner, StowagePlanKind.Courier),
            "Available");
    }

    /// <summary>The manager's order: Transfer plans then Courier plans, each by name.</summary>
    private static IEnumerable<StowagePlan> Ordered(IEnumerable<StowagePlan> plans) =>
        plans
            .OrderBy(plan => plan.Kind)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id);

    private void DrawPlanRow(StowagePlan plan, int lineCount)
    {
        var selected = plan.Id == selectedPlanId;
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 1.8f;
        var nameWidth = ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().FramePadding.X * 2f);
        var display = Fit(plan.Name, nameWidth);
        if (ImGui.Selectable(
                $"{display}##transferplanmanager:{plan.Id}",
                selected,
                ImGuiSelectableFlags.AllowDoubleClick,
                new Vector2(0, rowHeight)))
        {
            SelectPlan(plan.Id);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Open(plan.Id);
        }
        if (display.Length != plan.Name.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(plan.Name);
        var planId = plan.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.transfer.plans.select.{plan.Id:N}",
            $"Select {PlanKindPresentation.Label(plan.Kind)} Plan {plan.Name} in the plan manager",
            true,
            () => SelectPlan(planId),
            selected ? "Selected" : $"{PlanKindPresentation.Label(plan.Kind)} · {lineCount:N0} lines");
        if (ImGui.IsItemVisible())
        {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(cursor.Y - ImGui.GetTextLineHeightWithSpacing());
            ImGui.Indent();
            ImGui.TextColored(PlanKindPresentation.Color(plan.Kind), PlanKindPresentation.Label(plan.Kind));
            ImGui.SameLine(0, 0);
            ImGui.TextDisabled($" · {lineCount:N0} line{(lineCount == 1 ? string.Empty : "s")}");
            ImGui.Unindent();
        }
    }

    private void DrawUndoRow(DeletedPlan deleted)
    {
        const string undoLabel = "Undo##transferplanmanager";
        var undoWidth = ImGui.CalcTextSize("Undo").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var labelWidth = ImGui.GetContentRegionAvail().X - undoWidth - ImGui.GetStyle().ItemSpacing.X;
        var full = $"Deleted \"{deleted.Plan.Name}\"";
        var text = Fit(full, labelWidth);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        if (text.Length != full.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(full);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - undoWidth));
        if (ImGui.SmallButton(undoLabel))
            Undo();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.undo",
            $"Restore the deleted Transfer Plan {deleted.Plan.Name}",
            true,
            Undo,
            "Available");
        ImGui.Separator();
    }

    // ---- Detail ---------------------------------------------------------------------------

    private void DrawDetail(QuartermasterRuntimeSnapshot runtime, StowagePlan plan)
    {
        var owner = runtime.Owner;
        var stable = owner.HasStableIdentity;
        var courier = plan.Kind == StowagePlanKind.Courier;
        PlanKindPresentation.Badge(plan.Kind);
        ImGui.SameLine();
        DrawName(owner, plan, stable);

        var rows = resolveProjection(runtime, plan).Rows;
        if (courier)
        {
            var destinations = rows
                .Where(row => row.Rule.Courier is not null)
                .GroupBy(row => (row.Rule.Courier!.Character.LocalContentId, row.Rule.Courier.Character.HomeWorldId))
                .Select(group => $"{CourierSession.CharacterLabel(group.First().Rule.Courier!.Character)} {group.Count():N0}")
                .ToArray();
            if (destinations.Length > 0)
                ImGui.TextDisabled($"To {string.Join(" · ", destinations)}");
        }
        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(ErrorColor, error);
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y + 2;
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable(
                courier ? "RQCourierPlanManagerLines" : "RQTransferPlanManagerLines",
                3,
                tableFlags,
                new Vector2(0, Math.Max(120, ImGui.GetContentRegionAvail().Y - footerHeight))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(courier ? "Send" : "Keep", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("999,999").X);
            ImGui.TableSetupColumn(courier ? "Route" : "Stowage", ImGuiTableColumnFlags.WidthStretch, courier ? 1.4f : .8f);
            ImGui.TableHeadersRow();
            foreach (var row in rows)
                DrawLine(runtime, row);
            ImGui.EndTable();
        }

        DrawActions(owner, plan, stable);
    }

    private void DrawName(OwnerScope owner, StowagePlan plan, bool stable)
    {
        if (!stable)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(plan.Name);
            return;
        }
        // Follow the saved name unless the field is being edited (e.g. a rename landed elsewhere).
        if (nameBufferPlanId != plan.Id || (!nameEditing && !focusName))
        {
            // Selection moved while a rename was in progress: keep the typed name.
            if (nameEditing && nameBufferPlanId is { } previousId && previousId != plan.Id)
                CommitRename(owner, previousId);
            nameEditing = false;
            nameBufferPlanId = plan.Id;
            nameBuffer = plan.Name;
        }
        ImGui.SetNextItemWidth(-1);
        if (focusName)
        {
            ImGui.SetKeyboardFocusHere();
            focusName = false;
        }
        var committed = ImGui.InputTextWithHint(
            "##transferplanmanagername",
            "Plan name",
            ref nameBuffer,
            80,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        nameEditing = ImGui.IsItemActive();
        var deactivated = ImGui.IsItemDeactivated();
        if (deactivated && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            nameBuffer = plan.Name;
            return;
        }
        if (committed || deactivated)
            CommitRename(owner, plan.Id);
    }

    private static void DrawLine(QuartermasterRuntimeSnapshot runtime, TransferWorkbenchRow row)
    {
        var rule = row.Rule;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var quality = QualityLabel(rule.Quality);
        var suffix = quality.Length == 0 ? string.Empty : $" {quality}";
        if (!rule.Enabled)
            suffix += " · paused";
        var full = rule.ItemName + suffix;
        var display = Fit(full, ImGui.GetContentRegionAvail().X);
        if (rule.Enabled)
            ImGui.TextUnformatted(display);
        else
            ImGui.TextDisabled(display);
        if (display.Length != full.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(full);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(rule.TargetQuantity.ToString("N0"));
        ImGui.TableNextColumn();
        var destination = rule.Courier is { } route
            ? $"{CourierSession.Side(runtime, route.SourceCharacter ?? row.Owner, route.SourceRetainerId, includeOwner: true)} → {CourierSession.Destination(runtime, route.Character, rule.Routing, includeOwner: true)}"
            : StowTo(runtime, rule.Routing);
        var shown = Fit(destination, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(shown);
        if (shown.Length != destination.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(destination);
    }

    private static string StowTo(QuartermasterRuntimeSnapshot runtime, StowageRoutingPolicy routing)
    {
        if (routing.PreferredRetainerIds.Count == 0)
            return routing.AnySpace ? "Any space" : "—";
        var first = runtime.Retainers.GetValueOrDefault(routing.PreferredRetainerIds[0])?.RetainerName
                    ?? $"Retainer {routing.PreferredRetainerIds[0]}";
        var more = routing.PreferredRetainerIds.Count - 1;
        return more > 0 ? $"{first} +{more}" : first;
    }

    private void DrawActions(OwnerScope owner, StowagePlan plan, bool stable)
    {
        var planId = plan.Id;
        if (UiLayout.PrimaryButton("Open##transferplanmanager"))
            Open(planId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.open",
            $"Open {PlanKindPresentation.Label(plan.Kind)} Plan {plan.Name}",
            true,
            () => Open(planId),
            "Available");

        if (!stable)
            return;

        UiLayout.SameLineIfFits("Duplicate");
        if (ImGui.Button("Duplicate##transferplanmanager"))
            DuplicatePlan(owner, planId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.duplicate",
            $"Duplicate Transfer Plan {plan.Name}",
            true,
            () => DuplicatePlan(runtimeSnapshots.Current.Owner, planId),
            "Available");

        var armed = armedPlanId == planId;
        var deleteLabel = armed ? "Delete?###transferplanmanagerdelete" : "Delete###transferplanmanagerdelete";
        UiLayout.SameLineIfFits(deleteLabel);
        if (armed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ArmedColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ArmedHoveredColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ArmedActiveColor);
        }
        var clicked = ImGui.Button(deleteLabel);
        if (armed)
            ImGui.PopStyleColor(3);
        var hovered = ImGui.IsItemHovered();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.delete",
            $"Delete Transfer Plan {plan.Name} (invoke twice)",
            true,
            () => PressDelete(runtimeSnapshots.Current.Owner, planId),
            armed ? "Armed: invoke again to delete" : "Invoke once to arm, again to delete");
        if (clicked)
            PressDelete(owner, planId);
        else if (armed && !hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            armedPlanId = null;
    }

    // ---- Actions --------------------------------------------------------------------------

    private void SelectPlan(Guid planId)
    {
        if (selectedPlanId == planId)
            return;
        selectedPlanId = planId;
        armedPlanId = null;
        error = string.Empty;
    }

    private void Open(Guid planId)
    {
        armedPlanId = null;
        openPlan(planId);
    }

    private void NewPlan(OwnerScope owner, StowagePlanKind kind)
    {
        if (!owner.HasStableIdentity)
            return;
        try
        {
            var id = state.Mutate(document => StowagePlanCatalog.Create(document, owner, kind: kind).Id);
            SelectWithRename(id);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    private void DuplicatePlan(OwnerScope owner, Guid sourceId)
    {
        if (!owner.HasStableIdentity)
            return;
        try
        {
            var id = state.Mutate(document => StowagePlanCatalog.Duplicate(document, owner, sourceId).Id);
            SelectWithRename(id);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    private void SelectWithRename(Guid planId)
    {
        selectedPlanId = planId;
        armedPlanId = null;
        nameBufferPlanId = null;
        focusName = true;
        filter = string.Empty;
        error = string.Empty;
    }

    private void CommitRename(OwnerScope owner, Guid planId)
    {
        var name = nameBuffer.Trim();
        var current = state.ReadConfiguration(document => document.StowagePlans
            .FirstOrDefault(plan => plan.Id == planId && !plan.IsEphemeral && plan.Owner.Matches(owner))?.Name);
        if (current is null || name.Length == 0 || string.Equals(name, current, StringComparison.Ordinal))
        {
            nameBuffer = current ?? nameBuffer;
            return;
        }
        try
        {
            var applied = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                draft.Name = name;
                return StowagePlanCatalog.Apply(document, owner, draft).Name;
            });
            nameBuffer = applied;
            error = string.Empty;
        }
        catch (Exception exception)
        {
            nameBuffer = current;
            error = exception.Message;
        }
    }

    private void PressDelete(OwnerScope owner, Guid planId)
    {
        if (!owner.HasStableIdentity)
            return;
        if (armedPlanId != planId || DateTime.UtcNow - armedAtUtc > ArmWindow)
        {
            selectedPlanId = planId;
            armedPlanId = planId;
            armedAtUtc = DateTime.UtcNow;
            return;
        }
        armedPlanId = null;
        Delete(owner, planId);
    }

    private void Delete(OwnerScope owner, Guid planId)
    {
        try
        {
            var openPlanId = workbench.SelectedStowagePlanId;
            var (deleted, nextOpen) = state.Mutate(document =>
            {
                var index = document.StowagePlans.FindIndex(candidate =>
                    candidate.Id == planId && !candidate.IsEphemeral && candidate.Owner.Matches(owner));
                if (index < 0)
                    return ((DeletedPlan?)null, openPlanId);
                var plan = document.StowagePlans[index];
                var rules = document.PlanItems.Where(rule => rule.StowagePlanId == planId).ToArray();
                var links = document.TransferPlanListingLinks.Where(link => link.StowagePlanId == planId).ToArray();
                document.PlanItems.RemoveAll(rule => rule.StowagePlanId == planId);
                document.TransferPlanListingLinks.RemoveAll(link => link.StowagePlanId == planId);
                document.StowagePlans.RemoveAt(index);
                StowagePlanCatalog.ClearStaleRecovery(document);
                var scratch = StowagePlanCatalog.EnsureScratch(document, owner, plan.Kind);
                var next = openPlanId == planId ? scratch.Id : openPlanId;
                return (new DeletedPlan(owner with { }, plan, index, rules, links, DateTime.UtcNow), next);
            });
            workbench.SelectedStowagePlanId = nextOpen;
            if (deleted is null)
                return;
            lastDeleted = deleted;
            selectedPlanId = NeighbourAfterDelete(owner, deleted.Plan);
            error = string.Empty;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    private Guid? NeighbourAfterDelete(OwnerScope owner, StowagePlan removed)
    {
        var remaining = state.ReadConfiguration(document =>
            Ordered(document.StowagePlans.Where(plan => !plan.IsEphemeral && plan.Owner.Matches(owner)))
                .Select(plan => (plan.Id, plan.Name, plan.Kind))
                .ToArray());
        if (remaining.Length == 0)
            return null;
        // The plan listed after the removed one, in the manager's order.
        int Compare((Guid Id, string Name, StowagePlanKind Kind) plan)
        {
            var byKind = plan.Kind.CompareTo(removed.Kind);
            if (byKind != 0) return byKind;
            var byName = StringComparer.OrdinalIgnoreCase.Compare(plan.Name, removed.Name);
            return byName != 0 ? byName : plan.Id.CompareTo(removed.Id);
        }
        var after = remaining.FirstOrDefault(plan => Compare(plan) > 0);
        return after.Id != Guid.Empty ? after.Id : remaining[^1].Id;
    }

    private void Undo()
    {
        if (lastDeleted is not { } deleted)
            return;
        lastDeleted = null;
        try
        {
            var restored = state.Mutate(document =>
            {
                if (document.StowagePlans.Any(plan => plan.Id == deleted.Plan.Id))
                    return false;
                var plan = deleted.Plan;
                var uniqueName = StowagePlanCatalog.UniqueName(document, deleted.Owner, plan.Name, plan.Id);
                var otherEnabled = document.StowagePlans.Any(candidate =>
                    !candidate.IsEphemeral && candidate.Enabled && candidate.Owner.Matches(deleted.Owner));
                if (!string.Equals(uniqueName, plan.Name, StringComparison.Ordinal) || (plan.Enabled && otherEnabled))
                {
                    plan.Name = uniqueName;
                    plan.Enabled = plan.Enabled && !otherEnabled;
                    plan.Revision = checked(plan.Revision + 1);
                }
                document.StowagePlans.Insert(Math.Clamp(deleted.Index, 0, document.StowagePlans.Count), plan);
                var ruleIds = document.PlanItems.Select(rule => rule.Id).ToHashSet();
                document.PlanItems.AddRange(deleted.Rules.Where(rule => !ruleIds.Contains(rule.Id)));
                var linkIds = document.TransferPlanListingLinks.Select(link => link.Id).ToHashSet();
                document.TransferPlanListingLinks.AddRange(deleted.Links.Where(link => !linkIds.Contains(link.Id)));
                return true;
            });
            if (restored)
                SelectPlan(deleted.Plan.Id);
            error = string.Empty;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static string Fit(string text, float width)
    {
        if (width <= 0 || ImGui.CalcTextSize(text).X <= width)
            return text;
        var display = text;
        while (display.Length > 1 && ImGui.CalcTextSize(display + "…").X > width)
            display = display[..^1];
        return display + "…";
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => string.Empty,
    };
}
