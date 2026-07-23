using Franthropy.FFXIV.Filtering;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Planning;

public enum StowageAction
{
    None,
    Retrieve,
    Deposit,
}

public sealed record StowageEvaluationLine(
    Guid PlanId,
    Guid RuleId,
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int PlayerQuantity,
    int DesiredPlayerQuantity,
    int RetrieveQuantity,
    int DepositQuantity,
    StowageAction Action,
    StowageRoutingPolicy Routing);

public sealed record StowageEvaluation(
    Guid PlanId,
    string PlanName,
    long PlanRevision,
    IReadOnlyList<StowageEvaluationLine> Lines)
{
    public int RetrieveQuantity => Lines.Sum(line => line.RetrieveQuantity);
    public int DepositQuantity => Lines.Sum(line => line.DepositQuantity);
}

public static class StowagePlanMigration
{
    public const string MigrationId = "target-plan-to-stowage-v1";

    public static bool EnsureOwnerPlan(StateRepository repository, OwnerScope owner, Func<DateTime>? utcNow = null)
    {
        if (!owner.HasStableIdentity)
            return false;
        if (!repository.Read(state => NeedsMigration(state, owner)))
            return false;

        repository.Mutate(state =>
        {
            if (!NeedsMigration(state, owner))
                return;
            var plan = state.StowagePlans.FirstOrDefault(candidate => candidate.Owner.Matches(owner));
            if (plan is null)
            {
                plan = new StowagePlan
                {
                    Owner = owner with { },
                    Name = "General",
                    Priority = 0,
                    Revision = 1,
                };
                state.StowagePlans.Add(plan);
            }

            foreach (var rule in state.PlanItems.Where(rule => rule.StowagePlanId == Guid.Empty))
            {
                rule.StowagePlanId = plan.Id;
                rule.Routing ??= new StowageRoutingPolicy();
            }

            if (state.StowageMigrations.All(record =>
                    record.MigrationId != MigrationId || !record.Owner.Matches(owner)))
            {
                state.StowageMigrations.Add(new StowageMigrationRecord
                {
                    MigrationId = MigrationId,
                    PlanId = plan.Id,
                    Owner = owner with { },
                    RuleCount = state.PlanItems.Count(rule => rule.StowagePlanId == plan.Id),
                    CompletedAtUtc = (utcNow ?? (() => DateTime.UtcNow))(),
                });
            }

            state.Schema = "gooseworks-quartermaster-state/v3";
        });
        return true;
    }

    public static bool NeedsMigration(QuartermasterState state, OwnerScope owner) =>
        owner.HasStableIdentity &&
        (state.StowagePlans.All(plan => !plan.Owner.Matches(owner)) ||
         state.PlanItems.Any(rule => rule.StowagePlanId == Guid.Empty) ||
         state.StowageMigrations.All(record =>
             record.MigrationId != MigrationId || !record.Owner.Matches(owner)));

    public static StowagePlan? OwnerPlan(QuartermasterState state, OwnerScope owner) =>
        state.StowagePlans
            .Where(plan => plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .FirstOrDefault();

    public static IReadOnlyList<TargetPlanItem> OwnerRules(
        QuartermasterState state,
        OwnerScope owner,
        bool enabledPlansOnly = true)
    {
        var planIds = state.StowagePlans
            .Where(plan => plan.Owner.Matches(owner) && (!enabledPlansOnly || plan.Enabled))
            .Select(plan => plan.Id)
            .ToHashSet();
        return state.PlanItems.Where(rule => planIds.Contains(rule.StowagePlanId)).ToArray();
    }
}

public static class StowageEvaluator
{
    public static IReadOnlyList<StowageEvaluation> Build(
        QuartermasterState state,
        BrowserProjection stock,
        OwnerScope owner)
    {
        var groups = stock.Items.ToDictionary(group => group.ItemId);
        return state.StowagePlans
            .Where(plan => plan.Enabled && plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .Select(plan =>
            {
                var lines = state.PlanItems
                    .Where(rule => rule.StowagePlanId == plan.Id && rule.Enabled && rule.ItemId > 0)
                    .Select(rule => BuildLine(plan, rule, groups.GetValueOrDefault(rule.ItemId)))
                    .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(line => line.ItemId)
                    .ToArray();
                return new StowageEvaluation(plan.Id, plan.Name, plan.Revision, lines);
            })
            .ToArray();
    }

    private static StowageEvaluationLine BuildLine(
        StowagePlan plan,
        TargetPlanItem rule,
        StockGroup? stock)
    {
        var player = stock?.Stacks
            .Where(stack => stack.ScopeKind == BrowserScopeKind.Player && Matches(rule.Quality, stack.Quality))
            .Sum(stack => stack.Quantity) ?? 0;
        var target = Math.Max(0, rule.TargetQuantity);
        var retrieve = Math.Max(0, target - player);
        var deposit = Math.Max(0, player - target);
        return new(
            plan.Id,
            rule.Id,
            rule.ItemId,
            rule.ItemName,
            rule.Quality,
            player,
            target,
            retrieve,
            deposit,
            retrieve > 0 ? StowageAction.Retrieve : deposit > 0 ? StowageAction.Deposit : StowageAction.None,
            Copy(rule.Routing));
    }

    private static bool Matches(ItemQualityPolicy policy, FfxivItemQuality quality) => policy switch
    {
        ItemQualityPolicy.NqOnly => quality == FfxivItemQuality.NQ,
        ItemQualityPolicy.HqOnly => quality == FfxivItemQuality.HQ,
        _ => true,
    };

    private static StowageRoutingPolicy Copy(StowageRoutingPolicy? routing) => new()
    {
        Mode = routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
        Overflow = routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
        PreferredRetainerIds = routing?.PreferredRetainerIds.ToList() ?? [],
    };
}

public sealed record StowageDepositRequest(
    Guid? SourcePlanId,
    Guid? SourceRuleId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity,
    StowageRoutingPolicy Routing,
    ulong? DestinationOverride = null);

public sealed record RetainerStowageCapacity(
    ulong RetainerId,
    string RetainerName,
    DateTime ObservedAtUtc,
    int PartialStackCapacity,
    int EmptySlotCapacity)
{
    public int TotalCapacity => checked(PartialStackCapacity + EmptySlotCapacity);
}

public sealed record StowageAllocation(
    ulong RetainerId,
    string RetainerName,
    int Quantity,
    int Capacity,
    DateTime ObservedAtUtc);

public sealed record StowageRoute(
    StowageDepositRequest Request,
    IReadOnlyList<StowageAllocation> Allocations,
    int RoutedQuantity,
    int RemainingQuantity);

public sealed record StowageDepositBatch(
    DateTime BuiltAtUtc,
    IReadOnlyList<StowageRoute> Routes)
{
    public int PlannedQuantity => Routes.Sum(route => route.RoutedQuantity);
    public int RemainingQuantity => Routes.Sum(route => route.RemainingQuantity);
}

public static class StowageRouter
{
    private static readonly string[] RetainerPageNames =
    [
        "RetainerPage1",
        "RetainerPage2",
        "RetainerPage3",
        "RetainerPage4",
        "RetainerPage5",
        "RetainerPage6",
        "RetainerPage7",
    ];

    public static StowageRoute Route(
        StowageDepositRequest request,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        int maxStackSize)
    {
        if (request.Quantity <= 0 || maxStackSize <= 0)
            return new(request, [], 0, Math.Max(0, request.Quantity));

        var preferred = request.Routing.PreferredRetainerIds
            .Distinct()
            .Select((id, index) => (id, index))
            .ToDictionary(entry => entry.id, entry => entry.index);
        var capacities = cache.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .Where(retainer => request.DestinationOverride is null ||
                               retainer.RetainerId == request.DestinationOverride)
            .Where(retainer => request.DestinationOverride is not null ||
                               request.Routing.Overflow == StowageOverflowPolicy.AnyOwnerRetainer ||
                               preferred.ContainsKey(retainer.RetainerId))
            .Select(retainer => Capacity(retainer, request.ItemId, request.IsHighQuality, maxStackSize))
            .Where(candidate => candidate.TotalCapacity > 0)
            .ToArray();

        IOrderedEnumerable<RetainerStowageCapacity> ordered;
        if (request.DestinationOverride is not null)
        {
            ordered = capacities
                .OrderBy(candidate => candidate.RetainerId == request.DestinationOverride ? 0 : 1)
                .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RetainerId);
        }
        else if (request.Routing.Mode == StowageRoutingMode.HomeFirst && preferred.Count > 0)
        {
            ordered = capacities
                .OrderBy(candidate => preferred.GetValueOrDefault(candidate.RetainerId, int.MaxValue))
                .ThenByDescending(candidate => candidate.PartialStackCapacity > 0)
                .ThenByDescending(candidate => candidate.TotalCapacity)
                .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RetainerId);
        }
        else
        {
            ordered = capacities
                .OrderByDescending(candidate => candidate.PartialStackCapacity > 0)
                .ThenBy(candidate => preferred.GetValueOrDefault(candidate.RetainerId, int.MaxValue))
                .ThenByDescending(candidate => candidate.PartialStackCapacity)
                .ThenByDescending(candidate => candidate.TotalCapacity)
                .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RetainerId);
        }

        var remaining = request.Quantity;
        var allocations = new List<StowageAllocation>();
        foreach (var candidate in ordered)
        {
            var quantity = Math.Min(remaining, candidate.TotalCapacity);
            if (quantity <= 0)
                continue;
            allocations.Add(new(
                candidate.RetainerId,
                candidate.RetainerName,
                quantity,
                candidate.TotalCapacity,
                candidate.ObservedAtUtc));
            remaining -= quantity;
            if (remaining == 0)
                break;
        }

        return new(request, allocations, request.Quantity - remaining, remaining);
    }

    public static StowageDepositBatch BuildBatch(
        IEnumerable<StowageDepositRequest> requests,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        Func<uint, int> maxStackSize,
        DateTime nowUtc) =>
        new(nowUtc, requests
            .Where(request => request.Quantity > 0)
            .Select(request => Route(request, cache, owner, Math.Max(1, maxStackSize(request.ItemId))))
            .ToArray());

    private static RetainerStowageCapacity Capacity(
        CachedRetainer retainer,
        uint itemId,
        bool isHighQuality,
        int maxStackSize)
    {
        if (ElementalCurrencyCatalog.IsElementalCurrency(itemId))
        {
            if (!retainer.ObservedSources.Contains("RetainerCrystals", StringComparer.Ordinal))
                return new(retainer.RetainerId, retainer.RetainerName, retainer.ObservedAtUtc, 0, 0);
            var current = retainer.Bags
                .Where(bag => bag.BagName == "RetainerCrystals")
                .SelectMany(bag => bag.Items)
                .Where(item => item.ItemId == itemId)
                .Sum(item => checked((int)item.Quantity));
            return new(
                retainer.RetainerId,
                retainer.RetainerName,
                retainer.ObservedAtUtc,
                Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - current),
                0);
        }

        if (RetainerPageNames.Any(page => !retainer.ObservedSources.Contains(page, StringComparer.Ordinal)))
            return new(retainer.RetainerId, retainer.RetainerName, retainer.ObservedAtUtc, 0, 0);

        var items = retainer.Bags
            .Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
            .SelectMany(bag => bag.Items)
            .Where(item => item.ItemId > 0 && item.Quantity > 0)
            .ToArray();
        var partial = items
            .Where(item => item.ItemId == itemId && item.IsHq == isHighQuality)
            .Sum(item => Math.Max(0, maxStackSize - checked((int)item.Quantity)));
        var emptySlots = Math.Max(0, 175 - items.Length);
        return new(
            retainer.RetainerId,
            retainer.RetainerName,
            retainer.ObservedAtUtc,
            partial,
            checked(emptySlots * maxStackSize));
    }
}
