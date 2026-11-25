using Franthropy.FFXIV.Filtering;
using RQ.Domain;

namespace RQ.Planning;

public sealed class ListingPlanDraft
{
    public const string DefaultPlanName = "Default";

    public Guid PlanId { get; init; }
    public long SourceRevision { get; set; }
    public bool IsNew { get; init; }
    /// <summary>The plan's name; used when a new plan is first saved.</summary>
    public string Name { get; set; } = DefaultPlanName;
    public OwnerScope Owner { get; init; } = new();
    public List<ListingPlanAssignment> Assignments { get; set; } = [];
    public List<ListingPlanAssignment> BaselineAssignments { get; set; } = [];
    public List<ListingReserveRule> Reserves { get; set; } = [];
    public List<ListingReserveRule> BaselineReserves { get; set; } = [];
    public IReadOnlyList<ulong> IncompleteListingRetainerIds { get; init; } = [];
}

/// <summary>A validation or merge issue. <paramref name="AssignmentId"/> holds the assignment or reserve rule id.</summary>
public sealed record ListingPlanValidationIssue(Guid? AssignmentId, string Field, string Message);

public sealed class ListingPlanValidationException(IReadOnlyList<ListingPlanValidationIssue> issues,
    IReadOnlyList<ListingPlanAssignment>? candidate = null,
    IReadOnlyList<ListingPlanAssignment>? canonicalAssignments = null, long canonicalRevision = 0,
    IReadOnlyList<ListingReserveRule>? candidateReserves = null,
    IReadOnlyList<ListingReserveRule>? canonicalReserves = null)
    : InvalidOperationException(issues.FirstOrDefault()?.Message ?? "The Listing Plan is invalid.")
{
    public IReadOnlyList<ListingPlanValidationIssue> Issues { get; } = issues;
    public IReadOnlyList<ListingPlanAssignment>? Candidate { get; } = candidate;
    public IReadOnlyList<ListingPlanAssignment>? CanonicalAssignments { get; } = canonicalAssignments;
    public long CanonicalRevision { get; } = canonicalRevision;
    public IReadOnlyList<ListingReserveRule>? CandidateReserves { get; } = candidateReserves;
    public IReadOnlyList<ListingReserveRule>? CanonicalReserves { get; } = canonicalReserves;
}

public sealed class ListingPlanConflictException(
    IReadOnlyList<ListingPlanValidationIssue> conflicts,
    IReadOnlyList<ListingPlanAssignment> rebasedAssignments,
    IReadOnlyList<ListingPlanAssignment> canonicalAssignments, long canonicalRevision,
    IReadOnlyList<ListingReserveRule>? rebasedReserves = null,
    IReadOnlyList<ListingReserveRule>? canonicalReserves = null)
    : InvalidOperationException("The Listing Plan changed while this draft was open. Conflicting fields remain in the draft for review.")
{
    public IReadOnlyList<ListingPlanValidationIssue> Conflicts { get; } = conflicts;
    public IReadOnlyList<ListingPlanAssignment> RebasedAssignments { get; } = rebasedAssignments;
    public IReadOnlyList<ListingPlanAssignment> CanonicalAssignments { get; } = canonicalAssignments;
    public long CanonicalRevision { get; } = canonicalRevision;
    public IReadOnlyList<ListingReserveRule> RebasedReserves { get; } = rebasedReserves ?? [];
    public IReadOnlyList<ListingReserveRule> CanonicalReserves { get; } = canonicalReserves ?? [];
}

public static class ListingPlanCatalog
{
    public const int MaxNameLength = 64;

    /// <summary>Every Listing Plan of the owner, by name (case-insensitive) then id.</summary>
    public static IReadOnlyList<ListingPlan> OwnerPlans(QuartermasterState state, OwnerScope owner) =>
        state.ListingPlans.Where(plan => plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .ToArray();

    public static ListingPlan? FindPlan(QuartermasterState state, OwnerScope owner, Guid planId) =>
        state.ListingPlans.FirstOrDefault(plan => plan.Id == planId && plan.Owner.Matches(owner));

    /// <summary>The owner's first plan by name, or null when it has none.</summary>
    public static ListingPlan? DefaultPlan(QuartermasterState state, OwnerScope owner) =>
        OwnerPlans(state, owner).FirstOrDefault();

    /// <summary>The selected plan when it still exists for the owner, else the owner's default plan.</summary>
    public static ListingPlan? SelectedOrDefault(QuartermasterState state, OwnerScope owner, Guid? selectedPlanId) =>
        (selectedPlanId is { } id ? FindPlan(state, owner, id) : null) ?? DefaultPlan(state, owner);

    /// <summary>Why a plan name is unusable for the owner, or null. <paramref name="exceptPlanId"/> is the plan being renamed.</summary>
    public static string? NameIssue(QuartermasterState state, OwnerScope owner, string? name, Guid? exceptPlanId = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return "Name the plan.";
        if (trimmed.Length > MaxNameLength)
            return $"Plan names are at most {MaxNameLength} characters.";
        return state.ListingPlans.Any(plan => plan.Owner.Matches(owner) && plan.Id != exceptPlanId &&
                                              string.Equals(plan.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            ? $"Another Listing Plan is already named \"{trimmed}\"."
            : null;
    }

    /// <summary>A name not yet used by the owner: <paramref name="baseName"/>, else "baseName 2", "baseName 3", ...</summary>
    public static string UniqueName(QuartermasterState state, OwnerScope owner, string baseName)
    {
        var root = string.IsNullOrWhiteSpace(baseName) ? ListingPlanDraft.DefaultPlanName : baseName.Trim();
        if (NameIssue(state, owner, root) is null)
            return root;
        for (var n = 2; ; n++)
        {
            var candidate = $"{root} {n}";
            if (NameIssue(state, owner, candidate) is null)
                return candidate;
        }
    }

    /// <summary>Creates an empty named plan for the owner (with <paramref name="planId"/> when given).</summary>
    public static ListingPlan CreatePlan(QuartermasterState state, OwnerScope owner, string name, DateTime nowUtc, Guid? planId = null)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Listing Plans require stable owner identity.");
        if (NameIssue(state, owner, name) is { } issue)
            throw new InvalidOperationException(issue);
        if (planId is { } id && state.ListingPlans.Any(existing => existing.Id == id))
            throw new InvalidOperationException("This Listing Plan was already saved.");
        var plan = new ListingPlan { Id = planId ?? Guid.NewGuid(), Owner = owner with { }, Name = name.Trim(), UpdatedAtUtc = nowUtc };
        state.ListingPlans.Add(plan);
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
    }

    /// <summary>
    /// Renames a plan. The revision is unchanged: a name is not listing intent, so an open draft and a running
    /// Market Run of the plan stay valid.
    /// </summary>
    public static ListingPlan RenamePlan(QuartermasterState state, OwnerScope owner, Guid planId, string name, DateTime nowUtc)
    {
        var plan = FindPlan(state, owner, planId) ?? throw new InvalidOperationException("That Listing Plan no longer exists.");
        if (NameIssue(state, owner, name, planId) is { } issue)
            throw new InvalidOperationException(issue);
        if (plan.Name == name.Trim())
            return plan;
        plan.Name = name.Trim();
        plan.UpdatedAtUtc = nowUtc;
        return plan;
    }

    /// <summary>Copies a plan's assignments and reserves (new identities) under "Name (copy)", "Name (copy 2)", ...</summary>
    public static ListingPlan DuplicatePlan(QuartermasterState state, OwnerScope owner, Guid sourcePlanId, DateTime nowUtc)
    {
        var source = FindPlan(state, owner, sourcePlanId) ?? throw new InvalidOperationException("That Listing Plan no longer exists.");
        var baseName = string.IsNullOrWhiteSpace(source.Name) ? "Default" : source.Name.Trim();
        var existingNames = OwnerPlans(state, owner)
            .Select(existing => existing.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Candidate(string suffix)
        {
            var room = Math.Max(0, MaxNameLength - suffix.Length);
            var prefix = baseName.Length > room ? baseName[..room].TrimEnd() : baseName;
            return $"{prefix}{suffix}".Trim();
        }
        var name = Candidate(" (copy)");
        for (var n = 2; existingNames.Contains(name); n++)
            name = Candidate($" (copy {n})");
        if (NameIssue(state, owner, name) is { } issue)
            throw new InvalidOperationException(issue);
        var plan = new ListingPlan
        {
            Owner = owner with { },
            Name = name,
            UpdatedAtUtc = nowUtc,
            Assignments = source.Assignments.Select(assignment => { var copy = Copy(assignment); copy.Id = Guid.NewGuid(); return copy; }).ToList(),
            Reserves = source.Reserves.Select(reserve => { var copy = Copy(reserve); copy.Id = Guid.NewGuid(); return copy; }).ToList(),
        };
        state.ListingPlans.Add(plan);
        state.Schema = "gooseworks-quartermaster-state/v5";
        return plan;
    }

    /// <summary>Deletes a plan and every Transfer Plan link to it. True when it existed.</summary>
    public static bool DeletePlan(QuartermasterState state, OwnerScope owner, Guid planId)
    {
        if (FindPlan(state, owner, planId) is not { } plan)
            return false;
        state.ListingPlans.Remove(plan);
        state.TransferPlanListingLinks.RemoveAll(link => link.ListingPlanId == planId);
        return true;
    }

    /// <summary>
    /// The current roster name of <paramref name="retainerId"/> for <paramref name="owner"/>, or null when the
    /// retainer is not assigned to that character in the latest roster observation. Retainer selection uses only
    /// this; a plan's saved <see cref="ListingPlanAssignment.RetainerName"/> is a display fallback.
    /// </summary>
    public static string? CurrentRetainerName(IReadOnlyDictionary<ulong, CachedRetainer> roster, OwnerScope owner, ulong retainerId) =>
        roster.GetValueOrDefault(retainerId) is { } retainer && retainer.Owner.Matches(owner) &&
        retainer.IsCurrentlyAssigned != false && !string.IsNullOrWhiteSpace(retainer.RetainerName)
            ? retainer.RetainerName
            : null;

    /// <summary>True when any plan's saved retainer name differs from the current roster name for the same ID.</summary>
    public static bool HasStaleRetainerNames(QuartermasterState state, IReadOnlyDictionary<ulong, CachedRetainer> roster) =>
        state.ListingPlans.Any(plan => plan.Owner.HasStableIdentity && plan.Assignments.Any(assignment =>
            CurrentRetainerName(roster, plan.Owner, assignment.RetainerId) is { } current && current != assignment.RetainerName));

    /// <summary>
    /// Rewrites stale retainer-name snapshots from the current roster, bumping each changed plan's revision.
    /// Retainers missing from the roster keep their saved name. Returns the number of assignments renamed.
    /// </summary>
    public static int RefreshRetainerNames(QuartermasterState state, IReadOnlyDictionary<ulong, CachedRetainer> roster, DateTime nowUtc)
    {
        var renamed = 0;
        foreach (var plan in state.ListingPlans.Where(plan => plan.Owner.HasStableIdentity))
        {
            var changed = 0;
            foreach (var assignment in plan.Assignments)
            {
                if (CurrentRetainerName(roster, plan.Owner, assignment.RetainerId) is not { } current || current == assignment.RetainerName)
                    continue;
                assignment.RetainerName = current;
                changed++;
            }
            if (changed == 0)
                continue;
            plan.Revision = checked(plan.Revision + 1);
            plan.UpdatedAtUtc = nowUtc;
            renamed += changed;
        }
        return renamed;
    }

    /// <summary>
    /// A draft of the owner's plan <paramref name="planId"/>; with no id (or one that no longer exists), a new
    /// unsaved plan named <paramref name="newName"/> (default "Default").
    /// </summary>
    public static ListingPlanDraft Draft(
        QuartermasterState state,
        OwnerScope owner,
        Guid? planId,
        BrowserProjection projection,
        string? newName = null)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Listing Plans require stable owner identity.");
        var existing = planId is { } id ? FindPlan(state, owner, id) : null;
        var incomplete = projection.Scopes
            .Where(scope => scope.Kind == BrowserScopeKind.Retainer &&
                            !projection.RetainerListingsCompleteByScope.GetValueOrDefault(scope.Key))
            .Select(scope => scope.RetainerId!.Value)
            .ToArray();
        if (existing is not null)
        {
            var assignments = existing.Assignments.Select(Copy).ToList();
            var reserves = existing.Reserves.Select(Copy).ToList();
            return new ListingPlanDraft
            {
                PlanId = existing.Id,
                SourceRevision = existing.Revision,
                Name = existing.Name,
                Owner = owner with { },
                Assignments = assignments,
                BaselineAssignments = assignments.Select(Copy).ToList(),
                Reserves = reserves,
                BaselineReserves = reserves.Select(Copy).ToList(),
                IncompleteListingRetainerIds = incomplete,
            };
        }

        return new ListingPlanDraft
        {
            PlanId = Guid.NewGuid(),
            IsNew = true,
            Name = string.IsNullOrWhiteSpace(newName) ? UniqueName(state, owner, ListingPlanDraft.DefaultPlanName) : newName.Trim(),
            Owner = owner with { },
            Assignments = [],
            BaselineAssignments = [],
            IncompleteListingRetainerIds = [],
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
        IReadOnlyList<ListingReserveRule> candidateReserves;
        var didMerge = false;
        if (draft.IsNew)
        {
            if (state.ListingPlans.Any(existing => existing.Id == draft.PlanId))
                throw new InvalidOperationException("This Listing Plan was already saved.");
            if (NameIssue(state, owner, draft.Name) is { } nameIssue)
                throw new InvalidOperationException(nameIssue);
            plan = new ListingPlan { Id = draft.PlanId, Owner = owner with { }, Name = draft.Name.Trim() };
            candidate = draft.Assignments.Select(Copy).ToArray();
            candidateReserves = draft.Reserves.Select(Copy).ToArray();
        }
        else
        {
            plan = FindPlan(state, owner, draft.PlanId)
                   ?? throw new InvalidOperationException("This Listing Plan was deleted. Discard the draft to continue.");
            didMerge = plan.Revision != draft.SourceRevision;
            if (didMerge)
            {
                var conflicts = new List<ListingPlanValidationIssue>();
                candidate = MergeAssignments(draft.BaselineAssignments, draft.Assignments, plan.Assignments, conflicts);
                candidateReserves = MergeReserves(draft.BaselineReserves, draft.Reserves, plan.Reserves, conflicts);
                if (conflicts.Count != 0)
                    throw new ListingPlanConflictException(conflicts, candidate.Select(Copy).ToArray(),
                        plan.Assignments.Select(Copy).ToArray(), plan.Revision,
                        candidateReserves.Select(Copy).ToArray(), plan.Reserves.Select(Copy).ToArray());
            }
            else
            {
                candidate = draft.Assignments.Select(Copy).ToArray();
                candidateReserves = draft.Reserves.Select(Copy).ToArray();
            }
        }

        var issues = Validate(candidate, candidateReserves, resolveMaxStack, ownerRetainerIds);
        if (issues.Count != 0)
            throw new ListingPlanValidationException(issues,
                didMerge ? candidate.Select(Copy).ToArray() : null,
                didMerge ? plan.Assignments.Select(Copy).ToArray() : null,
                didMerge ? plan.Revision : 0,
                didMerge ? candidateReserves.Select(Copy).ToArray() : null,
                didMerge ? plan.Reserves.Select(Copy).ToArray() : null);
        plan.Assignments = candidate.Select(Copy).ToList();
        plan.Reserves = candidateReserves.Select(Copy).ToList();
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
            if (assignment.ListingCount is < 1 or > MarketRunPlanner.ListingCapacity)
                issues.Add(new(assignment.Id, nameof(assignment.ListingCount), $"Listings must be between 1 and {MarketRunPlanner.ListingCapacity}."));
            var maxStack = assignment.ItemId == 0 ? 1 : Math.Max(1, resolveMaxStack(assignment.ItemId));
            if (assignment.QuantityPerListing < 1 || assignment.QuantityPerListing > maxStack)
                issues.Add(new(assignment.Id, nameof(assignment.QuantityPerListing), $"Quantity per listing must be between 1 and {maxStack:N0}."));
            if (assignment.UnitPrice is < 1 or > 999_999_999)
                issues.Add(new(assignment.Id, nameof(assignment.UnitPrice), "Unit price must be between 1 and 999,999,999 gil."));
        }
        foreach (var retainer in assignments.Where(assignment => assignment.Enabled).GroupBy(assignment => assignment.RetainerId))
        {
            var plannedSlots = retainer.Sum(assignment => assignment.ListingCount);
            if (plannedSlots <= MarketRunPlanner.ListingCapacity)
                continue;
            var retainerName = retainer.Select(assignment => assignment.RetainerName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Retainer {retainer.Key}";
            var message = $"{retainerName} has {plannedSlots:N0} / {MarketRunPlanner.ListingCapacity} planned listing slots.";
            foreach (var assignment in retainer)
                issues.Add(new(assignment.Id, "RetainerCapacity", message));
        }
        return issues;
    }

    public static IReadOnlyList<ListingPlanValidationIssue> Validate(
        IReadOnlyList<ListingPlanAssignment> assignments,
        IReadOnlyList<ListingReserveRule> reserves,
        Func<uint, int> resolveMaxStack,
        IReadOnlySet<ulong> ownerRetainerIds) =>
        Validate(assignments, resolveMaxStack, ownerRetainerIds)
            .Concat(ValidateReserves(reserves, ownerRetainerIds))
            .ToArray();

    public static IReadOnlyList<ListingPlanValidationIssue> ValidateReserves(
        IReadOnlyList<ListingReserveRule> reserves,
        IReadOnlySet<ulong> ownerRetainerIds)
    {
        var issues = new List<ListingPlanValidationIssue>();
        foreach (var duplicate in reserves.GroupBy(reserve => reserve.Id).Where(group => group.Count() > 1))
            issues.Add(new(duplicate.Key, "Id", "Reserve rule identities must be unique."));
        var enabled = reserves.Where(reserve => reserve.Enabled).ToArray();
        foreach (var reserve in enabled)
        {
            if (reserve.ItemId == 0)
                issues.Add(new(reserve.Id, nameof(reserve.ItemId), "Choose an item."));
            if (reserve.Quality == ItemQualityPolicy.Any)
                issues.Add(new(reserve.Id, nameof(reserve.Quality), "Reserve quality must be explicitly NQ or HQ."));
            if (reserve.ReserveUnits is < 0 or > ListingReserveRule.MaxReserveUnits)
                issues.Add(new(reserve.Id, nameof(reserve.ReserveUnits), $"Reserve must be between 0 and {ListingReserveRule.MaxReserveUnits:N0} units."));
        }
        foreach (var duplicate in enabled.GroupBy(reserve => (reserve.ItemId, reserve.Quality)).Where(group => group.Count() > 1))
        {
            var name = duplicate.Select(reserve => reserve.ItemName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                       $"Item {duplicate.Key.ItemId}";
            foreach (var reserve in duplicate)
                issues.Add(new(reserve.Id, "ReserveDuplicate", $"{name} has more than one enabled reserve rule for this quality."));
        }
        return issues;
    }

    /// <summary>Why a home chain is invalid, or null: explicit retainers of identified characters, caps of 0 or more, each home once.</summary>
    public static string? HomesIssue(IReadOnlyList<PlacementHome>? homes)
    {
        if (homes is null)
            return "Reserve homes are missing.";
        if (homes.Any(home => home is null || home.RetainerId == 0))
            return "Every reserve home must name a retainer.";
        if (homes.Any(home => home.Character is not { HasStableIdentity: true }))
            return "Every reserve home must name a character by content id and home world.";
        if (homes.Any(home => home.MaxUnits is < 0))
            return "A reserve home cap cannot be negative.";
        for (var index = 1; index < homes.Count; index++)
            if (homes.Take(index).Any(earlier => earlier.SameHome(homes[index].Character, homes[index].RetainerId)))
                return "A reserve home is listed more than once.";
        return null;
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
        _ = state.StowagePlans.Single(plan => !plan.IsEphemeral && plan.Id == stowagePlanId && plan.Owner.Matches(owner));
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
        _ = state.StowagePlans.Single(plan => !plan.IsEphemeral && plan.Id == stowagePlanId && plan.Owner.Matches(owner));
        _ = state.ListingPlans.Single(plan => plan.Id == listingPlanId && plan.Owner.Matches(owner));
        return state.TransferPlanListingLinks.RemoveAll(link =>
            link.StowagePlanId == stowagePlanId && link.ListingPlanId == listingPlanId &&
            link.ItemId == itemId && link.Quality == quality) > 0;
    }

    public static ListingPlanAssignment AssignmentFromListings(IReadOnlyList<ListingRow> listings)
    {
        if (listings.Count == 0) throw new ArgumentException("At least one listing is required.", nameof(listings));
        var first = listings[0];
        if (listings.Any(row => row.RetainerId != first.RetainerId || row.ItemId != first.ItemId ||
            row.Quality != first.Quality || row.Quantity != first.Quantity ||
            row.UnitPrice.IsKnown != first.UnitPrice.IsKnown ||
            (row.UnitPrice.IsKnown && row.UnitPrice.Value != first.UnitPrice.Value)))
            throw new ArgumentException("Listings must share item, quality, retainer, shape and price evidence.", nameof(listings));
        return new ListingPlanAssignment
        {
            ItemId = first.ItemId, ItemName = first.ItemName,
            Quality = first.Quality == FfxivItemQuality.HQ ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly,
            RetainerId = first.RetainerId, RetainerName = first.RetainerName,
            ListingCount = listings.Count, QuantityPerListing = first.Quantity,
            UnitPrice = first.UnitPrice.IsKnown ? checked((int)first.UnitPrice.Value) : 0,
        };
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

    public static ListingReserveRule Copy(ListingReserveRule source) => new()
    {
        Id = source.Id,
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        Quality = source.Quality,
        ReserveUnits = source.ReserveUnits,
        Enabled = source.Enabled,
    };

    public static bool ReserveEquals(ListingReserveRule left, ListingReserveRule right) =>
        left.Id == right.Id && left.ItemId == right.ItemId && left.ItemName == right.ItemName &&
        left.Quality == right.Quality && left.ReserveUnits == right.ReserveUnits && left.Enabled == right.Enabled;

    private static IReadOnlyList<ListingPlanAssignment> MergeAssignments(
        IReadOnlyList<ListingPlanAssignment> baseline,
        IReadOnlyList<ListingPlanAssignment> draft,
        IReadOnlyList<ListingPlanAssignment> current,
        List<ListingPlanValidationIssue> conflicts) =>
        MergeById(baseline, draft, current, assignment => assignment.Id, Copy, AssignmentEquals, MergeFields,
            "Assignment", "assignment", conflicts);

    private static IReadOnlyList<ListingReserveRule> MergeReserves(
        IReadOnlyList<ListingReserveRule> baseline,
        IReadOnlyList<ListingReserveRule> draft,
        IReadOnlyList<ListingReserveRule> current,
        List<ListingPlanValidationIssue> conflicts) =>
        MergeById(baseline, draft, current, reserve => reserve.Id, Copy, ReserveEquals, MergeFields,
            "Reserve", "reserve rule", conflicts);

    private static IReadOnlyList<T> MergeById<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> draft,
        IReadOnlyList<T> current,
        Func<T, Guid> identity,
        Func<T, T> copy,
        Func<T, T, bool> equals,
        Func<T, T, T, Guid, List<ListingPlanValidationIssue>, T> mergeFields,
        string field,
        string noun,
        List<ListingPlanValidationIssue> conflicts) where T : class
    {
        var baselineById = baseline.ToDictionary(identity);
        var draftById = draft.ToDictionary(identity);
        var currentById = current.ToDictionary(identity);
        var order = draft.Select(identity)
            .Concat(current.Select(identity))
            .Distinct()
            .ToArray();
        var merged = new List<T>();
        foreach (var id in order)
        {
            baselineById.TryGetValue(id, out var original);
            draftById.TryGetValue(id, out var edited);
            currentById.TryGetValue(id, out var canonical);
            if (original is null)
            {
                if (edited is not null && canonical is not null && !equals(edited, canonical))
                {
                    conflicts.Add(new(id, field, $"This newly added {noun} has two different versions."));
                    merged.Add(copy(edited));
                }
                else if (edited is not null || canonical is not null)
                    merged.Add(copy(edited ?? canonical!));
                continue;
            }
            if (edited is null || canonical is null)
            {
                var survivor = edited ?? canonical;
                if (survivor is not null && !equals(survivor, original))
                {
                    conflicts.Add(new(id, field, $"This {noun} was deleted in one revision and edited in another."));
                    if (edited is not null)
                        merged.Add(copy(edited));
                }
                continue;
            }

            merged.Add(mergeFields(original, edited, canonical, id, conflicts));
        }
        return merged;
    }

    private static ListingReserveRule MergeFields(
        ListingReserveRule original,
        ListingReserveRule edited,
        ListingReserveRule canonical,
        Guid id,
        List<ListingPlanValidationIssue> conflicts)
    {
        var result = Copy(original);
        result.ItemId = Merge(original.ItemId, edited.ItemId, canonical.ItemId, id, nameof(result.ItemId), conflicts);
        result.ItemName = Merge(original.ItemName, edited.ItemName, canonical.ItemName, id, nameof(result.ItemName), conflicts);
        result.Quality = Merge(original.Quality, edited.Quality, canonical.Quality, id, nameof(result.Quality), conflicts);
        result.ReserveUnits = Merge(original.ReserveUnits, edited.ReserveUnits, canonical.ReserveUnits, id, nameof(result.ReserveUnits), conflicts);
        result.Enabled = Merge(original.Enabled, edited.Enabled, canonical.Enabled, id, nameof(result.Enabled), conflicts);
        return result;
    }

    private static ListingPlanAssignment MergeFields(
        ListingPlanAssignment original,
        ListingPlanAssignment edited,
        ListingPlanAssignment canonical,
        Guid id,
        List<ListingPlanValidationIssue> conflicts)
    {
        var result = Copy(original);
        result.ItemId = Merge(original.ItemId, edited.ItemId, canonical.ItemId, id, nameof(result.ItemId), conflicts);
        result.ItemName = Merge(original.ItemName, edited.ItemName, canonical.ItemName, id, nameof(result.ItemName), conflicts);
        result.Quality = Merge(original.Quality, edited.Quality, canonical.Quality, id, nameof(result.Quality), conflicts);
        result.RetainerId = Merge(original.RetainerId, edited.RetainerId, canonical.RetainerId, id, nameof(result.RetainerId), conflicts);
        // The name is a display snapshot of the retainer ID, never an edit of its own: it follows whichever ID won,
        // preferring the canonical (roster-refreshed) name for the same ID so a rename never conflicts a draft.
        result.RetainerName = result.RetainerId == canonical.RetainerId && !string.IsNullOrWhiteSpace(canonical.RetainerName)
            ? canonical.RetainerName
            : result.RetainerId == edited.RetainerId ? edited.RetainerName : original.RetainerName;
        result.ListingCount = Merge(original.ListingCount, edited.ListingCount, canonical.ListingCount, id, nameof(result.ListingCount), conflicts);
        result.QuantityPerListing = Merge(original.QuantityPerListing, edited.QuantityPerListing, canonical.QuantityPerListing, id, nameof(result.QuantityPerListing), conflicts);
        result.UnitPrice = Merge(original.UnitPrice, edited.UnitPrice, canonical.UnitPrice, id, nameof(result.UnitPrice), conflicts);
        result.Enabled = Merge(original.Enabled, edited.Enabled, canonical.Enabled, id, nameof(result.Enabled), conflicts);
        return result;
    }

    private static T Merge<T>(
        T baseline,
        T draft,
        T current,
        Guid assignmentId,
        string field,
        ICollection<ListingPlanValidationIssue> conflicts,
        IEqualityComparer<T>? equality = null)
    {
        var comparer = equality ?? EqualityComparer<T>.Default;
        if (comparer.Equals(draft, baseline))
            return current;
        if (comparer.Equals(current, baseline) || comparer.Equals(draft, current))
            return draft;
        conflicts.Add(new(assignmentId, field, $"{field} changed in both revisions (current: {current})."));
        return draft;
    }

    public static bool AssignmentEquals(ListingPlanAssignment left, ListingPlanAssignment right) =>
        left.Id == right.Id && left.ItemId == right.ItemId && left.ItemName == right.ItemName &&
        left.Quality == right.Quality && left.RetainerId == right.RetainerId &&
        left.RetainerName == right.RetainerName && left.ListingCount == right.ListingCount &&
        left.QuantityPerListing == right.QuantityPerListing && left.UnitPrice == right.UnitPrice &&
        left.Enabled == right.Enabled;
}
