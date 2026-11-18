using Dalamud.Bindings.ImGui;
using Franthropy.FFXIV.Filtering;
using RQ.Interop;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

// A display-only projection. Only DrawToolbar schedules reads; notifications never drive the game.
internal sealed class ListingMarketView
{
    internal sealed record Figure(string Text, ListingStateTone Tone, (int, int, decimal) Sort,
        ListingRow Mine, MarketListing[] Competitors, long? Timestamp, string? Reason);
    private readonly ComplicatedMarketBoardCompetitionClient client;
    private readonly PluginConfiguration config;
    private readonly Action save;
    private readonly ListingPlanDraftSession session;
    private readonly Dictionary<ListingSlotView, Figure> slots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, Figure> summaries = [];
    private readonly HashSet<ulong> aggregate = [];
    private MarketSnapshot? snapshot;
    private Task<MarketSnapshot>? read;
    private int generation, readingGeneration, dirty = 1;
    private bool force;
    private string ownerKey = "";
    private string? preference;
    private long listingsRevision = -1, nextRead, oldest;
    private MarketPair[] pairs = [];
    private IReadOnlyList<ListingRetainerRow>? projected;
    private ListingRow? popup;
    private bool openPopup;
    private string input = "";
    private Dictionary<ListingRow, Figure> figures = new(ReferenceEqualityComparer.Instance);
    private sealed record Interest(string? Scope, HashSet<uint> Ids);
    private volatile Interest? interest;

    public ListingMarketView(ComplicatedMarketBoardCompetitionClient client, PluginConfiguration config, Action save,
        ListingPlanDraftSession session)
    {
        this.client = client; this.config = config; this.save = save; this.session = session;
        client.Changed += (key, ids) =>
        {
            if (interest is { } i && (i.Scope == null || i.Scope == key) && ids.Any(i.Ids.Contains))
                Interlocked.Exchange(ref dirty, 1);
        };
    }

    private static MarketPair Pair(ListingRow l) => new(l.ItemId, l.Quality == FfxivItemQuality.HQ);
    private void Invalidate()
    {
        generation++;
        snapshot = null;
        projected = null;
        popup = null;
        openPopup = false;
        Interlocked.Exchange(ref dirty, 1);
    }

    internal void DrawToolbar(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<ListingRetainerRow> rows)
    {
        if (listingsRevision != runtime.ListingsRevision)
        {
            listingsRevision = runtime.ListingsRevision;
            var owner = $"{runtime.Owner.LocalContentId}:{runtime.Owner.HomeWorldId}";
            var listed = runtime.Browser.Listings.Select(Pair).Distinct().OrderBy(p => p.ItemId).ThenBy(p => p.Hq).ToArray();
            if (owner != ownerKey || !pairs.SequenceEqual(listed))
            {
                ownerKey = owner;
                preference = config.MarketScopesByOwner.GetValueOrDefault(ownerKey);
                pairs = listed;
                interest = new(preference, pairs.Select(p => p.ItemId).ToHashSet());
                force = false;
                Invalidate();
            }
        }
        if (read is { IsCompletedSuccessfully: true })
        {
            if (readingGeneration == generation)
            {
                snapshot = read.Result;
                interest = new(snapshot.Key, interest!.Ids);
                projected = null;
            }
            read = null;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh market")) { force = true; Interlocked.Exchange(ref dirty, 1); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(145);
        if (ImGui.BeginCombo("##market-scope", snapshot?.Scopes.FirstOrDefault(s => s.Key == snapshot.Key)?.Label
            ?? preference ?? (snapshot?.Error == null ? "…" : "No data")))
        {
            foreach (var scope in snapshot?.Scopes ?? [])
                if (ImGui.Selectable(scope.Label + "###" + scope.Key, scope.Key == snapshot?.Key))
                {
                    config.MarketScopesByOwner[ownerKey] = preference = scope.Key;
                    save();
                    interest = new(preference, interest!.Ids);
                    Invalidate();
                }
            ImGui.EndCombo();
        }
        if (snapshot?.Error is { } error && ImGui.IsItemHovered()) ImGui.SetTooltip(error);
        var now = Environment.TickCount64;
        if (read == null && (Interlocked.Exchange(ref dirty, 0) != 0 || now >= nextRead))
        {
            var batch = pairs; var key = preference; var bypass = force;
            readingGeneration = generation;
            read = Task.Run(() => client.Read(key, batch, bypass));
            force = false;
            nextRead = now + 60_000;
        }
        if (!ReferenceEquals(projected, rows))
        {
            Rebuild(runtime, rows);
            projected = rows;
        }
        if (oldest > 0) { ImGui.SameLine(); ImGui.TextDisabled(Age(oldest) + " ago"); }
        DrawPopup(runtime);
    }

    private static string Age(long stamp)
    {
        var mins = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp) / 60000);
        return mins < 60 ? $"{mins}m" : $"{mins / 60}h";
    }

    private void Rebuild(QuartermasterRuntimeSnapshot runtime, IReadOnlyList<ListingRetainerRow> rows)
    {
        slots.Clear(); summaries.Clear(); aggregate.Clear(); figures.Clear();
        var own = runtime.Retainers.Values.Select(r => r.RetainerName + "@" + r.Owner.HomeWorldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = snapshot?.Items.ToDictionary(p => p.Key, p => p.Value.Listings
            .Where(l => l.Hq == p.Key.Hq && !own.Contains(l.RetainerName + "@" + l.WorldName))
            .OrderBy(l => l.PricePerUnit).ToArray());
        oldest = 0;
        foreach (var row in rows)
        {
            (MarketPair, decimal)? previous = null;
            var items = new Dictionary<MarketPair, Figure>();
            foreach (var slot in row.Slots)
            {
                if (slot.Listing is not { } mine) { previous = null; continue; }
                if (!mine.UnitPrice.IsKnown)
                {
                    previous = null;
                    var unknown = new Figure("No data", ListingStateTone.Dim, (3, 0, 0), mine, [], null, "Listed price unknown");
                    slots[slot] = unknown;
                    items.TryAdd(Pair(mine), unknown);
                    continue;
                }
                var pair = Pair(mine);
                var price = mine.UnitPrice.Value;
                var key = (pair, price);
                var item = snapshot?.Items.GetValueOrDefault(pair);
                var listings = filtered?.GetValueOrDefault(pair) ?? [];
                var stamp = item?.LastUploadUtcMs is > 0 ? item.LastUploadUtcMs : item?.FetchedUtcMs;
                var below = listings.Count(l => l.PricePerUnit < price);
                var tied = listings.Count(l => l.PricePerUnit == price);
                var delta = below > 0 ? price - listings[0].PricePerUnit : 0;
                var reason = snapshot?.Error ?? (item?.Status == "unavailable" ? item.Reason ?? "Unavailable" : null);
                var rank = reason != null || item?.Status is null or "pending" ? 3 : below > 0 ? 0 : tied > 0 ? 1 : 2;
                var label = rank switch { 0 => $"−{delta:N0} · {(item!.Truncated && below == item.Listings.Length ? "≥" : "")}{below} below",
                    1 => $"Tied · {tied}", 2 => "Lowest", _ => reason == null ? "…" : "No data" };
                var ladder = listings.Take(Math.Min(11, below)).Concat(listings.Skip(below + tied)
                    .Take(Math.Min(2, Math.Max(0, 11 - below)))).ToArray();
                var fig = new Figure(label, rank < 2 ? ListingStateTone.Amber : rank == 2 ? ListingStateTone.Green : ListingStateTone.Dim,
                    (rank, -below, -delta), mine, ladder, stamp, reason);
                figures[mine] = fig;
                if (stamp is > 0 && (oldest == 0 || stamp < oldest)) oldest = stamp.Value;
                if (previous != key) slots.Add(slot, fig);
                previous = key;
                if (!items.TryGetValue(pair, out var prior) || fig.Sort.CompareTo(prior.Sort) < 0) items[pair] = fig;
            }
            if (items.Count == 0) continue;
            var worst = items.Values.MinBy(f => f.Sort)!;
            if (items.Count > 1)
            {
                aggregate.Add(row.RetainerId);
                var count = items.Values.Count(f => f.Sort.Item1 < 2);
                // An unknown item prevents a truthful count (it isn't evidence of Lowest).
                var missing = items.Values.FirstOrDefault(f => f.Sort.Item1 == 3);
                worst = missing ?? worst with { Text = $"{count} of {items.Count} undercut", Timestamp = items.Values.Min(f => f.Timestamp) };
            }
            summaries[row.RetainerId] = worst;
        }
    }

    internal Figure? Summary(ListingRetainerRow r) => summaries.GetValueOrDefault(r.RetainerId);
    internal Figure? Slot(ListingSlotView s) => slots.GetValueOrDefault(s);
    internal void DrawSlot(ListingSlotView s) => DrawCell(Slot(s), null);
    internal void DrawSummary(ListingRetainerRow r, Action expand) => DrawCell(Summary(r), aggregate.Contains(r.RetainerId) ? expand : null);
    private void DrawCell(Figure? f, Action? expand)
    {
        if (f == null) return;
        ImGui.TextColored(ListingWorkspace.Color(f.Tone), f.Text);
        if (ImGui.IsItemClicked())
        {
            expand?.Invoke();
            if (f.Mine.UnitPrice.IsKnown) { popup = f.Mine; input = ""; openPopup = true; }
        }
        if (f.Reason != null && ImGui.IsItemHovered()) ImGui.SetTooltip(f.Reason);
        if (f.Timestamp is > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - f.Timestamp > 600000)
        { ImGui.SameLine(); ImGui.TextDisabled(Age(f.Timestamp.Value)); }
    }

    private void DrawPopup(QuartermasterRuntimeSnapshot runtime)
    {
        if (openPopup) { ImGui.OpenPopup("RQMarketLadder"); openPopup = false; }
        // The game theme's translucent popup background lets the right pane bleed through the ladder.
        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]; bg.W = 1;
        ImGui.PushStyleColor(ImGuiCol.PopupBg, bg);
        var open = ImGui.BeginPopup("RQMarketLadder");
        ImGui.PopStyleColor();
        if (!open) return;
        try
        {
            if (popup == null || !figures.TryGetValue(popup, out var f)) { ImGui.CloseCurrentPopup(); return; }
            var pair = Pair(popup);
            var age = f.Timestamp is > 0 ? " · " + Age(f.Timestamp.Value) + " ago" : "";
            ImGui.TextDisabled($"{popup.ItemName} {(pair.Hq ? "HQ" : "NQ")} · {snapshot?.Label}{age}");
            var world = snapshot?.Scopes.FirstOrDefault(s => s.Key == snapshot.Key)?.Kind != "world";
            if (ImGui.BeginTable("ladder", world ? 4 : 3))
            {
                void Row(decimal price, int qty, string where, string who)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted($"{price:N0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"×{qty}");
                    if (world) { ImGui.TableNextColumn(); ImGui.TextUnformatted(where); }
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(who);
                }
                var price = popup.UnitPrice.Value;
                var below = f.Competitors.Where(l => l.PricePerUnit < price).Take(11).ToArray();
                foreach (var l in below) Row(l.PricePerUnit, l.Quantity, l.WorldName, l.RetainerName);
                ImGui.PushStyleColor(ImGuiCol.Text, ListingWorkspace.Color(ListingStateTone.Info));
                Row(price, popup.Quantity, runtime.Owner.HomeWorldName, "yours");
                ImGui.PopStyleColor();
                foreach (var l in f.Competitors.Where(l => l.PricePerUnit > price).Take(Math.Min(2, 11 - below.Length)))
                    Row(l.PricePerUnit, l.Quantity, l.WorldName, l.RetainerName);
                ImGui.EndTable();
            }
            var quality = ListingRetainerProjection.Quality(popup);
            var count = session.Draft!.Assignments.Where(a => a.ItemId == pair.ItemId && a.Quality == quality).Sum(a => a.ListingCount);
            ImGui.Separator(); ImGui.TextDisabled($"All {count} slots"); ImGui.SameLine();
            ImGui.BeginDisabled(count == 0);
            ImGui.SetNextItemWidth(84);
            var enter = ImGui.InputText("##all-slots-price", ref input, 16, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            var set = ImGui.SmallButton("Set");
            ImGui.EndDisabled();
            if ((enter || set) && ListingPlanInput.Commit(input, ListingPlanInput.MaxUnitPrice) is var value and > 0)
            {
                foreach (var r in projected!)
                {
                    var edits = r.Slots.Where(s => s.Assignment is { } a && a.ItemId == pair.ItemId && a.Quality == quality && a.UnitPrice != value)
                        .Select(s => (s.Key, (ListingSlotPlan?)(ListingSlotPlan.Of(s.Assignment!) with { Price = value }))).ToArray();
                    if (edits.Length > 0) session.RewriteSlots(r.RetainerId, r.RetainerName, edits);
                }
                ImGui.CloseCurrentPopup();
            }
        }
        finally { ImGui.EndPopup(); }
    }
}
