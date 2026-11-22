using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Courier;

/// <summary>
/// One container of one character listed on a courier line: its bags (null Retainer) or one retainer.
/// Target is the units it should hold once the run is done; null leaves it to the line's other places
/// (see <see cref="CourierDistribution"/>).
/// </summary>
public sealed record CourierPlace(OwnerScope Character, ulong? Retainer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Target = null)
{
    internal bool Is(OwnerScope character, ulong? retainer) => Retainer == retainer && Character.Matches(character);
}

/// <summary>
/// A place as written before lines listed their sources and destinations apart: After is what it
/// should hold after the run, Destination marks a place the user added to receive the item. Read from
/// older state and converted by <see cref="CourierDistribution.Resolve"/>.
/// </summary>
public sealed record LegacyCourierPlace(OwnerScope Character, ulong? Retainer, int? After = null, bool Destination = false)
{
    internal bool Is(OwnerScope character, ulong? retainer) => Retainer == retainer && Character.Matches(character);
}

/// <summary>
/// A Courier plan line: one item, the places it is taken from and the places it is brought to, in
/// order. Places not listed are never touched and a place is never both. Everything that moves is
/// derived from these places and observed stock (see <see cref="CourierDistribution"/>).
/// <see cref="Places"/> and the members after it are older shapes: places written before roles were
/// listed apart (kept until stock is known, then converted by <see cref="CourierDistribution.Resolve"/>),
/// and the per-route shape folded into places on load (<c>StowagePlanMigration.CollapseCourierLines</c>).
/// </summary>
public sealed record CourierLine
{
    public ImmutableArray<CourierPlace> Sources { get; init; } = [];

    public ImmutableArray<CourierPlace> Destinations { get; init; } = [];

    /// <summary>Whether the user dismissed the destination this line would otherwise suggest.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SuggestionDismissed { get; init; }

    /// <summary>Whether the user dismissed the source this line would otherwise suggest.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SourceSuggestionDismissed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ImmutableArray<LegacyCourierPlace> Places { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public OwnerScope? Character { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ulong? SourceRetainerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public OwnerScope? SourceCharacter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool KeepInSource { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ulong? RetainerId { get; init; }

    /// <summary>Whether this line still has the per-route shape and awaits migration.</summary>
    [JsonIgnore]
    internal bool IsLegacy => Character is not null;

    /// <summary>Whether this line still holds places written before roles were listed apart.</summary>
    [JsonIgnore]
    internal bool HasLegacyPlaces => !Places.IsDefaultOrEmpty;

    [JsonIgnore]
    internal ImmutableArray<LegacyCourierPlace> LegacyPlaces => Places.IsDefault ? [] : Places;

    internal bool Lists(OwnerScope who, ulong? retainer) =>
        Sources.Any(place => place.Is(who, retainer)) || Destinations.Any(place => place.Is(who, retainer));

    private CourierLine Without(OwnerScope who, ulong? retainer, out CourierPlace? removed)
    {
        removed = Sources.FirstOrDefault(place => place.Is(who, retainer)) ?? Destinations.FirstOrDefault(place => place.Is(who, retainer));
        return removed is null ? this : this with
        {
            Sources = [.. Sources.Where(place => !place.Is(who, retainer))],
            Destinations = [.. Destinations.Where(place => !place.Is(who, retainer))],
        };
    }

    /// <summary>Lists a place as the last source; a destination there becomes a source with its target.</summary>
    internal CourierLine WithSource(OwnerScope who, ulong? retainer)
    {
        if (Sources.Any(place => place.Is(who, retainer)))
            return this;
        var line = Without(who, retainer, out var removed);
        return line with { Sources = line.Sources.Add(new(who with { }, retainer, removed?.Target)) };
    }

    /// <summary>Lists a place as the last destination; a source there becomes a destination with its target.</summary>
    internal CourierLine WithDestination(OwnerScope who, ulong? retainer)
    {
        if (Destinations.Any(place => place.Is(who, retainer)))
            return this;
        var line = Without(who, retainer, out var removed);
        return line with { Destinations = line.Destinations.Add(new(who with { }, retainer, removed?.Target)) };
    }

    /// <summary>Removes a place from the line, whichever role it has.</summary>
    internal CourierLine Without(OwnerScope who, ulong? retainer) => Without(who, retainer, out _);

    /// <summary>Sets what a listed place should hold after the run; null leaves it to the line's other places.</summary>
    internal CourierLine WithTarget(OwnerScope who, ulong? retainer, int? target)
    {
        static ImmutableArray<CourierPlace> Set(ImmutableArray<CourierPlace> places, OwnerScope who, ulong? retainer, int? target)
        {
            for (var i = 0; i < places.Length; i++)
                if (places[i].Is(who, retainer))
                    return places.SetItem(i, places[i] with { Target = target });
            return places;
        }
        return this with { Sources = Set(Sources, who, retainer, target), Destinations = Set(Destinations, who, retainer, target) };
    }

    public bool Equals(CourierLine? other) =>
        other is not null && Sources.SequenceEqual(other.Sources) && Destinations.SequenceEqual(other.Destinations) &&
        SuggestionDismissed == other.SuggestionDismissed && SourceSuggestionDismissed == other.SourceSuggestionDismissed &&
        LegacyPlaces.SequenceEqual(other.LegacyPlaces) &&
        Equals(Character, other.Character) && SourceRetainerId == other.SourceRetainerId && Equals(SourceCharacter, other.SourceCharacter) &&
        KeepInSource == other.KeepInSource && RetainerId == other.RetainerId;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var place in Sources)
            hash.Add(place);
        foreach (var place in Destinations)
            hash.Add(place);
        foreach (var place in LegacyPlaces)
            hash.Add(place);
        hash.Add(SuggestionDismissed);
        hash.Add(SourceSuggestionDismissed);
        hash.Add(Character);
        hash.Add(SourceRetainerId);
        return hash.ToHashCode();
    }
}
public readonly record struct WardPoint(float X,float Y,float Z)
{
    internal System.Numerics.Vector3 Position => new(X,Y,Z);
}
public sealed record MeetingBell(uint Territory, uint World, uint ObjectId, float X, float Y, float Z, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ward = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Subdivision = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Shard = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WardPoint[]? Trail = null)
{
    internal int ApproachSeconds => Ward.HasValue ? 300 : 150;
    /// <summary>
    /// A rendezvous at a city's main aetheryte rather than a summoning bell (ObjectId 0): used where a
    /// side cannot meet the carrier on its own home world, such as the cross-region Ravana — Ul'dah bridge.
    /// No retainer can be reached there, so only carried stock and gil change hands.
    /// </summary>
    [JsonIgnore]
    internal bool IsPlaza => ObjectId == 0;
    internal bool HasRoute => Ward!=null && Shard is >0 && Trail is {Length:>0};
    public bool SamePlace(MeetingBell other) => this with { Label = "",Shard=null,Trail=null } == other with { Label = "",Shard=null,Trail=null };
}
public sealed record CourierSettings
{
    public OwnerScope? Carrier { get; init; }
    public OwnerScope? PairedOwner { get; init; }
    public ImmutableArray<OwnerScope> PairedOwners { get; init; } = [];
    internal IEnumerable<OwnerScope> Owners => PairedOwners.Concat(PairedOwner is {} one ? [one] : []).DistinctBy(x=>(x.LocalContentId,x.HomeWorldId));
    internal bool IsPaired(OwnerScope who) => Owners.Any(o=>CourierPlanner.Exact(o,who));
    public ImmutableArray<OwnerScope> LocalRoster { get; init; } = [];
    public bool CarrierOptIn { get; init; }
    public bool ReturnOwner { get; init; } = true;
    public bool ReturnCarrier { get; init; } = true;
    public ImmutableDictionary<ulong, MeetingBell> Bells { get; init; } = ImmutableDictionary<ulong, MeetingBell>.Empty;
}
public sealed record BagStock(uint ItemId, bool Hq, int Units);
/// <summary>
/// A character's bags: its stacks, free slots and carried gil. Gil is never a stack: it takes no slot and
/// is counted under (<see cref="CourierPlanner.GilItemId"/>, NQ), the same key trades verify it under.
/// </summary>
public sealed record InventoryView(OwnerScope Character, int FreeSlots, ImmutableArray<BagStock> Stock,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Gil = 0)
{
    public int Count(uint item, bool hq) => item == CourierPlanner.GilItemId && !hq ? Gil
        : Stock.Where(x => x.ItemId == item && x.Hq == hq).Sum(x => x.Units);
}
public sealed record CourierLocation(OwnerScope Character, ulong? Retainer, InventoryView Inventory);
public sealed record Shipment(Guid Line, uint Item, string Name, bool Hq, int Units, int StackSize,
    ulong? From, ulong? To, OwnerScope Source, OwnerScope Destination)
{
    /// <summary>Carried gil: one shipment of up to the gil cap, taking no bag slot on any side and never a retainer.</summary>
    [JsonIgnore]
    internal bool IsGil => Item == CourierPlanner.GilItemId;
}
/// <summary>
/// One carrier round from Source to Destination. A Local trip moves between containers of one character:
/// the owner withdraws and deposits at its own bell with no carrier and no trade. A trip whose Source is the
/// run's carrier has no pickup leg: the carrier's own units are claimed into custody and delivered (its Pickup
/// is its Dropoff).
/// </summary>
public sealed record CourierTrip(OwnerScope Source, OwnerScope Destination, MeetingBell Pickup, MeetingBell Dropoff,
    ImmutableArray<Shipment> Manifest)
{
    internal bool Local => CourierPlanner.Exact(Source, Destination);
}
public sealed record Refusal(Guid Line, string Name, string Code, string? Source=null, string? Destination=null);
/// <summary>
/// Destination-side consolidation for one line: once its units are delivered, gather the item's
/// stray stacks on Destination's other retainers into Retainer (the line's rank 1).
/// </summary>
public sealed record CourierConsolidation(OwnerScope Destination, Guid Line, uint Item, string Name,
    ItemQualityPolicy Quality, ulong Retainer, int StackSize);
/// <param name="ReturnDestinationHome">After the last deposit, a destination left off its home world (at a bridge) returns home.</param>
/// <param name="RequestId">The IPC request that submitted this plan, its idempotency key; null for plans started in the UI.</param>
public sealed record CourierPlan(Guid PlanId, long Revision, ImmutableArray<CourierTrip> Trips, ImmutableArray<Refusal> Refusals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ReturnDestinationHome = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null)
{
    /// <summary>Absent in journals written before destination consolidation, which read as none.</summary>
    public ImmutableArray<CourierConsolidation> Consolidations { get; init; } = [];

    /// <summary>
    /// The canonical payload hash of the IPC request behind <see cref="RequestId"/>; a later request under the same id
    /// with another payload is a conflict. Absent for UI plans and in journals written before it was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestHash { get; init; }
}

public static class CourierPlanner
{
    /// <summary>The trade key carried gil is verified under (Franthropy's <c>TradePlanner.GilItemId</c>).</summary>
    public const uint GilItemId = Franthropy.Dalamud.Automation.Trades.TradePlanner.GilItemId;
    /// <summary>The most gil a character can carry.</summary>
    public const int GilCap = 999_999_999;

    internal static bool Exact(OwnerScope? a, OwnerScope? b) => a is { HasStableIdentity: true }
        && b is { HasStableIdentity: true } && a.Matches(b) && !string.IsNullOrWhiteSpace(a.CharacterName)
        && string.Equals(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FFXIV DC Travel allows visiting any world in the carrier's own region or OCE (Materia, region 4).
    /// JP (1) and EU (3) are unreachable from an NA (2) carrier. Zero means region unknown (do not block).
    /// </summary>
    internal static bool CanReachRegion(uint carrierRegion, uint targetRegion) =>
        carrierRegion == 0 || targetRegion == 0 || targetRegion == carrierRegion || targetRegion == 4;

    /// <summary>
    /// Whether a character's stock may be listed on a courier line without the user adding it:
    /// never the carrier, and never a character whose region the carrier cannot reach.
    /// </summary>
    internal static bool Listable(OwnerScope who, CourierSettings settings, Func<uint, uint>? worldRegion)
    {
        if (Exact(who, settings.Carrier))
            return false;
        var carrierRegion = CarrierRegion(settings, worldRegion);
        return carrierRegion == 0 || CanReachRegion(carrierRegion, worldRegion!(who.HomeWorldId ?? 0));
    }

    private static uint CarrierRegion(CourierSettings settings, Func<uint, uint>? worldRegion) =>
        worldRegion != null && settings.Carrier is { HasStableIdentity: true } ? worldRegion(settings.Carrier.HomeWorldId ?? 0) : 0u;

    // Owner locations must belong to the authenticated service-account roster; omit unknown containers.
    // Each line's moves come from its distribution (see CourierDistribution); the first refusal of any
    // of a line's moves refuses the whole line, so a line either runs completely or not at all.
    public static CourierPlan Build(StowagePlan plan, IEnumerable<TargetPlanItem> rules,
        IReadOnlyList<CourierLocation> locations, InventoryView? carrier, CourierSettings settings,
        Func<uint, ItemMetadata> metadata, int? testSlotCap = null, Func<uint, uint>? worldRegion = null)
    {
        var trips = ImmutableArray.CreateBuilder<CourierTrip>();
        var refusals = ImmutableArray.CreateBuilder<Refusal>();
        var uniqueDestinations = new HashSet<(ulong?, uint)>();
        var stock = locations.ToDictionary(x => (x.Character.LocalContentId, x.Retainer), x => x.Inventory.Stock.ToList());
        var space = locations.ToDictionary(x => (x.Character.LocalContentId, x.Retainer), x => x.Inventory.FreeSlots);
        var groups = new List<(OwnerScope Src, OwnerScope Dst, List<Shipment> Stacks)>();
        var carrierRegion = CarrierRegion(settings, worldRegion);
        bool Reachable(OwnerScope who) => carrierRegion == 0 || CanReachRegion(carrierRegion, worldRegion!(who.HomeWorldId ?? 0));
        CourierLocation? At(OwnerScope who, ulong? ret) => locations.FirstOrDefault(x =>
            Exact(x.Character, who) && Exact(x.Inventory.Character, who) && x.Retainer == ret);
        List<Shipment> Group(OwnerScope src, OwnerScope dst)
        {
            foreach (var group in groups)
                if (group.Src.Matches(src) && group.Dst.Matches(dst))
                    return group.Stacks;
            groups.Add((src, dst, []));
            return groups[^1].Stacks;
        }

        foreach (var r in rules.Where(r => r.Enabled && r.StowagePlanId == plan.Id && r.Courier is { IsLegacy: false }))
        {
            var allocation = CourierDistribution.Allocate(r.Courier!, r.ItemId, r.Quality, plan.Owner, locations,
                who => Listable(who, settings, worldRegion));
            // A line that cannot reach its listed end states moves nothing; the plan table names why.
            if (allocation.Problem != CourierProblem.None)
                continue;
            var meta = metadata(r.ItemId);
            string? code = null, srcName = null, dstName = null;
            if (allocation.Places.FirstOrDefault(p => p.Has == null && p.Typed != null) is { } unseen)
            {
                code = unseen.Destination ? "DestinationUnknown" : "SourceUnknown";
                if (unseen.Destination) dstName = unseen.Character.CharacterName;
                else srcName = unseen.Character.CharacterName;
            }
            var lineStacks = new List<(List<Shipment> Group, Shipment Stack)>();
            var lineUnique = new List<(ulong?, uint)>();
            var saved = new Dictionary<(ulong?, ulong?), (List<BagStock> Stock, int Space)>();
            void Save((ulong?, ulong?) key)
            {
                if (!saved.ContainsKey(key))
                    saved[key] = ([.. stock[key]], space[key]);
            }
            foreach (var flow in allocation.Flows)
            {
                if (code != null) break;
                var from = allocation.Places[flow.From];
                var to = allocation.Places[flow.To];
                var src = from.Character;
                var dst = to.Character;
                // Between containers of one character the owner withdraws and deposits itself: nothing is traded.
                var local = Exact(src, dst);
                (srcName, dstName) = (src.CharacterName, dst.CharacterName);
                var pickup = settings.Bells.GetValueOrDefault(src.LocalContentId ?? 0);
                var drop = settings.Bells.GetValueOrDefault(dst.LocalContentId ?? 0);
                // A local move involves no carrier: it needs none chosen, running or able to reach the character.
                code = !local && settings.Carrier is not { HasStableIdentity: true } ? "CarrierNotConfigured"
                    : !local && !Exact(carrier?.Character, settings.Carrier) ? "CarrierUnreachable"
                    : !settings.LocalRoster.Any(o => Exact(o, src)) || !settings.LocalRoster.Any(o => Exact(o, dst)) ? "OutsideClientRoster"
                    : !local && (!Reachable(src) || !Reachable(dst)) ? "RegionUnreachable"
                    : At(src, null) == null || At(dst, null) == null || !local && src.Matches(dst) || Exact(src, settings.Carrier) || Exact(dst, settings.Carrier) ? "CharacterNotKnown"
                    : pickup?.World != src.HomeWorldId || drop?.World != dst.HomeWorldId || pickup == null || drop == null ? "MeetingBellMissing"
                    : !local && meta.IsTradable != true ? "ItemNotTradeable"
                    : At(src, from.Retainer) == null ? "SourceUnknown" : At(dst, to.Retainer) == null ? "DestinationUnknown"
                    : meta.MaxStackSize is not (> 0 and <= int.MaxValue) || meta.IsUnique == null ? "ItemMetadataUnknown" : null;
                if (code != null) break;
                if (!local && meta.IsUnique == true && (flow.Units != 1 || uniqueDestinations.Contains((dst.LocalContentId, r.ItemId)) || lineUnique.Contains((dst.LocalContentId, r.ItemId))
                    || carrier!.Stock.Any(s => s.ItemId == r.ItemId && s.Units > 0)
                    || locations.Where(l => Exact(l.Character, dst)).Any(l => stock[(dst.LocalContentId, l.Retainer)].Any(s => s.ItemId == r.ItemId && s.Units > 0))))
                { code = "UniqueConflict"; break; }
                var size = (int)meta.MaxStackSize!.Value;
                var sourceKey = (src.LocalContentId, from.Retainer);
                var destKey = (dst.LocalContentId, to.Retainer);
                Save(sourceKey);
                Save(destKey);
                var group = Group(src, dst);
                var remaining = flow.Units;
                foreach (var hq in new[] { false, true })
                {
                    if (remaining == 0 || (r.Quality != ItemQualityPolicy.Any && hq != (r.Quality == ItemQualityPolicy.HqOnly))) continue;
                    var inventory = stock[sourceKey];
                    var supply = inventory.Where(s => s.ItemId == r.ItemId && s.Hq == hq).Sum(s => s.Units);
                    var units = Math.Min(supply, remaining);
                    if (units == 0) continue;
                    var dest = stock[destKey];
                    var partial = dest.Where(s => s.ItemId == r.ItemId && s.Hq == hq && s.Units > 0).Sum(s => (size - s.Units % size) % size);
                    if (partial + (long)Math.Max(0, space[destKey]) * size < units) { code = "DestinationFull"; break; }
                    space[destKey] -= (int)((Math.Max(0L, (long)units - partial) + size - 1) / size);
                    // Preserve physical stack boundaries when consuming partial-stack room.
                    var add = units;
                    for (var i = 0; i < dest.Count && add > 0; i++)
                        if (dest[i].ItemId == r.ItemId && dest[i].Hq == hq && dest[i].Units > 0)
                        {
                            var n = Math.Min(add, (size - dest[i].Units % size) % size);
                            dest[i] = dest[i] with { Units = dest[i].Units + n }; add -= n;
                        }
                    if (add > 0) dest.Add(new(r.ItemId, hq, add));
                    for (var slice = units; slice > 0;)
                    {
                        var n = Math.Min(size, slice);
                        lineStacks.Add((group, new(r.Id, r.ItemId, r.ItemName, hq, n, size, from.Retainer, to.Retainer, src, dst))); slice -= n;
                    }
                    inventory.RemoveAll(s => s.ItemId == r.ItemId && s.Hq == hq);
                    inventory.Add(new(r.ItemId, hq, supply - units));
                    remaining -= units;
                }
                if (code == null && remaining > 0) code = "SourceShort";
                if (!local && meta.IsUnique == true) lineUnique.Add((dst.LocalContentId, r.ItemId));
            }
            if (code != null)
            {
                refusals.Add(new(r.Id, r.ItemName, code, srcName, dstName));
                foreach (var (key, (kept, free)) in saved)
                {
                    stock[key] = kept;
                    space[key] = free;
                }
                continue;
            }
            foreach (var (group, stack) in lineStacks)
                group.Add(stack);
            uniqueDestinations.UnionWith(lineUnique);
        }

        // Destination bag slots do not shorten a trip: the conductor splits the hand-in into batches that
        // each fit and are stowed before the next, which beats an extra round trip. It still needs one slot.
        // Stacks already in the source's bags take no source slot; only retainer-sourced ones are retrieved into it.
        int CarrierCap(OwnerScope dst) => (At(dst, null)?.Inventory.FreeSlots ?? 0) <= 0 ? 0 : Math.Min(testSlotCap ?? int.MaxValue, carrier?.FreeSlots ?? 0);
        int SourceCap(OwnerScope src) => At(src, null)?.Inventory.FreeSlots ?? 0;
        // A same-character group has no carrier: retainer stacks bound for the bags keep their slots, and
        // stacks passing from one retainer to another need one free slot, which they hand back before
        // any bag-bound stack is retrieved (they run first).
        static bool Stays(Shipment s) => s.From != null && s.To == null;
        static bool Passes(Shipment s) => s.From != null && s.To != null;
        foreach (var (src, dst, stacks) in groups)
        {
            var carrierCap = CarrierCap(dst);
            var sourceCap = SourceCap(src);
            var full = Exact(src, dst)
                ? stacks.Count(Stays) > sourceCap || stacks.Any(Passes) && sourceCap <= 0
                    ? stacks.Where(s => s.From != null) : []
                : stacks.Where(s => !s.IsGil && (carrierCap <= 0 || s.From != null && sourceCap <= 0));
            foreach (var s in full.DistinctBy(s => s.Line))
                if (refusals.All(f => f.Line != s.Line))
                    refusals.Add(new(s.Line, s.Name, "BagSlotLimit", src.CharacterName, dst.CharacterName));
        }
        var refused = refusals.Select(f => f.Line).ToHashSet();
        var locals = new List<(OwnerScope Who, List<CourierTrip> Trips)>();
        foreach (var (src, dst, stacks) in groups)
        {
            var pickup = settings.Bells[src.LocalContentId ?? 0];
            var drop = settings.Bells[dst.LocalContentId ?? 0];
            var live = stacks.Where(s => !refused.Contains(s.Line)).ToList();
            if (!Exact(src, dst))
            {
                foreach (var batch in Pack(live, CarrierCap(dst), SourceCap(src)))
                    trips.Add(new(src, dst, pickup, drop, batch));
                continue;
            }
            // Stacks passing between retainers go first, while the most bag slots are free.
            live = [.. live.Where(Passes), .. live.Where(Stays), .. live.Where(s => s.From == null)];
            locals.Add((src, [.. Pack(live, int.MaxValue, SourceCap(src)).Select(batch => new CourierTrip(src, dst, pickup, drop, batch))]));
        }
        // A character's own moves run while the owner is already there: right after the last hand-in it
        // receives (once that custody is stowed), else right before the first hand-out it gives, else after
        // every trip. They never run inside a trade, so its own moves and a trade's stacks never share its bags.
        foreach (var (who, own) in locals)
        {
            int received = -1, given = -1;
            for (var i = 0; i < trips.Count; i++)
            {
                if (trips[i].Local) continue;
                if (Exact(trips[i].Destination, who)) received = i;
                if (given < 0 && Exact(trips[i].Source, who)) given = i;
            }
            trips.InsertRange(received >= 0 ? received + 1 : given >= 0 ? given : trips.Count, own);
        }
        return new(plan.Id, plan.Revision, trips.ToImmutable(), refusals.ToImmutable());
    }

    /// <summary>
    /// Packs stacks into trips of at most <paramref name="carrierCap"/> stacks, of which at most
    /// <paramref name="sourceCap"/> come from a retainer (stacks already in the source's bags take no
    /// source slot). Each trip takes retainer stacks first, then fills with bag stacks; stacks keep their
    /// order within a trip. Both caps must be positive when any stack needs them. Gil takes no slot on any
    /// side: it rides with the first trip, and a gil-only manifest needs no free slot at all.
    /// </summary>
    internal static IEnumerable<ImmutableArray<Shipment>> Pack(IReadOnlyList<Shipment> stacks, int carrierCap, int sourceCap)
    {
        if (stacks.Count == 0) yield break;
        var gil = Enumerable.Range(0, stacks.Count).Where(i => stacks[i].IsGil).ToList();
        if (gil.Count == stacks.Count)
        {
            yield return [.. stacks];
            yield break;
        }
        if (carrierCap <= 0 || sourceCap <= 0 && stacks.Any(s => s.From != null))
            throw new ArgumentOutOfRangeException(nameof(carrierCap), "No bag slot for the stacks.");
        var retained = new Queue<int>(Enumerable.Range(0, stacks.Count).Where(i => !stacks[i].IsGil && stacks[i].From != null));
        var bagged = new Queue<int>(Enumerable.Range(0, stacks.Count).Where(i => !stacks[i].IsGil && stacks[i].From == null));
        while (retained.Count + bagged.Count > 0)
        {
            var take = new List<int>(gil);
            gil.Clear();
            var slots = take.Count;
            while (retained.Count > 0 && take.Count - slots < Math.Min(carrierCap, sourceCap)) take.Add(retained.Dequeue());
            while (bagged.Count > 0 && take.Count - slots < carrierCap) take.Add(bagged.Dequeue());
            take.Sort();
            yield return [.. take.Select(i => stacks[i])];
        }
    }
}
