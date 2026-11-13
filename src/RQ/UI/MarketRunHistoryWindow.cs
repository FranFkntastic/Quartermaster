using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RQ.Domain;
using RQ.Operations;

namespace RQ.UI;

internal sealed class MarketRunHistoryWindow(MarketRunHistoryStore history, Func<OwnerScope> currentOwner)
    : Window("Market run history###RQMarketRunHistory")
{
    private OwnerScope? owner;
    private long revision = -1;
    private IReadOnlyList<MarketRunRecord> records = [];
    private string? error;
    private Guid? selected;
    private int page;
    private int outputPage;

    public void Open() => IsOpen = true;
    public string? Warning => history.Warning;

    public MarketRunRecord? Latest(OwnerScope scope)
    {
        if (owner != scope)
        {
            owner = scope; revision = -1; records = []; selected = null; page = outputPage = 0;
        }
        if (revision != history.Revision)
        {
            // Capture before reading: a concurrent final write must invalidate this snapshot.
            var requestedRevision = history.Revision;
            try { records = history.Read(scope); revision = requestedRevision; error = null; }
            catch (Exception ex) { revision = requestedRevision; error = $"Could not read market run history: {ex.Message}"; }
        }
        return records.FirstOrDefault();
    }

    public override void Draw()
    {
        var scope = currentOwner();
        Latest(scope);
        if (!scope.HasStableIdentity) { ImGui.TextDisabled("Character identity unavailable."); return; }
        ImGui.TextUnformatted($"{scope.CharacterName} @ {scope.HomeWorldName}");
        ImGui.TextDisabled("Summaries only. Viewing history never acknowledges recovery or replays a run.");
        if (history.Warning is { } warning) ImGui.TextWrapped(warning);
        if (history.IsLoading) ImGui.TextDisabled("Loading saved run summaries...");
        if (error is not null)
        {
            ImGui.TextWrapped(error);
            if (ImGui.SmallButton("Retry reading history")) revision = -1;
        }
        if (records.Count == 0) { ImGui.TextDisabled("No recorded market runs for this character."); return; }
        const int pageSize = 20;
        page = Math.Clamp(page, 0, (records.Count - 1) / pageSize);
        ImGui.BeginDisabled(page == 0);
        if (ImGui.SmallButton("Newer runs")) page--;
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled((page + 1) * pageSize >= records.Count);
        if (ImGui.SmallButton("Older runs")) page++;
        ImGui.EndDisabled();
        if (ImGui.BeginChild("runs", new Vector2(0, 150), true))
            foreach (var record in records.Skip(page * pageSize).Take(pageSize))
                if (ImGui.Selectable($"{Time(record.StartedAtUtc)} · {record.State}###run:{record.Id}", selected == record.Id))
                { selected = record.Id; outputPage = 0; }
        ImGui.EndChild();
        var result = records.FirstOrDefault(r => r.Id == selected) ?? records[0];
        ImGui.TextWrapped($"Started: {Time(result.StartedAtUtc)} · Finished: {Time(result.FinishedAtUtc)}");
        ImGui.TextWrapped($"{result.State}: {result.Reason}");
        ImGui.TextWrapped(Summary(result));
        ImGui.TextDisabled("Needs-check describes this receipt, not the current acknowledgement state.");
        var lines = result.Results.Concat(result.NeedsCheck.Distinct().Select(id =>
                $"Needs check: {result.NeedsCheckNames.GetValueOrDefault(id) ?? result.Assignments.FirstOrDefault(a => a.RetainerId == id)?.RetainerName ?? id.ToString()}"))
            .Concat(result.Mutations.Select(m => $"{m.Intent.Kind}: {m.Intent.ItemName} × {m.Intent.Quantity:N0} units ({Retainer(result, m)}) · {m.Outcome?.ToString() ?? "Unknown (intent recorded)"} · {m.Code}: {m.Message}"))
            .ToArray();
        const int outputSize = 50;
        outputPage = Math.Clamp(outputPage, 0, Math.Max(0, (lines.Length - 1) / outputSize));
        ImGui.BeginDisabled(outputPage == 0);
        if (ImGui.SmallButton("Previous output")) outputPage--;
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled((outputPage + 1) * outputSize >= lines.Length);
        if (ImGui.SmallButton("Next output")) outputPage++;
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.TextDisabled($"Output {outputPage + 1} / {Math.Max(1, (lines.Length + outputSize - 1) / outputSize)}");
        if (ImGui.BeginChild("output", Vector2.Zero, true))
            foreach (var line in lines.Skip(outputPage * outputSize).Take(outputSize)) ImGui.TextWrapped(line);
        ImGui.EndChild();
    }

    internal static string Time(DateTime? time) => time is { } value ? $"{value.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC" : "Unknown (not recorded)";
    internal static string Retainer(MarketRunRecord record, MarketMutationReceipt receipt) =>
        !string.IsNullOrEmpty(receipt.RetainerName) ? receipt.RetainerName :
        record.NeedsCheckNames.GetValueOrDefault(receipt.Intent.RetainerId) ??
        record.Assignments.FirstOrDefault(a => a.RetainerId == receipt.Intent.RetainerId)?.RetainerName ?? "Retainer not recorded";

    internal static (string Label, string Detail)[] Unconfirmed(MarketRunRecord record)
    {
        var mutations = record.Mutations.Where(m => m.Outcome is null or MarketMutationOutcome.Indeterminate).ToArray();
        return mutations.Select(m => ($"{m.Intent.Kind.ToLowerInvariant()} ({m.Intent.ItemName}, {Retainer(record, m)})",
                $"{m.Code}: {m.Message}"))
            .Concat(record.NeedsCheck.Distinct().Where(id => mutations.All(m => m.Intent.RetainerId != id)).Select(id =>
                ($"item move ({record.NeedsCheckNames.GetValueOrDefault(id) ?? record.Assignments.FirstOrDefault(a => a.RetainerId == id)?.RetainerName ?? "Retainer not recorded"})", record.Reason)))
            .DefaultIfEmpty(("item move (details not recorded)", record.Reason)).ToArray();
    }
    internal static string Summary(MarketRunRecord result)
    {
        int Count(string kind) => result.Mutations.Count(m => m.Intent.Kind == kind && m.Outcome == MarketMutationOutcome.Committed);
        return $"Posted {Count("Post")} · Repriced {Count("Reprice")} · Pulled {Count("Pull")} listings · Still short {(result.ListingSummaryComplete ? result.StillShort.ToString("N0") : "unknown")} listings · No slot {result.NoSlot} listings · Inventory blocked {result.NoInventorySpace} listings · Gather blocked {result.GatherBlockedUnits} units · Split stacks {result.SplitStacks}";
    }
}
