using RQ.Courier;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

public interface ICourierPlacementPort
{
    Task<Result<bool>> MoveAsync(StowagePlan plan, Shipment shipment, bool deposit, IDisposable lease,
        Func<bool> authorized, Deadline deadline);
}

public sealed partial class TransferCoordinator : ICourierPlacementPort
{
    async Task<Result<bool>> ICourierPlacementPort.MoveAsync(StowagePlan plan, Shipment s, bool deposit,
        IDisposable lease, Func<bool> authorized, Deadline deadline)
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
            result = await ExecuteDepositUnderLeaseAsync(id, lease, cancel.Token, leg);
        }
        var record = journal.Get(id);
        return result.Started && record?.Status == OperationStatuses.Succeeded &&
            record.Lines.Sum(l => l.TransferredQuantity) == s.Units ? Result<bool>.Ok(true) :
            new(CourierOutcome.Indeterminate, "PlacementNotVerified", $"{s.Name}: {id}: {record?.Message ?? result.Message}");
    }
}
