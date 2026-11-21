using System.Collections.Immutable;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Courier;

/// <summary>One carried item stack group an external request asks the courier to move.</summary>
public sealed record CourierTransferItem(uint ItemId, string ItemName, bool IsHq, int Quantity);

/// <summary>
/// A direct carried transfer between two roster characters: carried gil and bag-held items only, never a
/// retainer. Source and Destination are the roster's own identities.
/// </summary>
public sealed record CourierTransferOrder(OwnerScope Source, OwnerScope Destination, int Gil,
    ImmutableArray<CourierTransferItem> Items, bool ReturnDestinationHome, string RequestId);

/// <summary>A transfer the planner refused: a stable code and the plain reason.</summary>
public sealed record CourierTransferRefusal(string Code, string Message);

/// <summary>
/// Builds a <see cref="CourierPlan"/> for a direct carried transfer. Each side meets the carrier at its own
/// home-world meeting bell when the carrier's region can reach it, otherwise at the cross-region bridge
/// (a plaza every region reaches by data-center travel). Gil takes no bag slot; item stacks are packed
/// into as many trips as the carrier's free slots need.
/// </summary>
public static class CourierTransferPlanner
{
    public static (CourierPlan? Plan, CourierTransferRefusal? Refusal) Build(CourierTransferOrder order, CourierSettings settings,
        InventoryView? carrier, InventoryView? sourceLive, Func<uint, ItemMetadata> metadata, Func<uint, uint> worldRegion, MeetingBell? bridge)
    {
        static (CourierPlan?, CourierTransferRefusal?) Refuse(string code, string message) => (null, new(code, message));
        var (source, destination) = (order.Source, order.Destination);
        if (settings.Carrier is not { HasStableIdentity: true } carrierScope)
            return Refuse("CarrierNotConfigured", "No carrier is chosen.");
        if (carrier == null || !CourierPlanner.Exact(carrier.Character, carrierScope))
            return Refuse("CarrierUnreachable", $"{carrierScope.CharacterName} is not logged in on a linked client.");
        if (!settings.LocalRoster.Any(o => CourierPlanner.Exact(o, source)) || !settings.LocalRoster.Any(o => CourierPlanner.Exact(o, destination)))
            return Refuse("OutsideClientRoster", "Source and destination must both be characters of this client's service account.");
        if (CourierPlanner.Exact(source, destination) || CourierPlanner.Exact(source, carrierScope) || CourierPlanner.Exact(destination, carrierScope))
            return Refuse("InvalidParticipants", "Source, destination and carrier must be three different characters.");
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
        var (pickup, pickupRefusal) = Rendezvous(source);
        if (pickupRefusal != null) return (null, pickupRefusal);
        var (dropoff, dropoffRefusal) = Rendezvous(destination);
        if (dropoffRefusal != null) return (null, dropoffRefusal);

        var live = sourceLive != null && CourierPlanner.Exact(sourceLive.Character, source) ? sourceLive : null;
        if (GilRefusal(live?.Gil, carrier.Gil, order.Gil) is { } gilRefusal)
            return (null, gilRefusal);

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
                return Refuse("SourceShort", $"{source.CharacterName} carries {live.Count(group.Key.ItemId, group.Key.IsHq):N0} {name}; the transfer needs {units:N0}.");
            if (units > int.MaxValue)
                return Refuse("InvalidManifest", $"{name}: quantity too large.");
            var size = (int)meta.MaxStackSize.Value;
            var line = Guid.NewGuid();
            for (var left = (int)units; left > 0; left -= Math.Min(size, left))
                stacks.Add(new(line, group.Key.ItemId, name, group.Key.IsHq, Math.Min(size, left), size, null, null, source, destination));
        }
        if (stacks.Any(s => !s.IsGil) && carrier.FreeSlots <= 0)
            return Refuse("BagSlotLimit", $"{carrierScope.CharacterName} has no free bag slot.");
        var trips = CourierPlanner.Pack(stacks, Math.Max(1, carrier.FreeSlots), int.MaxValue)
            .Select(batch => new CourierTrip(source, destination, pickup!, dropoff!, batch)).ToImmutableArray();
        return (new CourierPlan(Guid.NewGuid(), 0, trips, [], order.ReturnDestinationHome, order.RequestId), null);
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
