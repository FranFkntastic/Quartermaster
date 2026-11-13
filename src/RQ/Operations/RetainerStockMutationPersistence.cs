using Franthropy.Dalamud.Automation.Transactions;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Operations;

internal sealed record RetainerStockMutationIntent(
    string OperationId,
    ulong RetainerId,
    OwnerScope Owner,
    uint ItemId,
    string ItemName,
    bool IsHighQuality);

internal sealed class RetainerStockMutationPersistence(
    OperationJournal journal,
    RetainerCacheRepository cache,
    Func<RetainerStockMutationIntent, Task<RetainerVariantObservation>>? recoverObservation = null)
    : IVerifiedMutationPersistence<RetainerStockMutationIntent, RetainerVariantObservation>
{
    public ValueTask ArmAsync(RetainerStockMutationIntent intent)
    {
        journal.ArmCacheInvalidation(intent.OperationId, intent.RetainerId, intent.Owner);
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync(RetainerStockMutationIntent intent, RetainerVariantObservation mutation)
    {
        if (mutation.RetainerId != intent.RetainerId)
            throw new InvalidOperationException("Verified stock evidence belongs to a different retainer.");
        // The journal marker is resolved right after this returns, so success must mean
        // the committed stock is durable; a failed write keeps the marker for recovery.
        return new ValueTask(cache.ReplaceObservedVariantDurablyAsync(mutation));
    }

    public async ValueTask InvalidateAsync(RetainerStockMutationIntent intent)
    {
        // Withdraw authority before publishing any recovery observation.
        var invalidation = cache.InvalidateStock(intent.RetainerId);
        if (!invalidation.Persisted)
            throw new IOException($"Retainer {intent.RetainerId} cache invalidation did not persist: {invalidation.Error}");
        if (recoverObservation is not null)
        {
            try
            {
                // Repair observations only, never replay or acknowledge the movement.
                var observed = await recoverObservation(intent).ConfigureAwait(false);
                // A reread is an observation, not proof that a late move cannot arrive.
                // Coverage stays invalid even when this observation succeeds.
                cache.ReplaceObservedVariant(observed);
            }
            catch (Exception exception)
            {
                journal.RecordWarning(intent.OperationId, "StockObservationRecoveryFailed", exception.Message);
            }
        }
    }

    public ValueTask ResolveAsync(RetainerStockMutationIntent intent)
    {
        journal.ResolveCacheInvalidation(intent.OperationId, intent.RetainerId);
        return ValueTask.CompletedTask;
    }

    public static int RecoverPending(OperationJournal journal, RetainerCacheRepository cache)
    {
        var recovered = 0;
        foreach (var pending in journal.PendingCacheInvalidations())
        {
            var result = cache.InvalidateStock(pending.RetainerId);
            if (!result.Persisted)
                continue;
            journal.ResolveCacheInvalidation(pending.OperationId, pending.RetainerId);
            recovered++;
        }
        return recovered;
    }
}
