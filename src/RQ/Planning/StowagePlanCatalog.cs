using RQ.Domain;

namespace RQ.Planning;

public sealed class StowagePlanDraft
{
    public Guid PlanId { get; init; }
    public long SourceRevision { get; init; }
    public bool IsNew { get; init; }
    public StowagePlanKind Kind { get; init; } = StowagePlanKind.Transfer;
    public string Name { get; set; } = "General";
    public bool Enabled { get; set; } = true;
    public List<TargetPlanItem> Rules { get; set; } = [];
}

public static class StowagePlanCatalog
{
    /// <summary>The name an owner's unsaved plan of a kind always carries.</summary>
    public static string ScratchName(StowagePlanKind kind) =>
        kind == StowagePlanKind.Courier ? "Unsaved courier plan" : "Unsaved plan";

    /// <summary>The default name of a new saved plan of a kind.</summary>
    public static string DefaultName(StowagePlanKind kind) =>
        kind == StowagePlanKind.Courier ? "Courier plan" : "Transfer plan";

    /// <summary>The owner's unsaved plan of a kind, or null before one was created.</summary>
    public static StowagePlan? FindScratch(QuartermasterState state, OwnerScope owner, StowagePlanKind kind = StowagePlanKind.Transfer) =>
        state.StowagePlans.SingleOrDefault(p => p.IsEphemeral && p.Kind == kind && p.Owner.Matches(owner));

    // Additive v5 migration: absent IsEphemeral fields deserialize as false. Create
    // lazily under the repository mutation lock, after the legacy plan migrations.
    // Each owner keeps one unsaved plan per kind: a Transfer scratchpad and a Courier one.
    public static StowagePlan EnsureScratch(QuartermasterState state, OwnerScope owner, StowagePlanKind kind = StowagePlanKind.Transfer)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Transfer plans require stable owner identity.");
        var existing = FindScratch(state, owner, kind);
        if (existing is not null) return existing;
        var plan = new StowagePlan { Owner = owner with { }, Name = ScratchName(kind), IsEphemeral = true, Enabled = false, Kind = kind };
        state.StowagePlans.Add(plan);
        return plan;
    }

    public static StowagePlan PromoteScratch(QuartermasterState state, OwnerScope owner, Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A Transfer Plan needs a name.");
        var plan = state.StowagePlans.Single(p => p.Id == id && p.Owner.Matches(owner) && p.IsEphemeral);
        plan.Name = UniqueName(state, owner, name, id);
        plan.IsEphemeral = false;
        plan.Revision = checked(plan.Revision + 1);
        EnsureScratch(state, owner, plan.Kind);
        ClearStaleRecovery(state);
        return plan;
    }

    public static void SetStockRule(StowagePlanDraft draft, uint itemId, string name, int retrieveTarget, bool stow, ulong? retainerId = null)
    {
        var rule = draft.Rules.FirstOrDefault(r => r.Courier is null && r.ItemId == itemId && r.Quality == ItemQualityPolicy.Any);
        if (rule is null)
        {
            rule = new TargetPlanItem { StowagePlanId = draft.PlanId, ItemId = itemId, ItemName = name, TargetQuantity = Math.Max(0, retrieveTarget) };
            draft.Rules.Add(rule);
        }
        rule.Enabled = true;
        if (stow)
        {
            rule.TargetQuantity = 0;
            rule.Routing = new StowageRoutingPolicy
            {
                PreferredRetainerIds = retainerId.HasValue ? [retainerId.Value] : [],
                AnySpace = !retainerId.HasValue,
            };
        }
    }

    /// <summary>
    /// Adds a line that starts balanced: its target equals the quantity currently carried,
    /// so it moves nothing until the user sets a target. Returns the existing line when the
    /// item and quality are already planned.
    /// </summary>
    public static TargetPlanItem AddBalancedRule(
        StowagePlanDraft draft,
        uint itemId,
        string name,
        ItemQualityPolicy quality,
        int playerQuantity)
    {
        var rule = draft.Rules.FirstOrDefault(r => r.Courier is null && r.ItemId == itemId && r.Quality == quality);
        if (rule is not null)
            return rule;
        rule = new TargetPlanItem
        {
            StowagePlanId = draft.PlanId,
            ItemId = itemId,
            ItemName = name,
            Quality = quality,
            TargetQuantity = Math.Max(0, playerQuantity),
        };
        draft.Rules.Add(rule);
        return rule;
    }

    public static IReadOnlyList<StowagePlan> OwnerPlans(QuartermasterState state, OwnerScope owner, StowagePlanKind? kind = null) =>
        state.StowagePlans
            .Where(plan => !plan.IsEphemeral && plan.Owner.Matches(owner) && (kind is null || plan.Kind == kind))
            .OrderByDescending(plan => plan.Enabled)
            .ThenBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .ToArray();

    /// <summary>
    /// Creates a saved plan. Only Transfer plans take part in the owner's single enabled plan;
    /// a Courier plan is never enabled.
    /// </summary>
    public static StowagePlan Create(
        QuartermasterState state,
        OwnerScope owner,
        string? requestedName = null,
        StowagePlanKind kind = StowagePlanKind.Transfer)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Transfer plans require stable owner identity.");
        var hasEnabledPlan = OwnerPlans(state, owner, StowagePlanKind.Transfer).Any(plan => plan.Enabled);
        var plan = new StowagePlan
        {
            Owner = owner with { },
            Name = UniqueName(state, owner, requestedName ?? DefaultName(kind)),
            Enabled = kind == StowagePlanKind.Transfer && !hasEnabledPlan,
            Priority = OwnerPlans(state, owner).Count,
            Kind = kind,
        };
        state.StowagePlans.Add(plan);
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
    }

    public static StowagePlan Duplicate(
        QuartermasterState state,
        OwnerScope owner,
        Guid sourcePlanId)
    {
        var source = state.StowagePlans.Single(plan =>
            plan.Id == sourcePlanId && plan.Owner.Matches(owner));
        var copy = Create(state, owner, $"{source.Name} copy", source.Kind);
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
            Kind = plan.Kind,
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
        string? requestedName = null,
        StowagePlanKind kind = StowagePlanKind.Transfer)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Transfer plans require stable owner identity.");
        return new StowagePlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Kind = kind,
            Name = UniqueName(state, owner, requestedName ?? DefaultName(kind)),
            Enabled = kind == StowagePlanKind.Transfer && !OwnerPlans(state, owner, StowagePlanKind.Transfer).Any(plan => plan.Enabled),
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
            Kind = source.Kind,
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
            throw new InvalidOperationException("Transfer plans require stable owner identity.");
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("A Transfer Plan needs a name.");
        if (draft.IsNew && draft.Rules.All(rule => rule.ItemId == 0))
            throw new InvalidOperationException("Add at least one item before saving a new Transfer Plan.");

        StowagePlan plan;
        if (draft.IsNew)
        {
            if (state.StowagePlans.Any(candidate => candidate.Id == draft.PlanId))
                throw new InvalidOperationException("This new Transfer Plan was already saved.");
            plan = new StowagePlan
            {
                Id = draft.PlanId,
                Owner = owner with { },
                Revision = 1,
                Priority = OwnerPlans(state, owner).Count,
                Kind = draft.Kind,
            };
        }
        else
        {
            plan = state.StowagePlans.Single(candidate =>
                candidate.Id == draft.PlanId && candidate.Owner.Matches(owner));
            if (plan.Revision != draft.SourceRevision)
                throw new InvalidOperationException("This Transfer Plan changed after the editor opened. Reopen it to continue.");
        }
        // A plan never mixes kinds: Transfer plans hold same-character lines, Courier plans hold courier lines.
        if (draft.Rules.FirstOrDefault(rule => rule.ItemId > 0 && !Holds(plan.Kind, rule)) is { } foreign)
            throw new InvalidOperationException(plan.Kind == StowagePlanKind.Courier
                ? $"{foreign.ItemName} is not a courier line; a Courier Plan only sends items to other characters."
                : $"{foreign.ItemName} is a courier line; send items to other characters from a Courier Plan.");
        if (draft.IsNew)
            state.StowagePlans.Add(plan);
        else
            plan.Revision = checked(plan.Revision + 1);

        plan.Name = plan.IsEphemeral ? ScratchName(plan.Kind) : UniqueName(state, owner, draft.Name, plan.Id);
        plan.Enabled = !plan.IsEphemeral && plan.Kind == StowagePlanKind.Transfer && draft.Enabled;
        if (plan.Enabled)
        {
            foreach (var other in state.StowagePlans.Where(candidate =>
                         !candidate.IsEphemeral && candidate.Kind == StowagePlanKind.Transfer &&
                         candidate.Id != plan.Id && candidate.Owner.Matches(owner) && candidate.Enabled))
            {
                other.Enabled = false;
                other.Revision = checked(other.Revision + 1);
            }
        }

        state.PlanItems.RemoveAll(rule => rule.StowagePlanId == plan.Id);
        state.PlanItems.AddRange(draft.Rules
            .Where(rule => rule.ItemId > 0)
            .GroupBy(LineKey)
            .Select(group => CopyRule(group.First(), plan.Id, newIdentity: false)));
        state.TransferPlanListingLinks.RemoveAll(link =>
            link.StowagePlanId == plan.Id &&
            !state.PlanItems.Any(rule => rule.StowagePlanId == plan.Id && rule.Enabled &&
                                          rule.ItemId == link.ItemId && rule.Quality == link.Quality));
        ClearStaleRecovery(state);
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
    }

    /// <summary>Whether a line belongs in a plan of the kind: courier lines only in Courier plans.</summary>
    public static bool Holds(StowagePlanKind kind, TargetPlanItem rule) =>
        (rule.Courier is not null) == (kind == StowagePlanKind.Courier);

    /// <summary>
    /// A plan holds one ordinary line per item and quality, plus one courier line per source container
    /// and destination character for that item and quality. Where a courier line lands on its
    /// destination is the line's ranked Routing, as for an ordinary line, not part of its identity.
    /// </summary>
    public static (uint ItemId, ItemQualityPolicy Quality, ulong? To, ulong? From, ulong? FromRetainer, bool Courier) LineKey(TargetPlanItem rule) =>
        rule.Courier is { } route
            ? (rule.ItemId, rule.Quality, route.Character.LocalContentId, route.SourceCharacter?.LocalContentId, route.SourceRetainerId, true)
            : (rule.ItemId, rule.Quality, (ulong?)null, (ulong?)null, (ulong?)null, false);

    /// <summary>
    /// Collapses courier lines that share a <see cref="LineKey"/> into the first of them, the way
    /// adding an item already on the route does: quantities sum (saturating at int.MaxValue), the
    /// merged line is enabled when any of them was, and its destinations are the first line's ranked
    /// retainers followed by the others' not already listed, with Any space and Consolidate kept when
    /// any line had them (Consolidate only while a rank 1 exists). Non-courier lines are left alone.
    /// </summary>
    public static void MergeCourierDuplicates(List<TargetPlanItem> rules)
    {
        foreach (var group in rules.Where(rule => rule.Courier is not null).GroupBy(LineKey).Where(group => group.Count() > 1).ToArray())
        {
            var survivor = group.First();
            var total = 0L;
            foreach (var rule in group)
                total += Math.Max(0, rule.TargetQuantity);
            survivor.TargetQuantity = (int)Math.Min(int.MaxValue, total);
            survivor.Enabled = group.Any(rule => rule.Enabled);
            survivor.Routing = group.Skip(1).Aggregate(survivor.Routing, (routing, rule) => UnionRouting(routing, rule.Routing));
            if (string.IsNullOrWhiteSpace(survivor.Notes))
                survivor.Notes = group.Select(rule => rule.Notes).FirstOrDefault(notes => !string.IsNullOrWhiteSpace(notes)) ?? survivor.Notes;
            var redundant = group.Skip(1).ToHashSet();
            rules.RemoveAll(redundant.Contains);
        }
    }

    /// <summary>
    /// The destinations of two merged lines: the first's ranked retainers, then the second's not
    /// already listed; Any space and Consolidate when either had them, Consolidate only with a rank 1.
    /// </summary>
    public static StowageRoutingPolicy UnionRouting(StowageRoutingPolicy? first, StowageRoutingPolicy? second)
    {
        var routing = StowageRoutingPolicy.CopyOf(first);
        foreach (var id in second?.PreferredRetainerIds ?? [])
            if (!routing.PreferredRetainerIds.Contains(id))
                routing.PreferredRetainerIds.Add(id);
        routing.AnySpace |= second?.AnySpace == true;
        routing.Consolidate = (routing.Consolidate || second?.Consolidate == true) && routing.PreferredRetainerIds.Count > 0;
        return routing;
    }

    public static void ClearStaleRecovery(QuartermasterState state)
    {
        if (state.TransferPlanRecovery is not { } recovery)
            return;
        var current = state.StowagePlans.FirstOrDefault(plan =>
            plan.Id == recovery.PlanId && plan.Owner.Matches(recovery.Owner));
        if (current is null || current.Revision != recovery.PlanRevision)
            state.TransferPlanRecovery = null;
    }

    public static string UniqueName(
        QuartermasterState state,
        OwnerScope owner,
        string requestedName,
        Guid? excludingPlanId = null)
    {
        var basis = string.IsNullOrWhiteSpace(requestedName) ? "Transfer plan" : requestedName.Trim();
        var used = state.StowagePlans
            .Where(plan => !plan.IsEphemeral && plan.Owner.Matches(owner) && plan.Id != excludingPlanId)
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
        Routing = StowageRoutingPolicy.CopyOf(source.Routing),
        Courier = source.Courier,
        AllowVendorPurchase = source.AllowVendorPurchase,
        PreferredVendorNpcId = source.PreferredVendorNpcId,
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
            pair.First.AllowVendorPurchase == pair.Second.AllowVendorPurchase &&
            pair.First.PreferredVendorNpcId == pair.Second.PreferredVendorNpcId &&
            pair.First.Notes == pair.Second.Notes &&
            pair.First.Enabled == pair.Second.Enabled &&
            Equals(pair.First.Courier, pair.Second.Courier) &&
            pair.First.Routing.SameAs(pair.Second.Routing));
    }
}

public sealed class ItemGroupDraft
{
    public Guid GroupId { get; init; }
    public long SourceRevision { get; init; }
    public bool IsNew { get; init; }
    public string Name { get; set; } = "Item group";
    public List<ItemGroupItem> Items { get; set; } = [];
}

public static class ItemGroupCatalog
{
    public static IReadOnlyList<ItemGroup> All(QuartermasterState state) =>
        state.ItemGroups
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Id)
            .ToArray();

    public static ItemGroupDraft NewDraft(
        QuartermasterState state,
        string requestedName = "Item group",
        IEnumerable<ItemGroupItem>? items = null) =>
        new()
        {
            GroupId = Guid.NewGuid(),
            IsNew = true,
            Name = UniqueName(state, requestedName),
            Items = NormalizeItems(items ?? []),
        };

    public static ItemGroupDraft NewDraft(
        QuartermasterState state,
        string requestedName,
        IEnumerable<TargetPlanItem> rules) =>
        NewDraft(state, requestedName, ItemsFrom(rules));

    public static ItemGroupDraft NewDraft(
        QuartermasterState state,
        string requestedName,
        IEnumerable<RestockPlanItem> items) =>
        NewDraft(state, requestedName, ItemsFrom(items));

    public static ItemGroupDraft Draft(QuartermasterState state, Guid groupId)
    {
        var group = state.ItemGroups.Single(candidate => candidate.Id == groupId);
        return new ItemGroupDraft
        {
            GroupId = group.Id,
            SourceRevision = group.Revision,
            Name = group.Name,
            Items = NormalizeItems(group.Items),
        };
    }

    public static bool CanApply(QuartermasterState state, ItemGroupDraft draft) =>
        !string.IsNullOrWhiteSpace(draft.Name) &&
        draft.Items.Any(item => item.ItemId > 0) &&
        HasChanges(state, draft);

    public static bool HasChanges(QuartermasterState state, ItemGroupDraft draft)
    {
        if (draft.IsNew)
            return true;
        var source = state.ItemGroups.FirstOrDefault(candidate => candidate.Id == draft.GroupId);
        return source is null ||
               source.Revision != draft.SourceRevision ||
               !string.Equals(source.Name, draft.Name, StringComparison.Ordinal) ||
               !ItemsEqual(source.Items, draft.Items);
    }

    public static ItemGroup Apply(QuartermasterState state, ItemGroupDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("An Item Group needs a name.");
        var items = NormalizeItems(draft.Items);
        if (items.Count == 0)
            throw new InvalidOperationException("An Item Group needs at least one item.");

        ItemGroup group;
        if (draft.IsNew)
        {
            if (state.ItemGroups.Any(candidate => candidate.Id == draft.GroupId))
                throw new InvalidOperationException("This new Item Group was already saved.");
            group = new ItemGroup
            {
                Id = draft.GroupId,
                Revision = 1,
            };
            state.ItemGroups.Add(group);
        }
        else
        {
            group = state.ItemGroups.Single(candidate => candidate.Id == draft.GroupId);
            if (group.Revision != draft.SourceRevision)
                throw new InvalidOperationException("This Item Group changed after the editor opened. Reopen it to continue.");
            group.Revision = checked(group.Revision + 1);
        }

        group.Name = UniqueName(state, draft.Name, group.Id);
        group.Items = items;
        state.Schema = "gooseworks-quartermaster-state/v5";
        return group;
    }

    public static void Delete(QuartermasterState state, Guid groupId, long expectedRevision)
    {
        var group = state.ItemGroups.Single(candidate => candidate.Id == groupId);
        if (group.Revision != expectedRevision)
            throw new InvalidOperationException("This Item Group changed after the editor opened. Reopen it to continue.");
        state.ItemGroups.Remove(group);
        state.Schema = "gooseworks-quartermaster-state/v5";
    }

    public static int AddMissing(ItemGroupDraft draft, IEnumerable<ItemGroupItem> items)
    {
        var existing = draft.Items.Select(item => (item.ItemId, item.Quality)).ToHashSet();
        var added = 0;
        foreach (var item in items.Where(item => item.ItemId > 0 && existing.Add((item.ItemId, item.Quality))))
        {
            draft.Items.Add(CopyItem(item));
            added++;
        }
        SortItems(draft.Items);
        return added;
    }

    public static int AddMissing(ItemGroupDraft draft, IEnumerable<TargetPlanItem> rules) =>
        AddMissing(draft, ItemsFrom(rules));

    public static int AddMissing(ItemGroupDraft draft, IEnumerable<RestockPlanItem> items) =>
        AddMissing(draft, ItemsFrom(items));

    public static ItemGroup Create(
        QuartermasterState state,
        string requestedName,
        IEnumerable<TargetPlanItem> rules)
    {
        var draft = NewDraft(state, requestedName, rules);
        return Apply(state, draft);
    }

    public static void Rename(QuartermasterState state, Guid groupId, string requestedName)
    {
        var group = state.ItemGroups.Single(candidate => candidate.Id == groupId);
        group.Name = UniqueName(state, requestedName, groupId);
        group.Revision = checked(group.Revision + 1);
        state.Schema = "gooseworks-quartermaster-state/v5";
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
        state.Schema = "gooseworks-quartermaster-state/v5";
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
        NormalizeItems(rules
            .Where(rule => rule.ItemId > 0)
            .Select(rule => new ItemGroupItem
            {
                ItemId = rule.ItemId,
                ItemName = rule.ItemName,
                Quality = rule.Quality,
            }));

    private static List<ItemGroupItem> ItemsFrom(IEnumerable<RestockPlanItem> items) =>
        NormalizeItems(items
            .Where(item => item.ItemId > 0)
            .Select(item => new ItemGroupItem
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quality = item.Quality,
            }));

    private static List<ItemGroupItem> NormalizeItems(IEnumerable<ItemGroupItem> items)
    {
        var normalized = items
            .Where(item => item.ItemId > 0)
            .GroupBy(item => (item.ItemId, item.Quality))
            .Select(group => CopyItem(group.First()))
            .ToList();
        SortItems(normalized);
        return normalized;
    }

    private static void SortItems(List<ItemGroupItem> items) =>
        items.Sort((left, right) =>
        {
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.ItemName, right.ItemName);
            if (name != 0)
                return name;
            var id = left.ItemId.CompareTo(right.ItemId);
            return id != 0 ? id : left.Quality.CompareTo(right.Quality);
        });

    private static ItemGroupItem CopyItem(ItemGroupItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        Quality = item.Quality,
    };

    private static bool ItemsEqual(IReadOnlyList<ItemGroupItem> left, IReadOnlyList<ItemGroupItem> right)
    {
        var normalizedLeft = NormalizeItems(left);
        var normalizedRight = NormalizeItems(right);
        return normalizedLeft.Count == normalizedRight.Count &&
               normalizedLeft.Zip(normalizedRight).All(pair =>
                   pair.First.ItemId == pair.Second.ItemId &&
                   pair.First.ItemName == pair.Second.ItemName &&
                   pair.First.Quality == pair.Second.Quality);
    }

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
