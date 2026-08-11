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

    public AtomicDocumentStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public T Load() => Exists ? AtomicJsonFile.Read<T>(Path, JsonOptions) ?? new T() : new T();

    public void Save(T value) => AtomicJsonFile.Write(Path, value, JsonOptions);
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
    private Dictionary<ulong, RetainerListingCheckpoint> listingCheckpoints;
    private Channel<bool>? listingSignals;
    private Task? listingWorker;
    private bool disposed;

    public event Action<Exception>? ListingWriteFailed;
    public event Action<RetainerListingPersistenceReceipt>? ListingPersisted;

    public RetainerCacheStore(string path)
    {
        store = new(path);
        listingStore = new(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(store.Path)!,
            "retainer-listings-cache.json"));
        listingCheckpoints = listingStore.Load();
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
        File.Delete(Path);
        if (cache.Count > 0)
            Save(cache);
        lock (listingGate)
        {
            var removed = listingCheckpoints.Keys.Where(retainerId => !cache.ContainsKey(retainerId)).ToArray();
            foreach (var retainerId in removed)
                listingCheckpoints.Remove(retainerId);
            if (removed.Length > 0)
            {
                EnsureListingWorker();
                listingSignals!.Writer.TryWrite(true);
            }
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
            listingSignals?.Writer.TryComplete();
            worker = listingWorker;
        }
        worker?.GetAwaiter().GetResult();
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
        await foreach (var _ in listingSignals!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Dictionary<ulong, RetainerListingCheckpoint> snapshot;
            lock (listingGate)
                snapshot = listingCheckpoints.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            try
            {
                var started = Stopwatch.GetTimestamp();
                listingStore.Save(snapshot);
                var writeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var persistedAtUtc = DateTime.UtcNow;
                foreach (var checkpoint in snapshot.Values.Where(checkpoint =>
                             checkpoint.ObservedAtUtc.HasValue &&
                             (!persisted.TryGetValue(checkpoint.RetainerId, out var previous) || checkpoint.ObservedAtUtc > previous)))
                {
                    persisted[checkpoint.RetainerId] = checkpoint.ObservedAtUtc;
                    ListingPersisted?.Invoke(new(
                        checkpoint.RetainerId,
                        checkpoint.ObservedAtUtc!.Value,
                        persistedAtUtc,
                        writeMilliseconds));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                ListingWriteFailed?.Invoke(exception);
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

public sealed class PlayerInventoryCacheStore
{
    private readonly AtomicDocumentStore<Dictionary<ulong, CachedPlayerInventory>> store;

    public PlayerInventoryCacheStore(string path) => store = new(path);

    public string Path => store.Path;
    public bool Exists => store.Exists;
    public Dictionary<ulong, CachedPlayerInventory> Load() => store.Load();
    public void Save(IReadOnlyDictionary<ulong, CachedPlayerInventory> cache) =>
        store.Save(cache.ToDictionary(entry => entry.Key, entry => entry.Value));
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
