using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.FFXIV.Filtering;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Owns the Listings workbench projection, selection, tables, and retainer
/// navigation. Editing and cross-workspace navigation remain explicit callbacks.
/// </summary>
internal sealed class ListingWorkspace
{
    private readonly WorkbenchState workbench;
    private readonly ListingNavigationCoordinator listingNavigation;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action<QuartermasterRuntimeSnapshot, ListingGroupView> focusStock;
    private readonly Action<QuartermasterRuntimeSnapshot, ListingItemKey?> openPlanEditor;
    private readonly Action clearStockSelection;
    private readonly BrowserQueryController queries = new();
    private readonly TableSelectionModel<ListingItemKey> selection = new();
    private readonly DalamudTableProjection<ListingGroupView> groupTable;
    private readonly DalamudTableProjection<ListingGroupView> compactGroupTable;
    private readonly DalamudTableProjection<PhysicalListingGroupView> physicalTable;

    public ListingWorkspace(
        WorkbenchState workbench,
        ListingNavigationCoordinator listingNavigation,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action<QuartermasterRuntimeSnapshot, ListingGroupView> focusStock,
        Action<QuartermasterRuntimeSnapshot, ListingItemKey?> openPlanEditor,
        Action clearStockSelection)
    {
        this.workbench = workbench;
        this.listingNavigation = listingNavigation;
        this.reviewRegistry = reviewRegistry;
        this.focusStock = focusStock;
        this.openPlanEditor = openPlanEditor;
        this.clearStockSelection = clearStockSelection;
        groupTable = CreateGroupTable();
        compactGroupTable = CreateCompactGroupTable();
        physicalTable = CreatePhysicalTable();
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var projection = runtime.Browser;
        var sourceListings = projection.GetListings(workbench.ScopeKey);
        var context = BrowserQueryController.CreateListingContext(sourceListings, projection.Owner);
        DalamudFilterAutocompleteRenderer.Draw(
            "RQListingsWorkbench",
            "Search listed or planned items",
            context,
            workbench.ListingFilterState,
            Math.Max(240, ImGui.GetContentRegionAvail().X - 340));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        var selectedScope = projection.Scopes.First(scope => scope.Key == workbench.ScopeKey);
        if (ImGui.BeginCombo("##RQListingScope", selectedScope.Label))
        {
            foreach (var scope in projection.Scopes)
            {
                if (ImGui.Selectable($"{scope.Label}##listing-scope:{scope.Key}", scope.Key == workbench.ScopeKey))
                {
                    workbench.ScopeKey = scope.Key;
                    workbench.FocusedStockItemId = null;
                    clearStockSelection();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Manage Listing Plan##top"))
            openPlanEditor(runtime, null);

        var result = queries.QueryListings(
            projection,
            workbench.ListingFilterState.Expression,
            workbench.ScopeKey,
            workbench.ListingFilterState.IsInputActive,
            runtime.ListingsRevision);
        if (!result.Filter.IsValid)
            ImGui.TextColored(
                new Vector4(1f, .65f, .25f, 1f),
                result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        var evaluation = ListingPlanEvaluator.Evaluate(
            ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner),
            projection,
            workbench.ScopeKey);
        var resultKeys = result.Listings.Select(listing => new ListingItemKey(
            listing.ItemId,
            listing.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly)).ToHashSet();
        var hasFilter = !string.IsNullOrWhiteSpace(workbench.ListingFilterState.Expression);
        var groups = evaluation.Items
            .Where(item => !hasFilter || resultKeys.Contains(new(item.ItemId, item.Quality)) ||
                           item.ItemName.Contains(workbench.ListingFilterState.Expression, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ListingGroupView(
                new(item.ItemId, item.Quality),
                item.ItemId,
                item.ItemName,
                item.Quality,
                item.DesiredUnits,
                item.ListedUnits,
                item.NeedUnits,
                item.PlayerUnits,
                item.RetainerUnits,
                item.ImmediatelyListableUnits,
                item.MovementNeedUnits,
                item.OtherRetainerUnits,
                item.RetrievableUnits,
                item.MissingUnits,
                item.Coverage,
                item.Assignments,
                item.PhysicalListings,
                item.UnmanagedPhysicalListings))
            .OrderBy(group => group.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Quality)
            .ToArray();
        if (groups.Length == 0)
        {
            DrawEmptyState(projection);
            return;
        }

        if (workbench.SelectedListingItem is not { } selectedItem ||
            groups.All(group => group.Key != selectedItem))
            workbench.SelectedListingItem = groups[0].Key;
        var selected = groups.Single(group => group.Key == workbench.SelectedListingItem);
        selection.Retain(groups.Select(group => group.Key));
        selection.SelectOnly(selected.Key);
        var compact = ImGui.GetContentRegionAvail().X <= 1150;
        var table = compact ? compactGroupTable : groupTable;

        if (!ImGui.BeginTable(
                "RQListingsWorkbench",
                2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthStretch, .9f);
        ImGui.TableSetupColumn("Listing detail", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQListingGroups", Vector2.Zero, false))
            DrawGroups(groups, table, compact);
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQListingDetail", Vector2.Zero, false))
            DrawDetail(runtime, selected);
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void DrawEmptyState(BrowserProjection projection)
    {
        ImGui.TextDisabled("No listings match this view.");
        var navigationTarget = ResolveEmptyNavigationTarget(projection);
        if (navigationTarget is not null)
        {
            if (listingNavigation.IsRunning)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Open {navigationTarget.RetainerName}'s listings"))
                _ = listingNavigation.OpenRetainerListingsAsync(navigationTarget);
            if (listingNavigation.IsRunning)
                ImGui.EndDisabled();
            reviewRegistry.RegisterLastButton(
                "quartermaster.listings.open-first",
                $"Open {navigationTarget.RetainerName}'s listings",
                !listingNavigation.IsRunning,
                () =>
                {
                    if (ResolveEmptyNavigationTarget(projection) is { } target)
                        _ = listingNavigation.OpenRetainerListingsAsync(target);
                },
                listingNavigation.IsRunning ? listingNavigation.Status : "Ready");
        }
        DrawNavigationStatus(showRecovery: true);
    }

    private void DrawGroups(
        IReadOnlyList<ListingGroupView> groups,
        DalamudTableProjection<ListingGroupView> table,
        bool compact)
    {
        if (!table.Begin(
                compact ? "RQListingGroupRowsCompact" : "RQListingGroupRowsWide",
                ImGui.GetContentRegionAvail().Y))
            return;
        table.DrawFilterRow();
        var visibleGroups = table.Apply(groups, ImGui.TableGetSortSpecs());
        var groupKeys = visibleGroups.Select(group => group.Key).ToArray();
        for (var index = 0; index < visibleGroups.Count; index++)
        {
            var group = visibleGroups[index];
            if (!table.DrawSelectableRow(
                    group,
                    selection,
                    groupKeys,
                    index,
                    $"##listing-group:{group.ItemId}:{group.Quality}"))
                continue;
            selection.SelectOnly(group.Key);
            workbench.SelectedListingItem = group.Key;
        }
        DalamudTableSelectionRenderer.EndRows(selection);
        table.End();
    }

    private void DrawDetail(QuartermasterRuntimeSnapshot runtime, ListingGroupView selected)
    {
        ImGui.TextUnformatted(selected.ItemName);
        ImGui.SameLine();
        ImGui.TextDisabled(QualityLabel(selected.Quality));
        var stateColor = StateColor(selected) ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.TextColored(stateColor, VerdictText(selected));
        ImGui.TextDisabled(selected.Assignments.Count == 0
            ? $"{EvidenceText(selected.ListedUnits)} listed · no assignment"
            : $"{selected.DesiredUnits:N0} desired · {EvidenceText(selected.ListedUnits)} listed · {CoverageText(selected)}");
        if (ImGui.SmallButton("Review stock & movement"))
            focusStock(runtime, selected);
        ImGui.SameLine();
        var assignmentAction = selected.Assignments.Count == 0 ? "Manage Listing Plan" : "Edit assignments";
        if (ImGui.SmallButton(assignmentAction))
            openPlanEditor(runtime, selected.Assignments.Count == 0 ? null : selected.Key);
        DrawAssignments(selected);

        ImGui.Separator();
        var physicalGroups = GroupPhysicalListings(selected.Listings);
        ImGui.TextDisabled(
            $"Physical listing groups · {selected.Listings.Count:N0} listing{(selected.Listings.Count == 1 ? string.Empty : "s")} · " +
            $"{physicalGroups.Count:N0} group{(physicalGroups.Count == 1 ? string.Empty : "s")}");
        var detailHeight = Math.Max(
            120,
            ImGui.GetContentRegionAvail().Y -
            (!string.IsNullOrWhiteSpace(listingNavigation.Status) ? ImGui.GetTextLineHeightWithSpacing() : 0));
        if (physicalTable.Begin("RQPhysicalListingGroupsV1", detailHeight))
        {
            physicalTable.DrawFilterRow();
            var visibleGroups = physicalTable.Apply(physicalGroups, ImGui.TableGetSortSpecs());
            foreach (var group in visibleGroups)
                physicalTable.DrawRow(group);
            physicalTable.End();
        }
        DrawNavigationStatus();
    }

    private void DrawAssignments(ListingGroupView selected)
    {
        if (selected.Assignments.Count == 0)
            return;
        ImGui.Separator();
        ImGui.TextDisabled("Listing Plan assignments");
        if (!ImGui.BeginTable(
                "RQListingAssignments",
                5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Exact", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn("Exceptions", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var assignment in selected.Assignments)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (listingNavigation.IsRunning)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{assignment.Assignment.RetainerName}##open-assignment:{assignment.Assignment.Id}"))
                _ = listingNavigation.OpenRetainerListingsAsync(new(
                    assignment.Assignment.RetainerId,
                    assignment.Assignment.RetainerName));
            if (listingNavigation.IsRunning)
                ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{assignment.Assignment.ListingCount:N0} × {assignment.Assignment.QuantityPerListing:N0}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(selected.ListedUnits.IsKnown ? assignment.ExactListings.ToString("N0") : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{assignment.Assignment.UnitPrice:N0}");
            ImGui.TableNextColumn();
            DrawAssignmentExceptions(selected, assignment);
        }
        ImGui.EndTable();
    }

    private static void DrawAssignmentExceptions(
        ListingGroupView selected,
        ListingAssignmentEvaluation assignment)
    {
        if (!selected.ListedUnits.IsKnown)
        {
            ImGui.TextDisabled("—");
            return;
        }
        var exceptions = new List<string>();
        if (assignment.UnknownPriceListings > 0)
            exceptions.Add($"{assignment.UnknownPriceListings:N0} price unknown");
        if (assignment.WrongPriceListings > 0)
            exceptions.Add($"{assignment.WrongPriceListings:N0} price");
        if (assignment.WrongShapeListings > 0)
            exceptions.Add($"{assignment.WrongShapeListings:N0} shape");
        if (assignment.WrongRetainerListings > 0)
            exceptions.Add($"{assignment.WrongRetainerListings:N0} retainer");
        ImGui.TextDisabled(exceptions.Count == 0 ? "—" : string.Join(" · ", exceptions));
    }

    private RetainerListingsOpenRequest? ResolveEmptyNavigationTarget(BrowserProjection projection)
    {
        var scope = projection.Scopes.FirstOrDefault(
                        candidate => candidate.Kind == BrowserScopeKind.Retainer && candidate.Key == workbench.ScopeKey)
                    ?? projection.Scopes.FirstOrDefault(candidate => candidate.Kind == BrowserScopeKind.Retainer);
        return scope?.RetainerId is { } retainerId
            ? new RetainerListingsOpenRequest(retainerId, scope.Label)
            : null;
    }

    private void DrawNavigationStatus(bool showRecovery = false)
    {
        if (!string.IsNullOrWhiteSpace(listingNavigation.Status))
            ImGui.TextDisabled(listingNavigation.Status);
        if (!showRecovery && !listingNavigation.Status.StartsWith("Opened ", StringComparison.Ordinal))
            return;
        if (!string.IsNullOrWhiteSpace(listingNavigation.Status))
            ImGui.SameLine();
        if (listingNavigation.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Return to retainer list"))
            _ = listingNavigation.ReturnToRetainerListAsync();
        if (listingNavigation.IsRunning)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.listings.return-to-list",
            "Return to retainer list",
            !listingNavigation.IsRunning,
            () => _ = listingNavigation.ReturnToRetainerListAsync(),
            listingNavigation.IsRunning ? listingNavigation.Status : "Ready");
    }

    private void DrawPhysicalRetainerLink(PhysicalListingGroupView listing)
    {
        if (listingNavigation.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{listing.RetainerName}##open-physical:{listing.RetainerId}:{listing.Quantity}:{listing.Quality}:{listing.UnitPrice}"))
            _ = listingNavigation.OpenRetainerListingsAsync(new(listing.RetainerId, listing.RetainerName));
        if (listingNavigation.IsRunning)
            ImGui.EndDisabled();
    }

    private DalamudTableProjection<ListingGroupView> CreateGroupTable() => new(
    [
        new("Item", 1.45f, row => $"{row.ItemName} {QualityLabel(row.Quality)}", row => row.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Desired", 62, DesiredText, row => row.DesiredUnits),
        new("Listed", 72, row => EvidenceText(row.ListedUnits), row => row.ListedUnits.IsKnown ? row.ListedUnits.Value : -1),
        new("Need", 72, NeedText, row => row.NeedUnits.IsKnown ? row.NeedUnits.Value : -1),
        new("Coverage", 132, CoverageText, row => CoverageText(row)),
        new("State", 118, StateText, row => StateText(row), TextColor: StateColor),
    ]);

    private DalamudTableProjection<ListingGroupView> CreateCompactGroupTable() => new(
    [
        new("Item", 1.45f, row => $"{row.ItemName} {QualityLabel(row.Quality)}", row => row.ItemName, ImGuiTableColumnFlags.WidthStretch),
        new("Need", 72, NeedText, row => row.NeedUnits.IsKnown ? row.NeedUnits.Value : -1),
        new("State", 118, StateText, row => StateText(row), TextColor: StateColor),
    ]);

    private DalamudTableProjection<PhysicalListingGroupView> CreatePhysicalTable() => new(
    [
        new("Retainer", 1f, row => row.RetainerName, row => row.RetainerName, ImGuiTableColumnFlags.WidthStretch, Draw: DrawPhysicalRetainerLink),
        new("Listings", 62, row => row.Listings.Count.ToString("N0"), row => row.Listings.Count),
        new("Qty each", 72, row => row.Quantity.ToString("N0"), row => row.Quantity),
        new("Quality", 78, row => row.Quality.ToString(), row => row.Quality),
        new("Unit price", 104, UnitPriceText, row => row.UnitPrice.IsKnown ? row.UnitPrice.Value : decimal.MinValue),
    ]);

    private static IReadOnlyList<PhysicalListingGroupView> GroupPhysicalListings(IReadOnlyList<ListingRow> listings) =>
        listings
            .GroupBy(listing => new
            {
                listing.RetainerId,
                listing.RetainerName,
                listing.Quantity,
                listing.Quality,
                PriceKnown = listing.UnitPrice.IsKnown,
                UnitPrice = listing.UnitPrice.IsKnown ? listing.UnitPrice.Value : decimal.Zero,
            })
            .Select(group => new PhysicalListingGroupView(
                group.Key.RetainerId,
                group.Key.RetainerName,
                group.Key.Quantity,
                group.Key.Quality,
                group.First().UnitPrice,
                group.OrderBy(listing => listing.SlotIndex).ToArray()))
            .OrderBy(group => group.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.UnitPrice.IsKnown ? group.UnitPrice.Value : decimal.MaxValue)
            .ThenBy(group => group.Quantity)
            .ToArray();

    private static string UnitPriceText(PhysicalListingGroupView listing) =>
        listing.UnitPrice.IsKnown ? $"{listing.UnitPrice.Value:N0} gil" : "Unknown";

    private static string EvidenceText(Franthropy.Filtering.Evaluation.FieldEvidence<int> value) =>
        value.IsKnown ? value.Value.ToString("N0") : "—";

    private static string DesiredText(ListingGroupView group) =>
        group.Assignments.Count == 0 ? "—" : group.DesiredUnits.ToString("N0");

    private static string NeedText(ListingGroupView group) =>
        group.Assignments.Count == 0 ? "—" : EvidenceText(group.NeedUnits);

    private static string CoverageText(ListingGroupView group)
    {
        if (group.Assignments.Count == 0)
            return "No assignment";
        if (group.Coverage == ListingCoverageState.Satisfied &&
            (group.Assignments.Any(assignment => assignment.UnknownPriceListings + assignment.WrongPriceListings +
                                                  assignment.WrongShapeListings + assignment.WrongRetainerListings > 0) ||
             group.UnmanagedListings.Count > 0))
            return "No stock deficit";
        return group.Coverage switch
        {
            ListingCoverageState.Satisfied => "Satisfied",
            ListingCoverageState.ReadyOnAssignedRetainer => $"{group.NeedUnits.Value:N0} on assigned retainer",
            ListingCoverageState.ReadyOnPlayer => $"{EvidenceText(group.MovementNeedUnits)} on player",
            ListingCoverageState.Retrievable => $"{group.RetrievableUnits.Value:N0} / {group.NeedUnits.Value:N0} retrievable",
            ListingCoverageState.Missing => $"{group.MissingUnits.Value:N0} missing",
            _ => "Unknown",
        };
    }

    private static string StateText(ListingGroupView group)
    {
        var price = group.Assignments.Sum(assignment => assignment.WrongPriceListings);
        var shape = group.Assignments.Sum(assignment => assignment.WrongShapeListings);
        var unknownPrice = group.Assignments.Sum(assignment => assignment.UnknownPriceListings);
        var wrongRetainer = group.Assignments.Sum(assignment => assignment.WrongRetainerListings);
        if (!group.ListedUnits.IsKnown)
            return "Listings unknown";
        if (group.NeedUnits.IsKnown && group.NeedUnits.Value > 0)
            return group.Coverage switch
            {
                ListingCoverageState.ReadyOnAssignedRetainer => $"List {group.NeedUnits.Value:N0}",
                ListingCoverageState.ReadyOnPlayer => $"Move {EvidenceText(group.MovementNeedUnits)} from player",
                ListingCoverageState.Retrievable => $"Retrieve {group.RetrievableUnits.Value:N0}",
                ListingCoverageState.Missing => $"Missing {group.MissingUnits.Value:N0}",
                _ => $"Need {group.NeedUnits.Value:N0} · source?",
            };
        if (price > 0)
            return $"{price:N0} wrong price";
        if (shape > 0)
            return $"{shape:N0} wrong shape";
        if (wrongRetainer > 0)
            return $"{wrongRetainer:N0} wrong retainer";
        if (unknownPrice > 0)
            return $"{unknownPrice:N0} price unknown";
        if (group.UnmanagedListings.Count > 0)
            return $"{group.UnmanagedListings.Count:N0} unplanned listing{(group.UnmanagedListings.Count == 1 ? string.Empty : "s")}";
        return group.Assignments.Count == 0 ? "Not planned" : "On plan";
    }

    private static Vector4? StateColor(ListingGroupView group)
    {
        var state = StateText(group);
        if (state == "On plan")
            return new Vector4(.45f, .78f, .58f, 1f);
        if (group.Assignments.Count == 0 && group.UnmanagedListings.Count > 0)
            return new Vector4(1f, .4f, .4f, 1f);
        if (!group.ListedUnits.IsKnown || state.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            return ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        return new Vector4(1f, .7f, .3f, 1f);
    }

    private static string VerdictText(ListingGroupView group)
    {
        var parts = new List<string> { StateText(group) };
        var price = group.Assignments.Sum(assignment => assignment.WrongPriceListings);
        var shape = group.Assignments.Sum(assignment => assignment.WrongShapeListings);
        var unknownPrice = group.Assignments.Sum(assignment => assignment.UnknownPriceListings);
        var wrongRetainer = group.Assignments.Sum(assignment => assignment.WrongRetainerListings);
        if (group.NeedUnits.IsKnown && group.NeedUnits.Value > 0)
        {
            if (price > 0)
                parts.Add($"{price:N0} wrong price");
            if (shape > 0)
                parts.Add($"{shape:N0} wrong shape");
            if (wrongRetainer > 0)
                parts.Add($"{wrongRetainer:N0} wrong retainer");
            if (unknownPrice > 0)
                parts.Add($"{unknownPrice:N0} price unknown");
        }
        return string.Join(" · ", parts.Distinct(StringComparer.Ordinal));
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };
}
