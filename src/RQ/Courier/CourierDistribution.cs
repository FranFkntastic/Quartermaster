using System.Collections.Immutable;
using RQ.Domain;

namespace RQ.Courier;

/// <summary>
/// One place listed under a courier line. Has is the observed units of the line's item there
/// (null when the container was never observed); Typed is the stored target; End is where the
/// place lands once the line's moves are done.
/// </summary>
public sealed record CourierSpot(OwnerScope Character, ulong? Retainer, int? Has, int? Typed, bool Destination, int End)
{
    public int Delta => Has is null ? 0 : End - Has.Value;
}

/// <summary>Units moving from one listed place to another, by index into the allocation's places.</summary>
public readonly record struct CourierFlow(int From, int To, int Units);

/// <summary>What keeps a line from running as listed; a line with a problem moves nothing.</summary>
public enum CourierProblem
{
    None,
    NoSource,
    NoDestination,
    /// <summary>Every source with units sits on the character of every destination; the courier only moves between characters.</summary>
    SameCharacter,
    /// <summary>Typed destinations want more than the sources can give (<see cref="CourierAllocation.Short"/>).</summary>
    Short,
    /// <summary>Typed sources give more than any destination takes (<see cref="CourierAllocation.Stuck"/>).</summary>
    Unplaced,
}

/// <summary>
/// A line's solved moves. Places are its sources, then its destinations, in listed order. Line is the
/// line as solved (older lines converted). Holders are observed places holding the item that the line
/// does not list and the courier can use; Suggestion is the destination offered while the line has none.
/// </summary>
public sealed record CourierAllocation(
    CourierLine Line,
    ImmutableArray<CourierSpot> Places,
    ImmutableArray<CourierFlow> Flows,
    CourierProblem Problem = CourierProblem.None,
    int Short = 0,
    int Stuck = 0)
{
    public ImmutableArray<CourierSpot> Holders { get; init; } = [];
    public CourierSpot? Suggestion { get; init; }

    public int Total => Places.Sum(p => p.Has ?? 0);
    public int Moving => Flows.Sum(f => f.Units);

    /// <summary>Units the run moves: none while the line has a problem.</summary>
    public int Runs => Problem == CourierProblem.None ? Moving : 0;

    public int SourceCount => Places.Count(p => !p.Destination);
}

/// <summary>
/// Turns a courier line's sources and destinations into moves. Solving is shared by the planner and
/// the plan table so the numbers the user sees are the numbers the run moves.
/// </summary>
public static class CourierDistribution
{
    private static int Count(CourierLocation at, uint item, ItemQualityPolicy quality) => at.Inventory.Stock
        .Where(s => s.ItemId == item && (quality == ItemQualityPolicy.Any || s.Hq == (quality == ItemQualityPolicy.HqOnly)))
        .Sum(s => s.Units);

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
    /// Converts a line written before sources and destinations were listed apart; any other line is
    /// returned as is. An added destination stays a destination with its value. A typed place below
    /// what it holds becomes a source with its value, above it a destination; one never observed
    /// becomes a source. Untyped places that only held the item are dropped, except that a line left
    /// with destinations and no source takes every untyped holder as an untyped source.
    /// </summary>
    public static CourierLine Resolve(CourierLine line, uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable)
    {
        if (!line.HasLegacyPlaces)
            return line;
        var sources = line.Sources.ToBuilder();
        var destinations = line.Destinations.ToBuilder();
        bool Listed(OwnerScope who, ulong? retainer) =>
            sources.Any(p => p.Is(who, retainer)) || destinations.Any(p => p.Is(who, retainer));
        foreach (var place in line.Places)
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
        if (destinations.Count > 0 && sources.Count == 0)
            foreach (var (at, _) in Holding(item, quality, owner, locations, listable))
                if (!Listed(at.Character, at.Retainer))
                    sources.Add(new(at.Character with { }, at.Retainer));
        return line with { Sources = sources.ToImmutable(), Destinations = destinations.ToImmutable(), Places = default };
    }

    /// <summary>
    /// Solves a line's moves against observed stock. A typed source ends with its target, giving what
    /// it holds above it; a typed destination ends with its target. Typed destinations fill first, in
    /// order, from typed sources and then from untyped sources in listed order. What typed sources still
    /// give, and everything untyped sources hold when the line has an untyped destination, goes to the
    /// first untyped destination. Nothing moves between two containers of one character; an unobserved
    /// source gives nothing and an unobserved destination is taken to hold nothing so its moves surface
    /// (and are refused) on the line. Units typed sources give that nothing takes count as Stuck, units
    /// typed destinations want that nothing gives as Short; either is a problem and the line moves nothing.
    /// While the line has no destination, the observed holder with the most units that is not a source,
    /// and not only on the sources' characters, is offered as a suggestion, unless dismissed.
    /// </summary>
    public static CourierAllocation Allocate(CourierLine line, uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable)
    {
        line = Resolve(line, item, quality, owner, locations, listable);
        var solved = Solve(line.Sources, line.Destinations, item, quality, locations);
        var holding = Holding(item, quality, owner, locations, listable)
            .Where(held => !line.Lists(held.At.Character, held.At.Retainer))
            .ToArray();
        ImmutableArray<CourierSpot> holders =
            [.. holding.Select(held => new CourierSpot(held.At.Character, held.At.Retainer, held.Units, null, false, held.Units))];
        CourierSpot? suggestion = null;
        if (line.Destinations.IsEmpty && !line.SuggestionDismissed)
        {
            var best = holding
                .Where(held => line.Sources.IsEmpty || line.Sources.Any(source => !source.Character.Matches(held.At.Character)))
                .OrderByDescending(held => held.Units)
                .Select(held => held.At)
                .FirstOrDefault();
            if (best is not null)
            {
                var offered = Solve(line.Sources, [new(best.Character, best.Retainer)], item, quality, locations);
                suggestion = offered.Places[^1];
            }
        }
        return solved with { Line = line, Holders = holders, Suggestion = suggestion };
    }

    private static CourierAllocation Solve(ImmutableArray<CourierPlace> sources, ImmutableArray<CourierPlace> destinations,
        uint item, ItemQualityPolicy quality, IReadOnlyList<CourierLocation> locations)
    {
        var listed = new List<(OwnerScope Who, ulong? Retainer, int? Has, int? Typed, bool Destination)>(sources.Length + destinations.Length);
        foreach (var (places, destination) in new[] { (sources, false), (destinations, true) })
            foreach (var place in places)
            {
                var at = Observed(locations, place.Character, place.Retainer);
                listed.Add((at?.Character ?? place.Character, place.Retainer, at is null ? null : Count(at, item, quality), place.Target, destination));
            }

        var n = listed.Count;
        var sent = new int[n];
        var received = new int[n];
        var give = new int[n];
        var want = new int[n];
        var flows = new List<CourierFlow>();
        bool Source(int i) => !listed[i].Destination;
        int Held(int i) => listed[i].Has ?? 0;
        for (var i = 0; i < n; i++)
            if (listed[i].Typed is { } typed)
            {
                if (Source(i) && listed[i].Has is { } has)
                    give[i] = Math.Max(0, has - typed);
                else if (!Source(i))
                    want[i] = Math.Max(0, typed - Held(i));
            }
        int Spare(int i) => listed[i].Typed is null ? Held(i) - sent[i] : give[i];
        void Move(int from, int to, int units)
        {
            if (units <= 0) return;
            sent[from] += units;
            received[to] += units;
            if (listed[from].Typed is not null)
                give[from] -= units;
            if (listed[to].Typed is not null)
                want[to] -= units;
            var last = flows.FindIndex(f => f.From == from && f.To == to);
            if (last >= 0)
                flows[last] = flows[last] with { Units = flows[last].Units + units };
            else
                flows.Add(new(from, to, units));
        }
        bool Apart(int a, int b) => !listed[a].Who.Matches(listed[b].Who);

        // Typed destinations fill first, from typed sources, then from untyped sources, in listed order.
        for (var taker = 0; taker < n; taker++)
        {
            if (Source(taker) || want[taker] <= 0) continue;
            foreach (var typed in new[] { true, false })
                for (var giver = 0; giver < n && want[taker] > 0; giver++)
                    if (Source(giver) && (listed[giver].Typed is not null) == typed && Apart(giver, taker))
                        Move(giver, taker, Math.Min(Spare(giver), want[taker]));
        }
        // The rest goes to the first untyped destination: what typed sources give, and with one listed,
        // everything untyped sources hold.
        for (var giver = 0; giver < n; giver++)
        {
            if (!Source(giver) || Spare(giver) <= 0) continue;
            var to = Enumerable.Range(0, n).FirstOrDefault(t => !Source(t) && listed[t].Typed is null && Apart(giver, t), -1);
            if (to >= 0)
                Move(giver, to, Spare(giver));
        }

        var stuck = Enumerable.Range(0, n).Where(i => Source(i) && listed[i].Typed is not null).Sum(i => give[i]);
        var shortfall = Enumerable.Range(0, n).Where(i => !Source(i)).Sum(i => want[i]);
        // Units are due to move, yet every source shares a character with every destination.
        var due = stuck > 0 || shortfall > 0 ||
                  Enumerable.Range(0, n).Any(t => !Source(t) && listed[t].Typed is null) &&
                  Enumerable.Range(0, n).Any(g => Source(g) && listed[g].Typed is null && Held(g) > 0);
        var problem = sources.IsEmpty ? CourierProblem.NoSource
            : destinations.IsEmpty ? CourierProblem.NoDestination
            : flows.Count == 0 && due && Enumerable.Range(0, n).Any(g => Source(g) && Spare(g) > 0) &&
              !Enumerable.Range(0, n).Any(g => Source(g) && Spare(g) > 0 && Enumerable.Range(0, n).Any(t => !Source(t) && Apart(g, t)))
                ? CourierProblem.SameCharacter
            : shortfall > 0 ? CourierProblem.Short
            : stuck > 0 ? CourierProblem.Unplaced
            : CourierProblem.None;

        var spots = ImmutableArray.CreateBuilder<CourierSpot>(n);
        for (var i = 0; i < n; i++)
        {
            var (who, retainer, has, typed, destination) = listed[i];
            var end = has is null && Source(i) ? typed ?? 0 : Held(i) - sent[i] + received[i];
            spots.Add(new(who, retainer, has, typed, destination, end));
        }
        return new(new CourierLine(), spots.MoveToImmutable(), [.. flows], problem, shortfall, stuck);
    }
}
