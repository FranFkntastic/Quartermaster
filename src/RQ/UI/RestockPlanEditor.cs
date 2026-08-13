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

internal sealed record RestockPlanEditorSnapshot(bool IsOpen, bool HasUnsavedChanges);

/// <summary>
/// Owns Restock Plan draft editing, selection, bulk controls, and modal lifecycle.
/// </summary>
internal sealed class RestockPlanEditor
{
    private readonly StateRepository state;
    private readonly WorkbenchState workbench;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Action closeTransferEditor;
    private readonly Action requestRestockView;
    private readonly DalamudTableProjection<RestockPlanItem> restockDraftTable;
    private RestockPlanDraft? restockDraft;
    private readonly TableSelectionModel<Guid> selectedRestockItemIds = new();
    private Guid? activeRestockItemId;
    private Guid? selectedRestockItemGroupId;
    private string restockItemSearch = string.Empty;
    private string restockItemFilter = string.Empty;
    private ItemChoice? selectedRestockChoice;
    private string restockEditorError = string.Empty;
    private int bulkRestockQuality = -1;
    private int bulkRestockTarget = -1;
    private int bulkRestockNoteMode = -1;
    private bool requestRestockEditorOpen;
    private bool restockEditorVisible;

    public RestockPlanEditor(
        StateRepository state,
        WorkbenchState workbench,
        ItemGroupWorkspace itemGroupWorkspace,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Action closeTransferEditor,
        Action requestRestockView)
    {
        this.state = state;
        this.workbench = workbench;
        this.itemGroupWorkspace = itemGroupWorkspace;
        this.reviewRegistry = reviewRegistry;
        this.searchItems = searchItems;
        this.closeTransferEditor = closeTransferEditor;
        this.requestRestockView = requestRestockView;
        restockDraftTable = CreateDraftTable();
    }

    public RestockPlanEditorSnapshot Snapshot(QuartermasterState document, OwnerScope owner) =>
        new(
            restockDraft is not null && (requestRestockEditorOpen || restockEditorVisible),
            restockDraft is not null && RestockPlanCatalog.HasChanges(document, owner, restockDraft));

    public void SelectItemGroup(Guid groupId) => selectedRestockItemGroupId = groupId;

    public void ClearItemGroup(Guid groupId)
    {
        if (selectedRestockItemGroupId == groupId)
            selectedRestockItemGroupId = null;
    }

    public void FocusItem(Guid itemId)
    {
        activeRestockItemId = itemId;
        selectedRestockItemIds.Clear();
        selectedRestockItemIds.SetSelected(itemId, true);
    }


    private DalamudTableProjection<RestockPlanItem> CreateDraftTable() => new(
    [
        new(
            "Item",
            1.5f,
            item => item.ItemName,
            item => item.ItemName,
            ImGuiTableColumnFlags.WidthStretch),
        new("Rule", 52, item => item.Enabled ? "On" : "Off", Draw: item =>
            item.Enabled = DrawRuleToggle($"restock{item.Id}", item.Enabled)),
        new("Target", 80, item => item.TargetQuantity.ToString("N0"), Draw: DrawRestockTarget),
        new("Quality", 112, item => QualityLabel(item.Quality), Draw: DrawRestockDraftQuality),
        new(
            "Note",
            1f,
            item => item.Notes,
            item => item.Notes,
            ImGuiTableColumnFlags.WidthStretch,
            Draw: DrawRestockNote),
        new(
            "Item group",
            .8f,
            RestockItemGroups,
            item => RestockItemGroups(item),
            ImGuiTableColumnFlags.WidthStretch,
            TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
        new("", 28, _ => string.Empty, Draw: DrawRestockDraftRemove),
    ]);


    public void Open(RestockPlanDraft draft)
    {
        closeTransferEditor();
        itemGroupWorkspace.CloseEditor();
        restockDraft = draft;
        selectedRestockItemIds.Clear();
        activeRestockItemId = draft.Items.FirstOrDefault()?.Id;
        selectedRestockItemGroupId = null;
        restockItemSearch = string.Empty;
        restockItemFilter = string.Empty;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
        bulkRestockQuality = -1;
        bulkRestockTarget = -1;
        bulkRestockNoteMode = -1;
        requestRestockEditorOpen = true;
        workbench.View = WorkbenchView.Restock;
        requestRestockView();
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Restock Plan##RQRestockEditor";
        restockEditorVisible = false;
        if (requestRestockEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestRestockEditorOpen = false;
        }
        if (restockDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1160, available.X - 48), Math.Min(700, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            Close();
            return;
        }
        restockEditorVisible = true;
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Restock))
        {
            itemGroupWorkspace.DrawEditor(
                selectedRestockItemIds.Count,
                groupDraft => ItemGroupCatalog.AddMissing(
                    groupDraft,
                    restockDraft?.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)) ?? []));
            ImGui.EndPopup();
            return;
        }

        var draft = restockDraft;
        ImGui.TextUnformatted("Edit plan");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.55f, .8f, .95f, 1f), "<- Restock");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##restockdraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        var planEnabled = draft.Enabled;
        if (ImGui.Checkbox("Enabled##restockdraft", ref planEnabled))
            draft.Enabled = planEnabled;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedRestockItemIds.Count:N0} selected of {draft.Items.Count:N0} items");

        DrawRestockEditorToolbar(draft);
        DrawRestockEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(restockEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), restockEditorError);

        if (ImGui.BeginChild("RQRestockItemTable", new Vector2(0, -42), true))
            DrawRestockDraftItems(draft);
        ImGui.EndChild();

        var snapshot = state.Snapshot();
        var canApply = RestockPlanCatalog.CanApply(snapshot, runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Items.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Items.Count:N0} items | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##restockeditor"))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.restock.editor.cancel",
            "Discard the open Restock Plan draft",
            true,
            Close,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##restockeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    RestockPlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedRestockPlanId = appliedId;
                Close();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                restockEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawRestockEditorToolbar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawRestockDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var groups = ItemGroupCatalog.All(state.Snapshot());
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedRestockItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##restockgroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##restockgroup{group.Id}", selectedGroup?.Id == group.Id))
                    selectedRestockItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group##restock") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeRestockItemId ??= draft.Items.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##restock"))
            itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Restock, selectedRestockItemGroupId);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.restock",
            "Open Item Groups from the Restock Plan editor",
            true,
            () => itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Restock, selectedRestockItemGroupId),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##restockitemfilter", "Filter plan items", ref restockItemFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible##restock"))
        {
            foreach (var item in FilteredRestockDraftItems(draft))
                selectedRestockItemIds.SetSelected(item.Id, true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection##restock"))
            selectedRestockItemIds.Clear();
        ImGui.SameLine();
        if (selectedRestockItemIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group##restock"))
            itemGroupWorkspace.OpenNewEditor(
                ItemGroupEditorOrigin.Restock,
                draft.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)));
        if (selectedRestockItemIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftAdd(RestockPlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQRestockItemName", "Add item by name", ref restockItemSearch, 96))
            selectedRestockChoice = null;
        if (restockItemSearch.Trim().Length >= 2 && selectedRestockChoice is null)
        {
            foreach (var choice in searchItems(restockItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##restockchoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedRestockChoice = choice;
                    restockItemSearch = choice.Name;
                }
        }
        if (selectedRestockChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##restockdraft"))
        {
            var choice = selectedRestockChoice;
            var existing = draft.Items.FirstOrDefault(item =>
                item.ItemId == choice.ItemId && item.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new RestockPlanItem
                {
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 1,
                };
                draft.Items.Add(existing);
            }
            activeRestockItemId = existing.Id;
            selectedRestockItemIds.Clear();
            selectedRestockItemIds.SetSelected(existing.Id, true);
            selectedRestockChoice = null;
            restockItemSearch = string.Empty;
        }
    }

    private void DrawRestockEditorBulkBar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedRestockItemIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##restockbulktarget", ref bulkRestockTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##restockbulkquality", ref bulkRestockQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Note");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(125);
        if (ImGui.BeginCombo("##restockbulknote", bulkRestockNoteMode < 0 ? "Leave unchanged" : "Clear notes"))
        {
            if (ImGui.Selectable("Leave unchanged", bulkRestockNoteMode < 0))
                bulkRestockNoteMode = -1;
            if (ImGui.Selectable("Clear notes", bulkRestockNoteMode == 0))
                bulkRestockNoteMode = 0;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var canApply = selectedRestockItemIds.Count > 0 &&
                       (bulkRestockQuality >= 0 || bulkRestockTarget >= 0 || bulkRestockNoteMode >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##restockbulk"))
        {
            foreach (var item in draft.Items.Where(item => selectedRestockItemIds.IsSelected(item.Id)))
            {
                if (bulkRestockQuality >= 0)
                    item.Quality = (ItemQualityPolicy)bulkRestockQuality;
                if (bulkRestockTarget >= 0)
                    item.TargetQuantity = bulkRestockTarget;
                if (bulkRestockNoteMode == 0)
                    item.Notes = string.Empty;
            }
            bulkRestockQuality = bulkRestockTarget = bulkRestockNoteMode = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedRestockItemIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##restockbulk"))
        {
            draft.Items.RemoveAll(item => selectedRestockItemIds.IsSelected(item.Id));
            selectedRestockItemIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftItems(RestockPlanDraft draft)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!restockDraftTable.Begin(
                "RQRestockDraftItems",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)),
                    flags)))
            return;
        var filtered = FilteredRestockDraftItems(draft);
        selectedRestockItemIds.Retain(draft.Items.Select(item => item.Id));
        if (filtered.Count == 0)
            restockDraftTable.DrawMessageRow(draft.Items.Count == 0
                ? "No items yet. Search by name above or add a selected Stock item."
                : "No items match this filter.");
        var rowKeys = filtered.Select(item => item.Id).ToArray();
        for (var index = 0; index < filtered.Count; index++)
        {
            var item = filtered[index];
            restockDraftTable.DrawSelectableRow(
                item,
                selectedRestockItemIds,
                rowKeys,
                index,
                $"##selectrestock:{item.Id}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedRestockItemIds);
        restockDraftTable.End();
    }


    private static void DrawRestockTarget(RestockPlanItem item)
    {
        var target = item.TargetQuantity;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##draftrestocktarget{item.Id}", ref target))
            item.TargetQuantity = Math.Max(0, target);
    }

    private static void DrawRestockNote(RestockPlanItem item)
    {
        var note = item.Notes;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText($"##draftrestocknote{item.Id}", ref note, 160))
            item.Notes = note;
    }

    private string RestockItemGroups(RestockPlanItem item)
    {
        var matchingGroups = ItemGroupCatalog.All(state.Snapshot())
            .Where(group => group.Items.Any(member =>
                member.ItemId == item.ItemId && member.Quality == item.Quality))
            .Select(group => $"@{group.Name}")
            .Take(2)
            .ToArray();
        return matchingGroups.Length == 0 ? "Ungrouped" : string.Join(", ", matchingGroups);
    }

    private void DrawRestockDraftRemove(RestockPlanItem item)
    {
        if (!ImGui.SmallButton($"X##draftrestockremove{item.Id}"))
            return;
        restockDraft?.Items.Remove(item);
        selectedRestockItemIds.SetSelected(item.Id, false);
        if (activeRestockItemId == item.Id)
            activeRestockItemId = restockDraft?.Items.FirstOrDefault()?.Id;
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

    private static void DrawRestockDraftQuality(RestockPlanItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftrestockquality{item.Id}", QualityLabel(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private IReadOnlyList<RestockPlanItem> FilteredRestockDraftItems(RestockPlanDraft draft)
    {
        var filter = restockItemFilter.Trim();
        return draft.Items
            .Where(item => filter.Length == 0 || item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }


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

    public void Close()
    {
        if (itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Restock))
            itemGroupWorkspace.CloseEditor();
        restockDraft = null;
        requestRestockEditorOpen = false;
        restockEditorVisible = false;
        selectedRestockItemIds.Clear();
        activeRestockItemId = null;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
    }

}
