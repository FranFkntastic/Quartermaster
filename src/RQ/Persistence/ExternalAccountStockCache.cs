using System.Diagnostics;
using System.Text.Json;
using RQ.Domain;

namespace RQ.Persistence;

/// <summary>
/// What sibling XIVLauncher profiles' Quartermaster caches last held, merged across profiles.
/// Keys are content IDs (players) and retainer IDs (retainers). Read-only visibility: nothing
/// here is local-account stock.
/// </summary>
public sealed record ExternalAccountSnapshot(
    long Revision,
    IReadOnlyDictionary<ulong, CachedPlayerInventory> PlayerBags,
    IReadOnlyDictionary<ulong, CachedRetainer> Retainers,
    IReadOnlyDictionary<ulong, string> ProfileByCharacter)
{
    public static readonly ExternalAccountSnapshot Empty = new(
        0,
        new Dictionary<ulong, CachedPlayerInventory>(),
        new Dictionary<ulong, CachedRetainer>(),
        new Dictionary<ulong, string>());
}

/// <summary>
/// Reads the player-inventory and retainer caches of sibling XIVLauncher profiles
/// (<c>%APPDATA%\XIVLauncher*</c>) without ever locking them. Polling is demand driven: reading
/// <see cref="Current"/> starts a background stat pass at most every <see cref="PollInterval"/>,
/// and a file is parsed only when its size or write time changed. A file that fails to parse
/// keeps its last good contents.
/// </summary>
public sealed class ExternalAccountStockCache : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const string PlayerCacheFile = "player-inventory-cache.json";
    private const string RetainerCacheFile = "retainer-cache.json";

    private readonly string? appDataRoot;
    private readonly string? ownProfileRoot;
    private readonly string pluginFolder;
    private readonly Action<string>? warn;
    private readonly object gate = new();
    private readonly Dictionary<string, FileEntry> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private ExternalAccountSnapshot current = ExternalAccountSnapshot.Empty;
    private long polledAt;
    private Task? polling;
    private bool disposed;

    /// <param name="pluginConfigDirectory">This client's own plugin config directory, e.g. <c>%APPDATA%\XIVLauncher\pluginConfigs\RQ</c>.</param>
    public ExternalAccountStockCache(string pluginConfigDirectory, Action<string>? warn = null)
    {
        this.warn = warn;
        var full = Path.GetFullPath(pluginConfigDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        pluginFolder = Path.GetFileName(full);
        // …\XIVLauncher\pluginConfigs\RQ → …\XIVLauncher → …\Roaming
        ownProfileRoot = Directory.GetParent(full)?.Parent?.FullName;
        appDataRoot = ownProfileRoot is null ? null : Directory.GetParent(ownProfileRoot)?.FullName;
    }

    /// <summary>
    /// The latest merged snapshot. Cheap enough for every frame: it never touches the disk on the
    /// calling thread, only schedules a background poll when one is due.
    /// </summary>
    public ExternalAccountSnapshot Current
    {
        get
        {
            SchedulePoll();
            return Volatile.Read(ref current);
        }
    }

    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            disposed = true;
            running = polling;
        }
        try { running?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
    }

    private void SchedulePoll()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref polledAt) < (long)(PollInterval.TotalSeconds * Stopwatch.Frequency))
            return;
        lock (gate)
        {
            if (disposed || polling is { IsCompleted: false } || now - polledAt < (long)(PollInterval.TotalSeconds * Stopwatch.Frequency))
                return;
            Volatile.Write(ref polledAt, now);
            polling = Task.Run(Poll);
        }
    }

    private void Poll()
    {
        try
        {
            var changed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (profile, directory) in SiblingConfigDirectories())
            {
                changed |= Refresh(profile, Path.Combine(directory, PlayerCacheFile), FileKind.Player, seen);
                changed |= Refresh(profile, Path.Combine(directory, RetainerCacheFile), FileKind.Retainer, seen);
            }
            foreach (var gone in files.Keys.Where(path => !seen.Contains(path)).ToArray())
            {
                files.Remove(gone);
                changed = true;
            }
            if (changed)
                Volatile.Write(ref current, Merge(Volatile.Read(ref current).Revision + 1));
        }
        catch (Exception exception)
        {
            // Never let a background poll fault unobserved; the last snapshot stays.
            ReportOnce($"poll:{exception.GetType().Name}", $"Quartermaster could not scan other XIVLauncher profiles' caches: {exception.Message}");
        }
    }

    /// <summary>Sibling profiles' plugin config directories that exist, as (profile folder name, directory).</summary>
    private IEnumerable<(string Profile, string Directory)> SiblingConfigDirectories()
    {
        if (appDataRoot is null || ownProfileRoot is null || !Directory.Exists(appDataRoot))
            yield break;
        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(appDataRoot, "XIVLauncher*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        Array.Sort(profiles, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (string.Equals(Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar), ownProfileRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = Path.Combine(profile, "pluginConfigs", pluginFolder);
            if (Directory.Exists(directory))
                yield return (Path.GetFileName(profile), directory);
        }
    }

    /// <summary>Re-reads one cache file when its size or write time moved; returns whether its contents changed.</summary>
    private bool Refresh(string profile, string path, FileKind kind, HashSet<string> seen)
    {
        FileStamp stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;
            stamp = new(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep whatever was read last; the file is still there as far as we know.
            if (files.ContainsKey(path))
                seen.Add(path);
            return false;
        }
        seen.Add(path);
        var previous = files.GetValueOrDefault(path);
        if (previous is not null && previous.Stamp == stamp)
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            FileEntry entry = kind == FileKind.Player
                ? new(profile, stamp, JsonSerializer.Deserialize<Dictionary<ulong, CachedPlayerInventory>>(stream,
                    AtomicDocumentStore<Dictionary<ulong, CachedPlayerInventory>>.JsonOptions) ?? [], null)
                : new(profile, stamp, null, JsonSerializer.Deserialize<Dictionary<ulong, CachedRetainer>>(stream,
                    AtomicDocumentStore<Dictionary<ulong, CachedRetainer>>.JsonOptions) ?? []);
            files[path] = entry;
            return true;
        }
        catch (IOException)
        {
            // Sharing violations and mid-replace reads are transient; try again next poll.
            return false;
        }
        catch (Exception exception) when (exception is JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A torn or unreadable file keeps its last good contents. Remember the stamp so the
            // same bad bytes are not parsed every poll; the next write retries.
            if (previous is not null)
                files[path] = previous with { Stamp = stamp };
            else
                files[path] = new(profile, stamp, null, null);
            ReportOnce($"{path}:{stamp.Length}:{stamp.WrittenUtc.Ticks}",
                $"Quartermaster could not read {profile}'s {Path.GetFileName(path)}; keeping its last good copy. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>Every sibling's characters and retainers; a character or retainer seen in several profiles keeps its newest copy.</summary>
    private ExternalAccountSnapshot Merge(long revision)
    {
        var players = new Dictionary<ulong, CachedPlayerInventory>();
        var retainers = new Dictionary<ulong, CachedRetainer>();
        var profiles = new Dictionary<ulong, string>();
        foreach (var entry in files.Values.OrderBy(entry => entry.Profile, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (id, player) in entry.Players ?? [])
            {
                if (id == 0 || player.Owner.LocalContentId is not > 0)
                    continue;
                if (players.TryGetValue(id, out var existing) && existing.UpdatedAtUtc >= player.UpdatedAtUtc)
                    continue;
                players[id] = player;
                profiles[id] = entry.Profile;
            }
            foreach (var (id, retainer) in entry.Retainers ?? [])
            {
                if (id == 0 || retainer.Owner.LocalContentId is not > 0)
                    continue;
                if (retainers.TryGetValue(id, out var existing) && existing.ObservedAtUtc >= retainer.ObservedAtUtc)
                    continue;
                retainers[id] = retainer;
                profiles.TryAdd(retainer.Owner.LocalContentId!.Value, entry.Profile);
            }
        }
        return new(revision, players, retainers, profiles);
    }

    private void ReportOnce(string key, string message)
    {
        if (warn is null)
            return;
        lock (reported)
        {
            if (!reported.Add(key))
                return;
        }
        warn(message);
    }

    private enum FileKind { Player, Retainer }

    private readonly record struct FileStamp(long Length, DateTime WrittenUtc);

    private sealed record FileEntry(
        string Profile,
        FileStamp Stamp,
        Dictionary<ulong, CachedPlayerInventory>? Players,
        Dictionary<ulong, CachedRetainer>? Retainers);
}
