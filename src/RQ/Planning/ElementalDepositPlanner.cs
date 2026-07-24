using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;

namespace RQ.Planning;

public sealed record ElementalDepositLine(uint ItemId, string ItemName, int PlayerQuantity, int Capacity, int PlannedQuantity, int RemainingQuantity);
public sealed record ElementalDepositCandidate(ulong RetainerId, string RetainerName, DateTime ObservedAtUtc, IReadOnlyDictionary<uint, int> CapacityByItem, int UsableCapacity, bool CapacityKnown);
public sealed record ElementalDepositPlan(DateTime BuiltAtUtc, IReadOnlyList<ElementalDepositLine> Lines, IReadOnlyList<ElementalDepositCandidate> Candidates, int UnknownCrystalCacheCount)
{
    public int PlayerQuantity => Lines.Sum(line => line.PlayerQuantity);
    public int PlannedQuantity => Lines.Sum(line => line.PlannedQuantity);
    public bool CanRun => PlannedQuantity > 0 && Candidates.Count > 0;
}

public static class ElementalDepositPlanner
{
    public static ElementalDepositPlan Build(
        IReadOnlyDictionary<uint, int> playerCrystals,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        Func<uint, string?> resolveName,
        DateTime nowUtc)
    {
        var carried = playerCrystals
            .Where(entry => ElementalCurrencyCatalog.IsShardOrCrystal(entry.Key) && entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var scoped = cache.Values.Where(retainer => retainer.Owner.Matches(owner)).ToArray();
        var candidates = scoped.Select(retainer =>
            {
                var crystals = retainer.Bags.FirstOrDefault(bag => bag.BagName == "RetainerCrystals");
                var capacities = carried.Keys.ToDictionary(
                    itemId => itemId,
                    itemId => crystals is null
                        ? 0
                        : Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - crystals.Items.Where(item => item.ItemId == itemId).Sum(item => checked((int)item.Quantity))));
                return new ElementalDepositCandidate(
                    retainer.RetainerId,
                    retainer.RetainerName,
                    crystals?.ObservedAtUtc ?? retainer.ObservedAtUtc,
                    capacities,
                    capacities.Sum(entry => Math.Min(entry.Value, carried[entry.Key])),
                    crystals is not null);
            })
            .Where(candidate => candidate.CapacityKnown && candidate.UsableCapacity > 0)
            .OrderByDescending(candidate => candidate.UsableCapacity)
            .ThenByDescending(candidate => candidate.CapacityKnown)
            .ThenByDescending(candidate => candidate.ObservedAtUtc)
            .ThenBy(candidate => candidate.RetainerName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.RetainerId)
            .ToArray();
        var lines = carried.OrderBy(entry => entry.Key).Select(entry =>
        {
            var capacity = candidates.Sum(candidate => candidate.CapacityByItem[entry.Key]);
            var planned = Math.Min(entry.Value, capacity);
            return new ElementalDepositLine(entry.Key, resolveName(entry.Key) ?? $"Item {entry.Key}", entry.Value, capacity, planned, entry.Value - planned);
        }).ToArray();
        return new(nowUtc, lines, candidates, scoped.Count(retainer => retainer.Bags.All(bag => bag.BagName != "RetainerCrystals")));
    }
}
