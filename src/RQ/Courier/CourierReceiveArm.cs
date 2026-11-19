using Franthropy.Dalamud.Automation.Trades;
using RQ.Domain;

namespace RQ.Courier;

// Items is the remaining manifest; Original records quantities before any credited window.
// Arm and Items are replaced in the same atomic document, never in separate receipts.
internal sealed record CourierReceiveArm(Guid Step, OwnerScope Partner, InventoryView Before);
internal static class CourierReceiveRecovery
{
    internal static Result<bool> Settle(TradeCheckpoint state, Guid step, OwnerScope partner,
        InventoryView fresh, TradeEvidence sender, Action save)
    {
        if (state.Step != step || !state.Receiving) return Result<bool>.Fail("TradeCheckpointMismatch",step.ToString());
        if (state.Arm is not {} arm)
            return state.Items.All(i=>i.Quantity==0) ? Result<bool>.Ok(true) :
                new(CourierOutcome.Indeterminate,"ReceiveNotArmed",step.ToString());
        state.ReceiveAfter = fresh;
        state.SenderEvidence = sender;
        Result<bool> Unknown() { save(); return new(CourierOutcome.Indeterminate,"ReceiveDeltaMismatch",step.ToString()); }
        if (arm.Step != step || !CourierPlanner.Exact(arm.Partner,partner) ||
            !CourierPlanner.Exact(sender.Before.Character,partner) || !CourierPlanner.Exact(sender.After.Character,partner) ||
            !CourierPlanner.Exact(arm.Before.Character,fresh.Character)) return Unknown();

        var delta = new List<(TradeItem Item,int Units)>();
        var keys = arm.Before.Stock.Concat(fresh.Stock).Concat(sender.Before.Stock).Concat(sender.After.Stock)
            .Concat(state.Original).Select(x=>(x.ItemId,x.Hq)).Distinct();
        foreach (var (id,hq) in keys)
        {
            var item = state.Items.SingleOrDefault(i=>i.ItemId==id && i.IsHighQuality==hq);
            int remain = item?.Quantity ?? 0;
            long original = state.Original.Where(i=>i.ItemId==id && i.Hq==hq).Sum(i=>(long)i.Units);
            long increase = (long)fresh.Count(id,hq)-arm.Before.Count(id,hq);
            long sent = (long)sender.Before.Count(id,hq)-sender.After.Count(id,hq);
            if (increase < 0 || increase > remain || sent != original-remain+increase) return Unknown();
            if (increase > 0) delta.Add((item!,checked((int)increase)));
        }
        foreach (var (item,units) in delta) item.Quantity -= units;
        state.LastSettledArm = arm;
        state.Arm = null;
        save();
        return new(CourierOutcome.Succeeded,delta.Count==0 ? "ReceiveNotDelivered" : "ReceiveDelivered",step.ToString(),true);
    }
}
