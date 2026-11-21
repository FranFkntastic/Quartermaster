using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Feedback;
using Franthropy.Dalamud.UI.Layout;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Lists the current character's saved Transfer Plans beside a detail pane for renaming,
/// opening, duplicating, and deleting them. Shares its layout with the Item Groups workspace.
/// </summary>
internal sealed class TransferPlanManager
{
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);

    private sealed record DeletedPlan(
        OwnerScope Owner,
        StowagePlan Plan,
        int Index,
        IReadOnlyList<TargetPlanItem> Rules,
        IReadOnlyList<TransferPlanListingLink> Links);

    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly WorkbenchState workbench;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<QuartermasterRuntimeSnapshot, StowagePlan, TransferWorkbenchProjection> resolveProjection;
    private readonly Action<Guid> openPlan;
    private readonly DalamudArmedConfirmButton<Guid> deleteButton = new();
    private readonly DalamudTimedUndoStrip<DeletedPlan> undoStrip = new() { ExpireWhenHidden = true };

    private Guid? selectedPlanId;
    private string filter = string.Empty;
    private Guid? nameBufferPlanId;
    private string nameBuffer = string.Empty;
    private bool focusName;
    private bool nameEditing;
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
        deleteButton.Disarm();
        error = string.Empty;
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var frame = ImGui.GetFrameCount();
        if (lastDrawFrame != frame - 1)
        {
            // The manager was hidden since the last frame: drop transient delete/undo state, even
            // an arm or offer a bridge invocation made while nothing was drawn.
            undoStrip.Clear();
            deleteButton.Disarm();
        }
        lastDrawFrame = frame;

        var owner = runtime.Owner;
        var plans = Ordered(runtime.State.StowagePlans.Where(plan => !plan.IsEphemeral && plan.Owner.Matches(owner))).ToArray();
        var lineCounts = runtime.State.PlanItems
            .GroupBy(rule => rule.StowagePlanId)
            .ToDictionary(group => group.Key, group => group.Count());

        if (undoStrip.TryPeek(out var deleted) && !deleted.Owner.Matches(owner))
            undoStrip.Clear();
        if (deleteButton.TryGetArmedKey(out var armedId) && armedId != selectedPlanId)
            deleteButton.Disarm();
        if (selectedPlanId is null || plans.All(plan => plan.Id != selectedPlanId))
        {
            selectedPlanId = plans.FirstOrDefault(plan => plan.Id == workbench.SelectedStowagePlanId)?.Id
                             ?? plans.FirstOrDefault()?.Id;
        }
        var selected = plans.FirstOrDefault(plan => plan.Id == selectedPlanId);

        MasterDetailPane.Draw(
            "RQTransferPlanManager",
            () => DrawList(owner, plans, lineCounts),
            () =>
            {
                if (selected is not null)
                    DrawDetail(runtime, selected);
            });
    }

    // ---- List -----------------------------------------------------------------------------

    private void DrawList(OwnerScope owner, IReadOnlyList<StowagePlan> plans, IReadOnlyDictionary<Guid, int> lineCounts)
    {
        var stable = owner.HasStableIdentity;
        MasterDetailPane.DrawFilter("##transferplanfilter", "Filter plans", ref filter);

        MasterDetailPane.DrawRows("RQTransferPlanManagerRows", 2, () =>
        {
            if (stable)
                DrawUndoStrip();

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
        });

        if (!stable)
            return;
        if (MasterDetailPane.FooterButton("+ New transfer plan##transferplanmanager"))
            NewPlan(owner, StowagePlanKind.Transfer);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.new",
            "Create a new Transfer Plan in the plan manager",
            true,
            () => NewPlan(runtimeSnapshots.Current.Owner, StowagePlanKind.Transfer),
            "Available");
        if (MasterDetailPane.FooterButton("+ New courier plan##transferplanmanager"))
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
        if (MasterDetailPane.Row($"transferplanmanager:{plan.Id}", plan.Name, selected, ImGuiSelectableFlags.AllowDoubleClick))
        {
            SelectPlan(plan.Id);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Open(plan.Id);
        }
        var planId = plan.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.transfer.plans.select.{plan.Id:N}",
            $"Select {PlanKindPresentation.Label(plan.Kind)} Plan {plan.Name} in the plan manager",
            true,
            () => SelectPlan(planId),
            selected ? "Selected" : $"{PlanKindPresentation.Label(plan.Kind)} · {lineCount:N0} lines");
        MasterDetailPane.RowDetail(() =>
        {
            ImGui.TextColored(PlanKindPresentation.Color(plan.Kind), PlanKindPresentation.Label(plan.Kind));
            ImGui.SameLine(0, 0);
            ImGui.TextDisabled($" · {lineCount:N0} line{(lineCount == 1 ? string.Empty : "s")}");
        });
    }

    private void DrawUndoStrip()
    {
        if (!undoStrip.TryPeek(out var offered))
            return;
        var restoring = undoStrip.Draw(
            "transferplanmanager-undo",
            deleted => $"Deleted \"{deleted.Plan.Name}\"",
            out var restored,
            result =>
            {
                if (result == DalamudUndoStripResult.Undo)
                    reviewRegistry.RegisterLastButton(
                        "quartermaster.transfer.plans.undo",
                        $"Restore the deleted Transfer Plan {offered.Plan.Name}",
                        true,
                        Undo,
                        "Available");
                else
                    reviewRegistry.RegisterLastButton(
                        "quartermaster.transfer.plans.undo-dismiss",
                        $"Dismiss the undo offer for {offered.Plan.Name}",
                        true,
                        undoStrip.Clear,
                        "Available");
            });
        if (restoring)
            Restore(restored!);
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
                .SelectMany(row => row.Rule.Courier is { } line
                    ? line.Places.Where(place => place.Destination).Select(place => place.Character)
                        .DistinctBy(character => (character.LocalContentId, character.HomeWorldId))
                    : [])
                .GroupBy(character => (character.LocalContentId, character.HomeWorldId))
                .Select(group => $"{CourierSession.CharacterLabel(group.First())} {group.Count():N0}")
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
            ImGui.TableSetupColumn(courier ? "Moves" : "Keep", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("999,999").X);
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
        var quality = TransferPresentation.QualityLabel(rule.Quality, anyLabel: string.Empty);
        var suffix = quality.Length == 0 ? string.Empty : $" {quality}";
        if (!rule.Enabled)
            suffix += " · paused";
        var full = rule.ItemName + suffix;
        var display = DalamudUiLayout.ElideText(full, ImGui.GetContentRegionAvail().X);
        if (rule.Enabled)
            ImGui.TextUnformatted(display);
        else
            ImGui.TextDisabled(display);
        if (display.Length != full.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(full);
        ImGui.TableNextColumn();
        if (rule.Courier is null)
            ImGui.TextUnformatted(rule.TargetQuantity.ToString("N0"));
        else if (row.Distribution is { Runs: > 0 } moving)
            ImGui.TextUnformatted(moving.Runs.ToString("N0"));
        ImGui.TableNextColumn();
        var destination = rule.Courier is null
            ? StowTo(runtime, rule.Routing)
            : row.Distribution is { Problem: not Courier.CourierProblem.None } problem
                ? CourierSession.ProblemLabel(problem)
                : row.Distribution is { Moving: > 0 } distribution
                    ? CourierSession.RouteSummary(runtime, distribution)
                    : "In place";
        var shown = DalamudUiLayout.ElideText(destination, ImGui.GetContentRegionAvail().X);
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

        DalamudUiLayout.SameLineIfFits("Duplicate");
        if (ImGui.Button("Duplicate##transferplanmanager"))
            DuplicatePlan(owner, planId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.duplicate",
            $"Duplicate Transfer Plan {plan.Name}",
            true,
            () => DuplicatePlan(runtimeSnapshots.Current.Owner, planId),
            "Available");

        const string deleteLabel = "Delete";
        const string armedLabel = "Delete?";
        DalamudUiLayout.SameLineIfFits(deleteButton.IsArmed(planId) ? armedLabel : deleteLabel);
        if (deleteButton.Draw("transferplanmanagerdelete", planId, deleteLabel, armedLabel))
            Delete(owner, planId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.plans.delete",
            $"Delete Transfer Plan {plan.Name} (invoke twice)",
            true,
            () => PressDelete(runtimeSnapshots.Current.Owner, planId),
            deleteButton.IsArmed(planId) ? "Armed: invoke again to delete" : "Invoke once to arm, again to delete");
    }

    // ---- Actions --------------------------------------------------------------------------

    private void SelectPlan(Guid planId)
    {
        if (selectedPlanId == planId)
            return;
        selectedPlanId = planId;
        deleteButton.Disarm();
        error = string.Empty;
    }

    private void Open(Guid planId)
    {
        deleteButton.Disarm();
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
        deleteButton.Disarm();
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

    /// <summary>The bridge's delete: the first invocation selects and arms the plan, the second deletes it.</summary>
    private void PressDelete(OwnerScope owner, Guid planId)
    {
        if (!owner.HasStableIdentity)
            return;
        selectedPlanId = planId;
        if (deleteButton.Press(planId))
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
                return (new DeletedPlan(owner with { }, plan, index, rules, links), next);
            });
            workbench.SelectedStowagePlanId = nextOpen;
            if (deleted is null)
                return;
            undoStrip.Offer(deleted);
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

    /// <summary>The bridge's undo: restores the offered plan if the offer is still live.</summary>
    private void Undo()
    {
        if (undoStrip.TryTake(out var deleted))
            Restore(deleted);
    }

    private void Restore(DeletedPlan deleted)
    {
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
}
