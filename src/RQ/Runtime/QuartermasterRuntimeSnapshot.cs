using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Runtime;

public sealed record QuartermasterRuntimeSnapshot(
    long Revision,
    DateTime CapturedAtUtc,
    OwnerScope Owner,
    PlayerStorageCapture PlayerStorage,
    IReadOnlyDictionary<ulong, CachedRetainer> Retainers,
    QuartermasterState State,
    BrowserProjection Browser,
    RetrievalPlan Retrieval,
    ElementalDepositPlan Deposit,
    IReadOnlyList<StowageEvaluation> Stowage);

public sealed class QuartermasterRuntimeSnapshotSource
{
    private readonly InventoryScanner scanner;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly Func<OwnerScope> currentOwner;
    private QuartermasterRuntimeSnapshot? current;
    private long revision;

    public QuartermasterRuntimeSnapshotSource(
        InventoryScanner scanner,
        RetainerCacheRepository cache,
        StateRepository state,
        Func<OwnerScope> currentOwner)
    {
        this.scanner = scanner;
        this.cache = cache;
        this.state = state;
        this.currentOwner = currentOwner;
    }

    public QuartermasterRuntimeSnapshot Current => Volatile.Read(ref current)
        ?? throw new InvalidOperationException("Quartermaster runtime snapshot has not been initialized.");

    public QuartermasterRuntimeSnapshot Refresh()
    {
        var capturedAtUtc = DateTime.UtcNow;
        var owner = currentOwner();
        var playerStorage = scanner.CapturePlayerStorage();
        var retainers = cache.Snapshot();
        var stateSnapshot = state.Snapshot();
        var playerCounts = playerStorage.Bags
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var browser = BrowserProjectionBuilder.Build(playerStorage.Bags, retainers, owner, scanner.ResolveItemMetadata);
        var ownerRules = StowagePlanMigration.OwnerRules(stateSnapshot, owner);
        var retrieval = RestockPlanner.Build(ownerRules, playerCounts, retainers, owner, capturedAtUtc, browser);
        var deposit = ElementalDepositPlanner.Build(scanner.CountPlayerCrystals(), retainers, owner, scanner.ResolveItemName, capturedAtUtc);
        var stowage = StowageEvaluator.Build(stateSnapshot, browser, owner);
        var snapshot = new QuartermasterRuntimeSnapshot(
            Interlocked.Increment(ref revision),
            capturedAtUtc,
            owner,
            playerStorage,
            retainers,
            stateSnapshot,
            browser,
            retrieval,
            deposit,
            stowage);
        Volatile.Write(ref current, snapshot);
        return snapshot;
    }
}
