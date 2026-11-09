using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Inputs;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.FFXIV.Filtering;
using RQ.Automation;
using RQ.Domain;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record ListingAddChoice(uint ItemId, string ItemName, ItemQualityPolicy Quality);

internal sealed class ListingWorkspace
{
    private readonly WorkbenchState workbench;
    private readonly ListingNavigationCoordinator navigation;
    private readonly AgentBridgeUiReviewRegistry registry;
    private readonly ListingPlanDraftSession session;
    private readonly Func<string, int, IReadOnlyList<ItemChoice>> searchItems;
    private readonly Func<uint, bool?> listability;
    private readonly Action<QuartermasterRuntimeSnapshot, ListingPlanItemEvaluation, Guid?> prepare;
    private readonly Func<string> planName;
    private readonly Func<Guid?> planId;
    private Guid? pendingPlanId;
    private ListingItemKey? preparation;
    private bool preparationPending;
    private string? preparationError;
    private readonly RetainerRefreshCoordinator refresh;
    private readonly TableSelectionModel<ulong> selection = new();
    private readonly DalamudTableProjection<ListingRetainerRow> table;
    private readonly DalamudSuggestionInputState addInput = new();
    private readonly Dictionary<(Guid, string), string> buffers = [];
    private readonly Dictionary<Guid, int> priceAtActivation = [];
    private (long Runtime, int Draft) projectionKey = (-1, -1);
    private IReadOnlyList<ListingRetainerRow> projection = [];
    private IReadOnlySet<ulong> dirtyRetainers = new HashSet<ulong>();
    private string catalogQuery = "\0";
    private IReadOnlyList<ItemChoice> catalog = [];
    private (string Query, ulong Retainer, long Revision) choicesKey = ("\0", 0, -1);
    private IReadOnlyList<DalamudSuggestion<ListingAddChoice>> choices = [];
    private int lastFrame = -2;
    private ListingPlanDraft? previousDraft;
    private bool refreshStartedHere;

    public ListingWorkspace(WorkbenchState workbench, ListingNavigationCoordinator navigation,
        AgentBridgeUiReviewRegistry registry, ListingPlanDraftSession session,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems, Func<uint, bool?> listability,
        Action<QuartermasterRuntimeSnapshot, ListingPlanItemEvaluation, Guid?> prepare, Func<string> planName, Func<Guid?> planId, RetainerRefreshCoordinator refresh)
    {
        this.workbench = workbench; this.navigation = navigation; this.registry = registry; this.session = session;
        this.searchItems = searchItems; this.listability = listability; this.prepare = prepare; this.planName = planName; this.planId = planId; this.refresh = refresh;
        table = new([
            new("Retainer", 1, r => r.RetainerName + (dirtyRetainers.Contains(r.RetainerId) ? " *" : ""), Flags: ImGuiTableColumnFlags.WidthStretch),
            new("Slots", 132, _ => "", Draw: DrawSlots),
            new("State", 110, r => r.State.Text, TextColor: r => Color(r.State.Tone)),
        ], DalamudTableSelection<ListingRetainerRow>.Single(selection, r => r.RetainerId));
    }

    internal static Vector4 Color(ListingStateTone tone) => tone switch
    {
        ListingStateTone.Amber => new(1, .7f, .3f, 1), ListingStateTone.Green => new(.45f, .78f, .58f, 1),
        ListingStateTone.Info => new(.56f, .70f, .85f, 1), _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
    };

    private IReadOnlyList<ListingRetainerRow> Resolve(QuartermasterRuntimeSnapshot runtime)
    {
        if (session.Draft is null) return [];
        if (projectionKey != (runtime.Revision, session.Version))
        {
            projectionKey = (runtime.Revision, session.Version);
            var savedIds = ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner)?.Assignments.Where(a => a.Enabled).Select(a => a.ItemId).ToHashSet() ?? [];
            projection = ListingRetainerProjection.Build(session.Draft, runtime.Browser, session.CurrentRetainerIds(runtime),
                id => session.MaxStack(runtime.Browser, id), listability, savedIds);
            dirtyRetainers = session.DirtyRetainerIds();
        }
        return projection;
    }

    public void Focus(QuartermasterRuntimeSnapshot runtime, ListingItemKey key)
    {
        session.Ensure(runtime);
        var rows = Resolve(runtime);
        var target = rows.FirstOrDefault(r => r.PlanRows.Any(a => a.Assignment.ItemId == key.ItemId && a.Assignment.Quality == key.Quality))
            ?? rows.FirstOrDefault(r => runtime.Browser.GetListings(BrowserScope.RetainerKey(r.RetainerId)).Any(l => l.ItemId == key.ItemId && ListingRetainerProjection.Quality(l) == key.Quality));
        if (target is null) return;
        Select(target.RetainerId);
        workbench.ListingFilterState.SetExpression(string.Empty);
        workbench.HighlightedListingItem = key;
        workbench.ScrollToListingRetainer = true;
        workbench.ScrollToListingDetail = key;
    }

    private void Select(ulong? id)
    {
        workbench.SelectedListingRetainerId = id;
        preparation = null;
        preparationPending = false;
        preparationError = null;
        workbench.HighlightedListingItem = null;
        session.PriceEchoAssignmentId = null;
        buffers.Clear(); priceAtActivation.Clear();
        addInput.Text = string.Empty; addInput.Active = false;
        selection.Clear();
        if (id.HasValue) selection.SetSelected(id.Value, true);
    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        if (!runtime.Owner.HasStableIdentity || session.Draft is null) return;
        if (previousDraft is not null && !previousDraft.Owner.Matches(session.Draft.Owner))
        { preparation = null; preparationPending = false; preparationError = null; }
        if (lastFrame != ImGui.GetFrameCount() - 1 || !ReferenceEquals(previousDraft, session.Draft))
        { buffers.Clear(); priceAtActivation.Clear(); addInput.Active = false; }
        previousDraft = session.Draft;
        lastFrame = ImGui.GetFrameCount();
        // Name-only search applies equally to drafts and observed items.
        var search = workbench.ListingFilterState.Expression;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##ListingNameSearch", "Search listed or planned items", ref search, 160))
            workbench.ListingFilterState.SetExpression(search);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Search item names in both planned and observed listings.");
        var all = Resolve(runtime);
        DrawRefresh(all.Count == 0);
        if (all.Count == 0) { ImGui.TextDisabled("No retainers observed yet."); DrawNavigationStatus(); return; }
        var visible = all.Where(r => string.IsNullOrWhiteSpace(search) ||
            r.PlanRows.Any(a => a.Assignment.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
            runtime.Browser.GetListings(BrowserScope.RetainerKey(r.RetainerId)).Any(l => l.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!visible.Any(r => r.RetainerId == workbench.SelectedListingRetainerId)) Select(visible.FirstOrDefault()?.RetainerId);
        if (visible.Length == 0) { ImGui.TextDisabled($"No retainers match \"{search}\"."); return; }
        if (ImGui.GetContentRegionAvail().X < 900)
        {
            var selectedRetainer = visible.First(r => r.RetainerId == workbench.SelectedListingRetainerId);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##RQRetainer", selectedRetainer.RetainerName))
            {
                foreach (var row in visible)
                    if (ImGui.Selectable($"{row.RetainerName} · {row.PlannedSlots} planned", row.RetainerId == selectedRetainer.RetainerId)) Select(row.RetainerId);
                ImGui.EndCombo();
            }
            DrawDetail(runtime, visible.First(r => r.RetainerId == workbench.SelectedListingRetainerId));
            return;
        }
        if (!ImGui.BeginTable("RQRetainerListingLayout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV)) return;
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 350);
        ImGui.TableSetupColumn("Assignments", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        if (table.Begin("RQRetainerListingsV2", new DalamudTableLayout(new(0, ImGui.GetContentRegionAvail().Y),
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, FreezeRows: 1)))
        {
            for (var n = 0; n < visible.Length; n++)
            {
                var row = visible[n];
                if (table.DrawSaneRow(visible, n, $"retainer:{row.RetainerId}")) Select(row.RetainerId);
                if (row.RetainerId == workbench.SelectedListingRetainerId && workbench.ScrollToListingRetainer)
                { ImGui.SetScrollHereY(); workbench.ScrollToListingRetainer = false; }
            }
            table.End();
        }
        ImGui.TableNextColumn();
        if (visible.FirstOrDefault(r => r.RetainerId == workbench.SelectedListingRetainerId) is { } selected) DrawDetail(runtime, selected);
        ImGui.EndTable();
    }

    private void DrawRefresh(bool empty)
    {
        if (!empty && !refreshStartedHere && !refresh.CanCancel) return;
        var canStart = refresh.CanStart;
        ImGui.BeginDisabled(!canStart);
        if (ImGui.SmallButton("Refresh retainers##listings")) { refreshStartedHere = true; refresh.Start(); }
        ImGui.EndDisabled();
        if (refresh.CanCancel) { ImGui.SameLine(); if (ImGui.SmallButton("Cancel refresh##listings")) refresh.Cancel(); }
        if (!string.IsNullOrWhiteSpace(refresh.Status)) { ImGui.SameLine(); ImGui.TextDisabled(refresh.Status); }
    }

    private static void DrawSlots(ListingRetainerRow row)
    {
        foreach (var cell in row.Cells)
        {
            var color = cell.Kind switch
            {
                SlotCellKind.Listed => Color(ListingStateTone.Green), SlotCellKind.Differs => Color(ListingStateTone.Info),
                SlotCellKind.Planned => new Vector4(.48f, .35f, .14f, 1), SlotCellKind.PlannedUnknown => new(.48f, .35f, .14f, .5f),
                SlotCellKind.Unplanned => new(.36f, .36f, .36f, 1), _ => new(.15f, .15f, .15f, 1),
            };
            var pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(4, 12), ImGui.GetColorU32(color));
            ImGui.Dummy(new(4, 12));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(cell.Tooltip);
            ImGui.SameLine(0, 1);
        }
        ImGui.SameLine(0, 6);
        var total = row.PlannedSlots + row.UnplannedSlots;
        if (total > 20) ImGui.TextColored(Color(ListingStateTone.Amber), total.ToString("N0"));
        else ImGui.TextUnformatted(total.ToString("N0"));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{row.PlannedSlots} planned · {row.UnplannedSlots} listed off-plan");
    }

    private void DrawDetail(QuartermasterRuntimeSnapshot runtime, ListingRetainerRow retainer)
    {
        ImGui.TextUnformatted(retainer.RetainerName);
        if (retainer.Available)
        {
            var label = "Open in game";
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(label).X - ImGui.GetStyle().FramePadding.X * 2));
            ImGui.BeginDisabled(navigation.IsRunning);
            if (ImGui.SmallButton(label)) _ = navigation.OpenRetainerListingsAsync(new(retainer.RetainerId, retainer.RetainerName));
            ImGui.EndDisabled();
            registry.RegisterLastButton("quartermaster.listings.open-first", label, !navigation.IsRunning,
                () => _ = navigation.OpenRetainerListingsAsync(new(retainer.RetainerId, retainer.RetainerName)), navigation.IsRunning ? navigation.Status : "Ready");
        }
        DrawNavigationStatus();
        if (!ImGui.BeginChild("ListingDetail", Vector2.Zero, false)) { ImGui.EndChild(); return; }
        var detailLeft = ImGui.GetCursorScreenPos().X;
        var detailRight = detailLeft + ImGui.GetContentRegionAvail().X;
        if (ImGui.BeginTable("RQInlineListingAssignments", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##enabled", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Listed / planned", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableHeadersRow();
            foreach (var row in retainer.PlanRows)
            {
                var a = row.Assignment;
                ImGui.PushID(a.Id.ToString());
                BeginItemRow(new(a.ItemId, a.Quality));
                ImGui.TableNextColumn();
                var enabled = a.Enabled;
                ImGui.BeginDisabled(!enabled && session.Free(a.RetainerId, a.Id) == 0);
                if (ImGui.Checkbox("##enabled", ref enabled)) session.SetEnabled(a.Id, enabled, runtime.Browser);
                ImGui.EndDisabled();
                ImGui.TableNextColumn(); ItemLabel(a.ItemName, a.Quality);
                DrawState(runtime, new(a.ItemId, a.Quality), row.State);
                foreach (var exception in retainer.ObservedGroups.Where(g => g.AssignmentId == a.Id))
                {
                    ImGui.TextColored(Color(exception.State.Tone), exception.State.Text);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{exception.Listings.Count} observed · {exception.Quantity} each · " +
                        (exception.UnitPrice.IsKnown ? $"{exception.UnitPrice.Value:N0} gil" : "price unknown"));
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ListedCount is { } count ? $"{count:N0} /" : "— /");
                ImGui.SameLine();
                Numeric(a, "count", a.ListingCount, !retainer.Available || !a.Enabled, t => session.SetListingCount(a.Id, t));
                ImGui.TableNextColumn();
                if (row.MaxStack == 1 && a.Enabled) ImGui.TextUnformatted("1");
                else Numeric(a, "quantity", a.QuantityPerListing, !retainer.Available || !a.Enabled, t => session.SetQuantity(a.Id, t, runtime.Browser));
                ImGui.TableNextColumn(); Numeric(a, "price", a.UnitPrice, !retainer.Available || !a.Enabled, t => session.SetUnitPrice(a.Id, t));
                ImGui.TableNextColumn(); if (ImGui.SmallButton("×")) session.Remove(a.Id);
                if (session.PriceEchoAssignmentId == a.Id)
                {
                    var siblings = session.PriceSiblings(a.Id, session.CurrentRetainerIds(runtime));
                    if (siblings.Count > 0)
                    {
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(1);
                        var rowTop = ImGui.GetCursorScreenPos().Y;
                        ImGui.PushClipRect(new(detailLeft, rowTop), new(detailRight, rowTop + ImGui.GetFrameHeight()), false);
                        if (ImGui.SmallButton($"Apply {a.UnitPrice:N0} gil to {string.Join(", ", siblings.Select(s => s.RetainerName).Distinct())}"))
                            session.ApplyPriceToSiblings(a.Id, session.CurrentRetainerIds(runtime));
                        ImGui.PopClipRect();
                    }
                }
                ImGui.PopID();
            }
            var groupIndex = 0;
            foreach (var g in retainer.ObservedGroups.Where(g => g.Kind == ListingMatchKind.Unplanned))
            {
                ImGui.PushID($"observed:{groupIndex++}");
                BeginItemRow(new(g.ItemId, g.Quality));
                ImGui.TableNextColumn(); ImGui.TableNextColumn(); ItemLabel(g.ItemName, g.Quality);
                ImGui.TableNextColumn(); ImGui.TextDisabled(g.Listings.Count.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextDisabled(g.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextDisabled(g.UnitPrice.IsKnown ? g.UnitPrice.Value.ToString("N0") : "—");
                ImGui.TableNextColumn();
                if (g.Kind == ListingMatchKind.Unplanned && session.CanAdopt(g.Listings, session.CurrentRetainerIds(runtime)))
                { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(1); if (ImGui.SmallButton("Plan as listed")) session.Adopt(g.Listings, session.CurrentRetainerIds(runtime)); }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        DrawPreparation(runtime);
        if (retainer.Available && session.Free(retainer.RetainerId) > 0 && retainer.PlanRows.Count < 20)
        {
            ImGui.Indent(24 + ImGui.GetStyle().CellPadding.X * 2);
            if (addInput.Active) ResolveChoices(runtime, retainer.RetainerId);
            if (DalamudSuggestionInputRenderer.Draw("RQListingAdd", addInput, choices, 260, "Add item", 8, out var picked) && picked is not null)
                session.Add(retainer.RetainerId, retainer.RetainerName, picked.ItemId, picked.ItemName, picked.Quality, runtime.Browser);
            ImGui.Unindent(24 + ImGui.GetStyle().CellPadding.X * 2);
        }
        workbench.ScrollToListingDetail = null;
        ImGui.EndChild();
    }

    private void BeginItemRow(ListingItemKey key)
    {
        ImGui.TableNextRow();
        if (workbench.HighlightedListingItem == key) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(.16f, .2f, .13f, 1)));
        if (workbench.ScrollToListingDetail == key) { ImGui.SetScrollHereY(.25f); workbench.ScrollToListingDetail = null; }
    }
    private static void ItemLabel(string name, ItemQualityPolicy quality)
    {
        ImGui.TextUnformatted(name);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{name} {QualityLabel(quality)}");
        ImGui.SameLine(); ImGui.TextDisabled(QualityLabel(quality));
    }
    private void Numeric(ListingPlanAssignment a, string field, int value, bool readOnly, Action<string> update)
    {
        if (readOnly) { ImGui.TextDisabled(ListingPlanInput.Display(value, field == "price")); return; }
        var key = (a.Id, field);
        var text = buffers.GetValueOrDefault(key) ?? ListingPlanInput.Display(value, field == "price");
        if (field == "count" && session.FocusListingsAssignmentId == a.Id)
        { ImGui.SetScrollHereY(.5f); ImGui.SetKeyboardFocusHere(); session.FocusListingsAssignmentId = null; }
        ImGui.SetNextItemWidth(field == "count" ? 48 : -1);
        if (ImGui.InputText($"##{field}", ref text, 48, ImGuiInputTextFlags.AutoSelectAll))
        { buffers[key] = text; update(text); }
        if (ImGui.IsItemActivated())
        { if (field == "price") priceAtActivation[a.Id] = value; buffers[key] = text; }
        if (ImGui.IsItemDeactivated())
        {
            buffers.Remove(key);
            if (field == "price" && ImGui.IsItemDeactivatedAfterEdit()) session.EndUnitPriceEdit(a.Id, priceAtActivation.GetValueOrDefault(a.Id, value));
            priceAtActivation.Remove(a.Id);
        }
    }
    private void DrawState(QuartermasterRuntimeSnapshot runtime, ListingItemKey key, ListingStateView state)
    {
        if (state.Rank is >= 1 and <= 4)
        {
            if (ImGui.SmallButton(state.Text + " · Prepare")) preparation = key;
        }
        else if (state.Rank < 12) ImGui.TextColored(Color(state.Tone), state.Text);
    }
    private void DrawPreparation(QuartermasterRuntimeSnapshot runtime)
    {
        if (preparation is not { } key || session.Draft is not { } draft) return;
        var plan = new ListingPlan { Owner = draft.Owner, Assignments = draft.Assignments.Where(a => a.Enabled).ToList() };
        var demand = ListingPlanEvaluator.Evaluate(plan, runtime.Browser).Find(key.ItemId, key.Quality);
        if (demand is null) { preparation = null; return; }
        if (preparationPending && !refresh.IsRefreshing && !refresh.IsQueued)
        {
            preparationPending = false;
            if (demand.MovementNeedUnits.IsKnown) prepare(runtime, demand, pendingPlanId);
            else preparationError = "Retainer verification did not produce complete listing observations. Retry preparation or cancel.";
        }
        ImGui.Separator();
        ImGui.TextUnformatted($"Prepare {demand.ItemName} · {QualityLabel(key.Quality)}");
        ImGui.TextDisabled("Across planned retainers · uses current listing draft");
        if (demand.MovementNeedUnits.IsKnown)
        {
            ImGui.TextWrapped($"Player target {demand.MovementNeedUnits.Value:N0} · carried {demand.PlayerUnits:N0}");
            if (ImGui.Button($"Add target to {planName()}")) prepare(runtime, demand, planId());
            ImGui.TextWrapped("Adds an independent transfer target. Does not save or post listings.");
        }
        else if (preparationPending) ImGui.TextDisabled("Verifying retainer listings...");
        else if (ImGui.Button("Verify and prepare target"))
        {
            preparationError = null;
            if (refresh.CanStart) { pendingPlanId = planId(); preparationPending = true; refreshStartedHere = true; refresh.Start(); }
            else preparationError = "Retainer verification cannot start now. Finish the active operation, then retry preparation.";
        }
        if (preparationError is not null) ImGui.TextWrapped(preparationError);
        if (ImGui.SmallButton("Close details")) { preparation = null; preparationPending = false; }
        ImGui.Separator();
    }
    private void DrawNavigationStatus()
    {
        if (string.IsNullOrWhiteSpace(navigation.Status)) return;
        ImGui.TextDisabled(navigation.Status);
        if (!navigation.Status.StartsWith("Opened ", StringComparison.Ordinal)) return;
        ImGui.SameLine(); ImGui.BeginDisabled(navigation.IsRunning);
        if (ImGui.SmallButton("Return to retainer list")) _ = navigation.ReturnToRetainerListAsync();
        ImGui.EndDisabled();
        registry.RegisterLastButton("quartermaster.listings.return-to-list", "Return to retainer list", !navigation.IsRunning,
            () => _ = navigation.ReturnToRetainerListAsync(), navigation.IsRunning ? navigation.Status : "Ready");
    }

    private void ResolveChoices(QuartermasterRuntimeSnapshot runtime, ulong retainer)
    {
        var query = addInput.Text.Trim().ToLowerInvariant();
        if (catalogQuery != query) { catalogQuery = query; catalog = query.Length >= 2 ? searchItems(query, 64) : []; }
        if (choicesKey == (query, retainer, runtime.Revision)) return;
        choicesKey = (query, retainer, runtime.Revision);
        choices = BuildAddChoices(query, retainer, runtime.Browser, catalog, session.CanBeHq, listability);
    }

    internal static IReadOnlyList<DalamudSuggestion<ListingAddChoice>> BuildAddChoices(string query, ulong retainerId,
        BrowserProjection browser, IReadOnlyList<ItemChoice> catalog, Func<uint, bool> canBeHq, Func<uint, bool?> listability)
    {
        query = query.Trim();
        var exactId = query.StartsWith('#') && uint.TryParse(query.AsSpan(1), out var parsed) ? parsed : (uint?)null;
        bool Match(uint id, string name) => exactId.HasValue ? id == exactId : name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var owned = browser.Items.Where(i => listability(i.ItemId) is true && Match(i.ItemId, i.ItemName))
            .SelectMany(i => i.Stacks.GroupBy(s => s.Quality).Select(g => new
            { i.ItemId, i.ItemName, Quality = g.Key == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly,
                Here = g.Where(s => s.RetainerId == retainerId).Sum(s => s.Quantity), Owned = g.Sum(s => s.Quantity) }))
            .Where(i => i.Quality != ItemQualityPolicy.HqOnly || canBeHq(i.ItemId))
            .OrderByDescending(i => i.Here).ThenByDescending(i => i.Owned).ThenBy(i => i.ItemId)
            .Select(i => new DalamudSuggestion<ListingAddChoice>(new(i.ItemId, i.ItemName, i.Quality),
                $"{i.ItemName} {QualityLabel(i.Quality)}", i.Here > 0 ? $"{i.Here:N0} here" : $"{i.Owned:N0} owned")).ToList();
        var seen = owned.Select(i => (i.Value.ItemId, i.Value.Quality)).ToHashSet();
        if (query.Length >= 2)
            foreach (var i in catalog.Where(i => listability(i.ItemId) is true && Match(i.ItemId, i.Name)))
                foreach (var q in canBeHq(i.ItemId) ? new[] { ItemQualityPolicy.NqOnly, ItemQualityPolicy.HqOnly } : new[] { ItemQualityPolicy.NqOnly })
                    if (seen.Add((i.ItemId, q))) owned.Add(new(new(i.ItemId, i.Name, q), $"{i.Label} {QualityLabel(q)}"));
        var duplicateNames = owned.GroupBy(s => s.Value.ItemName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(s => s.Value.ItemId).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return owned.Select(s => duplicateNames.Contains(s.Value.ItemName)
            ? s with { Label = $"{s.Value.ItemName} · #{s.Value.ItemId} {QualityLabel(s.Value.Quality)}" } : s).Take(64).ToArray();
    }
    private static string QualityLabel(ItemQualityPolicy quality) => quality == ItemQualityPolicy.HqOnly ? "HQ" : "NQ";
}
