using Franthropy.Dalamud.Automation.Retainers;

namespace RQ.Automation;

/// <summary>
/// Ref-counted in-process ownership of AutoRetainer suppression. The first
/// acquirer suppresses AutoRetainer unless suppression is already owned by
/// someone else, and only the last release restores suppression this scope
/// actually set, so nested queue/coordinator scopes cannot unsuppress while a
/// transfer is still moving or clear a foreign owner's suppression.
/// When a marker path is given, ownership is also recorded on disk while held,
/// so a load that died holding it is reclaimed by the next load
/// (<see cref="ReclaimOrphaned"/>) instead of leaving AutoRetainer suppressed.
/// When a holder registry is given (Dalamud shared data under <see cref="HolderRegistryKey"/>, shared with the
/// other Gooseworks plugins), this plugin's owner name is in it while it holds, and AutoRetainer is unsuppressed
/// only once no owner name is left: a plugin never lifts a suppression another one still needs.
/// </summary>
public sealed class AutoRetainerSuppression
{
    /// <summary>Dalamud shared-data key of the cross-plugin holder registry: a <see cref="HashSet{T}"/> of owner names, locked on itself.</summary>
    public const string HolderRegistryKey = "Gooseworks.AutoRetainerSuppression.Holders";
    /// <summary>This plugin's owner name in the holder registry.</summary>
    public const string OwnerName = "Quartermaster";

    private readonly IAutoRetainerIpc ipc;
    private readonly string? markerPath;
    private readonly Action<string>? warn;
    private readonly Action<string>? info;
    private readonly HashSet<string>? registry;
    private readonly object gate = new();
    private int holders;
    private bool weOwnSuppression;
    private bool orphanChecked;
    private bool deferredToHolderLogged;
    // Warned once per hold: a plugin or the user lifting the suppression repeatedly must not log every check.
    private bool reassertWarned;
    private bool reassertFailureWarned;

    public AutoRetainerSuppression(IAutoRetainerIpc ipc, string? markerPath = null, Action<string>? warn = null, HashSet<string>? holderRegistry = null,
        Action<string>? info = null)
    {
        this.ipc = ipc;
        this.markerPath = markerPath;
        this.warn = warn;
        this.info = info ?? warn;
        registry = holderRegistry;
        // A new load holds nothing: an entry under this plugin's name is a dead load's leftover.
        if (RegistryRemove())
            warn?.Invoke($"[Quartermaster] AutoRetainerHolderStale: removed '{OwnerName}' from the AutoRetainer holder registry, left by an earlier Quartermaster load.");
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return ipc.IsAvailable;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            try
            {
                return ipc.IsBusy;
            }
            catch
            {
                return false;
            }
        }
    }

    public Scope Acquire()
    {
        lock (gate)
        {
            if (holders == 0)
            {
                reassertWarned = reassertFailureWarned = false;
                // Another Gooseworks plugin already holding it makes the suppression shared: the last holder out lifts it.
                var shared = RegistryAdd();
                if (!ipc.IsSuppressed)
                {
                    ipc.SetSuppressed(true);
                    weOwnSuppression = true;
                    WriteMarker();
                }
                else if (shared)
                {
                    weOwnSuppression = true;
                    WriteMarker();
                }
            }
            else
            {
                ReassertLocked();
            }
            holders++;
            return new Scope(this);
        }
    }

    /// <summary>
    /// While a scope is held, suppresses AutoRetainer again if another plugin (or the user) lifted it mid-hold.
    /// Cheap enough for a per-second tick: one IPC read while held, nothing otherwise. Warns once per hold.
    /// </summary>
    public void Reassert()
    {
        lock (gate)
        {
            if (holders > 0)
                ReassertLocked();
        }
    }

    private void ReassertLocked()
    {
        try
        {
            if (!ipc.IsAvailable || ipc.IsSuppressed)
                return;
            ipc.SetSuppressed(true);
            weOwnSuppression = true;
            WriteMarker();
            if (!reassertWarned)
            {
                reassertWarned = true;
                warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReasserted: AutoRetainer was unsuppressed while {holders} Quartermaster scope(s) still needed it suppressed; suppressed again (warned once per hold).");
            }
        }
        catch (Exception exception)
        {
            if (!reassertFailureWarned)
            {
                reassertFailureWarned = true;
                warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReassertFailed: {exception.Message} (warned once per hold; retried every check).");
            }
        }
    }

    /// <summary>
    /// Load-time reclaim: with no scope held here and a marker an earlier load left (it died holding suppression it
    /// set), AutoRetainer is unsuppressed and the marker removed. While another plugin's owner name is in the holder
    /// registry the reclaim waits (that holder may have found AutoRetainer already suppressed and so never owned it, so
    /// it will not lift it) and runs once the registry has no other owner. Returns true once settled (nothing to do,
    /// reclaimed, or a scope is held so the marker is ours); false while AutoRetainer is not reachable yet, another
    /// holder is registered, or a scope is held, so the caller retries. Without a registry no other holder can be
    /// seen, so the reclaim lifts the suppression outright.
    /// </summary>
    public bool ReclaimOrphaned()
    {
        lock (gate)
        {
            if (orphanChecked)
                return true;
            if (markerPath is null || weOwnSuppression || !File.Exists(markerPath))
            {
                orphanChecked = true;
                return true;
            }
            // A scope held here keeps AutoRetainer suppressed anyway; reclaim once it is released.
            if (holders > 0)
                return false;
            try
            {
                var since = ReadMarker();
                // Another holder may have joined after the dead load's suppression and so never took ownership of it
                // (it found AutoRetainer already suppressed with no registered holder): keep the marker and retry, and
                // reclaim once that holder is gone, or nobody would ever lift it.
                if (OtherHolders() is { Length: > 0 } others)
                {
                    if (!deferredToHolderLogged)
                    {
                        deferredToHolderLogged = true;
                        info?.Invoke($"[Quartermaster] AutoRetainerSuppressionReclaimDeferred: an earlier Quartermaster load suppressed AutoRetainer{(since is null ? "" : $" at {since}")} and never restored it; " +
                            $"{string.Join(", ", others)} hold(s) it now, so it is reclaimed once they release (logged once per load).");
                    }
                    return false;
                }
                if (!ipc.IsAvailable)
                    return false;
                if (ipc.IsSuppressed)
                    ipc.SetSuppressed(false);
                DeleteMarker();
                orphanChecked = true;
                warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReclaimed: an earlier Quartermaster load suppressed AutoRetainer{(since is null ? "" : $" at {since}")} and never restored it; unsuppressed now" +
                    (registry is null ? " (holder registry unavailable: another plugin's suppression could not be checked and may have been lifted)." : "."));
                return true;
            }
            catch (Exception exception)
            {
                warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReclaimFailed: {exception.Message}; retrying.");
                return false;
            }
        }
    }

    /// <summary>
    /// Unload: restores suppression this load set even while scopes are still held (their owners are being torn
    /// down), so a throw elsewhere in the unload never leaves AutoRetainer suppressed. Another holder in the registry
    /// keeps it suppressed. The marker is removed only once the restore succeeded (or was left to another holder);
    /// otherwise the next load reclaims it.
    /// </summary>
    public void RestoreOnUnload()
    {
        lock (gate)
        {
            RegistryRemove();
            if (!weOwnSuppression)
                return;
            try
            {
                if (OtherHolders() is { Length: > 0 } others)
                    LeftToOthers(others, "unload");
                else
                    ipc.SetSuppressed(false);
                weOwnSuppression = false;
                DeleteMarker();
            }
            catch (Exception exception)
            {
                warn?.Invoke($"[Quartermaster] AutoRetainerRestoreOnUnloadFailed: {exception.Message}; the next load unsuppresses it.");
            }
        }
    }

    private void Release(Scope scope)
    {
        lock (gate)
        {
            holders--;
            if (holders != 0)
                return;
            RegistryRemove();
            if (!weOwnSuppression)
                return;
            try
            {
                // Another Gooseworks plugin still holds it: left suppressed; that plugin lifts it when it is done.
                if (OtherHolders() is { Length: > 0 } others)
                    LeftToOthers(others, "release");
                else
                    ipc.SetSuppressed(false);
                weOwnSuppression = false;
                DeleteMarker();
            }
            catch (Exception exception)
            {
                // Ownership is retained so the next scope lifecycle retries the
                // restore instead of mistaking our own suppression for a
                // foreign owner's and abandoning it.
                scope.RestoreFailure = exception.Message;
            }
        }
    }

    private void LeftToOthers(IEnumerable<string> others, string when) =>
        info?.Invoke($"[Quartermaster] AutoRetainerSuppressionLeftToOthers: on {when}, left AutoRetainer suppressed for {string.Join(", ", others)}; that holder lifts it when it is done.");

    /// <summary>Adds this plugin's owner name; true when another owner name was already present.</summary>
    private bool RegistryAdd()
    {
        if (registry is null)
            return false;
        lock (registry)
        {
            var others = registry.Any(name => !string.Equals(name, OwnerName, StringComparison.Ordinal));
            registry.Add(OwnerName);
            return others;
        }
    }

    /// <summary>Removes this plugin's owner name; true when it was present.</summary>
    private bool RegistryRemove()
    {
        if (registry is null)
            return false;
        lock (registry)
            return registry.Remove(OwnerName);
    }

    /// <summary>Owner names in the registry other than this plugin's; empty without a registry.</summary>
    private string[] OtherHolders()
    {
        if (registry is null)
            return [];
        lock (registry)
            return [.. registry.Where(name => !string.Equals(name, OwnerName, StringComparison.Ordinal))];
    }

    private void WriteMarker()
    {
        if (markerPath is null)
            return;
        try { File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O")); }
        catch (Exception exception) { warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionMarkerUnwritten: {exception.Message}"); }
    }

    private void DeleteMarker()
    {
        if (markerPath is null)
            return;
        try { File.Delete(markerPath); }
        catch (Exception exception) { warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionMarkerUndeleted: {exception.Message}"); }
    }

    private string? ReadMarker()
    {
        try { return markerPath is null ? null : File.ReadAllText(markerPath).Trim(); }
        catch { return null; }
    }

    public sealed class Scope : IDisposable
    {
        private AutoRetainerSuppression? owner;

        internal Scope(AutoRetainerSuppression owner) => this.owner = owner;

        public string? RestoreFailure { get; internal set; }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Release(this);
        }
    }
}
