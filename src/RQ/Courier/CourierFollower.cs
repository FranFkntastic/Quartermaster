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
    public HashSet<Guid> NotDelivered { get; set; } = [];
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Action,byte> deferredReleases=new();
    private IDisposable? lease, suppress, tradeHold;
    private volatile bool stopped;
    private volatile bool disposed;
    private volatile CourierMessage? active;
    internal CourierProgress? Progress => active is {} m ? new(m.Run,m.Step,m.Command.ToString()) : null;
    public event Action<Guid>? StopRequested;

    internal void RestoreCustodyHold()
    {
        var held=store.Load();
        if(held.Units.IsEmpty && held.Unconfirmed==null)return;
        if(!automation.TryAcquire("courier carrier",out lease))throw new InvalidOperationException("CarrierCustodyLeaseUnavailable");
        try{if(needsSuppression?.Invoke()!=false)suppress=suppression.Acquire();}
        catch{lease?.Dispose();lease=null;throw;}
    }
    public CarrierView View()
    {
        var ledger = store.Load();
        return new(ledger.Run, ledger.Units, ledger.Paused, ledger.Unconfirmed != null, active?.Command);
    }

    // peerOwner comes from the authenticated session, never from the message body.
    public async Task<Result<CourierReceipt>> HandleAsync(OwnerScope peerOwner, CourierMessage m, Deadline deadline, Guid? endpoint = null, Action<CourierProgress>? started = null)
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
        bool Authorized(CourierSettings c, CarrierLedger held) => c.IsPaired(m.Owner) &&
            (CourierPlanner.Exact(m.Owner,peerOwner) || endpoint != null && held.Run == m.Run &&
                (held.OwnerEndpoint == endpoint || held.RunKey is {Length:64} key && key==m.RunKey));
        if (!Authorized(cfg,store.Load())) return Refuse("OwnerNotPaired");
        if (!CourierPlanner.Exact(cfg.Carrier, m.Carrier) || m.Version != (m.Bell?.Ward != null ? 2 : 1) || m.Run == Guid.Empty || m.Step == Guid.Empty)
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
            if (ledger.NotDelivered.Contains(m.Step)) return Refuse("NotDelivered");
            bool travelResume=false;
            if (ledger.Steps.TryGetValue(m.Step, out var old))
            {
                if(old.Fingerprint!=hash)return Refuse("StepCollision");
                if(old.Result!=null)return old.Result;
                travelResume=m.Command is CourierCommand.Travel or CourierCommand.ReturnHome;
                if(!travelResume)return new(CourierOutcome.Indeterminate,"StepUnsettled","Carrier intent has no receipt.");
            }
            if (!travelResume && ledger.Unconfirmed == null && ledger.Steps.Values.Any(x => x.Result == null && x.Command.Run == m.Run &&
                !(m.Command==CourierCommand.Begin && x.Command.Command is CourierCommand.Travel or CourierCommand.ReturnHome) &&
                !(m.Command==CourierCommand.Finish && x.Command.Command==CourierCommand.ReturnHome))
                && m.Command is not (CourierCommand.Stop or CourierCommand.Status or CourierCommand.Clear or CourierCommand.NotDelivered)) return Refuse("CarrierIntentUnsettled");
            if (m.Command == CourierCommand.Status)
            {
                var snap = await Observe();
                return Result<CourierReceipt>.Ok(new(m.Run, m.Step, snap, Custody: ledger.Units, Unconfirmed: ledger.Unconfirmed, LastConfirmed: ledger.LastConfirmed, ActiveRun: ledger.Run));
            }
            if (ledger.Run != null && ledger.Run != m.Run) return Refuse("CarrierRunConflict");
            if (m.Command == CourierCommand.NotDelivered && m.ConfirmStep is {} fence && ledger.Run == m.Run)
            {
                // Serialize with start; fencing an absent step also rejects a delayed start.
                if (ledger.LastConfirmed == fence || ledger.Steps.TryGetValue(fence,out var prior) && prior.Result?.Outcome==CourierOutcome.Succeeded ||
                    ledger.Unconfirmed==fence && (trade is not ICourierDispatchEvidence trace ||
                    !(await trace.WasNotOpened(fence,Deadline.After("CarrierNeverOpened",10)).WaitAsync(TimeSpan.FromSeconds(10)) is {Outcome:CourierOutcome.Succeeded, Value:true})))
                    return Refuse("TradeMayHaveOpened");
                var fresh = await Observe();
                if (!Custody.Matches(m.OwnerEvidence,m.Partner,m.Manifest,0) || m.CarrierBaseline==null || !Custody.Matches(new(m.CarrierBaseline,fresh),m.Carrier,m.Manifest,0))
                {
                    var refusal=Refuse("NotDeliveredEvidenceMismatch");
                    return refusal with {Value=new(m.Run,m.Step,fresh,Custody:ledger.Units)};
                }
                if(ledger.Unconfirmed==fence)ledger.Unconfirmed=null;
                ledger.NotDelivered.Add(fence);
                if (ledger.Steps.TryGetValue(fence,out var abandoned))
                    ledger.Steps[fence]=abandoned with {Result=Result<CourierReceipt>.Fail("NotDelivered",deadline.Name)};
                store.Save(ledger);
                return Result<CourierReceipt>.Ok(new(m.Run,m.Step,fresh,Custody:ledger.Units));
            }
            if (ledger.Completed.Contains(m.Run)) return Refuse("RunClosed");

            if (ledger.Run == null && m.Command != CourierCommand.Begin) return Refuse("RunNotClaimed");
            if (ledger.Paused && m.Command is not (CourierCommand.Begin or CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle or CourierCommand.VerifySettlement or CourierCommand.Clear))
                return Refuse("CarrierStopped");
            if (ledger.Unconfirmed != null && m.Command is not (CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle or CourierCommand.VerifySettlement)) return Refuse("UnsettledHandOff");
            if (lease == null)
            {
                if (!automation.TryAcquire("courier carrier", out lease)) return Refuse("AutomationBusy");
                if(needsSuppression?.Invoke()!=false)suppress = suppression.Acquire();
                stopped = false;
            }
            if (suppression.IsBusy) return Refuse("AutoRetainerBusy");
            if (trade is ICourierHoldPort holds && m.Command is not (CourierCommand.Stop or CourierCommand.Clear or CourierCommand.Finish or CourierCommand.Confirm))
            {
                if(tradeHold==null && m.Command is not (CourierCommand.Begin or CourierCommand.Settle or CourierCommand.VerifySettlement)) return Refuse("TradeHoldScopeMissing");
                tradeHold ??= await calls.Call("CarrierTradeHold",10,d=>holds.BeginHold(m.Run,d));
                holds.CheckHold();
            }
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
            if (m.Command is not (CourierCommand.Receive or CourierCommand.Send)) { ledger.Steps[m.Step]=step; store.Save(ledger); }
            TradeEvidence? evidence = null;
            switch (m.Command)
            {
                case CourierCommand.Begin:
                    ledger.Paused = false; stopped = false; break;
                case CourierCommand.Travel:
                    if (m.Bell == null) return Refuse("BellMissing");
                    if ((await calls.Call("CarrierPosition", 15, bells.PositionAsync)).World != m.Bell.World)
                        await calls.Call("CarrierWorld", Deadline.WorldSeconds(m.Bell), d => worlds.EnsureWorldAsync(m.Bell.World, d));
                    await calls.Call(m.Bell.Ward.HasValue ? "WardTravel" : m.Bell.IsPlaza ? "CarrierPlaza" : "CarrierBell", m.Bell.ApproachSeconds, d => bells.GoInRangeAsync(m.Bell, d)); break;
                case CourierCommand.Receive:
                case CourierCommand.Send:
                    bool send = m.Command == CourierCommand.Send;
                    // Any number of stacks: the trade runner packs them into successive five-slot windows.
                    // Carried gil is one NQ shipment up to the gil cap, its stack size the cap itself.
                    if (m.Manifest.IsDefaultOrEmpty || m.Manifest.Any(s => s.Units <= 0 || s.Units > s.StackSize ||
                            s.IsGil && (s.Hq || s.StackSize != CourierPlanner.GilCap)))
                        return Refuse("InvalidManifest");
                    if (m.Manifest.Any(s => !CourierPlanner.Exact(m.Partner, send ? s.Destination : s.Source)))
                        return Refuse("ManifestPartnerMismatch");
                    // Travel fees spend carried gil between hand-offs, so gil need only still cover the custody.
                    if (send && (!Custody.Covers(ledger.Units, m.Manifest) || m.Manifest.GroupBy(s => (s.Item, s.Hq))
                        .Any(g => before.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => (long)s.Units) || g.Key.Item != CourierPlanner.GilItemId &&
                            ledger.LastConfirmedBags?.Count(g.Key.Item, g.Key.Hq) != before.Count(g.Key.Item, g.Key.Hq))))
                    {
                        var refusal = Refuse("CustodyInsufficient");
                        ledger.Steps[m.Step] = step with { Result = refusal }; store.Save(ledger); return refusal;
                    }
                    await calls.Call("CarrierPartner", 30, d => bells.ObserveTradeableAsync(m.Partner, d));
                    if (stopped || !settings().CarrierOptIn || !Authorized(settings(),ledger)) return Refuse("CarrierDisarmed");
                    ledger.Unconfirmed = m.Step;
                    ledger.Steps[m.Step]=step; store.Save(ledger);
                    CourierGame.TradeStarted.Value=()=>started?.Invoke(new(m.Run,m.Step,"TradeStarted"));
                    try
                    {
                        await calls.Call("CarrierTrade", Deadline.TradeSeconds(m.Manifest), d => send ? trade.SendAsync(m.Partner, m.Manifest, d) : trade.ReceiveAsync(m.Partner, m.Manifest, d));
                    }
                    catch (CourierFault fault) when (fault.Outcome == CourierOutcome.Cancelled)
                    {
                        evidence = new(before, await Observe());
                        if (!Custody.Matches(evidence, m.Carrier, m.Manifest, 0)) throw;
                        var cancelled = new Result<CourierReceipt>(CourierOutcome.Cancelled, "CancelledPreLock", fault.Message,
                            new(m.Run, m.Step, Evidence: evidence, Custody: ledger.Units));
                        ledger.Unconfirmed = null; ledger.Steps[m.Step] = step with { Result = cancelled }; store.Save(ledger);
                        refused = true; return cancelled;
                    }
                    catch (Exception fault)
                    {
                        evidence = new(before, await Observe());
                        var uncertain = new Result<CourierReceipt>(CourierOutcome.Indeterminate,
                            (fault as CourierFault)?.Code ?? "CarrierTradeUnsettled", fault.Message,
                            new(m.Run, m.Step, Evidence: evidence, Custody: ledger.Units));
                        warning($"{m.Run}/{m.Step} {uncertain.Code}: {fault.Message}");
                        ledger.Steps[m.Step] = step with { Result = uncertain }; store.Save(ledger);
                        refused = true; return uncertain;
                    }
                    evidence = new(before, await Observe()); break;
                case CourierCommand.Confirm:
                case CourierCommand.VerifySettlement:
                case CourierCommand.Settle:
                    if (m.ConfirmStep == null || ledger.Unconfirmed != m.ConfirmStep ||
                        !ledger.Steps.TryGetValue(m.ConfirmStep.Value, out var staged)) return Refuse("NoPendingHandOff");
                    var pending = staged.Command;
                    var sign = pending.Command == CourierCommand.Receive ? 1 : -1;
                    var credit = pending.Manifest;
                    if(m.Command is CourierCommand.VerifySettlement or CourierCommand.Settle){
                        if(staged.Before==null || m.OwnerEvidence==null ||
                            !Custody.TrySlice(new(staged.Before,before),m.Carrier,m.OwnerEvidence,pending.Partner,pending.Manifest,sign,out credit,out _))
                            return Refuse("SettlementDeltaMismatch");
                    }
                    if (m.Command is CourierCommand.Settle or CourierCommand.VerifySettlement && staged.Result?.Outcome==CourierOutcome.Succeeded &&
                        (staged.Result.Value?.Evidence is not {} recorded || !Custody.TrySlice(recorded,m.Carrier,m.OwnerEvidence!,pending.Partner,pending.Manifest,sign,out var saved,out _) ||
                        !saved.SequenceEqual(credit))) return Refuse("SettledReceiptChanged");
                    if (m.Command is CourierCommand.Settle or CourierCommand.VerifySettlement && staged.Result?.Outcome != CourierOutcome.Succeeded)
                    {
                        if (trade is not ICourierSettlement recovery || staged.Before == null) return Refuse("SettlementDeltaMismatch");
                        await calls.Call("CarrierSettlement",15,d=>recovery.Settle(pending.Step,pending.Partner,m.OwnerEvidence!,d));
                        staged=staged with {Result=Result<CourierReceipt>.Ok(new(m.Run,pending.Step,Evidence:new(staged.Before,before),Custody:ledger.Units))};
                        ledger.Steps[pending.Step]=staged;
                    }
                    if(m.Command==CourierCommand.VerifySettlement){
                        if(sign!=-1)return Refuse("SenderSettlementRequired");
                        break;
                    }
                    if (m.Command==CourierCommand.Confirm && (staged.Result?.Outcome != CourierOutcome.Succeeded || !Custody.Matches(staged.Result?.Value?.Evidence, m.Carrier, pending.Manifest, sign) ||
                        !Custody.Matches(m.OwnerEvidence, pending.Partner, pending.Manifest, -sign)))
                        return Refuse("HandOffDeltaMismatch");
                    ledger.Units = Custody.Apply(ledger.Units, credit, sign);
                    // A process may have died after persisting an older settlement request,
                    // before returning its receipt. It can no longer own an unsettled intent.
                    foreach(var abandoned in ledger.Steps.Where(x=>x.Key!=m.Step && x.Value.Result==null &&
                        x.Value.Command.ConfirmStep==pending.Step && x.Value.Command.Command is CourierCommand.Settle or CourierCommand.VerifySettlement).ToArray())
                        ledger.Steps[abandoned.Key]=abandoned.Value with {Result=Result<CourierReceipt>.Fail("SettlementSuperseded",pending.Step.ToString())};
                    ledger.LastConfirmed = ledger.Unconfirmed;
                    ledger.LastConfirmedBags = staged.Result!.Value!.Evidence!.After;
                    ledger.Unconfirmed = null; break;
                case CourierCommand.ReturnHome:
                    await ReturnHomeAsync(cfg, m, ledger.Start); break;
                case CourierCommand.Clear:
                    if (ledger.Unconfirmed!=null || !Custody.Covers(ledger.Units,m.Manifest) || !Custody.Covers(m.Manifest,ledger.Units))
                        return Refuse("ClearCustodyChanged");
                    // The acknowledged manifest survives closure in the durable Clear step.
                    ledger.Units=[];goto case CourierCommand.Finish;
                case CourierCommand.Finish:
                    if (!ledger.Units.IsEmpty || ledger.Unconfirmed != null) return Refuse("CustodyRemaining");
                    ledger.Run = null; ledger.RunKey=null;ledger.OwnerEndpoint=null;ledger.Completed.Add(m.Run);
                    Release(force:true); break;
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
            return new((e as CourierFault)?.Outcome ?? CourierOutcome.Indeterminate, (e as CourierFault)?.Code ?? "CarrierUnsettled", e.Message);
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
    // The carrier always ends a run on its home world: at its own meeting bell when one is saved, otherwise back where
    // the run found it if that was an ordinary place on the home world, otherwise wherever the home-world return lands.
    // Where a run happened to start elsewhere (a visited world or ward) is never a destination, so a stranding cannot
    // become the next run's home.
    private async Task ReturnHomeAsync(CourierSettings cfg, CourierMessage m, CourierPosition? start)
    {
        var home = m.Carrier.HomeWorldId;
        var at = $"{m.Run}/{m.Step} ReturnHome";
        await calls.Call("CarrierReturnWorld", Deadline.DataCenterSeconds, worlds.ReturnHomeAsync);
        if (cfg.Bells.GetValueOrDefault(m.Carrier.LocalContentId ?? 0) is { } own)
        {
            if (own.World == home)
            {
                await calls.Call(own.Ward.HasValue ? "CarrierReturnWardBell" : "CarrierReturnBell", own.ApproachSeconds, d => bells.GoInRangeAsync(own, d));
                return;
            }
            warning($"{at}: CarrierBellOffHomeWorld: saved bell {own.Label} is on world {own.World}, home world {home}; returning to the start position instead.");
        }
        if (start is null || start.World != home || start.Ward != null)
        {
            information?.Invoke($"{at}: no meeting bell saved and the run started " + (start is null ? "at an unknown position" :
                start.World != home ? $"on world {start.World}" : $"in ward {start.Ward}") + $"; carrier left on home world {home}.");
            return;
        }
        try { await calls.Call("CarrierReturnStart", 150, d => bells.ReturnToAsync(start, d)); }
        catch (CourierFault fault) when (fault.Code == "ReturnStartUnreachable")
        {
            // The home world was reached; only the exact start cannot be travelled to by ordinary navigation.
            warning($"{at}: ReturnStartUnreachable: territory {start.Territory} ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0}) on home world {home}: {fault.Message}; carrier left on home world.");
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
    private void Release(bool force=false)
    {
        if (!force && store.Load() is {} held && (!held.Units.IsEmpty || held.Unconfirmed!=null)) return;
        var heldSuppression=Interlocked.Exchange(ref suppress,null);
        var heldLease=Interlocked.Exchange(ref lease,null);
        var heldTrade=Interlocked.Exchange(ref tradeHold,null);
        void Drop(){try{heldTrade?.Dispose();}finally{try{heldSuppression?.Dispose();}finally{heldLease?.Dispose();}}}
        deferredReleases.TryAdd(Drop,0);
        void Once(){if(deferredReleases.TryRemove(Drop,out _))Drop();}
        if(disposed)Once();else calls.WhenSettled(Once);
    }
    public void Dispose()
    {
        stopped=disposed=true;
        var drained=gate.Wait(TimeSpan.FromSeconds(2));
        if (!drained) warning("CarrierUnloadDrainDeadline: 2s elapsed.");
        try {
            Release(force:true);
            foreach(var release in deferredReleases.Keys)if(deferredReleases.TryRemove(release,out _))release();
        } finally { if (drained) gate.Release(); }
    }
}
