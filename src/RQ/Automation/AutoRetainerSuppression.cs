using Franthropy.Dalamud.Automation.Retainers;

namespace RQ.Automation;

/// <summary>
/// Ref-counted in-process ownership of AutoRetainer suppression. The first
/// acquirer suppresses AutoRetainer unless suppression is already owned by
/// someone else, and only the last release restores suppression this scope
/// actually set, so nested queue/coordinator scopes cannot unsuppress while a
/// transfer is still moving or clear a foreign owner's suppression.
/// </summary>
public sealed class AutoRetainerSuppression
{
    private readonly IAutoRetainerIpc ipc;
    private readonly object gate = new();
    private int holders;
    private bool restoreOnLastRelease;

    public AutoRetainerSuppression(IAutoRetainerIpc ipc) => this.ipc = ipc;

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
                    restoreOnLastRelease = true;
                }
            }
            holders++;
            return new Scope(this);
        }
    }

    private void Release(Scope scope)
    {
        lock (gate)
        {
            holders--;
            if (holders != 0 || !restoreOnLastRelease)
                return;
            restoreOnLastRelease = false;
            try
            {
                ipc.SetSuppressed(false);
            }
            catch (Exception exception)
            {
                scope.RestoreFailure = exception.Message;
            }
        }
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
