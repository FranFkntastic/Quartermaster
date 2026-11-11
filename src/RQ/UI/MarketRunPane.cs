using System.Numerics;
using Dalamud.Bindings.ImGui;
using RQ.Domain;
using RQ.Interop;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Side pane of the Market tab. It summarises the market run across retainers.
/// </summary>
internal sealed class MarketRunPane(Func<uint, uint?> resolveWorkshopProject, string configDirectory,
    MarketMafiosoSavedJobClient importer)
{
    private (long Runtime, int Draft, DateTime Date) cacheKey = (-1, -1, default);
    private ListingPlanDraft? cachedDraft;
    private MarketRunEvaluation run = new(null, 0, [], [], []);
    private MarketRunCraftExportResult export = new(null, [], []);
    private string? status;
    private IReadOnlyList<MarketRunItem> items = [];
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
        var key = (runtime.Revision, session.Version, DateTime.Today);
        if (cacheKey != key || !ReferenceEquals(cachedDraft, draft))
        {
            cacheKey = key; cachedDraft = draft;
            run = MarketRunPlanner.Evaluate(MarketRunPresentation.PlanView(draft), runtime.Browser);
            items = MarketRunPresentation.DisplayItems(run, draft);
            export = MarketRunCraftExport.Export($"Market run {DateTime.Today:yyyy-MM-dd}", run.CraftQueue,
                resolveWorkshopProject, DateTime.UtcNow);
        }
        ImGui.TextUnformatted(MarketRunPresentation.Header(run));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(MarketRunPresentation.Header(run));
        ImGui.Separator(); ImGui.TextUnformatted("Reserves");
        var width = ImGui.GetContentRegionAvail().X;
        var paneClipMin = ImGui.GetWindowDrawList().GetClipRectMin();
        var paneClipMax = ImGui.GetWindowDrawList().GetClipRectMax();
        var full = width >= 650;
        var listed = width >= 400;
        if (ImGui.BeginTable("RQMarketReserves", full ? 6 : listed ? 4 : 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            if (listed) ImGui.TableSetupColumn("Listed / Planned", ImGuiTableColumnFlags.WidthFixed, 108);
            if (full) ImGui.TableSetupColumn("Spare", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Reserve", ImGuiTableColumnFlags.WidthFixed, 65);
            if (full) ImGui.TableSetupColumn("After run", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("To craft", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();
            foreach (var item in items)
            {
                ImGui.PushID($"{item.ItemId}:{item.Quality}");
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                var selected = retainers.FirstOrDefault(r => r.RetainerId == selectedRetainerId);
                var sells = selected?.PlanRows.Any(r => r.Assignment.Enabled && r.Assignment.ItemId == item.ItemId && r.Assignment.Quality == item.Quality) == true ||
                    selected?.ObservedGroups.Any(g => g.ItemId == item.ItemId && g.Quality == item.Quality) == true;
                if (sells) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(.12f, .18f, .24f, .65f)));
                var label = Label(item.ItemName, item.Quality);
                var open = ImGui.TreeNodeEx(label + "###item", ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
                if (listed) { ImGui.TableNextColumn(); ImGui.TextUnformatted($"{MarketRunPresentation.Number(item.ListedUnits)} / {item.PlannedUnits:N0}"); }
                if (full) { ImGui.TableNextColumn(); ImGui.TextUnformatted(MarketRunPresentation.Number(item.SpareUnits)); }
                ImGui.TableNextColumn();
                var reserve = draft.Reserves.FirstOrDefault(r => r.ItemId == item.ItemId && r.Quality == item.Quality);
                var units = reserve?.ReserveUnits ?? 0;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##reserve", ref units, 0))
                {
                    reserve ??= session.AddReserve(item.ItemId, item.ItemName, item.Quality);
                    session.SetReserveUnits(reserve.Id, Math.Clamp(units, 0, ListingReserveRule.MaxReserveUnits).ToString());
                }
                if (full) { ImGui.TableNextColumn(); ImGui.TextUnformatted(MarketRunPresentation.Number(item.ReserveAfterRun)); }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(MarketRunPresentation.Number(item.CraftUnits));
                if (open)
                {
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                    // Span columns without escaping the run pane's scrolling clip rectangle.
                    ImGui.PushClipRect(paneClipMin, paneClipMax, false);
                    if (ImGui.BeginChild("detail", new Vector2(width - 8, ImGui.GetFrameHeightWithSpacing() * (reserve is null ? 2 : 6)), false))
                    {
                        if (!listed) ImGui.TextUnformatted($"Listed {MarketRunPresentation.Number(item.ListedUnits)} / {item.PlannedUnits:N0}");
                        if (!full) ImGui.TextUnformatted($"Spare {MarketRunPresentation.Number(item.SpareUnits)} · After run {MarketRunPresentation.Number(item.ReserveAfterRun)}");
                        if (reserve is not null)
                        {
                            var location = ListingPlanCatalog.CopyLocation(reserve.Location);
                            var enabled = reserve.Enabled;
                            if (ImGui.Checkbox("Reserve enabled", ref enabled)) session.SetReserveEnabled(reserve.Id, enabled);
                            RoutingPolicyEditor.DrawDestination(location, runtime);
                            RoutingPolicyEditor.DrawOverflow(location);
                            if (location.Mode != reserve.Location.Mode || location.Overflow != reserve.Location.Overflow ||
                                !location.PreferredRetainerIds.SequenceEqual(reserve.Location.PreferredRetainerIds))
                                session.SetReserveLocation(reserve.Id, location);
                            if (ImGui.SmallButton("Remove reserve rule")) session.RemoveReserve(reserve.Id);
                        }
                    }
                    ImGui.EndChild(); ImGui.PopClipRect();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.Spacing(); ImGui.Separator(); ImGui.TextUnformatted("Craft queue");
        var without = export.WithoutWorkshopProject.Select(l => (l.ItemId, l.Quality)).ToHashSet();
        foreach (var line in run.CraftQueue.Where(l => !without.Contains((l.ItemId, l.Quality))))
        {
            var capped = export.Capped.Contains(line) ? $" (export capped at {MarketRunCraftExport.MaxQuantity:N0})" : string.Empty;
            ImGui.TextWrapped($"{Label(line.ItemName, line.Quality)} × {line.Quantity:N0}{capped}");
        }
        if (export.WithoutWorkshopProject.Count > 0)
        {
            ImGui.TextDisabled("No workshop project");
            foreach (var line in export.WithoutWorkshopProject)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ListingWorkspace.Color(ListingStateTone.Dim));
                ImGui.TextWrapped($"{Label(line.ItemName, line.Quality)} × {line.Quantity:N0}");
                ImGui.PopStyleColor();
            }
        }
        if (run.CraftQueue.Count == 0) ImGui.TextDisabled(run.IsComplete ? "Nothing to craft." : "—");
        ImGui.BeginDisabled(!run.IsComplete || export.Json is null);
        if (ImGui.SmallButton("Copy")) { ImGui.SetClipboardText(export.Json!); status = "Craft queue copied."; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Save…")) Save();
        if (width > 380) ImGui.SameLine();
        if (ImGui.SmallButton("Send to MarketMafioso")) status = importer.Send(export.Json!).Message;
        ImGui.EndDisabled();
        if (!string.IsNullOrEmpty(status)) ImGui.TextWrapped(status);
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
