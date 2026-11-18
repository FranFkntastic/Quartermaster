using RQ.Domain;

namespace RQ.Planning;

/// <summary>Observed inventory for one line. Partial capacity excludes empty slots and other items.</summary>
public sealed record PlacementStock(ulong RetainerId, int Nq, int Hq, int FreeSlots,
    int NqPartialCapacity = 0, int HqPartialCapacity = 0);
public sealed record PlacementMove(ulong SourceRetainerId, ulong DestinationRetainerId,
    uint ItemId, string ItemName, bool IsHighQuality, int Quantity);
public sealed record PlacementLimit(string Code, ulong RetainerId, uint ItemId, int Quantity, string Message);
public sealed record PlacementMovePlan(IReadOnlyList<PlacementMove> Moves, IReadOnlyList<PlacementLimit> Limits)
{
    public int MoveQuantity => Moves.Sum(move => move.Quantity);
}

public static class PlacementMovePlanner
{
    internal static PlacementMovePlan FromCache(TargetPlanItem rule, IEnumerable<CachedRetainer> ownerRoster, int size)
    {
        var roster = ownerRoster.ToDictionary(r => r.RetainerId);
        var destination = rule.Routing.PreferredRetainerIds.First();
        string Name(ulong id) => roster.GetValueOrDefault(id)?.RetainerName ?? $"retainer {id}";
        var limits = new List<PlacementLimit>();
        var stock = new List<PlacementStock>();
        var crystal = Franthropy.Dalamud.Automation.Retainers.ElementalCurrencyCatalog.IsElementalCurrency(rule.ItemId);
        foreach (var id in roster.Keys.Append(destination).Distinct().Order())
        {
            var r = roster.GetValueOrDefault(id);
            if (id != destination && (r?.IsCurrentlyAssigned == false || r?.IsUiAccessible == false)) continue;
            if (r is not { Owner.HasStableIdentity: true, IsCurrentlyAssigned: true, IsUiAccessible: true } || !StockCoverage.CoversItem(r, rule.ItemId))
            {
                limits.Add(new("PlacementEvidenceUnavailable", id, rule.ItemId, 0,
                    $"{rule.ItemName} → {Name(destination)}: inventory, assignment or access unverified for {Name(id)}."));
                continue;
            }
            stock.Add(new(id, StowageRouter.Held(r, rule.ItemId, ItemQualityPolicy.NqOnly),
                StowageRouter.Held(r, rule.ItemId, ItemQualityPolicy.HqOnly), crystal ? 0 : StowageRouter.FreeInventorySlots(r) ?? 0,
                StowageRouter.Capacity(r, rule.ItemId, false, size).PartialStackCapacity,
                StowageRouter.Capacity(r, rule.ItemId, true, size).PartialStackCapacity));
        }
        var moves = new List<PlacementMove>();
        if (stock.FirstOrDefault(s => s.RetainerId == destination) is not { } target) return new(moves, limits);
        var slots = target.FreeSlots;
        int[] partial = [target.NqPartialCapacity, target.HqPartialCapacity];
        foreach (var source in stock.Where(s => s.RetainerId != destination))
        {
            foreach (var hq in new[] { false, true })
            {
                if (rule.Quality != ItemQualityPolicy.Any && hq != (rule.Quality == ItemQualityPolicy.HqOnly)) continue;
                var q = hq ? 1 : 0;
                var units = hq ? source.Hq : source.Nq;
                var moved = (int)Math.Min(units, partial[q] + (long)slots * size);
                if (moved > 0)
                {
                    var used = (int)(((long)Math.Max(0, moved - partial[q]) + size - 1) / size);
                    slots -= used;
                    partial[q] = checked(partial[q] + used * size - moved);
                    moves.Add(new(source.RetainerId, destination, rule.ItemId, rule.ItemName, hq, moved));
                }
                if (moved < units)
                    limits.Add(new("PlacementDestinationFull", source.RetainerId, rule.ItemId, units - moved,
                        $"{rule.ItemName} from {Name(source.RetainerId)}: {Name(destination)} has no room for {units - moved:N0} units."));
            }
        }
        return new(moves, limits);
    }
}
