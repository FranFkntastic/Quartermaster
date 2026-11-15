namespace RQ.Domain;

public enum ListingCountBound { Exact, Upper, Lower, Mixed }

/// <summary>A newer roster count constrains cached rows without identifying which rows changed.</summary>
public sealed record ListingCountObservation(int GameCount, int CachedCount)
{
    public ListingCountBound Bound => GameCount < CachedCount ? ListingCountBound.Upper : ListingCountBound.Lower;
    public string Summary => GameCount < CachedCount ? "Some listings sold" : "Newer listings";
    public string Description => GameCount < CachedCount
        ? $"Game reports {GameCount} listed (was {CachedCount}) — some of these have sold"
        : $"Game reports {GameCount} listed (cached {CachedCount}) — newer listings not yet seen";

    public static ListingCountObservation? From(CachedRetainer retainer) =>
        retainer.MarketItemCount is > 0 && retainer.RosterObservedAtUtc.HasValue &&
        (!retainer.ListingsObservedAtUtc.HasValue || retainer.RosterObservedAtUtc > retainer.ListingsObservedAtUtc) &&
        retainer.MarketItemCount.Value != retainer.Listings.Count
            ? new(retainer.MarketItemCount.Value, retainer.Listings.Count) : null;
}
