using RQ.Domain;

namespace RQ.Planning;

public enum PlanLineStatus
{
    Satisfied,
    Ready,
    Partial,
    NoCachedStock,
    StockUnverified,
}

public sealed record RetainerCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTime ObservedAtUtc,
    int CachedQuantity);

public sealed record PlanLine(
    Guid PlanItemId,
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int TargetQuantity,
    int PlayerQuantity,
    int NeededQuantity,
    int CachedRetainerQuantity,
    int MissingQuantity,
    IReadOnlyList<RetainerCandidate> Candidates,
    PlanLineStatus Status,
    TimeSpan? OldestEvidenceAge,
    int SaddlebagQuantity = 0)
{
    /// <summary>Units the saddlebag leg retrieves before any retainer is opened.</summary>
    public int SaddlebagCoverQuantity => Math.Min(NeededQuantity, SaddlebagQuantity);
}

public sealed record RetrievalPlan(DateTime BuiltAtUtc, IReadOnlyList<PlanLine> Lines)
{
    public int NeededQuantity => Lines.Sum(line => line.NeededQuantity);
    public int CoveredQuantity => Lines.Sum(line => Math.Min(line.NeededQuantity, line.SaddlebagCoverQuantity + line.CachedRetainerQuantity));
    public int MissingQuantity => Lines.Sum(line => line.MissingQuantity);
}

public static class RestockPlanner
{
    public static RetrievalPlan Build(
        IReadOnlyList<TargetPlanItem> rows,
        IReadOnlyDictionary<uint, int> playerInventory,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        DateTime nowUtc,
        BrowserProjection? stock = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(playerInventory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(owner);

        var lines = rows
            .Where(row => row.Enabled && row.ItemId > 0 && row.TargetQuantity > 0)
            .Select(row => BuildLine(row, playerInventory, cache, owner, nowUtc, stock))
            .OrderByDescending(line => line.NeededQuantity > 0)
            .ThenByDescending(line => line.MissingQuantity > 0)
            .ThenBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.ItemId)
            .ToArray();
        return new(nowUtc, lines);
    }

    private static PlanLine BuildLine(
        TargetPlanItem row,
        IReadOnlyDictionary<uint, int> playerInventory,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        DateTime nowUtc,
        BrowserProjection? stock)
    {
        var stacks = stock?.Items.FirstOrDefault(item => item.ItemId == row.ItemId)?.Stacks
            .Where(stack => QualityMatches(row.Quality, stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ))
            .ToArray();
        var player = stacks?.Where(stack => stack.ScopeKind == BrowserScopeKind.Player).Sum(stack => stack.Quantity)
            ?? playerInventory.GetValueOrDefault(row.ItemId);
        var saddlebag = stacks?.Where(stack => stack.ScopeKind == BrowserScopeKind.Saddlebag).Sum(stack => stack.Quantity) ?? 0;
        var needed = Math.Max(0, row.TargetQuantity - player);
        var candidates = needed == 0
            ? []
            : cache.Values
                .Where(retainer => retainer.Owner.Matches(owner))
                .Where(retainer => retainer.IsCurrentlyAssigned is not false)
                .Where(retainer => retainer.IsUiAccessible is not false)
                .Where(retainer => StockCoverage.CoversItem(retainer, row.ItemId))
                .Select(retainer =>
                {
                    var evidence = retainer.Bags
                        .Select(bag => new
                        {
                            bag.ObservedAtUtc,
                            Quantity = bag.Items
                                .Where(item => item.ItemId == row.ItemId && QualityMatches(row.Quality, item.IsHq))
                                .Sum(item => checked((int)item.Quantity)),
                        })
                        .Where(bag => bag.Quantity > 0)
                        .ToArray();
                    return new RetainerCandidate(
                        retainer.RetainerId,
                        retainer.RetainerName,
                        evidence.Length == 0 ? DateTime.MinValue : evidence.Min(bag => bag.ObservedAtUtc ?? DateTime.MinValue),
                        evidence.Sum(bag => bag.Quantity));
                })
                .Where(candidate => candidate.CachedQuantity > 0)
                .OrderByDescending(candidate => candidate.CachedQuantity)
                .ThenByDescending(candidate => candidate.ObservedAtUtc)
                .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var cached = candidates.Sum(candidate => candidate.CachedQuantity);
        // The saddlebag is drawn first; retainers cover only what it leaves short.
        var retainerNeed = needed - Math.Min(needed, saddlebag);
        var missing = Math.Max(0, retainerNeed - cached);
        var unverified = cache.Values.Any(retainer => retainer.Owner.Matches(owner) &&
            retainer.IsCurrentlyAssigned is not false && retainer.IsUiAccessible is not false &&
            !StockCoverage.CoversItem(retainer, row.ItemId));
        var status = retainerNeed > cached && unverified
            ? PlanLineStatus.StockUnverified
            : needed == 0
            ? PlanLineStatus.Satisfied
            : cached == 0 && retainerNeed == needed
                ? PlanLineStatus.NoCachedStock
                : missing > 0 ? PlanLineStatus.Partial : PlanLineStatus.Ready;
        TimeSpan? oldestAge = candidates.Length == 0 ? null : nowUtc - candidates.Min(candidate => candidate.ObservedAtUtc);
        return new(row.Id, row.ItemId, row.ItemName, row.Quality, row.TargetQuantity, player, needed, cached, missing, candidates, status, oldestAge, saddlebag);
    }

    private static bool QualityMatches(ItemQualityPolicy policy, bool isHighQuality) => policy switch
    {
        ItemQualityPolicy.NqOnly => !isHighQuality,
        ItemQualityPolicy.HqOnly => isHighQuality,
        _ => true,
    };
}
