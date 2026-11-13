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
/// (and discard queued snapshots) against the worker.
/// </summary>
internal sealed class CoalescingCacheWriter<TValue> : IDisposable
{
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(5);
    private readonly AtomicDocumentStore<Dictionary<ulong, TValue>> store;
    private readonly SemaphoreSlim writeGate;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
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

    public void Enqueue(IReadOnlyDictionary<ulong, TValue> snapshot, long revision)
    {
        lock (gate)
        {
            if (!disposed)
            {
                pending = snapshot;
                pendingRevision = revision;
                EnsureWorker();
                signals!.Writer.TryWrite(true);
                return;
            }
        }

        // Mutations that arrive during teardown still land, synchronously.
        writeGate.Wait();
        try
        {
            WriteOrReport(snapshot, revision, attempt: 1, retryAllowed: false);
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
            return;
        }

        IReadOnlyDictionary<ulong, TValue>? unwritten;
        long revision;
        lock (gate)
        {
            unwritten = pending;
            revision = pendingRevision;
        }
        WriteFailed?.Invoke(new(
            store.Path,
            revision,
            unwritten?.Count ?? -1,
            0,
            false,
            new TimeoutException(
                $"Cache write did not finish within {DisposeFlushTimeout.TotalSeconds:0}s of shutdown; the latest snapshot may not be durable.")));
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
                    if (snapshot is not null && WriteOrReport(snapshot, revision, attempt, retryAllowed: !stopping))
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

    /// <summary>Writes the snapshot; returns true when a failed write should be retried.</summary>
    private bool WriteOrReport(IReadOnlyDictionary<ulong, TValue> snapshot, long revision, int attempt, bool retryAllowed)
    {
        try
        {
            store.Save(snapshot.ToDictionary(entry => entry.Key, entry => entry.Value));
            return false;
        }
        catch (Exception exception)
        {
            // Only filesystem failures are worth retrying; anything else is reported
            // once and the next mutation hands off a fresh snapshot.
            var retry = retryAllowed && exception is IOException or UnauthorizedAccessException;
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
    public void SaveInBackground(IReadOnlyDictionary<ulong, CachedRetainer> cache, long revision) =>
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

    public void SaveAfterInvalidation(IReadOnlyDictionary<ulong, CachedRetainer> cache)
    {
        listingWriteGate.Wait();
        try
        {
            // This rewrite supersedes any queued snapshot, which may predate the invalidation.
            cacheWriter.DiscardPending();
            File.Delete(Path);
            if (cache.Count > 0)
                Save(cache);
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
    public void SaveInBackground(IReadOnlyDictionary<ulong, CachedPlayerInventory> cache, long revision) =>
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
