using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Inputs;
using Franthropy.Dalamud.UI.Layout;
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
    private const float RunPaneDefaultFraction = .40f;
    private const float RetainerListMinWidth = 540;
    private const float SplitterWidth = 6;
    private const float SlotsColumnWidth = 140;
    private const float StateColumnWidth = 165;
    private const float BulkUnitsWidth = 46;
    private const float BulkPriceWidth = 84;
    private const float BulkItemMinWidth = 120;
    private readonly Func<uint, bool?> listability;
    private readonly RetainerRefreshCoordinator refresh;
    private readonly MarketRunPane runPane;
    private readonly PlacementMatrix<(uint ItemId, ItemQualityPolicy Quality)> placement;
    private bool placementView;
    private readonly Dalamud.Plugin.Services.IPluginLog log;
    private bool openFirstRegistered;
    private string? duplicateRowsWarned;
    private readonly DalamudTableProjection<ListingRetainerRow> retainerTable;
    private readonly DalamudTableProjection<ListingSlotView> slotTable;
    private readonly DalamudTableProjection<ListingSlotView> slotsWithoutMarket;
    internal RQ.Interop.ComplicatedMarketBoardItemClient? ItemClient { get; set; }
    private string? itemOpenStatus;
    private (long Planning, int Draft) projectionKey = (-1, -1);
    private IReadOnlyList<ListingRetainerRow> projection = [];
    private IReadOnlySet<ulong> dirtyRetainers = new HashSet<ulong>();
    private readonly Dictionary<string, IReadOnlyList<ItemChoice>> catalogs = [];
    private readonly Dictionary<ulong, (string Query, long Revision, IReadOnlyList<DalamudSuggestion<ListingAddChoice>> Values)> choiceCache = [];
    private IReadOnlyList<DalamudSuggestion<ListingAddChoice>> choices = [];
    private ulong drawingRetainerId;
    private IReadOnlyList<ListingSlotView> drawingSlots = [];
    private IReadOnlyList<MarketRunPullListing> drawingPulls = [];
    private ListingPlanDraft? previousDraft;
    private bool refreshStartedHere;
    private float? runPaneWidth;
    private Dictionary<ulong, int> toList = [];
    // Slot selection belongs to one expanded retainer at a time and is keyed by stable slot identity.
    private readonly TableSelectionModel<ListingSlotKey> slotSelection = new();
    private ulong? slotRetainerId;
    private readonly DalamudSuggestionInputState bulkItemInput = new();
    private string? bulkUnits;
    private string? bulkPrice;
    internal ListingMarketView? Market { get; set; }
    private void OpenItem(uint id) => itemOpenStatus = ItemClient?.Open(id);

    public ListingWorkspace(WorkbenchState workbench, ListingNavigationCoordinator navigation,
        AgentBridgeUiReviewRegistry registry, ListingPlanDraftSession session,
        Func<string, int, IReadOnlyList<ItemChoice>> searchItems, Func<uint, bool?> listability,
        RetainerRefreshCoordinator refresh, MarketRunPane runPane, Dalamud.Plugin.Services.IPluginLog log)
    {
        this.log = log;
        this.workbench = workbench; this.navigation = navigation; this.registry = registry; this.session = session;
        this.searchItems = searchItems; this.listability = listability; this.refresh = refresh; this.runPane = runPane;
        placement = new("quartermaster.listings.placement", "Spare", registry);
        runPane.OpenItem = OpenItem;
        runPane.OpenPlacement = OpenPlacement;
        runPane.Registry = registry;
        retainerTable = new(
        [
            new("Retainer", 1, r => r.RetainerName, r => r.RetainerName,
                Flags: ImGuiTableColumnFlags.WidthStretch,
                Draw: r => ImGui.TextUnformatted($"{(workbench.IsListingExpanded(r.RetainerId) ? "v" : ">")} {r.RetainerName}{(dirtyRetainers.Contains(r.RetainerId) ? " *" : string.Empty)}"), Id: "retainer"),
            new("Slots", SlotsColumnWidth, r => (r.PlannedSlots + r.UnplannedSlots).ToString(),
                r => r.PlannedSlots + r.UnplannedSlots, Draw: DrawSlots, Id: "slots", SelectionTargetFraction: 1),
            // No row target here: the open-in-game icon sits in this cell.
            // Never hideable: the open-in-game icon and its bridge control are drawn here.
            new("State", StateColumnWidth, r => r.State.Text, r => r.State.Rank,
                Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide,
                Draw: DrawRetainerState, Id: "state"),
            new("Market", 145, r => Market?.Summary(r)?.Text ?? "", r => Market?.Summary(r)?.Sort ?? (4, 0, 0m),
                Draw: r => Market?.DrawSummary(r, () => workbench.ExpandListing(r.RetainerId)), Id: "market"),
        ], DalamudTableSelection<ListingRetainerRow>.Single(
            r => r.RetainerId == workbench.SelectedListingRetainerId,
            r => workbench.ToggleListing(r.RetainerId)));
        DalamudTableColumn<ListingSlotView>[] slotColumns =
        [
            new("##select", 22, _ => string.Empty, Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHeaderLabel,
                Draw: DrawSlotCheck, Id: "select"),
            new("#", 24, s => s.Number.ToString(), Draw: s => SlotCell(s, () => RightText(s.Number > 0 ? s.Number.ToString() : string.Empty, true)),
                Id: "number", SelectionTargetFraction: 1),
            new("Planned", 1, s => s.Assignment?.ItemName ?? "Free", Flags: ImGuiTableColumnFlags.WidthStretch,
                Draw: s => SlotCell(s, () => DrawSlotItem(s)), Id: "planned", SelectionTargetFraction: 1),
            new("Units", 42, s => s.Assignment?.QuantityPerListing.ToString() ?? string.Empty,
                Draw: s => SlotCell(s, () => RightText(s.Assignment is { } a ? ListingPlanInput.Display(a.QuantityPerListing, false) : string.Empty, Dim(s))),
                Id: "units", SelectionTargetFraction: 1),
            new("Unit price", 84, s => s.Assignment?.UnitPrice.ToString() ?? string.Empty,
                Draw: s => SlotCell(s, () => RightText(s.Assignment is { } a ? ListingPlanInput.Display(a.UnitPrice, true) : string.Empty, Dim(s))),
                Id: "price", SelectionTargetFraction: 1),
            new("Listed", 110, s => s.Listed.ToString(), Draw: s => SlotCell(s, () => DrawSlotListed(s)),
                Id: "listed", SelectionTargetFraction: 1),
            new("Market", 145, s => Market?.Slot(s)?.Text ?? "", Draw: s => Market?.DrawSlot(s), Id: "market"),
        ];
        var selection = DalamudTableSelection<ListingSlotView>.Single(
            s => slotRetainerId == drawingRetainerId && slotSelection.IsSelected(s.Key), ClickSlot);
        slotTable = new(slotColumns, selection);
        slotsWithoutMarket = new(slotColumns[..^1], selection);
    }

    private static Vector4 Color(ListingStateTone tone) => QuartermasterPalette.Tone(tone);

    private IReadOnlyList<ListingRetainerRow> Resolve(QuartermasterRuntimeSnapshot runtime)
    {
        if (session.Draft is null) return [];
        if (projectionKey != (runtime.PlanningRevision, session.Version))
        {
            var savedIds = ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner)?.Assignments.Where(a => a.Enabled).Select(a => a.ItemId).ToHashSet() ?? [];
            projection = ListingRetainerProjection.Build(session.Draft, runtime.Browser, session.CurrentRetainerIds(runtime),
                listability, savedIds);
            dirtyRetainers = session.DirtyRetainerIds();
            projectionKey = (runtime.PlanningRevision, session.Version);
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

    public unsafe void Draw(QuartermasterRuntimeSnapshot runtime, FrameTimings timings)
    {
        workbench.EnsureListingOwner(runtime.Owner);
        if (!runtime.Owner.HasStableIdentity || session.Draft is null) return;
        if (!ReferenceEquals(previousDraft, session.Draft))
        { catalogs.Clear(); choiceCache.Clear(); itemOpenStatus = null; ResetSlots(null); }
        previousDraft = session.Draft;
        // Expansion moving to another retainer, or collapsing this one, ends the slot selection.
        if (slotRetainerId is { } slotRetainer && (!workbench.IsListingExpanded(slotRetainer) || workbench.SelectedListingRetainerId != slotRetainer))
            ResetSlots(null);
        if (itemOpenStatus is not null) ImGui.TextWrapped(itemOpenStatus);
        // Name-only search applies equally to drafts and observed items.
        var search = workbench.ListingFilterState.Expression;
        const string replanAllLabel = "Replan all as listed";
        var replanWidth = DalamudUiLayout.ButtonWidth(replanAllLabel);
        var searchWidth = ImGui.GetContentRegionAvail().X - replanWidth - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(searchWidth >= 160 ? searchWidth : -1);
        if (ImGui.InputTextWithHint("##ListingNameSearch", "Search listed or planned items", ref search, 160))
            workbench.ListingFilterState.SetExpression(search);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Search item names in both planned and observed listings.");
        DalamudUiLayout.SameLineIfFits(replanAllLabel);
        var currentRetainerIds = session.CurrentRetainerIds(runtime);
        ImGui.BeginDisabled(!session.CanReplanAll(runtime.Browser, currentRetainerIds));
        if (ImGui.SmallButton(replanAllLabel)) session.ReplanAll(runtime.Browser, currentRetainerIds);
        ImGui.EndDisabled();
        timings.Section("listing projection");
        var all = Resolve(runtime);
        // Listings this run will post per retainer, drawn in the run pane's blue on the slot bar and State.
        toList = all.ToDictionary(r => r.RetainerId, r =>
            runPane.Retainer(runtime, session, r.RetainerId)?.RefillListings is { IsKnown: true, Value: > 0 } refill ? refill.Value : 0);
        timings.Section("listing table");
        DrawRefresh(all.Count == 0);
        DrawNavigationStatus();
        if (placementView)
        {
            timings.Section("listing placement");
            DrawPlacement(runtime, search);
            return;
        }
        retainerTable.DrawColumnMenuButton("RQMarketRetainerColumns");
        Market?.DrawToolbar(runtime, all);
        var visible = all.Where(r => string.IsNullOrWhiteSpace(search) ||
            r.PlanRows.Any(a => a.Assignment.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
            runtime.Browser.GetListings(BrowserScope.RetainerKey(r.RetainerId)).Any(l => l.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Filtering hides rows, not intent: neither focus nor expansions follow display indexes.

        var avail = ImGui.GetContentRegionAvail();
        var height = Math.Max(1, avail.Y);
        var maxRunWidth = Math.Max(RunPaneMinWidth, avail.X - RetainerListMinWidth - SplitterWidth);
        var runWidth = Math.Clamp(runPaneWidth ?? avail.X * RunPaneDefaultFraction, RunPaneMinWidth, maxRunWidth);
        var listWidth = Math.Max(1, avail.X - runWidth - SplitterWidth);

        if (ImGui.BeginChild("RQMarketRetainers", new Vector2(listWidth, height), false))
        {
            if (all.Count == 0) ImGui.TextDisabled("No retainers observed yet.");
            else if (visible.Length == 0) ImGui.TextDisabled($"No retainers match \"{search}\".");
            if (visible.Length > 0 && retainerTable.Begin("RQMarketRetainerTable",
                DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)))
            {
                try
                {
                    var ordered = DistinctRetainers(retainerTable.Apply(visible, ImGui.TableGetSortSpecs()));
                    openFirstRegistered = false;
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
        timings.Section("market run pane");
        if (ImGui.BeginChild("RQMarketRun", new Vector2(runWidth, height), false))
            runPane.Draw(runtime, session, all, workbench.SelectedListingRetainerId);
        ImGui.EndChild();
    }

    /// <summary>Swaps the retainer list for the placement matrix; an item name filters it to that item.</summary>
    private void OpenPlacement(string? itemName)
    {
        placementView = true;
        placement.ClearSelection();
        if (itemName is not null) workbench.ListingFilterState.SetExpression(itemName);
    }

    private void DrawPlacement(QuartermasterRuntimeSnapshot runtime, string search)
    {
        if (ImGui.SmallButton("Return to retainer list##RQPlacementReturn")) placementView = false;
        registry.RegisterLastButton("quartermaster.listings.placement.return", "Return to retainer list", true,
            () => placementView = false, "Ready");
        if (session.Draft is not { } draft) return;
        var items = runPane.Items(runtime, session);
        var owner = runtime.Owner;
        var rows = items.Select(item =>
        {
            var reserve = draft.Reserves.FirstOrDefault(r => r.ItemId == item.ItemId && r.Quality == item.Quality);
            var homes = reserve?.Homes ?? [];
            var (routing, ranks, caps) = PlacementMatrixData.Homes(homes, owner);
            return new PlacementMatrixRow<(uint, ItemQualityPolicy)>(
                (item.ItemId, item.Quality),
                $"{item.ItemId}-{item.Quality}",
                $"{item.ItemName} {TransferPresentation.QualityLabel(item.Quality, anyLabel: "NQ")}",
                MarketRunPresentation.Number(item.SpareUnits),
                routing,
                PlacementMatrixData.Holdings(runtime, item.ItemId, item.Quality))
            {
                Ranks = ranks,
                Caps = caps,
                NextRank = homes.Count + 1,
                // Homed only on another character: it goes there, not nowhere.
                Elsewhere = ranks.Count == 0 && homes.Count > 0 ? PlacementMatrixData.HomesSummary(runtime, homes, owner) : null,
            };
        }).ToArray();
        var visible = rows.Where(row => string.IsNullOrWhiteSpace(search) ||
            row.Label.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var columns = PlacementMatrixData.Columns(runtime, rows.Select(row => row.Routing));
        placement.DrawHomes(rows, visible, columns, (operation, retainerId, cap, keys) =>
        {
            foreach (var key in keys)
            {
                var item = items.FirstOrDefault(i => i.ItemId == key.ItemId && i.Quality == key.Quality);
                if (item is null) continue;
                var existing = draft.Reserves.FirstOrDefault(r => r.ItemId == key.ItemId && r.Quality == key.Quality);
                var holdings = PlacementMatrixData.Holdings(runtime, item.ItemId, item.Quality)
                    .Select(entry => new PlacementHolding(owner, entry.Key, entry.Value));
                var next = ReservePlacement.Operate(operation, existing?.Homes ?? [], owner, retainerId, cap, holdings);
                if (ReservePlacement.Same(existing?.Homes, next)) continue;
                // Placing an item without a reserve rule creates the rule, as typing a level does.
                var reserve = existing ?? session.AddReserve(item.ItemId, item.ItemName, item.Quality);
                session.SetReserveHomes(reserve.Id, next);
            }
            return true;
        });
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

    private void DrawSlots(ListingRetainerRow row)
    {
        var listing = toList.GetValueOrDefault(row.RetainerId);
        foreach (var slot in row.Slots.Where(s => s.Number > 0))
        {
            var unlisted = slot.Assignment is not null && slot.Listed is ListingSlotListed.NotListed or ListingSlotListed.None;
            var (color, label) = unlisted && listing-- > 0 ? (Color(ListingStateTone.Run), "to list") : slot.Listed switch
            {
                ListingSlotListed.Match => (Color(ListingStateTone.Green), "listed"),
                ListingSlotListed.UnknownPrice or ListingSlotListed.PriceDiffers or ListingSlotListed.ShapeDiffers => (Color(ListingStateTone.Info), "differs"),
                ListingSlotListed.Other when slot.Assignment is not null => (Color(ListingStateTone.Info), "differs"),
                ListingSlotListed.Other => (new Vector4(.36f, .36f, .36f, 1), "unplanned"),
                ListingSlotListed.NotListed => (new Vector4(.48f, .35f, .14f, 1), "planned"),
                _ when slot.Assignment is not null => (new Vector4(.48f, .35f, .14f, row.ListingsObserved ? 1 : .5f), "planned"),
                _ => (new Vector4(.15f, .15f, .15f, 1), string.Empty),
            };
            var name = slot.Listed == ListingSlotListed.Other && slot.Assignment is null ? slot.Listing?.ItemName : slot.Assignment?.ItemName;
            var pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(4, 12), ImGui.GetColorU32(color));
            ImGui.Dummy(new(4, 12));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(name is null ? "Free" : $"{name} · {label}");
            ImGui.SameLine(0, 1);
        }
        ImGui.SameLine(0, 6);
        var total = row.PlannedSlots + row.UnplannedSlots;
        if (total > MarketRunPlanner.ListingCapacity) ImGui.TextColored(Color(ListingStateTone.Amber), total.ToString("N0"));
        else ImGui.TextUnformatted(total.ToString("N0"));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{row.PlannedSlots} planned · {row.UnplannedSlots} listed off-plan");
    }

    private void DrawRetainerState(ListingRetainerRow row)
    {
        var start = ImGui.GetCursorPosX();
        var right = start + ImGui.GetContentRegionAvail().X;
        if (toList.GetValueOrDefault(row.RetainerId) is var listing and > 0)
        {
            ImGui.TextColored(Color(ListingStateTone.Run), $"{listing:N0} to list");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.State.Text);
        }
        else DrawState(row.State);
        if (ImGui.IsItemClicked()) workbench.ToggleListing(row.RetainerId);
        if (!row.Available) return;
        var size = ImGui.GetTextLineHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - size));
        void Open() => _ = navigation.OpenRetainerListingsAsync(new(row.RetainerId, row.RetainerName));
        ImGui.BeginDisabled(navigation.IsRunning);
        var clicked = ImGuiComponents.IconButton($"RQOpenListings{row.RetainerId}", FontAwesomeIcon.ExternalLinkAlt,
            Vector4.Zero, null, null, new Vector2(size, size) / ImGuiHelpers.GlobalScale);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Open in game");
        if (clicked) Open();
        // One semantic open control per frame, bound to the selected retainer's own button.
        if (row.RetainerId != workbench.SelectedListingRetainerId || openFirstRegistered) return;
        openFirstRegistered = true;
        registry.RegisterLastButton("quartermaster.listings.open-first", "Open in game", !navigation.IsRunning,
            Open, navigation.IsRunning ? navigation.Status : "Ready");
    }

    // A retainer listed twice would draw two detail panels and register the same review
    // control twice in one frame. Keep the first row and say which retainer repeated.
    private IReadOnlyList<ListingRetainerRow> DistinctRetainers(IReadOnlyList<ListingRetainerRow> rows)
    {
        var seen = new HashSet<ulong>();
        List<ListingRetainerRow>? distinct = null;
        List<string>? repeated = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (seen.Add(rows[i].RetainerId)) { distinct?.Add(rows[i]); continue; }
            distinct ??= rows.Take(i).ToList();
            (repeated ??= []).Add($"{rows[i].RetainerName} ({rows[i].RetainerId})");
        }
        if (repeated is null) { duplicateRowsWarned = null; return rows; }
        var summary = string.Join(", ", repeated);
        if (duplicateRowsWarned != summary)
        {
            duplicateRowsWarned = summary;
            log.Warning("ListingRetainerRowsDuplicated retainers=[{Retainers}] rows={Rows} distinct={Distinct}; duplicates hidden", summary, rows.Count, distinct!.Count);
        }
        return distinct!;
    }

    private void DrawDetail(QuartermasterRuntimeSnapshot runtime, ListingRetainerRow retainer)
    {
        drawingRetainerId = retainer.RetainerId;
        drawingSlots = retainer.Slots;
        drawingPulls = runPane.Retainer(runtime, session, retainer.RetainerId)?.ListingsToPull ?? [];
        if (slotRetainerId == retainer.RetainerId)
        {
            slotSelection.Retain(retainer.Slots.Select(s => s.Key));
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsAnyItemActive() && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                slotSelection.Clear();
            var picked = retainer.Slots.Where(s => slotSelection.IsSelected(s.Key)).ToArray();
            if (picked.Length > 0) DrawBulkBar(runtime, retainer, picked);
        }

        var table = (ImGui.TableGetColumnFlags(3) & ImGuiTableColumnFlags.IsEnabled) != 0 ? slotTable : slotsWithoutMarket;
        if (table.Begin($"RQSlots{retainer.RetainerId}-{table.ColumnCount}", DalamudTableLayout.FitContent(
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuterH)))
        {
            try
            {
                DrawSelectAll(retainer);
                for (var index = 0; index < retainer.Slots.Count; index++)
                {
                    var slot = retainer.Slots[index];
                    var highlighted = workbench.HighlightedListingItem is { } key && (Is(slot.Assignment, key) || Is(slot.Listing, key));
                    table.DrawSaneRow(retainer.Slots, index, $"slot:{SlotId(slot.Key)}",
                        highlighted ? new Vector4(.16f, .2f, .13f, 1) : null);
                }
            }
            finally { table.End(); }
        }

        if (retainer.RetainerId == workbench.SelectedListingRetainerId) workbench.ScrollToListingDetail = null;
    }

    private static bool Is(ListingPlanAssignment? a, ListingItemKey key) => a is not null && a.ItemId == key.ItemId && a.Quality == key.Quality;
    private static bool Is(ListingRow? l, ListingItemKey key) => l is not null && l.ItemId == key.ItemId && ListingRetainerProjection.Quality(l) == key.Quality;
    private static string SlotId(ListingSlotKey key) => $"{key.Source}:{key.AssignmentId:N}:{key.Index}";
    private static bool Dim(ListingSlotView slot) => slot.Continuation || slot.Number == 0;

    private void ResetSlots(ulong? retainerId)
    {
        slotSelection.Clear();
        slotRetainerId = retainerId;
        bulkUnits = bulkPrice = null;
        bulkItemInput.Text = string.Empty;
    }

    private void ClaimSlots()
    {
        if (slotRetainerId == drawingRetainerId) return;
        ResetSlots(drawingRetainerId);
        workbench.SelectedListingRetainerId = drawingRetainerId;
    }

    // Click selects one (or deselects the sole selection), Shift extends from the anchor,
    // Ctrl or the row checkbox toggles.
    private void ClickSlot(ListingSlotView slot)
    {
        ClaimSlots();
        var keys = drawingSlots.Select(s => s.Key).ToArray();
        var index = Array.IndexOf(keys, slot.Key);
        if (index < 0) return;
        var io = ImGui.GetIO();
        var selected = slotSelection.IsSelected(slot.Key);
        if (io.KeyShift) slotSelection.ApplyClick(keys, index, io.KeyCtrl, true);
        else if (io.KeyCtrl || ImGui.TableGetColumnIndex() == 0) slotSelection.ApplyClick(keys, index, !selected, false, selected);
        else if (selected && slotSelection.Count == 1) slotSelection.Clear();
        else slotSelection.ApplyClick(keys, index, false, false);
        slotSelection.EndDrag();
    }

    private void DrawSelectAll(ListingRetainerRow retainer)
    {
        // TableHeader allows overlap, so the checkbox drawn over its cell stays interactive.
        if (!ImGui.TableSetColumnIndex(0)) return;
        var mine = slotRetainerId == retainer.RetainerId;
        var all = mine && retainer.Slots.Count > 0 && retainer.Slots.All(s => slotSelection.IsSelected(s.Key));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
        if (ImGui.Checkbox("##slot-all", ref all))
        {
            ClaimSlots();
            slotSelection.Clear();
            if (all) foreach (var slot in retainer.Slots) slotSelection.SetSelected(slot.Key, true);
        }
        ImGui.PopStyleVar();
    }

    private void SlotCell(ListingSlotView slot, Action draw)
    {
        if (slot.GroupStart)
        {
            var padding = ImGui.GetStyle().CellPadding;
            var cursor = ImGui.GetCursorScreenPos();
            var y = cursor.Y - padding.Y;
            ImGui.GetWindowDrawList().AddLine(new(cursor.X - padding.X, y), new(cursor.X + ImGui.GetContentRegionAvail().X + padding.X, y),
                ImGui.GetColorU32(ImGuiCol.Separator));
        }
        draw();
    }

    private void DrawSlotCheck(ListingSlotView slot) => SlotCell(slot, () =>
    {
        // Visual only: the row's own selection target handles the click as a toggle.
        var on = slotRetainerId == drawingRetainerId && slotSelection.IsSelected(slot.Key);
        ImGui.PushID(SlotId(slot.Key));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
        ImGui.Checkbox("##slot", ref on);
        ImGui.PopStyleVar();
        ImGui.PopID();
    });

    private void DrawSlotItem(ListingSlotView slot)
    {
        if (drawingRetainerId == workbench.SelectedListingRetainerId && workbench.ScrollToListingDetail is { } key &&
            (Is(slot.Assignment, key) || Is(slot.Listing, key)))
        { ImGui.SetScrollHereY(.25f); workbench.ScrollToListingDetail = null; }
        if (slot.Assignment is not { } a) { ImGui.TextDisabled("Free"); return; }
        if (Dim(slot)) ImGui.TextDisabled(a.ItemName); else ImGui.TextUnformatted(a.ItemName);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenItem(a.ItemId);
        ImGui.SameLine(); ImGui.TextDisabled(TransferPresentation.QualityLabel(a.Quality, anyLabel: "NQ"));
    }

    private void DrawSlotListed(ListingSlotView slot)
    {
        var l = slot.Listing;
        string Price(ListingRow row) => row.UnitPrice.IsKnown ? row.UnitPrice.Value.ToString("N0") : "?";
        switch (slot.Listed)
        {
            case ListingSlotListed.Match:
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextColored(Color(ListingStateTone.Green), FontAwesomeIcon.Check.ToIconString());
                ImGui.PopFont();
                break;
            case ListingSlotListed.PriceDiffers:
                ImGui.TextColored(Color(ListingStateTone.Info), Price(l!));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Listed at {Price(l!)} · plan {ListingPlanInput.Display(slot.Assignment!.UnitPrice, true)}");
                break;
            case ListingSlotListed.UnknownPrice:
                ImGui.TextDisabled("Price unknown");
                break;
            case ListingSlotListed.ShapeDiffers:
                ImGui.TextColored(Color(ListingStateTone.Info), $"×{l!.Quantity:N0} · {Price(l)}");
                break;
            case ListingSlotListed.NotListed:
                ImGui.TextColored(Color(ListingStateTone.Amber), "Not listed");
                break;
            case ListingSlotListed.Other:
                ImGui.TextDisabled($"{l!.ItemName} · {Price(l)}");
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenItem(l.ItemId);
                break;
            default:
                if (slot.Number == 0) ImGui.TextDisabled("Excluded");
                return;
        }
        if (l is not null && drawingPulls.FirstOrDefault(p => ReferenceEquals(p.Listing, l)) is { } pull && ImGui.IsItemHovered())
            ImGui.SetTooltip(MarketRunPresentation.PullLabel(pull));
    }

    private static void RightText(string text, bool dim)
    {
        if (text.Length == 0) return;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        if (dim) ImGui.TextDisabled(text); else ImGui.TextUnformatted(text);
    }

    private void DrawBulkBar(QuartermasterRuntimeSnapshot runtime, ListingRetainerRow retainer, IReadOnlyList<ListingSlotView> picked)
    {
        var planned = picked.Where(s => s.Assignment is not null).ToArray();
        var included = planned.Count(s => s.Assignment!.Enabled);
        var excluded = planned.Length - included;
        var editable = retainer.Available;
        var current = session.CurrentRetainerIds(runtime);
        var adoptable = editable
            ? picked.Where(s => s.Listing is not null && s.Listed != ListingSlotListed.Match &&
                session.CanAdoptListed(retainer.RetainerId, s.Listing, runtime.Browser, current)).ToArray()
            : [];
        var canInclude = excluded > 0 && excluded <= session.Free(retainer.RetainerId);
        var buttons = new List<(string Label, Action Apply)>();
        if (adoptable.Length > 0)
            buttons.Add(("Adopt listed", () => Rewrite(retainer, picked, s => adoptable.Contains(s) ? (true, ListingSlotPlan.Of(s.Listing!)) : (false, null))));
        if (included > 0)
            buttons.Add(("Exclude", () => Rewrite(retainer, picked, s => s.Assignment is { Enabled: true } a ? (true, ListingSlotPlan.Of(a) with { Enabled = false }) : (false, null))));
        if (canInclude)
            buttons.Add(("Include", () => Rewrite(retainer, picked, s =>
            {
                if (s.Assignment is not { Enabled: false } a) return (false, null);
                var stack = session.MaxStack(runtime.Browser, a.ItemId);
                return (true, ListingSlotPlan.Of(a) with { Enabled = true, Units = stack == 1 ? 1 : Math.Clamp(a.QuantityPerListing, 0, stack) });
            })));
        if (planned.Length > 0)
            buttons.Add(("Clear", () => Rewrite(retainer, picked, s => s.Assignment is null ? (false, null) : (true, null))));

        var style = ImGui.GetStyle();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(picked.Count == 1 ? "1 slot" : $"{picked.Count:N0} slots");
        if (editable)
        {
            var reserved = buttons.Sum(b => DalamudUiLayout.ButtonWidth(b.Label) + style.ItemSpacing.X) +
                (planned.Length > 0 ? BulkUnitsWidth + BulkPriceWidth + style.ItemSpacing.X * 2 : 0) + style.ItemSpacing.X;
            var itemWidth = Math.Max(BulkItemMinWidth, ImGui.GetContentRegionAvail().X - reserved);
            var items = picked.Select(s => s.Assignment is { } a ? (a.ItemId, a.Quality, a.ItemName) : default).Distinct().ToArray();
            var hint = items.Length == 1 && items[0].ItemId != 0 ? $"{items[0].ItemName} {TransferPresentation.QualityLabel(items[0].Quality, anyLabel: "NQ")}" :
                planned.Length > 0 ? "Mixed" : "Item";
            ImGui.SameLine();
            ResolveChoices(runtime, retainer.RetainerId, bulkItemInput);
            if (DalamudSuggestionInputRenderer.Draw("RQSlotItem", bulkItemInput, choices, itemWidth, hint, 8, out var choice) && choice is not null)
                ApplyItem(runtime, retainer, picked, choice);
            if (planned.Length > 0)
            {
                ImGui.SameLine();
                var stacks = planned.Select(s => session.MaxStack(runtime.Browser, s.Assignment!.ItemId)).ToArray();
                if (stacks.All(stack => stack == 1))
                {
                    var start = ImGui.GetCursorPosX();
                    ImGui.TextDisabled("1");
                    ImGui.SameLine(start + BulkUnitsWidth + style.ItemSpacing.X);
                }
                else
                {
                    var units = BulkField("##slot-units", ref bulkUnits, Common(planned, a => a.QuantityPerListing, false), BulkUnitsWidth);
                    ImGui.SameLine();
                    // Units clamp to each slot's own stack size; non-stackable items stay at 1.
                    if (units is not null)
                        Rewrite(retainer, picked, s =>
                        {
                            if (s.Assignment is not { } a) return (false, null);
                            var stack = session.MaxStack(runtime.Browser, a.ItemId);
                            var value = stack == 1 ? 1 : ListingPlanInput.Commit(units, stack);
                            return value == 0 ? (false, null) : (true, ListingSlotPlan.Of(a) with { Units = value });
                        });
                }
                if (BulkField("##slot-price", ref bulkPrice, Common(planned, a => a.UnitPrice, true), BulkPriceWidth) is { } price)
                {
                    var value = ListingPlanInput.Commit(price, ListingPlanInput.MaxUnitPrice);
                    if (value > 0) Rewrite(retainer, picked, s => s.Assignment is { } a ? (true, ListingSlotPlan.Of(a) with { Price = value }) : (false, null));
                }
            }
        }
        foreach (var (label, apply) in buttons)
        {
            DalamudUiLayout.SameLineIfFits(label);
            if (ImGui.Button(label)) apply();
        }
    }

    private static (string Text, bool Mixed) Common(IReadOnlyList<ListingSlotView> planned, Func<ListingPlanAssignment, int> value, bool grouped)
    {
        var values = planned.Select(s => value(s.Assignment!)).Distinct().ToArray();
        return values.Length == 1 ? (ListingPlanInput.Display(values[0], grouped), false) : (string.Empty, true);
    }

    // Commits on Enter or deactivation after an edit; an untouched field commits nothing.
    private static string? BulkField(string id, ref string? buffer, (string Text, bool Mixed) common, float width)
    {
        var text = buffer ?? common.Text;
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputTextWithHint(id, common.Mixed ? "Mixed" : string.Empty, ref text, 16, ImGuiInputTextFlags.AutoSelectAll))
            buffer = text;
        if (ImGui.IsItemActivated()) buffer = text;
        if (!ImGui.IsItemDeactivated()) return null;
        var commit = ImGui.IsItemDeactivatedAfterEdit() ? buffer : null;
        buffer = null;
        return commit;
    }

    private void ApplyItem(QuartermasterRuntimeSnapshot runtime, ListingRetainerRow retainer,
        IReadOnlyList<ListingSlotView> picked, ListingAddChoice choice)
    {
        var stack = session.MaxStack(runtime.Browser, choice.ItemId);
        // Reuse the price this retainer already plans for the item; never carry another item's price or units over.
        var price = session.Draft?.Assignments.FirstOrDefault(a => a.RetainerId == retainer.RetainerId &&
            a.ItemId == choice.ItemId && a.Quality == choice.Quality && a.UnitPrice > 0)?.UnitPrice ?? 0;
        Rewrite(retainer, picked, s => s.Assignment is { } a && a.ItemId == choice.ItemId && a.Quality == choice.Quality
            ? (false, null)
            : (true, new ListingSlotPlan(choice.ItemId, choice.ItemName, choice.Quality,
                stack == 1 ? 1 : 0, price, s.Assignment?.Enabled ?? true)));
    }

    /// <summary>
    /// Applies one bulk edit. Every selected planned slot is passed through the rewrite (changed
    /// or not) so the selection follows it through regrouping. Freed slots drop out of the selection.
    /// </summary>
    private void Rewrite(ListingRetainerRow retainer, IReadOnlyList<ListingSlotView> picked,
        Func<ListingSlotView, (bool Change, ListingSlotPlan? Plan)> map)
    {
        var rewrites = new List<(ListingSlotKey Key, ListingSlotPlan? Plan)>();
        var untouched = new List<ListingSlotKey>();
        var changed = false;
        foreach (var slot in picked)
        {
            var (change, plan) = map(slot);
            var currentPlan = slot.Assignment is { } a ? ListingSlotPlan.Of(a) : null;
            if (change && plan != currentPlan)
            {
                changed = true;
                rewrites.Add((slot.Key, plan));
            }
            else if (currentPlan is not null) rewrites.Add((slot.Key, currentPlan));
            else untouched.Add(slot.Key);
        }
        if (!changed || session.RewriteSlots(retainer.RetainerId, retainer.RetainerName, rewrites) is not { } moved) return;
        slotSelection.Clear();
        foreach (var key in untouched.Concat(moved))
            slotSelection.SetSelected(key, true);
        bulkUnits = bulkPrice = null;
    }

    private static void DrawState(ListingStateView state)
    {
        ImGui.TextColored(Color(state.Tone), state.Text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ListingRetainerProjection.Tooltip(state));
    }

    // Navigation speaks only when it failed; a successful open offers the way back.
    private void DrawNavigationStatus()
    {
        if (navigation.IsRunning || string.IsNullOrWhiteSpace(navigation.Status)) return;
        if (navigation.LastFailed)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Color(ListingStateTone.Amber), navigation.Status);
            ImGui.PopTextWrapPos();
        }
        if (!navigation.Status.StartsWith("Opened ", StringComparison.Ordinal)) return;
        if (ImGui.SmallButton("Return to retainer list")) _ = navigation.ReturnToRetainerListAsync();
        registry.RegisterLastButton("quartermaster.listings.return-to-list", "Return to retainer list", true,
            () => _ = navigation.ReturnToRetainerListAsync(), "Ready");
    }

    private void ResolveChoices(QuartermasterRuntimeSnapshot runtime, ulong retainer, DalamudSuggestionInputState input)
    {
        var query = input.Text.Trim().ToLowerInvariant();
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
                $"{i.ItemName} {TransferPresentation.QualityLabel(i.Quality, anyLabel: "NQ")}", i.Here > 0 ? $"{i.Here:N0} here" : $"{i.Owned:N0} owned")).ToList();
        var seen = owned.Select(i => (i.Value.ItemId, i.Value.Quality)).ToHashSet();
        if (query.Length >= 2)
            foreach (var i in catalog.Where(i => listability(i.ItemId) is true && Match(i.ItemId, i.Name)))
                foreach (var q in canBeHq(i.ItemId) ? new[] { ItemQualityPolicy.NqOnly, ItemQualityPolicy.HqOnly } : new[] { ItemQualityPolicy.NqOnly })
                    if (seen.Add((i.ItemId, q))) owned.Add(new(new(i.ItemId, i.Name, q), $"{i.Label} {TransferPresentation.QualityLabel(q, anyLabel: "NQ")}"));
        var duplicateNames = owned.GroupBy(s => s.Value.ItemName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(s => s.Value.ItemId).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return owned.Select(s => duplicateNames.Contains(s.Value.ItemName)
            ? s with { Label = $"{s.Value.ItemName} · #{s.Value.ItemId} {TransferPresentation.QualityLabel(s.Value.Quality, anyLabel: "NQ")}" } : s).Take(64).ToArray();
    }
}
