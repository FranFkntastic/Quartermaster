using System.Collections.Immutable;
using RQ.Domain;

namespace RQ.Courier;

/// <summary>
/// One place listed under a courier line. Has is the observed units of the line's item there
/// (null when the container was never observed); Typed is the stored After value; End is where the
/// place lands once the line's moves are done.
/// </summary>
public sealed record CourierSpot(OwnerScope Character, ulong? Retainer, int? Has, int? Typed, bool Destination, int End)
{
    public int Delta => Has is null ? 0 : End - Has.Value;
}

/// <summary>Units moving from one listed place to another, by index into the allocation's places.</summary>
public readonly record struct CourierFlow(int From, int To, int Units);

/// <summary>A line's solved moves. Stuck counts units a typed place gives up that have nowhere to go.</summary>
public sealed record CourierAllocation(ImmutableArray<CourierSpot> Places, ImmutableArray<CourierFlow> Flows, int Stuck = 0)
{
    public int Total => Places.Sum(p => p.Has ?? 0);
    public int Moving => Flows.Sum(f => f.Units);
}

/// <summary>
/// Turns a courier line's places into moves. Listing and solving are shared by the planner and the
/// plan table so the numbers the user sees are the numbers the run moves.
/// </summary>
public static class CourierDistribution
{
    /// <summary>
    /// Lists a line's places and solves its moves against observed stock.
    /// Places: every observed container holding the item on a <paramref name="listable"/> character
    /// (the plan owner's first, then in <paramref name="locations"/> order), stored typed places, then
    /// the added destinations in the order they were added.
    /// A typed place below what it has gives up the difference; above, it wants the difference. Given-up
    /// units fill typed places that want more, in listed order, then go to the first empty destination,
    /// else to the first untyped place on another character already holding the item. A typed place
    /// that still wants more draws from untyped places holding the item. Nothing moves between two
    /// containers of one character; units with nowhere to go stay where they are and count as Stuck.
    /// </summary>
    public static CourierAllocation Allocate(CourierLine line, uint item, ItemQualityPolicy quality, OwnerScope? owner,
        IReadOnlyList<CourierLocation> locations, Func<OwnerScope, bool> listable)
    {
        int Count(CourierLocation at) => at.Inventory.Stock
            .Where(s => s.ItemId == item && (quality == ItemQualityPolicy.Any || s.Hq == (quality == ItemQualityPolicy.HqOnly)))
            .Sum(s => s.Units);
        CourierLocation? Observed(OwnerScope who, ulong? retainer) =>
            locations.FirstOrDefault(l => l.Retainer == retainer && l.Character.Matches(who));

        var listed = new List<(OwnerScope Who, ulong? Retainer, int? Has, int? Typed, bool Destination)>();
        var ordered = locations.Where(l => owner is not null && l.Character.Matches(owner))
            .Concat(locations.Where(l => owner is null || !l.Character.Matches(owner)))
            .DistinctBy(l => (l.Character.LocalContentId, l.Character.HomeWorldId, l.Retainer));
        foreach (var at in ordered)
        {
            if (line.Places.Any(p => p.Destination && p.Is(at.Character, at.Retainer)))
                continue;
            var typed = line.Places.FirstOrDefault(p => !p.Destination && p.Is(at.Character, at.Retainer))?.After;
            var has = Count(at);
            if (typed is not null || has > 0 && listable(at.Character))
                listed.Add((at.Character, at.Retainer, has, typed, false));
        }
        foreach (var place in line.Places.Where(p => !p.Destination && p.After is not null && Observed(p.Character, p.Retainer) is null))
            listed.Add((place.Character, place.Retainer, null, place.After, false));
        foreach (var place in line.Places.Where(p => p.Destination))
        {
            var at = Observed(place.Character, place.Retainer);
            listed.Add((at?.Character ?? place.Character, place.Retainer, at is null ? null : Count(at), place.After, true));
        }

        var n = listed.Count;
        var sent = new int[n];
        var received = new int[n];
        var flows = new List<CourierFlow>();
        // An unobserved typed place cannot be reasoned about and takes no part; an unobserved empty
        // destination is taken to hold nothing so its moves surface (and are refused) on the line.
        bool Active(int i) => listed[i].Has is not null || listed[i].Typed is null;
        int Held(int i) => listed[i].Has ?? 0;
        var give = new int[n];
        var want = new int[n];
        for (var i = 0; i < n; i++)
            if (Active(i) && listed[i].Typed is { } typed)
            {
                give[i] = Math.Max(0, Held(i) - typed);
                want[i] = Math.Max(0, typed - Held(i));
            }
        void Move(int from, int to, int units)
        {
            if (units <= 0) return;
            sent[from] += units;
            received[to] += units;
            give[from] = Math.Max(0, give[from] - units);
            want[to] -= units;
            var last = flows.FindIndex(f => f.From == from && f.To == to);
            if (last >= 0)
                flows[last] = flows[last] with { Units = flows[last].Units + units };
            else
                flows.Add(new(from, to, units));
        }
        bool Apart(int a, int b) => !listed[a].Who.Matches(listed[b].Who);

        for (var taker = 0; taker < n; taker++)
        {
            for (var giver = 0; giver < n && want[taker] > 0; giver++)
                if (give[giver] > 0 && Apart(giver, taker))
                    Move(giver, taker, Math.Min(give[giver], want[taker]));
            for (var spare = 0; spare < n && want[taker] > 0; spare++)
                if (spare != taker && listed[spare].Typed is null && !listed[spare].Destination && listed[spare].Has is { } held &&
                    Apart(spare, taker))
                    Move(spare, taker, Math.Min(held - sent[spare], want[taker]));
        }
        bool Open(int to) => listed[to].Typed is null && listed[to].Has is not null;
        for (var giver = 0; giver < n; giver++)
        {
            if (give[giver] <= 0) continue;
            var to = Enumerable.Range(0, n).FirstOrDefault(t => listed[t].Destination && listed[t].Typed is null && Apart(giver, t), -1);
            if (to < 0)
                to = Enumerable.Range(0, n).FirstOrDefault(t => t != giver && !listed[t].Destination && Open(t) && Apart(giver, t), -1);
            if (to >= 0)
                Move(giver, to, give[giver]);
        }
        var stuck = Enumerable.Range(0, n).Where(Active).Sum(i => give[i]);

        var spots = ImmutableArray.CreateBuilder<CourierSpot>(n);
        for (var i = 0; i < n; i++)
        {
            var (who, retainer, has, typed, destination) = listed[i];
            var end = Active(i) ? Held(i) - sent[i] + received[i] : typed ?? 0;
            spots.Add(new(who, retainer, has, typed, destination, end));
        }
        return new(spots.MoveToImmutable(), [.. flows], stuck);
    }
}
