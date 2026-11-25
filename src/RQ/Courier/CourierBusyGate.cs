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
