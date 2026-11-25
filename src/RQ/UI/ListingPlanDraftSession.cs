using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>What one market slot plans; slots with equal plans collapse into one assignment.</summary>
internal sealed record ListingSlotPlan(uint ItemId, string ItemName, ItemQualityPolicy Quality, int Units, int Price, bool Enabled)
{
    public static ListingSlotPlan Of(ListingPlanAssignment a) => new(a.ItemId, a.ItemName, a.Quality, a.QuantityPerListing, a.UnitPrice, a.Enabled);
    public static ListingSlotPlan Of(ListingRow listing) => new(listing.ItemId, listing.ItemName, ListingRetainerProjection.Quality(listing), listing.Quantity,
        listing.UnitPrice.IsKnown ? (int)Math.Clamp(listing.UnitPrice.Value, 0, ListingPlanInput.MaxUnitPrice) : 0, true);
    public (uint, ItemQualityPolicy, int, int, bool) Shape => (ItemId, Quality, Units, Price, Enabled);
}

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

    /// <summary>
    /// Rewrites individual slots of one retainer: each key gets a new plan, or null to free it.
    /// The retainer's assignments are re-derived from its slots in plan order, so equal plans merge
    /// (summed ListingCount) and partially edited assignments split. An assignment keeps its identity
    /// when its slots are untouched or a regrouped set still matches it, else the largest unclaimed contributor is reused.
    /// Returns the rewritten slots' new keys (freed slots omitted), or null when the edit would
    /// exceed the retainer's listing capacity.
    /// </summary>
    public IReadOnlyList<ListingSlotKey>? RewriteSlots(ulong retainerId, string retainerName,
        IReadOnlyList<(ListingSlotKey Key, ListingSlotPlan? Plan)> rewrites)
    {
        if (Draft is null || rewrites.Count == 0) return null;
        var targets = rewrites.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last().Plan);
        var own = Draft.Assignments.Where(a => a.RetainerId == retainerId).ToArray();
        var entries = new List<(ListingSlotPlan Plan, Guid Source, ListingSlotKey? Key)>();
        foreach (var a in own)
            for (var n = 0; n < a.ListingCount; n++)
            {
                var key = ListingSlotKey.Planned(a.Id, n);
                if (!targets.TryGetValue(key, out var plan)) entries.Add((ListingSlotPlan.Of(a), a.Id, null));
                else if (plan is not null) entries.Add((plan, a.Id, key));
            }
        foreach (var (key, plan) in targets)
            if (key.Source != ListingSlotSource.Planned && plan is not null) entries.Add((plan, Guid.Empty, key));
        var before = own.Where(a => a.Enabled).Sum(a => a.ListingCount);
        var after = entries.Count(e => e.Plan.Enabled);
        if (after > MarketRunPlanner.ListingCapacity && after > before) return null;

        var originals = own.ToDictionary(a => a.Id);
        // Untouched slots stay in their own assignment (even beside an identically shaped one), so an
        // edit never rewrites intent the user did not touch. Touched slots join the first group of the
        // same shape, else form a new group.
        var groups = new List<((uint, ItemQualityPolicy, int, int, bool) Shape, Guid Fixed, List<(int Order, (ListingSlotPlan Plan, Guid Source, ListingSlotKey? Key) Entry)> Items)>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key is not null) continue;
            var g = groups.FindIndex(x => x.Fixed == entries[i].Source);
            if (g < 0) { groups.Add((entries[i].Plan.Shape, entries[i].Source, [])); g = groups.Count - 1; }
            groups[g].Items.Add((i, entries[i]));
        }
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key is null) continue;
            var g = groups.FindIndex(x => Equals(x.Shape, entries[i].Plan.Shape));
            if (g < 0) { groups.Add((entries[i].Plan.Shape, Guid.Empty, [])); g = groups.Count - 1; }
            groups[g].Items.Add((i, entries[i]));
        }
        var claimed = groups.Where(g => g.Fixed != Guid.Empty).Select(g => g.Fixed).ToHashSet();
        var rebuilt = new List<ListingPlanAssignment>();
        var moved = new Dictionary<ListingSlotKey, ListingSlotKey>();
        foreach (var raw in groups.OrderBy(g => g.Items.Min(x => x.Order)))
        {
            var group = raw.Items.OrderBy(x => x.Order).Select(x => x.Entry).ToArray();
            var id = raw.Fixed;
            if (id == Guid.Empty)
            {
                // Prefer a source whose original shape this group still is, then the biggest contributor;
                // the identity follows the intent that stayed, so Save's merge-by-id sees no false edit.
                var sources = group.Where(e => e.Source != Guid.Empty && !claimed.Contains(e.Source))
                    .GroupBy(e => e.Source).OrderByDescending(s => s.Count()).Select(s => s.Key).ToArray();
                id = sources.FirstOrDefault(s => Equals(ListingSlotPlan.Of(originals[s]).Shape, raw.Shape));
                if (id == Guid.Empty) id = sources.FirstOrDefault();
                if (id == Guid.Empty) id = Guid.NewGuid();
                claimed.Add(id);
            }
            var plan = group.First().Plan;
            rebuilt.Add(new ListingPlanAssignment
            {
                Id = id, RetainerId = retainerId, RetainerName = originals.GetValueOrDefault(id)?.RetainerName ?? retainerName,
                ItemId = plan.ItemId, ItemName = plan.ItemName, Quality = plan.Quality, ListingCount = group.Count(),
                QuantityPerListing = plan.Units, UnitPrice = plan.Price, Enabled = plan.Enabled,
            });
            var ordinal = 0;
            foreach (var entry in group)
            {
                if (entry.Key is { } key) moved[key] = ListingSlotKey.Planned(id, ordinal);
                ordinal++;
            }
        }
        // Assignments without slots have no key in the rewrite and must survive untouched.
        for (var i = own.Length - 1; i >= 0; i--)
        {
            if (own[i].ListingCount != 0) continue;
            var next = own.Skip(i + 1).Select(a => rebuilt.FindIndex(b => b.Id == a.Id)).FirstOrDefault(n => n >= 0, -1);
            rebuilt.Insert(next < 0 ? rebuilt.Count : next, own[i]);
        }
        var index = Draft.Assignments.FindIndex(a => a.RetainerId == retainerId);
        Draft.Assignments.RemoveAll(a => a.RetainerId == retainerId);
        Draft.Assignments.InsertRange(index < 0 ? Draft.Assignments.Count : index, rebuilt);
        Changed();
        return rewrites.Select(r => r.Key).Distinct().Where(moved.ContainsKey).Select(k => moved[k]).ToArray();
    }

    /// <summary>
    /// The single gate for adopting an observed listing as a slot's plan. Today it is
    /// Replan's guard; listing-specific proof (for example price evidence) belongs here.
    /// </summary>
    public bool CanAdoptListed(ulong retainerId, ListingRow listing, BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds) =>
        listing.RetainerId == retainerId && CanReplan(retainerId, browser, currentRetainerIds);

    public bool CanReplan(ulong retainerId, BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds) =>
        Draft is not null && currentRetainerIds.Contains(retainerId) &&
        browser.RetainerListingsCompleteByScope.GetValueOrDefault(BrowserScope.RetainerKey(retainerId));

    public bool CanReplanAll(BrowserProjection browser, IReadOnlySet<ulong> currentRetainerIds) =>
        currentRetainerIds.Any(id => CanReplan(id, browser, currentRetainerIds));

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
    public void SetReserveHomes(Guid id, IReadOnlyList<PlacementHome> homes)
    {
        if (FindReserve(id) is not { } r || ReservePlacement.Same(r.Homes, homes)) return;
        r.Homes = ReservePlacement.Copy(homes);
        Changed();
    }
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
