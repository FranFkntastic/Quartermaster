using System.Collections.Immutable;
using RQ.Courier;

namespace RQ.UI;

/// <summary>One automatic resume that was sent, with the halt that triggered it.</summary>
public sealed record CourierAutoResumeAttempt(int Attempt, DateTimeOffset At, string State, string Code, string Detail);

/// <summary>What the automatic resume is doing for the current run, for the run pane and the courier IPC status.</summary>
/// <param name="Attempt">The next attempt while one is pending; otherwise the attempts made since the run last progressed.</param>
/// <param name="DueInSeconds">Seconds until the pending attempt fires; 0 once it waits for the client to be ready.</param>
/// <param name="Exhausted">The halt that ended the retries once every attempt was spent without progress.</param>
public sealed record CourierAutoResumeView(Guid Run, bool Pending, int Attempt, int MaxAttempts, int DueInSeconds,
    string Code, string Detail, CourierAutoResumeAttempt? Exhausted, ImmutableArray<CourierAutoResumeAttempt> History);

/// <summary>
/// Resumes a halted owner run on its own, through the same <see cref="CourierSession.Resume"/> the run pane's Resume uses:
/// the resume path keeps every custody, ledger and checkpoint check, and its refusal is the real stop. A halt is retried
/// only when it was watched happen in this session, the run can resume, nobody asked it to stop, and its code is not one
/// a retry cannot change. Attempts are bounded and reset whenever the run gets past a step it had not reached before.
/// Driven from the framework thread by <see cref="Tick"/>.
/// </summary>
internal sealed class CourierAutoResume
{
    internal const int MaxAttempts = 3;
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)];
    /// <summary>How long a due attempt waits for the client to be ready (logged in, not switching, run gate free) before giving up.</summary>
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Halt codes a retry cannot change: the resume checks themselves refused, custody or a ledger disagrees, an identity
    /// is wrong, or stock, space or setup the run needs is missing. Each needs a person; none is retried.
    /// </summary>
    internal static readonly ImmutableHashSet<string> DenyList = ImmutableHashSet.Create(StringComparer.Ordinal,
        // The resume path refused: its checks found the journal, the carrier and the bags disagree.
        "ResumeCustodyMismatch",          // the run's custody differs from the carrier's ledger
        "ResumeCheckpointMismatch",       // bags changed since the last verified checkpoint
        "ResumeCarrierUnconfirmed",       // the carrier holds a hand-off this run has no pending step for
        "ResumePendingUnsettled",         // the pending step's outcome cannot be proven from fresh bags
        "ResumeFinishCustodyRemaining",   // Finish is pending while the run still holds custody
        CourierConductor.HandInNeedsSettlement, // a hand-in neither bags nor the carrier's receipt settle
        "PlacementSettlementIdentity",    // placement owner, carrier or custody changed while it was pending
        "PlacementSettlementMismatch",    // bags and the placement journal disagree
        "ConsolidationUnsettled",         // an interrupted consolidation's outcome is unknown
        // Custody, ledger or evidence disagreement during the run.
        "CustodyMismatch",                // carrier identity or custody changed between steps
        "LedgerMismatch",                 // the carrier's confirmation differs from the run's custody
        "HandOffDeltaMismatch",           // a trade's bag deltas differ from its manifest
        "NotDeliveredDeltaMismatch",      // a not-delivered fence found bags that moved
        "NotDeliveredEvidenceMismatch",   // the carrier's not-delivered evidence disagrees
        "ClaimLedgerMismatch",            // the carrier's ledger after a claim differs from the run
        "ReceiptMismatch",                // a reply named the wrong run or step
        "ReturnCustodyUnsettled",         // the carrier reported custody after a failed return
        "CustodyRemaining",               // the carrier still holds stock at Finish
        "CustodyInsufficient",            // a hand-in exceeds what the run holds
        "ScheduleShapeMismatch",          // a hand-in's deposits no longer match its batch
        "SettlementLedgerMismatch",       // the carrier has not acknowledged the settled delta
        "SettlementDeltaMismatch",        // a verified receipt cannot become a partial transfer
        "SettleSentEvidenceMismatch",     // the carrier's send receipt does not show the hand-in
        "SettleSentUnproven",             // the carrier cannot prove its send
        "SettleSentManifestMismatch",     // the send receipt's manifest differs
        "TradeClosureUnproven",           // unchanged bags cannot settle a dispatched trade
        "ReceiveDeltaMismatch",           // the receive's bag delta differs from its manifest
        "AttestedSenderDeltaMismatch",    // the sender's attested delta differs
        "TradeCheckpointMismatch",        // the trade checkpoint disagrees with the step
        "ManifestPartnerMismatch",        // the command's partner is not the manifest's
        "EnvelopeMismatch",               // the command does not match the carrier's run
        "ReceiveAttestationUnavailable",  // this client cannot close its receive checkpoint
        // Identity: the wrong character is involved.
        "CarrierIdentityMismatch",        // a different character is carrying
        "OwnerIdentityMismatch",          // the wrong owner character is logged in for the step
        "SwitchIdentityMismatch",         // a character switch landed on someone else
        "CarrierRunConflict",             // the carrier holds another run
        // Stock, space or setup the run needs is missing.
        "BagSlotLimit",                   // no free slot for the next trip
        "DestinationBagsFull",            // the destination cannot take the next stack
        "DestinationFull",                // no room at the destination
        "SourceGilShort",                 // the source carries too little gil
        "DestinationGilShort",            // the gil did not arrive
        "BagStockShort",                  // the items are no longer in the bags
        "SenderStockShort",               // the sender no longer holds the manifest
        "CarrierStockShort",              // the carrier holds too few of its own items
        "CarrierGilShort",                // the carrier carries too little gil
        "CarrierGilCapExceeded",          // the carrier would pass the gil cap
        "DestinationGilCapExceeded",      // the destination would pass the gil cap
        "CarrierSourceUnsupported",       // the carrier's Quartermaster cannot send its own stock
        "InvalidManifest",                // the carrier refused the manifest
        "OwnerNotPaired",                 // the owner is not paired on the carrier
        "CarrierOptInOff",                // the carrier no longer carries for others
        "NoAttunedAetheryte",             // no attuned aetheryte to travel by
        // Hand-off, settlement and trade-record evidence that does not settle (the pane labels these mismatch/unverified).
        "ManifestDeltaMismatch",          // bag deltas differ from the manifest
        "ManifestDeltaOutOfRange",        // a delta falls outside the manifest's bounds
        "TradeRecordDisagrees",           // the trade record contradicts the bags
        "TradeRecordUnsettled",           // the trade record cannot settle the step
        "TradeRecordMissing",             // no trade record for a dispatched trade
        "TradeSettlementUnproven",        // the trade's settlement cannot be proven
        "TradeManifestUnsettled",         // the traded manifest is not settled
        "UnmanifestedTransfer",           // items moved that no manifest names
        "SettlementIdentityMismatch",     // the settlement names another run, step or character
        "SettlementEvidenceMissing",      // the evidence a settlement needs is gone
        "SettlementUnavailable",          // the settlement cannot be read
        "SettleBagsChanged",              // bags changed while settling
        "ResumeUnsettled",                // the resume could not settle the pending step
        "ReceiveBaselineMismatch",        // the receive baseline differs from the bags
        "ReceiveCancellationUnsettled",   // a cancelled receive's outcome is unknown
        // Carrier refusals over custody it cannot reconcile.
        "UnclaimCustodyChanged",          // the carrier's custody changed before an unclaim
        "UnclaimBagsChanged",             // the carrier's bags changed before an unclaim
        "SettledReceiptChanged",          // a settled receipt no longer matches
        "CarrierIntentUnsettled",         // the carrier's last intent has no proven outcome
        "CarrierTradeUnsettled");         // the carrier's last trade has no proven outcome

    private readonly CourierSession session;
    private readonly object gate = new();
    private Guid run;
    private int highWater;
    private int attempts;
    private bool watching, fired;
    /// <summary>Set by any cancel (an escape hatch, Cancel auto-retry): this run's next halt is not retried until a launch watches it again.</summary>
    private bool suppressed;
    private (DateTimeOffset At, string State, string Code, string Detail)? pending;
    private CourierAutoResumeAttempt? exhausted;
    private ImmutableArray<CourierAutoResumeAttempt> history = [];
    private DateTimeOffset nextTick;
    private volatile bool disposed;

    internal CourierAutoResume(CourierSession session) => this.session = session;

    /// <summary>Whether the auto-resume setting is on; read on every tick.</summary>
    internal Func<bool> Enabled { get; set; } = () => true;
    /// <summary>Whether the owner client could run a resume now: logged in, its character loaded and not switching.</summary>
    internal Func<bool> Ready { get; set; } = () => true;

    private void Warn(string message) => session.Warn?.Invoke($"Courier: {message}");

    /// <summary>The current run's automatic resume state; null while there is nothing to show.</summary>
    internal CourierAutoResumeView? View
    {
        get
        {
            lock (gate)
            {
                if (run == Guid.Empty || pending is null && exhausted is null && history.IsEmpty)
                    return null;
                var now = DateTimeOffset.UtcNow;
                return pending is { } p
                    ? new(run, true, attempts + 1, MaxAttempts, Math.Max(0, (int)Math.Ceiling((p.At - now).TotalSeconds)), p.Code, p.Detail, null, history)
                    : new(run, false, attempts, MaxAttempts, 0, exhausted?.Code ?? "", exhausted?.Detail ?? "", exhausted, history);
            }
        }
    }

    /// <summary>Drops a pending attempt (Stop, Clear, a replacing run, another resume, Cancel auto-retry). Escape hatches never wait on it.</summary>
    internal void Cancel(string why)
    {
        lock (gate)
        {
            // A halt not yet evaluated is dropped too: nothing stopped, cleared or replaced is resumed afterwards. A launch
            // that follows (Resume, a new run) watches again.
            watching = false;
            suppressed = true;
            if (pending is not { } p)
                return;
            pending = null;
            Warn($"{run} AutoResumeCancelled: {why}; attempt {attempts + 1} of {MaxAttempts} after {p.State} {p.Code} will not be sent.");
        }
    }

    /// <summary>A start or resume is launching: its halt, however quick, is watched.</summary>
    internal void Watch()
    {
        lock (gate) { watching = true; suppressed = false; }
    }

    /// <summary>
    /// Whether the automatic attempt being fired is still wanted. Called under the session's launch lock, which every
    /// cancel also takes, so a cancel either lands before this (and the launch is refused) or after the launch exists.
    /// </summary>
    internal bool TryClaim()
    {
        lock (gate) return !disposed && pending is not null && !suppressed;
    }

    /// <summary>Stops every future attempt; called first on unload.</summary>
    internal void Dispose()
    {
        disposed = true;
        lock (gate) pending = null;
    }

    internal void Tick()
    {
        if (disposed || session.Conductor is not { } conductor)
            return;
        var now = DateTimeOffset.UtcNow;
        if (now < nextTick)
            return;
        nextTick = now + TickInterval;
        CourierJournal journal;
        try { journal = conductor.PolledJournal(); }
        catch { return; } // The run pane reports an unreadable journal.
        (string State, string Code, string Detail)? fire = null;
        lock (gate)
        {
            if (journal.Run != run)
            {
                // A watched launch keeps its watch: it is what wrote the new run.
                run = journal.Run; highWater = journal.Cursor; attempts = 0; fired = false; suppressed = false;
                pending = null; exhausted = null; history = [];
            }
            if (journal.Cursor > highWater)
            {
                // A step the run had not reached before completed: the retries start over.
                if (attempts > 0 || exhausted != null)
                    session.Inform?.Invoke($"Courier: {run} AutoResumeReset: the run progressed past step {highWater}; automatic retries start over.");
                highWater = journal.Cursor; attempts = 0; exhausted = null;
            }
            if (session.IsStarting || journal.State == "Running")
            {
                watching = true;
                return;
            }
            if (watching)
            {
                watching = false;
                if (journal.State is "Failed" or "Interrupted" or "Indeterminate")
                    OnHalt(conductor, journal, now);
                else
                {
                    // Completed, refused or cleared: nothing left to retry.
                    attempts = 0; exhausted = null; fired = false;
                }
            }
            if (pending is not { } due)
                return;
            if (!Enabled())
            {
                pending = null;
                Warn($"{run} AutoResumeCancelled: automatic retry was turned off; attempt {attempts + 1} of {MaxAttempts} will not be sent.");
                return;
            }
            if (now < due.At)
                return;
            // Never spend an attempt while the client cannot run it: re-check each tick, within a bounded wait.
            if (!Ready() || conductor.IsBusy || !conductor.PolledSnapshot().CanResume)
            {
                if (now - due.At < ReadyBudget)
                    return;
                pending = null;
                Warn($"{run} AutoResumeAbandoned: attempt {attempts + 1} of {MaxAttempts} waited {ReadyBudget.TotalMinutes:N0} min for " +
                    $"{(!Ready() ? "the character to be ready" : conductor.IsBusy ? "the last run's calls to settle" : "the run to be resumable (carrier link)")}; " +
                    $"left {due.State} {due.Code}: {due.Detail}");
                return;
            }
            fire = (due.State, due.Code, due.Detail);
        }
        Fire(fire.Value, now);
    }

    private void OnHalt(CourierConductor conductor, CourierJournal journal, DateTimeOffset now)
    {
        var (state, code, detail) = (journal.State, journal.Code, journal.Detail);
        var afterAttempt = fired;
        fired = false;
        exhausted = null;
        if (!Enabled())
            return;
        if (suppressed)
        {
            Warn($"{run} AutoResumeSkipped {code}: automatic retry was cancelled for this run.");
            return;
        }
        if (conductor.StopCause is CourierStopCause.User or CourierStopCause.Carrier)
        {
            Warn($"{run} AutoResumeSkipped {code}: the {(conductor.StopCause == CourierStopCause.User ? "user" : "carrier")} stopped the run.");
            return;
        }
        // The resume itself could not start (another run took the gate, or it threw before the journal recorded anything).
        if (afterAttempt && session.ResumeNote is { } note)
        {
            // The run pane shows the note beside Resume.
            Warn($"{run} AutoResumeStopped: attempt {attempts} of {MaxAttempts} was refused ({note}); {state} {code}: {detail}");
            return;
        }
        if (DenyList.Contains(code))
        {
            Warn($"{run} AutoResumeSkipped {code}: a retry cannot change this outcome ({state}: {detail}).");
            return;
        }
        // A carrier briefly gone from discovery makes the run unresumable only until it is back: schedule anyway, and let
        // the due attempt wait for it within the readiness budget. Only a run that cannot resume with its carrier present is skipped.
        if (conductor.PolledSnapshot() is { CanResume: false, CarrierConnected: true })
        {
            Warn($"{run} AutoResumeSkipped {code}: the run cannot resume ({state}: {detail}).");
            return;
        }
        if (attempts >= MaxAttempts)
        {
            exhausted = new(attempts, now, state, code, detail);
            Warn($"{run} AutoResumeExhausted {code}: {MaxAttempts} automatic resumes made no progress; {state}: {detail}");
            return;
        }
        var delay = Delays[Math.Min(attempts, Delays.Length - 1)];
        pending = (now + delay, state, code, detail);
        Warn($"{run} AutoResumeScheduled: attempt {attempts + 1} of {MaxAttempts} in {delay.TotalSeconds:N0}s after {state} {code}: {detail}");
    }

    private void Fire((string State, string Code, string Detail) halt, DateTimeOffset now)
    {
        int attempt;
        lock (gate) attempt = attempts + 1;
        Warn($"{run} AutoResumeFired: attempt {attempt} of {MaxAttempts} after {halt.State} {halt.Code}: {halt.Detail}");
        // Outside the gate: the session's launch lock is taken first by every other caller.
        var launched = session.Resume(automatic: true);
        lock (gate)
        {
            if (pending is null)
                return; // Cancelled while it was being sent; any launch it made reads as a manual one.
            pending = null;
            if (launched)
            {
                attempts = attempt; watching = fired = true;
                history = history.Add(new(attempt, now, halt.State, halt.Code, halt.Detail));
            }
            else if (session.IsActive)
                Warn($"{run} AutoResumeCancelled: another resume started first; attempt {attempt} of {MaxAttempts} was not sent.");
            else
            {
                Warn($"{run} AutoResumeRefused: Resume did not start ({halt.State} {halt.Code}); automatic retries stop.");
            }
        }
    }
}
