using System.Collections.Immutable;
using RQ.Courier;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

public interface ICourierPlacementPort
{
    Task<Result<int>> RetainerUnits(OwnerScope owner,Shipment shipment,bool deposit,Deadline deadline);
    int? JournalUnits(string operationId,OwnerScope owner,Shipment shipment);
    Task<Result<bool>> MoveAsync(StowagePlan plan, Shipment shipment, bool deposit, IDisposable lease,
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
    async Task<Result<int>> ICourierPlacementPort.RetainerUnits(OwnerScope owner,Shipment shipment,bool deposit,Deadline deadline)
    {
        var id=deposit?shipment.To:shipment.From;
        if (id==null)return Result<int>.Ok(0);
        if(!SamePlacementOwner(owner) || !cache.Snapshot().TryGetValue(id.Value,out var ret) || !ret.Owner.Matches(owner))
            return Result<int>.Fail("PlacementRetainerUnknown",shipment.Name);
        using var cts=new CancellationTokenSource(deadline.Expires-DateTimeOffset.UtcNow);
        await driver.RequireRetainerListAsync(cts.Token);
        try {
            await driver.OpenRetainerAsync(new(id.Value,ret.RetainerName,ret.ObservedAtUtc),cts.Token);
            await driver.OpenInventoryAsync(cts.Token);
            var stacks=await driver.ScanRetainerAsync(new HashSet<uint>{shipment.Item},cts.Token);
            return SamePlacementOwner(owner)?Result<int>.Ok(stacks.Where(x=>x.ItemId==shipment.Item && x.IsHighQuality==shipment.Hq).Sum(x=>x.Quantity)):
                Result<int>.Fail("PlacementOwnerChanged",shipment.Name);
        } finally { await driver.CloseRetainerAsync(cts.Token);await driver.CloseRetainerListAsync(cts.Token); }
    }
    int? ICourierPlacementPort.JournalUnits(string id,OwnerScope owner,Shipment s) =>
        journal.Get(id) is {} record && CourierPlanner.Exact(record.Owner,owner) && record.Lines.All(l=>l.SourceRuleId==s.Line && l.ItemId==s.Item) ?
        record.Lines.Sum(l=>l.TransferredQuantity):null;
    async Task<Result<bool>> ICourierPlacementPort.MoveAsync(StowagePlan plan, Shipment s, bool deposit,
        IDisposable lease, Func<bool> authorized, Deadline deadline, Action<string> recordOperation)
    {
        using var cancel = new CancellationTokenSource(deadline.Expires - DateTimeOffset.UtcNow);
        bool Allowed() => authorized() && automation.Owns(lease) && SamePlacementOwner(plan.Owner) && !cancel.IsCancellationRequested;
        if (!Allowed()) return Result<bool>.Fail("PlacementAuthorityLost", s.Name);
        var bags = await driver.ReadPlacementBagsAsync(cancel.Token);
        var held = bags.Stacks.Where(x => x.ItemId == s.Item && x.IsHighQuality == s.Hq).Sum(x => x.Quantity);
        var quality = s.Hq ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
        if ((deposit || s.From == null) && held < s.Units) return Result<bool>.Fail("BagStockShort", s.Name);
        if ((deposit ? s.To : s.From) == null) return Result<bool>.Ok(true);
        if (!Allowed()) return Result<bool>.Fail("PlacementAuthorityLost", s.Name);
        string id;
        TransferExecutionResult result;
        if (!deposit)
        {
            var row = new TargetPlanItem
            {
                Id = s.Line,
                StowagePlanId = plan.Id,
                ItemId = s.Item,
                ItemName = s.Name,
                TargetQuantity = checked(held + s.Units),
                Quality = quality
            };
            var leg = new PlacementLeg(Allowed, new Dictionary<(uint, ItemQualityPolicy), int> { [(s.Item, quality)] = held }, s.From, KeepListOpen: false);
            id = journal.CreateTransferRetrieval(plan.Owner, plan, [row]).OperationId;
            recordOperation(id);
            result = await ExecuteRetrievalUnderLeaseAsync(id, lease, cancel.Token, leg);
        }
        else
        {
            var request = new StowageDepositRequest(plan.Id, s.Line, s.Item, s.Name, s.Hq, s.Units,
                new() { PreferredRetainerIds = [s.To!.Value] }, s.To, s.StackSize);
            var batch = StowageRouter.BuildBatch([request], cache.Snapshot(), plan.Owner, _ => s.StackSize, utcNow());
            if (batch.PlannedQuantity != s.Units) return Result<bool>.Fail("DestinationFull", s.Name);
            var leg = new PlacementLeg(Allowed, DepositFloors: new Dictionary<(uint, bool), (ItemQualityPolicy, int)>
            {
                [(s.Item, s.Hq)] = (quality, held - s.Units)
            }, KeepListOpen: false);
            id = journal.CreateTransferDeposit(plan.Owner, plan, batch).OperationId;
            recordOperation(id);
            result = await ExecuteDepositUnderLeaseAsync(id, lease, cancel.Token, leg);
        }
        var record = journal.Get(id);
        return result.Started && record?.Status == OperationStatuses.Succeeded &&
            record.Lines.Sum(l => l.TransferredQuantity) == s.Units ? Result<bool>.Ok(true) :
            new(CourierOutcome.Indeterminate, "PlacementNotVerified", $"{s.Name}: {id}: {record?.Message ?? result.Message}");
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
