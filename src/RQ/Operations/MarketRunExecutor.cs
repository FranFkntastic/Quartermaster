using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Operations;

public enum MarketMutationOutcome { FailedBeforeSend, Committed, Indeterminate, Unchanged }
public sealed record MarketPlayerInventory(IReadOnlyList<DalamudInventoryStack> Stacks, int FreeSlots);
public sealed record MarketGatherTarget(uint ItemId, string ItemName, ItemQualityPolicy Quality, int Units);
public sealed record MarketMutationIntent(string Kind, ulong RetainerId, string ItemName,
    RetainerMarketListingTarget? Listing, DalamudInventoryStack? Source, int Quantity, uint Price);

/// <summary>All reads are live. Implementations must not retry mutations.</summary>
public interface IMarketRunDriver
{
    string ItemName(uint itemId);
    Task RequireRetainerListAsync(CancellationToken token);
    Task OpenAsync(ulong retainerId, string name, CancellationToken token);
    Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken token);
    Task<MarketPlayerInventory> PlayerAsync(CancellationToken token);
    Task<MarketMutationOutcome> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken token);
    Task<MarketMutationOutcome> PullAsync(RetainerMarketListingTarget listing, CancellationToken token);
    Task<MarketMutationOutcome> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken token);
    Task ReturnAsync(CancellationToken token);
    Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, CancellationToken token);
}

public interface IMarketRunDeadline
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action);
}

public sealed class MarketRunDeadline(TimeSpan timeout) : IMarketRunDeadline
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // Give the primitive time to classify a cancellation after dispatch. A driver
        // ignoring cancellation cannot leave this executor waiting indefinitely.
        return await action(deadline.Token).WaitAsync(timeout + TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
}

/// <summary>Owns one immutable plan snapshot and one lease across gather and listing passes.</summary>
public sealed class MarketRunExecutor(
    IMarketRunDriver driver, AutomationLease automation, Func<ListingPlan?> currentPlan,
    Action<MarketRunRecord> persist, AutoRetainerSuppression? suppression = null,
    IMarketRunDeadline? deadlines = null)
{
    private readonly IMarketRunDeadline deadline = deadlines ?? new MarketRunDeadline(TimeSpan.FromSeconds(15));
    private int running;
    private int cancelled;
    private bool driverUnsettled;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public bool CanStart => !IsRunning && !automation.IsHeld;
    public string Status { get; private set; } = string.Empty;
    public MarketRunRecord? Result { get; private set; }
    public void Restore(MarketRunRecord? record) => Result = record;
    public void Cancel() => Interlocked.Exchange(ref cancelled, 1);

    public async Task RunAsync(ListingPlan plan, MarketRunEvaluation evaluation, bool draftDirty = false)
    {
        if (draftDirty || !evaluation.IsComplete || evaluation.PlanId != plan.Id || evaluation.PlanRevision != plan.Revision ||
            !evaluation.Retainers.Any(r => r.Assignments.Any(a => a.ShortListings.Value > 0 || a.RepriceListings.Value > 0))) return;
        if (!automation.TryAcquire("market run", out var lease)) return;
        using (lease)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            Interlocked.Exchange(ref cancelled, 0);
            driverUnsettled = false;
            var record = new MarketRunRecord { PlanId = plan.Id, PlanRevision = plan.Revision, Owner = plan.Owner with { },
                Assignments = plan.Assignments.Where(a => a.Enabled).Select(ListingPlanCatalog.Copy).ToList() };
            AutoRetainerSuppression.Scope? scope = null;
            try
            {
                persist(record);
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
                var targets = evaluation.Items.Where(i => i.RefillUnits.Value > 0)
                    .Select(i => new MarketGatherTarget(i.ItemId, i.ItemName, i.Quality, i.RefillUnits.Value)).ToArray();
                // Gather owns per-call deadlines, not a single deadline for a multi-retainer route.
                record.Results.AddRange(await driver.GatherAsync(targets, lease!, () => Check(record), CancellationToken.None).ConfigureAwait(false));
                Check(record);
                await Step(driver.RequireRetainerListAsync).ConfigureAwait(false);
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
                    finally
                    {
                        // A best-effort final scan is also required for an indeterminate mutation.
                        try
                        {
                            if (opened && !driverUnsettled)
                            {
                                var final = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                                var matches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, retainer.Key.RetainerName, final));
                                foreach (var a in assignments)
                                {
                                    var present = matches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
                                    if (present < a.ListingCount) Add(record, $"{retainer.Key.RetainerName} / {a.ItemName}: final short {a.ListingCount - present}");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            if (!record.NeedsCheck.Contains(id)) record.NeedsCheck.Add(id);
                            Add(record, $"{retainer.Key.RetainerName}: final scan unavailable ({e.Message})");
                        }
                    }
                    await Step(driver.ReturnAsync).ConfigureAwait(false);
                }
                record.State = "Completed";
            }
            catch (OperationCanceledException) when (Volatile.Read(ref cancelled) != 0)
            { record.State = "Cancelled"; record.Reason = "Cancelled between steps."; }
            catch (Exception e) { record.State = "Stopped"; record.Reason = e.Message; }
            finally
            {
                scope?.Dispose();
                if (scope?.RestoreFailure is { } failure) Add(record, $"AutoRetainer restore: {failure}");
                Status = record.State + (record.Reason.Length == 0 ? string.Empty : $": {record.Reason}");
                Result = record;
                try { persist(record); }
                finally { Volatile.Write(ref running, 0); }
            }
        }
    }

    private async Task ListRetainer(MarketRunRecord record, ulong id, string name, ListingPlanAssignment[] assignments)
    {
        var listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        var rows = Rows(id, name, listings);
        foreach (var match in ListingPlanEvaluator.MatchListings(assignments, rows))
        {
            Check(record);
            if (match.Kind == ListingMatchKind.UnknownPrice) { Add(record, $"{match.Listing.ItemName}: unknown price; left alone"); continue; }
            if (match.Kind != ListingMatchKind.PriceDiffers) continue;
            var assignment = assignments.Single(a => a.Id == match.AssignmentId);
            var listing = listings.Single(l => l.SlotIndex == match.Listing.SlotIndex);
            if (!await StillPresent(listing).ConfigureAwait(false)) { Add(record, $"{assignment.ItemName}: listing changed; not repriced"); continue; }
            await Mutate(record, new("Reprice", id, assignment.ItemName, listing, null, listing.Quantity, (uint)assignment.UnitPrice),
                t => driver.RepriceAsync(listing, (uint)assignment.UnitPrice, t)).ConfigureAwait(false);
        }

        listings = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
        var matches = ListingPlanEvaluator.MatchListings(assignments, Rows(id, name, listings));
        var player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
        var allocations = new List<(ListingPlanAssignment Assignment, int Count)>();
        var remaining = player.Stacks.GroupBy(s => (s.ItemId, s.IsHighQuality)).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        foreach (var a in assignments)
        {
            var held = matches.Count(m => m.AssignmentId == a.Id && m.Kind is ListingMatchKind.Exact or ListingMatchKind.PriceDiffers or ListingMatchKind.UnknownPrice);
            var missing = Math.Max(0, a.ListingCount - held);
            var key = (a.ItemId, a.Quality == ItemQualityPolicy.HqOnly);
            var backed = Math.Min(missing, remaining.GetValueOrDefault(key) / a.QuantityPerListing);
            remaining[key] = remaining.GetValueOrDefault(key) - backed * a.QuantityPerListing;
            allocations.Add((a, backed));
            if (missing > backed) Add(record, $"{a.ItemName}: still short {missing - backed} (stock or gather inventory space)");
        }
        var count = allocations.Sum(a => a.Count);
        var pulls = MarketRunPlanner.SelectListingsToPull(Rows(id, name, listings), assignments, count);
        foreach (var pull in pulls)
        {
            Check(record);
            player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
            if (player.FreeSlots == 0) { Add(record, $"{name}: blocked by inventory space {Math.Max(0, count - (20 - listings.Count))}"); break; }
            var target = listings.Single(l => l.SlotIndex == pull.Listing.SlotIndex);
            if (!await StillPresent(target).ConfigureAwait(false)) { Add(record, $"{pull.Listing.ItemName}: pull source changed"); break; }
            // Re-select with the same planner policy immediately before dispatch.
            var live = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
            if (!live.Contains(target) || !MarketRunPlanner.SelectListingsToPull(Rows(id, name, live), assignments, count)
                .Any(p => p.Listing.SlotIndex == target.SlotIndex)) break;
            await Mutate(record, new("Pull", id, pull.Listing.ItemName, target, null, target.Quantity, 0),
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
                if (listings.Count >= MarketRunPlanner.ListingCapacity) { Add(record, $"{a.ItemName}: blocked by slots {refill - n}"); break; }
                player = await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
                var stock = player.Stacks.Where(s => s.ItemId == a.ItemId && s.IsHighQuality == (a.Quality == ItemQualityPolicy.HqOnly)).ToArray();
                var source = stock.FirstOrDefault(s => s.Quantity >= a.QuantityPerListing);
                if (source is null)
                {
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
        persist(record); // A persistence failure must prevent dispatch.
        try { receipt.Outcome = await deadline.RunAsync(action).ConfigureAwait(false); }
        catch (RetainerMarketMutationIndeterminateException) { receipt.Outcome = MarketMutationOutcome.Indeterminate; }
        catch (OperationCanceledException) { receipt.Outcome = intent.Kind == "Reprice" ? MarketMutationOutcome.Indeterminate : MarketMutationOutcome.FailedBeforeSend; }
        catch (TimeoutException)
        {
            driverUnsettled = true;
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            record.NeedsCheck.Add(intent.RetainerId);
            persist(record);
            throw new InvalidOperationException("Mutation deadline expired without a settled driver; stopped to prevent overlapping requests.");
        }
        catch
        {
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            record.NeedsCheck.Add(intent.RetainerId);
            persist(record);
            throw;
        }
        // A failed reprice has no three-way primitive outcome; verify the exact
        // expected after-state before classifying it, without ever sending again.
        persist(record);
        if (intent.Kind == "Reprice" && receipt.Outcome != MarketMutationOutcome.Committed)
        {
            try
            {
                var live = await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false);
                receipt.Outcome = live.Contains(intent.Listing! with { UnitPrice = intent.Price }) ? MarketMutationOutcome.Committed :
                    live.Contains(intent.Listing!) ? MarketMutationOutcome.Unchanged : MarketMutationOutcome.Indeterminate;
            }
            catch
            {
                receipt.Outcome = MarketMutationOutcome.Indeterminate;
                record.NeedsCheck.Add(intent.RetainerId);
                persist(record);
                throw;
            }
        }
        if (receipt.Outcome == MarketMutationOutcome.Indeterminate) record.NeedsCheck.Add(intent.RetainerId);
        persist(record);
        await deadline.RunAsync(driver.PlayerAsync).ConfigureAwait(false);
        Add(record, $"{intent.Kind} {intent.ItemName} × {intent.Quantity}: {receipt.Outcome}{(intent.Kind == "Pull" && receipt.Outcome == MarketMutationOutcome.Committed ? " (player inventory)" : string.Empty)}");
        if (receipt.Outcome != MarketMutationOutcome.Committed) throw new RetainerStoppedException();
    }

    private async Task<bool> StillPresent(RetainerMarketListingTarget target) =>
        (await deadline.RunAsync(driver.ListingsAsync).ConfigureAwait(false)).Contains(target);
    private Task<bool> Step(Func<CancellationToken, Task> action) => deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
    private void Check(MarketRunRecord record)
    {
        if (Volatile.Read(ref cancelled) != 0) throw new OperationCanceledException();
        var plan = currentPlan();
        if (plan is null || plan.Id != record.PlanId || plan.Revision != record.PlanRevision || !plan.Owner.Matches(record.Owner))
            throw new InvalidOperationException("Listing Plan changed; run stopped before the next mutation.");
    }
    private static void Add(MarketRunRecord record, string message) => record.Results.Add(message);
    private ListingRow[] Rows(ulong id, string name, IReadOnlyList<RetainerMarketListingTarget> listings) => listings.Select(l =>
        new ListingRow(BrowserScope.RetainerKey(id), id, name, l.SlotIndex, l.ItemId, driver.ItemName(l.ItemId), l.Quantity,
            l.IsHq ? FfxivItemQuality.HQ : FfxivItemQuality.NQ, Evidence.Unknown<decimal>("Not scanned"),
            l.UnitPrice is { } p ? Evidence.Known((decimal)p) : Evidence.Unknown<decimal>("Price unknown"),
            l.UnitPrice is { } total ? Evidence.Known((decimal)total * l.Quantity) : Evidence.Unknown<decimal>("Price unknown"), DateTime.UtcNow)).ToArray();
    private sealed class RetainerStoppedException : Exception;
}
