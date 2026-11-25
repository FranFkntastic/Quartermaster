using RQ.Automation;

namespace RQ.Courier;

/// <summary>
/// Waits a bounded time for AutoRetainer and Lifestream to go idle before a courier step moves the character or
/// trades. AutoRetainer's busy flag also reads true while Lifestream travels, so Lifestream is probed on its own to
/// name the plugin that is actually still working when the wait runs out.
/// </summary>
internal static class CourierBusyGate
{
    /// <summary>Matches the retainer refresh's AutoRetainer budget.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    /// <summary>
    /// Before a run's first step nothing is dispatched and no peer deadline runs, so the owner waits longer for a
    /// login or travel to finish; a stop still ends the wait at once.
    /// </summary>
    internal static readonly TimeSpan RunStartBudget = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The wait a command can afford: what its deadline at <paramref name="expires"/> leaves once <paramref name="reserve"/>
    /// is kept for the command's own calls, never less than <see cref="Budget"/> (nor more than the deadline leaves).
    /// </summary>
    internal static TimeSpan Within(DateTimeOffset expires, TimeSpan reserve)
    {
        var remaining = expires - DateTimeOffset.UtcNow;
        var spare = remaining - reserve;
        return spare > Budget ? spare : remaining < Budget ? remaining : Budget;
    }

    /// <summary>
    /// Null once both are idle. Otherwise, when the budget runs out or <paramref name="cancelled"/> turns true,
    /// the refusal code and detail naming the busy plugin; the caller checks its own stop state first.
    /// A null <paramref name="autoRetainer"/> waits for Lifestream alone.
    /// </summary>
    internal static async Task<(string Code, string Detail)?> WaitIdleAsync(AutoRetainerSuppression? autoRetainer,
        Func<bool>? lifestreamBusy, Func<bool> cancelled, TimeSpan budget)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            var lifestream = Probe(lifestreamBusy);
            if (!lifestream && autoRetainer?.IsBusy != true) return null;
            var waited = DateTime.UtcNow - started;
            if (cancelled() || waited >= budget)
                return lifestream
                    ? ("LifestreamBusy", $"Lifestream still busy after {waited.TotalSeconds:0}s")
                    : ("AutoRetainerBusy", $"AutoRetainer still busy after {waited.TotalSeconds:0}s");
            await Task.Delay(Poll).ConfigureAwait(false);
        }
    }

    // A missing or failing Lifestream IPC is not busy here: AutoRetainer's own flag still covers it.
    private static bool Probe(Func<bool>? lifestreamBusy)
    {
        try { return lifestreamBusy?.Invoke() == true; }
        catch { return false; }
    }
}

/// <summary>
/// The one budget both sides of a hand-off share for the time between the owner sending Receive/Send and the carrier's
/// TradeStarted. The carrier never starts its trade later than <see cref="CarrierSeconds"/> after the command reached it
/// (it refuses instead), and the owner waits for TradeStarted, extending on each carrier heartbeat, up to
/// <see cref="OwnerCapSeconds"/>: strictly later, so the two budgets can never cross.
/// </summary>
internal static class CourierTradeStart
{
    /// <summary>Carrier: command arrival to TradeStarted, at most. Covers the busy wait, hold, bag read and partner sighting.</summary>
    internal const int CarrierSeconds = 165;
    /// <summary>Carrier: what the calls after the busy wait (hold 30, re-acquire 15, bags 15, partner 30) and the trade's opening keep back.</summary>
    internal const int AfterBusySeconds = 30 + 15 + 15 + 30 + 15;
    /// <summary>Owner: the hard cap, past the carrier's own by the link's delivery of the command and of its acknowledgement.</summary>
    internal const int OwnerCapSeconds = CarrierSeconds + 15;
    /// <summary>
    /// Owner: this long with no heartbeat (nor TradeStarted) ends the wait before the cap. Longer than the peer link's
    /// discovery grace (30s) plus its step-start request (5s), which run before the carrier can beat at all.
    /// </summary>
    internal const int QuietSeconds = 45;
    /// <summary>The carrier's heartbeat phase while it waits ahead of TradeStarted; <see cref="CourierProgress.Waiting"/> names on what.</summary>
    internal const string Preparing = "TradePreparing";
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The carrier's heartbeat ahead of TradeStarted: every few seconds reports what it is waiting on. Disposing it (at
/// TradeStarted, or when the command ends) stops it for good; no beat lands after, so it never overwrites TradeStarted.
/// </summary>
internal sealed class CourierTradeStartHeartbeat : IDisposable
{
    private readonly object gate = new();
    private readonly Action<string> beat;
    private Timer? timer;
    private bool disposed;
    private volatile string reason = "CommandQueue";
    internal CourierTradeStartHeartbeat(Action<string> beat)
    {
        this.beat = beat;
        timer = new Timer(_ => Beat(), null, TimeSpan.Zero, CourierTradeStart.HeartbeatInterval);
    }
    internal string Reason { get => reason; set { reason = value; Beat(); } }
    private void Beat()
    {
        lock (gate)
        {
            if (disposed) return;
            try { beat(reason); } catch { /* A heartbeat is advisory; the owner's cap still bounds the wait. */ }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer?.Dispose(); timer = null;
        }
    }
}
