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
public sealed record MarketPlayerInventory(IReadOnlyList<DalamudInventoryStack> Stacks, int FreeSlots);
public sealed record MarketGatherTarget(uint ItemId, string ItemName, ItemQualityPolicy Quality, int Units);
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
    Task<MarketMutationOutcome> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken token);
    Task<MarketMutationOutcome> PullAsync(RetainerMarketListingTarget listing, CancellationToken token);
    Task<MarketMutationOutcome> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken token);
    Task ReturnAsync(CancellationToken token);
    Task CloseListAsync(CancellationToken token);
    Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, CancellationToken token);
}

public interface IMarketRunDeadline
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action);
}

public sealed class MarketRunDeadline(TimeSpan timeout) : IMarketRunDeadline
{
    // Bell routing has its own two-minute budget; leave room for opening the list.
    public static readonly TimeSpan RetainerListTimeout = TimeSpan.FromSeconds(150);
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
    public bool RequiresAcknowledgement => State == "Interrupted" || NeedsCheck.Count > 0 ||
        Mutations.Any(m => m.Outcome is null or MarketMutationOutcome.Indeterminate);
}

public sealed class MarketMutationReceipt
{
    public required MarketMutationIntent Intent { get; set; }
    // Null is deliberately persisted before dispatch: after reload it means unknown,
    // not failed, and it never authorizes replay.
    public MarketMutationOutcome? Outcome { get; set; }
}

public sealed class MarketRunStore(string path)
{
    private readonly AtomicDocumentStore<MarketRunRecord> store = new(path);
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
        return record;
    }
    public void Save(MarketRunRecord record) => store.Save(record);
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

/// <summary>Owns one immutable plan snapshot and one lease across gather and listing passes.</summary>
public sealed class MarketRunExecutor(
    IMarketRunDriver driver, AutomationLease automation, Func<ListingPlan?> currentPlan,
    Action<MarketRunRecord> persist, AutoRetainerSuppression? suppression = null,
    IMarketRunDeadline? deadlines = null, Action? clear = null, IMarketRunDeadline? routeDeadlines = null)
{
    private readonly IMarketRunDeadline deadline = deadlines ?? new MarketRunDeadline(TimeSpan.FromSeconds(15));
    private readonly IMarketRunDeadline routeDeadline = routeDeadlines ?? new MarketRunDeadline(MarketRunDeadline.RetainerListTimeout);
    private int running;
    private int cancelled;
    private bool driverUnsettled;
    private bool persistenceFailed;
    private IDisposable? runLease;
    private readonly object persistenceGate = new();
    public bool IsRunning => Volatile.Read(ref running) != 0;
    private bool disposed;
    public Task? ActiveTask { get; private set; }
    public bool CanStart => !disposed && !IsRunning && !automation.IsHeld && Result?.RequiresAcknowledgement != true;
    public void Clear()
    {
        lock (persistenceGate)
        {
            if (disposed || IsRunning || automation.IsHeld) return;
            try { clear?.Invoke(); Result = null; }
            catch (Exception e) { if (Result is { } result) result.Reason = $"Could not clear result: {e.Message}"; }
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
            var record = new MarketRunRecord { PlanId = plan.Id, PlanRevision = plan.Revision, Owner = plan.Owner with { },
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
                record.Results.AddRange(await driver.GatherAsync(targets, lease!, () => Check(record), CancellationToken.None).ConfigureAwait(false));
                record.GatherBlockedUnits += driver.GatherInventoryBlockedUnits;
                Check(record);
                await routeDeadline.RunAsync(async t => { await driver.RequireRetainerListAsync(t).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                foreach (var retainer in record.Assignments.GroupBy(a => (a.RetainerId, a.RetainerName))
                    .OrderBy(g => g.Key.RetainerName, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.RetainerId))
                {
                    Check(record);
                    Status = retainer.Key.RetainerName;
                    var id = retainer.Key.RetainerId;
                    var assignments = retainer.ToArray();
                    var opened = false;
                    try
                    {
                        await Step(t => driver.OpenAsync(id, retainer.Key.RetainerName, t)).ConfigureAwait(false);
                        opened = true;
                        await ListRetainer(record, id, retainer.Key.RetainerName, assignments).ConfigureAwait(false);
                    }
                    catch (RetainerStoppedException) { }
                    catch (OperationCanceledException) { throw; }
                    catch (RunStoppedException) { throw; }
                    catch (Exception e) when (!persistenceFailed && !driverUnsettled && !MarketRunDeadline.HasUnsettled)
                    { Add(record, $"{retainer.Key.RetainerName}: {e.Message}"); }
                    finally
                    {
                        // A best-effort final scan is also required for an indeterminate mutation.
                        try
                        {
                            if (opened && !driverUnsettled && !MarketRunDeadline.HasUnsettled)
                            {
                                var final = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                                var matches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, retainer.Key.RetainerName, final));
                                foreach (var a in assignments)
                                {
                                    var present = matches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
                                    record.StillShort += Math.Max(0, a.ListingCount - present);
                                }
                                scannedRetainers.Add(id);
                            }
                        }
                        catch (Exception e)
                        {
                            Add(record, $"{retainer.Key.RetainerName}: final scan unavailable ({e.Message})");
                        }
                        if (!driverUnsettled && !MarketRunDeadline.HasUnsettled)
                            await Step(driver.ReturnAsync).ConfigureAwait(false);
                    }
                    Check(record);
                }
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
                    if (sessionStarted && !disposed && !driverUnsettled && !MarketRunDeadline.HasUnsettled &&
                        currentPlan()?.Owner.Matches(record.Owner) == true)
                    {
                        try { await Step(driver.CloseListAsync).ConfigureAwait(false); }
                        catch (Exception e) { Add(record, $"Could not close retainer list: {e.Message}"); }
                    }
                    record.ListingSummaryComplete = record.Assignments.Select(a => a.RetainerId).Distinct().All(scannedRetainers.Contains);
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

    private async Task ListRetainer(MarketRunRecord record, ulong id, string name, ListingPlanAssignment[] assignments)
    {
        var listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        var repriced = new HashSet<int>();
        while (true)
        {
            Check(record);
            listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
            var match = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings))
                .FirstOrDefault(m => m.Kind == ListingMatchKind.PriceDiffers && m.Listing.SlotIndex is { } slot && !repriced.Contains(slot));
            if (match is null) break;
            var assignment = assignments.Single(a => a.Id == match.AssignmentId);
            var listing = listings.FirstOrDefault(l => l.SlotIndex == match.Listing.SlotIndex);
            if (listing is null) { Add(record, $"{assignment.ItemName}: no longer present"); break; }
            repriced.Add(listing.SlotIndex);
            await Mutate(record, new("Reprice", id, assignment.ItemName, listing, null, listing.Quantity, (uint)assignment.UnitPrice),
                t => driver.RepriceAsync(listing, (uint)assignment.UnitPrice, t)).ConfigureAwait(false);
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

    private async Task Mutate(MarketRunRecord record, MarketMutationIntent intent, Func<CancellationToken, Task<MarketMutationOutcome>> action)
    {
        Check(record);
        var receipt = new MarketMutationReceipt { Intent = intent };
        record.Mutations.Add(receipt);
        try { Persist(record); Check(record); } // No dispatch without durable intent and current authority.
        catch (Exception e)
        {
            receipt.Outcome = MarketMutationOutcome.FailedBeforeSend;
            if (e is OperationCanceledException) throw;
            throw new RunStoppedException(e.Message);
        }
        try { receipt.Outcome = await deadline.RunAsync(action).ConfigureAwait(false); }
        catch (RetainerMarketMutationIndeterminateException) { receipt.Outcome = MarketMutationOutcome.Indeterminate; }
        catch (OperationCanceledException) { receipt.Outcome = MarketMutationOutcome.FailedBeforeSend; }
        catch (TimeoutException)
        {
            driverUnsettled = true;
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            record.NeedsCheck.Add(intent.RetainerId);
            Persist(record);
            throw new InvalidOperationException("Mutation deadline expired without a settled driver; stopped to prevent overlapping requests.");
        }
        catch
        {
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
        }
        // A failed reprice has no three-way primitive outcome; verify the exact
        // expected after-state before classifying it, without ever sending again.
        if (intent.Kind != "Reprice" && receipt.Outcome == MarketMutationOutcome.Indeterminate && !record.NeedsCheck.Contains(intent.RetainerId))
            record.NeedsCheck.Add(intent.RetainerId);
        Persist(record);
        if (intent.Kind == "Reprice" && receipt.Outcome != MarketMutationOutcome.Committed)
        {
            try
            {
                var live = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                receipt.Outcome = live.Contains(intent.Listing! with { UnitPrice = intent.Price }) ? MarketMutationOutcome.Committed :
                    receipt.Outcome == MarketMutationOutcome.FailedBeforeSend && live.Contains(intent.Listing!) ? MarketMutationOutcome.Unchanged : MarketMutationOutcome.Indeterminate;
            }
            catch
            {
                receipt.Outcome = MarketMutationOutcome.Indeterminate;
                record.NeedsCheck.Add(intent.RetainerId);
                Persist(record);
                throw;
            }
        }
        if (receipt.Outcome == MarketMutationOutcome.Indeterminate && !record.NeedsCheck.Contains(intent.RetainerId)) record.NeedsCheck.Add(intent.RetainerId);
        Persist(record);
        Add(record, $"{intent.Kind} {intent.ItemName} × {intent.Quantity}: {receipt.Outcome}{(intent.Kind == "Pull" && receipt.Outcome == MarketMutationOutcome.Committed ? " (player inventory)" : string.Empty)}");
        if (receipt.Outcome == MarketMutationOutcome.Indeterminate && intent.Kind != "Reprice")
            throw new RunStoppedException("Needs a check: inventory mutation outcome unknown.");
        if (receipt.Outcome != MarketMutationOutcome.Committed) throw new RetainerStoppedException();
    }

    private Task<bool> Step(Func<CancellationToken, Task> action) => deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
    private void Check(MarketRunRecord record)
    {
        if (Volatile.Read(ref cancelled) != 0 || Volatile.Read(ref disposed)) throw new OperationCanceledException();
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
            catch { persistenceFailed = true; throw; }
        }
    }
    private static void Add(MarketRunRecord record, string message) => record.Results.Add(message);
    private ListingRow[] Rows(ulong id, string name, IReadOnlyList<RetainerMarketListingTarget> listings) => listings.Select(l =>
        new ListingRow(BrowserScope.RetainerKey(id), id, name, l.SlotIndex, l.ItemId, driver.ItemName(l.ItemId), l.Quantity,
            l.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, Evidence.Unknown<decimal>("Not scanned"),
            l.UnitPrice is { } p ? Evidence.Known((decimal)p) : Evidence.Unknown<decimal>("Price unknown"),
            l.UnitPrice is { } total ? Evidence.Known((decimal)total * l.Quantity) : Evidence.Unknown<decimal>("Price unknown"), DateTime.UtcNow)).ToArray();
    private sealed class RetainerStoppedException : Exception;
    private sealed class RunStoppedException(string message) : Exception(message);
}
