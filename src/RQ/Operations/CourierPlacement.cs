using System.Collections.Immutable;
using RQ.Courier;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

/// <summary>Verified units one journal line moved: the line (null when the journal merged lines), item and quality.</summary>
public sealed record PlacementTally(Guid? Line, uint Item, bool Hq, int Units);

/// <summary>
/// The one grouping both the placement port and the courier's resume settlement use, so a
/// journaled operation can be attributed back to the shipments it served.
/// </summary>
public static class CourierPlacementBatch
{
    /// <summary>Retainer-sourced shipments per source retainer, in first-appearance order: one retrieval operation each.</summary>
    public static ImmutableArray<(ulong Retainer, ImmutableArray<Shipment> Shipments)> Retrievals(ImmutableArray<Shipment> batch) =>
        [.. batch.Where(s => s.From != null).GroupBy(s => s.From!.Value).Select(g => (g.Key, g.ToImmutableArray()))];
    /// <summary>
    /// Retainer-bound shipments per (line, item, quality, destination), in first-appearance order: one
    /// deposit route each, so the single deposit operation's journal lines follow this order.
    /// </summary>
    public static ImmutableArray<(Guid Line, uint Item, bool Hq, ulong To, ImmutableArray<Shipment> Shipments)> Deposits(ImmutableArray<Shipment> batch) =>
        [.. batch.Where(s => s.To != null).GroupBy(s => (s.Line, s.Item, s.Hq, To: s.To!.Value))
            .Select(g => (g.Key.Line, g.Key.Item, g.Key.Hq, g.Key.To, g.ToImmutableArray()))];
}

public interface ICourierPlacementPort
{
    /// <summary>Verified transfers per journal line, in line order; null if the operation is unknown or another owner's.</summary>
    ImmutableArray<PlacementTally>? JournalUnits(string operationId, OwnerScope owner);
    string? OperationStatus(string operationId, OwnerScope owner);
    /// <summary>
    /// Moves a whole trip batch inside one summoning-bell session. Retrieval runs one journaled
    /// operation per source retainer (<see cref="CourierPlacementBatch.Retrievals"/>) and keeps the
    /// retainer list open between them; deposit runs a single journaled operation over every
    /// destination retainer. Each operation id is reported as soon as it is journaled.
    /// Shipments with no retainer on that side only have their bag stock verified.
    /// </summary>
    Task<Result<bool>> MoveBatchAsync(StowagePlan plan, ImmutableArray<Shipment> batch, bool deposit, IDisposable lease,
        Func<bool> authorized, Deadline deadline, Action<string> recordOperation);
    /// <summary>
    /// Gathers each line's stray stacks on the logged-in plan owner's retainers into the line's
    /// retainer, as Transfer Placement's Consolidate does. Succeeded: every move landed. Failed: it
    /// stopped short with every leg settled (nothing unverified in transit). Indeterminate: a leg's
    /// outcome is unverified. The placement operation id is reported as soon as it is journaled.
    /// </summary>
    Task<Result<bool>> ConsolidateAsync(StowagePlan plan, ImmutableArray<CourierConsolidation> lines, IDisposable lease,
        Func<bool> authorized, Deadline deadline, Action<string> recordOperation);
}

public sealed partial class TransferCoordinator : ICourierPlacementPort
{
    ImmutableArray<PlacementTally>? ICourierPlacementPort.JournalUnits(string id, OwnerScope owner) =>
        journal.Get(id) is { } record && CourierPlanner.Exact(record.Owner, owner)
            ? record.Lines.Select(l => new PlacementTally(l.SourceRuleId, l.ItemId, l.Quality == ItemQualityPolicy.HqOnly, l.TransferredQuantity)).ToImmutableArray()
            : null;
    string? ICourierPlacementPort.OperationStatus(string id, OwnerScope owner) =>
        journal.Get(id) is { } record && CourierPlanner.Exact(record.Owner, owner) ? record.Status : null;
    async Task<Result<bool>> ICourierPlacementPort.MoveBatchAsync(StowagePlan plan, ImmutableArray<Shipment> batch, bool deposit,
        IDisposable lease, Func<bool> authorized, Deadline deadline, Action<string> recordOperation)
    {
        if (batch.IsDefaultOrEmpty) return Result<bool>.Ok(true);
        using var cancel = new CancellationTokenSource(deadline.Expires - DateTimeOffset.UtcNow);
        bool Allowed() => authorized() && automation.Owns(lease) && SamePlacementOwner(plan.Owner) && !cancel.IsCancellationRequested;
        if (!Allowed()) return Result<bool>.Fail("PlacementAuthorityLost", string.Join(", ", batch.Select(s => s.Name).Distinct()));
        var bags = await driver.ReadPlacementBagsAsync(cancel.Token);
        int Held(uint item, bool hq) => bags.Stacks.Where(x => x.ItemId == item && x.IsHighQuality == hq).Sum(x => x.Quantity);
        // Deposits leave from the bags; bag-sourced retrieval lines never touch a retainer but must already be held.
        foreach (var need in batch.Where(s => deposit || s.From == null).GroupBy(s => (s.Item, s.Hq)))
            if (Held(need.Key.Item, need.Key.Hq) < need.Sum(s => s.Units))
                return Result<bool>.Fail("BagStockShort", need.First().Name);
        return deposit
            ? await DepositBatchAsync(plan, batch, lease, Allowed, Held, cancel, recordOperation)
            : await RetrieveBatchAsync(plan, batch, lease, Allowed, cancel, recordOperation);
    }
    private async Task<Result<bool>> RetrieveBatchAsync(StowagePlan plan, ImmutableArray<Shipment> batch, IDisposable lease,
        Func<bool> allowed, CancellationTokenSource cancel, Action<string> recordOperation)
    {
        var sources = CourierPlacementBatch.Retrievals(batch);
        if (sources.IsEmpty) return Result<bool>.Ok(true);
        var listUsed = false;
        try
        {
            foreach (var (retainer, shipments) in sources)
            {
                if (!allowed()) return Result<bool>.Fail("PlacementAuthorityLost", shipments[0].Name);
                // Earlier sources may have retrieved the same item: each leg's baseline is a fresh bag read.
                var bags = await driver.ReadPlacementBagsAsync(cancel.Token);
                var wanted = shipments.GroupBy(s => (s.Item, s.Hq)).Select(g =>
                {
                    var held = bags.Stacks.Where(x => x.ItemId == g.Key.Item && x.IsHighQuality == g.Key.Hq).Sum(x => x.Quantity);
                    var quality = g.Key.Hq ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
                    return (Quality: quality, Held: held, Row: new TargetPlanItem
                    {
                        Id = g.First().Line,
                        StowagePlanId = plan.Id,
                        ItemId = g.Key.Item,
                        ItemName = g.First().Name,
                        TargetQuantity = checked(held + g.Sum(s => s.Units)),
                        Quality = quality,
                    });
                }).ToArray();
                // KeepListOpen: the next source retainer reuses this bell session; the finally below closes it once.
                var leg = new PlacementLeg(allowed, wanted.ToDictionary(w => (w.Row.ItemId, w.Quality), w => w.Held), retainer, KeepListOpen: true);
                var id = journal.CreateTransferRetrieval(plan.Owner, plan, [.. wanted.Select(w => w.Row)]).OperationId;
                recordOperation(id);
                listUsed = true;
                var result = await ExecuteRetrievalUnderLeaseAsync(id, lease, cancel.Token, leg);
                var record = journal.Get(id);
                if (!result.Started || record?.Status != OperationStatuses.Succeeded ||
                    record.Lines.Sum(l => l.TransferredQuantity) != shipments.Sum(s => s.Units))
                    return new(CourierOutcome.Indeterminate, "PlacementNotVerified", $"{shipments[0].Name}: {id}: {record?.Message ?? result.Message}");
            }
            return Result<bool>.Ok(true);
        }
        finally
        {
            if (listUsed)
            {
                try { await driver.CloseRetainerListAsync(CancellationToken.None); }
                catch (Exception) { /* A failed leg already released the session; nothing is in transit here. */ }
            }
        }
    }
    private async Task<Result<bool>> DepositBatchAsync(StowagePlan plan, ImmutableArray<Shipment> batch, IDisposable lease,
        Func<bool> allowed, Func<uint, bool, int> held, CancellationTokenSource cancel, Action<string> recordOperation)
    {
        var routes = CourierPlacementBatch.Deposits(batch);
        if (routes.IsEmpty) return Result<bool>.Ok(true);
        if (!allowed()) return Result<bool>.Fail("PlacementAuthorityLost", routes[0].Shipments[0].Name);
        var sizes = routes.GroupBy(r => r.Item).ToDictionary(g => g.Key, g => Math.Max(1, g.Max(r => r.Shipments.Max(s => s.StackSize))));
        var requests = routes.Select(r => new StowageDepositRequest(plan.Id, r.Line, r.Item, r.Shipments[0].Name, r.Hq,
            r.Shipments.Sum(s => s.Units), new() { PreferredRetainerIds = [r.To] }, r.To, sizes[r.Item])).ToArray();
        var built = StowageRouter.BuildBatch(requests, cache.Snapshot(), plan.Owner, item => sizes.GetValueOrDefault(item, 1), utcNow());
        // Every route must fit whole: the journal's lines then follow CourierPlacementBatch.Deposits order exactly.
        var unrouted = built.Routes.Where(r => r.RoutedQuantity != r.Request.Quantity).Select(r => r.Request.ItemName).ToArray();
        if (built.Routes.Count != requests.Length || unrouted.Length > 0)
            return Result<bool>.Fail("DestinationFull", string.Join(", ", unrouted));
        // One floor per (item, quality): what stays in the bags once every route in this batch is stowed.
        var floors = routes.GroupBy(r => (r.Item, r.Hq)).ToDictionary(g => g.Key,
            g => (g.Key.Hq ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly, held(g.Key.Item, g.Key.Hq) - g.Sum(r => r.Shipments.Sum(s => s.Units))));
        // The deposit core opens each destination retainer once and stows all of its stacks before moving on.
        var leg = new PlacementLeg(allowed, DepositFloors: floors, KeepListOpen: false);
        var id = journal.CreateTransferDeposit(plan.Owner, plan, built).OperationId;
        recordOperation(id);
        var result = await ExecuteDepositUnderLeaseAsync(id, lease, cancel.Token, leg);
        var record = journal.Get(id);
        return result.Started && record?.Status == OperationStatuses.Succeeded &&
            record.Lines.Sum(l => l.TransferredQuantity) == requests.Sum(r => r.Quantity) ? Result<bool>.Ok(true) :
            new(CourierOutcome.Indeterminate, "PlacementNotVerified", $"{routes[0].Shipments[0].Name}: {id}: {record?.Message ?? result.Message}");
    }
    async Task<Result<bool>> ICourierPlacementPort.ConsolidateAsync(StowagePlan plan, ImmutableArray<CourierConsolidation> lines,
        IDisposable lease, Func<bool> authorized, Deadline deadline, Action<string> recordOperation)
    {
        using var cancel = new CancellationTokenSource(deadline.Expires - DateTimeOffset.UtcNow);
        bool Allowed() => authorized() && automation.Owns(lease) && SamePlacementOwner(plan.Owner) && !cancel.IsCancellationRequested;
        if (!Allowed()) return Result<bool>.Fail("PlacementAuthorityLost", "Destination consolidation");
        if (lines.Any(line => !CourierPlanner.Exact(line.Destination, plan.Owner)))
            return Result<bool>.Fail("ConsolidationOwnerMismatch", "Consolidation lines belong to another character.");
        var rules = lines.Select(line => new TargetPlanItem
        {
            Id = line.Line,
            StowagePlanId = plan.Id,
            ItemId = line.Item,
            ItemName = line.Name,
            Quality = line.Quality,
            Routing = new() { PreferredRetainerIds = [line.Retainer], Consolidate = true },
        }).ToArray();
        var sizes = lines.GroupBy(line => line.Item).ToDictionary(group => group.Key, group => Math.Max(1, group.First().StackSize));
        string? id = null;
        var result = await RunPlacementAsync(plan.Owner, plan, rules, item => sizes.GetValueOrDefault(item, 1), lease, Allowed, cancel.Token,
            movesOnly: true, started: operation => { id = operation; recordOperation(operation); });
        var record = id is null ? null : journal.Get(id);
        var message = record?.Message ?? result.Message;
        return record?.Status switch
        {
            OperationStatuses.Succeeded => Result<bool>.Ok(true),
            OperationStatuses.Indeterminate or null => new(CourierOutcome.Indeterminate, "ConsolidationUnverified", $"{id}: {message}"),
            _ => Result<bool>.Fail("ConsolidationLimited", $"{id}: {message}"),
        };
    }
}
