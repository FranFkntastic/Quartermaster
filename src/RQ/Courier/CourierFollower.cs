using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RQ.Automation;
using RQ.Domain;
using RQ.Persistence;
using RQ.UI;

namespace RQ.Courier;

public sealed record CarrierStep(string Fingerprint, CourierMessage Command, Result<CourierReceipt>? Result, InventoryView? Before = null);
// Read-only projection for the carrier pane: the run held, the units in custody and the command in flight.
public sealed record CarrierView(Guid? Run, ImmutableArray<Shipment> Custody, bool Paused, bool Unconfirmed, CourierCommand? Active, bool Locked = false);
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
    IInventoryObservationPort inventory, Action<string> warning, Action<string>? information = null, Func<bool>? needsSuppression=null,
    Func<bool>? lifestreamBusy=null) : IDisposable
{
    private readonly CourierStore<CarrierLedger> store = new(Path.Combine(directory, "courier-custody.json"), warning);
    private readonly CourierCalls calls = new(warning);
    private readonly SemaphoreSlim gate = new(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Action,byte> deferredReleases=new();
    private IDisposable? lease, suppress, tradeHold;
    private volatile bool stopped;
    private volatile bool disposed;
    private volatile CourierMessage? active;
    // When the last owner command arrived: a presence that says the owner no longer has the run is trusted only after
    // this long without commands, so an owner presence published just before a Begin never forgets the run it began.
    private DateTimeOffset lastCommand = DateTimeOffset.MinValue;
    private static readonly TimeSpan OrphanQuiet = TimeSpan.FromSeconds(30);
    internal CourierProgress? Progress => active is {} m ? new(m.Run,m.Step,m.Command.ToString()) : null;
    public event Action<Guid>? StopRequested;

    // A load never re-takes the run's locks: no command is in flight after a reload, so a run that still held custody
    // is paused and every lock is re-taken by the owner's next Begin or settlement. Custody stays protected by the
    // ledger: every send requires the bags to equal the last confirmed bags, so interference refuses, never loses units.
    internal void RestoreCustodyHold()
    {
        var held=store.Load();
        if(held.Run==null || held.Paused || held.Units.IsEmpty && held.Unconfirmed==null)return;
        held.Paused=true;store.Save(held);
        warning($"{held.Run} CarrierRunPausedOnLoad: holding {held.Units.Sum(s=>(long)s.Units):N0} units{(held.Unconfirmed is {} u?$", hand-off {u} unconfirmed":"")}; " +
            "trade, automation and AutoRetainer locks stay released until the owner resumes.");
    }
    public CarrierView View()
    {
        var ledger = store.Load();
        return new(ledger.Run, ledger.Units, ledger.Paused, ledger.Unconfirmed != null, active?.Command,
            Volatile.Read(ref lease) != null || Volatile.Read(ref tradeHold) != null || Volatile.Read(ref suppress) != null);
    }

    // peerOwner comes from the authenticated session, never from the message body.
    public async Task<Result<CourierReceipt>> HandleAsync(OwnerScope peerOwner, CourierMessage m, Deadline deadline, Guid? endpoint = null, Action<CourierProgress>? started = null)
    {
        var cfg = settings();
        var refused = false;
        Result<CourierReceipt> Refuse(string code, string? detail = null)
        {
            refused = true;
            warning($"{m.Run}/{m.Step} {m.Command}: {code} ({deadline.Name}){(detail == null ? "" : ": " + detail)}");
            return Result<CourierReceipt>.Fail(code, detail ?? deadline.Name);
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
        lastCommand = DateTimeOffset.UtcNow;
        if (m.Command is CourierCommand.Stop or CourierCommand.Clear && store.Load().Run == m.Run) await StopLocalAsync(false);
        if (!await gate.WaitAsync(remaining)) return Refuse("CommandQueueDeadline");
        active = m;
        try
        {
            cfg = settings();
            if (disposed || !cfg.CarrierOptIn || !Authorized(cfg,store.Load()) || !CourierPlanner.Exact(cfg.Carrier, m.Carrier))
                return Refuse("PairingChanged");
            var ledger = store.Load();
            // The owner cleared the run: forget it whatever the ledger holds or awaits, with no lock, observation or identity check.
            if (m.Command == CourierCommand.Clear)
            {
                if (ledger.Run == m.Run) Forget(ledger, "the owner cleared it");
                return Result<CourierReceipt>.Ok(new(m.Run, m.Step, Custody: []));
            }
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
            if (ledger.NotDelivered.Contains(m.Step)) return Refuse("NotDelivered");
            bool travelResume=false;
            if (ledger.Steps.TryGetValue(m.Step, out var old))
            {
                if(old.Fingerprint!=hash)return Refuse("StepCollision");
                if(old.Result!=null)return old.Result;
                // A claim's (or unclaim's) ledger change and its receipt are saved together: an intent without a receipt changed nothing.
                travelResume=m.Command is CourierCommand.Travel or CourierCommand.ReturnHome or CourierCommand.Claim or CourierCommand.Unclaim or CourierCommand.SettleSent;
                if(!travelResume)return new(CourierOutcome.Indeterminate,"StepUnsettled","Carrier intent has no receipt.");
            }
            if (!travelResume && ledger.Unconfirmed == null && ledger.Steps.Values.Any(x => x.Result == null && x.Command.Run == m.Run &&
                !(m.Command==CourierCommand.Begin && x.Command.Command is CourierCommand.Travel or CourierCommand.ReturnHome or CourierCommand.Claim or CourierCommand.Unclaim) &&
                !(m.Command==CourierCommand.Finish && x.Command.Command==CourierCommand.ReturnHome))
                && m.Command is not (CourierCommand.Stop or CourierCommand.Status or CourierCommand.NotDelivered)) return Refuse("CarrierIntentUnsettled");
            if (m.Command == CourierCommand.Status)
            {
                var snap = await Observe();
                // The open (else last confirmed) hand-off's completed send reports its own receipt, so the owner can show what it proves.
                var sentProof = (ledger.Unconfirmed ?? ledger.LastConfirmed) is { } open && ledger.Steps.TryGetValue(open, out var openStep) &&
                    openStep.Command.Command == CourierCommand.Send && openStep.Result?.Outcome == CourierOutcome.Succeeded ? openStep.Result.Value?.Evidence : null;
                return Result<CourierReceipt>.Ok(new(m.Run, m.Step, snap, sentProof, Custody: ledger.Units, Unconfirmed: ledger.Unconfirmed, LastConfirmed: ledger.LastConfirmed, ActiveRun: ledger.Run));
            }
            if (ledger.Run != null && ledger.Run != m.Run)
            {
                // An owner client runs one run at a time: its Begin of another run means it no longer has the one held here.
                if (m.Command != CourierCommand.Begin || !SameOwnerClient(ledger, m.Owner, peerOwner, endpoint)) return Refuse("CarrierRunConflict");
                Forget(ledger, $"the owner began run {m.Run} instead");
            }
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
            // Unclaim moves nothing: a stopped run releases its unhanded claim without resuming.
            if (ledger.Paused && m.Command is not (CourierCommand.Begin or CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle or CourierCommand.VerifySettlement or CourierCommand.Unclaim or CourierCommand.SettleSent))
                return Refuse("CarrierStopped");
            if (ledger.Unconfirmed != null && m.Command is not (CourierCommand.Confirm or CourierCommand.Stop or CourierCommand.Settle or CourierCommand.VerifySettlement or CourierCommand.SettleSent)) return Refuse("UnsettledHandOff");
            if (lease == null)
            {
                if (!automation.TryAcquire("courier carrier", out lease)) return Refuse("AutomationBusy");
                if(needsSuppression?.Invoke()!=false)suppress = suppression.Acquire();
                stopped = false;
            }
            // Only a step that moves the character, trades or takes a bag baseline waits for AutoRetainer and Lifestream
            // to settle; ledger-only steps (Finish, Stop, settlements, Unclaim) move nothing and are never held back by them.
            if (m.Command is CourierCommand.Travel or CourierCommand.Receive or CourierCommand.Send or CourierCommand.ReturnHome or CourierCommand.Claim)
            {
                var budget = deadline.Expires - DateTimeOffset.UtcNow;
                if (budget > CourierBusyGate.Budget) budget = CourierBusyGate.Budget;
                if (await CourierBusyGate.WaitIdleAsync(suppression, lifestreamBusy, () => stopped || disposed, budget) is { } busy)
                    return stopped || disposed ? Refuse("CarrierDisarmed") : Refuse(busy.Code, busy.Detail);
            }
            if (trade is ICourierHoldPort holds && m.Command is not (CourierCommand.Stop or CourierCommand.Finish or CourierCommand.Confirm or CourierCommand.Unclaim or CourierCommand.SettleSent))
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
            // A refused ledger-only command keeps its refusal as the step's receipt, so its intent never stays unsettled.
            Result<CourierReceipt> Recorded(string code)
            {
                var refusal = Refuse(code);
                ledger.Steps[m.Step] = step with { Result = refusal }; store.Save(ledger); return refusal;
            }
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
                case CourierCommand.Claim:
                    // The run's source is this carrier: units it already holds enter the run's custody so the hand-in
                    // that follows trades only custody, as every send does. Nothing moves; only the ledger changes,
                    // saved together with this step's receipt. A claim is taken only while the run holds no custody,
                    // and only for units the fresh bags hold.
                    if (m.Manifest.IsDefaultOrEmpty || m.Manifest.Any(s => s.Units <= 0 || s.Units > s.StackSize ||
                            s.IsGil && (s.Hq || s.StackSize != CourierPlanner.GilCap)))
                        return Recorded("InvalidManifest");
                    if (m.Manifest.Any(s => !CourierPlanner.Exact(s.Source, m.Carrier) || !CourierPlanner.Exact(s.Destination, m.Partner)))
                        return Recorded("ManifestPartnerMismatch");
                    if (!ledger.Units.IsEmpty || ledger.Unconfirmed != null) return Recorded("ClaimCustodyNotEmpty");
                    if (m.Manifest.GroupBy(s => (s.Item, s.Hq)).FirstOrDefault(g => before.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => (long)s.Units)) is { } unheld)
                    {
                        warning($"{m.Run}/{m.Step} Claim: item {unheld.Key.Item}{(unheld.Key.Hq ? " HQ" : "")}: carrier holds {before.Count(unheld.Key.Item, unheld.Key.Hq):N0}, the claim needs {unheld.Sum(s => (long)s.Units):N0}.");
                        return Recorded(unheld.Key.Item == CourierPlanner.GilItemId ? "CarrierGilShort" : "CarrierStockShort");
                    }
                    ledger.Units = Custody.Apply(ledger.Units, m.Manifest, 1);
                    // The send that delivers the claim requires the bags to be unchanged since this point.
                    ledger.LastConfirmedBags = before;
                    break;
                case CourierCommand.Unclaim:
                    // Claimed units no hand-in reached return to the carrier's own stock, by ledger alone. Only exactly the
                    // ledger's units, only units this carrier itself sourced (claimed stock), and only while the bags still
                    // hold every claimed item unchanged since the claim; anything else stays custody for acknowledgement.
                    if (m.Manifest.IsDefaultOrEmpty || !Custody.Covers(ledger.Units, m.Manifest) || !Custody.Covers(m.Manifest, ledger.Units))
                        return Recorded("UnclaimCustodyChanged");
                    if (ledger.Units.Any(s => !CourierPlanner.Exact(s.Source, m.Carrier)))
                        return Recorded("UnclaimNotClaimed");
                    if (ledger.Units.Where(s => !s.IsGil).GroupBy(s => (s.Item, s.Hq)).FirstOrDefault(g =>
                            ledger.LastConfirmedBags?.Count(g.Key.Item, g.Key.Hq) != before.Count(g.Key.Item, g.Key.Hq) ||
                            before.Count(g.Key.Item, g.Key.Hq) < g.Sum(s => (long)s.Units)) is { } changed)
                    {
                        warning($"{m.Run}/{m.Step} Unclaim: item {changed.Key.Item}{(changed.Key.Hq ? " HQ" : "")}: carrier holds {before.Count(changed.Key.Item, changed.Key.Hq):N0}, " +
                            $"{ledger.LastConfirmedBags?.Count(changed.Key.Item, changed.Key.Hq).ToString("N0") ?? "unknown"} at the claim, {changed.Sum(s => (long)s.Units):N0} claimed.");
                        return Recorded("UnclaimBagsChanged");
                    }
                    ledger.Units = [];
                    break;
                case CourierCommand.Confirm:
                case CourierCommand.VerifySettlement:
                case CourierCommand.Settle:
                    if (m.ConfirmStep == null || ledger.Unconfirmed != m.ConfirmStep ||
                        !ledger.Steps.TryGetValue(m.ConfirmStep.Value, out var staged)) return Refuse("NoPendingHandOff");
                    var pending = staged.Command;
                    var sign = pending.Command == CourierCommand.Receive ? 1 : -1;
                    var credit = pending.Manifest;
                    IReadOnlyDictionary<(uint Item, bool Hq), long>? record = null;
                    if(m.Command is CourierCommand.VerifySettlement or CourierCommand.Settle){
                        if(staged.Before==null || m.OwnerEvidence==null)
                            return Refuse("SettlementEvidenceMissing", $"hand-off {pending.Step}: {(staged.Before==null?"no carrier bags before it":"no owner evidence")}.");
                        // This side's own trade record breaks a tie the two sides' bags cannot (an outside change on a manifest key):
                        // it settles at the record when the record equals either side's bag change, not only this side's.
                        record = await Custody.RecordOf(trade, pending.Step);
                        var settled = Custody.Reconcile(new(staged.Before,before),m.Carrier,m.OwnerEvidence,pending.Partner,pending.Manifest,sign,record);
                        if (!settled.Outside.IsEmpty)
                            warning($"{m.Run}/{pending.Step} SettlementOutsideChange: ignored as outside the trade: {string.Join("; ", settled.Outside)}.");
                        if (!settled.Settled) return Refuse(settled.Code!, $"hand-off {pending.Step}: {settled.Detail}");
                        credit = settled.Moved;
                    }
                    if (m.Command is CourierCommand.Settle or CourierCommand.VerifySettlement && staged.Result?.Outcome==CourierOutcome.Succeeded &&
                        (staged.Result.Value?.Evidence is not {} recorded ||
                            Custody.Reconcile(recorded,m.Carrier,m.OwnerEvidence!,pending.Partner,pending.Manifest,sign,record) is not {Settled:true} saved ||
                        !saved.Moved.SequenceEqual(credit))) return Refuse("SettledReceiptChanged");
                    if (m.Command is CourierCommand.Settle or CourierCommand.VerifySettlement && staged.Result?.Outcome != CourierOutcome.Succeeded)
                    {
                        if (trade is not ICourierSettlement recovery || staged.Before == null) return Refuse("SettlementUnavailable", "this client cannot settle its trade record.");
                        var reconciled = credit;
                        await calls.Call("CarrierSettlement",15,d=>recovery.Settle(pending.Step,pending.Partner,m.OwnerEvidence!,reconciled,d));
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
                        x.Value.Command.ConfirmStep==pending.Step && x.Value.Command.Command is CourierCommand.Settle or CourierCommand.VerifySettlement or CourierCommand.SettleSent).ToArray())
                        ledger.Steps[abandoned.Key]=abandoned.Value with {Result=Result<CourierReceipt>.Fail("SettlementSuperseded",pending.Step.ToString())};
                    ledger.LastConfirmed = ledger.Unconfirmed;
                    ledger.LastConfirmedBags = staged.Result!.Value!.Evidence!.After;
                    ledger.Unconfirmed = null; break;
                case CourierCommand.SettleSent:
                    // The owner settles a hand-in its own receive cannot prove (the goods moved on before it looked).
                    // The carrier settles only its own completed send: its recorded receipt
                    // shows exactly the manifest left its bags, and those bags still hold, for every manifest item, what they
                    // held right after that send. The owner's evidence is kept in this step for audit only.
                    if (m.ConfirmStep == null || ledger.Unconfirmed != m.ConfirmStep || !ledger.Steps.TryGetValue(m.ConfirmStep.Value, out var shipped))
                        return Recorded("NoPendingHandOff");
                    if (shipped.Command.Command != CourierCommand.Send) return Recorded("SettleSentNotASend");
                    if (!shipped.Command.Manifest.SequenceEqual(m.Manifest)) return Recorded("SettleSentManifestMismatch");
                    if (shipped.Result is not { Outcome: CourierOutcome.Succeeded, Value.Evidence: { } shippedProof } ||
                        !Custody.Matches(shippedProof, m.Carrier, shipped.Command.Manifest, -1))
                        return Recorded("SettleSentUnproven");
                    if (shipped.Command.Manifest.Where(x => !x.IsGil).GroupBy(x => (x.Item, x.Hq))
                            .FirstOrDefault(g => before.Count(g.Key.Item, g.Key.Hq) != shippedProof.After.Count(g.Key.Item, g.Key.Hq)) is { } returned)
                    {
                        // The send receipt alone proves the manifest left; later stock movement (its own retainers, MMF)
                        // cannot undo a completed trade. Report it; the next send still refuses on the last confirmed bags.
                        warning($"{m.Run}/{m.Step} SettleSentBagsChanged: item {returned.Key.Item}{(returned.Key.Hq ? " HQ" : "")}: carrier holds {before.Count(returned.Key.Item, returned.Key.Hq):N0}, " +
                            $"{shippedProof.After.Count(returned.Key.Item, returned.Key.Hq):N0} right after the send; settling from the send receipt.");
                    }
                    ledger.Units = Custody.Apply(ledger.Units, shipped.Command.Manifest, -1);
                    foreach(var abandoned in ledger.Steps.Where(x=>x.Key!=m.Step && x.Value.Result==null &&
                        x.Value.Command.ConfirmStep==shipped.Command.Step && x.Value.Command.Command is CourierCommand.Settle or CourierCommand.VerifySettlement or CourierCommand.SettleSent).ToArray())
                        ledger.Steps[abandoned.Key]=abandoned.Value with {Result=Result<CourierReceipt>.Fail("SettlementSuperseded",shipped.Command.Step.ToString())};
                    ledger.LastConfirmed = ledger.Unconfirmed;
                    ledger.LastConfirmedBags = shippedProof.After;
                    ledger.Unconfirmed = null;
                    evidence = shippedProof;
                    information?.Invoke($"{m.Run}/{shipped.Command.Step} carrier settled its send from its own receipt ({m.Step}).");
                    break;
                case CourierCommand.ReturnHome:
                    await ReturnHomeAsync(cfg, m, ledger.Start);
                    // World arrival and bell range can be reached while Lifestream is still finishing its travel; the
                    // receipt waits (bounded) for Lifestream to settle, so the owner's next step does not meet a carrier still moving.
                    var settleBudget = deadline.Expires - DateTimeOffset.UtcNow;
                    if (settleBudget > CourierBusyGate.Budget) settleBudget = CourierBusyGate.Budget;
                    if (await CourierBusyGate.WaitIdleAsync(null, lifestreamBusy, () => stopped || disposed, settleBudget) is { } unsettled && !stopped && !disposed)
                        warning($"{m.Run}/{m.Step} ReturnHomeUnsettled: {unsettled.Code}: {unsettled.Detail}; the carrier is home.");
                    break;
                case CourierCommand.Finish:
                    if (!ledger.Units.IsEmpty || ledger.Unconfirmed != null) return Refuse("CustodyRemaining");
                    ledger.Run = null; ledger.RunKey=null;ledger.OwnerEndpoint=null;ledger.Completed.Add(m.Run);
                    Release(force:true); break;
                case CourierCommand.Stop:
                    ledger.Paused = true;
                    Release(force: true); break;
                default: return Recorded("UnknownCommand");
            }
            var result = Result<CourierReceipt>.Ok(new(m.Run, m.Step, before, evidence, ledger.Units));
            ledger.Steps[m.Step] = step with { Result = result };
            store.Save(ledger);
            return result;
        }
        catch (Exception e)
        {
            // A Stop that faulted (for example on a locked ledger file) still stops: never keep the locks behind it.
            Release(force: m.Command == CourierCommand.Stop);
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
            finally{try{if(refused || stopped || PausedRun())Release();}finally{gate.Release();}}
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
    /// <summary>
    /// Forgets the held run: the ledger's custody, unconfirmed hand-off, confirmations, pause and steps go, this client's
    /// trade checkpoint goes, and every lock is released. Goods in the bags stay exactly where they are, as the carrier's
    /// own. The run is recorded closed, so a late command for it refuses. Call holding the gate.
    /// </summary>
    private void Forget(CarrierLedger ledger, string why)
    {
        if (ledger.Run is not { } run) return;
        var held = ledger.Units; var unconfirmed = ledger.Unconfirmed;
        var steps = ledger.Steps.Keys.Concat(new[] { ledger.Unconfirmed, ledger.LastConfirmed }.OfType<Guid>()).ToHashSet();
        ledger.Run = null; ledger.RunKey = null; ledger.OwnerEndpoint = null; ledger.Start = null;
        ledger.Units = []; ledger.Unconfirmed = null; ledger.LastConfirmed = null; ledger.LastConfirmedBags = null;
        ledger.Paused = false; ledger.Steps.Clear(); ledger.NotDelivered.Clear(); ledger.Completed.Add(run);
        store.Save(ledger);
        try { (trade as ICourierTradeForget)?.Forget(steps); }
        finally { ReleaseNow(); }
        var units = held.Where(s => !s.IsGil).Sum(s => (long)s.Units);
        var gil = held.Where(s => s.IsGil).Sum(s => (long)s.Units);
        (information ?? warning)($"{run} CarrierRunForgotten: {why}; " +
            (held.IsEmpty ? "it held nothing" : $"{units:N0} item(s) and {gil:N0} gil it held stay in this character's bags as its own") +
            (unconfirmed is { } u ? $"; hand-off {u} was unconfirmed." : "."));
    }
    /// <summary>
    /// The carrier pane's Clear: forgets the carried run (if any) and releases every lock, whatever the ledger holds or
    /// awaits. A command still in flight is stopped; if it does not drain, the run is forgotten anyway and again once it ends.
    /// </summary>
    public async Task ForgetLocalAsync()
    {
        stopped = true;
        try { await calls.Call("CarrierCancelPreLock", 10, trade.CancelPreLockAsync); }
        catch (Exception e) { warning($"CarrierClearCancelPreLock: {e.Message}"); }
        var entered = await gate.WaitAsync(TimeSpan.FromSeconds(15));
        Guid? forgotten = null;
        try
        {
            var ledger = store.Load();
            forgotten = ledger.Run;
            Forget(ledger, "cleared on this client");
            ReleaseNow();
        }
        finally { if (entered) gate.Release(); }
        if (entered || forgotten is not { } run) return;
        warning($"{run} CarrierClearDrainDeadline: 15s elapsed; forgotten now and again once the command in flight ends.");
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try { if (store.Load() is { Run: { } again } ledger && again == run) Forget(ledger, "cleared on this client (after the command in flight)"); }
            catch (Exception e) { warning($"{run} CarrierClearRetryFailed: {e.Message}"); }
            finally { gate.Release(); }
        });
    }
    /// <summary>
    /// From the link's presence tick: forgets the held run when the owner client that ran it is linked again and
    /// advertises that it no longer has it (it cleared the run while this carrier was unreachable). Only between
    /// commands and after a quiet spell, so a presence published just before a Begin never counts.
    /// </summary>
    internal void ForgetOrphaned(IEnumerable<(Guid Instance, CourierPeer Peer)> owners)
    {
        if (disposed || active != null || DateTimeOffset.UtcNow - lastCommand < OrphanQuiet || !gate.Wait(0)) return;
        try
        {
            var ledger = store.Load();
            if (ledger.Run is not { } run) return;
            // The owner client is the endpoint that began the run; failing that (it restarted), the run owner logged in.
            var advertising = owners.Where(o => o.Peer.OwnerRun != null).ToArray();
            var linked = advertising.Where(o => ledger.OwnerEndpoint is { } ep && o.Instance == ep).Select(o => o.Peer).ToArray();
            if (linked.Length == 0 && RunOwner(ledger) is { } owner)
                linked = advertising.Where(o => CourierPlanner.Exact(o.Peer.Character, owner)).Select(o => o.Peer).ToArray();
            if (linked.Length == 0 || linked.Any(p => p.OwnerRun == run)) return;
            Forget(ledger, $"{linked[0].Character.CharacterName}'s client no longer has this run");
        }
        catch (Exception e) { warning($"CarrierOrphanCheck: {e.Message}"); }
        finally { gate.Release(); }
    }
    /// <summary>The character that owns the held run, from the commands it sent.</summary>
    private static OwnerScope? RunOwner(CarrierLedger ledger) =>
        ledger.Steps.Values.Select(s => s.Command).FirstOrDefault(c => c.Run == ledger.Run)?.Owner;
    /// <summary>Whether a command comes from the owner client of the held run: the endpoint that began it, or the run owner itself.</summary>
    private static bool SameOwnerClient(CarrierLedger ledger, OwnerScope owner, OwnerScope sender, Guid? endpoint) =>
        endpoint != null && endpoint == ledger.OwnerEndpoint ||
        RunOwner(ledger) is { } held && (CourierPlanner.Exact(held, owner) || CourierPlanner.Exact(held, sender));
    private bool PausedRun(){try{return store.Load() is {Run:not null,Paused:true};}catch(Exception e){warning($"CarrierLedgerUnreadable: {e.Message}");return false;}}
    // Locks outlive a refused command only while a live (unpaused) run holds custody: the owner's next step needs them.
    // A paused or stopped run never keeps them; the ledger and the last confirmed bags still guard every unit, and the
    // owner's Begin or settlement re-takes them.
    // A forgotten run's locks go now, not behind late calls that may never settle; a release already queued becomes a no-op.
    private void ReleaseNow()
    {
        Release(force: true, now: true);
        foreach (var release in deferredReleases.Keys) if (deferredReleases.TryRemove(release, out _)) release();
    }
    private void Release(bool force=false, bool now=false)
    {
        if (!force && store.Load() is {Paused:false} held && (!held.Units.IsEmpty || held.Unconfirmed!=null)) return;
        var heldSuppression=Interlocked.Exchange(ref suppress,null);
        var heldLease=Interlocked.Exchange(ref lease,null);
        var heldTrade=Interlocked.Exchange(ref tradeHold,null);
        void Drop(){try{heldTrade?.Dispose();}finally{try{heldSuppression?.Dispose();}finally{heldLease?.Dispose();}}}
        deferredReleases.TryAdd(Drop,0);
        void Once(){if(deferredReleases.TryRemove(Drop,out _))Drop();}
        if(disposed||now)Once();else calls.WhenSettled(Once);
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
