using Franthropy.Dalamud.Automation.Retainers;

namespace RQ.Domain;

/// <summary>Coverage authorizes use; retained bag values remain historical observations.</summary>
public static class StockCoverage
{
    public static bool Ordinary(CachedRetainer retainer) => DalamudRetainerInventory.HasOrdinaryCoverage(retainer.ObservedSources);
    public static bool Crystals(CachedRetainer retainer) => DalamudRetainerInventory.HasCrystalCoverage(retainer.ObservedSources);
    public static bool CoversItem(CachedRetainer retainer, uint itemId) =>
        DalamudRetainerInventory.HasCoverageForItem(retainer.ObservedSources, itemId);
}
