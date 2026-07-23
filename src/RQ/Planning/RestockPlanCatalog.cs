using RQ.Domain;

namespace RQ.Planning;

public static class RestockPlanCatalog
{
    public static IReadOnlyList<RestockPlan> OwnerPlans(QuartermasterState state, OwnerScope owner) =>
        state.RestockPlans
            .Where(plan => plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .ToArray();

    public static RestockPlan Create(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName = "Restock plan",
        IEnumerable<RestockPlanItem>? items = null)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Restock plans require stable owner identity.");

        var plan = new RestockPlan
        {
            Owner = owner with { },
            Name = UniqueName(state, owner, requestedName),
            Items = items?.Select(CopyWithNewIdentity).ToList() ?? [],
        };
        state.RestockPlans.Add(plan);
        state.Schema = "gooseworks-quartermaster-state/v3";
        return plan;
    }

    public static RestockPlan Duplicate(QuartermasterState state, OwnerScope owner, Guid sourcePlanId)
    {
        var source = state.RestockPlans.Single(plan => plan.Id == sourcePlanId && plan.Owner.Matches(owner));
        return Create(state, owner, $"{source.Name} copy", source.Items);
    }

    public static RestockPlan CreateFromStowage(QuartermasterState state, OwnerScope owner)
    {
        var stowage = StowagePlanMigration.OwnerPlan(state, owner)
            ?? throw new InvalidOperationException("Owner Stowage Plan is unavailable.");
        var items = state.PlanItems
            .Where(rule => rule.StowagePlanId == stowage.Id)
            .Select(rule => new RestockPlanItem
            {
                ItemId = rule.ItemId,
                ItemName = rule.ItemName,
                TargetQuantity = rule.TargetQuantity,
                Quality = rule.Quality,
                Notes = rule.Notes,
                Enabled = rule.Enabled,
            });
        return Create(state, owner, $"{stowage.Name} restock", items);
    }

    public static IReadOnlyList<TargetPlanItem> ToExecutionRows(RestockPlan plan) =>
        plan.Items.Select(item => new TargetPlanItem
        {
            Id = item.Id,
            StowagePlanId = plan.Id,
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            TargetQuantity = item.TargetQuantity,
            Quality = item.Quality,
            Notes = item.Notes,
            Enabled = plan.Enabled && item.Enabled,
        }).ToArray();

    public static string UniqueName(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName,
        Guid? excludingPlanId = null)
    {
        var basis = string.IsNullOrWhiteSpace(requestedName) ? "Restock plan" : requestedName.Trim();
        var used = state.RestockPlans
            .Where(plan => plan.Owner.Matches(owner) && plan.Id != excludingPlanId)
            .Select(plan => plan.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(basis))
            return basis;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{basis} {suffix}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    private static RestockPlanItem CopyWithNewIdentity(RestockPlanItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        TargetQuantity = item.TargetQuantity,
        Quality = item.Quality,
        Notes = item.Notes,
        Enabled = item.Enabled,
    };
}
