using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RQ.Automation;
using RQ.Domain;
using RQ.Persistence;

namespace RQ.Courier;

public sealed record CarrierStep(string Fingerprint, CourierMessage Command, Result<CourierReceipt>? Result);
public sealed class CarrierLedger
{
    public Guid? Run { get; set; }
    public ImmutableArray<Shipment> Units { get; set; } = [];
    public Dictionary<Guid, CarrierStep> Steps { get; set; } = [];
    public Guid? Unconfirmed { get; set; }
    public Guid? LastConfirmed { get; set; }
    public InventoryView? LastConfirmedBags { get; set; }
    public CourierPosition? Start { get; set; }
    public bool Paused { get; set; }
    public HashSet<Guid> Completed { get; set; } = [];
}

public sealed class CourierFollower(string directory, Func<CourierSettings> settings, AutomationLease automation,
    AutoRetainerSuppression suppression, ICourierTradePort trade, IWorldVisitPort worlds, IMeetingBellPort bells,
    IInventoryObservationPort inventory, Action<string> warning) : IDisposable
{
    private readonly CourierStore<CarrierLedger> store = new(Path.Combine(directory, "courier-custody.json"));
    private readonly CourierCalls calls = new(warning);
    private readonly SemaphoreSlim gate = new(1);
    private IDisposable? lease, suppress;
    private volatile bool stopped;
    public event Action<Guid>? StopRequested;

    // peerOwner comes from the authenticated session, never from the message body.
    public async Task<Result<CourierReceipt>> HandleAsync(OwnerScope peerOwner, CourierMessage m, Deadline deadline)
    {
        var cfg = settings();
        var refused = false;
        Result<CourierReceipt> Refuse(string code)
        {
            refused = true;
            warning($"{m.Run}/{m.Step} {m.Command}: {code} ({deadline.Name})");
            return Result<CourierReceipt>.Fail(code, deadline.Name);
        }
        if (!cfg.CarrierOptIn) return Refuse("CarrierOptInOff");
        if (!CourierPlanner.Exact(cfg.PairedOwner, peerOwner) || !CourierPlanner.Exact(peerOwner, m.Owner)) return Refuse("OwnerNotPaired");
        if (!CourierPlanner.Exact(cfg.Carrier, m.Carrier) || m.Version != 1 || m.Run == Guid.Empty || m.Step == Guid.Empty)
            return Refuse("EnvelopeMismatch");
        var remaining = deadline.Expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return Refuse("CommandExpired");
        if (m.Command == CourierCommand.Stop && store.Load().Run == m.Run) await StopLocalAsync(false);
        if (!await gate.WaitAsync(remaining)) return Refuse("CommandQueueDeadline");
        try
        {
            cfg = settings();
            if (!cfg.CarrierOptIn || !CourierPlanner.Exact(cfg.PairedOwner, peerOwner) || !CourierPlanner.Exact(cfg.Carrier, m.Carrier))
                return Refuse("PairingChanged");
            var ledger = store.Load();
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
            if (ledger.Steps.TryGetValue(m.Step, out var old))
                return old.Fingerprint != hash ? Refuse("StepCollision") : old.Result ??
                    new(CourierOutcome.Indeterminate, "StepUnsettled", "Carrier intent has no receipt.");
            if (ledger.Run != null && ledger.Run != m.Run) return Refuse("CarrierRunConflict");
            if (ledger.Unconfirmed == null && ledger.Steps.Values.Any(x => x.Result == null && x.Command.Run == m.Run)
                && m.Command is not (CourierCommand.Stop or CourierCommand.Status)) return Refuse("CarrierIntentUnsettled");
            if (m.Command == CourierCommand.Status)
            {
                var snap = await Observe();
                return Result<CourierReceipt>.Ok(new(m.Run, m.Step, snap, Custody: ledger.Units, Unconfirmed: ledger.Unconfirmed, LastConfirmed: ledger.LastConfirmed));
            }
            if (ledger.Completed.Contains(m.Run)) return Refuse("RunClosed");
            if (ledger.Run == null && m.Command != CourierCommand.Begin) return Refuse("RunNotClaimed");
            if (ledger.Paused && m.Command is not (CourierCommand.Begin or CourierCommand.Confirm or CourierCommand.Stop))
                return Refuse("CarrierStopped");
            if (ledger.Unconfirmed != null && m.Command is not (CourierCommand.Confirm or CourierCommand.Stop)) return Refuse("UnsettledHandOff");
            if (lease == null)
            {
                if (!automation.TryAcquire("courier carrier", out lease)) return Refuse("AutomationBusy");
                suppress = suppression.Acquire();
                stopped = false;
            }
            if (suppression.IsBusy) return Refuse("AutoRetainerBusy");
            var before = await Observe();
            if (!CourierPlanner.Exact(before.Character, m.Carrier)) return Refuse("CarrierIdentityMismatch");
            if (ledger.Run == null && m.Command != CourierCommand.Stop)
                ledger.Start = await calls.Call("CarrierStartPosition", 15, bells.PositionAsync);
            ledger.Run = m.Run;
            var step = new CarrierStep(hash, m, null);
            ledger.Steps.Add(m.Step, step);
            store.Save(ledger);
            TradeEvidence? evidence = null;
            switch (m.Command)
            {
                case CourierCommand.Begin:
                    ledger.Paused = false; stopped = false; break;
                case CourierCommand.Travel:
                    if (m.Bell == null) return Refuse("BellMissing");
                    if ((await calls.Call("CarrierPosition", 15, bells.PositionAsync)).World != m.Bell.World)
                        await calls.Call("CarrierWorld", 180, d => worlds.EnsureWorldAsync(m.Bell.World, d));
                    await calls.Call("CarrierBell", 150, d => bells.GoInRangeAsync(m.Bell, d)); break;
                case CourierCommand.Receive:
                case CourierCommand.Send:
                    bool send = m.Command == CourierCommand.Send;
                    if (m.Manifest.IsDefaultOrEmpty || m.Manifest.Length > 5 || m.Manifest.Any(s => s.Units <= 0 || s.Units > s.StackSize))
                        return Refuse("InvalidManifest");
                    if (m.Manifest.Any(s => !CourierPlanner.Exact(m.Partner, send ? s.Destination : s.Source)))
                        return Refuse("ManifestPartnerMismatch");
                    if (send && (!Custody.Covers(ledger.Units, m.Manifest) || m.Manifest.GroupBy(s => (s.Item, s.Hq))
                        .Any(g => before.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => s.Units) ||
                            ledger.LastConfirmedBags?.Count(g.Key.Item, g.Key.Hq) != before.Count(g.Key.Item, g.Key.Hq))))
                    {
                        var refusal = Refuse("CustodyInsufficient");
                        ledger.Steps[m.Step] = step with { Result = refusal }; store.Save(ledger); return refusal;
                    }
                    await calls.Call("CarrierPartner", 30, d => bells.ObserveTradeableAsync(m.Partner, d));
                    if (stopped || !settings().CarrierOptIn || !CourierPlanner.Exact(settings().PairedOwner, peerOwner)) return Refuse("CarrierDisarmed");
                    ledger.Unconfirmed = m.Step;
                    store.Save(ledger);
                    try
                    {
                        await calls.Call("CarrierTrade", 120, d => send ? trade.SendAsync(m.Partner, m.Manifest, d) : trade.ReceiveAsync(m.Partner, m.Manifest, d));
                    }
                    catch (CourierFault fault) when (fault.Outcome == CourierOutcome.Cancelled)
                    {
                        evidence = new(before, await Observe());
                        if (!Custody.Matches(evidence, m.Carrier, m.Manifest, 0)) throw;
                        var cancelled = new Result<CourierReceipt>(CourierOutcome.Cancelled, "CancelledPreLock", fault.Message,
                            new(m.Run, m.Step, Evidence: evidence, Custody: ledger.Units));
                        ledger.Unconfirmed = null; ledger.Steps[m.Step] = step with { Result = cancelled }; store.Save(ledger);
                        return cancelled;
                    }
                    catch (Exception fault)
                    {
                        evidence = new(before, await Observe());
                        var uncertain = new Result<CourierReceipt>(CourierOutcome.Indeterminate,
                            (fault as CourierFault)?.Code ?? "CarrierTradeUnsettled", fault.Message,
                            new(m.Run, m.Step, Evidence: evidence, Custody: ledger.Units));
                        warning($"{m.Run}/{m.Step} {uncertain.Code}: {fault.Message}");
                        ledger.Steps[m.Step] = step with { Result = uncertain }; store.Save(ledger);
                        return uncertain;
                    }
                    evidence = new(before, await Observe()); break;
                case CourierCommand.Confirm:
                    if (m.ConfirmStep == null || ledger.Unconfirmed != m.ConfirmStep ||
                        !ledger.Steps.TryGetValue(m.ConfirmStep.Value, out var staged)) return Refuse("NoPendingHandOff");
                    var pending = staged.Command;
                    var sign = pending.Command == CourierCommand.Receive ? 1 : -1;
                    if (staged.Result?.Outcome != CourierOutcome.Succeeded || !Custody.Matches(staged.Result?.Value?.Evidence, m.Carrier, pending.Manifest, sign) ||
                        !Custody.Matches(m.OwnerEvidence, pending.Partner, pending.Manifest, -sign))
                        return Refuse("HandOffDeltaMismatch");
                    ledger.Units = Custody.Apply(ledger.Units, pending.Manifest, sign);
                    ledger.LastConfirmed = ledger.Unconfirmed;
                    ledger.LastConfirmedBags = staged.Result!.Value!.Evidence!.After;
                    ledger.Unconfirmed = null; break;
                case CourierCommand.ReturnHome:
                    var start = ledger.Start ?? throw new InvalidOperationException("CarrierStartUnknown");
                    await calls.Call("CarrierReturnWorld", 180, d => start.World == m.Carrier.HomeWorldId
                        ? worlds.ReturnHomeAsync(d) : worlds.EnsureWorldAsync(start.World, d));
                    await calls.Call("CarrierReturnPosition", 150, d => bells.ReturnToAsync(start, d)); break;
                case CourierCommand.Finish:
                    if (!ledger.Units.IsEmpty || ledger.Unconfirmed != null) return Refuse("CustodyRemaining");
                    ledger.Run = null; ledger.Completed.Add(m.Run);
                    Release(); break;
                case CourierCommand.Stop:
                    ledger.Paused = true;
                    Release(); break;
                default: return Refuse("UnknownCommand");
            }
            var result = Result<CourierReceipt>.Ok(new(m.Run, m.Step, before, evidence, ledger.Units));
            ledger.Steps[m.Step] = step with { Result = result };
            store.Save(ledger);
            return result;
        }
        catch (Exception e)
        {
            Release();
            warning($"{m.Run}/{m.Step} {m.Command}: {e.Message}");
            return new(CourierOutcome.Indeterminate, (e as CourierFault)?.Code ?? "CarrierUnsettled", e.Message);
        }
        finally { try { if (refused) Release(); } finally { gate.Release(); } }
    }
    private Task<InventoryView> Observe() => calls.Call("CarrierBags", 15, inventory.ObserveAsync);
    public async Task StopLocalAsync(bool notifyOwner = true)
    {
        stopped = true;
        if (notifyOwner && store.Load().Run is { } run) StopRequested?.Invoke(run);
        await calls.Call("CarrierCancelPreLock", 10, trade.CancelPreLockAsync);
        if (notifyOwner && await gate.WaitAsync(TimeSpan.FromSeconds(15)))
        {
            try { Release(); } finally { gate.Release(); }
        }
    }
    private void Release()
    {
        var heldSuppression = suppress; var heldLease = lease; suppress = null; lease = null;
        calls.WhenSettled(() => { try { heldSuppression?.Dispose(); } finally { heldLease?.Dispose(); } });
    }
    public void Dispose() => Release();
}
