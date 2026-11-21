using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Courier;

// Null source retainer means bags; zero is not a retainer. A null SourceCharacter means the plan owner.
// Where the units land on Character lives in the line's Routing, as in Transfer Placement: no
// destinations keeps them in its bags, else its ranked retainers, then Any space.
public sealed record CourierRoute(OwnerScope Character, ulong? SourceRetainerId, OwnerScope? SourceCharacter = null)
{
    /// <summary>
    /// The single destination retainer of routes written before courier lines carried a Routing.
    /// Read from older state and moved into the line's Routing on load (see
    /// <c>StowagePlanMigration.MoveCourierRetainersToRouting</c>); never written once migrated.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ulong? RetainerId { get; init; }
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
public sealed record InventoryView(OwnerScope Character, int FreeSlots, ImmutableArray<BagStock> Stock)
{
    public int Count(uint item, bool hq) => Stock.Where(x => x.ItemId == item && x.Hq == hq).Sum(x => x.Units);
}
public sealed record CourierLocation(OwnerScope Character, ulong? Retainer, InventoryView Inventory);
public sealed record Shipment(Guid Line, uint Item, string Name, bool Hq, int Units, int StackSize,
    ulong? From, ulong? To, OwnerScope Source, OwnerScope Destination);
public sealed record CourierTrip(OwnerScope Source, OwnerScope Destination, MeetingBell Pickup, MeetingBell Dropoff,
    ImmutableArray<Shipment> Manifest);
public sealed record Refusal(Guid Line, string Name, string Code, string? Source=null, string? Destination=null);
/// <summary>
/// Destination-side consolidation for one line: once its units are delivered, gather the item's
/// stray stacks on Destination's other retainers into Retainer (the line's rank 1).
/// </summary>
public sealed record CourierConsolidation(OwnerScope Destination, Guid Line, uint Item, string Name,
    ItemQualityPolicy Quality, ulong Retainer, int StackSize);
public sealed record CourierPlan(Guid PlanId, long Revision, ImmutableArray<CourierTrip> Trips, ImmutableArray<Refusal> Refusals)
{
    /// <summary>Absent in journals written before destination consolidation, which read as none.</summary>
    public ImmutableArray<CourierConsolidation> Consolidations { get; init; } = [];
}

public static class CourierPlanner
{
    internal static bool Exact(OwnerScope? a, OwnerScope? b) => a is { HasStableIdentity: true }
        && b is { HasStableIdentity: true } && a.Matches(b) && !string.IsNullOrWhiteSpace(a.CharacterName)
        && string.Equals(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase);

    // Owner locations must belong to the authenticated service-account roster; omit unknown containers.
    public static CourierPlan Build(StowagePlan plan, IEnumerable<TargetPlanItem> rules,
        IReadOnlyList<CourierLocation> locations, InventoryView? carrier, CourierSettings settings,
        Func<uint, ItemMetadata> metadata, int? testSlotCap = null)
    {
        var trips = ImmutableArray.CreateBuilder<CourierTrip>();
        var refusals = ImmutableArray.CreateBuilder<Refusal>();
        var consolidations = ImmutableArray.CreateBuilder<CourierConsolidation>();
        var uniqueDestinations=new HashSet<(ulong?,uint)>();
        var stock = locations.ToDictionary(x => (x.Character.LocalContentId, x.Retainer), x => x.Inventory.Stock.ToList());
        var space = locations.ToDictionary(x => (x.Character.LocalContentId, x.Retainer), x => x.Inventory.FreeSlots);
        foreach (var group in rules.Where(r => r.Enabled && r.StowagePlanId == plan.Id && r.Courier != null)
            .GroupBy(r => (Source: r.Courier!.SourceCharacter ?? plan.Owner, Dest: r.Courier.Character)))
        {
            var (src, dst) = group.Key;
            CourierLocation? At(OwnerScope who, ulong? ret) => locations.FirstOrDefault(x =>
                Exact(x.Character, who) && Exact(x.Inventory.Character, who) && x.Retainer == ret);
            var sourceBags = At(src, null);
            var destBags = At(dst, null);
            // The destination's observed retainers in display order: Any space fills them in this order.
            var destRetainers = locations.Where(x => x.Retainer != null && Exact(x.Character, dst) && Exact(x.Inventory.Character, dst))
                .Select(x => x.Retainer!.Value).Distinct().ToArray();
            var pickup = settings.Bells.GetValueOrDefault(src.LocalContentId ?? 0);
            var drop = settings.Bells.GetValueOrDefault(dst.LocalContentId ?? 0);
            var tripUnique=new HashSet<uint>();
            var stacks = new List<Shipment>();
            foreach (var r in group)
            {
                var route = r.Courier!;
                var meta = metadata(r.ItemId);
                var source = At(src, route.SourceRetainerId);
                var targets = Destinations(r.Routing, destRetainers);
                string? code = settings.Carrier is not { HasStableIdentity: true } ? "CarrierNotConfigured"
                    : !Exact(carrier?.Character, settings.Carrier) ? "CarrierUnreachable"
                    : !settings.LocalRoster.Any(o=>Exact(o,src)) || !settings.LocalRoster.Any(o=>Exact(o,dst)) ? "OutsideClientRoster"
                    : sourceBags == null || destBags == null || Exact(src, dst) || Exact(src, settings.Carrier) || Exact(dst, settings.Carrier) ? "CharacterNotKnown"
                    : pickup?.World != src.HomeWorldId || drop?.World != dst.HomeWorldId || pickup == null || drop == null ? "MeetingBellMissing"
                    : meta.IsTradable != true ? "ItemNotTradeable"
                    : source == null ? "SourceUnknown" : targets.Count == 0 ? "DestinationUnknown"
                    : meta.MaxStackSize is not (> 0 and <= int.MaxValue) || meta.IsUnique == null ? "ItemMetadataUnknown"
                    : r.TargetQuantity <= 0 ? "InvalidQuantity" : null;
                void Refuse(string reason) => refusals.Add(new(r.Id, r.ItemName, reason,src.CharacterName,dst.CharacterName));
                if (code != null) { Refuse(code); continue; }
                if (meta.IsUnique == true && (r.TargetQuantity != 1 || tripUnique.Contains(r.ItemId) || uniqueDestinations.Contains((dst.LocalContentId,r.ItemId)) || carrier!.Stock.Any(s => s.ItemId == r.ItemId && s.Units > 0)
                    || locations.Where(l => Exact(l.Character, dst)).Any(l => stock[(dst.LocalContentId, l.Retainer)].Any(s => s.ItemId == r.ItemId && s.Units > 0))))
                { Refuse("UniqueConflict"); continue; }
                var size = (int)meta.MaxStackSize!.Value;
                var remaining = r.TargetQuantity;
                foreach (var hq in new[] { false, true })
                {
                    if (remaining == 0 || (r.Quality != ItemQualityPolicy.Any && hq != (r.Quality == ItemQualityPolicy.HqOnly))) continue;
                    var inventory = stock[(src.LocalContentId, route.SourceRetainerId)];
                    var supply = inventory.Where(s => s.ItemId == r.ItemId && s.Hq == hq).Sum(s => s.Units);
                    var units = Math.Min(supply, remaining);
                    if (units == 0) continue;
                    int Partial(List<BagStock> dest) =>
                        dest.Where(s => s.ItemId == r.ItemId && s.Hq == hq && s.Units > 0).Sum(s => (size - s.Units % size) % size);
                    long Room(ulong? to) =>
                        Partial(stock[(dst.LocalContentId, to)]) + (long)Math.Max(0, space[(dst.LocalContentId, to)]) * size;
                    // Rank 1 first, spilling to rank 2, ... then Any space; refuse only when the whole chain is short.
                    if (targets.Sum(Room) < units) { Refuse("DestinationFull"); break; }
                    var left = units;
                    foreach (var to in targets)
                    {
                        if (left == 0) break;
                        var placed = (int)Math.Min(left, Room(to));
                        if (placed == 0) continue;
                        var key = (dst.LocalContentId, to);
                        var dest = stock[key];
                        space[key] -= (int)((Math.Max(0L, (long)placed - Partial(dest)) + size - 1) / size);
                        // Preserve physical stack boundaries when consuming partial-stack room.
                        var add = placed;
                        for (var i = 0; i < dest.Count && add > 0; i++)
                            if (dest[i].ItemId == r.ItemId && dest[i].Hq == hq && dest[i].Units > 0)
                            {
                                var n = Math.Min(add, (size - dest[i].Units % size) % size);
                                dest[i] = dest[i] with { Units = dest[i].Units + n }; add -= n;
                            }
                        if (add > 0) dest.Add(new(r.ItemId, hq, add));
                        for (var slice = placed; slice > 0;)
                        {
                            var n = Math.Min(size, slice);
                            stacks.Add(new(r.Id, r.ItemId, r.ItemName, hq, n, size, route.SourceRetainerId, to, src, dst)); slice -= n;
                        }
                        left -= placed;
                    }
                    inventory.RemoveAll(s => s.ItemId == r.ItemId && s.Hq == hq);
                    inventory.Add(new(r.ItemId, hq, supply - units));
                    if(meta.IsUnique==true){tripUnique.Add(r.ItemId);uniqueDestinations.Add((dst.LocalContentId,r.ItemId));}
                    remaining -= units;
                }
                if (remaining > 0 && refusals.All(f => f.Line != r.Id)) Refuse("SourceShort");
                if (r.Routing.Consolidate && r.Routing.PreferredRetainerIds.Count > 0)
                    consolidations.Add(new(dst, r.Id, r.ItemId, r.ItemName, r.Quality, r.Routing.PreferredRetainerIds[0], size));
            }
            // Destination bag slots do not shorten a trip: the conductor splits the hand-in into batches that
            // each fit and are stowed before the next, which beats an extra round trip. It still needs one slot.
            var cap = (destBags?.Inventory.FreeSlots ?? 0) <= 0 ? 0 : Math.Min(testSlotCap ?? int.MaxValue,
                Math.Min(carrier?.FreeSlots ?? 0, sourceBags?.Inventory.FreeSlots ?? 0));
            if (cap <= 0) foreach (var s in stacks.DistinctBy(s => s.Line)) refusals.Add(new(s.Line, s.Name, "BagSlotLimit",src.CharacterName,dst.CharacterName));
            else foreach (var batch in stacks.Chunk(cap)) trips.Add(new(src, dst, pickup!, drop!, [.. batch]));
        }
        // A refused line delivers nothing, so there is nothing of it to gather.
        var refused = refusals.Select(f => f.Line).ToHashSet();
        return new(plan.Id, plan.Revision, trips.ToImmutable(), refusals.ToImmutable())
        {
            Consolidations = [.. consolidations.Where(c => !refused.Contains(c.Line))],
        };
    }

    /// <summary>
    /// The containers a line's units may land in on its destination, in fill order. No destinations
    /// is the destination's bags (null), as in Transfer Placement; otherwise its listed retainers that
    /// are observed, then, with Any space, every other observed retainer in display order. Empty when
    /// nothing the line lists is observed.
    /// </summary>
    internal static IReadOnlyList<ulong?> Destinations(StowageRoutingPolicy routing, IReadOnlyList<ulong> observed)
    {
        if (routing.PreferredRetainerIds.Count == 0 && !routing.AnySpace)
            return [null];
        var listed = routing.PreferredRetainerIds.Distinct().Where(observed.Contains).ToList();
        if (routing.AnySpace)
            listed.AddRange(observed.Where(id => !listed.Contains(id)));
        return [.. listed.Select(id => (ulong?)id)];
    }
}
