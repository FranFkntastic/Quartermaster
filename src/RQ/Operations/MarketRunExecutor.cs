using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using System.Collections.Concurrent;
using RQ.Automation;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Operations;

public enum MarketMutationOutcome { FailedBeforeSend, Committed, Indeterminate, Unchanged }
public sealed record MarketMutationResult(MarketMutationOutcome Outcome, string Code = "", string Message = "");
public sealed record MarketPlayerInventory(IReadOnlyList<DalamudInventoryStack> Stacks, int FreeSlots);
/// <param name="AllowSaddlebag">False when this run already had a failed saddlebag attempt for the item.</param>
public sealed record MarketGatherTarget(uint ItemId, string ItemName, ItemQualityPolicy Quality, int Units, bool AllowSaddlebag = true);
internal sealed class MarketGatherIndeterminateException(ulong retainerId, string retainerName, string message) : Exception(message)
{
    public ulong RetainerId { get; } = retainerId;
    public string RetainerName { get; } = retainerName;
}
public sealed record MarketMutationIntent(string Kind, ulong RetainerId, string ItemName,
    RetainerMarketListingTarget? Listing, DalamudInventoryStack? Source, int Quantity, uint Price);

/// <summary>All reads are live. Implementations must not retry mutations.</summary>
public interface IMarketRunDriver
{
    int GatherInventoryBlockedUnits => 0;
    string ItemName(uint itemId);
    Task RequireRetainerListAsync(CancellationToken token);
    Task OpenAsync(ulong retainerId, string name, CancellationToken token);
    Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken token);
    Task<MarketPlayerInventory> PlayerAsync(CancellationToken token);
    Task<MarketMutationResult> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken token);
    Task<MarketMutationResult> PullAsync(RetainerMarketListingTarget listing, CancellationToken token);
    Task<MarketMutationResult> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken token);
    Task ReturnAsync(CancellationToken token);
    Task CloseListAsync(CancellationToken token);
    Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken token, Action<MarketMutationReceipt>? receiptChanged = null);
}

public interface IMarketRunDeadline
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action);
}

public sealed class MarketRunDeadline(TimeSpan timeout) : IMarketRunDeadline
{
    // Bell routing has its own two-minute budget; leave room for opening the list.
    public static readonly TimeSpan RetainerListTimeout = TimeSpan.FromSeconds(150);
    // Franthropy's reprice recovers pre-send selection failures for up to 40 attempts, each
    // bounded in frames (backoff 10, surface release <=360, quiet wait <=360, menu wait <=360,
    // label settle ~26). With the final release, the post-Adjust-Price editor wait (~360) and
    // the confirm observation (~360), the worst case is about 45,900 frames: ~12.8 min at
    // 60 fps, ~25.5 min at 30 fps. Forty minutes stays above that down to about 19 fps, so
    // this deadline never turns a pre-send recovery into an Indeterminate timeout.
    public static readonly TimeSpan RepriceTimeout = TimeSpan.FromMinutes(40);
    internal static readonly AsyncLocal<ConcurrentBag<Task>?> Calls = new();
    internal static bool HasUnsettled => Calls.Value?.Any(t => !t.IsCompleted) == true;
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // Give the primitive time to classify a cancellation after dispatch. A driver
        // ignoring cancellation cannot leave this executor waiting indefinitely.
        var task = action(deadline.Token);
        Calls.Value?.Add(task);
        return await task.WaitAsync(timeout + TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}

public sealed class MarketRunRecord
{
    // Null means the legacy receipt did not record this time, not the import time.
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public long PlanRevision { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public List<ListingPlanAssignment> Assignments { get; set; } = [];
    public string State { get; set; } = "Running";
    public string Reason { get; set; } = string.Empty;
    public List<MarketMutationReceipt> Mutations { get; set; } = [];
    public List<string> Results { get; set; } = [];
    public List<ulong> NeedsCheck { get; set; } = [];
    public Dictionary<ulong, string> NeedsCheckNames { get; set; } = [];
    public int StillShort { get; set; }
    public bool ListingSummaryComplete { get; set; }
    public int NoSlot { get; set; }
    public int NoInventorySpace { get; set; }
    public int GatherBlockedUnits { get; set; }
    public int SplitStacks { get; set; }
    /// <summary>Listings the end-of-run top-up pass set out to cover, from this run's final scans.</summary>
    public int TopUpShortfall { get; set; }
    public bool RequiresAcknowledgement => State == "Interrupted" || NeedsCheck.Count > 0 ||
        Mutations.Any(m => m.Outcome is null or MarketMutationOutcome.Indeterminate);
}

public sealed class MarketMutationReceipt
{
    public required MarketMutationIntent Intent { get; set; }
    // Null is deliberately persisted before dispatch: after reload it means unknown,
    // not failed, and it never authorizes replay.
    public MarketMutationOutcome? Outcome { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RetainerName { get; set; } = string.Empty;
    /// <summary>True when the end-of-run top-up pass dispatched this mutation.</summary>
    public bool TopUp { get; set; }
}

public sealed class MarketRunStore(string path)
{
    private readonly AtomicDocumentStore<MarketRunRecord> store = new(path);
    public MarketRunHistoryStore History { get; } = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "market-run-history"));
    public MarketRunRecord? Load()
    {
        if (!store.Exists) return null;
        var record = store.Load();
        if (record.State == "Running")
        {
            record.State = "Interrupted";
            record.Reason = "Reload interrupted the run; unresolved mutation outcome unknown.";
            foreach (var mutation in record.Mutations.Where(m => m.Outcome is null))
                if (!record.NeedsCheck.Contains(mutation.Intent.RetainerId)) record.NeedsCheck.Add(mutation.Intent.RetainerId);
            store.Save(record);
        }
        History.Save(record);
        return record;
    }
    public void Save(MarketRunRecord record) { store.Save(record); History.Save(record); }
    public void Clear()
    {
        if (!store.Exists) return;
        // History is advisory. An explicit acknowledgement must not depend on its I/O.
        try { History.Save(store.Load()); }
        catch (Exception exception) { History.ReportWarning($"Could not archive the acknowledged receipt: {exception.Message}"); }
        File.Delete(path);
    }
}

/// <summary>Owns one immutable plan snapshot and one lease across gather and listing passes.</summary>
public sealed class MarketRunExecutor(
    IMarketRunDriver driver, AutomationLease automation, Func<ListingPlan?> currentPlan,
    Action<MarketRunRecord> persist, AutoRetainerSuppression? suppression = null,
    IMarketRunDeadline? deadlines = null, Action? clear = null, IMarketRunDeadline? routeDeadlines = null,
    Action<string>? warning = null, Action<string>? information = null, IMarketRunDeadline? repriceDeadlines = null)
{
    private readonly IMarketRunDeadline deadline = deadlines ?? new MarketRunDeadline(TimeSpan.FromSeconds(15));
    // A caller-supplied deadline (tests) also governs reprices unless one is given for them.
    private readonly IMarketRunDeadline repriceDeadline = repriceDeadlines ?? deadlines ?? new MarketRunDeadline(MarketRunDeadline.RepriceTimeout);
    private readonly IMarketRunDeadline routeDeadline = routeDeadlines ?? new MarketRunDeadline(MarketRunDeadline.RetainerListTimeout);
    private int running;
    private int cancelled;
    private bool driverUnsettled;
    private bool persistenceFailed;
    private IDisposable? runLease;
    // Per run: the latest final-scan shortfall per assignment and the listings that scan saw.
    // Only these fresh observations drive the top-up pass, never the listing cache.
    private readonly Dictionary<Guid, int> shortByAssignment = [];
    private readonly Dictionary<ulong, IReadOnlyList<RetainerMarketListingTarget>> freshListings = [];
    private bool topUpActive;
    private readonly object persistenceGate = new();
    public bool IsRunning => Volatile.Read(ref running) != 0;
    private bool disposed;
    public Task? ActiveTask { get; private set; }
    public string? PersistenceError { get; private set; }
    public bool CanStart => !disposed && !IsRunning && automation.CanAcquire && Result?.RequiresAcknowledgement != true && PersistenceError is null;
    public void Clear()
    {
        lock (persistenceGate)
        {
            if (disposed || IsRunning || automation.IsHeld) return;
            try
            {
                clear?.Invoke(); Result = null; PersistenceError = null;
            }
            catch (Exception e) { PersistenceError = $"Could not clear result: {e.Message}"; }
        }
    }
    public void CancelAndWait(TimeSpan timeout)
    {
        lock (persistenceGate)
        {
            Cancel();
            disposed = true;
        }
        try { ActiveTask?.Wait(timeout); } catch (AggregateException) { }
    }
    public string Status { get; private set; } = string.Empty;
    public MarketRunRecord? Result { get; private set; }
    public void Restore(MarketRunRecord? record) => Result = record;
    public void Cancel() => Interlocked.Exchange(ref cancelled, 1);

    public Task RunAsync(ListingPlan plan, MarketRunEvaluation evaluation, bool draftDirty = false)
    {
        lock (persistenceGate)
        {
            if (!CanStart) return Task.CompletedTask;
            return ActiveTask = RunCoreAsync(plan, evaluation, draftDirty);
        }
    }

    private async Task RunCoreAsync(ListingPlan plan, MarketRunEvaluation evaluation, bool draftDirty)
    {
        if (draftDirty || !evaluation.IsComplete || evaluation.PlanId != plan.Id || evaluation.PlanRevision != plan.Revision ||
            !evaluation.Retainers.Any(r => r.Assignments.Any(a => a.ShortListings.Value > 0 || a.RepriceListings.Value > 0))) return;
        if (!automation.TryAcquire("market run", out var lease)) return;
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) { lease!.Dispose(); return; }
            var calls = new ConcurrentBag<Task>();
            MarketRunDeadline.Calls.Value = calls;
            Interlocked.Exchange(ref cancelled, 0);
            runLease = lease;
            driverUnsettled = false;
            persistenceFailed = false;
            shortByAssignment.Clear();
            freshListings.Clear();
            topUpActive = false;
            var record = new MarketRunRecord { StartedAtUtc = DateTime.UtcNow, PlanId = plan.Id, PlanRevision = plan.Revision, Owner = plan.Owner with { },
                Assignments = plan.Assignments.Where(a => a.Enabled).Select(ListingPlanCatalog.Copy).ToList() };
            AutoRetainerSuppression.Scope? scope = null;
            var sessionStarted = false;
            var scannedRetainers = new HashSet<ulong>();
            try
            {
                Persist(record);
                Check(record);
                if (suppression is not null)
                {
                    var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    while (suppression.IsAvailable && suppression.IsBusy)
                    {
                        Check(record);
                        if (DateTime.UtcNow >= until) throw new InvalidOperationException("AutoRetainer remained busy.");
                        Status = "Waiting for AutoRetainer";
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                    if (suppression.IsAvailable) scope = suppression.Acquire();
                }
                Status = "Gathering stock";
                sessionStarted = true;
                var targets = evaluation.Items.Where(i => i.RefillUnits.Value > 0)
                    .Select(i => new MarketGatherTarget(i.ItemId, i.ItemName, i.Quality, i.RefillUnits.Value)).ToArray();
                // Gather owns per-call deadlines, not a single deadline for a multi-retainer route.
                await GatherAsync(record, targets).ConfigureAwait(false);
                Check(record);
                await routeDeadline.RunAsync(async t => { await driver.RequireRetainerListAsync(t).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                var visits = record.Assignments.GroupBy(a => a.RetainerId)
                    .Select(g => new RetainerVisit(g.Key, g.Select(a => a.RetainerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? $"Retainer {g.Key}", g.ToArray()))
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToArray();
                foreach (var visit in visits)
                    await VisitRetainerAsync(record, visit, scannedRetainers, reprice: true).ConfigureAwait(false);
                await TopUpAsync(record, visits, scannedRetainers).ConfigureAwait(false);
                record.State = record.NeedsCheck.Count > 0 ? "Stopped" : "Completed";
                if (record.NeedsCheck.Count > 0) record.Reason = "Needs a check: reprice outcome unknown.";
            }
            catch (OperationCanceledException) when (Volatile.Read(ref cancelled) != 0)
            { record.State = "Cancelled"; record.Reason = "Cancelled between steps."; }
            catch (MarketGatherIndeterminateException e)
            { record.State = "Stopped"; record.Reason = e.Message; record.NeedsCheck.Add(e.RetainerId); record.NeedsCheckNames[e.RetainerId] = e.RetainerName; }
            catch (Exception e) { record.State = "Stopped"; record.Reason = e.Message; }
            finally
            {
                try
                {
                    // Never overlap an abandoned call, touch a new character, or use an unloaded session.
                    if (sessionStarted && CanCleanUp(record))
                    {
                        try { await Step(driver.CloseListAsync).ConfigureAwait(false); }
                        catch (Exception e) { Add(record, $"Could not close retainer list: {e.Message}"); }
                    }
                    record.ListingSummaryComplete = record.Assignments.Select(a => a.RetainerId).Distinct().All(scannedRetainers.Contains);
                    record.FinishedAtUtc = DateTime.UtcNow;
                    Status = record.State + (record.Reason.Length == 0 ? string.Empty : $": {record.Reason}");
                    Result = record;
                    Persist(record);
                }
                finally
                {
                    var pending = calls.Where(t => !t.IsCompleted).ToArray();
                    void Release() { try { scope?.Dispose(); } finally { lease!.Dispose(); } }
                    try
                    {
                        if (pending.Length == 0) Release();
                        else _ = Task.WhenAll(pending).ContinueWith(t => { _ = t.Exception; Release(); },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    finally { MarketRunDeadline.Calls.Value = null; Volatile.Write(ref running, 0); }
                }
            }
        }
    }

    private sealed record RetainerVisit(ulong Id, string Name, ListingPlanAssignment[] Assignments);

    private async Task GatherAsync(MarketRunRecord record, IReadOnlyList<MarketGatherTarget> targets)
    {
        // Gather owns per-call deadlines, not a single deadline for a multi-retainer route.
        var reports = await driver.GatherAsync(targets, runLease!, () => Check(record), () => HasSessionAuthority(record), CancellationToken.None, receipt =>
        {
            if (!record.Mutations.Contains(receipt)) { receipt.TopUp = topUpActive; record.Mutations.Add(receipt); }
            if (receipt.Outcome is not null)
                Add(record, $"{receipt.Intent.Kind} {receipt.Intent.ItemName} × {receipt.Intent.Quantity}: {receipt.Outcome} · {receipt.Code}: {receipt.Message}");
            Persist(record);
        }).ConfigureAwait(false);
        foreach (var report in reports) Add(record, report);
        record.GatherBlockedUnits += driver.GatherInventoryBlockedUnits;
    }

    private async Task VisitRetainerAsync(MarketRunRecord record, RetainerVisit visit, HashSet<ulong> scannedRetainers, bool reprice)
    {
        Check(record);
        Status = topUpActive ? $"Top-up · {visit.Name}" : visit.Name;
        var (id, name, assignments) = (visit.Id, visit.Name, visit.Assignments);
        // A revisit must rescan; a failed rescan leaves the summary incomplete, not stale.
        scannedRetainers.Remove(id);
        var opened = false;
        try
        {
            await Step(t => driver.OpenAsync(id, name, t)).ConfigureAwait(false);
            opened = true;
            await ListRetainer(record, id, name, assignments, reprice).ConfigureAwait(false);
        }
        catch (RetainerStoppedException) { }
        catch (OperationCanceledException) { throw; }
        catch (RunStoppedException) { throw; }
        catch (Exception e) when (!persistenceFailed && !driverUnsettled && !MarketRunDeadline.HasUnsettled)
        {
            warning?.Invoke($"MarketRunRetainerVisitEnded: retainer={name}{(topUpActive ? " (top-up)" : string.Empty)}: {e.Message}");
            Add(record, $"{name}: {e.Message}");
        }
        finally
        {
            // A best-effort final scan is also required for an indeterminate mutation.
            try
            {
                if (opened && CanCleanUp(record))
                {
                    var final = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                    if (CanCleanUp(record))
                    {
                        var matches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, final));
                        foreach (var a in assignments)
                        {
                            var present = matches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
                            shortByAssignment[a.Id] = Math.Max(0, a.ListingCount - present);
                        }
                        // Still short is the latest fresh scan per assignment, so a top-up revisit replaces it.
                        record.StillShort = shortByAssignment.Values.Sum();
                        freshListings[id] = final;
                        scannedRetainers.Add(id);
                    }
                }
            }
            catch (Exception e)
            {
                Add(record, $"{name}: final scan unavailable ({e.Message})");
                if (MarketRunDeadline.HasUnsettled)
                {
                    driverUnsettled = true;
                    throw new RunStoppedException("Final listing scan has not settled; run stopped to prevent overlapping session calls.");
                }
            }
            // Recheck after the final scan: unload or character change can
            // happen while that read is in flight, too.
            if (CanCleanUp(record))
                await Step(driver.ReturnAsync).ConfigureAwait(false);
        }
        Check(record);
    }

    private sealed record TopUpLine(RetainerVisit Visit, ListingPlanAssignment Assignment, int Listings)
    {
        public int Units => Listings * Assignment.QuantityPerListing;
        public string Describe => $"{Assignment.ItemName} ({Assignment.Quality}) at {Visit.Name}: short {Listings} listings × {Assignment.QuantityPerListing} = {Units} units";
    }

    /// <summary>
    /// One extra gather → post pass for shortfalls this run's own final scans found. Never a
    /// loop, never after a needs-check, and never a resend: every mutation is a new intent
    /// that the same verified machinery dispatches and classifies.
    /// </summary>
    private async Task TopUpAsync(MarketRunRecord record, IReadOnlyList<RetainerVisit> visits, HashSet<ulong> scannedRetainers)
    {
        // An unproven saddlebag move blocks only its item (below); it has no retainer to check.
        if (record.NeedsCheck.Count > 0 || record.Mutations.Any(m => m.Intent.Kind != MarketRunLiveDriver.SaddlebagKind && m.Outcome is null or MarketMutationOutcome.Indeterminate))
        {
            if (record.StillShort > 0)
            {
                var reason = $"MarketRunTopUpSkipped: run={record.Id:N} reason=NeedsCheck; a mutation outcome is unknown, so {record.StillShort} short listings are left for the check.";
                warning?.Invoke(reason);
                Add(record, $"Top-up skipped: needs a check first ({record.StillShort} short listings left)");
            }
            return;
        }
        Check(record);
        // The top-up never repeats work this run already attempted and failed, even when it
        // failed before send: a post or pull that did not commit rules out its retainer, and a
        // retainer retrieval that did not commit rules out its item. A saddlebag move that may
        // have happened unproven rules out its item; any other failed saddlebag attempt
        // (refused or not enqueued, nothing moved) rules out only the saddlebag for that item.
        var failedPosting = record.Mutations.Where(m => m.Intent.Kind is "Post" or "Pull" && m.Outcome != MarketMutationOutcome.Committed)
            .GroupBy(m => m.Intent.RetainerId).ToDictionary(g => g.Key, g => g.First());
        var failedRetrieval = record.Mutations.Where(m => m.Intent.Kind == "Retrieval" && m.Outcome != MarketMutationOutcome.Committed && m.Intent.Source is not null)
            .GroupBy(m => (m.Intent.Source!.ItemId, m.Intent.Source.IsHighQuality)).ToDictionary(g => g.Key, g => g.First());
        // Saddlebag lines may draw either quality, so saddlebag failures are keyed by item.
        var saddlebagAttempts = record.Mutations.Where(m => m.Intent.Kind == MarketRunLiveDriver.SaddlebagKind && m.Intent.Source is not null).ToArray();
        var unprovenSaddlebag = saddlebagAttempts.Where(m => m.Outcome is null or MarketMutationOutcome.Indeterminate)
            .GroupBy(m => m.Intent.Source!.ItemId).ToDictionary(g => g.Key, g => g.First());
        var failedSaddlebag = saddlebagAttempts.Where(m => m.Outcome is MarketMutationOutcome.FailedBeforeSend or MarketMutationOutcome.Unchanged)
            .GroupBy(m => m.Intent.Source!.ItemId).ToDictionary(g => g.Key, g => g.First());
        MarketMutationReceipt? PriorItemFailure(ListingPlanAssignment a) =>
            failedRetrieval.GetValueOrDefault((a.ItemId, a.Quality == ItemQualityPolicy.HqOnly)) ?? unprovenSaddlebag.GetValueOrDefault(a.ItemId);
        var lines = new List<TopUpLine>();
        var uncovered = new Dictionary<Guid, string>();
        foreach (var visit in visits)
        {
            var shortHere = visit.Assignments.Where(a => a.QuantityPerListing > 0 && a.ListingCount > 0).ToArray();
            if (freshListings.ContainsKey(visit.Id))
            {
                var excluded = shortHere.Where(a => shortByAssignment.GetValueOrDefault(a.Id) > 0 &&
                    (failedPosting.ContainsKey(visit.Id) || PriorItemFailure(a) is not null)).ToArray();
                foreach (var a in excluded)
                {
                    var prior = failedPosting.GetValueOrDefault(visit.Id) ?? PriorItemFailure(a)!;
                    UncoveredWarning(record, a, visit.Name, shortByAssignment[a.Id], "PriorMutationFailed",
                        $"{prior.Intent.Kind} {prior.Intent.ItemName} × {prior.Intent.Quantity} at {(prior.RetainerName.Length > 0 ? prior.RetainerName : prior.Intent.RetainerId.ToString())} " +
                        $"ended {prior.Outcome?.ToString() ?? "unresolved"} ({prior.Code}) earlier this run and is not repeated");
                }
                shortHere = shortHere.Except(excluded).ToArray();
            }
            if (!freshListings.TryGetValue(visit.Id, out var listings))
            {
                // No fresh observation this run: the cache is never a top-up source of truth.
                if (shortHere.Length > 0)
                {
                    warning?.Invoke($"MarketRunTopUpShortfallUncovered: reason=NoFreshObservation retainer={visit.Name} items={string.Join(", ", shortHere.Select(a => a.ItemName).Distinct())}; " +
                        "the retainer's listings were not scanned at the end of its visit, so its shortfall is unknown and the cache is not used.");
                    record.Results.Add($"Top-up · uncovered · {visit.Name}: NoFreshObservation");
                }
                continue;
            }
            var room = Math.Max(0, MarketRunPlanner.ListingCapacity - listings.Count) +
                MarketRunPlanner.SelectListingsToPull(Rows(visit.Id, visit.Name, listings), visit.Assignments, listings.Count + MarketRunPlanner.ListingCapacity).Count;
            foreach (var a in shortHere)
            {
                var missing = shortByAssignment.GetValueOrDefault(a.Id);
                if (missing == 0) continue;
                var fits = Math.Min(missing, room);
                room -= fits;
                if (fits < missing)
                {
                    uncovered[a.Id] = "NoListingSlot";
                    UncoveredWarning(record, a, visit.Name, missing - fits, "NoListingSlot", "the retainer has no free or pullable listing slot");
                }
                if (fits > 0) lines.Add(new(visit, a, fits));
            }
        }
        if (lines.Count == 0) return;

        topUpActive = true;
        try
        {
            record.TopUpShortfall = lines.Sum(l => l.Listings);
            Persist(record);
            var keys = lines.GroupBy(l => (l.Assignment.ItemId, l.Assignment.Quality)).ToArray();
            int Held(MarketPlayerInventory inventory, uint itemId, ItemQualityPolicy quality) =>
                inventory.Stacks.Where(s => s.ItemId == itemId && s.IsHighQuality == (quality == ItemQualityPolicy.HqOnly)).Sum(s => s.Quantity);
            var before = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
            information?.Invoke($"MarketRunTopUpStarted: run={record.Id:N} shortfall={record.TopUpShortfall} listings; " +
                string.Join("; ", lines.Select(l => l.Describe)) + " | sources: " +
                string.Join("; ", keys.Select(k =>
                {
                    var need = k.Sum(l => l.Units);
                    var held = Held(before, k.Key.ItemId, k.Key.Quality);
                    return $"{k.First().Assignment.ItemName} ({k.Key.Quality}) bags {held}/{need}, " +
                        (held >= need ? "covered by bags" : $"{need - held} units from saddlebag or retainers");
                })));
            Add(record, $"Started: {record.TopUpShortfall} short listings from this run's final scans");
            Status = "Top-up · gathering stock";
            var retrievalsBefore = record.Mutations.Count;
            var blockedBefore = record.GatherBlockedUnits;
            foreach (var k in keys.Where(k => failedSaddlebag.ContainsKey(k.Key.ItemId)))
            {
                var prior = failedSaddlebag[k.Key.ItemId];
                warning?.Invoke($"MarketRunTopUpSaddlebagExcluded: item={k.First().Assignment.ItemName} ({k.Key.Quality}): saddlebag attempt × {prior.Intent.Quantity} " +
                    $"ended {prior.Outcome} ({prior.Code}) earlier this run and is not repeated; nothing moved, so retainers may still cover this item.");
            }
            await GatherAsync(record, keys.Select(k => new MarketGatherTarget(k.Key.ItemId, k.First().Assignment.ItemName, k.Key.Quality, k.Sum(l => l.Units),
                    AllowSaddlebag: !failedSaddlebag.ContainsKey(k.Key.ItemId))).ToArray())
                .ConfigureAwait(false);
            Check(record);
            var gatherBlocked = record.GatherBlockedUnits - blockedBefore;
            var after = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
            var stocked = new HashSet<(uint, ItemQualityPolicy)>();
            foreach (var k in keys)
            {
                var need = k.Sum(l => l.Units);
                var held = Held(after, k.Key.ItemId, k.Key.Quality);
                if (held > 0) stocked.Add(k.Key);
                if (held >= need) continue;
                var (code, detail) = gatherBlocked > 0
                    ? ("NoInventorySpace", "player inventory had no free slot to gather into")
                    : ("NoStock", "no further stock was found in the bags, saddlebag or cached retainer inventories");
                foreach (var l in k) uncovered.TryAdd(l.Assignment.Id, code);
                warning?.Invoke($"MarketRunTopUpShortfallUncovered: reason={code} item={k.First().Assignment.ItemName} ({k.Key.Quality}) quantity={need - held} units " +
                    $"(bags {held}/{need}) retainers={string.Join(", ", k.Select(l => l.Visit.Name).Distinct())}; {detail}.");
            }

            await routeDeadline.RunAsync(async t => { await driver.RequireRetainerListAsync(t).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            foreach (var visit in lines.Select(l => l.Visit).Distinct())
            {
                // A visit with nothing in hand to post would only open and close the retainer.
                if (!lines.Any(l => l.Visit == visit && stocked.Contains((l.Assignment.ItemId, l.Assignment.Quality)))) continue;
                await VisitRetainerAsync(record, visit, scannedRetainers, reprice: false).ConfigureAwait(false);
            }

            var gathered = record.Mutations.Skip(retrievalsBefore)
                .Where(m => m.Intent.Kind is "Retrieval" or MarketRunLiveDriver.SaddlebagKind && m.Outcome == MarketMutationOutcome.Committed).ToArray();
            var remaining = 0;
            var summary = new List<string>();
            foreach (var l in lines)
            {
                var left = Math.Min(l.Listings, shortByAssignment.GetValueOrDefault(l.Assignment.Id));
                var posted = record.Mutations.Count(m => m.TopUp && m.Intent.Kind == "Post" && m.Outcome == MarketMutationOutcome.Committed &&
                    m.Intent.RetainerId == l.Visit.Id && m.Intent.ItemName == l.Assignment.ItemName);
                remaining += left;
                summary.Add($"{l.Assignment.ItemName} at {l.Visit.Name}: posted {posted}, still short {left}");
                if (left > 0)
                    UncoveredWarning(record, l.Assignment, l.Visit.Name, left, uncovered.GetValueOrDefault(l.Assignment.Id) ?? "NotPosted",
                        "stock was in hand but the visit did not post it; see the run results for the visit's stop reason");
            }
            var sources = keys.Select(k =>
            {
                // Committed receipts name each source: "Saddlebag" or the retainer.
                var moved = gathered.Where(m => m.Intent.Source is { } s && s.ItemId == k.Key.ItemId).ToArray();
                return $"{k.First().Assignment.ItemName}: bags {Held(before, k.Key.ItemId, k.Key.Quality)}" +
                    string.Concat(moved.GroupBy(m => m.RetainerName).Select(g => $", {g.Key} {g.Sum(m => m.Intent.Quantity)}"));
            });
            information?.Invoke($"MarketRunTopUpCompleted: run={record.Id:N} shortfall={record.TopUpShortfall} remaining={remaining}; " +
                string.Join("; ", summary) + " | sources: " + string.Join("; ", sources));
            Add(record, $"Completed: {record.TopUpShortfall - remaining} of {record.TopUpShortfall} short listings covered");
        }
        finally { topUpActive = false; }
    }

    private void UncoveredWarning(MarketRunRecord record, ListingPlanAssignment a, string retainer, int listings, string code, string detail)
    {
        warning?.Invoke($"MarketRunTopUpShortfallUncovered: reason={code} item={a.ItemName} ({a.Quality}) quantity={listings} listings × {a.QuantityPerListing} retainer={retainer}; {detail}.");
        record.Results.Add($"Top-up · uncovered · {a.ItemName} at {retainer}: {code} {listings}");
    }

    private async Task ListRetainer(MarketRunRecord record, ulong id, string name, ListingPlanAssignment[] assignments, bool reprice)
    {
        var listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        int? previousPriceDifferences = null;
        // Listings whose reprice was proven not to have been sent this visit. They stay at
        // their old price; the rest of the visit (other reprices, posts) still runs.
        var leftUnchanged = new List<RetainerMarketListingTarget>();
        int? unsentBudget = null;
        while (reprice)
        {
            Check(record);
            listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
            var priceDifferences = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings))
                .Where(m => m.Kind == ListingMatchKind.PriceDiffers &&
                    !leftUnchanged.Contains(listings.FirstOrDefault(l => l.SlotIndex == m.Listing.SlotIndex)!)).ToArray();
            if (priceDifferences.Length == 0) break;
            // Unsent reprices are bounded by the visit's first count of price differences, so a
            // listing that keeps shifting slots cannot loop the visit.
            unsentBudget ??= priceDifferences.Length;
            // Slot numbers are locations, not listing identities. Fresh work must
            // converge instead of skipping a different listing that reused a slot.
            if (previousPriceDifferences is { } previous && priceDifferences.Length >= previous)
                throw new RunStoppedException("Repricing did not converge on the planned prices; run stopped.");
            previousPriceDifferences = priceDifferences.Length;
            var match = priceDifferences[0];
            var assignment = assignments.Single(a => a.Id == match.AssignmentId);
            var listing = listings.FirstOrDefault(l => l.SlotIndex == match.Listing.SlotIndex);
            if (listing is null) { Add(record, $"{assignment.ItemName}: no longer present"); break; }
            var outcome = await Mutate(record, new("Reprice", id, assignment.ItemName, listing, null, listing.Quantity, (uint)assignment.UnitPrice),
                t => driver.RepriceAsync(listing, (uint)assignment.UnitPrice, t)).ConfigureAwait(false);
            if (outcome == MarketMutationOutcome.Unchanged)
            {
                // Nothing was dispatched (Mutate logged why), so the remaining work here is
                // safe. This exact listing is not resent this visit. A sale or slot shift
                // changes the listing set, so convergence is measured afresh from here.
                leftUnchanged.Add(listing);
                previousPriceDifferences = null;
                if (leftUnchanged.Count >= unsentBudget)
                {
                    warning?.Invoke($"MarketRunRepricePassEnded: {leftUnchanged.Count} reprices at {name} were not sent; " +
                        "the remaining price differences are left for the next run, continuing with posts.");
                    break;
                }
            }
        }

        listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        var matches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings));
        var player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
        var allocations = new List<(ListingPlanAssignment Assignment, int Count)>();
        var remaining = player.Stacks.ToDictionary(s => (s.Container, s.SlotIndex), s => s.Quantity);
        foreach (var a in assignments)
        {
            var held = matches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
            var missing = Math.Max(0, a.ListingCount - held);
            var stock = player.Stacks.Where(s => s.ItemId == a.ItemId && s.IsHighQuality == (a.Quality == ItemQualityPolicy.HqOnly)).ToArray();
            var availableUnits = stock.Sum(s => remaining[(s.Container, s.SlotIndex)]);
            var backed = 0;
            foreach (var s in stock)
            {
                var key = (s.Container, s.SlotIndex);
                var take = Math.Min(missing - backed, remaining[key] / a.QuantityPerListing);
                remaining[key] -= take * a.QuantityPerListing;
                backed += take;
            }
            allocations.Add((a, backed));
            var split = Math.Min(missing, availableUnits / a.QuantityPerListing) - backed;
            if (split > 0) { record.SplitStacks += split; Add(record, $"{a.ItemName}: split across stacks {split}"); }
        }
        while (true)
        {
            Check(record);
            player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
            listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
            var liveMatches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings));
            var available = player.Stacks.ToDictionary(s => (s.Container, s.SlotIndex), s => s.Quantity);
            var backed = 0;
            foreach (var (a, allocated) in allocations)
            {
                var held = liveMatches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
                var needed = Math.Min(allocated, Math.Max(0, a.ListingCount - held));
                foreach (var s in player.Stacks.Where(s => s.ItemId == a.ItemId && s.IsHighQuality == (a.Quality == ItemQualityPolicy.HqOnly)))
                {
                    var key = (s.Container, s.SlotIndex);
                    var take = Math.Min(needed, available[key] / a.QuantityPerListing);
                    available[key] -= take * a.QuantityPerListing;
                    backed += take;
                    needed -= take;
                }
            }
            var room = Math.Max(0, backed - (MarketRunPlanner.ListingCapacity - listings.Count));
            if (room == 0) break;
            if (player.FreeSlots == 0) { record.NoInventorySpace += room; Add(record, $"{name}: blocked by inventory space {room}"); break; }
            var pull = MarketRunPlanner.SelectListingsToPull(Rows(id, name, listings), assignments, backed).FirstOrDefault();
            if (pull is null) break;
            var target = listings.FirstOrDefault(l => l.SlotIndex == pull.Listing.SlotIndex);
            if (target is null) { Add(record, "Pull: no longer present"); break; }
            await Mutate(record, new("Pull", id, driver.ItemName(target.ItemId), target, null, target.Quantity, target.UnitPrice ?? 0),
                t => driver.PullAsync(target, t)).ConfigureAwait(false);
            listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        }
        foreach (var (a, refill) in allocations)
        {
            for (var n = 0; n < refill; n++)
            {
                Check(record);
                listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                var held = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings))
                    .Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
                if (held >= a.ListingCount) break;
                if (listings.Count >= MarketRunPlanner.ListingCapacity) { record.NoSlot += refill - n; break; }
                player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
                var stock = player.Stacks.Where(s => s.ItemId == a.ItemId && s.IsHighQuality == (a.Quality == ItemQualityPolicy.HqOnly)).ToArray();
                var source = stock.FirstOrDefault(s => s.Quantity >= a.QuantityPerListing);
                if (source is null)
                {
                    if (stock.Sum(s => s.Quantity) >= a.QuantityPerListing) record.SplitStacks += refill - n;
                    Add(record, $"{a.ItemName}: {(stock.Sum(s => s.Quantity) >= a.QuantityPerListing ? "split across stacks" : "still short for stock")} {refill - n}");
                    break;
                }
                // Re-read the exact physical source; inventory indices can move after any mutation.
                var verify = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
                if (!verify.Stacks.Contains(source)) { Add(record, $"{a.ItemName}: source stack changed; not posted"); continue; }
                await Mutate(record, new("Post", id, a.ItemName, null, source, a.QuantityPerListing, (uint)a.UnitPrice),
                    t => driver.PostAsync(source, a.QuantityPerListing, (uint)a.UnitPrice, t)).ConfigureAwait(false);
            }
        }
    }

    private async Task<MarketMutationOutcome> Mutate(MarketRunRecord record, MarketMutationIntent intent, Func<CancellationToken, Task<MarketMutationResult>> action)
    {
        Check(record);
        var receipt = new MarketMutationReceipt { Intent = intent,
            RetainerName = record.Assignments.FirstOrDefault(a => a.RetainerId == intent.RetainerId)?.RetainerName ?? string.Empty, TopUp = topUpActive };
        record.Mutations.Add(receipt);
        try { Persist(record); Check(record); } // No dispatch without durable intent and current authority.
        catch (Exception e)
        {
            receipt.Outcome = MarketMutationOutcome.FailedBeforeSend;
            receipt.Code = "StoppedBeforeSend"; receipt.Message = e.Message;
            Warn();
            Add(record, $"{intent.Kind} {intent.ItemName}: {receipt.Code}: {receipt.Message}");
            if (e is OperationCanceledException) throw;
            throw new RunStoppedException(e.Message);
        }
        try
        {
            var result = await (intent.Kind == "Reprice" ? repriceDeadline : deadline).RunAsync(action).ConfigureAwait(false);
            receipt.Outcome = result.Outcome; receipt.Code = result.Code; receipt.Message = result.Message;
        }
        catch (RetainerMarketMutationIndeterminateException e)
        { receipt.Outcome = MarketMutationOutcome.Indeterminate; receipt.Code = e.Code; receipt.Message = e.Message; }
        catch (OperationCanceledException e)
        { receipt.Outcome = MarketMutationOutcome.FailedBeforeSend; receipt.Code = "MutationCancelledBeforeSend"; receipt.Message = e.Message; }
        catch (TimeoutException e)
        {
            driverUnsettled = true;
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            receipt.Code = "MutationDeadlineExpired"; receipt.Message = e.Message;
            Warn();
            Add(record, $"{intent.Kind} {intent.ItemName}: {receipt.Code}: {receipt.Message}");
            record.NeedsCheck.Add(intent.RetainerId);
            Persist(record);
            throw new InvalidOperationException("Mutation deadline expired without a settled driver; stopped to prevent overlapping requests.");
        }
        catch (Exception e)
        {
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            receipt.Code = "MutationFaulted"; receipt.Message = e.Message;
        }
        if (receipt.Outcome != MarketMutationOutcome.Committed) Warn();
        // A failed reprice has no three-way primitive outcome; verify the exact
        // expected after-state before classifying it, without ever sending again.
        if (intent.Kind != "Reprice" && receipt.Outcome == MarketMutationOutcome.Indeterminate && !record.NeedsCheck.Contains(intent.RetainerId))
            record.NeedsCheck.Add(intent.RetainerId);
        Persist(record);
        if (intent.Kind == "Reprice" && receipt.Outcome != MarketMutationOutcome.Committed)
        {
            // Only a price change that was actually dispatched can be uncertain. A pre-send
            // failure is Unchanged whatever the listing did since, including selling.
            var notSent = receipt.Outcome == MarketMutationOutcome.FailedBeforeSend;
            IReadOnlyList<RetainerMarketListingTarget> live;
            try
            {
                if (!CanCleanUp(record)) throw new RunStoppedException(notSent ? "Run authority lost after an unsent reprice." : "Reprice outcome unknown; run authority lost.");
                live = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                if (!CanCleanUp(record)) throw new RunStoppedException(notSent ? "Run authority lost after an unsent reprice." : "Reprice outcome unknown; run authority lost.");
            }
            catch (Exception e)
            {
                if (notSent)
                {
                    receipt.Outcome = MarketMutationOutcome.Unchanged;
                    warning?.Invoke($"MarketRunRepriceObservationUnavailable: {intent.ItemName} × {intent.Quantity} slot {intent.Listing!.SlotIndex} at {receipt.RetainerName}: " +
                        $"reprice failed before send ({receipt.Code}); the re-scan failed ({e.Message}). Nothing was sent, so no check is needed.");
                    Persist(record);
                    Add(record, $"{intent.Kind} {intent.ItemName} × {intent.Quantity}: {receipt.Outcome} · {receipt.Code}: {receipt.Message}");
                    throw;
                }
                receipt.Outcome = MarketMutationOutcome.Indeterminate;
                record.NeedsCheck.Add(intent.RetainerId);
                Persist(record);
                throw;
            }
            var original = intent.Listing!;
            if (live.Contains(original with { UnitPrice = intent.Price })) receipt.Outcome = MarketMutationOutcome.Committed;
            else if (!notSent) receipt.Outcome = MarketMutationOutcome.Indeterminate;
            else
            {
                receipt.Outcome = MarketMutationOutcome.Unchanged;
                if (live.Contains(original))
                    warning?.Invoke($"MarketRunRepriceSkipped: {intent.ItemName} × {original.Quantity} slot {original.SlotIndex} at {receipt.RetainerName} left at {original.UnitPrice?.ToString() ?? "unknown"} gil " +
                        $"(planned {intent.Price}) after {receipt.Code}; not resent this visit, continuing with the rest of this retainer.");
                else
                    warning?.Invoke($"MarketRunRepriceListingChanged: {intent.ItemName} × {original.Quantity} at {original.UnitPrice?.ToString() ?? "unknown"} gil slot {original.SlotIndex} at {receipt.RetainerName}: " +
                        $"{DescribeChange(live, original)}. Reprice failed before send ({receipt.Code}); nothing was sent, continuing with the rest of this retainer.");
            }
        }
        if (receipt.Outcome == MarketMutationOutcome.Indeterminate && !record.NeedsCheck.Contains(intent.RetainerId)) record.NeedsCheck.Add(intent.RetainerId);
        Persist(record);
        Add(record, $"{intent.Kind} {intent.ItemName} × {intent.Quantity}: {receipt.Outcome} · {receipt.Code}: {receipt.Message}{(intent.Kind == "Pull" && receipt.Outcome == MarketMutationOutcome.Committed ? " (player inventory)" : string.Empty)}");
        if (receipt.Outcome == MarketMutationOutcome.Indeterminate && intent.Kind != "Reprice")
            throw new RunStoppedException("Needs a check: inventory mutation outcome unknown.");
        // A reprice proven Unchanged dispatched nothing; the caller keeps working at this retainer.
        if (intent.Kind == "Reprice" && receipt.Outcome == MarketMutationOutcome.Unchanged) return MarketMutationOutcome.Unchanged;
        if (receipt.Outcome != MarketMutationOutcome.Committed) throw new RetainerStoppedException();
        return MarketMutationOutcome.Committed;

        void Warn() => warning?.Invoke($"{intent.Kind} {intent.ItemName} × {intent.Quantity} at {record.Assignments.FirstOrDefault(a => a.RetainerId == intent.RetainerId)?.RetainerName ?? intent.RetainerId.ToString()}: {receipt.Code}: {receipt.Message}");
    }

    private string DescribeChange(IReadOnlyList<RetainerMarketListingTarget> live, RetainerMarketListingTarget original)
    {
        if (live.FirstOrDefault(l => l != original && l with { SlotIndex = original.SlotIndex } == original) is { } moved)
            return $"the listing moved to slot {moved.SlotIndex}";
        var now = live.FirstOrDefault(l => l.SlotIndex == original.SlotIndex);
        // Gone listings free a slot; this visit's posting pass re-reads the live listings, so
        // the freed slot and the assignment's new shortfall are counted there and by the top-up.
        return now is null
            ? $"the listing is gone (sold or removed) and slot {original.SlotIndex} is now empty; the freed slot counts toward this visit's posts"
            : $"the listing is gone (sold or removed); slot {original.SlotIndex} now holds {driver.ItemName(now.ItemId)} × {now.Quantity} at {now.UnitPrice?.ToString() ?? "unknown"} gil; the freed slot counts toward this visit's posts";
    }

    private Task<bool> Step(Func<CancellationToken, Task> action) => deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
    private bool HasSessionAuthority(MarketRunRecord record) => !Volatile.Read(ref disposed) &&
        runLease is not null && automation.Owns(runLease) &&
        currentPlan() is { Owner.HasStableIdentity: true } plan && plan.Owner.Matches(record.Owner);
    private bool CanCleanUp(MarketRunRecord record) => HasSessionAuthority(record) &&
        !driverUnsettled && !MarketRunDeadline.HasUnsettled;
    private void Check(MarketRunRecord record)
    {
        if (Volatile.Read(ref cancelled) != 0 || Volatile.Read(ref disposed)) throw new OperationCanceledException();
        if (driverUnsettled || MarketRunDeadline.HasUnsettled)
            throw new RunStoppedException("A session call has not settled; run stopped to prevent overlapping requests.");
        if (runLease is null || !automation.Owns(runLease)) throw new RunStoppedException("Automation lease lost.");
        var plan = currentPlan();
        if (plan is null) throw new RunStoppedException("No Listing Plan or character still loading.");
        if (!plan.Owner.HasStableIdentity) throw new RunStoppedException("Character identity is unstable.");
        if (plan.Id != record.PlanId || plan.Revision != record.PlanRevision || !plan.Owner.Matches(record.Owner))
            throw new RunStoppedException("Listing Plan changed; run stopped before the next mutation.");
    }
    private void Persist(MarketRunRecord record)
    {
        lock (persistenceGate)
        {
            if (disposed) return;
            try { persist(record); }
            catch (Exception e) { persistenceFailed = true; PersistenceError = $"Market run persistence failed: {e.Message}"; throw; }
        }
    }
    private void Add(MarketRunRecord record, string message) => record.Results.Add(topUpActive ? $"Top-up · {message}" : message);
    private ListingRow[] Rows(ulong id, string name, IReadOnlyList<RetainerMarketListingTarget> listings) => listings.Select(l =>
        new ListingRow(BrowserScope.RetainerKey(id), id, name, l.SlotIndex, l.ItemId, driver.ItemName(l.ItemId), l.Quantity,
            l.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, Evidence.Unknown<decimal>("Not scanned"),
            l.UnitPrice is { } p ? Evidence.Known((decimal)p) : Evidence.Unknown<decimal>("Price unknown"),
            l.UnitPrice is { } total ? Evidence.Known((decimal)total * l.Quantity) : Evidence.Unknown<decimal>("Price unknown"), DateTime.UtcNow)).ToArray();
    private sealed class RetainerStoppedException : Exception;
    private sealed class RunStoppedException(string message) : Exception(message);
}
