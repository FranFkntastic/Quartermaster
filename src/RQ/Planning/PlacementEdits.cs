using RQ.Domain;

namespace RQ.Planning;

/// <summary>
/// Placement edits over a <see cref="StowageRoutingPolicy"/>. A target is a retainer id, or
/// <see cref="AnySpace"/>. Any space is always ranked after every listed retainer.
/// </summary>
public static class PlacementEdits
{
    public const ulong AnySpace = 0;

    /// <summary>
    /// The player's bags as a placement column (Courier Placement). Never a retainer id and
    /// distinct from <see cref="AnySpace"/>; a courier route stores bags as a null retainer.
    /// </summary>
    public const ulong PlayerBags = ulong.MaxValue;

    /// <summary>The placement column a courier route's destination retainer lands in (null is bags).</summary>
    public static ulong CourierColumn(ulong? retainerId) => retainerId ?? PlayerBags;

    /// <summary>The courier route retainer for a placement column (bags is null).</summary>
    public static ulong? CourierRetainer(ulong column) => column == PlayerBags ? null : column;

    /// <summary>
    /// The retainer holding the most units (ties go to the lower id), limited to the candidates
    /// when given; null when none holds any. Bags and Any space never count as a holder.
    /// </summary>
    public static ulong? CurrentHolder(IReadOnlyDictionary<ulong, int> holdings, IEnumerable<ulong>? candidates = null)
    {
        var allowed = candidates?.ToHashSet();
        return holdings
            .Where(entry => entry.Key != AnySpace && entry.Key != PlayerBags && entry.Value > 0 &&
                            (allowed is null || allowed.Contains(entry.Key)))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Select(entry => (ulong?)entry.Key)
            .FirstOrDefault();
    }

    /// <summary>1-based rank of the target in the destination order, or 0 when not listed.</summary>
    public static int Rank(StowageRoutingPolicy routing, ulong target)
    {
        var listed = routing.PreferredRetainerIds.Distinct().ToList();
        if (target == AnySpace)
            return routing.AnySpace ? listed.Count + 1 : 0;
        return listed.IndexOf(target) + 1;
    }

    public static bool Contains(StowageRoutingPolicy routing, ulong target) => Rank(routing, target) > 0;

    /// <summary>Rank the target would take if it were added at the end.</summary>
    public static int RankIfAdded(StowageRoutingPolicy routing, ulong target) =>
        Contains(routing, target) ? Rank(routing, target) : Rank(AddFallback(routing, target), target);

    public static StowageRoutingPolicy Toggle(StowageRoutingPolicy routing, ulong target) =>
        Contains(routing, target) ? Remove(routing, target) : AddFallback(routing, target);

    public static StowageRoutingPolicy AddFallback(StowageRoutingPolicy routing, ulong target)
    {
        var next = routing.Copy();
        if (target == AnySpace)
        {
            next.AnySpace = true;
            if (next.PreferredRetainerIds.Count == 0) next.Consolidate = false;
        }
        else if (!next.PreferredRetainerIds.Contains(target))
            next.PreferredRetainerIds.Add(target);
        return next;
    }

    /// <summary>Any space as first leaves Any space as the only destination.</summary>
    public static StowageRoutingPolicy MakeFirst(StowageRoutingPolicy routing, ulong target)
    {
        if (target == AnySpace)
            return new() { AnySpace = true };
        var next = routing.Copy();
        next.PreferredRetainerIds.Remove(target);
        next.PreferredRetainerIds.Insert(0, target);
        return next;
    }

    public static StowageRoutingPolicy Remove(StowageRoutingPolicy routing, ulong target)
    {
        var next = routing.Copy();
        if (target == AnySpace)
            next.AnySpace = false;
        else
            next.PreferredRetainerIds.Remove(target);
        if (next.PreferredRetainerIds.Count == 0) next.Consolidate = false;
        return next;
    }

    public static StowageRoutingPolicy Clear() => new();

    /// <summary>
    /// Puts the retainers holding the item (most units first) ahead of the existing entries.
    /// Returns the policy unchanged when no retainer holds any.
    /// </summary>
    public static StowageRoutingPolicy WhereItLives(StowageRoutingPolicy routing, IReadOnlyDictionary<ulong, int> holdings)
    {
        var homes = holdings
            .Where(entry => entry.Key != AnySpace && entry.Key != PlayerBags && entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Select(entry => entry.Key)
            .ToList();
        if (homes.Count == 0)
            return routing.Copy();
        var next = routing.Copy();
        next.PreferredRetainerIds = homes.Concat(next.PreferredRetainerIds.Where(id => !homes.Contains(id))).ToList();
        return next;
    }
}
