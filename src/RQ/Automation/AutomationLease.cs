namespace RQ.Automation;

public sealed class AutomationLease
{
    private readonly object gate = new();
    private Guid activeToken;

    public string? Holder { get; private set; }

    public bool IsHeld
    {
        get
        {
            lock (gate)
                return Holder is not null;
        }
    }

    public bool TryAcquire(string holder, out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        lock (gate)
        {
            if (Holder is not null)
            {
                lease = null;
                return false;
            }

            activeToken = Guid.NewGuid();
            Holder = holder;
            lease = new Releaser(this, activeToken);
            return true;
        }
    }

    private void Release(Guid token)
    {
        lock (gate)
        {
            if (token != activeToken)
                return;
            Holder = null;
            activeToken = Guid.Empty;
        }
    }

    private sealed class Releaser(AutomationLease owner, Guid token) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Release(token);
        }
    }
}
