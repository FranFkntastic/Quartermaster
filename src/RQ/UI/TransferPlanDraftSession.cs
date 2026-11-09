using System.Text.Json;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.UI;

/// <summary>One owner-bound collection of drafts, shared by every planning entry point.</summary>
internal sealed class TransferPlanDraftSession(StateRepository repository)
{
    private sealed record Entry(StowagePlanDraft Draft, StowagePlanDraft? Baseline)
    {
        public HashSet<Guid> RemovedLinks { get; init; } = [];
    }
    private readonly Dictionary<Guid, Entry> drafts = [];
    private OwnerScope? owner;
    public StowagePlanDraft? Draft { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<StowagePlanDraft> Drafts => drafts.Values.Select(e => e.Draft).ToArray();
    public IReadOnlySet<Guid> RemovedLinks => Draft is { } d && drafts.TryGetValue(d.PlanId, out var e) ? e.RemovedLinks : new HashSet<Guid>();
    public void Unlink(Guid id) { if (Draft is { } d) drafts[d.PlanId].RemovedLinks.Add(id); }
    public void Forget(Guid id)
    {
        drafts.Remove(id);
        if (Draft?.PlanId == id) { Draft = null; Error = null; }
    }
    public bool HasChanges => Draft is { } draft && drafts.TryGetValue(draft.PlanId, out var entry) &&
        (entry.RemovedLinks.Count > 0 || (entry.Baseline is null ? draft.Rules.Count > 0 || draft.Name != "Transfer plan" : !Equal(draft, entry.Baseline)));

    public void Ensure(OwnerScope currentOwner, Guid? selected)
    {
        if (!currentOwner.HasStableIdentity) return;
        if (owner is null || !owner.Matches(currentOwner))
        {
            drafts.Clear(); Draft = null; Error = null; owner = currentOwner with { };
        }
        var state = repository.Snapshot();
        if (selected is { } id && drafts.TryGetValue(id, out var cached)) Draft = cached.Draft;
        else if (selected is { } planId && StowagePlanCatalog.OwnerPlans(state, currentOwner).Any(p => p.Id == planId))
            Adopt(StowagePlanCatalog.Draft(state, currentOwner, planId));
        else if (Draft is null)
        {
            var first = StowagePlanCatalog.OwnerPlans(state, currentOwner).FirstOrDefault();
            Adopt(first is null ? StowagePlanCatalog.NewDraft(state, currentOwner) : StowagePlanCatalog.Draft(state, currentOwner, first.Id));
        }
        Reconcile(state);
    }

    public void Adopt(StowagePlanDraft draft)
    {
        if (drafts.TryGetValue(draft.PlanId, out var existing)) { Draft = existing.Draft; return; }
        drafts[draft.PlanId] = new(draft, draft.IsNew ? null : Copy(draft));
        Draft = draft; Error = null;
    }

    public TargetPlanItem Add(uint itemId, string name, ItemQualityPolicy quality, int target, bool replaceTarget = false)
    {
        var draft = Draft ?? throw new InvalidOperationException("Transfer draft is unavailable.");
        var rule = draft.Rules.FirstOrDefault(r => r.ItemId == itemId && r.Quality == quality);
        if (rule is null)
        {
            rule = new() { StowagePlanId = draft.PlanId, ItemId = itemId, ItemName = name, Quality = quality, TargetQuantity = Math.Max(0, target) };
            draft.Rules.Add(rule);
        }
        else if (replaceTarget) rule.TargetQuantity = Math.Max(0, target);
        rule.Enabled = true;
        return rule;
    }

    public bool Save(OwnerScope currentOwner)
    {
        if (owner is null || !currentOwner.HasStableIdentity || !owner.Matches(currentOwner) || Draft is null)
        { Error = "Transfer plan was not saved: owner identity is unavailable or changed."; return false; }
        try
        {
            if (Reconcile(repository.Snapshot())) return false;
            if (Draft.Rules.GroupBy(r => (r.ItemId, r.Quality)).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Two rows use the same item and quality. Combine their targets before saving.");
            var saved = repository.Mutate(state =>
            {
                state.TransferPlanListingLinks.RemoveAll(l => l.StowagePlanId == Draft.PlanId && RemovedLinks.Contains(l.Id));
                return StowagePlanCatalog.Apply(state, currentOwner, Draft);
            });
            drafts.Remove(Draft.PlanId);
            Adopt(StowagePlanCatalog.Draft(repository.Snapshot(), currentOwner, saved.Id));
            Error = null;
            return true;
        }
        catch (Exception exception)
        {
            if (Reconcile(repository.Snapshot())) return false;
            Error = $"Transfer plan was not saved: {exception.Message}";
            return false;
        }
    }

    public bool Prepare(ListingPlanItemEvaluation demand, QuartermasterState state)
    {
        if (Draft is not { } draft || !demand.MovementNeedUnits.IsKnown)
        { Error = "Listing observations are unavailable; the target was not changed."; return false; }
        if (state.TransferPlanListingLinks.Any(l => l.StowagePlanId == draft.PlanId &&
            l.ItemId == demand.ItemId && l.Quality == demand.Quality && !RemovedLinks.Contains(l.Id)))
        { Error = "This item already uses a live listing-demand link in this plan."; return false; }
        if (draft.Rules.Any(r => r.ItemId == demand.ItemId && r.Quality == ItemQualityPolicy.Any))
        { Error = "This item has an Any-quality target. Choose NQ or HQ in the plan before preparing exact-quality listings."; return false; }
        var rule = Add(demand.ItemId, demand.ItemName, demand.Quality, demand.MovementNeedUnits.Value);
        rule.TargetQuantity = Math.Max(rule.TargetQuantity, demand.MovementNeedUnits.Value);
        Error = null;
        return true;
    }

    public void Discard(OwnerScope currentOwner)
    {
        if (owner is null || !owner.Matches(currentOwner) || Draft is null) return;
        var id = Draft.PlanId;
        drafts.Remove(id); Draft = null; Error = null;
        Ensure(currentOwner, id);
    }

    // Rebase independent changes automatically; colliding records retain the draft and
    // require an explicit subsequent Save against the new baseline, never a reopen.
    private bool Reconcile(QuartermasterState state)
    {
        if (Draft is not { IsNew: false } draft || owner is null || !drafts.TryGetValue(draft.PlanId, out var entry)) return false;
        var saved = state.StowagePlans.FirstOrDefault(p => p.Id == draft.PlanId && p.Owner.Matches(owner));
        if (saved is null) { Error = "This plan was deleted elsewhere. Your draft is retained; duplicate it to save a new plan."; return true; }
        if (saved.Revision == draft.SourceRevision) return false;
        var fresh = StowagePlanCatalog.Draft(state, owner, saved.Id);
        var baseline = entry.Baseline!;
        if (Equal(draft, baseline)) { drafts[draft.PlanId] = new(fresh, Copy(fresh)) { RemovedLinks = entry.RemovedLinks }; Draft = fresh; return false; }
        var conflicts = 0;
        T Merge<T>(T before, T local, T remote)
        {
            if (Equal(local, before)) return remote;
            if (!Equal(remote, before) && !Equal(remote, local)) conflicts++;
            return local;
        }
        var merged = new StowagePlanDraft
        {
            PlanId = draft.PlanId, SourceRevision = fresh.SourceRevision,
            Name = Merge(baseline.Name, draft.Name, fresh.Name),
            Enabled = Merge(baseline.Enabled, draft.Enabled, fresh.Enabled),
        };
        foreach (var id in baseline.Rules.Concat(draft.Rules).Concat(fresh.Rules).Select(r => r.Id).Distinct())
        {
            var rule = Merge(baseline.Rules.FirstOrDefault(r => r.Id == id), draft.Rules.FirstOrDefault(r => r.Id == id), fresh.Rules.FirstOrDefault(r => r.Id == id));
            if (rule is not null) merged.Rules.Add(StowagePlanCatalog.CopyRule(rule, draft.PlanId, false));
        }
        // Independently added records can have different identities for the same item/quality.
        // Keep the local record explicitly rather than letting Apply silently drop either one.
        merged.Rules = merged.Rules.GroupBy(r => (r.ItemId, r.Quality)).Select(g =>
        {
            if (g.Count() > 1) conflicts++;
            return g.FirstOrDefault(r => draft.Rules.Any(local => local.Id == r.Id)) ?? g.First();
        }).ToList();
        drafts[draft.PlanId] = new(merged, Copy(fresh)) { RemovedLinks = entry.RemovedLinks }; Draft = merged;
        Error = conflicts > 0 ? $"Plan changed elsewhere: {conflicts} conflicts kept your draft. Review, then Save or Discard." : null;
        return conflicts > 0;
    }

    private static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static StowagePlanDraft Copy(StowagePlanDraft draft) => JsonSerializer.Deserialize<StowagePlanDraft>(JsonSerializer.Serialize(draft))!;
}
