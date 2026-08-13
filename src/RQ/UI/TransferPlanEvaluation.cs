using Franthropy.FFXIV.Filtering;
using RQ.Domain;
using RQ.Inventory;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal static class TransferPlanEvaluation
{
    public static StowageDepositBatch BuildSurplusBatch(
        QuartermasterRuntimeSnapshot runtime,
        StowageEvaluation? evaluation)
    {
        if (evaluation is null)
            return new(runtime.CapturedAtUtc, []);
        var requests = new List<StowageDepositRequest>();
        foreach (var line in evaluation.Lines.Where(line => line.DepositQuantity > 0))
        {
            var stock = runtime.Browser.Items.FirstOrDefault(item => item.ItemId == line.ItemId);
            var nq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player && stack.Quality == FfxivItemQuality.NQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var hq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player && stack.Quality == FfxivItemQuality.HQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var remaining = line.DepositQuantity;
            if (line.Quality != ItemQualityPolicy.HqOnly)
            {
                var quantity = Math.Min(remaining, nq);
                if (quantity > 0)
                    requests.Add(new(
                        line.PlanId,
                        line.RuleId,
                        line.ItemId,
                        line.ItemName,
                        false,
                        quantity,
                        CopyRouting(line.Routing)));
                remaining -= quantity;
            }
            if (line.Quality != ItemQualityPolicy.NqOnly)
            {
                var quantity = Math.Min(remaining, hq);
                if (quantity > 0)
                    requests.Add(new(
                        line.PlanId,
                        line.RuleId,
                        line.ItemId,
                        line.ItemName,
                        true,
                        quantity,
                        CopyRouting(line.Routing)));
            }
        }
        return StowageRouter.BuildBatch(
            requests,
            runtime.Retainers,
            runtime.Owner,
            itemId => ResolveMaxStack(runtime.Browser, itemId),
            runtime.CapturedAtUtc);
    }

    public static int PlayerQuantity(BrowserProjection browser, TargetPlanItem rule) =>
        browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId)?.Stacks
            .Where(stack =>
                stack.ScopeKind == BrowserScopeKind.Player &&
                (rule.Quality == ItemQualityPolicy.Any ||
                 rule.Quality == ItemQualityPolicy.HqOnly && stack.Quality == FfxivItemQuality.HQ ||
                 rule.Quality == ItemQualityPolicy.NqOnly && stack.Quality == FfxivItemQuality.NQ))
            .Sum(stack => stack.Quantity) ?? 0;

    private static int ResolveMaxStack(BrowserProjection browser, uint itemId) =>
        checked((int)Math.Clamp(
            browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize ?? 999,
            1,
            int.MaxValue));

    private static StowageRoutingPolicy CopyRouting(StowageRoutingPolicy? routing) => new()
    {
        Mode = routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
        Overflow = routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
        PreferredRetainerIds = routing?.PreferredRetainerIds.ToList() ?? [],
    };
}
