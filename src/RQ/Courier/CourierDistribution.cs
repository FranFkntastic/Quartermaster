using System.Collections.Immutable;
using RQ.Domain;

namespace RQ.Courier;

/// <summary>
/// One place listed under a courier line. Has is the observed units of the line's item there (null
/// when the container was never observed); Min and Max are its stored bounds (neither: a pool); End is
/// where the place lands once the line's moves are done.
/// </summary>
public sealed record CourierSpot(OwnerScope Character, ulong? Retainer, int? Has, int? Min, int? Max, int End)
{
    public int Delta => Has is null ? 0 : End - Has.Value;

    public bool IsPool => Min is null && Max is null;
}

/// <summary>Units moving from one listed place to another, by index into the allocation's places.</summary>
public readonly record struct CourierFlow(int From, int To, int Units);

/// <summary>
/// A line's solved moves. Places are its entries in listed order. Line is the line as solved (older
/// lines converted). Short is what bounded places still want below their Min once every move is made,
/// Stuck what they still hold above their Max with nowhere listed to go. Holders are observed places
/// holding the item that the line does not list and the courier can use; Suggestion is the pool offered
/// while the line lists a single place.
/// </summary>
public sealed record CourierAllocation(
    CourierLine Line,
    ImmutableArray<CourierSpot> Places,
    ImmutableArray<CourierFlow> Flows,
    int Short = 0,
    int Stuck = 0)
{
    public ImmutableArray<CourierSpot> Holders { get; init; } = [];
    public CourierSpot? Suggestion { get; init; }

    public int Total => Places.Sum(p => p.Has ?? 0);
    public int Moving => Flows.Sum(f => f.Units);

    /// <summary>Units the run moves: every solved move runs, even while the line is short or has surplus left.</summary>
    public int Runs => Moving;

    /// <summary>Whether the line leaves something for the user: a shortfall, unplaced surplus, or nothing to move between.</summary>
    public bool NeedsAttention => Short > 0 || Stuck > 0 || Moving == 0 && Line.Entries.Length < 2;
}

/// <summary>
/// Turns a courier line's places and their bounds into moves. Solving is shared by the planner and
/// the plan table so the numbers the user sees are the numbers the run moves.
/// </summary>
public static class CourierDistribution
{
    private static bool Wanted(BagStock stack, uint item, ItemQualityPolicy quality) =>
        stack.ItemId == item && (quality == ItemQualityPolicy.Any || stack.Hq == (quality == ItemQualityPolicy.HqOnly));

    private static int Count(CourierLocation at, uint item, ItemQualityPolicy quality) =>
        at.Inventory.Stock.Where(s => Wanted(s, item, quality)).Sum(s => s.Units);

    /// <summary>Units of the item a container can still take: the rest of its partial stacks and its free slots.</summary>
    private static long Room(CourierLocation at, uint item, ItemQualityPolicy quality, int size) =>
        at.Inventory.Stock.Where(s => Wanted(s, item, quality) && s.Units > 0).Sum(s => (long)((size - s.Units % size) % size)) +
        (long)Math.Max(0, at.Inventory.FreeSlots) * size;

    private static CourierLocation? Observed(IReadOnlyList<CourierLocation> locations, OwnerScope who, ulong? retainer) =>
        locations.FirstOrDefault(l => l.Retainer == retainer && l.Character.Matches(who));

    /// <summary>
    /// Observed containers holding the item on a <paramref name="listable"/> character: the plan
    /// owner's first, then in <paramref name="locations"/> order.
    /// </summary>
    private static IEnumerable<(CourierLocation At, int Units)> Holding(uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable) =>
        locations.Where(l => owner is not null && l.Character.Matches(owner))
            .Concat(locations.Where(l => owner is null || !l.Character.Matches(owner)))
            .DistinctBy(l => (l.Character.LocalContentId, l.Character.HomeWorldId, l.Retainer))
            .Select(at => (At: at, Units: Count(at, item, quality)))
            .Where(held => held.Units > 0 && listable(held.At.Character));

    /// <summary>
    /// Folds places listed by role into the line's entries, after any it already lists, without
    /// stock: a typed place holds exactly its target. An untyped source drains when the line also has an
    /// untyped destination (everything it held went there) and is a pool otherwise (it gave typed
    /// destinations their demand); an untyped destination is a pool. The role lists are cleared.
    /// </summary>
    internal static CourierLine ConvertRoles(CourierLine line)
    {
        var sources = line.LegacySources;
        var destinations = line.LegacyDestinations;
        var drain = sources.Any(place => place.Target is null) && destinations.Any(place => place.Target is null);
        var entries = line.Entries.ToBuilder();
        void Add(LegacyRoleCourierPlace place, int? min, int? max)
        {
            if (!entries.Any(known => known.Is(place.Character, place.Retainer)))
                entries.Add(new(place.Character with { }, place.Retainer, min, max));
        }
        foreach (var place in sources)
        {
            if (place.Target is { } target)
                Add(place, target, target);
            else if (drain)
                Add(place, 0, 0);
            else
                Add(place, null, null);
        }
        foreach (var place in destinations)
            Add(place, place.Target, place.Target);
        return line with { Entries = entries.ToImmutable(), Sources = default, Destinations = default, SourceSuggestionDismissed = false };
    }

    /// <summary>
    /// Converts a line in an older shape; any other line is returned as is. Places written before
    /// roles were listed apart take a role first: an added destination stays a destination with its value,
    /// a typed place below what it holds becomes a source with its value and above it a destination, and
    /// one never observed becomes a source; untyped places that only held the item are dropped, except
    /// that a line left with only typed destinations takes every untyped holder as an untyped source.
    /// Roles then fold into entries (<see cref="ConvertRoles"/>).
    /// </summary>
    public static CourierLine Resolve(CourierLine line, uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable)
    {
        if (!line.NeedsResolve)
            return line;
        var sources = line.LegacySources.ToBuilder();
        var destinations = line.LegacyDestinations.ToBuilder();
        bool Listed(OwnerScope who, ulong? retainer) => line.Lists(who, retainer) ||
            sources.Any(p => p.Is(who, retainer)) || destinations.Any(p => p.Is(who, retainer));
        foreach (var place in line.LegacyPlaces)
        {
            if (Listed(place.Character, place.Retainer))
                continue;
            if (place.Destination)
                destinations.Add(new(place.Character with { }, place.Retainer, place.After));
            else if (place.After is { } after)
            {
                var held = Observed(locations, place.Character, place.Retainer) is { } at ? Count(at, item, quality) : (int?)null;
                if (held is { } units && after > units)
                    destinations.Add(new(place.Character with { }, place.Retainer, after));
                else
                    sources.Add(new(place.Character with { }, place.Retainer, after));
            }
        }
        // Only typed destinations drew from untyped holders before; a blank destination moved nothing,
        // so holders join only when every destination is typed, as pools that give just its demand.
        if (line.Entries.IsEmpty && destinations.Count > 0 && sources.Count == 0 && destinations.All(place => place.Target is not null))
            foreach (var (at, _) in Holding(item, quality, owner, locations, listable))
                if (!Listed(at.Character, at.Retainer))
                    sources.Add(new(at.Character with { }, at.Retainer));
        return ConvertRoles(line with { Sources = sources.ToImmutable(), Destinations = destinations.ToImmutable(), Places = default });
    }

    /// <summary>
    /// Solves a line's moves against observed stock. Each place keeps what it holds inside its bounds:
    /// above Max it must give the excess, below Min it must be filled, and a pool (no bounds) supplies and
    /// absorbs whatever the others need. Mandatory surplus fills mandatory deficits first; deficits still
    /// open then draw on optional supply, pools before what bounded places hold above their Min; surplus
    /// still left then goes to pools before bounded places with room under their Max. Each step prefers
    /// places of the same character, then listed order. Surplus spreads over several pools by their
    /// physical room when <paramref name="stackSize"/> is known, the last observed pool taking the rest;
    /// otherwise the first pool takes it all. An unobserved place never gives and is taken to hold nothing
    /// so its moves surface (and are refused) on the line. What deficits still want is Short and what
    /// surplus is still held is Stuck; every solved move runs regardless. While the line lists a single
    /// place, the observed holder with the most units it does not list is offered as a pool, unless
    /// dismissed; the suggestion is solved against the line as listed and moves nothing.
    /// </summary>
    public static CourierAllocation Allocate(CourierLine line, uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable, int? stackSize = null)
    {
        line = Resolve(line, item, quality, owner, locations, listable);
        var solved = Solve(line.Entries, item, quality, locations, stackSize);
        var holding = Holding(item, quality, owner, locations, listable)
            .Where(held => !line.Lists(held.At.Character, held.At.Retainer))
            .ToArray();
        ImmutableArray<CourierSpot> holders =
            [.. holding.Select(held => new CourierSpot(held.At.Character, held.At.Retainer, held.Units, null, null, held.Units))];
        CourierSpot? suggestion = null;
        if (line.Entries.Length == 1 && !line.SuggestionDismissed &&
            holding.OrderByDescending(held => held.Units).Select(held => held.At).FirstOrDefault() is { } best)
            suggestion = Solve(line.Entries.Add(new(best.Character, best.Retainer)), item, quality, locations, stackSize).Places[^1];
        return solved with { Line = line, Holders = holders, Suggestion = suggestion };
    }

    private static CourierAllocation Solve(ImmutableArray<CourierPlace> entries, uint item, ItemQualityPolicy quality,
        IReadOnlyList<CourierLocation> locations, int? stackSize)
    {
        var n = entries.Length;
        var who = new OwnerScope[n];
        var has = new int?[n];
        var min = new int?[n];
        var max = new int?[n];
        var room = new long?[n];
        for (var i = 0; i < n; i++)
        {
            var place = entries[i];
            var at = Observed(locations, place.Character, place.Retainer);
            who[i] = at?.Character ?? place.Character;
            has[i] = at is null ? null : Count(at, item, quality);
            (min[i], max[i]) = CourierPlace.Normalize(place.Min, place.Max);
            room[i] = at is not null && stackSize is { } size and > 0 ? Room(at, item, quality, size) : null;
        }

        int Held(int i) => has[i] ?? 0;
        bool Pool(int i) => min[i] is null && max[i] is null;
        var give = new int[n];
        var want = new int[n];
        var spare = new int[n];
        var slack = new int[n];
        for (var i = 0; i < n; i++)
        {
            // An unobserved place never gives: its excess is unknown.
            give[i] = has[i] is { } held && max[i] is { } high ? Math.Max(0, held - high) : 0;
            want[i] = min[i] is { } low ? Math.Max(0, low - Held(i)) : 0;
            spare[i] = has[i] is { } stock ? Math.Max(0, stock - give[i] - (min[i] ?? 0)) : 0;
            slack[i] = max[i] is { } cap ? Math.Max(0, cap - (Held(i) + want[i])) : int.MaxValue;
        }

        var sent = new int[n];
        var received = new int[n];
        var flows = new List<CourierFlow>();
        void Move(int from, int to, int units)
        {
            if (units <= 0) return;
            sent[from] += units;
            received[to] += units;
            var last = flows.FindIndex(f => f.From == from && f.To == to);
            if (last >= 0)
                flows[last] = flows[last] with { Units = flows[last].Units + units };
            else
                flows.Add(new(from, to, units));
        }
        bool Apart(int a, int b) => a != b && (entries[a].Retainer != entries[b].Retainer || !who[a].Matches(who[b]));
        // Places that can pair with anchor: the same character's first, then in the order given.
        IEnumerable<int> Prefer(int anchor, IEnumerable<int> places) =>
            places.Where(place => Apart(place, anchor)).OrderBy(place => who[place].Matches(who[anchor]) ? 0 : 1);
        var all = Enumerable.Range(0, n).ToArray();
        var pools = all.Where(Pool).ToArray();
        var bounded = all.Where(i => !Pool(i)).ToArray();

        // 1. Mandatory surplus fills mandatory deficits.
        foreach (var taker in all.Where(i => want[i] > 0))
            foreach (var giver in Prefer(taker, all))
            {
                if (want[taker] == 0) break;
                var units = Math.Min(give[giver], want[taker]);
                give[giver] -= units;
                want[taker] -= units;
                Move(giver, taker, units);
            }

        // 2. Deficits still open draw on pools, then on what bounded places hold above their Min.
        foreach (var taker in all.Where(i => want[i] > 0))
            foreach (var giver in Prefer(taker, pools).Concat(Prefer(taker, bounded)))
            {
                if (want[taker] == 0) break;
                var units = Math.Min(spare[giver], want[taker]);
                spare[giver] -= units;
                want[taker] -= units;
                Move(giver, taker, units);
            }

        // 3. Surplus still held goes to pools, then to bounded places with room under their Max. With stack
        // sizes known, pools fill to their physical room in turn and the last observed one takes the rest
        // (so a full one surfaces on the line); without, the first pool takes everything.
        var observedPools = pools.Where(i => has[i] is not null).ToArray();
        var overflow = stackSize is > 0
            ? observedPools.Length > 0 ? observedPools[^1] : pools.DefaultIfEmpty(-1).Last()
            : observedPools.Length > 0 ? observedPools[0] : pools.DefaultIfEmpty(-1).First();
        var spill = stackSize is > 0 ? observedPools.Where(i => i != overflow).ToArray() : [];
        long PoolRoom(int i) => i == overflow ? long.MaxValue : room[i] is { } r ? Math.Max(0, r - received[i]) : 0;
        foreach (var giver in all.Where(i => give[i] > 0))
        {
            var takers = Prefer(giver, spill)
                .Concat(overflow >= 0 && Apart(overflow, giver) ? [overflow] : [])
                .Concat(Prefer(giver, bounded));
            foreach (var taker in takers)
            {
                if (give[giver] == 0) break;
                var capacity = Pool(taker) ? PoolRoom(taker) : slack[taker];
                var units = (int)Math.Min(give[giver], capacity);
                give[giver] -= units;
                if (!Pool(taker))
                    slack[taker] -= units;
                Move(giver, taker, units);
            }
        }

        var spots = ImmutableArray.CreateBuilder<CourierSpot>(n);
        for (var i = 0; i < n; i++)
            spots.Add(new(who[i], entries[i].Retainer, has[i], min[i], max[i], Held(i) - sent[i] + received[i]));
        return new(new CourierLine(), spots.MoveToImmutable(), [.. flows], Total(want), Total(give));

        static int Total(int[] units) => (int)Math.Min(int.MaxValue, units.Sum(unit => (long)unit));
    }
}
