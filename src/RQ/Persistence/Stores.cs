using System.Text.Json;
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

public sealed class RetainerCacheStore
{
    private readonly AtomicDocumentStore<Dictionary<ulong, CachedRetainer>> store;

    public RetainerCacheStore(string path) => store = new(path);

    public string Path => store.Path;
    public bool Exists => store.Exists;
    public Dictionary<ulong, CachedRetainer> Load() => store.Load();
    public void Save(IReadOnlyDictionary<ulong, CachedRetainer> cache) => store.Save(cache.ToDictionary(entry => entry.Key, entry => entry.Value));

    public void SaveAfterInvalidation(IReadOnlyDictionary<ulong, CachedRetainer> cache)
    {
        File.Delete(Path);
        if (cache.Count > 0)
            Save(cache);
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
