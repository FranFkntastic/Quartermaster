using RQ.Domain;

namespace RQ.Inventory;

public sealed class PlayerInventoryReconciler
{
    private readonly PlayerInventoryCacheRepository repository;
    private readonly Func<OwnerScope> owner;
    private readonly Func<PlayerStorageCapture> capture;
    private readonly TimeSpan interval;
    private DateTime nextReconciliationAtUtc = DateTime.MinValue;

    public PlayerInventoryReconciler(
        PlayerInventoryCacheRepository repository,
        Func<OwnerScope> owner,
        Func<PlayerStorageCapture> capture,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(capture);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        this.repository = repository;
        this.owner = owner;
        this.capture = capture;
        this.interval = interval;
    }

    public bool ReconcileIfDue(DateTime utcNow, bool force = false)
    {
        if (!force && utcNow < nextReconciliationAtUtc)
            return false;

        var currentOwner = owner();
        if (!currentOwner.HasStableIdentity)
            return false;

        var observation = capture();
        nextReconciliationAtUtc = utcNow.Add(interval);
        return repository.Observe(currentOwner, observation, utcNow);
    }
}
