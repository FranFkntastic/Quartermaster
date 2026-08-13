using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.UI;

internal enum ItemGroupEditorOrigin
{
    Restock,
    Stowage,
}

internal sealed record ItemGroupWorkspaceSnapshot(
    Guid? SelectedGroupId,
    string? SelectedGroupName,
    bool WorkspaceEditorOpen,
    bool HasUnsavedChanges);

/// <summary>
/// Owns Item Group selection, drafts, tables, and plan-embedded editing. The root
/// window supplies navigation and the plan selection being copied into a group.
/// </summary>
internal sealed class ItemGroupWorkspace
{
    private readonly StateRepository state;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly RootConfirmationDialog confirmationDialog;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Action<ItemGroupEditorOrigin, Guid> groupSaved;
    private readonly Action<Guid> groupDeleted;
    private readonly DalamudTableProjection<ItemGroupItem> workspaceTable;
    private readonly DalamudTableProjection<ItemGroupItem> editorTable;
    private readonly TableSelectionModel<ItemGroupItem> selectedItems = new();
    private ItemGroupDraft? draft;
    private ItemGroupEditorOrigin? editorOrigin;
    private Guid? selectedGroupId;
    private string groupFilter = string.Empty;
    private string itemSearch = string.Empty;
    private ItemChoice? selectedChoice;
    private ItemQualityPolicy addQuality = ItemQualityPolicy.Any;
    private string error = string.Empty;
    private string status = string.Empty;
    private bool requestDelete;

    public ItemGroupWorkspace(
        StateRepository state,
        AgentBridgeUiReviewRegistry reviewRegistry,
        RootConfirmationDialog confirmationDialog,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Action<ItemGroupEditorOrigin, Guid> groupSaved,
        Action<Guid> groupDeleted)
    {
        this.state = state;
        this.reviewRegistry = reviewRegistry;
        this.confirmationDialog = confirmationDialog;
        this.searchItems = searchItems;
        this.groupSaved = groupSaved;
        this.groupDeleted = groupDeleted;
        workspaceTable = CreateWorkspaceTable();
        editorTable = CreateEditorTable();
    }

    public bool IsEditorOpenFor(ItemGroupEditorOrigin origin) =>
        draft is not null && editorOrigin == origin;

    public ItemGroupWorkspaceSnapshot Snapshot(bool workspaceVisible)
    {
        var document = state.Snapshot();
        return new(
            draft is { IsNew: false } ? draft.GroupId : null,
            draft?.Name,
            workspaceVisible && draft is not null,
            draft is not null && ItemGroupCatalog.HasChanges(document, draft));
    }

    public void DrawWorkspace(QuartermasterState document)
    {
        EnsureWorkspaceDraft(document);
        var groups = ItemGroupCatalog.All(document);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "RQItemGroupWorkspace",
                2,
                flags,
                new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;

        ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQItemGroupList", Vector2.Zero, false))
        {
            ImGui.TextUnformatted("Item groups");
            ImGui.TextDisabled("Reusable item-name shorthands");
            ImGui.Separator();
            foreach (var group in groups)
            {
                var selected = draft?.GroupId == group.Id;
                if (ImGui.Selectable(
                        $"{group.Name}##group-workspace:{group.Id}",
                        selected,
                        ImGuiSelectableFlags.None,
                        new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 1.8f)))
                    RequestSwitch(group.Id);
                if (ImGui.IsItemVisible())
                {
                    var cursor = ImGui.GetCursorPos();
                    ImGui.SetCursorPosY(cursor.Y - ImGui.GetTextLineHeightWithSpacing());
                    ImGui.Indent();
                    ImGui.TextDisabled($"{group.Items.Count:N0} items");
                    ImGui.Unindent();
                }
            }
            if (groups.Count == 0)
                ImGui.TextDisabled("No item groups yet.");
            ImGui.Separator();
            if (ImGui.Button("New item group", new Vector2(-1, 0)))
                OpenNewWorkspaceDraft();
            reviewRegistry.RegisterLastButton(
                "quartermaster.item-groups.new",
                "Create a new Item Group draft",
                true,
                OpenNewWorkspaceDraft,
                "Nothing is saved until Save group");
        }
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQItemGroupDetail", Vector2.Zero, false))
        {
            if (draft is null)
            {
                ImGui.TextUnformatted("Choose an Item Group or create a new one.");
                ImGui.TextDisabled("Selecting stock on the left can then add several items at once.");
            }
            else
            {
                DrawWorkspaceDetail(draft);
            }
        }
        ImGui.EndChild();
        ImGui.EndTable();
    }

    public int AddStockItems(QuartermasterState document, IReadOnlyList<StockGroup> items)
    {
        EnsureWorkspaceDraft(document);
        if (draft is null)
            OpenNewWorkspaceDraft();
        if (draft is null)
            return 0;

        var added = ItemGroupCatalog.AddMissing(
            draft,
            items.Select(item => new ItemGroupItem
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quality = ItemQualityPolicy.Any,
            }));
        status = added == 0
            ? "Those items are already in this group."
            : $"Added {added:N0} item{(added == 1 ? string.Empty : "s")}.";
        return added;
    }

    public void OpenEditor(ItemGroupEditorOrigin origin, Guid? preferredId)
    {
        editorOrigin = origin;
        var snapshot = state.Snapshot();
        var group = snapshot.ItemGroups.FirstOrDefault(candidate => candidate.Id == preferredId)
                    ?? ItemGroupCatalog.All(snapshot).FirstOrDefault();
        draft = group is null
            ? ItemGroupCatalog.NewDraft(snapshot)
            : ItemGroupCatalog.Draft(snapshot, group.Id);
        ResetEditorInput();
    }

    public void OpenNewEditor(ItemGroupEditorOrigin origin, IEnumerable<RestockPlanItem> items)
    {
        editorOrigin = origin;
        draft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", items);
        ResetEditorInput();
    }

    public void OpenNewEditor(ItemGroupEditorOrigin origin, IEnumerable<TargetPlanItem> rules)
    {
        editorOrigin = origin;
        draft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", rules);
        ResetEditorInput();
    }

    public void CloseEditor()
    {
        draft = null;
        editorOrigin = null;
        selectedItems.Clear();
        selectedChoice = null;
        error = string.Empty;
        requestDelete = false;
    }

    public void DrawEditor(int planSelectionCount, Action<ItemGroupDraft> addPlanSelection)
    {
        if (draft is not { } currentDraft || editorOrigin is not { } origin)
            return;
        var hasChanges = ItemGroupCatalog.HasChanges(state.Snapshot(), currentDraft);

        if (ImGui.Button("<- Back to plan##itemgroups"))
        {
            CloseEditor();
            return;
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.back",
            "Discard the open Item Group draft and return to the Transfer Plan",
            true,
            CloseEditor,
            hasChanges ? "Unsaved Item Group changes will be discarded" : "The Transfer Plan draft remains open");
        ImGui.SameLine();
        ImGui.TextUnformatted("Item groups");
        ImGui.SameLine();
        ImGui.TextDisabled("Reusable across Transfer Plans");
        if (hasChanges)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "Returning to the plan will discard unsaved Item Group changes.");
        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), error);

        var bodyHeight = Math.Max(260, ImGui.GetContentRegionAvail().Y - 42);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQItemGroupEditor", 2, flags, new Vector2(0, bodyHeight)))
        {
            ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 290);
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupList", Vector2.Zero, false))
                DrawGroupList(currentDraft);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupMembers", Vector2.Zero, false))
                DrawMembers(currentDraft, planSelectionCount, addPlanSelection);
            ImGui.EndChild();
            ImGui.EndTable();
        }

        var snapshot = state.Snapshot();
        var canApply = ItemGroupCatalog.CanApply(snapshot, currentDraft);
        ImGui.TextDisabled(currentDraft.IsNew && currentDraft.Items.Count == 0
            ? "Add at least one item to save this group."
            : canApply ? "Unsaved changes - groups remember item and quality; plans own quantities and routing." : "No unsaved changes.");
        var saveButtonWidth = ImGui.CalcTextSize("Save group").X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(Math.Max(
            ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X,
            ImGui.GetWindowContentRegionMax().X - saveButtonWidth));
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save group##itemgroupeditor"))
            SaveEditor(origin, currentDraft);
        if (!canApply)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the open Item Group draft",
            canApply,
            () =>
            {
                if (draft is not null && editorOrigin is { } savedOrigin)
                    SaveEditor(savedOrigin, draft);
            },
            canApply ? "Changes are saved together" : "No valid changes");

        DrawDeletePopup();
    }

    private void DrawWorkspaceDetail(ItemGroupDraft currentDraft)
    {
        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 150));
        var name = currentDraft.Name;
        if (ImGui.InputText("##item-group-name", ref name, 80))
            currentDraft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{currentDraft.Items.Count:N0} items");
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##item-group-workspace"))
            RequestWorkspaceDelete(currentDraft);

        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 140));
        if (ImGui.InputTextWithHint("##item-group-add", "Add an item by name", ref itemSearch, 120))
            selectedChoice = null;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        DrawQualityCombo("##item-group-add-quality");

        var matches = searchItems(itemSearch, 6);
        if (!string.IsNullOrWhiteSpace(itemSearch) && matches.Count > 0)
        {
            if (ImGui.BeginChild(
                    "RQItemGroupSearchResults",
                    new Vector2(0, Math.Min(150, matches.Count * ImGui.GetTextLineHeightWithSpacing() + 8)),
                    true))
            {
                foreach (var choice in matches)
                {
                    if (!ImGui.Selectable($"{choice.Label}##group-add:{choice.ItemId}"))
                        continue;
                    var added = AddChoice(currentDraft, choice);
                    status = added == 0
                        ? $"{choice.Name} is already in this group."
                        : $"Added {choice.Name}.";
                    itemSearch = string.Empty;
                    selectedChoice = null;
                    break;
                }
            }
            ImGui.EndChild();
        }

        ImGui.Separator();
        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2) + 8;
        if (workspaceTable.Begin(
                "RQItemGroupMembersWorkbench",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(130, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)))
        {
            foreach (var member in currentDraft.Items.ToArray())
                workspaceTable.DrawRow(member, id: $"item-group-workspace:{member.ItemId}:{member.GetHashCode()}");
            workspaceTable.End();
        }

        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), error);
        else if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextDisabled(status);
        else
            ImGui.TextDisabled("Select stock on the left to add several items at once.");

        var snapshot = state.Snapshot();
        var canApply = ItemGroupCatalog.CanApply(snapshot, currentDraft);
        var hasChanges = ItemGroupCatalog.HasChanges(snapshot, currentDraft);
        ImGui.SameLine();
        if (!hasChanges)
            ImGui.BeginDisabled();
        if (ImGui.Button("Discard"))
            DiscardWorkspace();
        if (!hasChanges)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.discard",
            "Discard changes to the current Item Group",
            hasChanges,
            DiscardWorkspace,
            hasChanges ? "Unsaved changes will be discarded" : "No unsaved changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save group"))
            SaveWorkspace(currentDraft);
        if (!canApply)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the current Item Group",
            canApply,
            () =>
            {
                if (draft is not null)
                    SaveWorkspace(draft);
            },
            canApply ? "Valid changes" : "No valid changes");
    }

    private void DrawGroupList(ItemGroupDraft currentDraft)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##itemgroupfilter", "Filter groups", ref groupFilter, 80);
        ImGui.Separator();
        var groups = ItemGroupCatalog.All(state.Snapshot())
            .Where(group => groupFilter.Trim().Length == 0 ||
                            group.Name.Contains(groupFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            group.Items.Any(item => item.ItemName.Contains(groupFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var group in groups)
        {
            var preview = string.Join(", ", group.Items.Select(item => item.ItemName).Take(3));
            if (group.Items.Count > 3)
                preview += "...";
            var selected = !currentDraft.IsNew && currentDraft.GroupId == group.Id;
            if (ImGui.Selectable(
                    $"@{group.Name}  ({group.Items.Count:N0})##itemgroup{group.Id}",
                    selected,
                    ImGuiSelectableFlags.AllowDoubleClick))
                TrySwitch(group.Id);
            var groupId = group.Id;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.item-groups.select.{group.Id}",
                $"Edit Item Group {group.Name}",
                true,
                () => TrySwitch(groupId),
                selected ? "Selected" : $"{group.Items.Count:N0} items");
            if (!string.IsNullOrWhiteSpace(preview))
                ImGui.TextDisabled(preview);
        }
        ImGui.Separator();
        if (ImGui.Button("New item group", new Vector2(-1, 0)))
        {
            if (ItemGroupCatalog.HasChanges(state.Snapshot(), currentDraft))
                error = "Save or discard this Item Group before creating another.";
            else
            {
                draft = ItemGroupCatalog.NewDraft(state.Snapshot());
                selectedItems.Clear();
                error = string.Empty;
            }
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.new",
            "Open a new Item Group draft",
            true,
            () =>
            {
                if (draft is not null && !ItemGroupCatalog.HasChanges(state.Snapshot(), draft))
                    draft = ItemGroupCatalog.NewDraft(state.Snapshot());
            },
            "Nothing is saved until Save group");
    }

    private void DrawMembers(
        ItemGroupDraft currentDraft,
        int planSelectionCount,
        Action<ItemGroupDraft> addPlanSelection)
    {
        ImGui.TextDisabled("Name");
        ImGui.SameLine();
        var name = currentDraft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##itemgroupname", ref name, 80))
            currentDraft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{currentDraft.Items.Count:N0} items");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100);
        if (currentDraft.IsNew)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete group##itemgroup"))
            requestDelete = true;
        if (currentDraft.IsNew)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Add member");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##itemgroupitemsearch", "Search by item name", ref itemSearch, 96))
            selectedChoice = null;
        if (itemSearch.Trim().Length >= 2 && selectedChoice is null)
        {
            foreach (var match in searchItems(itemSearch, 5))
                if (ImGui.Selectable(
                        $"{match.Label}##itemgroupchoice{match.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedChoice = match;
                    itemSearch = match.Name;
                }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        DrawQualityCombo("##itemgroupaddquality");
        ImGui.SameLine();
        var canAddItem = selectedChoice is not null;
        if (!canAddItem)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add item##itemgroup") && selectedChoice is { } choice)
        {
            AddChoice(currentDraft, choice);
            selectedChoice = null;
            itemSearch = string.Empty;
        }
        if (!canAddItem)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (planSelectionCount == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Add {planSelectionCount:N0} selected from plan##itemgroup"))
            addPlanSelection(currentDraft);
        if (planSelectionCount == 0)
            ImGui.EndDisabled();

        var hasSelectedMembers = selectedItems.Count > 0;
        if (!hasSelectedMembers)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Remove selected ({selectedItems.Count:N0})##itemgroup"))
        {
            currentDraft.Items.RemoveAll(selectedItems.IsSelected);
            selectedItems.Clear();
        }
        if (!hasSelectedMembers)
            ImGui.EndDisabled();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!editorTable.Begin(
                "RQItemGroupMembersTable",
                new DalamudTableLayout(new Vector2(0, Math.Max(180, ImGui.GetContentRegionAvail().Y)), flags)))
            return;
        var items = currentDraft.Items.ToArray();
        selectedItems.Retain(items);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            editorTable.DrawSelectableRow(
                item,
                selectedItems,
                items,
                index,
                $"##selectgroupitem:{item.ItemId}:{item.GetHashCode()}");
        }
        DalamudTableSelectionRenderer.EndRows(selectedItems);
        editorTable.End();
    }

    private void OpenNewWorkspaceDraft()
    {
        draft = ItemGroupCatalog.NewDraft(state.Snapshot());
        editorOrigin = null;
        selectedItems.Clear();
        itemSearch = string.Empty;
        status = string.Empty;
    }

    private void EnsureWorkspaceDraft(QuartermasterState document)
    {
        if (editorOrigin is not null)
            CloseEditor();
        if (draft is not null)
            return;
        var groups = ItemGroupCatalog.All(document);
        var selected = selectedGroupId is { } selectedId
            ? groups.FirstOrDefault(group => group.Id == selectedId)
            : groups.FirstOrDefault();
        if (selected is null)
            return;
        draft = ItemGroupCatalog.Draft(document, selected.Id);
        selectedGroupId = selected.Id;
    }

    private void RequestSwitch(Guid groupId)
    {
        if (draft?.GroupId == groupId)
            return;
        if (draft is not null && ItemGroupCatalog.HasChanges(state.Snapshot(), draft))
        {
            confirmationDialog.Request(
                $"switch-item-group:{groupId}",
                "Discard unsaved Item Group changes?",
                "The selected Item Group will open and the current draft will be discarded.",
                "Discard and switch",
                () => LoadWorkspaceDraft(groupId));
            return;
        }
        LoadWorkspaceDraft(groupId);
    }

    private void LoadWorkspaceDraft(Guid groupId)
    {
        draft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        editorOrigin = null;
        selectedGroupId = groupId;
        selectedItems.Clear();
        itemSearch = string.Empty;
        status = string.Empty;
        error = string.Empty;
    }

    private void DiscardWorkspace()
    {
        if (draft is not { } currentDraft)
            return;
        var snapshot = state.Snapshot();
        if (currentDraft.IsNew)
        {
            draft = ItemGroupCatalog.All(snapshot).FirstOrDefault() is { } first
                ? ItemGroupCatalog.Draft(snapshot, first.Id)
                : null;
            selectedGroupId = draft?.GroupId;
            selectedItems.Clear();
            itemSearch = string.Empty;
        }
        else
        {
            LoadWorkspaceDraft(currentDraft.GroupId);
        }
        status = string.Empty;
        error = string.Empty;
    }

    private void SaveWorkspace(ItemGroupDraft currentDraft)
    {
        try
        {
            var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, currentDraft).Id);
            LoadWorkspaceDraft(groupId);
            status = "Item Group saved.";
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    private void RequestWorkspaceDelete(ItemGroupDraft currentDraft)
    {
        if (currentDraft.IsNew)
        {
            draft = ItemGroupCatalog.All(state.Snapshot()).FirstOrDefault() is { } first
                ? ItemGroupCatalog.Draft(state.Snapshot(), first.Id)
                : null;
            selectedGroupId = draft?.GroupId;
            return;
        }
        confirmationDialog.Request(
            $"delete-item-group:{currentDraft.GroupId}",
            $"Delete \"{currentDraft.Name}\"?",
            "Existing Transfer Plan items will not be changed.",
            "Delete group",
            () => DeleteGroup(currentDraft));
    }

    private void SaveEditor(ItemGroupEditorOrigin origin, ItemGroupDraft currentDraft)
    {
        try
        {
            var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, currentDraft).Id);
            groupSaved(origin, groupId);
            selectedGroupId = groupId;
            draft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
            selectedItems.Clear();
            error = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
        }
    }

    private void TrySwitch(Guid groupId)
    {
        if (draft is null || draft.GroupId == groupId)
            return;
        if (ItemGroupCatalog.HasChanges(state.Snapshot(), draft))
        {
            error = "Save or discard this Item Group before switching.";
            return;
        }
        draft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        selectedGroupId = groupId;
        selectedItems.Clear();
        itemSearch = string.Empty;
        selectedChoice = null;
        error = string.Empty;
    }

    private void DrawDeletePopup()
    {
        if (requestDelete)
        {
            ImGui.OpenPopup("Delete Item Group##RQ");
            requestDelete = false;
        }
        if (!ImGui.BeginPopupModal("Delete Item Group##RQ", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (draft is not { IsNew: false } currentDraft)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted($"Delete \"@{currentDraft.Name}\"?");
        ImGui.TextDisabled("Existing plan items are not changed.");
        if (ImGui.Button("Delete##itemgroupconfirm"))
        {
            try
            {
                DeleteGroup(currentDraft);
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##itemgroupdelete"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DeleteGroup(ItemGroupDraft currentDraft)
    {
        state.Mutate(document => ItemGroupCatalog.Delete(document, currentDraft.GroupId, currentDraft.SourceRevision));
        groupDeleted(currentDraft.GroupId);
        if (selectedGroupId == currentDraft.GroupId)
            selectedGroupId = null;
        var next = ItemGroupCatalog.All(state.Snapshot()).FirstOrDefault();
        draft = next is null
            ? editorOrigin is null ? null : ItemGroupCatalog.NewDraft(state.Snapshot())
            : ItemGroupCatalog.Draft(state.Snapshot(), next.Id);
        selectedGroupId = draft is { IsNew: false } ? draft.GroupId : null;
        selectedItems.Clear();
        error = string.Empty;
    }

    private int AddChoice(ItemGroupDraft currentDraft, ItemChoice choice) =>
        ItemGroupCatalog.AddMissing(
            currentDraft,
            [new ItemGroupItem
            {
                ItemId = choice.ItemId,
                ItemName = choice.Name,
                Quality = addQuality,
            }]);

    private void DrawQualityCombo(string id)
    {
        if (!ImGui.BeginCombo(id, QualityLabel(addQuality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityLabel(quality), addQuality == quality))
                addQuality = quality;
        ImGui.EndCombo();
    }

    private void ResetEditorInput()
    {
        selectedItems.Clear();
        groupFilter = string.Empty;
        itemSearch = string.Empty;
        selectedChoice = null;
        addQuality = ItemQualityPolicy.Any;
        error = string.Empty;
        requestDelete = false;
    }

    private DalamudTableProjection<ItemGroupItem> CreateWorkspaceTable() => new(
    [
        new("Item", 1.4f, item => item.ItemName, item => item.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Quality", 145, item => QualityLabel(item.Quality), Draw: DrawItemQuality),
        new("##remove", 62, _ => string.Empty, Draw: item =>
        {
            if (ImGui.SmallButton($"Remove##group-member:{item.ItemId}:{item.GetHashCode()}"))
                draft?.Items.Remove(item);
        }),
    ]);

    private DalamudTableProjection<ItemGroupItem> CreateEditorTable() => new(
    [
        new("Item", 1.7f, item => item.ItemName, item => item.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Quality identity", 180, item => QualityLabel(item.Quality), Draw: DrawItemQuality),
        new("", 28, _ => string.Empty, Draw: item =>
        {
            if (!ImGui.SmallButton($"X##removegroupitem{item.GetHashCode()}"))
                return;
            draft?.Items.Remove(item);
            selectedItems.SetSelected(item, false);
        }),
    ]);

    private static void DrawItemQuality(ItemGroupItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##group-quality:{item.ItemId}:{item.GetHashCode()}", QualityLabel(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityLabel(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ only",
        ItemQualityPolicy.HqOnly => "HQ only",
        _ => "Any quality",
    };
}
