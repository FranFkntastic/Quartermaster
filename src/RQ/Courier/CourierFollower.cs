using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RQ.Automation;
using RQ.Domain;
using RQ.Persistence;

namespace RQ.Courier;

public sealed record CarrierStep(string Fingerprint, CourierMessage Command, Result<CourierReceipt>? Result, InventoryView? Before = null);
// Read-only projection for the carrier pane: the run held, the units in custody and the command in flight.
public sealed record CarrierView(Guid? Run, ImmutableArray<Shipment> Custody, bool Paused, bool Unconfirmed, CourierCommand? Active);
public sealed class CarrierLedger
{
    public Guid? Run { get; set; }
    public Guid? OwnerEndpoint { get; set; }
    public string? RunKey {get;set;}
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
    IInventoryObservationPort inventory, Action<string> warning, Action<string>? information = null, Func<bool>? needsSuppression=null) : IDisposable
{
    private readonly CourierStore<CarrierLedger> store = new(Path.Combine(directory, "courier-custody.json"));
    private readonly CourierCalls calls = new(warning);
    private readonly SemaphoreSlim gate = new(1);
    private IDisposable? lease, suppress;
    private volatile bool stopped;
    private volatile bool disposed;
    private volatile CourierMessage? active;
    internal CourierProgress? Progress => active is {} m ? new(m.Run,m.Step,m.Command.ToString()) : null;
    public event Action<Guid>? StopRequested;

    public CarrierView View()
    {
        var ledger = store.Load();
        return new(ledger.Run, ledger.Units, ledger.Paused, ledger.Unconfirmed != null, active?.Command);
    }

    // peerOwner comes from the authenticated session, never from the message body.
    public async Task<Result<CourierReceipt>> HandleAsync(OwnerScope peerOwner, CourierMessage m, Deadline deadline, Guid? endpoint = null)
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
        if(disposed)return Refuse("CarrierUnloaded");
        bool Authorized(CourierSettings c, CarrierLedger held) => CourierPlanner.Exact(c.PairedOwner,m.Owner) &&
            (CourierPlanner.Exact(c.PairedOwner,peerOwner) || endpoint != null && held.Run == m.Run &&
                (held.OwnerEndpoint == endpoint || held.RunKey is {Length:64} key && key==m.RunKey));
        if (!Authorized(cfg,store.Load())) return Refuse("OwnerNotPaired");
        if (!CourierPlanner.Exact(cfg.Carrier, m.Carrier) || m.Version != 1 || m.Run == Guid.Empty || m.Step == Guid.Empty)
            return Refuse("EnvelopeMismatch");
        var remaining = deadline.Expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return Refuse("CommandExpired");
        if (m.Command == CourierCommand.Stop && store.Load().Run == m.Run) await StopLocalAsync(false);
        if (!await gate.WaitAsync(remaining)) return Refuse("CommandQueueDeadline");
        active = m;
        try
        {
            cfg = settings();
            if (disposed || !cfg.CarrierOptIn || !Authorized(cfg,store.Load()) || !CourierPlanner.Exact(cfg.Carrier, m.Carrier))
                return Refuse("PairingChanged");
            var ledger = store.Load();
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
            bool travelResume=false;
            if (ledger.Steps.TryGetValue(m.Step, out var old))
            {
                if(old.Fingerprint!=hash)return Refuse("StepCollision");
                if(old.Result!=null)return old.Result;
                travelResume=m.Command is CourierCommand.Travel or CourierCommand.ReturnHome;
                if(!travelResume)return new(CourierOutcome.Indeterminate,"StepUnsettled","Carrier intent has no receipt.");
            }
            if (ledger.Run != null && ledger.Run != m.Run) return Refuse("CarrierRunConflict");
            if (!travelResume && ledger.Unconfirmed == null && ledger.Steps.Values.Any(x => x.Result == null && x.Command.Run == m.Run &&
                !(m.Command==CourierCommand.Begin && x.Command.Command is CourierCommand.Travel or CourierCommand.ReturnHome))
                && m.Command is not (CourierCommand.Stop or CourierCommand.Status or CourierCommand.Clear)) return Refuse("CarrierIntentUnsettled");
            if (m.Command == CourierCommand.Status)
            {
                var snap = await Observe();
                return Result<CourierReceipt>.Ok(new(m.Run, m.Step, snap, Custody: ledger.Units, Unconfirmed: ledger.Unconfirmed, LastConfirmed: ledger.LastConfirmed, ActiveRun: ledger.Run));
            }
            if (ledger.Completed.Contains(m.Run)) return Refuse("RunClosed");
            if(m.Command==CourierCommand.Begin && trade is CourierTrade live && live.IncompatibleAcceptor()!=null)
                return Refuse("UncoordinatedTradeAcceptor");
            if (ledger.Run == null && m.Command != CourierCommand.Begin) return Refuse("RunNotClaimed");
            if (ledger.Paused && m.Command is not (CourierCommand.Begin or CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle or CourierCommand.Clear))
                return Refuse("CarrierStopped");
            if (ledger.Unconfirmed != null && m.Command is not (CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle)) return Refuse("UnsettledHandOff");
            if (lease == null)
            {
                if (!automation.TryAcquire("courier carrier", out lease)) return Refuse("AutomationBusy");
                if(needsSuppression?.Invoke()!=false)suppress = suppression.Acquire();
                stopped = false;
            }
            if (suppression.IsBusy) return Refuse("AutoRetainerBusy");
            var before = await Observe();
            if (!CourierPlanner.Exact(before.Character, m.Carrier)) return Refuse("CarrierIdentityMismatch");
            if (ledger.Run == null && m.Command != CourierCommand.Stop)
                ledger.Start = await calls.Call("CarrierStartPosition", 15, bells.PositionAsync);
            ledger.Run = m.Run;
            if (m.Command == CourierCommand.Begin) { ledger.OwnerEndpoint = endpoint; ledger.RunKey ??= m.RunKey; }
            CourierGame.Step.Value = m.Step;
            CourierGame.CanContinue.Value=()=>!stopped && !disposed && settings() is {} current &&
                current.CarrierOptIn && Authorized(current,ledger) && CourierPlanner.Exact(current.Carrier,m.Carrier);
            information?.Invoke($"{m.Run}/{m.Step} carrier {m.Command} ({deadline.Name}, expires {deadline.Expires:O})");
            var step = new CarrierStep(hash, m, null, m.Command is CourierCommand.Receive or CourierCommand.Send ? before : null);
            ledger.Steps[m.Step]=step;
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
                    if (stopped || !settings().CarrierOptIn || !Authorized(settings(),ledger)) return Refuse("CarrierDisarmed");
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
                case CourierCommand.Settle:
                    if (m.ConfirmStep == null || ledger.Unconfirmed != m.ConfirmStep ||
                        !ledger.Steps.TryGetValue(m.ConfirmStep.Value, out var staged)) return Refuse("NoPendingHandOff");
                    var pending = staged.Command;
                    var sign = pending.Command == CourierCommand.Receive ? 1 : -1;
                    if (m.Command == CourierCommand.Settle && staged.Result?.Outcome != CourierOutcome.Succeeded)
                    {
                        if (trade is not ICourierSettlement recovery || staged.Before == null ||
                            !Custody.Matches(new(staged.Before,before),m.Carrier,pending.Manifest,sign) ||
                            !Custody.Matches(m.OwnerEvidence,pending.Partner,pending.Manifest,-sign)) return Refuse("SettlementDeltaMismatch");
                        await calls.Call("CarrierSettlement",15,d=>recovery.Settle(pending.Step,d));
                        staged=staged with {Result=Result<CourierReceipt>.Ok(new(m.Run,pending.Step,Evidence:new(staged.Before,before),Custody:ledger.Units))};
                        ledger.Steps[pending.Step]=staged;
                    }
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
                case CourierCommand.Clear:
                    if (ledger.Unconfirmed!=null || !Custody.Covers(ledger.Units,m.Manifest) || !Custody.Covers(m.Manifest,ledger.Units))
                        return Refuse("ClearCustodyChanged");
                    // The acknowledged manifest survives closure in the durable Clear step.
                    ledger.Units=[];goto case CourierCommand.Finish;
                case CourierCommand.Finish:
                    if (!ledger.Units.IsEmpty || ledger.Unconfirmed != null) return Refuse("CustodyRemaining");
                    ledger.Run = null; ledger.RunKey=null;ledger.OwnerEndpoint=null;ledger.Completed.Add(m.Run);
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
        finally
        {
            active=null;
            try
            {
                if(stopped && store.Load() is {Run:not null,Paused:false} held){held.Paused=true;store.Save(held);}
            }
            finally{try{if(refused || stopped)Release();}finally{gate.Release();}}
        }
    }
    private Task<InventoryView> Observe() => calls.Call("CarrierBags", 15, inventory.ObserveAsync);
    public async Task StopLocalAsync(bool notifyOwner = true)
    {
        stopped = true;
        if (notifyOwner && store.Load().Run is { } run) StopRequested?.Invoke(run);
        await calls.Call("CarrierCancelPreLock", 10, trade.CancelPreLockAsync);
        if (notifyOwner)
        {
            if(!await gate.WaitAsync(TimeSpan.FromSeconds(15)))
            {
                warning("CarrierStopDrainDeadline: 15s elapsed; the active step retains ownership through settlement.");
                return;
            }
            try { var ledger=store.Load(); if(ledger.Run!=null){ledger.Paused=true;store.Save(ledger);} Release(); } finally { gate.Release(); }
        }
    }
    private void Release()
    {
        var heldSuppression = suppress; var heldLease = lease; suppress = null; lease = null;
        calls.WhenSettled(() => { try { heldSuppression?.Dispose(); } finally { heldLease?.Dispose(); } });
    }
    public void Dispose()
    {
        stopped=disposed=true;
        _=ReleaseAfterCommand();
    }
    private async Task ReleaseAfterCommand()
    {
        await gate.WaitAsync();
        try{Release();}finally{gate.Release();}
    }
}
