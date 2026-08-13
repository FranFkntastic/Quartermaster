using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record TransferPlanEditorSnapshot(bool IsOpen, bool HasUnsavedChanges);

/// <summary>
/// Owns the Transfer Plan draft, member selection, bulk controls, and modal lifecycle.
/// </summary>
internal sealed class TransferPlanEditor
{
    private readonly StateRepository state;
    private readonly WorkbenchState workbench;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Action closeRestockEditor;
    private readonly Action requestStowageView;
    private readonly DalamudTableProjection<StowageDraftRow> stowageDraftTable;
    private StowagePlanDraft? stowageDraft;
    private readonly TableSelectionModel<Guid> selectedStowageRuleIds = new();
    private Guid? activeStowageRuleId;
    private Guid? selectedStowageItemGroupId;
    private string stowageItemSearch = string.Empty;
    private string stowageRuleFilter = string.Empty;
    private ItemChoice? selectedStowageChoice;
    private string stowageEditorError = string.Empty;
    private int bulkQuality = -1;
    private int bulkRoutingMode = -1;
    private int bulkOverflow = -1;
    private int bulkStowageTarget = -1;
    private bool requestStowageEditorOpen;
    private bool stowageEditorVisible;

    public TransferPlanEditor(
        StateRepository state,
        WorkbenchState workbench,
        ItemGroupWorkspace itemGroupWorkspace,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Action closeRestockEditor,
        Action requestStowageView)
    {
        this.state = state;
        this.workbench = workbench;
        this.itemGroupWorkspace = itemGroupWorkspace;
        this.reviewRegistry = reviewRegistry;
        this.searchItems = searchItems;
        this.closeRestockEditor = closeRestockEditor;
        this.requestStowageView = requestStowageView;
        stowageDraftTable = CreateDraftTable();
    }

    public void Open(Guid planId, OwnerScope owner) =>
        Open(StowagePlanCatalog.Draft(state.Snapshot(), owner, planId));

    public void Open(StowagePlanDraft draft)
    {
        closeRestockEditor();
        itemGroupWorkspace.CloseEditor();
        stowageDraft = draft;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = stowageDraft.Rules.FirstOrDefault()?.Id;
        selectedStowageItemGroupId = null;
        stowageItemSearch = string.Empty;
        stowageRuleFilter = string.Empty;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
        bulkQuality = -1;
        bulkRoutingMode = -1;
        bulkOverflow = -1;
        bulkStowageTarget = -1;
        requestStowageEditorOpen = true;
        workbench.View = WorkbenchView.Stowage;
        requestStowageView();
    }

    public TransferPlanEditorSnapshot Snapshot(QuartermasterState document, OwnerScope owner) =>
        new(
            stowageDraft is not null && (requestStowageEditorOpen || stowageEditorVisible),
            stowageDraft is not null && StowagePlanCatalog.HasChanges(document, owner, stowageDraft));

    public void SelectItemGroup(Guid groupId) => selectedStowageItemGroupId = groupId;

    public void ClearItemGroup(Guid groupId)
    {
        if (selectedStowageItemGroupId == groupId)
            selectedStowageItemGroupId = null;
    }

    public void Close()
    {
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage))
            itemGroupWorkspace.CloseEditor();
        stowageDraft = null;
        requestStowageEditorOpen = false;
        stowageEditorVisible = false;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = null;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Transfer Plan##RQStowageEditor";
        stowageEditorVisible = false;
        if (requestStowageEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestStowageEditorOpen = false;
        }
        if (stowageDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1220, available.X - 48), Math.Min(720, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            Close();
            return;
        }
        stowageEditorVisible = true;
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage))
        {
            itemGroupWorkspace.DrawEditor(
                selectedStowageRuleIds.Count,
                groupDraft => ItemGroupCatalog.AddMissing(
                    groupDraft,
                    stowageDraft?.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)) ?? []));
            ImGui.EndPopup();
            return;
        }

        var draft = stowageDraft;
        ImGui.TextUnformatted("Edit Transfer Plan");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##stowagedraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedStowageRuleIds.Count:N0} selected of {draft.Rules.Count:N0} rules");

        DrawStowageEditorToolbar(draft);
        DrawStowageEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(stowageEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), stowageEditorError);

        if (ImGui.BeginChild("RQStowageRuleTable", new Vector2(0, -42), true))
            DrawStowageDraftRules(draft, runtime);
        ImGui.EndChild();

        var canApply = StowagePlanCatalog.CanApply(state.Snapshot(), runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Rules.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Rules.Count:N0} rules | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##stowageeditor"))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.editor.cancel",
            "Discard the open Transfer Plan draft",
            true,
            Close,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##stowageeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    StowagePlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedStowagePlanId = appliedId;
                Close();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                stowageEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawStowageEditorToolbar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawStowageDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var snapshot = state.Snapshot();
        var groups = ItemGroupCatalog.All(snapshot);
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedStowageItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##stowagegroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##group{group.Id}", selectedGroup?.Id == group.Id))
                    selectedStowageItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeStowageRuleId ??= draft.Rules.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##stowage"))
            itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Stowage, selectedStowageItemGroupId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.transfer",
            "Open Item Groups from the Transfer Plan editor",
            true,
            () => itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Stowage, selectedStowageItemGroupId),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##stowagerulefilter", "Filter plan items", ref stowageRuleFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible"))
        {
            foreach (var rule in FilteredDraftRules(draft))
                selectedStowageRuleIds.SetSelected(rule.Id, true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection"))
            selectedStowageRuleIds.Clear();
        ImGui.SameLine();
        if (selectedStowageRuleIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group"))
            itemGroupWorkspace.OpenNewEditor(
                ItemGroupEditorOrigin.Stowage,
                draft.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)));
        if (selectedStowageRuleIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawStowageDraftAdd(StowagePlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQStowageItemName", "Add item by name", ref stowageItemSearch, 96))
            selectedStowageChoice = null;
        if (stowageItemSearch.Trim().Length >= 2 && selectedStowageChoice is null)
        {
            foreach (var choice in SearchItems(stowageItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##stowagechoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedStowageChoice = choice;
                    stowageItemSearch = choice.Name;
                }
        }
        if (selectedStowageChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##stowagedraft"))
        {
            var choice = selectedStowageChoice;
            var existing = draft.Rules.FirstOrDefault(rule =>
                rule.ItemId == choice.ItemId && rule.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new TargetPlanItem
                {
                    StowagePlanId = draft.PlanId,
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 0,
                };
                draft.Rules.Add(existing);
            }
            activeStowageRuleId = existing.Id;
            selectedStowageRuleIds.Clear();
            selectedStowageRuleIds.SetSelected(existing.Id, true);
            selectedStowageChoice = null;
            stowageItemSearch = string.Empty;
        }
    }

    private void DrawStowageEditorBulkBar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedStowageRuleIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Player target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##stowagebulktarget", ref bulkStowageTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkquality", ref bulkQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Destination");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkdestination", ref bulkRoutingMode, Enum.GetValues<StowageRoutingMode>(), TransferPresentation.RoutingModeLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Overflow");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkoverflow", ref bulkOverflow, Enum.GetValues<StowageOverflowPolicy>(), TransferPresentation.OverflowLabel);
        ImGui.SameLine();
        var canApply = selectedStowageRuleIds.Count > 0 &&
                       (bulkStowageTarget >= 0 || bulkQuality >= 0 || bulkRoutingMode >= 0 || bulkOverflow >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##stowagebulk"))
        {
            foreach (var rule in draft.Rules.Where(rule => selectedStowageRuleIds.IsSelected(rule.Id)))
            {
                if (bulkStowageTarget >= 0)
                    rule.TargetQuantity = bulkStowageTarget;
                if (bulkQuality >= 0)
                    rule.Quality = (ItemQualityPolicy)bulkQuality;
                if (bulkRoutingMode >= 0)
                    rule.Routing.Mode = (StowageRoutingMode)bulkRoutingMode;
                if (bulkOverflow >= 0)
                    rule.Routing.Overflow = (StowageOverflowPolicy)bulkOverflow;
            }
            bulkStowageTarget = bulkQuality = bulkRoutingMode = bulkOverflow = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedStowageRuleIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##stowagebulk"))
        {
            draft.Rules.RemoveAll(rule => selectedStowageRuleIds.IsSelected(rule.Id));
            selectedStowageRuleIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private static void DrawOptionalEnumCombo<T>(
        string label,
        ref int selected,
        IReadOnlyList<T> values,
        Func<T, string> display)
        where T : struct, Enum
    {
        ImGui.SetNextItemWidth(155);
        var preview = selected < 0 ? "No change" : display((T)Enum.ToObject(typeof(T), selected));
        if (!ImGui.BeginCombo(label, preview))
            return;
        if (ImGui.Selectable("No change", selected < 0))
            selected = -1;
        foreach (var value in values)
        {
            var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (ImGui.Selectable(display(value), selected == numeric))
                selected = numeric;
        }
        ImGui.EndCombo();
    }

    private void DrawStowageDraftRules(StowagePlanDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!stowageDraftTable.Begin(
                "RQStowageDraftRules",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags)))
            return;
        var filtered = FilteredDraftRules(draft);
        selectedStowageRuleIds.Retain(draft.Rules.Select(rule => rule.Id));
        if (filtered.Count == 0)
            stowageDraftTable.DrawMessageRow(draft.Rules.Count == 0
                ? "No rules yet. Search by name above or add a selected Stock item."
                : "No rules match this filter.");
        var rows = filtered.Select(rule => new StowageDraftRow(rule, runtime)).ToArray();
        var rowKeys = rows.Select(row => row.Rule.Id).ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            stowageDraftTable.DrawSelectableRow(
                row,
                selectedStowageRuleIds,
                rowKeys,
                index,
                $"##selectstowage:{row.Rule.Id}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedStowageRuleIds);
        stowageDraftTable.End();
    }

    private void DrawDraftQuality(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftquality{rule.Id}", QualityLabel(rule.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), rule.Quality == quality))
                rule.Quality = quality;
        ImGui.EndCombo();
    }

    private static void DrawVendorPurchaseToggle(TargetPlanItem rule)
    {
        var supported = rule.Quality == ItemQualityPolicy.Any;
        if (!supported)
            ImGui.BeginDisabled();
        var allowed = rule.AllowVendorPurchase;
        if (ImGui.Checkbox($"##vendor-purchase:{rule.Id}", ref allowed))
            rule.AllowVendorPurchase = allowed;
        if (!supported)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(supported
                ? "Allow an ordinary-gil vendor to cover the shortage left after accessible retainer stock. Every run is reviewed first."
                : "Vendor purchasing currently requires Any quality so live reconciliation cannot count the wrong quality.");
    }

    private static bool DrawRuleToggle(string id, bool enabled)
    {
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            enabled ? new Vector4(.16f, .34f, .24f, 1f) : new Vector4(.13f, .15f, .17f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            enabled ? new Vector4(.2f, .43f, .3f, 1f) : new Vector4(.22f, .25f, .28f, 1f));
        if (ImGui.SmallButton($"{(enabled ? "On" : "Off")}##rule{id}"))
            enabled = !enabled;
        ImGui.PopStyleColor(2);
        return enabled;
    }

    private void DrawStowageRouteCombo(TargetPlanItem rule, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##draftdestination{rule.Id}",
                TransferPresentation.RouteSummary(rule.Routing, runtime.Retainers, runtime.Owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
            if (ImGui.Selectable(TransferPresentation.RoutingModeLabel(mode), rule.Routing.Mode == mode))
                rule.Routing.Mode = mode;

        ImGui.Separator();
        ImGui.TextDisabled("Preferred retainers - in order");
        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        for (var index = 0; index < rule.Routing.PreferredRetainerIds.Count; index++)
        {
            var retainerId = rule.Routing.PreferredRetainerIds[index];
            var retainer = ownerRetainers.FirstOrDefault(candidate => candidate.RetainerId == retainerId);
            ImGui.PushID($"inline-route{rule.Id}:{retainerId}:{index}");
            ImGui.TextUnformatted(retainer?.RetainerName ?? $"Retainer {retainerId}");
            ImGui.SameLine();
            if (index > 0 && ImGui.SmallButton("Up"))
                (rule.Routing.PreferredRetainerIds[index - 1], rule.Routing.PreferredRetainerIds[index]) =
                    (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index - 1]);
            ImGui.SameLine();
            if (index + 1 < rule.Routing.PreferredRetainerIds.Count && ImGui.SmallButton("Down"))
                (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index + 1]) =
                    (rule.Routing.PreferredRetainerIds[index + 1], rule.Routing.PreferredRetainerIds[index]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                rule.Routing.PreferredRetainerIds.RemoveAt(index);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        var available = ownerRetainers
            .Where(retainer => !rule.Routing.PreferredRetainerIds.Contains(retainer.RetainerId))
            .ToArray();
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo($"##addinlinepreferred{rule.Id}", "Add preferred retainer"))
        {
            foreach (var retainer in available)
                if (ImGui.Selectable($"{retainer.RetainerName}##addinline{retainer.RetainerId}"))
                    rule.Routing.PreferredRetainerIds.Add(retainer.RetainerId);
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Note");
        var notes = rule.Notes;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText($"##draftstowagenote{rule.Id}", ref notes, 160))
            rule.Notes = notes;
        ImGui.EndCombo();
    }

    private static void DrawStowageOverflowCombo(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftoverflow{rule.Id}", TransferPresentation.OverflowLabel(rule.Routing.Overflow)))
            return;
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
            if (ImGui.Selectable(TransferPresentation.OverflowLabel(overflow), rule.Routing.Overflow == overflow))
                rule.Routing.Overflow = overflow;
        ImGui.EndCombo();
    }

    private IReadOnlyList<TargetPlanItem> FilteredDraftRules(StowagePlanDraft draft)
    {
        var filter = stowageRuleFilter.Trim();
        return draft.Rules
            .Where(rule => filter.Length == 0 || rule.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.ItemId)
            .ToArray();
    }

    private IReadOnlyList<ItemChoice> SearchItems(string search, int limit) => searchItems(search, limit);

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };

    private static string QualityChoiceLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ only",
        ItemQualityPolicy.HqOnly => "HQ only",
        _ => "Any quality",
    };

    private DalamudTableProjection<StowageDraftRow> CreateDraftTable() => new(
    [
        new("Item", 1.5f, row => row.Rule.ItemName, row => row.Rule.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Rule", 52, row => row.Rule.Enabled ? "On" : "Off", Draw: row =>
            row.Rule.Enabled = DrawRuleToggle($"stowage{row.Rule.Id}", row.Rule.Enabled)),
        new("Player target", 92, row => row.Rule.TargetQuantity.ToString("N0"), Draw: DrawStowageTarget),
        new("Quality", 112, row => QualityLabel(row.Rule.Quality), Draw: row => DrawDraftQuality(row.Rule)),
        new("Vendor", 72, row => row.Rule.AllowVendorPurchase ? "Allowed" : "Off", Draw: row => DrawVendorPurchaseToggle(row.Rule)),
        new("Now", 92, StowageDraftOutcome, TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new("Destination", 1.1f, row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner), row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner), ImGuiTableColumnFlags.WidthStretch, Draw: row => DrawStowageRouteCombo(row.Rule, row.Runtime)),
        new("Overflow", 112, row => TransferPresentation.OverflowLabel(row.Rule.Routing.Overflow), Draw: row => DrawStowageOverflowCombo(row.Rule)),
        new("", 28, _ => string.Empty, Draw: DrawStowageDraftRemove),
    ]);

    private static void DrawStowageTarget(StowageDraftRow row)
    {
        var target = row.Rule.TargetQuantity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##drafttarget{row.Rule.Id}", ref target))
            row.Rule.TargetQuantity = Math.Max(0, target);
    }

    private static string StowageDraftOutcome(StowageDraftRow row)
    {
        var player = TransferPlanEvaluation.PlayerQuantity(row.Runtime.Browser, row.Rule);
        return !row.Rule.Enabled
            ? "Off"
            : player < row.Rule.TargetQuantity
                ? $"Retrieve {row.Rule.TargetQuantity - player:N0}"
                : player > row.Rule.TargetQuantity
                    ? $"Stow {player - row.Rule.TargetQuantity:N0}"
                    : "Balanced";
    }

    private void DrawStowageDraftRemove(StowageDraftRow row)
    {
        if (!ImGui.SmallButton($"X##draftremove{row.Rule.Id}"))
            return;
        stowageDraft?.Rules.Remove(row.Rule);
        selectedStowageRuleIds.SetSelected(row.Rule.Id, false);
        if (activeStowageRuleId == row.Rule.Id)
            activeStowageRuleId = stowageDraft?.Rules.FirstOrDefault()?.Id;
    }
}
