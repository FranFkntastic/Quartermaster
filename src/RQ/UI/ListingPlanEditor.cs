using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Owns the Listing Plan draft, filters, validation repair focus, and modal lifecycle.
/// </summary>
internal sealed class ListingPlanEditor
{
    private readonly StateRepository state;
    private readonly IDataManager dataManager;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private ListingPlanDraft? listingPlanDraft;
    private bool requestListingPlanEditorOpen;
    private bool listingPlanEditorVisible;
    private ListingItemKey? listingPlanEditorFocus;
    private ulong? listingPlanEditorRetainerFilter;
    private Guid? listingPlanEditorAssignmentFilter;
    private string listingPlanEditorFilter = string.Empty;
    private readonly Dictionary<Guid, string> listingPlanPriceText = [];
    private string listingPlanItemSearch = string.Empty;
    private ItemChoice? selectedListingPlanChoice;
    private string listingPlanEditorError = string.Empty;
    private IReadOnlyList<ListingPlanValidationIssue> listingPlanEditorConflicts = [];

    public ListingPlanEditor(
        StateRepository state,
        IDataManager dataManager,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems)
    {
        this.state = state;
        this.dataManager = dataManager;
        this.searchItems = searchItems;
    }

    public void Open(QuartermasterRuntimeSnapshot runtime, ListingItemKey? focus)
    {
        listingPlanDraft = ListingPlanCatalog.Draft(state.Snapshot(), runtime.Owner, runtime.Browser);
        listingPlanEditorFocus = focus;
        listingPlanEditorRetainerFilter = null;
        listingPlanEditorAssignmentFilter = null;
        listingPlanEditorFilter = string.Empty;
        ResetListingPlanPriceText(listingPlanDraft.Assignments);
        listingPlanItemSearch = string.Empty;
        selectedListingPlanChoice = null;
        listingPlanEditorError = string.Empty;
        listingPlanEditorConflicts = [];
        requestListingPlanEditorOpen = true;
    }

    public void Close()
    {
        listingPlanDraft = null;
        requestListingPlanEditorOpen = false;
        listingPlanEditorVisible = false;
        listingPlanEditorFocus = null;
        listingPlanEditorRetainerFilter = null;
        listingPlanEditorAssignmentFilter = null;
        listingPlanPriceText.Clear();
        listingPlanEditorError = string.Empty;
        listingPlanEditorConflicts = [];
        selectedListingPlanChoice = null;
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Listing Plan##RQListingPlanEditor";
        listingPlanEditorVisible = false;
        if (requestListingPlanEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestListingPlanEditorOpen = false;
        }
        if (listingPlanDraft is null)
            return;
        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(new(Math.Min(1320, available.X - 48), Math.Min(720, available.Y - 48)), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            if (listingPlanEditorVisible)
                Close();
            return;
        }
        listingPlanEditorVisible = true;
        var draft = listingPlanDraft;
        ImGui.TextUnformatted("Edit Listing Plan");
        ImGui.SameLine();
        ImGui.TextDisabled($"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName} · one active plan · {draft.Assignments.Count:N0} assignments");
        if (draft.IncompleteListingRetainerIds.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), $"· {draft.IncompleteListingRetainerIds.Count:N0} retainers not seeded");
        }
        DrawListingPlanEditorToolbar(draft, runtime);

        var validation = ListingPlanCatalog.Validate(
            draft.Assignments,
            itemId => ResolveListingMaxStack(runtime.Browser, itemId),
            runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner)).Select(retainer => retainer.RetainerId).ToHashSet());
        var overfullRetainers = draft.Assignments.Where(assignment => assignment.Enabled)
            .GroupBy(assignment => assignment.RetainerId)
            .Select(group => new
            {
                RetainerId = group.Key,
                RetainerName = runtime.Retainers.Values.FirstOrDefault(retainer => retainer.RetainerId == group.Key)?.RetainerName ??
                               group.Select(assignment => assignment.RetainerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                               $"Retainer {group.Key}",
                Slots = group.Sum(assignment => assignment.ListingCount),
            })
            .Where(row => row.Slots > 20)
            .OrderBy(row => row.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (overfullRetainers.Length > 0)
        {
            ImGui.TextColored(new Vector4(1f, .45f, .35f, 1f), "Planned capacity needs attention:");
            foreach (var retainer in overfullRetainers)
            {
                if (ImGui.SmallButton($"Fix {retainer.RetainerName} {retainer.Slots:N0} / 20##capacity:{retainer.RetainerId}"))
                {
                    listingPlanEditorFocus = null;
                    listingPlanEditorRetainerFilter = retainer.RetainerId;
                    listingPlanEditorAssignmentFilter = null;
                    listingPlanEditorFilter = retainer.RetainerName;
                }
            }
        }
        else
        {
            var fullest = draft.Assignments.Where(assignment => assignment.Enabled)
                .GroupBy(assignment => assignment.RetainerId)
                .Select(group => new
                {
                    RetainerName = runtime.Retainers.Values.FirstOrDefault(retainer => retainer.RetainerId == group.Key)?.RetainerName ??
                                   group.Select(assignment => assignment.RetainerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
                                   $"Retainer {group.Key}",
                    Slots = group.Sum(assignment => assignment.ListingCount),
                })
                .OrderByDescending(row => row.Slots)
                .FirstOrDefault();
            ImGui.TextDisabled(fullest is null ? "No planned slots yet." : $"Highest planned capacity: {fullest.RetainerName} {fullest.Slots:N0} / 20");
        }
        var repairIssues = validation.Concat(listingPlanEditorConflicts)
            .Where(issue => issue.AssignmentId is not null && issue.Field != "RetainerCapacity")
            .GroupBy(issue => new { issue.AssignmentId, issue.Field })
            .Select(group => group.First())
            .ToArray();
        if (repairIssues.Length > 0 && ImGui.BeginTable(
                "RQListingPlanRepairs",
                2,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Issue", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 58);
            foreach (var issue in repairIssues)
            {
                var assignment = draft.Assignments.FirstOrDefault(candidate => candidate.Id == issue.AssignmentId);
                if (assignment is null)
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(
                    new Vector4(1f, .45f, .35f, 1f),
                    $"{assignment.ItemName} · {ListingIssueFieldLabel(issue.Field)}: {issue.Message}");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Fix##listing-issue:{assignment.Id}:{issue.Field}"))
                {
                    listingPlanEditorFocus = null;
                    listingPlanEditorRetainerFilter = null;
                    listingPlanEditorAssignmentFilter = assignment.Id;
                    listingPlanEditorFilter = assignment.ItemName;
                }
            }
            ImGui.EndTable();
        }
        var transitionConflict = ListingPlanPresentation.CapacityTransitionConflict(draft, runtime.Browser);
        if (transitionConflict is not null)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), transitionConflict);
        if (!string.IsNullOrWhiteSpace(listingPlanEditorError))
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), listingPlanEditorError);
        if (listingPlanEditorConflicts.Count > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, .7f, .3f, 1f),
                $"{listingPlanEditorConflicts.Count:N0} concurrent field changes were rebased. Your values remain highlighted; edit them or Save again to keep them.");
        }

        if (ImGui.BeginChild("RQListingPlanRows", new Vector2(0, Math.Max(230, ImGui.GetContentRegionAvail().Y - 60)), false))
            DrawListingPlanRows(draft, runtime, validation.Concat(listingPlanEditorConflicts).ToArray());
        ImGui.EndChild();
        ImGui.Separator();
        if (validation.Count == 0)
            ImGui.TextDisabled("Changes apply together; current listings never rewrite this plan automatically.");
        else
            ImGui.TextColored(
                new Vector4(1f, .4f, .4f, 1f),
                $"{validation[0].Message} · {validation.Count:N0} field{(validation.Count == 1 ? string.Empty : "s")} need attention.");
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - 176));
        if (ImGui.Button("Cancel##listingplan"))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (validation.Count != 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save Listing Plan"))
        {
            try
            {
                state.Mutate(document => ListingPlanCatalog.Apply(
                    document,
                    runtime.Owner,
                    draft,
                    itemId => ResolveListingMaxStack(runtime.Browser, itemId),
                    runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner)).Select(retainer => retainer.RetainerId).ToHashSet(),
                    DateTime.UtcNow));
                Close();
                ImGui.CloseCurrentPopup();
            }
            catch (ListingPlanConflictException exception)
            {
                var canonical = state.Snapshot().ListingPlans.Single(plan =>
                    plan.Id == draft.PlanId && plan.Owner.Matches(runtime.Owner));
                draft.Assignments = exception.RebasedAssignments.Select(ListingPlanCatalog.Copy).ToList();
                draft.BaselineAssignments = canonical.Assignments.Select(ListingPlanCatalog.Copy).ToList();
                draft.SourceRevision = canonical.Revision;
                ResetListingPlanPriceText(draft.Assignments);
                listingPlanEditorConflicts = exception.Conflicts;
                listingPlanEditorError = string.Empty;
            }
            catch (InvalidOperationException exception)
            {
                listingPlanEditorError = exception.Message;
            }
        }
        if (validation.Count != 0)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawListingPlanEditorToolbar(ListingPlanDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.Separator();
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputTextWithHint("##listingplanitem", "Add an item by name", ref listingPlanItemSearch, 96))
            selectedListingPlanChoice = null;
        if (listingPlanItemSearch.Trim().Length >= 2 && selectedListingPlanChoice is null)
        {
            foreach (var choice in SearchItems(listingPlanItemSearch, 5))
                if (ImGui.Selectable($"{choice.Label}##listingplanchoice{choice.ItemId}", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedListingPlanChoice = choice;
                    listingPlanItemSearch = choice.Name;
                }
        }
        ImGui.SameLine();
        if (selectedListingPlanChoice is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add item") && selectedListingPlanChoice is { } selectedChoice)
        {
            var retainer = runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner))
                .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var observedPrice = runtime.Browser.Listings.FirstOrDefault(listing => listing.ItemId == selectedChoice.ItemId && listing.UnitPrice.IsKnown)?.UnitPrice.Value;
            var assignment = new ListingPlanAssignment
            {
                ItemId = selectedChoice.ItemId,
                ItemName = selectedChoice.Name,
                RetainerId = retainer?.RetainerId ?? 0,
                RetainerName = retainer?.RetainerName ?? "Missing retainer",
                UnitPrice = observedPrice is null ? 0 : checked((int)observedPrice.Value),
            };
            draft.Assignments.Add(assignment);
            listingPlanPriceText[assignment.Id] = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
            listingPlanEditorFocus = new(selectedChoice.ItemId, ItemQualityPolicy.NqOnly);
            selectedListingPlanChoice = null;
            listingPlanItemSearch = string.Empty;
        }
        if (selectedListingPlanChoice is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Add current unplanned listings"))
        {
            var observed = ListingPlanCatalog.Draft(new QuartermasterState(), runtime.Owner, runtime.Browser).Assignments;
            foreach (var assignment in observed)
            {
                var exists = draft.Assignments.Any(current => current.ItemId == assignment.ItemId && current.Quality == assignment.Quality &&
                    current.RetainerId == assignment.RetainerId && current.QuantityPerListing == assignment.QuantityPerListing && current.UnitPrice == assignment.UnitPrice);
                if (!exists)
                {
                    var imported = ListingPlanCatalog.Copy(assignment);
                    draft.Assignments.Add(imported);
                    listingPlanPriceText[imported.Id] = imported.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
                }
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190);
        if (ImGui.InputTextWithHint("##listingplanfilter", "Filter assignments", ref listingPlanEditorFilter, 80))
        {
            listingPlanEditorFocus = null;
            listingPlanEditorRetainerFilter = null;
            listingPlanEditorAssignmentFilter = null;
        }
        ImGui.SameLine();
        if ((listingPlanEditorFocus is not null || listingPlanEditorRetainerFilter is not null || listingPlanEditorAssignmentFilter is not null) &&
            ImGui.SmallButton("All assignments"))
        {
            listingPlanEditorFocus = null;
            listingPlanEditorRetainerFilter = null;
            listingPlanEditorAssignmentFilter = null;
            listingPlanEditorFilter = string.Empty;
        }
    }

    private void DrawListingPlanRows(
        ListingPlanDraft draft,
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<ListingPlanValidationIssue> issues)
    {
        var rows = draft.Assignments
            .Where(assignment => listingPlanEditorAssignmentFilter is { } assignmentId
                ? assignment.Id == assignmentId
                : listingPlanEditorFocus is { } focus
                    ? assignment.ItemId == focus.ItemId && assignment.Quality == focus.Quality
                    : listingPlanEditorRetainerFilter is { } retainerId
                        ? assignment.RetainerId == retainerId
                        : listingPlanEditorFilter.Length == 0 ||
                          assignment.ItemName.Contains(listingPlanEditorFilter, StringComparison.OrdinalIgnoreCase) ||
                          assignment.RetainerName.Contains(listingPlanEditorFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(assignment => assignment.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(assignment => assignment.Quality)
            .ThenBy(assignment => assignment.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retainers = runtime.Retainers.Values.Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!ImGui.BeginTable("RQListingPlanAssignments", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Qty each", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableHeadersRow();
        foreach (var assignment in rows)
        {
            ImGui.PushID(assignment.Id.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); var enabled = assignment.Enabled; if (ImGui.Checkbox("##on", ref enabled)) assignment.Enabled = enabled;
            ImGui.TableNextColumn();
            var itemIssue = FindListingIssue(issues, assignment.Id, nameof(assignment.ItemId)) ??
                            FindListingIssue(issues, assignment.Id, "Assignment");
            if (itemIssue is null) ImGui.TextUnformatted(assignment.ItemName); else ImGui.TextColored(new Vector4(1f, .5f, .4f, 1f), assignment.ItemName);
            if (itemIssue is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(itemIssue.Message);
            ImGui.TableNextColumn();
            var qualityIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.Quality));
            DrawListingQuality(assignment);
            PopListingIssue(qualityIssue);
            ImGui.TableNextColumn();
            var retainerIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.RetainerId));
            DrawListingRetainer(assignment, retainers);
            PopListingIssue(retainerIssue);
            ImGui.TableNextColumn();
            var countIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.ListingCount));
            var count = assignment.ListingCount; ImGui.SetNextItemWidth(-1); if (ImGui.InputInt("##count", ref count, 0)) assignment.ListingCount = count;
            PopListingIssue(countIssue);
            ImGui.TableNextColumn();
            var quantityIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.QuantityPerListing));
            var quantity = assignment.QuantityPerListing; ImGui.SetNextItemWidth(-1); if (ImGui.InputInt("##quantity", ref quantity, 0)) assignment.QuantityPerListing = quantity;
            PopListingIssue(quantityIssue);
            ImGui.TableNextColumn();
            var priceIssue = PushListingIssue(issues, assignment.Id, nameof(assignment.UnitPrice));
            DrawListingUnitPrice(assignment);
            PopListingIssue(priceIssue);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(assignment.DesiredUnits.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Remove"))
            {
                draft.Assignments.Remove(assignment);
                listingPlanPriceText.Remove(assignment.Id);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawListingUnitPrice(ListingPlanAssignment assignment)
    {
        if (!listingPlanPriceText.TryGetValue(assignment.Id, out var text))
            text = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##price", ref text, 13, ImGuiInputTextFlags.CharsDecimal))
        {
            listingPlanPriceText[assignment.Id] = text;
            assignment.UnitPrice = TryParseListingUnitPrice(text, out var parsed) ? parsed : 0;
        }
        if (ImGui.IsItemActivated() && TryParseListingUnitPrice(text, out var activated))
            listingPlanPriceText[assignment.Id] = activated.ToString(CultureInfo.InvariantCulture);
        if (ImGui.IsItemDeactivatedAfterEdit() && TryParseListingUnitPrice(text, out var committed))
            listingPlanPriceText[assignment.Id] = committed.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static bool TryParseListingUnitPrice(string text, out int value) =>
        int.TryParse(
            text.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private void ResetListingPlanPriceText(IEnumerable<ListingPlanAssignment> assignments)
    {
        listingPlanPriceText.Clear();
        foreach (var assignment in assignments)
            listingPlanPriceText[assignment.Id] = assignment.UnitPrice.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static ListingPlanValidationIssue? FindListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field) =>
        issues.FirstOrDefault(issue => issue.AssignmentId == assignmentId && issue.Field == field);

    private static string ListingIssueFieldLabel(string field) => field switch
    {
        nameof(ListingPlanAssignment.ItemId) => "Item",
        nameof(ListingPlanAssignment.Quality) => "Quality",
        nameof(ListingPlanAssignment.RetainerId) => "Retainer",
        nameof(ListingPlanAssignment.ListingCount) => "Listings",
        nameof(ListingPlanAssignment.QuantityPerListing) => "Qty each",
        nameof(ListingPlanAssignment.UnitPrice) => "Unit price",
        "Assignment" => "Assignment",
        _ => field,
    };

    private static ListingPlanValidationIssue? PushListingIssue(
        IReadOnlyList<ListingPlanValidationIssue> issues,
        Guid assignmentId,
        string field)
    {
        var capacityIssue = field is nameof(ListingPlanAssignment.RetainerId) or nameof(ListingPlanAssignment.ListingCount)
            ? FindListingIssue(issues, assignmentId, "RetainerCapacity")
            : null;
        var issue = FindListingIssue(issues, assignmentId, field) ??
                    capacityIssue ??
                    FindListingIssue(issues, assignmentId, "Assignment");
        if (issue is not null)
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.45f, .12f, .1f, .72f));
        return issue;
    }

    private static void PopListingIssue(ListingPlanValidationIssue? issue)
    {
        if (issue is null)
            return;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(issue.Message);
        ImGui.PopStyleColor();
    }

    private static void DrawListingQuality(ListingPlanAssignment assignment)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##quality", QualityLabel(assignment.Quality)))
            return;
        foreach (var quality in new[] { ItemQualityPolicy.NqOnly, ItemQualityPolicy.HqOnly })
            if (ImGui.Selectable(QualityLabel(quality), assignment.Quality == quality))
                assignment.Quality = quality;
        ImGui.EndCombo();
    }

    private static void DrawListingRetainer(ListingPlanAssignment assignment, IReadOnlyList<CachedRetainer> retainers)
    {
        ImGui.SetNextItemWidth(-1);
        var current = retainers.FirstOrDefault(retainer => retainer.RetainerId == assignment.RetainerId);
        var label = current?.RetainerName ?? $"Missing retainer · ID {assignment.RetainerId}";
        if (!ImGui.BeginCombo("##retainer", label))
            return;
        foreach (var retainer in retainers)
            if (ImGui.Selectable($"{retainer.RetainerName}##{retainer.RetainerId}", assignment.RetainerId == retainer.RetainerId))
            {
                assignment.RetainerId = retainer.RetainerId;
                assignment.RetainerName = retainer.RetainerName;
            }
        ImGui.EndCombo();
    }

    private int ResolveListingMaxStack(BrowserProjection browser, uint itemId)
    {
        var observed = browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize;
        if (observed is > 0)
            return checked((int)Math.Min(observed.Value, int.MaxValue));
        var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item is { } value && value.StackSize > 0 ? checked((int)value.StackSize) : 1;
    }

    private IReadOnlyList<ItemChoice> SearchItems(string search, int limit) =>
        searchItems(search, limit);

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };
}
