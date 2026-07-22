using RQ.Domain;
using RQ.Persistence;

namespace RQ.Inventory;

public sealed record CacheInvalidationResult(bool Removed, bool Persisted, string? Error);

public sealed class RetainerCacheRepository
{
    private readonly object gate = new();
    private readonly RetainerCacheStore store;
    private Dictionary<ulong, CachedRetainer> cache;

    public RetainerCacheRepository(RetainerCacheStore store)
    {
        this.store = store;
        cache = store.Load();
    }

    public event Action? Changed;
    public long Revision { get; private set; }

    public IReadOnlyDictionary<ulong, CachedRetainer> Snapshot()
    {
        lock (gate)
            return new Dictionary<ulong, CachedRetainer>(cache);
    }

    public void Upsert(CachedRetainer retainer)
    {
        lock (gate)
        {
            var candidate = new Dictionary<ulong, CachedRetainer>(cache) { [retainer.RetainerId] = retainer };
            store.Save(candidate);
            cache = candidate;
            Revision++;
        }
        Changed?.Invoke();
    }

    public CacheInvalidationResult Invalidate(ulong retainerId)
    {
        Exception? persistenceError = null;
        lock (gate)
        {
            var candidate = new Dictionary<ulong, CachedRetainer>(cache);
            if (!candidate.Remove(retainerId))
                return new(false, true, null);
            cache = candidate;
            Revision++;
            try
            {
                store.SaveAfterInvalidation(candidate);
            }
            catch (Exception exception)
            {
                persistenceError = exception;
            }
        }
        Changed?.Invoke();
        return persistenceError is null
            ? new(true, true, null)
            : new(true, false, persistenceError.Message);
    }
}
