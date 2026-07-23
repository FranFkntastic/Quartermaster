using RQ.Domain;

namespace RQ.Planning;

public sealed class StowagePlanDraft
{
    public Guid PlanId { get; init; }
    public long SourceRevision { get; init; }
    public bool IsNew { get; init; }
    public string Name { get; set; } = "General";
    public bool Enabled { get; set; } = true;
    public List<TargetPlanItem> Rules { get; set; } = [];
}

public static class StowagePlanCatalog
{
    public static IReadOnlyList<StowagePlan> OwnerPlans(QuartermasterState state, OwnerScope owner) =>
        state.StowagePlans
            .Where(plan => plan.Owner.Matches(owner))
            .OrderByDescending(plan => plan.Enabled)
            .ThenBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .ToArray();

    public static StowagePlan Create(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName = "Stowage plan")
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Stowage plans require stable owner identity.");
        var hasEnabledPlan = state.StowagePlans.Any(plan => plan.Owner.Matches(owner) && plan.Enabled);
        var plan = new StowagePlan
        {
            Owner = owner with { },
            Name = UniqueName(state, owner, requestedName),
            Enabled = !hasEnabledPlan,
            Priority = OwnerPlans(state, owner).Count,
        };
        state.StowagePlans.Add(plan);
        state.Schema = "gooseworks-quartermaster-state/v4";
        return plan;
    }

    public static StowagePlan Duplicate(
        QuartermasterState state,
        OwnerScope owner,
        Guid sourcePlanId)
    {
        var source = state.StowagePlans.Single(plan =>
            plan.Id == sourcePlanId && plan.Owner.Matches(owner));
        var copy = Create(state, owner, $"{source.Name} copy");
        copy.Enabled = false;
        foreach (var sourceRule in state.PlanItems.Where(rule => rule.StowagePlanId == source.Id))
            state.PlanItems.Add(CopyRule(sourceRule, copy.Id, newIdentity: true));
        return copy;
    }

    public static StowagePlanDraft Draft(
        QuartermasterState state,
        OwnerScope owner,
        Guid planId)
    {
        var plan = state.StowagePlans.Single(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(owner));
        return new StowagePlanDraft
        {
            PlanId = plan.Id,
            SourceRevision = plan.Revision,
            Name = plan.Name,
            Enabled = plan.Enabled,
            Rules = state.PlanItems
                .Where(rule => rule.StowagePlanId == plan.Id)
                .Select(rule => CopyRule(rule, plan.Id, newIdentity: false))
                .ToList(),
        };
    }

    public static StowagePlanDraft NewDraft(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName = "Stowage plan")
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Stowage plans require stable owner identity.");
        return new StowagePlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, owner, requestedName),
            Enabled = !state.StowagePlans.Any(plan => plan.Owner.Matches(owner) && plan.Enabled),
        };
    }

    public static StowagePlanDraft DuplicateDraft(
        QuartermasterState state,
        OwnerScope owner,
        Guid sourcePlanId)
    {
        var source = state.StowagePlans.Single(plan =>
            plan.Id == sourcePlanId && plan.Owner.Matches(owner));
        return new StowagePlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, owner, $"{source.Name} copy"),
            Enabled = false,
            Rules = state.PlanItems
                .Where(rule => rule.StowagePlanId == source.Id)
                .Select(rule => CopyRule(rule, Guid.Empty, newIdentity: true))
                .Select(rule =>
                {
                    rule.StowagePlanId = Guid.Empty;
                    return rule;
                })
                .ToList(),
        };
    }

    public static bool CanApply(
        QuartermasterState state,
        OwnerScope owner,
        StowagePlanDraft draft) =>
        !string.IsNullOrWhiteSpace(draft.Name) &&
        (!draft.IsNew || draft.Rules.Any(rule => rule.ItemId > 0)) &&
        HasChanges(state, owner, draft);

    public static bool HasChanges(
        QuartermasterState state,
        OwnerScope owner,
        StowagePlanDraft draft)
    {
        if (draft.IsNew)
            return true;
        var plan = state.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == draft.PlanId && candidate.Owner.Matches(owner));
        if (plan is null ||
            plan.Revision != draft.SourceRevision ||
            !string.Equals(plan.Name, draft.Name, StringComparison.Ordinal) ||
            plan.Enabled != draft.Enabled)
            return true;
        var rules = state.PlanItems.Where(rule => rule.StowagePlanId == plan.Id).ToArray();
        return !RulesEqual(rules, draft.Rules);
    }

    public static StowagePlan Apply(
        QuartermasterState state,
        OwnerScope owner,
        StowagePlanDraft draft)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Stowage plans require stable owner identity.");
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("A Stowage Plan needs a name.");
        if (draft.IsNew && draft.Rules.All(rule => rule.ItemId == 0))
            throw new InvalidOperationException("Add at least one item before saving a new Stowage Plan.");

        StowagePlan plan;
        if (draft.IsNew)
        {
            if (state.StowagePlans.Any(candidate => candidate.Id == draft.PlanId))
                throw new InvalidOperationException("This new Stowage Plan was already saved.");
            plan = new StowagePlan
            {
                Id = draft.PlanId,
                Owner = owner with { },
                Revision = 1,
                Priority = OwnerPlans(state, owner).Count,
            };
            state.StowagePlans.Add(plan);
        }
        else
        {
            plan = state.StowagePlans.Single(candidate =>
                candidate.Id == draft.PlanId && candidate.Owner.Matches(owner));
            if (plan.Revision != draft.SourceRevision)
                throw new InvalidOperationException("This Stowage Plan changed after the editor opened. Reopen it to continue.");
            plan.Revision = checked(plan.Revision + 1);
        }

        plan.Name = UniqueName(state, owner, draft.Name, plan.Id);
        plan.Enabled = draft.Enabled;
        if (plan.Enabled)
        {
            foreach (var other in state.StowagePlans.Where(candidate =>
                         candidate.Id != plan.Id && candidate.Owner.Matches(owner) && candidate.Enabled))
            {
                other.Enabled = false;
                other.Revision = checked(other.Revision + 1);
            }
        }

        state.PlanItems.RemoveAll(rule => rule.StowagePlanId == plan.Id);
        state.PlanItems.AddRange(draft.Rules
            .Where(rule => rule.ItemId > 0)
            .GroupBy(rule => (rule.ItemId, rule.Quality))
            .Select(group => CopyRule(group.First(), plan.Id, newIdentity: false)));
        state.Schema = "gooseworks-quartermaster-state/v4";
        return plan;
    }

    public static string UniqueName(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName,
        Guid? excludingPlanId = null)
    {
        var basis = string.IsNullOrWhiteSpace(requestedName) ? "Stowage plan" : requestedName.Trim();
        var used = state.StowagePlans
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

    public static TargetPlanItem CopyRule(
        TargetPlanItem source,
        Guid planId,
        bool newIdentity) => new()
    {
        Id = newIdentity ? Guid.NewGuid() : source.Id,
        StowagePlanId = planId,
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        TargetQuantity = Math.Max(0, source.TargetQuantity),
        Quality = source.Quality,
        Routing = new StowageRoutingPolicy
        {
            Mode = source.Routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
            Overflow = source.Routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
            PreferredRetainerIds = source.Routing?.PreferredRetainerIds.ToList() ?? [],
        },
        Notes = source.Notes,
        Enabled = source.Enabled,
    };

    private static bool RulesEqual(
        IReadOnlyList<TargetPlanItem> left,
        IReadOnlyList<TargetPlanItem> right)
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
            pair.First.Enabled == pair.Second.Enabled &&
            pair.First.Routing.Mode == pair.Second.Routing.Mode &&
            pair.First.Routing.Overflow == pair.Second.Routing.Overflow &&
            pair.First.Routing.PreferredRetainerIds.SequenceEqual(pair.Second.Routing.PreferredRetainerIds));
    }
}

public static class ItemGroupCatalog
{
    public static IReadOnlyList<ItemGroup> All(QuartermasterState state) =>
        state.ItemGroups
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Id)
            .ToArray();

    public static ItemGroup Create(
        QuartermasterState state,
        string requestedName,
        IEnumerable<TargetPlanItem> rules)
    {
        var items = ItemsFrom(rules);
        if (items.Count == 0)
            throw new InvalidOperationException("An Item Group needs at least one item.");
        var group = new ItemGroup
        {
            Name = UniqueName(state, requestedName),
            Items = items,
        };
        state.ItemGroups.Add(group);
        state.Schema = "gooseworks-quartermaster-state/v4";
        return group;
    }

    public static void Rename(QuartermasterState state, Guid groupId, string requestedName)
    {
        var group = state.ItemGroups.Single(candidate => candidate.Id == groupId);
        group.Name = UniqueName(state, requestedName, groupId);
        group.Revision = checked(group.Revision + 1);
        state.Schema = "gooseworks-quartermaster-state/v4";
    }

    public static void ReplaceItems(
        QuartermasterState state,
        Guid groupId,
        IEnumerable<TargetPlanItem> rules)
    {
        var items = ItemsFrom(rules);
        if (items.Count == 0)
            throw new InvalidOperationException("An Item Group needs at least one item.");
        var group = state.ItemGroups.Single(candidate => candidate.Id == groupId);
        group.Items = items;
        group.Revision = checked(group.Revision + 1);
        state.Schema = "gooseworks-quartermaster-state/v4";
    }

    public static int AddMissing(ItemGroup group, StowagePlanDraft draft)
    {
        var existing = draft.Rules
            .Select(rule => (rule.ItemId, rule.Quality))
            .ToHashSet();
        var added = 0;
        foreach (var item in group.Items.Where(item => existing.Add((item.ItemId, item.Quality))))
        {
            draft.Rules.Add(new TargetPlanItem
            {
                StowagePlanId = draft.PlanId,
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quality = item.Quality,
                TargetQuantity = 0,
                Enabled = false,
            });
            added++;
        }
        return added;
    }

    public static IReadOnlySet<Guid> MatchingRuleIds(ItemGroup group, StowagePlanDraft draft)
    {
        var members = group.Items.Select(item => (item.ItemId, item.Quality)).ToHashSet();
        return draft.Rules
            .Where(rule => members.Contains((rule.ItemId, rule.Quality)))
            .Select(rule => rule.Id)
            .ToHashSet();
    }

    public static int AddMissing(ItemGroup group, RestockPlanDraft draft)
    {
        var existing = draft.Items
            .Select(item => (item.ItemId, item.Quality))
            .ToHashSet();
        var added = 0;
        foreach (var item in group.Items.Where(item => existing.Add((item.ItemId, item.Quality))))
        {
            draft.Items.Add(new RestockPlanItem
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quality = item.Quality,
                TargetQuantity = 1,
                Enabled = false,
            });
            added++;
        }
        return added;
    }

    public static IReadOnlySet<Guid> MatchingItemIds(ItemGroup group, RestockPlanDraft draft)
    {
        var members = group.Items.Select(item => (item.ItemId, item.Quality)).ToHashSet();
        return draft.Items
            .Where(item => members.Contains((item.ItemId, item.Quality)))
            .Select(item => item.Id)
            .ToHashSet();
    }

    public static ItemGroup Create(
        QuartermasterState state,
        string requestedName,
        IEnumerable<RestockPlanItem> items) =>
        Create(state, requestedName, items.Select(ItemAsRule));

    public static void ReplaceItems(
        QuartermasterState state,
        Guid groupId,
        IEnumerable<RestockPlanItem> items) =>
        ReplaceItems(state, groupId, items.Select(ItemAsRule));

    public static string UniqueName(
        QuartermasterState state,
        string requestedName,
        Guid? excludingGroupId = null)
    {
        var basis = string.IsNullOrWhiteSpace(requestedName) ? "Item group" : requestedName.Trim().TrimStart('@');
        var used = state.ItemGroups
            .Where(group => group.Id != excludingGroupId)
            .Select(group => group.Name)
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

    private static List<ItemGroupItem> ItemsFrom(IEnumerable<TargetPlanItem> rules) =>
        rules
            .Where(rule => rule.ItemId > 0)
            .GroupBy(rule => (rule.ItemId, rule.Quality))
            .Select(group => new ItemGroupItem
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                Quality = group.Key.Quality,
            })
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToList();

    private static TargetPlanItem ItemAsRule(RestockPlanItem item) => new()
    {
        Id = item.Id,
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        TargetQuantity = item.TargetQuantity,
        Quality = item.Quality,
        Notes = item.Notes,
        Enabled = item.Enabled,
    };
}
