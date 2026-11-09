using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Inputs;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record TransferPlanEditorSnapshot(bool IsOpen, bool HasUnsavedChanges);

/// <summary>
/// Owns inline transfer editing; its session is shared by both source views.
/// </summary>
internal sealed class TransferPlanEditor
{
    private readonly StateRepository state;
    private readonly WorkbenchState workbench;
    private readonly ItemGroupWorkspace itemGroupWorkspace;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Action closeRestockEditor;
    private readonly Func<GilVendorCatalog>? vendorCatalogSource;
    private readonly DalamudTableProjection<StowageDraftRow> stowageDraftTable;
    private readonly TransferPlanDraftSession session;
    private StowagePlanDraft? stowageDraft => session.Draft;
    private readonly DalamudSuggestionInputState addInput = new();
    private string addQuery = "\0";
    private IReadOnlyList<DalamudSuggestion<ItemChoice>> addChoices = [];
    private readonly TableSelectionModel<Guid> selectedStowageRuleIds = new();
    private Guid? activeStowageRuleId;
    private Guid? selectedStowageItemGroupId;
    private string stowageRuleFilter = string.Empty;
    private string stowageEditorError = string.Empty;
    private int bulkQuality = -1;
    private int bulkRoutingMode = -1;
    private int bulkOverflow = -1;
    private int bulkStowageTarget = -1;
    private IReadOnlyList<TransferPlanListingContribution> contributions = [];
    public int RenderedRowCount { get; private set; }

    public TransferPlanEditor(
        StateRepository state,
        WorkbenchState workbench,
        ItemGroupWorkspace itemGroupWorkspace,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems,
        Action closeRestockEditor,
        Action requestStowageView,
        Func<GilVendorCatalog>? vendorCatalogSource = null)
    {
        this.state = state;
        session = new(state);
        this.workbench = workbench;
        this.itemGroupWorkspace = itemGroupWorkspace;
        this.reviewRegistry = reviewRegistry;
        this.searchItems = searchItems;
        this.closeRestockEditor = closeRestockEditor;
        this.vendorCatalogSource = vendorCatalogSource;
        stowageDraftTable = CreateDraftTable();
    }

    public void Open(Guid planId, OwnerScope owner) =>
        Open(StowagePlanCatalog.Draft(state.Snapshot(), owner, planId));

    public void Open(StowagePlanDraft draft)
    {
        closeRestockEditor();
        itemGroupWorkspace.CloseEditor();
        session.Adopt(draft);
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = draft.Rules.FirstOrDefault()?.Id;
        selectedStowageItemGroupId = null;
        stowageRuleFilter = string.Empty;
        stowageEditorError = string.Empty;
        bulkQuality = -1;
        bulkRoutingMode = -1;
        bulkOverflow = -1;
        bulkStowageTarget = -1;
        workbench.SelectedStowagePlanId = draft.PlanId;
    }

    public TransferPlanEditorSnapshot Snapshot(QuartermasterState document, OwnerScope owner) =>
        new(stowageDraft is not null && owner.HasStableIdentity, session.HasChanges);
    public StowagePlanDraft? Draft => session.Draft;
    public IReadOnlyList<StowagePlanDraft> Drafts => session.Drafts;
    public void Duplicate()
    {
        if (Draft is not { } draft) return;
        var id = Guid.NewGuid();
        Open(new StowagePlanDraft { PlanId = id, IsNew = true, Name = draft.Name + " copy", Enabled = false,
            Rules = draft.Rules.Select(r => StowagePlanCatalog.CopyRule(r, id, true)).ToList() });
    }
    public bool HasChanges => session.HasChanges;
    public void Forget(Guid id) => session.Forget(id);
    public void Ensure(QuartermasterRuntimeSnapshot runtime)
    {
        session.Ensure(runtime.Owner, workbench.SelectedStowagePlanId);
        if (runtime.Owner.HasStableIdentity && session.Draft is { } draft)
            workbench.SelectedStowagePlanId = draft.PlanId;
    }
    public bool Save(OwnerScope owner)
    {
        var saved = session.Save(owner);
        if (saved) workbench.SelectedStowagePlanId = session.Draft?.PlanId;
        return saved;
    }
    public void AddStock(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<StockGroup> items, bool stow)
    {
        Ensure(runtime);
        if (!runtime.Owner.HasStableIdentity) return;
        foreach (var item in items)
            session.Add(item.ItemId, item.ItemName, ItemQualityPolicy.Any, stow ? 0 : item.PlayerQuantity, stow);
    }
    public void Prepare(QuartermasterRuntimeSnapshot runtime, ListingPlanItemEvaluation demand, Guid? destination = null)
    {
        if (destination.HasValue) workbench.SelectedStowagePlanId = destination;
        Ensure(runtime);
        if (runtime.Owner.HasStableIdentity) session.Prepare(demand, runtime.State);
    }

    public void SelectItemGroup(Guid groupId) => selectedStowageItemGroupId = groupId;

    public void ClearItemGroup(Guid groupId)
    {
        if (selectedStowageItemGroupId == groupId)
            selectedStowageItemGroupId = null;
    }

    // Closing the window or switching the source view must not discard the shared draft.
    public void Close() => itemGroupWorkspace.CloseEditor();

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        Ensure(runtime);
        if (!runtime.Owner.HasStableIdentity || stowageDraft is not { } draft)
        { ImGui.TextDisabled("Transfer plan unavailable while owner identity is unknown."); return; }
        contributions = ListingPlanEvaluator.Contributions(runtime.State, draft.PlanId,
            ListingPlanEvaluator.Evaluate(ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner), runtime.Browser))
            .Where(c => !session.RemovedLinks.Contains(c.Link.Id)).ToArray();
        ImGui.TextUnformatted("Transfer plan");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X));
        if (ImGui.InputText("##stowagedraftname", ref planName, 80)) draft.Name = planName;
        ImGui.TextDisabled("Player targets · changes apply together");
        DrawStowageEditorToolbar(draft);
        if (selectedStowageRuleIds.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.20f, .18f, .12f, 1));
            if (ImGui.BeginChild("RQTransferBulk", new Vector2(0, ImGui.GetFrameHeightWithSpacing() * 4), true))
                DrawStowageEditorBulkBar(draft);
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
        var error = session.Error ?? stowageEditorError;
        if (!string.IsNullOrWhiteSpace(error)) ImGui.TextWrapped(error);
        var footerHeight = ImGui.GetFrameHeightWithSpacing() * 2;
        if (ImGui.BeginChild("RQStowageRuleTable", new Vector2(0, -footerHeight), false))
        {
            DrawStowageDraftRules(draft, runtime);
            DrawStowageDraftAdd(draft);
            DrawGroupEntry(draft);
        }
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.TextDisabled(session.HasChanges ? "Unsaved transfer changes" : "No unsaved transfer changes");
        if (ImGui.SmallButton("Discard##stowageeditor")) session.Discard(runtime.Owner);
        reviewRegistry.RegisterLastButton("quartermaster.transfer.editor.cancel", "Discard the Transfer Plan draft", true,
            () => session.Discard(runtime.Owner), "Saved plan remains unchanged");
        ImGui.SameLine();
        var canApply = StowagePlanCatalog.CanApply(state.Snapshot(), runtime.Owner, draft);
        ImGui.BeginDisabled(!canApply);
        if (ImGui.SmallButton("Save transfer plan")) Save(runtime.Owner);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton("quartermaster.transfer.editor.save", "Save the Transfer Plan draft", canApply,
            () => Save(runtime.Owner), canApply ? "Save all transfer edits" : "No changes or a name/item is required");
    }

    public void DrawDialogs()
    {
        if (!itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage) || Draft is not { } draft) return;
        const string popup = "Item groups##RQTransferGroups";
        if (!ImGui.IsPopupOpen(popup)) ImGui.OpenPopup(popup);
        ImGui.SetNextWindowSize(new Vector2(850, 520), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup)) return;
        itemGroupWorkspace.DrawEditor(selectedStowageRuleIds.Count,
            groupDraft => ItemGroupCatalog.AddMissing(groupDraft, draft.Rules.Where(r => selectedStowageRuleIds.IsSelected(r.Id))));
        if (!itemGroupWorkspace.IsEditorOpenFor(ItemGroupEditorOrigin.Stowage)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawGroupEntry(StowagePlanDraft draft)
    {
        if (ImGui.SmallButton("Add group...")) ImGui.OpenPopup("RQTransferAddGroup");
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups..."))
            itemGroupWorkspace.OpenEditor(ItemGroupEditorOrigin.Stowage, selectedStowageItemGroupId);
        if (!ImGui.BeginPopup("RQTransferAddGroup")) return;
        foreach (var group in ItemGroupCatalog.All(state.Snapshot()))
            if (ImGui.Selectable("@" + group.Name))
            { ItemGroupCatalog.AddMissing(group, draft); selectedStowageItemGroupId = group.Id; }
        ImGui.EndPopup();
    }

    private void DrawStowageEditorToolbar(StowagePlanDraft draft)
    {
        ImGui.SetNextItemWidth(Math.Max(100, ImGui.GetContentRegionAvail().X - 100));
        ImGui.InputTextWithHint("##stowagerulefilter", "Filter plan items", ref stowageRuleFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible"))
            foreach (var rule in FilteredDraftRules(draft)) selectedStowageRuleIds.SetSelected(rule.Id, true);
    }

    private void DrawStowageDraftAdd(StowagePlanDraft draft)
    {
        if (addQuery != addInput.Text)
        {
            addQuery = addInput.Text;
            addChoices = addQuery.Trim().Length < 2 ? [] :
                SearchItems(addQuery, 64).Select(c => new DalamudSuggestion<ItemChoice>(c, c.Label)).ToArray();
        }
        if (DalamudSuggestionInputRenderer.Draw("RQTransferAdd", addInput, addChoices,
            Math.Max(160, ImGui.GetContentRegionAvail().X), "Add item by name", 8, out var picked) && picked is not null)
        {
            var rule = session.Add(picked.ItemId, picked.Name, ItemQualityPolicy.Any, 1);
            activeStowageRuleId = rule.Id;
            stowageRuleFilter = string.Empty;
        }
    }

    private void DrawStowageEditorBulkBar(StowagePlanDraft draft)
    {
        ImGui.TextUnformatted($"{selectedStowageRuleIds.Count:N0} selected");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection")) selectedStowageRuleIds.Clear();
        ImGui.SameLine();
        if (ImGui.SmallButton("Save as @group")) itemGroupWorkspace.OpenNewEditor(ItemGroupEditorOrigin.Stowage, draft.Rules.Where(r => selectedStowageRuleIds.IsSelected(r.Id)));
        ImGui.TextDisabled("Player target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##stowagebulktarget", ref bulkStowageTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkquality", ref bulkQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.TextDisabled("Destination");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkdestination", ref bulkRoutingMode, Enum.GetValues<StowageRoutingMode>(), TransferPresentation.RoutingModeLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Overflow");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkoverflow", ref bulkOverflow, Enum.GetValues<StowageOverflowPolicy>(), TransferPresentation.OverflowLabel);
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
        ImGui.SetNextItemWidth(110);
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
        stowageDraftTable.DrawColumnMenuButton("RQTransferDraftColumns");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Hideable;
        if (!stowageDraftTable.Begin(
                "RQStowageDraftRules",
                new DalamudTableLayout(
                    new Vector2(0, Math.Min(Math.Max(80, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3),
                        (FilteredDraftRules(draft).Count + 2) * ImGui.GetFrameHeightWithSpacing())),
                    flags)))
            return;
        var filtered = FilteredDraftRules(draft);
        selectedStowageRuleIds.Retain(draft.Rules.Select(rule => rule.Id));
        if (filtered.Count == 0)
            stowageDraftTable.DrawMessageRow(draft.Rules.Count == 0
                ? "No rules yet. Search by name below or add a selected Stock item."
                : "No rules match this filter.");
        var rows = filtered.Select(rule => new StowageDraftRow(rule, runtime)).ToArray();
        RenderedRowCount = rows.Length;
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
        {
            rule.AllowVendorPurchase = allowed;
            if (!allowed)
                rule.PreferredVendorNpcId = null;
        }
        if (!supported)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(supported
                ? "Allow an ordinary-gil vendor to cover the shortage left after accessible retainer stock. Every run is reviewed first."
                : "Vendor purchasing currently requires Any quality so live reconciliation cannot count the wrong quality.");
    }

    private void DrawVendorPurchaseCell(StowageDraftRow row)
    {
        var rule = row.Rule;
        DrawVendorPurchaseToggle(rule);
        if (!rule.AllowVendorPurchase)
            return;
        ImGui.SameLine();
        var offers = vendorCatalogSource?.Invoke().FindOffers(rule.ItemId) ?? [];
        if (offers.Count == 0)
        {
            ImGui.TextDisabled("no vendor known yet");
            return;
        }
        ImGui.SetNextItemWidth(-1);
        var selected = offers.FirstOrDefault(offer => offer.NpcId == rule.PreferredVendorNpcId);
        var preview = selected is null
            ? "Cheapest reachable"
            : $"{selected.NpcName} · {selected.UnitPriceGil:N0} ea";
        ImGui.PushID($"vendor-pick:{rule.Id}");
        if (!ImGui.BeginCombo("##vendor-pick", preview))
        {
            ImGui.PopID();
            return;
        }
        if (ImGui.Selectable("Cheapest reachable", selected is null))
            rule.PreferredVendorNpcId = null;
        foreach (var offer in offers)
        {
            var label = $"{offer.NpcName} · {offer.UnitPriceGil:N0} ea";
            if (ImGui.Selectable(label, selected?.NpcId == offer.NpcId))
                rule.PreferredVendorNpcId = offer.NpcId;
        }
        ImGui.EndCombo();
        ImGui.PopID();
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
        new("Rule", 38, row => row.Rule.Enabled ? "On" : "Off", Draw: row =>
            row.Rule.Enabled = DrawRuleToggle($"stowage{row.Rule.Id}", row.Rule.Enabled)),
        new("Player target", 76, row => row.Rule.TargetQuantity.ToString("N0"), Draw: DrawStowageTarget),
        new("Quality", 62, row => QualityLabel(row.Rule.Quality), Draw: row => DrawDraftQuality(row.Rule)),
        new("Vendor", 72, row => row.Rule.AllowVendorPurchase ? "Allowed" : "Off", Flags: ImGuiTableColumnFlags.DefaultHide, Draw: DrawVendorPurchaseCell),
        new("Target delta", 92, StowageDraftOutcome, TextColor: _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            HeaderTooltip: "Draft target minus carried stock. Review moves verifies available stock and capacity before execution."),
        new("Destination", 1.1f, row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner), row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Runtime.Owner), ImGuiTableColumnFlags.WidthStretch, Draw: row => DrawStowageRouteCombo(row.Rule, row.Runtime)),
        new("Overflow", 112, row => TransferPresentation.OverflowLabel(row.Rule.Routing.Overflow), Flags: ImGuiTableColumnFlags.DefaultHide, Draw: row => DrawStowageOverflowCombo(row.Rule)),
        new("", 28, _ => string.Empty, Draw: DrawStowageDraftRemove),
    ]);

    private void DrawStowageTarget(StowageDraftRow row)
    {
        var contribution = contributions.FirstOrDefault(c => c.Link.ItemId == row.Rule.ItemId && c.Link.Quality == row.Rule.Quality);
        var extra = contribution?.Quantity.IsKnown == true ? contribution.Quantity.Value : 0;
        var target = checked(row.Rule.TargetQuantity + extra);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##drafttarget{row.Rule.Id}", ref target))
            row.Rule.TargetQuantity = Math.Max(0, target - extra);
        if (contribution is not null)
        {
            ImGui.TextDisabled(contribution.Quantity.IsKnown ? $"Linked +{extra}" : "Linked +?");
            if (ImGui.SmallButton($"Unlink##{row.Rule.Id}")) session.Unlink(contribution.Link.Id);
        }
    }

    private string StowageDraftOutcome(StowageDraftRow row)
    {
        var contribution = contributions.FirstOrDefault(c => c.Link.ItemId == row.Rule.ItemId && c.Link.Quality == row.Rule.Quality);
        if (contribution is not null && !contribution.Quantity.IsKnown) return "Verify listing demand";
        var target = checked(row.Rule.TargetQuantity + (contribution?.Quantity.Value ?? 0));
        var player = TransferPlanEvaluation.PlayerQuantity(row.Runtime.Browser, row.Rule);
        return !row.Rule.Enabled
            ? "Off"
            : player < target
                ? $"Retrieve {target - player:N0}"
                : player > target
                    ? $"Stow {player - target:N0}"
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
