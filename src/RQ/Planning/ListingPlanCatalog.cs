using Franthropy.FFXIV.Filtering;
using RQ.Domain;

namespace RQ.Planning;

public sealed class ListingPlanDraft
{
    public Guid PlanId { get; init; }
    public long SourceRevision { get; set; }
    public bool IsNew { get; init; }
    public OwnerScope Owner { get; init; } = new();
    public List<ListingPlanAssignment> Assignments { get; set; } = [];
    public List<ListingPlanAssignment> BaselineAssignments { get; set; } = [];
    public IReadOnlyList<ulong> IncompleteListingRetainerIds { get; init; } = [];
}

public sealed record ListingPlanValidationIssue(Guid? AssignmentId, string Field, string Message);

public sealed class ListingPlanValidationException(IReadOnlyList<ListingPlanValidationIssue> issues)
    : InvalidOperationException(issues.FirstOrDefault()?.Message ?? "The Listing Plan is invalid.")
{
    public IReadOnlyList<ListingPlanValidationIssue> Issues { get; } = issues;
}

public sealed class ListingPlanConflictException(
    IReadOnlyList<ListingPlanValidationIssue> conflicts,
    IReadOnlyList<ListingPlanAssignment> rebasedAssignments)
    : InvalidOperationException("The Listing Plan changed while this draft was open. Conflicting fields remain in the draft for review.")
{
    public IReadOnlyList<ListingPlanValidationIssue> Conflicts { get; } = conflicts;
    public IReadOnlyList<ListingPlanAssignment> RebasedAssignments { get; } = rebasedAssignments;
}

public static class ListingPlanCatalog
{
    public static ListingPlan? OwnerPlan(QuartermasterState state, OwnerScope owner) =>
        state.ListingPlans.SingleOrDefault(plan => plan.Owner.Matches(owner));

    public static ListingPlanDraft Draft(
        QuartermasterState state,
        OwnerScope owner,
        BrowserProjection projection)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Listing Plans require stable owner identity.");
        var existing = OwnerPlan(state, owner);
        var incomplete = projection.Scopes
            .Where(scope => scope.Kind == BrowserScopeKind.Retainer &&
                            !projection.RetainerListingsCompleteByScope.GetValueOrDefault(scope.Key))
            .Select(scope => scope.RetainerId!.Value)
            .ToArray();
        if (existing is not null)
        {
            var assignments = existing.Assignments.Select(Copy).ToList();
            return new ListingPlanDraft
            {
                PlanId = existing.Id,
                SourceRevision = existing.Revision,
                Owner = owner with { },
                Assignments = assignments,
                BaselineAssignments = assignments.Select(Copy).ToList(),
                IncompleteListingRetainerIds = incomplete,
            };
        }

        var seeded = projection.Listings
            .Where(listing => projection.RetainerListingsCompleteByScope.GetValueOrDefault(listing.ScopeKey))
            .GroupBy(listing => new
            {
                listing.ItemId,
                listing.ItemName,
                listing.Quality,
                listing.RetainerId,
                listing.RetainerName,
                listing.Quantity,
                UnitPrice = listing.UnitPrice.IsKnown ? checked((int)listing.UnitPrice.Value) : 0,
            })
            .Select(group => new ListingPlanAssignment
            {
                ItemId = group.Key.ItemId,
                ItemName = group.Key.ItemName,
                Quality = group.Key.Quality == FfxivItemQuality.HQ
                    ? ItemQualityPolicy.HqOnly
                    : ItemQualityPolicy.NqOnly,
                RetainerId = group.Key.RetainerId,
                RetainerName = group.Key.RetainerName,
                ListingCount = group.Count(),
                QuantityPerListing = group.Key.Quantity,
                UnitPrice = group.Key.UnitPrice,
            })
            .OrderBy(assignment => assignment.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(assignment => assignment.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(assignment => assignment.Quality)
            .ThenBy(assignment => assignment.QuantityPerListing)
            .ThenBy(assignment => assignment.UnitPrice)
            .ToList();
        return new ListingPlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Owner = owner with { },
            Assignments = seeded,
            BaselineAssignments = [],
            IncompleteListingRetainerIds = incomplete,
        };
    }

    public static ListingPlan Apply(
        QuartermasterState state,
        OwnerScope owner,
        ListingPlanDraft draft,
        Func<uint, int> resolveMaxStack,
        IReadOnlySet<ulong> ownerRetainerIds,
        DateTime nowUtc)
    {
        if (!owner.HasStableIdentity || !draft.Owner.Matches(owner))
            throw new InvalidOperationException("Listing Plan owner identity does not match the current owner.");

        ListingPlan plan;
        IReadOnlyList<ListingPlanAssignment> candidate;
        if (draft.IsNew)
        {
            if (state.ListingPlans.Any(existing => existing.Owner.Matches(owner) || existing.Id == draft.PlanId))
                throw new InvalidOperationException("This owner already has a Listing Plan.");
            plan = new ListingPlan { Id = draft.PlanId, Owner = owner with { } };
            candidate = draft.Assignments.Select(Copy).ToArray();
        }
        else
        {
            plan = state.ListingPlans.Single(existing => existing.Id == draft.PlanId && existing.Owner.Matches(owner));
            candidate = plan.Revision == draft.SourceRevision
                ? draft.Assignments.Select(Copy).ToArray()
                : MergeAssignments(draft.BaselineAssignments, draft.Assignments, plan.Assignments);
        }

        var issues = Validate(candidate, resolveMaxStack, ownerRetainerIds);
        if (issues.Count != 0)
            throw new ListingPlanValidationException(issues);
        plan.Assignments = candidate.Select(Copy).ToList();
        plan.UpdatedAtUtc = nowUtc;
        if (draft.IsNew)
            state.ListingPlans.Add(plan);
        else
            plan.Revision = checked(plan.Revision + 1);
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
    }

    public static IReadOnlyList<ListingPlanValidationIssue> Validate(
        IReadOnlyList<ListingPlanAssignment> assignments,
        Func<uint, int> resolveMaxStack,
        IReadOnlySet<ulong> ownerRetainerIds)
    {
        var issues = new List<ListingPlanValidationIssue>();
        foreach (var duplicate in assignments.GroupBy(assignment => assignment.Id).Where(group => group.Count() > 1))
            issues.Add(new(duplicate.Key, "Id", "Listing assignment identities must be unique."));
        foreach (var assignment in assignments.Where(assignment => assignment.Enabled))
        {
            if (assignment.ItemId == 0)
                issues.Add(new(assignment.Id, nameof(assignment.ItemId), "Choose an item."));
            if (assignment.Quality == ItemQualityPolicy.Any)
                issues.Add(new(assignment.Id, nameof(assignment.Quality), "Listing quality must be explicitly NQ or HQ."));
            if (assignment.RetainerId == 0 || !ownerRetainerIds.Contains(assignment.RetainerId))
                issues.Add(new(assignment.Id, nameof(assignment.RetainerId), "Choose a retainer owned by this character."));
            if (assignment.ListingCount is < 1 or > 20)
                issues.Add(new(assignment.Id, nameof(assignment.ListingCount), "Listings must be between 1 and 20."));
            var maxStack = assignment.ItemId == 0 ? 1 : Math.Max(1, resolveMaxStack(assignment.ItemId));
            if (assignment.QuantityPerListing < 1 || assignment.QuantityPerListing > maxStack)
                issues.Add(new(assignment.Id, nameof(assignment.QuantityPerListing), $"Quantity per listing must be between 1 and {maxStack:N0}."));
            if (assignment.UnitPrice is < 1 or > 999_999_999)
                issues.Add(new(assignment.Id, nameof(assignment.UnitPrice), "Unit price must be between 1 and 999,999,999 gil."));
        }
        foreach (var retainer in assignments.Where(assignment => assignment.Enabled).GroupBy(assignment => assignment.RetainerId))
        {
            var plannedSlots = retainer.Sum(assignment => assignment.ListingCount);
            if (plannedSlots <= 20)
                continue;
            var retainerName = retainer.Select(assignment => assignment.RetainerName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Retainer {retainer.Key}";
            var message = $"{retainerName} has {plannedSlots:N0} / 20 planned listing slots.";
            foreach (var assignment in retainer)
                issues.Add(new(assignment.Id, "RetainerCapacity", message));
        }
        return issues;
    }

    public static TransferPlanListingLink Link(
        QuartermasterState state,
        OwnerScope owner,
        Guid stowagePlanId,
        Guid listingPlanId,
        uint itemId,
        ItemQualityPolicy quality)
    {
        if (quality == ItemQualityPolicy.Any)
            throw new InvalidOperationException("Listing demand links require exact NQ or HQ quality.");
        _ = state.StowagePlans.Single(plan => plan.Id == stowagePlanId && plan.Owner.Matches(owner));
        var listingPlan = state.ListingPlans.Single(plan => plan.Id == listingPlanId && plan.Owner.Matches(owner));
        if (!listingPlan.Assignments.Any(assignment => assignment.Enabled && assignment.ItemId == itemId && assignment.Quality == quality))
            throw new InvalidOperationException("The Listing Plan has no enabled assignment for this item and quality.");
        if (!state.PlanItems.Any(rule => rule.StowagePlanId == stowagePlanId && rule.ItemId == itemId &&
                                         rule.Quality == quality && rule.Enabled))
            throw new InvalidOperationException("The Transfer Plan needs an enabled exact-quality base target before listing demand can be linked.");
        var existing = state.TransferPlanListingLinks.SingleOrDefault(link =>
            link.StowagePlanId == stowagePlanId && link.ListingPlanId == listingPlanId &&
            link.ItemId == itemId && link.Quality == quality);
        if (existing is not null)
            return existing;
        var link = new TransferPlanListingLink
        {
            StowagePlanId = stowagePlanId,
            ListingPlanId = listingPlanId,
            ItemId = itemId,
            Quality = quality,
        };
        state.TransferPlanListingLinks.Add(link);
        return link;
    }

    public static bool Unlink(
        QuartermasterState state,
        OwnerScope owner,
        Guid stowagePlanId,
        Guid listingPlanId,
        uint itemId,
        ItemQualityPolicy quality)
    {
        _ = state.StowagePlans.Single(plan => plan.Id == stowagePlanId && plan.Owner.Matches(owner));
        _ = state.ListingPlans.Single(plan => plan.Id == listingPlanId && plan.Owner.Matches(owner));
        return state.TransferPlanListingLinks.RemoveAll(link =>
            link.StowagePlanId == stowagePlanId && link.ListingPlanId == listingPlanId &&
            link.ItemId == itemId && link.Quality == quality) > 0;
    }

    public static ListingPlanAssignment Copy(ListingPlanAssignment source) => new()
    {
        Id = source.Id,
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        Quality = source.Quality,
        RetainerId = source.RetainerId,
        RetainerName = source.RetainerName,
        ListingCount = source.ListingCount,
        QuantityPerListing = source.QuantityPerListing,
        UnitPrice = source.UnitPrice,
        Enabled = source.Enabled,
    };

    private static IReadOnlyList<ListingPlanAssignment> MergeAssignments(
        IReadOnlyList<ListingPlanAssignment> baseline,
        IReadOnlyList<ListingPlanAssignment> draft,
        IReadOnlyList<ListingPlanAssignment> current)
    {
        var baselineById = baseline.ToDictionary(assignment => assignment.Id);
        var draftById = draft.ToDictionary(assignment => assignment.Id);
        var currentById = current.ToDictionary(assignment => assignment.Id);
        var order = draft.Select(assignment => assignment.Id)
            .Concat(current.Select(assignment => assignment.Id))
            .Distinct()
            .ToArray();
        var merged = new List<ListingPlanAssignment>();
        var conflicts = new List<ListingPlanValidationIssue>();
        foreach (var id in order)
        {
            baselineById.TryGetValue(id, out var original);
            draftById.TryGetValue(id, out var edited);
            currentById.TryGetValue(id, out var canonical);
            if (original is null)
            {
                if (edited is not null && canonical is not null && !AssignmentEquals(edited, canonical))
                {
                    conflicts.Add(new(id, "Assignment", "This newly added assignment has two different versions."));
                    merged.Add(Copy(edited));
                }
                else if (edited is not null || canonical is not null)
                    merged.Add(Copy(edited ?? canonical!));
                continue;
            }
            if (edited is null || canonical is null)
            {
                var survivor = edited ?? canonical;
                if (survivor is not null && !AssignmentEquals(survivor, original))
                {
                    conflicts.Add(new(id, "Assignment", "This assignment was deleted in one revision and edited in another."));
                    if (edited is not null)
                        merged.Add(Copy(edited));
                }
                continue;
            }

            var result = Copy(original);
            result.ItemId = Merge(original.ItemId, edited.ItemId, canonical.ItemId, id, nameof(result.ItemId), conflicts);
            result.ItemName = Merge(original.ItemName, edited.ItemName, canonical.ItemName, id, nameof(result.ItemName), conflicts);
            result.Quality = Merge(original.Quality, edited.Quality, canonical.Quality, id, nameof(result.Quality), conflicts);
            result.RetainerId = Merge(original.RetainerId, edited.RetainerId, canonical.RetainerId, id, nameof(result.RetainerId), conflicts);
            result.RetainerName = Merge(original.RetainerName, edited.RetainerName, canonical.RetainerName, id, nameof(result.RetainerName), conflicts);
            result.ListingCount = Merge(original.ListingCount, edited.ListingCount, canonical.ListingCount, id, nameof(result.ListingCount), conflicts);
            result.QuantityPerListing = Merge(original.QuantityPerListing, edited.QuantityPerListing, canonical.QuantityPerListing, id, nameof(result.QuantityPerListing), conflicts);
            result.UnitPrice = Merge(original.UnitPrice, edited.UnitPrice, canonical.UnitPrice, id, nameof(result.UnitPrice), conflicts);
            result.Enabled = Merge(original.Enabled, edited.Enabled, canonical.Enabled, id, nameof(result.Enabled), conflicts);
            merged.Add(result);
        }
        if (conflicts.Count != 0)
            throw new ListingPlanConflictException(conflicts, merged.Select(Copy).ToArray());
        return merged;
    }

    private static T Merge<T>(
        T baseline,
        T draft,
        T current,
        Guid assignmentId,
        string field,
        ICollection<ListingPlanValidationIssue> conflicts)
    {
        var comparer = EqualityComparer<T>.Default;
        if (comparer.Equals(draft, baseline))
            return current;
        if (comparer.Equals(current, baseline) || comparer.Equals(draft, current))
            return draft;
        conflicts.Add(new(assignmentId, field, $"{field} changed in both revisions (current: {current})."));
        return draft;
    }

    private static bool AssignmentEquals(ListingPlanAssignment left, ListingPlanAssignment right) =>
        left.Id == right.Id && left.ItemId == right.ItemId && left.ItemName == right.ItemName &&
        left.Quality == right.Quality && left.RetainerId == right.RetainerId &&
        left.RetainerName == right.RetainerName && left.ListingCount == right.ListingCount &&
        left.QuantityPerListing == right.QuantityPerListing && left.UnitPrice == right.UnitPrice &&
        left.Enabled == right.Enabled;
}
