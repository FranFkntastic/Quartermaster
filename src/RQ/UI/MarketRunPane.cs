using Franthropy.Dalamud.UI.Inputs;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Interop;
using RQ.Planning;
using RQ.Runtime;
using RQ.Operations;

namespace RQ.UI;

/// <summary>
/// Side pane of the Market tab: the run bar, one row per planned or reserved item, and the craft export.
/// </summary>
internal sealed class MarketRunPane(Func<uint, uint?> resolveWorkshopProject, string configDirectory,
    MarketMafiosoSavedJobClient importer)
{
    // Pane widths at which the Listed and Goes to columns join Item | Reserve | Craft.
    private const float ListedColumnWidth = 470;
    private const float GoesToColumnWidth = 620;
    // Below this the Reserve cell drops its bar and over-reserve figure so the Item column keeps room.
    private const float ReserveBarWidth = 360;
    private const string AcknowledgePopup = "Acknowledge market receipt";
    private static readonly TimeSpan StartAfterSaveTimeout = TimeSpan.FromSeconds(10);

    private (long Runtime, int Draft, DateTime Date, bool Reorganize) cacheKey = (-1, -1, default, false);
    private ListingPlanDraft? cachedDraft;
    private MarketRunEvaluation run = new(null, 0, [], [], []);
    private MarketRunCraftExportResult export = new(null, [], []);
    private Dictionary<(uint ItemId, ItemQualityPolicy Quality), int> stowByItem = [];
    private bool stowKnown;
    private string? status;
    private bool reorganizeRetainers;
    private DateTime? startAfterSave;
    private IReadOnlyList<MarketRunItem> items = [];
    internal MarketRunExecutor? Executor { get; set; }
    internal MarketRunHistoryWindow? History { get; set; }
    /// <summary>Opens the Market Placement view, filtered to the named item when given.</summary>
    internal Action<string?>? OpenPlacement { get; set; }
    internal AgentBridgeUiReviewRegistry? Registry { get; set; }

    internal IReadOnlyList<MarketRunItem> Items(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session)
    {
        Update(runtime, session);
        return items;
    }
    internal MarketRunRetainer? Retainer(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session, ulong id)
    {
        Update(runtime, session);
        return run.Retainers.FirstOrDefault(r => r.RetainerId == id);
    }

    private void Update(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session)
    {
        if (session.Draft is not { } draft) return;
        var key = (runtime.Revision, session.Version, DateTime.Today, reorganizeRetainers);
        if (cacheKey == key && ReferenceEquals(cachedDraft, draft)) return;
        cacheKey = key; cachedDraft = draft;
        var plan = MarketRunPresentation.PlanView(draft);
        run = MarketRunPlanner.Evaluate(plan, runtime.Browser);
        stowByItem = [];
        var bags = MarketRunPlanner.ProjectedStowRequests(plan.Id, plan.Reserves, run, runtime.Browser.Items
            .SelectMany(i => i.Stacks).Where(s => s.ScopeKind == BrowserScopeKind.Player)
            .Select(s => (s.ItemId, s.Quality == FfxivItemQuality.HQ, s.Quantity)));
        stowKnown = bags is not null;
        if (bags is not null)
        {
            IReadOnlyList<StowageDepositRequest> moves = reorganizeRetainers
                ? MarketRunPlanner.RelocationRequests(plan.Id, plan.Reserves, runtime.Retainers, runtime.Owner) : [];
            // Bag requests route first so relocation only gets the room stowing leaves.
            var batch = StowageRouter.BuildBatch(bags.Concat(moves), runtime.Retainers, runtime.Owner,
                id => session.MaxStack(runtime.Browser, id), DateTime.UtcNow);
            foreach (var route in batch.Routes.Take(bags.Count).Where(r => r.RoutedQuantity > 0))
            {
                if (plan.Reserves.FirstOrDefault(r => r.Id == route.Request.SourceRuleId) is not { } reserve) continue;
                var itemKey = (reserve.ItemId, reserve.Quality);
                stowByItem[itemKey] = stowByItem.GetValueOrDefault(itemKey) + route.RoutedQuantity;
            }
            run = run with
            {
                StowUnits = stowByItem.Values.Sum(),
                MoveUnits = batch.Routes.Skip(bags.Count).Sum(r => r.RoutedQuantity),
            };
        }
        items = MarketRunPresentation.DisplayItems(run, draft);
        export = MarketRunCraftExport.Export($"Market run {DateTime.Today:yyyy-MM-dd}", run.CraftQueue,
            resolveWorkshopProject, DateTime.UtcNow);
    }

    /// <param name="runtime">Current runtime snapshot.</param>
    /// <param name="session">Shared Listing Plan draft session (its draft is non-null when this is called).</param>
    /// <param name="retainers">Projection of every retainer row, unfiltered by the Market search box.</param>
    /// <param name="selectedRetainerId">Retainer currently expanded in the list, if any.</param>
    public void Draw(
        QuartermasterRuntimeSnapshot runtime,
        ListingPlanDraftSession session,
        IReadOnlyList<ListingRetainerRow> retainers,
        ulong? selectedRetainerId)
    {
        if (session.Draft is not { } draft) return;
        Update(runtime, session);
        StartAfterSave(runtime, session);
        DrawRunBar(runtime, session);
        ImGui.Dummy(new Vector2(0, 6));
        DrawTable(runtime, session, draft, retainers, selectedRetainerId);
        DrawFooter();
    }

    // ---- Run bar ---------------------------------------------------------------------------------

    private static float BarHeight => (ImGui.GetTextLineHeight() * 2.4f) + 4;

    private void DrawRunBar(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session)
    {
        var executor = Executor;
        var result = executor?.Result;
        var foreign = result is not null && !result.Owner.Matches(runtime.Owner);
        if (executor is { IsRunning: true }) DrawRunning(executor);
        else if (executor is not null && result is { RequiresAcknowledgement: true } && !foreign) DrawReview(executor, result);
        else DrawReady(runtime, session, executor);

        if (foreign && result!.RequiresAcknowledgement)
            Notice(ResultOwnerLabel(result.Owner) + " requires review on that character.");
        if (executor?.PersistenceError is { } persistence) Notice(persistence);
        if (History?.Warning is { } historyWarning) Notice(historyWarning);
    }

    private static void Notice(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ListingWorkspace.Color(ListingStateTone.Amber));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private void DrawReady(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session, MarketRunExecutor? executor)
    {
        var origin = ImGui.GetCursorScreenPos();
        var right = origin.X + ImGui.GetContentRegionAvail().X;
        var height = BarHeight;
        var reason = executor is null ? "Market runs are unavailable." :
            !run.IsComplete ? "Observations incomplete." :
            session.IsDirty && session.Issues(runtime).Count > 0 ? "The Listing Plan has problems to fix before it can be saved." :
            !MarketRunExecutor.HasWork(run, reorganizeRetainers) ? "Nothing to do." :
            !executor.CanStart ? "Automation busy." : null;
        ImGui.BeginDisabled(reason is not null || startAfterSave is not null);
        if (UiLayout.PrimaryButton("Start run##RQMarketRunStart", new Vector2(ImGui.CalcTextSize("Start run").X + 40, height)))
            Start(runtime, session);
        ImGui.EndDisabled();
        if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(reason);

        ImGui.SameLine(0, 16);
        DrawStat(ToList(out var listTooltip), "to list", ListingWorkspace.Color(ListingStateTone.Run), listTooltip);
        ImGui.SameLine(0, 22);
        DrawStat(stowKnown && run.IsComplete ? run.StowUnits.ToString("N0") : "—", "to stow", ListingWorkspace.Color(ListingStateTone.Green), null);
        ImGui.SameLine(0, 22);
        DrawStat(run.IsComplete ? run.CraftQueue.Sum(l => (long)l.Quantity).ToString("N0") : "—", "to craft",
            ListingWorkspace.Color(ListingStateTone.Amber), null);
        if (reorganizeRetainers && stowKnown && run.MoveUnits > 0)
        {
            ImGui.SameLine(0, 22);
            DrawStat(run.MoveUnits.ToString("N0"), "to move", ListingWorkspace.Color(ListingStateTone.Info), null);
        }
        var statsRight = ImGui.GetItemRectMax().X;

        // Last run sits above Reorganize retainers at the right edge; both drop below the bar when narrow.
        var latest = History?.Latest(runtime.Owner);
        var link = latest is null ? null : LastRunValue(latest);
        var linkWidth = link is null ? 0 : ImGui.CalcTextSize("Last run ").X + ImGui.CalcTextSize(link).X;
        const string checkLabel = "Reorganize retainers";
        var checkWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(checkLabel).X;
        var sideWidth = Math.Max(linkWidth, checkWidth);
        if (right - statsRight >= sideWidth + 16)
        {
            if (latest is not null)
                DrawLastRun(latest, link!, new Vector2(right - linkWidth, origin.Y + 2));
            ImGui.SetCursorScreenPos(new Vector2(right - checkWidth, origin.Y + height - ImGui.GetFrameHeight()));
            DrawReorganize();
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + ImGui.GetStyle().ItemSpacing.Y));
        }
        else
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + ImGui.GetStyle().ItemSpacing.Y));
            DrawReorganize();
            if (latest is not null)
            {
                var y = ImGui.GetItemRectMin().Y + ((ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);
                var x = Math.Max(ImGui.GetItemRectMax().X + 12, right - linkWidth);
                if (x + linkWidth <= right) DrawLastRun(latest, link!, new Vector2(x, y));
                else DrawLastRun(latest, link!, new Vector2(origin.X, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y));
            }
        }
        ImGui.Dummy(Vector2.Zero);
    }

    private string ToList(out string? tooltip)
    {
        tooltip = null;
        if (!run.IsComplete || run.Retainers.Any(r => !r.RefillListings.IsKnown)) return "—";
        int Total(Func<MarketRunRetainer, FieldEvidence<int>> pick) => run.Retainers.Sum(r => pick(r) is { IsKnown: true } v ? v.Value : 0);
        var reprice = run.Retainers.SelectMany(r => r.Assignments).Sum(a => a.RepriceListings is { IsKnown: true } v ? v.Value : 0);
        var parts = new[]
        {
            (Total(r => r.RefillListings), "listings to post"),
            (reprice, "to reprice"),
            (Total(r => r.PullListings), "to pull for room"),
            (Total(r => r.BlockedBySlotsListings), "with no free slot"),
            (Total(r => r.UnfilledListings), "short of stock"),
        };
        if (parts.Skip(1).Any(p => p.Item1 > 0))
            tooltip = string.Join("\n", parts.Where(p => p.Item1 > 0).Select(p => $"{p.Item1:N0} {p.Item2}"));
        return parts[0].Item1.ToString("N0");
    }

    private static void DrawStat(string value, string label, Vector4 color, string? tooltip)
    {
        ImGui.BeginGroup();
        var top = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(top + 2);
        ImGui.SetWindowFontScale(1.45f);
        ImGui.TextColored(color, value);
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorPosY(top + BarHeight - ImGui.GetTextLineHeight() - 2);
        ImGui.TextDisabled(label);
        ImGui.EndGroup();
        if (tooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private void DrawReorganize()
    {
        ImGui.Checkbox("Reorganize retainers##RQMarketRunReorganize", ref reorganizeRetainers);
        Registry?.RegisterLastItem("quartermaster.listings.market-run.reorganize-retainers", "Reorganize retainers",
            AgentBridgeUiControlKind.Toggle, true, reorganizeRetainers, reorganizeRetainers ? "on" : "off",
            () => reorganizeRetainers = !reorganizeRetainers);
    }

    private static string LastRunValue(MarketRunRecord latest) => $"{When(latest.StartedAtUtc)} · {latest.State}";

    private static string When(DateTime? utc) => utc is { } value
        ? value.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture)
        : "unknown time";

    /// <summary>"Last run &lt;time · state&gt;"; the value opens Market run history.</summary>
    private void DrawLastRun(MarketRunRecord latest, string value, Vector2 position)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.TextDisabled("Last run");
        ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
        var at = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(value);
        if (ImGui.InvisibleButton("##RQMarketRunLastRun", size)) History?.Open();
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(at, ImGui.GetColorU32(ImGuiCol.Text), value);
        var dim = ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        for (var x = at.X; x < at.X + size.X; x += 3)
            draw.AddLine(new Vector2(x, at.Y + size.Y), new Vector2(Math.Min(x + 1, at.X + size.X), at.Y + size.Y), dim);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var reason = string.IsNullOrWhiteSpace(latest.Reason) ? string.Empty : latest.Reason + "\n";
            ImGui.SetTooltip(reason + MarketRunHistoryWindow.Summary(latest));
        }
        Registry?.RegisterLastButton("quartermaster.listings.market-run.last-run", "Open market run history", History is not null,
            () => History?.Open(), value);
    }

    private void DrawRunning(MarketRunExecutor executor)
    {
        var height = BarHeight;
        if (ImGui.Button("Cancel##RQMarketRunCancel", new Vector2(ImGui.CalcTextSize("Cancel").X + 40, height))) executor.Cancel();
        ImGui.SameLine(0, 16);
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var posted = executor.PostedListings;
        var target = executor.ListingTarget;
        var counts = target > 0 && posted <= target ? $"{posted:N0} of {target:N0} listed" :
            posted > 0 ? $"{posted:N0} listed" : string.Empty;
        var countsWidth = counts.Length == 0 ? 0 : ImGui.CalcTextSize(counts).X;
        var line = ImGui.GetTextLineHeight();
        var top = origin.Y + ((height - line - 10) / 2);
        var draw = ImGui.GetWindowDrawList();
        ImGui.PushClipRect(origin, new Vector2(origin.X + width - countsWidth - 12, origin.Y + height), true);
        draw.AddText(new Vector2(origin.X, top), ImGui.GetColorU32(ImGuiCol.Text), executor.Status);
        ImGui.PopClipRect();
        if (counts.Length > 0)
            draw.AddText(new Vector2(origin.X + width - countsWidth, top), ImGui.GetColorU32(ImGuiCol.TextDisabled), counts);
        if (target > 0)
        {
            var barTop = top + line + 4;
            draw.AddRectFilled(new Vector2(origin.X, barTop), new Vector2(origin.X + width, barTop + 6), ImGui.GetColorU32(SegmentBar.Empty));
            var fraction = Math.Clamp((float)posted / target, 0, 1);
            if (fraction > 0)
                draw.AddRectFilled(new Vector2(origin.X, barTop), new Vector2(origin.X + (width * fraction), barTop + 6),
                    ImGui.GetColorU32(UiLayout.PrimaryActive));
        }
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawReview(MarketRunExecutor executor, MarketRunRecord result)
    {
        var height = BarHeight;
        if (UiLayout.PrimaryButton("Review…##RQMarketRunReview", new Vector2(ImGui.CalcTextSize("Review…").X + 40, height)))
            ImGui.OpenPopup(AcknowledgePopup);
        ImGui.SameLine(0, 16);
        var unconfirmed = MarketRunHistoryWindow.Unconfirmed(result);
        var headline = unconfirmed.Length == 1
            ? Capitalize(unconfirmed[0].Label)
            : $"{unconfirmed.Length} moves: {string.Join(", ", unconfirmed.Select(m => m.Label))}";
        ImGui.BeginGroup();
        var top = ImGui.GetCursorPosY();
        var line = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPosY(top + Math.Max(0, (height - line - ImGui.GetTextLineHeight()) / 2));
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Amber), headline);
        ImGui.PopTextWrapPos();
        ImGui.TextDisabled($"Last run stopped here · {When(result.StartedAtUtc)}");
        ImGui.EndGroup();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", unconfirmed.Select(m => $"{m.Label}: {m.Detail}")));

        if (ImGui.BeginPopup(AcknowledgePopup))
        {
            ImGui.TextWrapped($"Quartermaster couldn't confirm {unconfirmed.Length} item move(s). Check your retainer and inventory in game. New runs stay blocked until you confirm. Nothing will be retried automatically; the summary stays in Market run history.");
            foreach (var move in unconfirmed) ImGui.TextWrapped($"{move.Label}: {move.Detail}");
            if (ImGui.Button("Checked, unblock runs")) { executor.Clear(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ImGui.Button("Not yet")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>One plan per character: Start run saves pending edits, then runs against the saved plan.</summary>
    private void Start(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session)
    {
        status = null;
        if (!session.IsDirty)
        {
            if (Executor is { } executor && ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner) is { } plan)
                _ = executor.RunAsync(plan, run, session.IsDirty, reorganizeRetainers);
            return;
        }
        session.Save(runtime);
        // A refused or conflicting save leaves the draft dirty and its error on the save bar.
        if (session.IsDirty || session.Error is not null) return;
        startAfterSave = DateTime.UtcNow;
    }

    /// <summary>Runs once the runtime snapshot carries the plan revision Start run just saved.</summary>
    private void StartAfterSave(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session)
    {
        if (startAfterSave is not { } since) return;
        if (Executor is not { } executor || executor.IsRunning || session.IsDirty || session.Error is not null)
        {
            startAfterSave = null;
            return;
        }
        if (DateTime.UtcNow - since > StartAfterSaveTimeout)
        {
            startAfterSave = null;
            status = "Plan saved; the run did not start. Press Start run again.";
            return;
        }
        if (session.Draft is not { } draft || ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner) is not { } plan ||
            plan.Id != draft.PlanId || plan.Revision != draft.SourceRevision) return;
        startAfterSave = null;
        Update(runtime, session);
        _ = executor.RunAsync(plan, run, session.IsDirty, reorganizeRetainers);
    }

    internal static string ResultOwnerLabel(OwnerScope owner) => string.IsNullOrWhiteSpace(owner.CharacterName)
        ? "Previous character's market run"
        : $"Market run: {owner.CharacterName} @ {owner.HomeWorldName}";

    // ---- Item table ------------------------------------------------------------------------------

    private void DrawTable(QuartermasterRuntimeSnapshot runtime, ListingPlanDraftSession session, ListingPlanDraft draft,
        IReadOnlyList<ListingRetainerRow> retainers, ulong? selectedRetainerId)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var paneClipMin = ImGui.GetWindowDrawList().GetClipRectMin();
        var paneClipMax = ImGui.GetWindowDrawList().GetClipRectMax();
        var showGoesTo = width >= GoesToColumnWidth;
        var showListed = width >= ListedColumnWidth;
        var layout = new RowLayout(showGoesTo ? 80 : 60, width < ReserveBarWidth);
        var listedBar = SegmentBar.Width(40);
        var listedWidth = listedBar + 6 + ImGui.CalcTextSize("000/000").X;
        var craftWidth = Math.Max(ImGui.CalcTextSize("Craft").X, ImGui.CalcTextSize("0,000").X);
        if (!ImGui.BeginTable("RQMarketRunItems", 3 + (showListed ? 1 : 0) + (showGoesTo ? 1 : 0),
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1);
        if (showListed) ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, listedWidth);
        ImGui.TableSetupColumn("Reserve", ImGuiTableColumnFlags.WidthFixed, layout.Width);
        ImGui.TableSetupColumn("Craft", ImGuiTableColumnFlags.WidthFixed, craftWidth);
        if (showGoesTo) ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthStretch, .8f);
        DrawHeaders(showListed, showGoesTo);
        var selected = retainers.FirstOrDefault(r => r.RetainerId == selectedRetainerId);
        var noProject = export.WithoutWorkshopProject.Select(l => (l.ItemId, l.Quality)).ToHashSet();
        foreach (var item in items)
        {
            ImGui.PushID($"{item.ItemId}:{item.Quality}");
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var sells = selected?.PlanRows.Any(r => r.Assignment.Enabled && r.Assignment.ItemId == item.ItemId && r.Assignment.Quality == item.Quality) == true ||
                selected?.ObservedGroups.Any(g => g.ItemId == item.ItemId && g.Quality == item.Quality) == true;
            if (sells) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(.12f, .18f, .24f, .65f)));
            var label = Label(item.ItemName, item.Quality);
            ImGui.AlignTextToFramePadding();
            var open = ImGui.TreeNodeEx(label + "###item", ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(label).X + ImGui.GetTreeNodeToLabelSpacing() > ImGui.GetItemRectSize().X)
                ImGui.SetTooltip(label);
            if (showListed) { ImGui.TableNextColumn(); DrawListed(item, listedBar); }
            ImGui.TableNextColumn();
            var reserve = draft.Reserves.FirstOrDefault(r => r.ItemId == item.ItemId && r.Quality == item.Quality);
            DrawReserve(item, reserve, session, layout);
            ImGui.TableNextColumn();
            DrawCraft(item, noProject.Contains((item.ItemId, item.Quality)));
            var goesTo = reserve is null ? string.Empty : TransferPresentation.RouteSummary(reserve.Location, runtime.Retainers, runtime.Owner);
            if (showGoesTo) { ImGui.TableNextColumn(); DrawGoesTo(item, goesTo, "column"); }
            if (open)
            {
                ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                // Span columns without escaping the run pane's scrolling clip rectangle.
                ImGui.PushClipRect(paneClipMin, paneClipMax, false);
                var frames = (showGoesTo ? 0 : 1) + (reserve is null ? 0 : 1) + (showListed ? 0 : 1);
                if (ImGui.BeginChild("detail", new Vector2(width - 8, (ImGui.GetFrameHeightWithSpacing() * frames) + 4), false))
                {
                    if (!showListed)
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextDisabled("Listed");
                        ImGui.SameLine();
                        DrawListed(item, listedBar);
                    }
                    if (!showGoesTo) DrawGoesTo(item, goesTo, "detail");
                    if (reserve is not null)
                    {
                        var enabled = reserve.Enabled;
                        if (ImGui.Checkbox("Reserve enabled", ref enabled)) session.SetReserveEnabled(reserve.Id, enabled);
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Remove reserve rule")) session.RemoveReserve(reserve.Id);
                    }
                }
                ImGui.EndChild(); ImGui.PopClipRect();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawHeaders(bool showListed, bool showGoesTo)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        var column = 0;
        ImGui.TableSetColumnIndex(column++);
        var cell = Header("Item");
        if (!showGoesTo) DrawPlacementButton(cell);
        if (showListed) { ImGui.TableSetColumnIndex(column++); ImGui.TableHeader("Listed"); }
        ImGui.TableSetColumnIndex(column++);
        var reserveCell = Header("Reserve");
        var stays = ListingWorkspace.Color(ListingStateTone.Green);
        var listings = ListingWorkspace.Color(ListingStateTone.Run);
        var keyWidth = ImGui.CalcTextSize("stays").X + ImGui.CalcTextSize("to listings").X + 44;
        if (reserveCell.Width - reserveCell.Label >= keyWidth)
        {
            AfterHeader(reserveCell, 12); Swatch(stays); ImGui.SameLine(0, 4); ImGui.TextDisabled("stays");
            ImGui.SameLine(0, 10); Swatch(listings); ImGui.SameLine(0, 4); ImGui.TextDisabled("to listings");
        }
        else if (ImGui.IsItemHovered()) ImGui.SetTooltip("Green stays in reserve · blue goes to listings");
        ImGui.TableSetColumnIndex(column++);
        ImGui.TableHeader("Craft");
        if (showGoesTo)
        {
            ImGui.TableSetColumnIndex(column);
            DrawPlacementButton(Header("Goes to"));
        }
    }

    private readonly record struct HeaderCell(float X, float Width, float Label);

    /// <summary>A header cell; ImGui's header item has zero layout width, so followers position from this.</summary>
    private static HeaderCell Header(string label)
    {
        var cell = new HeaderCell(ImGui.GetCursorPosX(), ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize(label).X);
        ImGui.TableHeader(label);
        return cell;
    }

    private static void AfterHeader(HeaderCell cell, float gap)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(cell.X + cell.Label + gap);
    }

    private static void Swatch(Vector4 color)
    {
        var size = ImGui.GetTextLineHeight() * .6f;
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + ((ImGui.GetTextLineHeight() - size) / 2);
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(pos.X, y), new Vector2(pos.X + size, y + size), ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(size, ImGui.GetTextLineHeight()));
    }

    private void DrawPlacementButton(HeaderCell cell)
    {
        if (OpenPlacement is not { } openPlacement) return;
        AfterHeader(cell, 8);
        if (ImGui.SmallButton("Placement##RQReservePlacement")) openPlacement(null);
        Registry?.RegisterLastButton("quartermaster.listings.reserves.placement", "Open the Market placement view", true,
            () => openPlacement(null), $"items={items.Count:N0}");
    }

    private void DrawListed(MarketRunItem item, float barWidth)
    {
        var inPlace = MarketRunPresentation.InPlaceUnits(run, item);
        SegmentBar.Draw(barWidth, item.PlannedUnits,
            (inPlace ?? 0, ListingWorkspace.Color(ListingStateTone.Green)),
            (inPlace is null || !item.RefillUnits.IsKnown ? 0 : item.RefillUnits.Value, ListingWorkspace.Color(ListingStateTone.Run)));
        ImGui.SameLine(0, 6);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{(inPlace is { } units ? units.ToString("N0") : "—")}/{item.PlannedUnits:N0}");
    }

    private sealed class RowLayout
    {
        public RowLayout(float bar, bool compact)
        {
            Compact = compact;
            Bar = SegmentBar.Width(bar);
            Number = ImGui.CalcTextSize("0,000").X;
            Of = ImGui.CalcTextSize("of").X;
            Input = ImGui.CalcTextSize("00,000").X + (ImGui.GetStyle().FramePadding.X * 2);
            Over = ImGui.CalcTextSize("+0,000").X;
            Width = compact ? Number + 4 + Of + 4 + Input : Bar + 7 + Number + 4 + Of + 4 + Input + 5 + Over;
        }
        public bool Compact { get; }
        public float Bar { get; }
        public float Number { get; }
        public float Of { get; }
        public float Input { get; }
        public float Over { get; }
        public float Width { get; }
    }

    /// <summary>Bar (green stays, blue to listings, empty shortfall), after-run units, "of", the editable reserve, "+N" over.</summary>
    private static void DrawReserve(MarketRunItem item, ListingReserveRule? reserve, ListingPlanDraftSession session, RowLayout layout)
    {
        var units = reserve?.ReserveUnits ?? 0;
        var known = item.ReserveAfterRun.IsKnown && item.RefillUnits.IsKnown;
        var stays = known ? Math.Max(0, item.ReserveAfterRun.Value) : 0;
        var listing = known ? item.RefillUnits.Value : 0;
        var total = units > 0 ? units : stays + listing;
        if (!layout.Compact)
        {
            SegmentBar.Draw(layout.Bar, known ? total : 0,
                (Math.Min(stays, total), ListingWorkspace.Color(ListingStateTone.Green)),
                (Math.Min(listing, Math.Max(0, total - stays)), ListingWorkspace.Color(ListingStateTone.Run)));
            ImGui.SameLine(0, 7);
        }
        var number = known ? stays.ToString("N0") : "—";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, layout.Number - ImGui.CalcTextSize(number).X));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(number);
        if (!known && ImGui.IsItemHovered() && item.ReserveAfterRun.UnknownReason is { } why) ImGui.SetTooltip(why);
        ImGui.SameLine(0, 4);
        ImGui.TextDisabled("of");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(layout.Input);
        if (DalamudGroupedIntegerInput.InputGroupedInt("##reserve", ref units, 0, 0))
        {
            reserve ??= session.AddReserve(item.ItemId, item.ItemName, item.Quality);
            session.SetReserveUnits(reserve.Id, Math.Clamp(units, 0, ListingReserveRule.MaxReserveUnits).ToString());
        }
        if (!layout.Compact && known && stays > units)
        {
            ImGui.SameLine(0, 5);
            ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Green), $"+{stays - units:N0}");
        }
    }

    private void DrawCraft(MarketRunItem item, bool noProject)
    {
        var known = item.CraftUnits.IsKnown;
        var text = !known ? "—" : item.CraftUnits.Value > 0 ? item.CraftUnits.Value.ToString("N0") : string.Empty;
        if (text.Length == 0) return;
        ImGui.AlignTextToFramePadding();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        if (!known || noProject) ImGui.TextDisabled(text);
        else ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Amber), text);
        if (!ImGui.IsItemHovered()) return;
        if (!known) { if (item.CraftUnits.UnknownReason is { } why) ImGui.SetTooltip(why); }
        else if (noProject) ImGui.SetTooltip("No workshop project; not included in the export.");
        else if (export.Capped.Any(l => l.ItemId == item.ItemId && l.Quality == item.Quality))
            ImGui.SetTooltip($"Export capped at {MarketRunCraftExport.MaxQuantity:N0}.");
    }

    /// <summary>"N →" when the run stows N units there, then the destinations; activating opens Placement filtered to this item.</summary>
    private void DrawGoesTo(MarketRunItem item, string summary, string where)
    {
        ImGui.AlignTextToFramePadding();
        if (stowByItem.GetValueOrDefault((item.ItemId, item.Quality)) is var stow and > 0 && run.IsComplete)
        {
            ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Run), $"{stow:N0} →");
            ImGui.SameLine(0, 5);
        }
        if (OpenPlacement is not { } openPlacement)
        {
            ImGui.TextDisabled(summary);
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        var clicked = ImGui.Selectable($"{(summary.Length == 0 && where == "detail" ? "—" : summary)}##goes-to-{where}");
        ImGui.PopStyleColor();
        if (clicked) openPlacement(item.ItemName);
        if (ImGui.IsItemHovered() && summary.Length > 0 && ImGui.CalcTextSize(summary).X > ImGui.GetItemRectSize().X)
            ImGui.SetTooltip(summary);
        Registry?.RegisterLastButton($"quartermaster.listings.reserves.goes-to.{where}.{item.ItemId}-{item.Quality}",
            $"Open placement for {Label(item.ItemName, item.Quality)}", true, () => openPlacement(item.ItemName),
            summary.Length == 0 ? "none" : summary);
    }

    // ---- Footer ----------------------------------------------------------------------------------

    private void DrawFooter()
    {
        ImGui.Dummy(new Vector2(0, 4));
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var wide = width >= ListedColumnWidth;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ListingWorkspace.Color(ListingStateTone.Amber),
            run.IsComplete ? run.CraftQueue.Sum(l => (long)l.Quantity).ToString("N0") : "—");
        ImGui.SameLine(0, 4);
        ImGui.TextDisabled(wide ? $"to craft · {run.CraftQueue.Count:N0} items" : "to craft");
        var leftEnd = ImGui.GetItemRectMax().X;

        var pad = ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + pad;
        var buttons = ButtonWidth("Copy") + spacing + (wide ? ButtonWidth("Save…") + spacing : 0) + ButtonWidth("Send to MarketMafioso");
        if (origin.X + width - buttons >= leftEnd + 12) ImGui.SameLine(origin.X + width - buttons - ImGui.GetWindowPos().X + ImGui.GetScrollX());
        ImGui.BeginDisabled(!run.IsComplete || export.Json is null);
        if (ImGui.Button("Copy##RQMarketRunCopy")) { ImGui.SetClipboardText(export.Json!); status = "Craft queue copied."; }
        if (wide)
        {
            ImGui.SameLine();
            if (ImGui.Button("Save…##RQMarketRunSave")) Save();
        }
        ImGui.SameLine();
        if (UiLayout.PrimaryButton("Send to MarketMafioso##RQMarketRunSend")) status = importer.Send(export.Json!).Message;
        ImGui.EndDisabled();
        if (!string.IsNullOrEmpty(status)) { ImGui.PushTextWrapPos(0); ImGui.TextDisabled(status); ImGui.PopTextWrapPos(); }
    }

    private void Save()
    {
        try
        {
            var folder = Path.Combine(configDirectory, "craft-queues");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Market run {DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, export.Json!);
            status = $"Saved {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { status = $"Could not save craft queue: {ex.Message}"; }
    }

    private static string Label(string name, ItemQualityPolicy quality) => $"{name} {(quality == ItemQualityPolicy.HqOnly ? "HQ" : "NQ")}";
}
