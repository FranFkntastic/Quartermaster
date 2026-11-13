using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;
using Franthropy.Dalamud.Persistence;
using RQ.Domain;

namespace RQ.Persistence;

public sealed class AtomicDocumentStore<T> where T : new()
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // Large caches are written compactly; reads accept either layout.
    public static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    private readonly JsonSerializerOptions options;

    public AtomicDocumentStore(string path, JsonSerializerOptions? options = null)
    {
        Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        this.options = options ?? JsonOptions;
    }

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public T Load() => Exists ? AtomicJsonFile.Read<T>(Path, options) ?? new T() : new T();

    public void Save(T value) => AtomicJsonFile.Write(Path, value, options);
}

public sealed record CacheWriteFailure(
    string Path,
    long Revision,
    int EntryCount,
    int Attempt,
    bool WillRetry,
    Exception Exception);

/// <summary>
/// Persists whole-cache snapshots off the calling thread. Only the latest handed-off
/// snapshot is written, and snapshots must not be mutated after handoff. Every write
/// runs under the supplied write gate so owners can serialize synchronous rewrites
/// (and discard queued snapshots) against the worker. Each handoff returns an
/// acknowledgment that completes once a snapshot at or after its revision is durable,
/// and faults when that write fails or cannot finish before shutdown.
/// </summary>
internal sealed class CoalescingCacheWriter<TValue> : IDisposable
{
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(5);
    private readonly AtomicDocumentStore<Dictionary<ulong, TValue>> store;
    private readonly SemaphoreSlim writeGate;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<(long Revision, TaskCompletionSource Completion)> waiters = [];
    private IReadOnlyDictionary<ulong, TValue>? pending;
    private long pendingRevision;
    private Channel<bool>? signals;
    private Task? worker;
    private bool disposed;

    public CoalescingCacheWriter(AtomicDocumentStore<Dictionary<ulong, TValue>> store, SemaphoreSlim writeGate)
    {
        this.store = store;
        this.writeGate = writeGate;
    }

    public event Action<CacheWriteFailure>? WriteFailed;

    /// <summary>
    /// Queues the snapshot. The returned task completes when this revision (or a later
    /// one) is durable and faults if that write fails; callers may ignore it.
    /// </summary>
    public Task Enqueue(IReadOnlyDictionary<ulong, TValue> snapshot, long revision)
    {
        lock (gate)
        {
            if (!disposed)
            {
                pending = snapshot;
                pendingRevision = revision;
                // Continuations must not run on the worker while it holds the write gate.
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((revision, completion));
                EnsureWorker();
                signals!.Writer.TryWrite(true);
                return completion.Task;
            }
        }

        // Mutations that arrive during teardown still land, synchronously.
        writeGate.Wait();
        try
        {
            WriteOrReport(snapshot, revision, attempt: 1, retryAllowed: false, out var failure);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>Drops any queued snapshot. Callers must hold the write gate.</summary>
    public void DiscardPending()
    {
        lock (gate)
            pending = null;
    }

    /// <summary>Acknowledges every waiter at or before a revision another write made durable.</summary>
    public void MarkPersisted(long revision) => Resolve(revision, failure: null);

    /// <summary>Faults every waiter at or before a revision whose write failed.</summary>
    public void MarkFailed(long revision, Exception failure) => Resolve(revision, failure);

    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            shutdown.Cancel();
            signals?.Writer.TryComplete();
            running = worker;
        }

        if (running is null || running.Wait(DisposeFlushTimeout))
        {
            shutdown.Dispose();
            // Nothing should remain once the worker drained; never leave a waiter hanging.
            Resolve(long.MaxValue, new InvalidOperationException("The cache writer shut down before this snapshot was persisted."));
            return;
        }

        IReadOnlyDictionary<ulong, TValue>? unwritten;
        long revision;
        lock (gate)
        {
            unwritten = pending;
            revision = pendingRevision;
        }
        var timeout = new TimeoutException(
            $"Cache write did not finish within {DisposeFlushTimeout.TotalSeconds:0}s of shutdown; the latest snapshot may not be durable.");
        Resolve(long.MaxValue, timeout);
        WriteFailed?.Invoke(new(store.Path, revision, unwritten?.Count ?? -1, 0, false, timeout));
    }

    private void Resolve(long revision, Exception? failure)
    {
        List<TaskCompletionSource> resolved;
        lock (gate)
        {
            resolved = waiters.Where(waiter => waiter.Revision <= revision).Select(waiter => waiter.Completion).ToList();
            waiters.RemoveAll(waiter => waiter.Revision <= revision);
        }
        foreach (var completion in resolved)
        {
            if (failure is null)
                completion.TrySetResult();
            else
                completion.TrySetException(failure);
        }
    }

    private void EnsureWorker()
    {
        if (worker is not null)
            return;
        signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        worker = Task.Run(ProcessWritesAsync);
    }

    private async Task ProcessWritesAsync()
    {
        var token = shutdown.Token;
        await foreach (var signal in signals!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _ = signal;
            while (signals.Reader.TryRead(out _)) { }
            var retryDelay = TimeSpan.FromMilliseconds(50);
            for (var attempt = 1; ; attempt++)
            {
                var retry = false;
                await writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    IReadOnlyDictionary<ulong, TValue>? snapshot;
                    long revision;
                    bool stopping;
                    lock (gate)
                    {
                        snapshot = pending;
                        revision = pendingRevision;
                        pending = null;
                        stopping = disposed;
                    }
                    if (snapshot is not null && WriteOrReport(snapshot, revision, attempt, retryAllowed: !stopping, out _))
                    {
                        retry = true;
                        lock (gate)
                        {
                            // A snapshot queued meanwhile is newer and supersedes the failed one.
                            if (pending is null)
                            {
                                pending = snapshot;
                                pendingRevision = revision;
                            }
                        }
                    }
                }
                finally
                {
                    writeGate.Release();
                }

                if (!retry)
                    break;
                try
                {
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Disposal immediately advances to one final write attempt.
                }
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(2000, retryDelay.TotalMilliseconds * 2));
            }
        }
    }

    /// <summary>
    /// Writes the snapshot and resolves its waiters; returns true when a failed write
    /// should be retried. Waiters fail on the first failed attempt so a durability
    /// acknowledgment is never held open by an unbounded retry loop.
    /// </summary>
    private bool WriteOrReport(
        IReadOnlyDictionary<ulong, TValue> snapshot,
        long revision,
        int attempt,
        bool retryAllowed,
        out Exception? failure)
    {
        try
        {
            store.Save(snapshot.ToDictionary(entry => entry.Key, entry => entry.Value));
            failure = null;
            MarkPersisted(revision);
            return false;
        }
        catch (Exception exception)
        {
            // Only filesystem failures are worth retrying; anything else is reported
            // once and the next mutation hands off a fresh snapshot.
            failure = exception;
            var retry = retryAllowed && exception is IOException or UnauthorizedAccessException;
            MarkFailed(revision, exception);
            WriteFailed?.Invoke(new(store.Path, revision, snapshot.Count, attempt, retry, exception));
            return retry;
        }
    }
}

public sealed record RetainerListingPersistenceReceipt(
    ulong RetainerId,
    DateTime ObservedAtUtc,
    DateTime PersistedAtUtc,
    double WriteMilliseconds);

public sealed class RetainerCacheStore : IDisposable
{
    private readonly AtomicDocumentStore<Dictionary<ulong, CachedRetainer>> store;
    private readonly AtomicDocumentStore<Dictionary<ulong, RetainerListingCheckpoint>> listingStore;
    private readonly object listingGate = new();
    private readonly SemaphoreSlim listingWriteGate = new(1, 1);
    private readonly CancellationTokenSource listingShutdown = new();
    private readonly CoalescingCacheWriter<CachedRetainer> cacheWriter;
    private Dictionary<ulong, RetainerListingCheckpoint> listingCheckpoints;
    private Channel<bool>? listingSignals;
    private Task? listingWorker;
    private bool disposed;

    public event Action<Exception>? ListingWriteFailed;
    public event Action<RetainerListingPersistenceReceipt>? ListingPersisted;

    public event Action<CacheWriteFailure>? CacheWriteFailed;

    public RetainerCacheStore(string path)
    {
        store = new(path, AtomicDocumentStore<Dictionary<ulong, CachedRetainer>>.CompactJsonOptions);
        listingStore = new(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(store.Path)!,
            "retainer-listings-cache.json"));
        listingCheckpoints = listingStore.Load();
        cacheWriter = new(store, listingWriteGate);
        cacheWriter.WriteFailed += failure => CacheWriteFailed?.Invoke(failure);
    }

    public string Path => store.Path;
    public bool Exists => store.Exists;
    public Dictionary<ulong, CachedRetainer> Load()
    {
        var cache = store.Load();
        lock (listingGate)
        {
            foreach (var checkpoint in listingCheckpoints.Values)
            {
                var current = cache.GetValueOrDefault(checkpoint.RetainerId);
                if (current is not null && current.ListingsObservedAtUtc > checkpoint.ObservedAtUtc)
                    continue;
                current ??= new CachedRetainer { RetainerId = checkpoint.RetainerId };
                current.RetainerName = checkpoint.RetainerName;
                current.Owner = checkpoint.Owner with { };
                current.ListingsObservedAtUtc = checkpoint.ObservedAtUtc;
                current.Listings = checkpoint.Listings.Select(Copy).ToList();
                var marketSource = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket.ToString();
                if (!current.RequestedSources.Contains(marketSource, StringComparer.Ordinal))
                    current.RequestedSources.Add(marketSource);
                if (!current.ObservedSources.Contains(marketSource, StringComparer.Ordinal))
                    current.ObservedSources.Add(marketSource);
                cache[checkpoint.RetainerId] = current;
            }
        }
        return cache;
    }
    public void Save(IReadOnlyDictionary<ulong, CachedRetainer> cache) => store.Save(cache.ToDictionary(entry => entry.Key, entry => entry.Value));

    /// <summary>
    /// Hands the snapshot to the background writer; the latest snapshot wins. The
    /// dictionary and its retainers must not be mutated after this call.
    /// </summary>
    public Task SaveInBackground(IReadOnlyDictionary<ulong, CachedRetainer> cache, long revision) =>
        cacheWriter.Enqueue(cache, revision);

    public void SaveListing(CachedRetainer retainer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var checkpoint = new RetainerListingCheckpoint
        {
            RetainerId = retainer.RetainerId,
            RetainerName = retainer.RetainerName,
            Owner = retainer.Owner with { },
            ObservedAtUtc = retainer.ListingsObservedAtUtc,
            Listings = retainer.Listings.Select(Copy).ToList(),
        };
        lock (listingGate)
        {
            listingCheckpoints[retainer.RetainerId] = checkpoint;
            EnsureListingWorker();
            listingSignals!.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Synchronously rewrites the cache after an invalidation. <paramref name="revision"/> is the
    /// repository revision <paramref name="cache"/> reflects; it supersedes queued snapshots.
    /// </summary>
    public void SaveAfterInvalidation(IReadOnlyDictionary<ulong, CachedRetainer> cache, long revision)
    {
        listingWriteGate.Wait();
        try
        {
            // This rewrite supersedes any queued snapshot, which may predate the invalidation.
            cacheWriter.DiscardPending();
            try
            {
                File.Delete(Path);
                if (cache.Count > 0)
                    Save(cache);
            }
            catch (Exception exception)
            {
                cacheWriter.MarkFailed(revision, exception);
                throw;
            }
            cacheWriter.MarkPersisted(revision);
            Dictionary<ulong, RetainerListingCheckpoint>? listingSnapshot = null;
            lock (listingGate)
            {
                var removed = listingCheckpoints.Keys.Where(retainerId => !cache.ContainsKey(retainerId)).ToArray();
                foreach (var retainerId in removed)
                    listingCheckpoints.Remove(retainerId);
                if (removed.Length > 0)
                    listingSnapshot = listingCheckpoints.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            }
            if (listingSnapshot is not null)
                listingStore.Save(listingSnapshot);
        }
        finally
        {
            listingWriteGate.Release();
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (listingGate)
        {
            if (disposed)
                return;
            disposed = true;
            listingShutdown.Cancel();
            listingSignals?.Writer.TryComplete();
            worker = listingWorker;
        }
        cacheWriter.Dispose();
        worker?.GetAwaiter().GetResult();
        listingShutdown.Dispose();
    }

    private void EnsureListingWorker()
    {
        if (listingWorker is not null)
            return;
        listingSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        listingWorker = Task.Run(ProcessListingWritesAsync);
    }

    private async Task ProcessListingWritesAsync()
    {
        var persisted = new Dictionary<ulong, DateTime?>();
        await foreach (var signal in listingSignals!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _ = signal;
            while (listingSignals.Reader.TryRead(out _)) { }
            var retryDelay = TimeSpan.FromMilliseconds(50);
            while (true)
            {
                Exception? failure = null;
                var receipts = new List<RetainerListingPersistenceReceipt>();
                await listingWriteGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    Dictionary<ulong, RetainerListingCheckpoint> snapshot;
                    lock (listingGate)
                        snapshot = listingCheckpoints.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
                    var started = Stopwatch.GetTimestamp();
                    listingStore.Save(snapshot);
                    var writeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var persistedAtUtc = DateTime.UtcNow;
                    foreach (var checkpoint in snapshot.Values.Where(checkpoint =>
                                 checkpoint.ObservedAtUtc.HasValue &&
                                 (!persisted.TryGetValue(checkpoint.RetainerId, out var previous) || checkpoint.ObservedAtUtc > previous)))
                    {
                        persisted[checkpoint.RetainerId] = checkpoint.ObservedAtUtc;
                        receipts.Add(new(
                            checkpoint.RetainerId,
                            checkpoint.ObservedAtUtc!.Value,
                            persistedAtUtc,
                            writeMilliseconds));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    failure = exception;
                }
                finally
                {
                    listingWriteGate.Release();
                }

                if (failure is null)
                {
                    foreach (var receipt in receipts)
                        ListingPersisted?.Invoke(receipt);
                    break;
                }

                ListingWriteFailed?.Invoke(failure);
                bool stopping;
                lock (listingGate)
                    stopping = disposed;
                if (stopping)
                    break;
                try
                {
                    await Task.Delay(retryDelay, listingShutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (listingShutdown.IsCancellationRequested)
                {
                    // Disposal immediately advances to one final write attempt.
                }
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(2000, retryDelay.TotalMilliseconds * 2));
            }
        }
    }

    private static RetainerListingCheckpoint Copy(RetainerListingCheckpoint source) => new()
    {
        RetainerId = source.RetainerId,
        RetainerName = source.RetainerName,
        Owner = source.Owner with { },
        ObservedAtUtc = source.ObservedAtUtc,
        Listings = source.Listings.Select(Copy).ToList(),
    };

    private static CachedMarketListing Copy(CachedMarketListing source) => new()
    {
        ItemId = source.ItemId,
        ItemName = source.ItemName,
        ItemType = source.ItemType,
        Quantity = source.Quantity,
        IsHq = source.IsHq,
        Condition = source.Condition,
        ConditionPercent = source.ConditionPercent,
        ContainerKey = source.ContainerKey,
        SlotIndex = source.SlotIndex,
        UnitPrice = source.UnitPrice,
        ListedAtUtc = source.ListedAtUtc,
    };

    private sealed class RetainerListingCheckpoint
    {
        public RetainerListingCheckpoint() { }

        public ulong RetainerId { get; set; }
        public string RetainerName { get; set; } = string.Empty;
        public OwnerScope Owner { get; set; } = new();
        public DateTime? ObservedAtUtc { get; set; }
        public List<CachedMarketListing> Listings { get; set; } = [];
    }
}

public sealed class PlayerInventoryCacheStore : IDisposable
{
    private readonly AtomicDocumentStore<Dictionary<ulong, CachedPlayerInventory>> store;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CoalescingCacheWriter<CachedPlayerInventory> writer;

    public PlayerInventoryCacheStore(string path)
    {
        store = new(path, AtomicDocumentStore<Dictionary<ulong, CachedPlayerInventory>>.CompactJsonOptions);
        writer = new(store, writeGate);
        writer.WriteFailed += failure => WriteFailed?.Invoke(failure);
    }

    public event Action<CacheWriteFailure>? WriteFailed;

    public string Path => store.Path;
    public bool Exists => store.Exists;
    public Dictionary<ulong, CachedPlayerInventory> Load() => store.Load();
    public void Save(IReadOnlyDictionary<ulong, CachedPlayerInventory> cache) =>
        store.Save(cache.ToDictionary(entry => entry.Key, entry => entry.Value));

    /// <summary>
    /// Hands the snapshot to the background writer; the latest snapshot wins. The
    /// dictionary and its entries must not be mutated after this call.
    /// </summary>
    public Task SaveInBackground(IReadOnlyDictionary<ulong, CachedPlayerInventory> cache, long revision) =>
        writer.Enqueue(cache, revision);

    public void Dispose() => writer.Dispose();
}

public sealed class QuartermasterStateStore
{
    private readonly AtomicDocumentStore<QuartermasterState> store;

    public QuartermasterStateStore(string path) => store = new(path);

    public string Path => store.Path;
    public bool Exists => store.Exists;
    public QuartermasterState Load() => store.Load();
    public void Save(QuartermasterState state) => store.Save(state);
}
