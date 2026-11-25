using RQ.Domain;

namespace RQ.Planning;

/// <summary>
/// The item deposition homes catalog (<see cref="QuartermasterState.Placements"/>): one <see cref="ItemPlacementRule"/>
/// per (owner, item, quality), independent of every Listing Plan. Editing placement never touches a Listing Plan.
/// </summary>
public static class PlacementCatalog
{
    /// <summary>The owner's placement rules (a world transfer keeps them: matched by content id when both carry one).</summary>
    public static IReadOnlyList<ItemPlacementRule> OwnerPlacements(QuartermasterState state, OwnerScope owner) =>
        state.Placements.Where(rule => PlacementHome.SameCharacter(rule.Owner, owner)).ToArray();

    public static ItemPlacementRule? Find(QuartermasterState state, OwnerScope owner, uint itemId, ItemQualityPolicy quality) =>
        Find(state.Placements, owner, itemId, quality);

    public static ItemPlacementRule? Find(IEnumerable<ItemPlacementRule> rules, OwnerScope owner, uint itemId, ItemQualityPolicy quality) =>
        rules.FirstOrDefault(rule => rule.ItemId == itemId && rule.Quality == quality && PlacementHome.SameCharacter(rule.Owner, owner));

    /// <summary>The rule for an arriving item variant among one owner's rules: exact quality (an Any rule counts for both).</summary>
    public static ItemPlacementRule? ForArrival(IEnumerable<ItemPlacementRule> ownerRules, uint itemId, bool isHq) =>
        ownerRules.FirstOrDefault(rule => rule.ItemId == itemId &&
            (rule.Quality == ItemQualityPolicy.Any || rule.Quality == ItemQualityPolicy.HqOnly == isHq));

    public static ItemPlacementRule Copy(ItemPlacementRule rule) => new()
    {
        Id = rule.Id,
        Owner = rule.Owner with { },
        ItemId = rule.ItemId,
        ItemName = rule.ItemName,
        Quality = rule.Quality,
        Homes = ReservePlacement.Copy(rule.Homes),
        Consolidate = rule.Consolidate,
        UpdatedAtUtc = rule.UpdatedAtUtc,
    };

    /// <summary>
    /// Sets one item's homes. Creates the rule when needed; removes it when left with no home and no consolidation.
    /// A blank <paramref name="itemName"/> never replaces a known name. Returns the rule, or null when removed or absent.
    /// </summary>
    public static ItemPlacementRule? Upsert(QuartermasterState state, OwnerScope owner, uint itemId, string itemName,
        ItemQualityPolicy quality, IReadOnlyList<PlacementHome> homes, bool consolidate, DateTime nowUtc)
    {
        var rule = Find(state, owner, itemId, quality);
        if (homes.Count == 0 && !consolidate)
        {
            if (rule is not null)
                state.Placements.Remove(rule);
            return null;
        }
        if (rule is null)
        {
            rule = new ItemPlacementRule { Owner = owner with { }, ItemId = itemId, Quality = quality };
            state.Placements.Add(rule);
        }
        if (!string.IsNullOrWhiteSpace(itemName))
            rule.ItemName = itemName.Trim();
        rule.Homes = ReservePlacement.Copy(homes);
        rule.Consolidate = consolidate;
        rule.UpdatedAtUtc = nowUtc;
        return rule;
    }

    /// <summary>
    /// Startup migration, idempotent: names every unnamed Listing Plan "Default", reads legacy reserve homes into
    /// their reserves, moves every enabled reserve's homes and consolidation into <see cref="QuartermasterState.Placements"/>
    /// (an existing placement rule wins), clears them from the reserve, and drops reserves that keep no units
    /// (placement-only stubs). True when anything changed.
    /// </summary>
    public static bool SeparateFromListingPlans(QuartermasterState state)
    {
        state.Placements ??= [];
        var changed = false;
        foreach (var plan in state.ListingPlans)
        {
            if (string.IsNullOrWhiteSpace(plan.Name))
            {
                plan.Name = "Default";
                changed = true;
            }
            changed |= plan.MigrateLegacyHomes();
            foreach (var reserve in plan.Reserves)
            {
                var homes = reserve.Homes ?? [];
                if (homes.Count == 0 && !reserve.Consolidate)
                {
                    if (reserve.Homes is not null)
                    {
                        reserve.Homes = null;
                        changed = true;
                    }
                    continue;
                }
                if (reserve.Enabled && reserve.ItemId > 0 && Find(state, plan.Owner, reserve.ItemId, reserve.Quality) is null)
                    state.Placements.Add(new ItemPlacementRule
                    {
                        Owner = plan.Owner with { },
                        ItemId = reserve.ItemId,
                        ItemName = reserve.ItemName,
                        Quality = reserve.Quality,
                        Homes = ReservePlacement.Copy(homes),
                        Consolidate = reserve.Consolidate,
                        UpdatedAtUtc = plan.UpdatedAtUtc,
                    });
                reserve.Homes = null;
                reserve.Consolidate = false;
                changed = true;
            }
            if (plan.Reserves.RemoveAll(reserve => reserve.ReserveUnits <= 0) > 0)
                changed = true;
        }
        return changed;
    }
}
