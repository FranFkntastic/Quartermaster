using RQ.Domain;

namespace RQ.Planning;

public sealed class RestockPlanDraft
{
    public Guid PlanId { get; init; }
    public long SourceRevision { get; init; }
    public bool IsNew { get; init; }
    public string Name { get; set; } = "Restock plan";
    public bool Enabled { get; set; } = true;
    public List<RestockPlanItem> Items { get; set; } = [];
}

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
        state.Schema = "gooseworks-quartermaster-state/v5";
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

    public static RestockPlanDraft NewDraft(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName = "Restock plan")
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Restock plans require stable owner identity.");
        return new RestockPlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, owner, requestedName),
        };
    }

    public static RestockPlanDraft Draft(
        QuartermasterState state,
        OwnerScope owner,
        Guid planId)
    {
        var plan = state.RestockPlans.Single(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(owner));
        return new RestockPlanDraft
        {
            PlanId = plan.Id,
            SourceRevision = plan.Revision,
            Name = plan.Name,
            Enabled = plan.Enabled,
            Items = plan.Items.Select(CopyWithIdentity).ToList(),
        };
    }

    public static RestockPlanDraft DuplicateDraft(
        QuartermasterState state,
        OwnerScope owner,
        Guid sourcePlanId)
    {
        var source = state.RestockPlans.Single(candidate =>
            candidate.Id == sourcePlanId && candidate.Owner.Matches(owner));
        return new RestockPlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, owner, $"{source.Name} copy"),
            Enabled = source.Enabled,
            Items = source.Items.Select(CopyWithNewIdentity).ToList(),
        };
    }

    public static RestockPlanDraft FromStowageDraft(
        QuartermasterState state,
        OwnerScope owner)
    {
        var stowage = StowagePlanMigration.OwnerPlan(state, owner)
            ?? throw new InvalidOperationException("Owner Stowage Plan is unavailable.");
        return new RestockPlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, owner, $"{stowage.Name} restock"),
            Items = state.PlanItems
                .Where(rule => rule.StowagePlanId == stowage.Id)
                .Select(rule => new RestockPlanItem
                {
                    ItemId = rule.ItemId,
                    ItemName = rule.ItemName,
                    TargetQuantity = rule.TargetQuantity,
                    Quality = rule.Quality,
                    Notes = rule.Notes,
                    Enabled = rule.Enabled,
                })
                .ToList(),
        };
    }

    public static bool CanApply(
        QuartermasterState state,
        OwnerScope owner,
        RestockPlanDraft draft) =>
        !string.IsNullOrWhiteSpace(draft.Name) &&
        (!draft.IsNew || draft.Items.Any(item => item.ItemId > 0)) &&
        HasChanges(state, owner, draft);

    public static bool HasChanges(
        QuartermasterState state,
        OwnerScope owner,
        RestockPlanDraft draft)
    {
        if (draft.IsNew)
            return true;
        var source = state.RestockPlans.FirstOrDefault(candidate =>
            candidate.Id == draft.PlanId && candidate.Owner.Matches(owner));
        return source is null ||
               source.Revision != draft.SourceRevision ||
               !string.Equals(source.Name, draft.Name, StringComparison.Ordinal) ||
               source.Enabled != draft.Enabled ||
               !ItemsEqual(source.Items, draft.Items);
    }

    public static RestockPlan Apply(
        QuartermasterState state,
        OwnerScope owner,
        RestockPlanDraft draft)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Restock plans require stable owner identity.");
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("A Restock Plan needs a name.");
        if (draft.IsNew && draft.Items.All(item => item.ItemId == 0))
            throw new InvalidOperationException("Add at least one item before saving a new Restock Plan.");

        RestockPlan plan;
        if (draft.IsNew)
        {
            if (state.RestockPlans.Any(candidate => candidate.Id == draft.PlanId))
                throw new InvalidOperationException("This new Restock Plan was already saved.");
            plan = new RestockPlan
            {
                Id = draft.PlanId,
                Owner = owner with { },
                Revision = 1,
            };
            state.RestockPlans.Add(plan);
        }
        else
        {
            plan = state.RestockPlans.Single(candidate =>
                candidate.Id == draft.PlanId && candidate.Owner.Matches(owner));
            if (plan.Revision != draft.SourceRevision)
                throw new InvalidOperationException("This Restock Plan changed after the editor opened. Reopen it to continue.");
            plan.Revision = checked(plan.Revision + 1);
        }

        plan.Name = UniqueName(state, owner, draft.Name, plan.Id);
        plan.Enabled = draft.Enabled;
        plan.Items = draft.Items
            .Where(item => item.ItemId > 0)
            .GroupBy(item => (item.ItemId, item.Quality))
            .Select(group => CopyWithIdentity(group.First()))
            .ToList();
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
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

    private static RestockPlanItem CopyWithIdentity(RestockPlanItem item) => new()
    {
        Id = item.Id,
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        TargetQuantity = item.TargetQuantity,
        Quality = item.Quality,
        Notes = item.Notes,
        Enabled = item.Enabled,
    };

    private static bool ItemsEqual(
        IReadOnlyList<RestockPlanItem> left,
        IReadOnlyList<RestockPlanItem> right)
    {
        if (left.Count != right.Count)
            return false;
        return left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.ItemId == pair.Second.ItemId &&
            pair.First.ItemName == pair.Second.ItemName &&
            pair.First.TargetQuantity == pair.Second.TargetQuantity &&
            pair.First.Quality == pair.Second.Quality &&
            pair.First.Notes == pair.Second.Notes &&
            pair.First.Enabled == pair.Second.Enabled);
    }
}
