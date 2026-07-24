using Franthropy.Dalamud.Automation.Transactions;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Operations;

internal sealed record RetainerStockMutationIntent(
    string OperationId,
    ulong RetainerId,
    OwnerScope Owner);

internal sealed class RetainerStockMutationPersistence(
    OperationJournal journal,
    RetainerCacheRepository cache)
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
        cache.ReplaceObservedVariant(mutation);
        return ValueTask.CompletedTask;
    }

    public ValueTask InvalidateAsync(RetainerStockMutationIntent intent)
    {
        var invalidation = cache.Invalidate(intent.RetainerId);
        if (!invalidation.Persisted)
            throw new IOException($"Retainer {intent.RetainerId} cache invalidation did not persist: {invalidation.Error}");
        return ValueTask.CompletedTask;
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
            var result = cache.Invalidate(pending.RetainerId);
            if (!result.Persisted)
                continue;
            journal.ResolveCacheInvalidation(pending.OperationId, pending.RetainerId);
            recovered++;
        }
        return recovered;
    }
}
