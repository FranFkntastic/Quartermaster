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
/// </summary>
public sealed class AutoRetainerSuppression
{
    private readonly IAutoRetainerIpc ipc;
    private readonly string? markerPath;
    private readonly Action<string>? warn;
    private readonly object gate = new();
    private int holders;
    private bool weOwnSuppression;
    private bool orphanChecked;

    public AutoRetainerSuppression(IAutoRetainerIpc ipc, string? markerPath = null, Action<string>? warn = null)
    {
        this.ipc = ipc;
        this.markerPath = markerPath;
        this.warn = warn;
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
                if (!ipc.IsSuppressed)
                {
                    ipc.SetSuppressed(true);
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
    /// Cheap enough for a per-second tick: one IPC read while held, nothing otherwise.
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
            warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReasserted: AutoRetainer was unsuppressed while {holders} Quartermaster scope(s) still needed it suppressed; suppressed again.");
        }
        catch (Exception exception)
        {
            warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReassertFailed: {exception.Message}");
        }
    }

    /// <summary>
    /// Load-time reclaim: with no scope held here and a marker an earlier load left (it died holding suppression it
    /// set), AutoRetainer is unsuppressed and the marker removed. Returns true once settled (nothing to do, reclaimed,
    /// or a scope is held so the marker is ours); false while AutoRetainer is not reachable yet, so the caller retries.
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
                if (!ipc.IsAvailable)
                    return false;
                var since = ReadMarker();
                if (ipc.IsSuppressed)
                    ipc.SetSuppressed(false);
                DeleteMarker();
                orphanChecked = true;
                warn?.Invoke($"[Quartermaster] AutoRetainerSuppressionReclaimed: an earlier Quartermaster load suppressed AutoRetainer{(since is null ? "" : $" at {since}")} and never restored it; unsuppressed now.");
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
    /// down), so a throw elsewhere in the unload never leaves AutoRetainer suppressed. The marker is removed only
    /// once the restore succeeded; otherwise the next load reclaims it.
    /// </summary>
    public void RestoreOnUnload()
    {
        lock (gate)
        {
            if (!weOwnSuppression)
                return;
            try
            {
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
            if (holders != 0 || !weOwnSuppression)
                return;
            try
            {
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
