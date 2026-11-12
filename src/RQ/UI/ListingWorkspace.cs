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
    private const float RunPaneMinWidth = 240;
    private const float RetainerListMinWidth = 540;
    private const float SplitterWidth = 6;
    private const float SlotsColumnWidth = 140;
    private const float StateColumnWidth = 165;
    private readonly Func<uint, bool?> listability;
    private readonly RetainerRefreshCoordinator refresh;
    private readonly MarketRunPane runPane;
    private readonly DalamudTableProjection<ListingRetainerRow> retainerTable;
    private readonly Dictionary<ulong, DalamudSuggestionInputState> addInputs = [];
    internal RQ.Interop.ComplicatedMarketBoardItemClient? ItemClient { get; set; }
    private string? itemOpenStatus;
    private readonly Dictionary<(Guid, string), string> buffers = [];
    private readonly Dictionary<Guid, int> priceAtActivation = [];
    private (long Runtime, int Draft) projectionKey = (-1, -1);
    private IReadOnlyList<ListingRetainerRow> projection = [];
    private IReadOnlySet<ulong> dirtyRetainers = new HashSet<ulong>();
    private readonly Dictionary<string, IReadOnlyList<ItemChoice>> catalogs = [];
    private readonly Dictionary<ulong, (string Query, long Revision, IReadOnlyList<DalamudSuggestion<ListingAddChoice>> Values)> choiceCache = [];
    private IReadOnlyList<DalamudSuggestion<ListingAddChoice>> choices = [];
    // Shared navigation status is shown and registered only once across expanded details.
    private int navigationStatusFrame = -1;
    private ulong drawingRetainerId;
    private ListingPlanDraft? previousDraft;
    private bool refreshStartedHere;
    private float? runPaneWidth;

    public ListingWorkspace(WorkbenchState workbench, ListingNavigationCoordinator navigation,
        AgentBridgeUiReviewRegistry registry, ListingPlanDraftSession session,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems, Func<uint, bool?> listability,
        RetainerRefreshCoordinator refresh, MarketRunPane runPane)
    {
        this.workbench = workbench; this.navigation = navigation; this.registry = registry; this.session = session;
        this.searchItems = searchItems; this.listability = listability; this.refresh = refresh; this.runPane = runPane;
        retainerTable = new(
        [
            new("Retainer", 1, r => r.RetainerName, r => r.RetainerName,
                Flags: ImGuiTableColumnFlags.WidthStretch,
                Draw: r => ImGui.TextUnformatted($"{(workbench.IsListingExpanded(r.RetainerId) ? "v" : ">")} {r.RetainerName}{(dirtyRetainers.Contains(r.RetainerId) ? " *" : string.Empty)}"), Id: "retainer"),
            new("Slots", SlotsColumnWidth, r => (r.PlannedSlots + r.UnplannedSlots).ToString(),
                r => r.PlannedSlots + r.UnplannedSlots, Draw: DrawSlots, Id: "slots", SelectionTargetFraction: 1),
            new("State", StateColumnWidth, r => r.State.Text, r => r.State.Rank,
                Draw: r => DrawState(r.State), Id: "state", SelectionTargetFraction: 1),
        ], DalamudTableSelection<ListingRetainerRow>.Single(
            r => r.RetainerId == workbench.SelectedListingRetainerId,
            r => workbench.ToggleListing(r.RetainerId)));
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
        workbench.EnsureListingOwner(runtime.Owner);
        var rows = Resolve(runtime);
        var target = rows.FirstOrDefault(r => r.PlanRows.Any(a => a.Assignment.ItemId == key.ItemId && a.Assignment.Quality == key.Quality))
            ?? rows.FirstOrDefault(r => runtime.Browser.GetListings(BrowserScope.RetainerKey(r.RetainerId)).Any(l => l.ItemId == key.ItemId && ListingRetainerProjection.Quality(l) == key.Quality));
        if (target is null) return;
        Select(target.RetainerId);
        workbench.ExpandListing(target.RetainerId);
        workbench.ListingFilterState.SetExpression(string.Empty);
        workbench.HighlightedListingItem = key;
        workbench.ScrollToListingRetainer = true;
        workbench.ScrollToListingDetail = key;
    }

    private void Select(ulong? id)
    {
        workbench.SelectedListingRetainerId = id;
        workbench.HighlightedListingItem = null;
    }

    public unsafe void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        workbench.EnsureListingOwner(runtime.Owner);
        if (!runtime.Owner.HasStableIdentity || session.Draft is null) return;
        if (!ReferenceEquals(previousDraft, session.Draft))
        { buffers.Clear(); priceAtActivation.Clear(); addInputs.Clear(); catalogs.Clear(); choiceCache.Clear(); itemOpenStatus = null; }
        previousDraft = session.Draft;
        if (itemOpenStatus is not null) ImGui.TextWrapped(itemOpenStatus);
        // Name-only search applies equally to drafts and observed items.
        var search = workbench.ListingFilterState.Expression;
        const string replanAllLabel = "Replan all as listed";
        var replanWidth = ImGui.CalcTextSize(replanAllLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        var searchWidth = ImGui.GetContentRegionAvail().X - replanWidth - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(searchWidth >= 160 ? searchWidth : -1);
        if (ImGui.InputTextWithHint("##ListingNameSearch", "Search listed or planned items", ref search, 160))
            workbench.ListingFilterState.SetExpression(search);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Search item names in both planned and observed listings.");
        UiLayout.SameLineIfFits(replanAllLabel);
        var currentRetainerIds = session.CurrentRetainerIds(runtime);
        ImGui.BeginDisabled(!session.CanReplanAll(runtime.Browser, currentRetainerIds));
        if (ImGui.SmallButton(replanAllLabel)) session.ReplanAll(runtime.Browser, currentRetainerIds);
        ImGui.EndDisabled();
        var all = Resolve(runtime);
        DrawRefresh(all.Count == 0);
        retainerTable.DrawColumnMenuButton("RQMarketRetainerColumns");
        var visible = all.Where(r => string.IsNullOrWhiteSpace(search) ||
            r.PlanRows.Any(a => a.Assignment.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
            runtime.Browser.GetListings(BrowserScope.RetainerKey(r.RetainerId)).Any(l => l.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Filtering hides rows, not intent: neither focus nor expansions follow display indexes.

        var avail = ImGui.GetContentRegionAvail();
        var height = Math.Max(1, avail.Y);
        var maxRunWidth = Math.Max(RunPaneMinWidth, avail.X - RetainerListMinWidth - SplitterWidth);
        var runWidth = Math.Clamp(runPaneWidth ?? avail.X * .38f, RunPaneMinWidth, maxRunWidth);
        var listWidth = Math.Max(1, avail.X - runWidth - SplitterWidth);

        if (ImGui.BeginChild("RQMarketRetainers", new Vector2(listWidth, height), false))
        {
            if (all.Count == 0) { ImGui.TextDisabled("No retainers observed yet."); DrawNavigationStatus(); }
            else if (visible.Length == 0) ImGui.TextDisabled($"No retainers match \"{search}\".");
            if (visible.Length > 0 && retainerTable.Begin("RQMarketRetainerTable",
                DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)))
            {
                try
                {
                    var ordered = retainerTable.Apply(visible, ImGui.TableGetSortSpecs());
                    for (var index = 0; index < ordered.Count; index++)
                    {
                        var row = ordered[index];
                        retainerTable.DrawSaneRow(ordered, index, $"retainer:{row.RetainerId}");
                        if (!workbench.IsListingExpanded(row.RetainerId)) continue;
                        if (row.RetainerId == workbench.SelectedListingRetainerId && workbench.ScrollToListingRetainer)
                        { ImGui.SetScrollHereY(.1f); workbench.ScrollToListingRetainer = false; }
                        retainerTable.DrawDetailRow($"retainer:{row.RetainerId}", () => DrawDetail(runtime, row));
                    }
                }
                finally { retainerTable.End(); }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGui.InvisibleButton("##RQMarketSplitter", new Vector2(SplitterWidth, height));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive()) runPaneWidth = Math.Clamp(runWidth - ImGui.GetIO().MouseDelta.X, RunPaneMinWidth, maxRunWidth);
        var splitterMin = ImGui.GetItemRectMin();
        var splitterMax = ImGui.GetItemRectMax();
        var splitterX = (splitterMin.X + splitterMax.X) / 2;
        ImGui.GetWindowDrawList().AddLine(new Vector2(splitterX, splitterMin.Y), new Vector2(splitterX, splitterMax.Y),
            ImGui.GetColorU32(ImGuiCol.Separator), 1);

        ImGui.SameLine(0, 0);
        if (ImGui.BeginChild("RQMarketRun", new Vector2(runWidth, height), false))
            runPane.Draw(runtime, session, all, workbench.SelectedListingRetainerId);
        ImGui.EndChild();
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
        drawingRetainerId = retainer.RetainerId;
        if (retainer.Available)
        {
            var label = "Open in game";
            ImGui.BeginDisabled(navigation.IsRunning);
            if (ImGui.SmallButton(label)) _ = navigation.OpenRetainerListingsAsync(new(retainer.RetainerId, retainer.RetainerName));
            ImGui.EndDisabled();
            if (retainer.RetainerId == workbench.SelectedListingRetainerId)
                registry.RegisterLastButton("quartermaster.listings.open-first", label, !navigation.IsRunning,
                    () => _ = navigation.OpenRetainerListingsAsync(new(retainer.RetainerId, retainer.RetainerName)), navigation.IsRunning ? navigation.Status : "Ready");
        }
        if (retainer.ListingsObserved)
        {
            const string label = "Replan as listed";
            if (retainer.Available) UiLayout.SameLineIfFits(label);
            var currentRetainerIds = session.CurrentRetainerIds(runtime);
            ImGui.BeginDisabled(!session.CanReplan(retainer.RetainerId, runtime.Browser, currentRetainerIds));
            if (ImGui.SmallButton(label)) session.Replan(retainer.RetainerId, runtime.Browser, currentRetainerIds);
            ImGui.EndDisabled();
        }
        DrawNavigationStatus();
        var runRetainer = runPane.Retainer(runtime, session, retainer.RetainerId);
        if (runRetainer is not null)
        {
            var capacity = MarketRunPresentation.Capacity(runRetainer.PullListings, runRetainer.BlockedBySlotsListings);
            if (capacity.Length > 0) ImGui.TextUnformatted(capacity[3..]);
        }
        if (ImGui.BeginTable("RQInlineListingAssignments", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##enabled", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Listed / planned", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Units/listing", ImGuiTableColumnFlags.WidthFixed, 86);
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
                ImGui.TableNextColumn(); ItemLabel(a.ItemId, a.ItemName, a.Quality);
                DrawState(row.State);
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
                        // The same shared span mechanics apply inside the nested assignment table.
                        retainerTable.DrawDetailRow($"price-echo:{a.Id}", () =>
                        {
                            if (ImGui.SmallButton($"Apply {a.UnitPrice:N0} gil to {string.Join(", ", siblings.Select(s => s.RetainerName).Distinct())}"))
                                session.ApplyPriceToSiblings(a.Id, session.CurrentRetainerIds(runtime));
                        });
                    }
                }
                ImGui.PopID();
            }
            var groupIndex = 0;
            // A listing the run will pull is shown once: as the pulled part of its unplanned
            // group when it has one, otherwise as a row of its own.
            var pulls = runRetainer?.ListingsToPull ?? [];
            var grouped = new HashSet<ListingRow>(ReferenceEqualityComparer.Instance);
            void PullRow(MarketRunPullListing pull, int count)
            {
                ImGui.PushID($"pull:{pull.Listing.SlotIndex}");
                BeginItemRow(new(pull.Listing.ItemId, pull.Listing.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ
                    ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly));
                ImGui.TableNextColumn(); ImGui.TableNextColumn();
                ItemLabel(pull.Listing.ItemId, pull.Listing.ItemName, pull.Listing.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly);
                ImGui.TextDisabled(MarketRunPresentation.PullLabel(pull));
                ImGui.TableNextColumn(); ImGui.TextDisabled(count.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextDisabled(pull.Listing.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextDisabled(MarketRunPresentation.PullPrice(pull));
                ImGui.TableNextColumn();
                ImGui.PopID();
            }
            foreach (var g in retainer.ObservedGroups.Where(g => g.Kind == ListingMatchKind.Unplanned))
            {
                ImGui.PushID($"observed:{groupIndex++}");
                var groupPulls = MarketRunPresentation.PullsIn(g.Listings, pulls);
                foreach (var pull in groupPulls) grouped.Add(pull.Listing);
                if (groupPulls.Count > 0) PullRow(groupPulls[0], groupPulls.Count);
                var kept = g.Listings.Count - groupPulls.Count;
                if (kept > 0)
                {
                    BeginItemRow(new(g.ItemId, g.Quality));
                    ImGui.TableNextColumn(); ImGui.TableNextColumn(); ItemLabel(g.ItemId, g.ItemName, g.Quality);
                    ImGui.TableNextColumn(); ImGui.TextDisabled(kept.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextDisabled(g.Quantity.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextDisabled(g.UnitPrice.IsKnown ? g.UnitPrice.Value.ToString("N0") : "—");
                    ImGui.TableNextColumn();
                }
                if (session.CanAdopt(g.Listings, session.CurrentRetainerIds(runtime)))
                { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(1); if (ImGui.SmallButton("Plan as listed")) session.Adopt(g.Listings, session.CurrentRetainerIds(runtime)); }
                ImGui.PopID();
            }
            foreach (var pull in pulls.Where(pull => !grouped.Contains(pull.Listing)))
                PullRow(pull, 1);
            ImGui.EndTable();
        }
        if (retainer.Available && session.Free(retainer.RetainerId) > 0 && retainer.PlanRows.Count < 20)
        {
            if (!addInputs.TryGetValue(retainer.RetainerId, out var addInput)) addInputs[retainer.RetainerId] = addInput = new();
            ImGui.Indent(24 + ImGui.GetStyle().CellPadding.X * 2);
            ResolveChoices(runtime, retainer.RetainerId, addInput);
            if (DalamudSuggestionInputRenderer.Draw("RQListingAdd", addInput, choices, 260, "Add item", 8, out var picked) && picked is not null)
                session.Add(retainer.RetainerId, retainer.RetainerName, picked.ItemId, picked.ItemName, picked.Quality, runtime.Browser);
            ImGui.Unindent(24 + ImGui.GetStyle().CellPadding.X * 2);
        }
        if (retainer.RetainerId == workbench.SelectedListingRetainerId) workbench.ScrollToListingDetail = null;
    }

    private void BeginItemRow(ListingItemKey key)
    {
        ImGui.TableNextRow();
        if (workbench.HighlightedListingItem == key) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(.16f, .2f, .13f, 1)));
        if (drawingRetainerId == workbench.SelectedListingRetainerId && workbench.ScrollToListingDetail == key)
        { ImGui.SetScrollHereY(.25f); workbench.ScrollToListingDetail = null; }
    }
    private void ItemLabel(uint itemId, string name, ItemQualityPolicy quality)
    {
        if (ImGui.Selectable(name + "###cmb-item", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(name).X, 0)))
            itemOpenStatus = ItemClient?.Open(itemId) ?? "CMB item search unavailable: item-open integration is not configured.";
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{name} {QualityLabel(quality)} · Open item search in CMB (requires item-open provider). No undercut assessment.");
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
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(field switch
        {
            "count" => "Planned listing slots. The observed count to the left comes from retainer listing observations.",
            "quantity" => "Planned item units per listing, not total inventory or listing slots.",
            _ => "Planned gil per item unit. This is your plan price, not a competitor or undercut assessment.",
        });
        if (ImGui.IsItemActivated())
        { if (field == "price") priceAtActivation[a.Id] = value; buffers[key] = text; }
        if (ImGui.IsItemDeactivated())
        {
            buffers.Remove(key);
            if (field == "price" && ImGui.IsItemDeactivatedAfterEdit()) session.EndUnitPriceEdit(a.Id, priceAtActivation.GetValueOrDefault(a.Id, value));
            priceAtActivation.Remove(a.Id);
        }
    }
    private static void DrawState(ListingStateView state)
    {
        ImGui.TextColored(Color(state.Tone), state.Text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ListingRetainerProjection.Tooltip(state));
    }
    private void DrawNavigationStatus()
    {
        if (string.IsNullOrWhiteSpace(navigation.Status) || navigationStatusFrame == ImGui.GetFrameCount()) return;
        navigationStatusFrame = ImGui.GetFrameCount();
        ImGui.TextDisabled(navigation.Status);
        if (!navigation.Status.StartsWith("Opened ", StringComparison.Ordinal)) return;
        ImGui.SameLine(); ImGui.BeginDisabled(navigation.IsRunning);
        if (ImGui.SmallButton("Return to retainer list")) _ = navigation.ReturnToRetainerListAsync();
        ImGui.EndDisabled();
        registry.RegisterLastButton("quartermaster.listings.return-to-list", "Return to retainer list", !navigation.IsRunning,
            () => _ = navigation.ReturnToRetainerListAsync(), navigation.IsRunning ? navigation.Status : "Ready");
    }

    private void ResolveChoices(QuartermasterRuntimeSnapshot runtime, ulong retainer, DalamudSuggestionInputState addInput)
    {
        var query = addInput.Text.Trim().ToLowerInvariant();
        if (choiceCache.TryGetValue(retainer, out var cached) && cached.Query == query && cached.Revision == runtime.Revision)
        { choices = cached.Values; return; }
        if (!catalogs.TryGetValue(query, out var catalog))
        {
            // Keep transient search caching bounded while avoiding cross-retainer thrashing.
            if (catalogs.Count >= 32) catalogs.Clear();
            catalogs[query] = catalog = query.Length >= 2 ? searchItems(query, 64) : [];
        }
        choices = BuildAddChoices(query, retainer, runtime.Browser, catalog, session.CanBeHq, listability);
        choiceCache[retainer] = (query, runtime.Revision, choices);
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
