using System.Collections.Immutable;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Courier;

/// <summary>
/// One carried item stack group an external request asks the courier to move. RetainerId, when set, names the
/// destination's retainer the units are deposited into after the hand-in; otherwise they stay in its bags.
/// </summary>
public sealed record CourierTransferItem(uint ItemId, string ItemName, bool IsHq, int Quantity, ulong? RetainerId = null);

/// <summary>
/// A direct carried transfer to a roster character: carried gil and bag-held items, never drawn from a retainer.
/// Items may name a destination retainer to be deposited into. Destination is a roster identity; Source is another
/// roster identity or the configured carrier itself.
/// </summary>
public sealed record CourierTransferOrder(OwnerScope Source, OwnerScope Destination, int Gil,
    ImmutableArray<CourierTransferItem> Items, bool ReturnDestinationHome, string RequestId);

/// <summary>A transfer the planner refused: a stable code and the plain reason.</summary>
public sealed record CourierTransferRefusal(string Code, string Message);

/// <summary>
/// Builds a <see cref="CourierPlan"/> for a direct carried transfer. Each side meets the carrier at its own
/// home-world meeting bell when the carrier's region can reach it, otherwise at the cross-region bridge
/// (a plaza every region reaches by data-center travel). Gil takes no bag slot; item stacks are packed
/// into as many trips as the carrier's free slots need. When the source is the carrier itself, one trip delivers
/// stock it already carries, with no pickup leg. Items naming a destination retainer are deposited there after the
/// hand-in, which needs the destination at its own summoning bell (never the bridge plaza), one free bag slot, and
/// room in that retainer, all as last observed (<paramref name="locations"/>).
/// </summary>
public static class CourierTransferPlanner
{
    public static (CourierPlan? Plan, CourierTransferRefusal? Refusal) Build(CourierTransferOrder order, CourierSettings settings,
        InventoryView? carrier, InventoryView? sourceLive, Func<uint, ItemMetadata> metadata, Func<uint, uint> worldRegion, MeetingBell? bridge,
        IReadOnlyList<CourierLocation>? locations = null)
    {
        static (CourierPlan?, CourierTransferRefusal?) Refuse(string code, string message) => (null, new(code, message));
        var (source, destination) = (order.Source, order.Destination);
        if (settings.Carrier is not { HasStableIdentity: true } carrierScope)
            return Refuse("CarrierNotConfigured", "No carrier is chosen.");
        if (carrier == null || !CourierPlanner.Exact(carrier.Character, carrierScope))
            return Refuse("CarrierUnreachable", $"{carrierScope.CharacterName} is not logged in on a linked client.");
        // The carrier may itself be the source: it then delivers stock it already holds, with no pickup leg.
        var fromCarrier = CourierPlanner.Exact(source, carrierScope);
        if (!fromCarrier && !settings.LocalRoster.Any(o => CourierPlanner.Exact(o, source)) || !settings.LocalRoster.Any(o => CourierPlanner.Exact(o, destination)))
            return Refuse("OutsideClientRoster", "The destination, and a source other than the carrier, must be characters of this client's service account.");
        if (CourierPlanner.Exact(source, destination) || CourierPlanner.Exact(destination, carrierScope))
            return Refuse("InvalidParticipants", "The destination must be a different character from the source and from the carrier.");
        if (order.Gil < 0 || order.Items.Any(i => i.Quantity <= 0 || i.ItemId == 0 || i.ItemId == CourierPlanner.GilItemId))
            return Refuse("InvalidManifest", "Gil must not be negative and every item needs a positive quantity.");
        if (order.Gil == 0 && order.Items.IsDefaultOrEmpty)
            return Refuse("NothingToSend", "The transfer names no gil and no items.");

        var carrierRegion = worldRegion(carrierScope.HomeWorldId ?? 0);
        (MeetingBell? Bell, CourierTransferRefusal? Refusal) Rendezvous(OwnerScope side)
        {
            var region = worldRegion(side.HomeWorldId ?? 0);
            if (CourierPlanner.CanReachRegion(carrierRegion, region))
                return settings.Bells.GetValueOrDefault(side.LocalContentId ?? 0) is { } own && own.World == side.HomeWorldId
                    ? (own, null)
                    : (null, new("MeetingBellMissing", $"{side.CharacterName} has no meeting bell saved on its home world."));
            // Every region reaches Oceania by data-center travel: the bridge serves a side the carrier cannot visit.
            return bridge != null && CourierPlanner.CanReachRegion(region, 4) && CourierPlanner.CanReachRegion(carrierRegion, 4)
                ? (bridge, null)
                : (null, new("RegionUnreachable", $"{carrierScope.CharacterName} cannot reach {side.CharacterName}'s region and no bridge is available."));
        }
        var (dropoff, dropoffRefusal) = Rendezvous(destination);
        if (dropoffRefusal != null) return (null, dropoffRefusal);
        MeetingBell? pickup = dropoff;
        if (!fromCarrier)
        {
            (pickup, var pickupRefusal) = Rendezvous(source);
            if (pickupRefusal != null) return (null, pickupRefusal);
        }

        // The carrier's own bags are the source's live bags when it sends its own stock.
        var live = fromCarrier ? carrier : sourceLive != null && CourierPlanner.Exact(sourceLive.Character, source) ? sourceLive : null;
        if (fromCarrier && order.Gil > carrier.Gil)
            return Refuse("CarrierGilShort", $"{carrierScope.CharacterName} carries {carrier.Gil:N0} gil; the transfer needs {order.Gil:N0}.");
        if (!fromCarrier && GilRefusal(live?.Gil, carrier.Gil, order.Gil) is { } gilRefusal)
            return (null, gilRefusal);

        if (RetainerRefusal(order, dropoff!, metadata, locations) is { } retainerRefusal)
            return (null, retainerRefusal);

        var stacks = new List<Shipment>();
        if (order.Gil > 0)
            stacks.Add(new(Guid.NewGuid(), CourierPlanner.GilItemId, "Gil", false, order.Gil, CourierPlanner.GilCap, null, null, source, destination));
        foreach (var group in (order.Items.IsDefault ? [] : order.Items).GroupBy(i => (i.ItemId, i.IsHq)))
        {
            var name = group.First().ItemName;
            var units = group.Sum(i => (long)i.Quantity);
            var meta = metadata(group.Key.ItemId);
            if (meta.IsTradable != true)
                return Refuse("ItemNotTradeable", $"{name} cannot be traded.");
            if (meta.MaxStackSize is not (> 0 and <= int.MaxValue) || meta.IsUnique == null)
                return Refuse("ItemMetadataUnknown", $"{name}: stack size or uniqueness unknown.");
            if (meta.IsUnique == true && units != 1)
                return Refuse("UniqueConflict", $"{name} is unique: one at a time.");
            if (live != null && live.Count(group.Key.ItemId, group.Key.IsHq) < units)
                return Refuse(fromCarrier ? "CarrierStockShort" : "SourceShort",
                    $"{source.CharacterName} carries {live.Count(group.Key.ItemId, group.Key.IsHq):N0} {name}; the transfer needs {units:N0}.");
            if (units > int.MaxValue)
                return Refuse("InvalidManifest", $"{name}: quantity too large.");
            var size = (int)meta.MaxStackSize.Value;
            var line = Guid.NewGuid();
            // Bag-bound units first, then each named retainer's, in request order.
            foreach (var part in group.GroupBy(i => i.RetainerId).OrderBy(part => part.Key.HasValue))
                for (var left = part.Sum(i => i.Quantity); left > 0; left -= Math.Min(size, left))
                    stacks.Add(new(line, group.Key.ItemId, name, group.Key.IsHq, Math.Min(size, left), size, null, part.Key, source, destination));
        }
        // Stock the carrier already holds takes no free slot of its own: one trip delivers all of it.
        if (fromCarrier)
            return (new CourierPlan(Guid.NewGuid(), 0, [new CourierTrip(source, destination, dropoff!, dropoff!, [.. stacks])], [],
                order.ReturnDestinationHome, order.RequestId), null);
        if (stacks.Any(s => !s.IsGil) && carrier.FreeSlots <= 0)
            return Refuse("BagSlotLimit", $"{carrierScope.CharacterName} has no free bag slot.");
        var trips = CourierPlanner.Pack(stacks, Math.Max(1, carrier.FreeSlots), int.MaxValue)
            .Select(batch => new CourierTrip(source, destination, pickup!, dropoff!, batch)).ToImmutableArray();
        return (new CourierPlan(Guid.NewGuid(), 0, trips, [], order.ReturnDestinationHome, order.RequestId), null);
    }

    /// <summary>
    /// Why the units naming a destination retainer cannot be deposited there: the destination meets the carrier at a
    /// plaza (no retainer can be reached), the retainer or the destination's bags were never observed, its bags have no
    /// free slot for the hand-in, or the retainer lacks room for what is named for it. Null when nothing names one.
    /// </summary>
    internal static CourierTransferRefusal? RetainerRefusal(CourierTransferOrder order, MeetingBell dropoff,
        Func<uint, ItemMetadata> metadata, IReadOnlyList<CourierLocation>? locations)
    {
        var named = (order.Items.IsDefault ? [] : order.Items).Where(i => i.RetainerId != null).ToArray();
        if (named.Length == 0)
            return null;
        var destination = order.Destination;
        if (dropoff.IsPlaza)
            return new("RetainerUnreachableAtPlaza",
                $"{destination.CharacterName} meets the carrier at {dropoff.Label}, where no retainer can be reached; deliver to its bags instead.");
        var observed = locations ?? [];
        if (observed.FirstOrDefault(l => l.Retainer == null && CourierPlanner.Exact(l.Character, destination)) is not { } bags)
            return new("DestinationBagsUnobserved", $"{destination.CharacterName}'s bags have not been observed on this client.");
        if (bags.Inventory.FreeSlots <= 0)
            return new("DestinationBagsFull", $"{destination.CharacterName} has no free bag slot to receive the goods.");
        foreach (var retainer in named.GroupBy(i => i.RetainerId!.Value))
        {
            if (observed.FirstOrDefault(l => l.Retainer == retainer.Key && CourierPlanner.Exact(l.Character, destination)) is not { } place)
                return new("DestinationRetainerUnknown", $"Retainer {retainer.Key} is not an observed retainer of {destination.CharacterName}.");
            // Partial stacks of the item take units first; the rest needs empty slots, as the courier planner counts room.
            var slots = 0L;
            foreach (var item in retainer.GroupBy(i => (i.ItemId, i.IsHq)))
            {
                // An item without a known stack size is refused as ItemMetadataUnknown by the item checks.
                if (metadata(item.Key.ItemId).MaxStackSize is not { } max || max is <= 0 or > int.MaxValue)
                    continue;
                var size = (long)max;
                var partial = place.Inventory.Stock.Where(s => s.ItemId == item.Key.ItemId && s.Hq == item.Key.IsHq && s.Units > 0)
                    .Sum(s => (size - s.Units % size) % size);
                slots += (Math.Max(0L, item.Sum(i => (long)i.Quantity) - partial) + size - 1) / size;
            }
            if (slots > place.Inventory.FreeSlots)
                return new("DestinationRetainerFull",
                    $"{destination.CharacterName}'s retainer {retainer.Key} has {place.Inventory.FreeSlots} free slot(s) as last observed; the transfer needs {slots}.");
        }
        return null;
    }

    /// <summary>
    /// Why carried gil cannot move: the source (when its live bags are known) carries too little, or the
    /// carrier would pass the carried-gil cap. Retainer gil is never drawn on.
    /// </summary>
    public static CourierTransferRefusal? GilRefusal(int? sourceGil, int carrierGil, int amount) =>
        amount <= 0 ? null
        : sourceGil is { } held && held < amount ? new("SourceGilShort", $"The source carries {held:N0} gil; the transfer needs {amount:N0}.")
        : (long)carrierGil + amount > CourierPlanner.GilCap ? new("CarrierGilCapExceeded", $"The carrier carries {carrierGil:N0} gil; {amount:N0} more passes the {CourierPlanner.GilCap:N0} cap.")
        : null;
}
