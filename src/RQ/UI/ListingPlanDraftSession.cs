using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>Window-lifetime editing intent. Only Save writes canonical state.</summary>
internal sealed class ListingPlanDraftSession(
    StateRepository state,
    Func<BrowserProjection, uint, int> resolveMaxStack,
    Func<uint, bool> canBeHq)
{
    private (int Version, long Runtime) issueKey = (-1, -1);
    private IReadOnlyList<ListingPlanValidationIssue> issues = [];
    public ListingPlanDraft? Draft { get; private set; }
    public int Version { get; private set; }
    public int ChangeCount { get; private set; }
    public bool IsDirty => ChangeCount > 0;
    public int ConflictCount { get; private set; }
    public bool MergedElsewhere { get; private set; }
    public string? Error { get; private set; }
    public Guid? PriceEchoAssignmentId { get; set; }
    public Guid? FocusListingsAssignmentId { get; set; }
    internal Action? BeforeSaveMutate { get; set; }

    public void Ensure(QuartermasterRuntimeSnapshot runtime) => Ensure(runtime.Owner, runtime.State, runtime.Browser);

    private void Ensure(OwnerScope owner, QuartermasterState document, BrowserProjection browser)
    {
        if (!owner.HasStableIdentity) return;
        if (Draft is null || !Draft.Owner.Matches(owner))
        {
            Reset(document, owner, browser);
            return;
        }
        var plan = ListingPlanCatalog.OwnerPlan(document, owner);
        if (!IsDirty)
        {
            if (plan is not null && (plan.Id != Draft.PlanId || plan.Revision != Draft.SourceRevision))
                Reset(document, owner, browser);
            return;
        }
        if (!Draft.IsNew || plan is null) return;
        var local = Draft.Assignments.Select(ListingPlanCatalog.Copy).ToArray();
        var localReserves = Draft.Reserves.Select(ListingPlanCatalog.Copy).ToArray();
        Draft = ListingPlanCatalog.Draft(document, owner, browser);
        Draft.Assignments.AddRange(local);
        Draft.Reserves.AddRange(localReserves);
        MergedElsewhere = true;
        Changed();
    }

    private void Reset(QuartermasterState document, OwnerScope owner, BrowserProjection browser)
    {
        Draft = ListingPlanCatalog.Draft(document, owner, browser);
        ConflictCount = 0;
        MergedElsewhere = false;
        Error = null;
        FocusListingsAssignmentId = null;
        Changed();
    }

    private HashSet<Guid> ChangedIds()
    {
        if (Draft is null) return [];
        var baseline = Draft.BaselineAssignments.ToDictionary(a => a.Id);
        var current = Draft.Assignments.ToDictionary(a => a.Id);
        var baselineReserves = Draft.BaselineReserves.ToDictionary(r => r.Id);
        var currentReserves = Draft.Reserves.ToDictionary(r => r.Id);
        return baseline.Keys.Union(current.Keys).Where(id =>
            !baseline.TryGetValue(id, out var before) || !current.TryGetValue(id, out var after) ||
            !ListingPlanCatalog.AssignmentEquals(before, after))
            .Concat(baselineReserves.Keys.Union(currentReserves.Keys).Where(id =>
                !baselineReserves.TryGetValue(id, out var before) || !currentReserves.TryGetValue(id, out var after) ||
                !ListingPlanCatalog.ReserveEquals(before, after)))
            .ToHashSet();
    }

    private void Changed()
    {
        Version++;
        PriceEchoAssignmentId = null;
        ChangeCount = ChangedIds().Count;
    }

    public IReadOnlySet<ulong> DirtyRetainerIds()
    {
        var ids = ChangedIds();
        return Draft is null ? new HashSet<ulong>() : Draft.Assignments.Concat(Draft.BaselineAssignments)
            .Where(a => ids.Contains(a.Id)).Select(a => a.RetainerId).ToHashSet();
    }

    public IReadOnlySet<ulong> CurrentRetainerIds(QuartermasterRuntimeSnapshot runtime) => runtime.Retainers.Values
        .Where(r => r.Owner.Matches(runtime.Owner) && r.IsCurrentlyAssigned is not false)
        .Select(r => r.RetainerId).ToHashSet();

    public IReadOnlyList<ListingPlanValidationIssue> Issues(QuartermasterRuntimeSnapshot runtime)
    {
        if (issueKey == (Version, runtime.Revision)) return issues;
        issueKey = (Version, runtime.Revision);
        return issues = Draft is null ? [] : ListingPlanCatalog.Validate(Draft.Assignments, Draft.Reserves,
            id => MaxStack(runtime.Browser, id), CurrentRetainerIds(runtime));
    }

    public string SaveBlockedTooltip(QuartermasterRuntimeSnapshot runtime)
    {
        var result = new List<string>();
        var current = CurrentRetainerIds(runtime);
        var validation = Issues(runtime);
        var order = runtime.Browser.Scopes.Where(s => s.RetainerId.HasValue).Select((s, i) => (s.RetainerId!.Value, i)).ToDictionary(x => x.Value, x => x.i);
        foreach (var a in (Draft?.Assignments ?? []).OrderBy(a => order.GetValueOrDefault(a.RetainerId, int.MaxValue)))
        {
            var own = validation.Where(i => i.AssignmentId == a.Id && i.Field != "RetainerCapacity").ToArray();
            if (own.Length == 0) continue;
            var fields = new List<string>();
            if (!current.Contains(a.RetainerId)) fields.Add("retainer unavailable");
            if (a.ListingCount == 0) fields.Add("listings");
            if (a.QuantityPerListing == 0) fields.Add("quantity");
            if (a.UnitPrice == 0) fields.Add("price");
            result.Add($"{a.RetainerName} · {a.ItemName} ({(fields.Count > 0 ? string.Join(", ", fields) : string.Join(", ", own.Select(i => i.Message)))})");
        }
        foreach (var group in (Draft?.Assignments ?? []).Where(a => a.Enabled).GroupBy(a => a.RetainerId).Where(g => g.Sum(a => a.ListingCount) > MarketRunPlanner.ListingCapacity))
            result.Add($"{group.First().RetainerName} ({group.Sum(a => a.ListingCount):N0} / {MarketRunPlanner.ListingCapacity} planned listings)");
        foreach (var r in Draft?.Reserves ?? [])
        {
            var own = validation.Where(i => i.AssignmentId == r.Id).Select(i => i.Message).Distinct().ToArray();
            if (own.Length > 0) result.Add($"{r.ItemName} reserve ({string.Join(", ", own)})");
        }
        return string.Join("; ", result.Take(6)) + (result.Count > 6 ? $"; +{result.Count - 6} more" : "");
    }

    public int Free(ulong retainerId, Guid? exceptAssignmentId = null) => Math.Max(0, MarketRunPlanner.ListingCapacity -
        (Draft?.Assignments.Where(a => a.Enabled && a.RetainerId == retainerId && a.Id != exceptAssignmentId).Sum(a => a.ListingCount) ?? 0));
    public int MaxStack(BrowserProjection browser, uint itemId) => Math.Max(1, resolveMaxStack(browser, itemId));
    public bool CanBeHq(uint itemId) => canBeHq(itemId);

    public ListingPlanAssignment Add(ulong retainerId, string retainerName, uint itemId, string itemName,
        ItemQualityPolicy quality, BrowserProjection browser)
    {
        if (Draft is null || Free(retainerId) == 0 || Draft.Assignments.Count(a => a.RetainerId == retainerId) >= MarketRunPlanner.ListingCapacity)
            throw new InvalidOperationException("No planned capacity remains on this retainer.");
        var assignment = new ListingPlanAssignment { RetainerId = retainerId, RetainerName = retainerName,
            ItemId = itemId, ItemName = itemName, Quality = quality, ListingCount = 0,
            QuantityPerListing = MaxStack(browser, itemId) == 1 ? 1 : 0, UnitPrice = 0 };
        Draft.Assignments.Add(assignment);
        FocusListingsAssignmentId = assignment.Id;
        Changed();
        return assignment;
    }

    private ListingPlanAssignment? Find(Guid id) => Draft?.Assignments.FirstOrDefault(a => a.Id == id);
    public void SetListingCount(Guid id, string text) { if (Find(id) is { } a) { a.ListingCount = ListingPlanInput.Commit(text, Free(a.RetainerId, id)); Changed(); } }
    public void SetQuantity(Guid id, string text, BrowserProjection browser) { if (Find(id) is { } a) { a.QuantityPerListing = ListingPlanInput.Commit(text, MaxStack(browser, a.ItemId)); Changed(); } }
    public void SetUnitPrice(Guid id, string text) { if (Find(id) is { } a) { a.UnitPrice = ListingPlanInput.Commit(text, ListingPlanInput.MaxUnitPrice); Changed(); } }
    public void EndUnitPriceEdit(Guid id, int valueAtActivation) { if (Find(id) is { UnitPrice: > 0 } a && a.UnitPrice != valueAtActivation) PriceEchoAssignmentId = id; }
    public void SetEnabled(Guid id, bool enabled, BrowserProjection browser)
    {
        if (Find(id) is not { } a) return;
        a.Enabled = enabled;
        if (enabled)
        {
            a.ListingCount = Math.Clamp(a.ListingCount, 0, Free(a.RetainerId, id));
            a.QuantityPerListing = MaxStack(browser, a.ItemId) == 1 ? 1 : Math.Clamp(a.QuantityPerListing, 0, MaxStack(browser, a.ItemId));
            a.UnitPrice = Math.Clamp(a.UnitPrice, 0, ListingPlanInput.MaxUnitPrice);
        }
        Changed();
    }
    public void Remove(Guid id) { if (Draft?.Assignments.RemoveAll(a => a.Id == id) > 0) Changed(); }
    public bool CanAdopt(IReadOnlyList<ListingRow> listings, IReadOnlySet<ulong> currentRetainerIds) =>
        Draft is not null && listings.Count > 0 && currentRetainerIds.Contains(listings[0].RetainerId) &&
        listings.Count <= Free(listings[0].RetainerId) && Draft.Assignments.Count(a => a.RetainerId == listings[0].RetainerId) < MarketRunPlanner.ListingCapacity;
    public void Adopt(IReadOnlyList<ListingRow> listings, IReadOnlySet<ulong> currentRetainerIds)
    {
        if (!CanAdopt(listings, currentRetainerIds)) return;
        var a = ListingPlanCatalog.AssignmentFromListings(listings);
        Draft!.Assignments.Add(a);
        FocusListingsAssignmentId = a.Id;
        Changed();
    }
    public bool CanReplan(ulong retainerId, BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds) =>
        Draft is not null && currentRetainerIds.Contains(retainerId) &&
        browser.RetainerListingsCompleteByScope.GetValueOrDefault(BrowserScope.RetainerKey(retainerId));

    public bool CanReplanAll(BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds) =>
        currentRetainerIds.Any(id => CanReplan(id, browser, currentRetainerIds));

    public void Replan(ulong retainerId, BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds)
    {
        if (!CanReplan(retainerId, browser, currentRetainerIds)) return;
        var assignments = AssignmentsFromListings(retainerId, browser);
        Draft!.Assignments.RemoveAll(a => a.RetainerId == retainerId);
        Draft.Assignments.AddRange(assignments);
        Changed();
    }

    public void ReplanAll(BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds)
    {
        var ids = currentRetainerIds.Where(id => CanReplan(id, browser, currentRetainerIds)).Order().ToArray();
        if (ids.Length == 0) return;
        var assignments = ids.SelectMany(id => AssignmentsFromListings(id, browser)).ToArray();
        Draft!.Assignments.RemoveAll(a => ids.Contains(a.RetainerId));
        Draft.Assignments.AddRange(assignments);
        Changed();
    }

    private static ListingPlanAssignment[] AssignmentsFromListings(ulong retainerId, BrowserProjection browser) =>
        browser.Listings.Where(l => l.RetainerId == retainerId)
            .OrderBy(l => l.SlotIndex ?? int.MaxValue)
            .GroupBy(l => new { l.ItemId, l.Quality, l.Quantity, Known = l.UnitPrice.IsKnown,
                Price = l.UnitPrice.IsKnown ? l.UnitPrice.Value : 0 })
            .Select(g => ListingPlanCatalog.AssignmentFromListings(g.ToArray())).ToArray();

    public IReadOnlyList<ListingPlanAssignment> PriceSiblings(Guid id, IReadOnlySet<ulong> currentRetainerIds) =>
        Find(id) is not { UnitPrice: > 0 } a ? [] : Draft!.Assignments.Where(b => b.Enabled &&
            b.RetainerId != a.RetainerId && currentRetainerIds.Contains(b.RetainerId) && b.ItemId == a.ItemId &&
            b.Quality == a.Quality && b.UnitPrice != a.UnitPrice).ToArray();
    public void ApplyPriceToSiblings(Guid id, IReadOnlySet<ulong> currentRetainerIds)
    {
        if (Find(id) is not { } a) return;
        foreach (var sibling in PriceSiblings(id, currentRetainerIds)) sibling.UnitPrice = a.UnitPrice;
        Changed();
    }
    public ListingReserveRule AddReserve(uint itemId, string itemName, ItemQualityPolicy quality)
    {
        if (Draft is null)
            throw new InvalidOperationException("No Listing Plan draft is open.");
        var reserve = new ListingReserveRule { ItemId = itemId, ItemName = itemName, Quality = quality };
        Draft.Reserves.Add(reserve);
        Changed();
        return reserve;
    }

    private ListingReserveRule? FindReserve(Guid id) => Draft?.Reserves.FirstOrDefault(r => r.Id == id);
    public void SetReserveUnits(Guid id, string text) { if (FindReserve(id) is { } r) { r.ReserveUnits = ReserveInput(text); Changed(); } }
    public void SetReserveLocation(Guid id, StowageRoutingPolicy location) { if (FindReserve(id) is { } r) { r.Location = ListingPlanCatalog.CopyLocation(location); Changed(); } }
    public void SetReserveEnabled(Guid id, bool enabled) { if (FindReserve(id) is { } r) { r.Enabled = enabled; Changed(); } }
    public void RemoveReserve(Guid id) { if (Draft?.Reserves.RemoveAll(r => r.Id == id) > 0) Changed(); }

    // Zero is a meaningful reserve (keep nothing spare), unlike listing counts.
    private static int ReserveInput(string text)
    {
        long value = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') continue;
            value = Math.Min(ListingReserveRule.MaxReserveUnits, value * 10 + c - '0');
        }
        return (int)value;
    }

    public void Discard(QuartermasterRuntimeSnapshot runtime)
    {
        if (runtime.Owner.HasStableIdentity) Reset(state.Snapshot(), runtime.Owner, runtime.Browser);
    }

    public void Save(QuartermasterRuntimeSnapshot runtime)
    {
        if (!runtime.Owner.HasStableIdentity) return;
        Error = null;
        Ensure(runtime.Owner, state.Snapshot(), runtime.Browser);
        if (Draft is null) return;
        try
        {
            BeforeSaveMutate?.Invoke();
            state.Mutate(StateChangeKind.Listings, document => ListingPlanCatalog.Apply(document, runtime.Owner, Draft,
                id => MaxStack(runtime.Browser, id), CurrentRetainerIds(runtime), DateTime.UtcNow));
            Reset(state.Snapshot(), runtime.Owner, runtime.Browser);
        }
        catch (ListingPlanConflictException ex)
        {
            Rebase(ex.RebasedAssignments, ex.CanonicalAssignments, ex.RebasedReserves, ex.CanonicalReserves, ex.CanonicalRevision);
            ConflictCount = ex.Conflicts.Count;
        }
        catch (ListingPlanValidationException ex) when (ex.Candidate is not null && ex.CanonicalAssignments is not null)
        {
            Rebase(ex.Candidate, ex.CanonicalAssignments, ex.CandidateReserves ?? [], ex.CanonicalReserves ?? [], ex.CanonicalRevision);
            MergedElsewhere = true;
        }
        catch (InvalidOperationException ex)
        {
            var latest = state.Snapshot();
            if (Draft.IsNew && ListingPlanCatalog.OwnerPlan(latest, runtime.Owner) is not null)
                Ensure(runtime.Owner, latest, runtime.Browser);
            else Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Listing Plan not saved: {ex.Message}";
        }
    }

    private void Rebase(IReadOnlyList<ListingPlanAssignment> assignments, IReadOnlyList<ListingPlanAssignment> baseline,
        IReadOnlyList<ListingReserveRule> reserves, IReadOnlyList<ListingReserveRule> baselineReserves, long revision)
    {
        Draft = new ListingPlanDraft
        {
            PlanId = Draft!.PlanId, Owner = Draft.Owner, IsNew = Draft.IsNew,
            IncompleteListingRetainerIds = Draft.IncompleteListingRetainerIds,
            Assignments = assignments.Select(ListingPlanCatalog.Copy).ToList(),
            BaselineAssignments = baseline.Select(ListingPlanCatalog.Copy).ToList(),
            Reserves = reserves.Select(ListingPlanCatalog.Copy).ToList(),
            BaselineReserves = baselineReserves.Select(ListingPlanCatalog.Copy).ToList(),
            SourceRevision = revision,
        };
        Changed();
    }
}
