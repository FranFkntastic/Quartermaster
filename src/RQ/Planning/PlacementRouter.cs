using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;

namespace RQ.Planning;

/// <summary>One item variant to place: what arrives and how many.</summary>
public sealed record PlacementItem(uint ItemId, string ItemName, bool IsHq, int Quantity);

/// <summary>Units placed in one home retainer.</summary>
public sealed record PlacementAllocation(OwnerScope Character, ulong RetainerId, string RetainerName, int Units, int Rank);

/// <summary>
/// Where one item variant goes. UnplacedReason: null when everything is placed; <see cref="PlacementRouter.NoHome"/>
/// when the item has no home; <see cref="PlacementRouter.AtCap"/> when a capped home with physical room stopped it and
/// no uncapped home had room; <see cref="PlacementRouter.NoRoom"/> otherwise (slots exhausted or a home unobserved).
/// UnobservedHomes: homes whose stock or free slots are unknown, which therefore took nothing.
/// </summary>
public sealed record PlacementRoute(PlacementItem Item, IReadOnlyList<PlacementAllocation> Allocations, int Unplaced, string? UnplacedReason)
{
    public int Placed => Item.Quantity - Unplaced;
    public IReadOnlyList<PlacementHome> UnobservedHomes { get; init; } = [];
}

/// <summary>
/// The one deposition router: walks an item's ranked homes across every character in order and places
/// <c>min(physical room, max(0, MaxUnits - held))</c> in each until nothing is left. Room and holdings come from the
/// observed retainer cache, projected across the whole batch so items sharing a retainer share its slots honestly.
/// A home whose stock is unobserved takes nothing: unknown stock is never read as zero held. Pure; never mutates the
/// cache it is given. Used by the arrival plan, delivered stow and the market run alike.
/// </summary>
public static class PlacementRouter
{
    public const string NoHome = "NoHome";
    public const string AtCap = "AtCap";
    public const string NoRoom = "NoRoom";

    /// <summary>Routes every item over <paramref name="homesOf"/>'s homes, in item order, against one projected cache.</summary>
    public static IReadOnlyList<PlacementRoute> Route(
        IEnumerable<(PlacementItem Item, IReadOnlyList<PlacementHome> Homes)> items,
        IReadOnlyDictionary<ulong, CachedRetainer> observed,
        Func<uint, int> maxStackSize)
    {
        var projected = new Dictionary<ulong, CachedRetainer>();
        var routes = new List<PlacementRoute>();
        foreach (var (item, homes) in items)
            routes.Add(RouteOne(item, homes, observed, projected, maxStackSize));
        return routes;
    }

    /// <summary>Routes one item against a projection the caller keeps across calls (pass an empty dictionary to start).</summary>
    public static PlacementRoute RouteOne(
        PlacementItem item,
        IReadOnlyList<PlacementHome> homes,
        IReadOnlyDictionary<ulong, CachedRetainer> observed,
        Dictionary<ulong, CachedRetainer> projected,
        Func<uint, int> maxStackSize)
    {
        if (item.Quantity <= 0)
            return new(item, [], 0, null);
        if (homes.Count == 0)
            return new(item, [], item.Quantity, NoHome);

        var stackSize = Math.Max(1, maxStackSize(item.ItemId));
        var quality = item.IsHq ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
        var remaining = item.Quantity;
        var allocations = new List<PlacementAllocation>();
        var unobserved = new List<PlacementHome>();
        var stoppedByCap = false;
        for (var index = 0; index < homes.Count && remaining > 0; index++)
        {
            var home = homes[index];
            if (Retainer(home, observed, projected) is not { } retainer || !Observed(retainer, item.ItemId))
            {
                unobserved.Add(home);
                continue;
            }
            var capacity = StowageRouter.Capacity(retainer, item.ItemId, item.IsHq, stackSize);
            var physical = capacity.TotalCapacity;
            var capRoom = home.MaxUnits is { } cap ? Math.Max(0, cap - StowageRouter.Held(retainer, item.ItemId, quality)) : int.MaxValue;
            var take = Math.Min(remaining, Math.Min(physical, capRoom));
            if (take > 0)
            {
                allocations.Add(new(home.Character, home.RetainerId, retainer.RetainerName, take, index + 1));
                StowageRouter.ApplyProjectedDeposit(retainer,
                    new StowageDepositRequest(null, null, item.ItemId, item.ItemName, item.IsHq, take, new StowageRoutingPolicy()), take, stackSize);
                remaining -= take;
            }
            // Physical room this home still has was refused by its cap: the cap, not space, is what stops the item here.
            if (remaining > 0 && physical > take && capRoom <= take)
                stoppedByCap = true;
        }
        // Every home with room took what it could, so units left over were stopped by a cap or by space (or by stock
        // nobody observed, which UnobservedHomes names).
        var reason = remaining <= 0 ? null : stoppedByCap ? AtCap : NoRoom;
        return new(item, allocations, Math.Max(0, remaining), reason) { UnobservedHomes = unobserved };
    }

    /// <summary>
    /// The owner's share of a route as a stowage batch: one route per home retainer with exactly its planned units
    /// (DestinationOverride) and that retainer as its only candidate, so execution can never deposit past a cap or
    /// into a retainer the plan did not choose. What it leaves stays in the bags.
    /// </summary>
    public static StowageDepositBatch LocalBatch(
        IEnumerable<(PlacementRoute Route, Guid? PlanId, Guid? RuleId, StowageRoutingPolicy Routing)> routes,
        IReadOnlyDictionary<ulong, CachedRetainer> observed,
        OwnerScope owner,
        Func<uint, int> maxStackSize,
        DateTime nowUtc)
    {
        var stowage = new List<StowageRoute>();
        foreach (var (route, planId, ruleId, routing) in routes)
        {
            var stackSize = Math.Max(1, maxStackSize(route.Item.ItemId));
            foreach (var allocation in route.Allocations.Where(allocation => allocation.Units > 0 && PlacementHome.SameCharacter(allocation.Character, owner)))
            {
                if (!observed.TryGetValue(allocation.RetainerId, out var retainer) || !retainer.Owner.Matches(owner))
                    continue;
                var request = new StowageDepositRequest(planId, ruleId, route.Item.ItemId, route.Item.ItemName, route.Item.IsHq,
                    allocation.Units, StowageRoutingPolicy.CopyOf(routing), allocation.RetainerId, stackSize);
                var live = StowageRouter.Capacity(retainer, route.Item.ItemId, route.Item.IsHq, stackSize);
                stowage.Add(new StowageRoute(request,
                    [new StowageAllocation(allocation.RetainerId, retainer.RetainerName, allocation.Units, live.TotalCapacity, retainer.ObservedAtUtc)],
                    allocation.Units, 0)
                {
                    Candidates = [live],
                });
            }
        }
        return new(nowUtc, stowage);
    }

    /// <summary>
    /// Owner-local routing for owner stowage requests (market run stow and relocation): each request's own retainers
    /// (its routing order) with the caps it carries, as a per-retainer batch.
    /// </summary>
    public static StowageDepositBatch LocalBatch(
        IEnumerable<StowageDepositRequest> requests,
        IReadOnlyDictionary<ulong, CachedRetainer> observed,
        OwnerScope owner,
        Func<uint, int> maxStackSize,
        DateTime nowUtc)
    {
        var projected = new Dictionary<ulong, CachedRetainer>();
        var routes = new List<(PlacementRoute, Guid?, Guid?, StowageRoutingPolicy)>();
        foreach (var request in requests.Where(request => request.Quantity > 0))
        {
            var homes = request.Routing.PreferredRetainerIds.Distinct()
                .Select(id => new PlacementHome(owner, id, request.Caps?.TryGetValue(id, out var cap) == true ? cap : null))
                .ToArray();
            var route = RouteOne(new(request.ItemId, request.ItemName, request.IsHighQuality, request.Quantity), homes, observed, projected, maxStackSize);
            routes.Add((route, request.SourcePlanId, request.SourceRuleId, request.Routing));
        }
        return LocalBatch(routes, observed, owner, maxStackSize, nowUtc);
    }

    /// <summary>The owner's homes of a rule, in global rank order: its routing for owner stowage, and their caps.</summary>
    public static (StowageRoutingPolicy Routing, IReadOnlyDictionary<ulong, int> Caps) OwnerRouting(ListingReserveRule rule, OwnerScope owner)
    {
        var mine = rule.Homes.Where(home => PlacementHome.SameCharacter(home.Character, owner)).ToArray();
        return (new StowageRoutingPolicy { PreferredRetainerIds = mine.Select(home => home.RetainerId).Distinct().ToList(), Consolidate = rule.Consolidate },
            mine.Where(home => home.MaxUnits is not null).GroupBy(home => home.RetainerId).ToDictionary(group => group.Key, group => group.First().MaxUnits!.Value));
    }

    private static CachedRetainer? Retainer(PlacementHome home, IReadOnlyDictionary<ulong, CachedRetainer> observed, Dictionary<ulong, CachedRetainer> projected)
    {
        if (projected.TryGetValue(home.RetainerId, out var copy))
            return PlacementHome.SameCharacter(copy.Owner, home.Character) ? copy : null;
        if (!observed.TryGetValue(home.RetainerId, out var retainer) || !PlacementHome.SameCharacter(retainer.Owner, home.Character) ||
            retainer.IsCurrentlyAssigned is false || retainer.IsUiAccessible is false)
            return null;
        copy = StowageRouter.Copy(retainer);
        projected[home.RetainerId] = copy;
        return copy;
    }

    /// <summary>Whether the retainer's holdings and room for this item were observed (all pages; the crystal bag for crystals).</summary>
    private static bool Observed(CachedRetainer retainer, uint itemId) =>
        ElementalCurrencyCatalog.IsElementalCurrency(itemId) ? StockCoverage.Crystals(retainer) : StowageRouter.FreeInventorySlots(retainer) is not null;
}
