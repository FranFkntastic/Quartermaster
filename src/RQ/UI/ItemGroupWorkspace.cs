using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Inputs;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Windows;
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
/// Owns Item Group selection, drafts, tables, and plan-embedded editing. The Item Groups tab and
/// the editor a plan opens share one group list, member table, item search, and save/delete flow;
/// the plan editor adds a way back to its plan and a way to copy the plan's selected lines in.
/// </summary>
internal sealed class ItemGroupWorkspace
{
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);

    /// <summary>The plan an editor was opened from: how many of its lines are selected, and how to add them.</summary>
    private sealed record PlanSelection(int Count, Action<ItemGroupDraft> Add);

    private readonly StateRepository state;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudRootConfirmationDialog confirmationDialog;
    private readonly Action<ItemGroupEditorOrigin, Guid> groupSaved;
    private readonly Action<Guid> groupDeleted;
    private readonly DalamudTableProjection<ItemGroupItem> membersTable;
    private readonly TableSelectionModel<ItemGroupItem> selectedItems = new();
    private readonly DalamudSuggestionInputState addInput = new();
    private readonly ItemChoiceSuggestions addSuggestions;
    private ItemGroupDraft? draft;
    private ItemGroupEditorOrigin? editorOrigin;
    private Guid? selectedGroupId;
    private string groupFilter = string.Empty;
    private ItemQualityPolicy addQuality = ItemQualityPolicy.Any;
    private string error = string.Empty;
    private string status = string.Empty;

    public ItemGroupWorkspace(
        StateRepository state,
        AgentBridgeUiReviewRegistry reviewRegistry,
        DalamudRootConfirmationDialog confirmationDialog,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Action<ItemGroupEditorOrigin, Guid> groupSaved,
        Action<Guid> groupDeleted)
    {
        this.state = state;
        this.reviewRegistry = reviewRegistry;
        this.confirmationDialog = confirmationDialog;
        this.groupSaved = groupSaved;
        this.groupDeleted = groupDeleted;
        addSuggestions = new(searchItems, 8, minimumLength: 1);
        membersTable = CreateMembersTable();
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

    // ---- Entry points ---------------------------------------------------------------------

    /// <summary>The Item Groups tab beside Stock.</summary>
    public void DrawWorkspace(QuartermasterState document)
    {
        EnsureWorkspaceDraft(document);
        MasterDetailPane.Draw(
            "RQItemGroupWorkspace",
            () =>
            {
                ImGui.TextUnformatted("Item groups");
                ImGui.TextDisabled("Reusable item-name shorthands");
                DrawGroupList(document);
            },
            () =>
            {
                if (draft is { } currentDraft)
                {
                    DrawDetail(currentDraft, null);
                    return;
                }
                ImGui.TextUnformatted("Choose an Item Group or create a new one.");
                ImGui.TextDisabled("Selecting stock on the left can then add several items at once.");
            });
    }

    /// <summary>The editor a plan opens in place of its own body; Back returns to the plan.</summary>
    public void DrawEditor(int planSelectionCount, Action<ItemGroupDraft> addPlanSelection)
    {
        if (draft is not { } currentDraft || editorOrigin is null)
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
            ImGui.TextColored(QuartermasterPalette.Amber, "Returning to the plan will discard unsaved Item Group changes.");

        var plan = new PlanSelection(planSelectionCount, addPlanSelection);
        MasterDetailPane.Draw(
            "RQItemGroupEditor",
            () => DrawGroupList(state.Snapshot()),
            () =>
            {
                if (draft is { } editing)
                    DrawDetail(editing, plan);
            });
    }

    /// <summary>The group a bulk add will land in: the open draft, else the group the workspace opens first.</summary>
    public string TargetGroupName(QuartermasterState document) =>
        draft?.Name ??
        (selectedGroupId is { } selectedId ? ItemGroupCatalog.All(document).FirstOrDefault(group => group.Id == selectedId)?.Name : null) ??
        ItemGroupCatalog.All(document).FirstOrDefault()?.Name ??
        "Item group";

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
        addInput.Text = string.Empty;
        error = string.Empty;
    }

    /// <summary>Starts a new workspace Item Group draft seeded with plan lines.</summary>
    public void OpenNewWorkspaceDraft(IEnumerable<TargetPlanItem> rules)
    {
        OpenNewWorkspaceDraft();
        draft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", rules);
    }

    // ---- Shared list and detail -----------------------------------------------------------

    private void DrawGroupList(QuartermasterState document)
    {
        MasterDetailPane.DrawFilter("##itemgroupfilter", "Filter groups", ref groupFilter);
        var needle = groupFilter.Trim();
        var all = ItemGroupCatalog.All(document);
        var groups = all
            .Where(group => needle.Length == 0 ||
                            group.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            group.Items.Any(item => item.ItemName.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        MasterDetailPane.DrawRows("RQItemGroupRows", 1, () =>
        {
            foreach (var group in groups)
                DrawGroupRow(group);
            if (groups.Length == 0)
                ImGui.TextDisabled(all.Count == 0 ? "No item groups yet." : "No groups match this filter.");
        });

        if (MasterDetailPane.FooterButton("New item group"))
            RequestNew();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.new",
            "Create a new Item Group draft",
            true,
            RequestNew,
            "Nothing is saved until Save group");
    }

    private void DrawGroupRow(ItemGroup group)
    {
        var selected = draft is { IsNew: false } && draft.GroupId == group.Id;
        if (MasterDetailPane.Row($"item-group:{group.Id}", $"@{group.Name}", selected))
            RequestSwitch(group.Id);
        var groupId = group.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.item-groups.select.{group.Id}",
            $"Edit Item Group {group.Name}",
            true,
            () => RequestSwitch(groupId),
            selected ? "Selected" : $"{group.Items.Count:N0} items");
        MasterDetailPane.RowDetail(() =>
        {
            var count = $"{group.Items.Count:N0} item{(group.Items.Count == 1 ? string.Empty : "s")}";
            var preview = string.Join(", ", group.Items.Select(item => item.ItemName).Take(3));
            var full = preview.Length == 0 ? count : $"{count} · {preview}{(group.Items.Count > 3 ? "…" : string.Empty)}";
            var shown = DalamudUiLayout.ElideText(full, ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(shown);
            if (shown.Length != full.Length && ImGui.IsItemHovered())
                ImGui.SetTooltip(full);
        });
    }

    private void DrawDetail(ItemGroupDraft currentDraft, PlanSelection? plan)
    {
        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 150));
        var name = currentDraft.Name;
        if (ImGui.InputTextWithHint("##item-group-name", "Group name", ref name, 80))
            currentDraft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{currentDraft.Items.Count:N0} items");
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##item-group"))
            RequestDelete(currentDraft);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.delete",
            currentDraft.IsNew ? "Drop the new Item Group draft" : $"Delete Item Group {currentDraft.Name}",
            true,
            () =>
            {
                if (draft is not null)
                    RequestDelete(draft);
            },
            currentDraft.IsNew ? "Nothing is saved yet" : "Asks for confirmation");

        DrawAddRow(currentDraft, plan);
        ImGui.Separator();
        DrawMembers(currentDraft);
        DrawFooter(currentDraft);
    }

    private void DrawAddRow(ItemGroupDraft currentDraft, PlanSelection? plan)
    {
        var width = Math.Max(220, ImGui.GetContentRegionAvail().X - 140);
        if (DalamudSuggestionInputRenderer.Draw(
                "RQItemGroupAdd",
                addInput,
                addSuggestions.For(addInput.Text),
                width,
                "Add an item by name",
                8,
                out var choice) &&
            choice is not null)
        {
            var added = AddChoice(currentDraft, choice);
            status = added == 0
                ? $"{choice.Name} is already in this group."
                : $"Added {choice.Name}.";
            error = string.Empty;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        DrawQualityCombo("##item-group-add-quality");

        if (plan is not null)
        {
            var addLabel = $"Add {plan.Count:N0} selected from plan##itemgroup";
            DalamudUiLayout.SameLineIfFits(addLabel);
            ImGui.BeginDisabled(plan.Count == 0);
            if (ImGui.Button(addLabel))
                plan.Add(currentDraft);
            ImGui.EndDisabled();
        }

        var removeLabel = $"Remove selected ({selectedItems.Count:N0})##itemgroup";
        DalamudUiLayout.SameLineIfFits(removeLabel);
        ImGui.BeginDisabled(selectedItems.Count == 0);
        if (ImGui.Button(removeLabel))
        {
            currentDraft.Items.RemoveAll(selectedItems.IsSelected);
            selectedItems.Clear();
        }
        ImGui.EndDisabled();
    }

    private void DrawMembers(ItemGroupDraft currentDraft)
    {
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + 2;
        if (!membersTable.Begin(
                "RQItemGroupMembersTable",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(130, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp)))
            return;
        var members = currentDraft.Items.ToArray();
        selectedItems.Retain(members);
        if (members.Length == 0)
            membersTable.DrawMessageRow("No items yet. Search by name above or select stock on the left.");
        for (var index = 0; index < members.Length; index++)
            membersTable.DrawSaneRow(members, index, $"##item-group-member:{members[index].ItemId}:{members[index].GetHashCode()}");
        DalamudTableSelectionRenderer.EndRows(selectedItems);
        membersTable.End();
    }

    private void DrawFooter(ItemGroupDraft currentDraft)
    {
        var (canApply, hasChanges) = state.ReadConfiguration(document =>
            (ItemGroupCatalog.CanApply(document, currentDraft), ItemGroupCatalog.HasChanges(document, currentDraft)));
        var message = !string.IsNullOrWhiteSpace(error) ? error
            : !string.IsNullOrWhiteSpace(status) ? status
            : currentDraft.IsNew && currentDraft.Items.Count == 0 ? "Add at least one item to save this group."
            : editorOrigin is null ? "Select stock on the left to add several items at once."
            : canApply ? "Unsaved changes - groups remember item and quality; plans own quantities and routing."
            : "No unsaved changes.";

        const string discardLabel = "Discard##item-group";
        const string saveLabel = "Save group##item-group";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttons = DalamudUiLayout.ButtonWidth(discardLabel) + spacing + DalamudUiLayout.ButtonWidth(saveLabel);
        var shown = DalamudUiLayout.ElideText(message, ImGui.GetContentRegionAvail().X - buttons - spacing);
        ImGui.AlignTextToFramePadding();
        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(ErrorColor, shown);
        else
            ImGui.TextDisabled(shown);
        if (shown.Length != message.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(message);

        DalamudUiLayout.SameLineRight(buttons);
        ImGui.BeginDisabled(!hasChanges);
        if (ImGui.Button(discardLabel))
            Discard();
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.discard",
            "Discard changes to the current Item Group",
            hasChanges,
            Discard,
            hasChanges ? "Unsaved changes will be discarded" : "No unsaved changes");
        ImGui.SameLine();
        ImGui.BeginDisabled(!canApply);
        if (ImGui.Button(saveLabel))
            Save(currentDraft);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the current Item Group",
            canApply,
            () =>
            {
                if (draft is not null)
                    Save(draft);
            },
            canApply ? "Changes are saved together" : "No valid changes");
    }

    // ---- Actions --------------------------------------------------------------------------

    private void OpenNewWorkspaceDraft()
    {
        draft = ItemGroupCatalog.NewDraft(state.Snapshot());
        editorOrigin = null;
        selectedItems.Clear();
        addInput.Text = string.Empty;
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

    private bool HasUnsavedDraft() =>
        draft is not null && ItemGroupCatalog.HasChanges(state.Snapshot(), draft);

    private void RequestSwitch(Guid groupId)
    {
        if (draft is { IsNew: false } && draft.GroupId == groupId)
            return;
        if (HasUnsavedDraft())
        {
            confirmationDialog.Request(
                $"switch-item-group:{groupId}",
                "Discard unsaved Item Group changes?",
                "The selected Item Group will open and the current draft will be discarded.",
                "Discard and switch",
                () => LoadDraft(groupId));
            return;
        }
        LoadDraft(groupId);
    }

    private void RequestNew()
    {
        if (HasUnsavedDraft())
        {
            confirmationDialog.Request(
                "new-item-group",
                "Discard unsaved Item Group changes?",
                "A new Item Group draft will open and the current draft will be discarded.",
                "Discard and create",
                StartNewDraft);
            return;
        }
        StartNewDraft();
    }

    /// <summary>Opens an empty draft, keeping whichever entry point (workspace or plan editor) is showing.</summary>
    private void StartNewDraft()
    {
        draft = ItemGroupCatalog.NewDraft(state.Snapshot());
        selectedItems.Clear();
        addInput.Text = string.Empty;
        status = string.Empty;
        error = string.Empty;
    }

    /// <summary>Opens a saved group, keeping whichever entry point (workspace or plan editor) is showing.</summary>
    private void LoadDraft(Guid groupId)
    {
        draft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        selectedGroupId = groupId;
        selectedItems.Clear();
        addInput.Text = string.Empty;
        status = string.Empty;
        error = string.Empty;
    }

    /// <summary>Drops an unsaved new draft for the first saved group; the plan editor falls back to a fresh draft.</summary>
    private void DropNewDraft()
    {
        var snapshot = state.Snapshot();
        var first = ItemGroupCatalog.All(snapshot).FirstOrDefault();
        draft = first is not null
            ? ItemGroupCatalog.Draft(snapshot, first.Id)
            : editorOrigin is null ? null : ItemGroupCatalog.NewDraft(snapshot);
        selectedGroupId = draft is { IsNew: false } ? draft.GroupId : null;
        selectedItems.Clear();
        addInput.Text = string.Empty;
    }

    private void Discard()
    {
        if (draft is not { } currentDraft)
            return;
        if (currentDraft.IsNew)
            DropNewDraft();
        else
            LoadDraft(currentDraft.GroupId);
        status = string.Empty;
        error = string.Empty;
    }

    private void Save(ItemGroupDraft currentDraft)
    {
        try
        {
            var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, currentDraft).Id);
            if (editorOrigin is { } origin)
                groupSaved(origin, groupId);
            LoadDraft(groupId);
            status = "Item Group saved.";
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    private void RequestDelete(ItemGroupDraft currentDraft)
    {
        if (currentDraft.IsNew)
        {
            DropNewDraft();
            return;
        }
        confirmationDialog.Request(
            $"delete-item-group:{currentDraft.GroupId}",
            $"Delete \"{currentDraft.Name}\"?",
            "Existing Transfer Plan items will not be changed.",
            "Delete group",
            () => DeleteGroup(currentDraft));
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

    private void ResetEditorInput()
    {
        selectedItems.Clear();
        groupFilter = string.Empty;
        addInput.Text = string.Empty;
        addQuality = ItemQualityPolicy.Any;
        status = string.Empty;
        error = string.Empty;
    }

    // ---- Quality and table ----------------------------------------------------------------

    private void DrawQualityCombo(string id)
    {
        if (!ImGui.BeginCombo(id, QualityText(addQuality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityText(quality), addQuality == quality))
                addQuality = quality;
        ImGui.EndCombo();
    }

    private DalamudTableProjection<ItemGroupItem> CreateMembersTable() => new(
    [
        new("Item", 1.4f, item => item.ItemName, item => item.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Quality", 145, item => QualityText(item.Quality), Draw: DrawItemQuality),
        new("##remove", 62, _ => string.Empty, Draw: item =>
        {
            if (!ImGui.SmallButton($"Remove##group-member:{item.ItemId}:{item.GetHashCode()}"))
                return;
            draft?.Items.Remove(item);
            selectedItems.SetSelected(item, false);
        }),
    ], DalamudTableSelection<ItemGroupItem>.Multi(selectedItems, item => item));

    private static void DrawItemQuality(ItemGroupItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##group-quality:{item.ItemId}:{item.GetHashCode()}", QualityText(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityText(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private static string QualityText(ItemQualityPolicy quality) =>
        TransferPresentation.QualityLabel(quality, "Any quality", " only");
}
