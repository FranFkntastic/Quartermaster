using System.Collections.Immutable;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Courier;

// Null retainer means bags; zero is not a retainer. A null SourceCharacter means the plan owner.
public sealed record CourierRoute(OwnerScope Character, ulong? RetainerId, ulong? SourceRetainerId,
    OwnerScope? SourceCharacter = null);
public sealed record MeetingBell(uint Territory, uint World, uint ObjectId, float X, float Y, float Z, string Label)
{
    public bool SamePlace(MeetingBell other) => this with { Label = "" } == other with { Label = "" };
}
public sealed record CourierSettings
{
    public OwnerScope? Carrier { get; init; }
    public OwnerScope? PairedOwner { get; init; }
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
public sealed record Refusal(Guid Line, string Name, string Code);
public sealed record CourierPlan(Guid PlanId, long Revision, ImmutableArray<CourierTrip> Trips, ImmutableArray<Refusal> Refusals);

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
            var pickup = settings.Bells.GetValueOrDefault(src.LocalContentId ?? 0);
            var drop = settings.Bells.GetValueOrDefault(dst.LocalContentId ?? 0);
            var stacks = new List<Shipment>();
            foreach (var r in group)
            {
                var route = r.Courier!;
                var meta = metadata(r.ItemId);
                var source = At(src, route.SourceRetainerId);
                var target = At(dst, route.RetainerId);
                string? code = settings.Carrier is not { HasStableIdentity: true } ? "CarrierNotConfigured"
                    : !Exact(carrier?.Character, settings.Carrier) ? "CarrierUnreachable"
                    : sourceBags == null || destBags == null || Exact(src, dst) || Exact(src, settings.Carrier) || Exact(dst, settings.Carrier) ? "CharacterNotKnown"
                    : pickup?.World != src.HomeWorldId || drop?.World != dst.HomeWorldId || pickup == null || drop == null ? "MeetingBellMissing"
                    : meta.IsTradable != true ? "ItemNotTradeable"
                    : source == null ? "SourceUnknown" : target == null ? "DestinationUnknown"
                    : meta.MaxStackSize is not (> 0 and <= int.MaxValue) || meta.IsUnique == null ? "ItemMetadataUnknown"
                    : r.TargetQuantity <= 0 ? "InvalidQuantity" : null;
                void Refuse(string reason) => refusals.Add(new(r.Id, r.ItemName, reason));
                if (code != null) { Refuse(code); continue; }
                var targetKey = (dst.LocalContentId, route.RetainerId);
                if (meta.IsUnique == true && (r.TargetQuantity != 1 || carrier!.Stock.Any(s => s.ItemId == r.ItemId && s.Units > 0)
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
                    var dest = stock[targetKey];
                    var partial = dest.Where(s => s.ItemId == r.ItemId && s.Hq == hq && s.Units > 0).Sum(s => (size - s.Units % size) % size);
                    var slots = (int)((Math.Max(0L, (long)units - partial) + size - 1) / size);
                    if (space[targetKey] < slots) { Refuse("DestinationFull"); break; }
                    space[targetKey] -= slots;
                    // Preserve physical stack boundaries when consuming partial-stack room.
                    var add = units;
                    for (var i = 0; i < dest.Count && add > 0; i++)
                        if (dest[i].ItemId == r.ItemId && dest[i].Hq == hq && dest[i].Units > 0)
                        {
                            var n = Math.Min(add, (size - dest[i].Units % size) % size);
                            dest[i] = dest[i] with { Units = dest[i].Units + n }; add -= n;
                        }
                    if (add > 0) dest.Add(new(r.ItemId, hq, add));
                    inventory.RemoveAll(s => s.ItemId == r.ItemId && s.Hq == hq);
                    inventory.Add(new(r.ItemId, hq, supply - units));
                    for (var left = units; left > 0;)
                    {
                        var n = Math.Min(size, left);
                        stacks.Add(new(r.Id, r.ItemId, r.ItemName, hq, n, size, route.SourceRetainerId, route.RetainerId, src, dst)); left -= n;
                    }
                    remaining -= units;
                }
                if (remaining > 0 && refusals.All(f => f.Line != r.Id)) Refuse("SourceShort");
            }
            var cap = Math.Min(testSlotCap ?? int.MaxValue, Math.Min(carrier?.FreeSlots ?? 0,
                Math.Min(sourceBags?.Inventory.FreeSlots ?? 0, destBags?.Inventory.FreeSlots ?? 0)));
            if (cap <= 0) foreach (var s in stacks.DistinctBy(s => s.Line)) refusals.Add(new(s.Line, s.Name, "BagSlotLimit"));
            else foreach (var batch in stacks.Chunk(cap)) trips.Add(new(src, dst, pickup!, drop!, [.. batch]));
        }
        return new(plan.Id, plan.Revision, trips.ToImmutable(), refusals.ToImmutable());
    }
}
