using System.Collections.Immutable;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Trades;
using RQ.Automation;
using RQ.Domain;

namespace RQ.Courier;

internal sealed class TradeCheckpoint
{
    public Guid Step {get;set;}
    public bool? DispatchAuthorized {get;set;}
    public bool PreLockCancelled {get;set;}
    public CourierReceiveArm? Arm {get;set;}
    public CourierReceiveArm? LastSettledArm {get;set;}
    public InventoryView? ReceiveAfter {get;set;}
    public TradeEvidence? SenderEvidence {get;set;}
    /// <summary>Set when the receive was closed by attestation rather than its own trade record (DeliveredThenPlaced; OperatorAttested in older checkpoints).</summary>
    public string? Disposition {get;set;}
    public List<BagStock> Original {get;set;}=[];
    public bool Receiving {get;set;}
    public List<TradeItem> Items {get;set;}=[];
    public TradeRecoveryState Recovery {get;set;}=new();
}
internal sealed class CourierTrade(CourierGame game,ITradeIo io,AutomationLease lease,IDalamudPluginInterface pi,
    DalamudExternalUiAutomationSuppression external,IPluginLog log,string directory) : ICourierTradePort, ICourierDispatchEvidence, ICourierSettlement, ICourierTradeRecord, ICourierHoldPort,
    ICourierReceiveStall, ICourierReceiveAttestation, ICourierTradeForget, ITradeSuppression, IDisposable
{
    // live is false once the checkpoint was forgotten: a stopped trade's late record never writes it back.
    private sealed class Intent(CourierStore<TradeCheckpoint> store, TradeCheckpoint current, Func<bool>? live = null) : ITradeIntentStore
    {
        public IList<TradeItem> Remaining => current.Items;
        public TradeRecoveryState Recovery => current.Recovery;
        public void Save(){if(live?.Invoke()!=false)store.Save(current);}
    }
    private readonly CourierStore<TradeCheckpoint> store=new(Path.Combine(directory,"courier-trade.json"),s=>log.Warning(s));
    // Paces UI actions only; every slot and the final batch are still verified against the trade window and bags.
    private static TradeTimingOptions Timing => new(){ActionDelayMilliseconds=80};
    // A partner window that opens and closes before either side locks moved nothing; retry it a few times.
    private const int MaxTradeOpenBounces = 3;
    private static readonly TimeSpan BounceBackoff = TimeSpan.FromSeconds(4);
    private TradeRunner? sender;
    private TradeReceiveOperation? receiver;
    private IDisposable? prompts;
    private HashSet<string>? dropbox, yesAlready;
    private bool stopped,locked;
    // Steps of forgotten runs: a trade for one ends on its next tick without touching the checkpoint or this side's trade.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,byte> forgotten=new();
    // The step of the trade that last took this side's sender or receiver (framework thread only).
    private Guid operationStep;
    // A trade is in flight from its checkpoint's first save until it ends, including a receive awaiting its baseline
    // and a send backing off after a bounced window (framework thread only).
    private bool inFlight;
    // Set while a forgotten trade's window is still open: unloading then keeps the auto-confirm suppression.
    private volatile bool retainSuppression;
    private Guid stalledStep;
    private CourierTradeHold? hold;
    public Task<Result<IDisposable>> BeginHold(Guid run, Deadline deadline)
    {
        var last="";
        return game.Poll<IDisposable>(deadline, () => {
            var next = new CourierTradeHold(pi, $"Quartermaster.Courier/{run:N}/{Guid.NewGuid():N}", s=>log.Warning(s));
            var result=next.Begin();
            // MarketMafioso refuses while its own trade window or queue is busy, which clears on its own: ask again until the deadline.
            if(result.Outcome!=CourierOutcome.Succeeded)
            {
                if(result.Code!="TradeHoldRefused")return result;
                if(last.Length==0)log.Warning($"TradeHoldRefused: {result.Detail}; asking again until {deadline.Expires:O}.");
                last=$"{result.Code}: {result.Detail}";return null;
            }
            hold=next;
            // Releasing the scope also forgets it, so a later trade refuses TradeHoldLost instead of checking a dead hold.
            return Result<IDisposable>.Ok(new HoldScope(()=>{try{next.Dispose();}finally{Interlocked.CompareExchange(ref hold,null,next);}}));
        },pollMilliseconds:500,waiting:()=>last,expiry:CourierOutcome.Failed);
    }
    /// <summary>
    /// Before a trade is dispatched: a hold MarketMafioso let lapse while no trade was open here is asked for again,
    /// once, on the framework thread where MarketMafioso can see its own window and queue.
    /// </summary>
    public Task<Result<bool>> RegainHoldAsync(Deadline deadline) => game.Poll<bool>(deadline, () => {
        if (hold is not { } held) return Result<bool>.Fail("TradeHoldLost","Missing courier work scope.");
        try { held.Check(true, between: !inFlight && WindowClosed()); return Result<bool>.Ok(true); }
        catch (CourierFault e) { return new(e.Outcome, e.Code, e.Message); }
    });
    private sealed class HoldScope(Action release) : IDisposable
    {
        private Action? pending = release;
        public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
    }
    // Between steps: a lapsed hold is left for RegainHoldAsync ahead of the next trade.
    public void CheckHold() => hold?.Check(false, between: true);
    public void SuppressTradeAutoConfirm()
    {

        // The courier's own YesAlready and Dropbox entries outlive an unload with a window open; the shared external
        // suppression (released on unload) covers TextAdvance only.
        if(pi.TryGetData<HashSet<string>>("YesAlready.StopRequests",out var yes) && yes!=null)
        {lock(yes){yes.Add(CourierOwner);yesAlready=yes;}}
        else if(pi.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName=="YesAlready"))
            throw new InvalidOperationException("YesAlreadySuppressionUnavailable");
        prompts??=external.Acquire();
        if(pi.TryGetData<HashSet<string>>("Dropbox.StopRequests",out var set) && set!=null)
        {lock(set){set.Add(CourierOwner);dropbox=set;}}
        else if(pi.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName.Equals("Dropbox",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("DropboxSuppressionUnavailable");
    }
    private const string CourierOwner="Quartermaster.Courier";
    // Lifts this plugin's YesAlready and Dropbox pauses even when this load never set them (a load that died without disposing left them).
    public void RestoreTradeAutoConfirm()
    {
        Lift("YesAlready.StopRequests",ref yesAlready);
        Lift("Dropbox.StopRequests",ref dropbox);
        prompts?.Dispose();prompts=null;
    }
    private void Lift(string tag,ref HashSet<string>? held)
    {
        var set=held;
        if(set==null && pi.TryGetData<HashSet<string>>(tag,out var shared))set=shared;
        if(set!=null)lock(set)set.Remove(CourierOwner);
        held=null;
    }
    // On load: a YesAlready or Dropbox pause a previous load left behind is lifted once no trade window is open or locked. A
    // cancellation a dead load left pending with no confirmation submitted is settled here once the window is observed
    // closed: without our confirmation that window moved nothing, so the checkpoint drops the pending flag (exactly as
    // the trade runner does when it observes the closure) and new trades no longer refuse SettlementRequired over it.
    // A pending cancellation awaiting confirmation verification keeps the pause (the run's settlement lifts it); a
    // window that is open, locked or unreadable keeps it until it closes.
    public void RestoreLeftoverSuppression()=>_=Task.Run(async()=>{
        try{
            var done=await game.Poll<bool>(new("CourierTradeLoadRestore",DateTimeOffset.MaxValue),()=>{
                var checkpoint=store.Load();
                if(checkpoint.Recovery.CancellationPending)
                {
                    // Observe-only recovery (or a confirmation awaiting verification) is the run's to settle, never cleared here.
                    if(checkpoint.Recovery.AwaitingVerification!=null || checkpoint.Recovery.ObserveOnly)return Result<bool>.Ok(false);
                    if(inFlight || !WindowClosed())return null;
                    checkpoint.Recovery.CancellationPending=false;store.Save(checkpoint);
                    log.Warning($"CourierTradeCancellationSettledOnLoad: step {checkpoint.Step}'s trade cancellation was left pending by an earlier load; " +
                        "the window is closed and no confirmation was submitted, so nothing moved in it. The pending flag is cleared and the auto-confirm pauses are lifted.");
                }
                if(!WindowClosed())return null;
                if(!inFlight)RestoreTradeAutoConfirm();
                return Result<bool>.Ok(true);
            },true,500);
            if(done.Outcome!=CourierOutcome.Succeeded)log.Warning($"CourierTradeLoadRestore {done.Code}: {done.Detail}");
        }
        catch(OperationCanceledException){}
        catch(Exception e){log.Warning($"CourierTradeLoadRestore {e.GetType().Name}: {e.Message}");}
    });
    // Stops (before any lock) and drops this side's trade; it ends with the window as it is.
    private void DropTrade()
    {
        stopped=true;
        if(!locked && !io.IsRemoteLocked){sender?.Stop();receiver?.Stop();}
        sender?.Dispose();sender=null;receiver=null;locked=false;inFlight=false;
    }
    private bool WindowClosed(){try{return !io.IsTradeOpen && !io.IsRemoteLocked;}catch{return false;}}
    public void RetainTradeSuppressionOnDispose() { /* Preserve stop request ownership until closure. */ }
    public Task<Result<bool>> CancelPreLockAsync(Deadline d)=>game.Poll<bool>(d,()=>{
        stopped=true;
        if(!locked && !io.IsRemoteLocked){sender?.Stop();receiver?.Stop();}
        return Result<bool>.Ok(true);
    });
    // A step credited nothing when no receive window is armed, no confirmation marker was ever persisted (the
    // runner saves it before submitting confirmation) and every queued unit is still queued. Whether the step
    // ended Cancelled, Failed or otherwise, a window that closed in that state moved nothing.
    // No window awaits verification, cancellation or a receive baseline: whatever earlier windows moved is already credited.
    private static bool BatchUnsettled(TradeCheckpoint state) =>
        state.Arm==null && state.Recovery.AwaitingVerification==null && !state.Recovery.CancellationPending &&
        state.Items.Any(i=>i.Quantity>0);
    private static bool NothingCredited(TradeCheckpoint state) =>
        state.Arm==null && state.Recovery.AwaitingVerification==null && !state.Recovery.CancellationPending &&
        state.Items.Sum(i=>(long)i.Quantity)==state.Original.Sum(x=>(long)x.Units);
    // Derived from the durable record too (when no trade runs here), so a step that ended before PreLockCancelled
    // covered failures, or whose process died after the window closed, still proves it was never credited.
    internal static bool NeverCredited(TradeCheckpoint record,Guid step,bool idle)=>
        record.Step!=step || record.DispatchAuthorized==false || record.PreLockCancelled || idle && NothingCredited(record);
    public Task<Result<bool>> WasNotOpened(Guid step,Deadline d)=>game.Poll<bool>(d,()=>
        Result<bool>.Ok(!io.IsTradeOpen && NeverCredited(store.Load(),step,sender==null && receiver==null && !locked)));
    public Task<Result<bool>> SendAsync(OwnerScope p,ImmutableArray<Shipment> m,Deadline d)=>Execute(p,m,d,false);
    public Task<Result<bool>> ReceiveAsync(OwnerScope p,ImmutableArray<Shipment> m,Deadline d)=>Execute(p,m,d,true);
    private Task<Result<bool>> Execute(OwnerScope p,ImmutableArray<Shipment> manifest,Deadline d,bool receive)
    {
        var started=CourierGame.TradeStarted.Value;
        var step=CourierGame.Step.Value; TradePartner? partner=null;TradeCheckpoint? state=null;
        var allowed=CourierGame.CanContinue.Value;
        int bounces=0; var retryAt=DateTimeOffset.MinValue;
        bool Live()=>!forgotten.ContainsKey(step);
        return game.Poll<bool>(d,()=>{
            if(!Live()){if(inFlight && operationStep==step)DropTrade();return new(CourierOutcome.Cancelled,"TradeForgotten",d.Name);}
            Result<bool>? StartSender()
            {
                sender=new(new Intent(store,state!,Live),Timing,io,this,log);
                sender.BatchLocking+=(_,_)=>locked=true;
                sender.BatchVerified+=(_,_)=>locked=false;
                var start=sender.Start(partner!);
                if(!start.Success)return End(CourierOutcome.Failed,start.Code??"SenderRejected",start.Message);
                started?.Invoke();started=null;
                return null;
            }
            Result<bool> End(CourierOutcome outcome,string code,string detail)
            {
                if(outcome==CourierOutcome.Indeterminate && sender is {} sent)
                    game.WatchLate(d.Name,()=>sent.ObserveLateEvidence()!=null);
                if(outcome==CourierOutcome.Indeterminate && receiver is {} received && code=="ReceiveDeadline")
                    game.WatchLate(d.Name,()=>{received.Tick(DateTimeOffset.UtcNow);return received.Evidence!=null;});
                // Never opened only if no window was credited: a multi-window trade stopped after its first
                // verified window has moved items, and must settle rather than read as never opened.
                if(outcome!=CourierOutcome.Succeeded && state!=null && !locked && !io.IsRemoteLocked && !io.IsTradeOpen && NothingCredited(state))
                {state.PreLockCancelled=true;store.Save(state);}
                sender?.Dispose();sender=null;receiver=null;locked=false;inFlight=false;
                // A trade can end (closure deadline, partial stop, adapter fault) with the window still open and our
                // items in it: keep YesAlready/Dropbox paused until it closes instead of releasing them here.
                if(state?.Recovery.CancellationPending!=true){if(WindowClosed())RestoreTradeAutoConfirm();else RestoreLeftoverSuppression();}
                return new(outcome,code,detail,outcome==CourierOutcome.Succeeded);
            }
            try{
                CourierFault? lost = null;
                try { if (hold == null) throw new CourierFault(CourierOutcome.Failed,"TradeHoldLost","Missing courier work scope."); hold.Check(); }
                catch (CourierFault e) {
                    if (state == null) return Result<bool>.Fail(e.Code,e.Message);
                    lost = e; stopped = true;
                    if(!locked && !io.IsRemoteLocked){sender?.Stop(e.Message); receiver?.Stop();}
                }
                if(allowed?.Invoke()==false){
                    if(state==null)return new(CourierOutcome.Cancelled,"TradeDisarmed",d.Name);
                    stopped=true;
                    if(!locked && !io.IsRemoteLocked){sender?.Stop();receiver?.Stop();}
                }
                if(state==null){
                    if(lease.Holder is not ("courier owner" or "courier carrier") || step==Guid.Empty)
                        return Result<bool>.Fail("TradeAuthorityMissing",d.Name);
                    if(DateTimeOffset.UtcNow>=d.Expires)return Result<bool>.Fail("TradeStartExpired",d.Name);
                    // Carried gil trades as NQ gil; HQ gil, item 0 and empty quantities never do.
                    if(manifest.IsDefaultOrEmpty || manifest.Any(s=>s.Item==0 || s.Item==TradePlanner.GilItemId && s.Hq || s.Units<=0))
                        return Result<bool>.Fail("InvalidItemManifest",d.Name);
                    var prior=store.Load();
                    if(prior.Arm!=null || prior.Recovery.AwaitingVerification!=null || prior.Recovery.CancellationPending || prior.Step==step)
                        return new(CourierOutcome.Indeterminate,"SettlementRequired",prior.Step.ToString());
                    partner=game.Partner(p);
                    if(partner==null)return Result<bool>.Fail("PartnerNotTradeable",p.CharacterName);
                    state=new(){Step=step,DispatchAuthorized=false,Receiving=receive,Items=[..manifest.GroupBy(s=>(s.Item,s.Hq)).Select(g=>new TradeItem{
                        ItemId=g.Key.Item,IsHighQuality=g.Key.Hq,Quantity=g.Sum(s=>s.Units),ItemName=g.First().Name})]};
                    state.Original=[..state.Items.Select(i=>new BagStock(i.ItemId,i.IsHighQuality,i.Quantity))];
                    store.Save(state);stopped=locked=false;stalledStep=Guid.Empty;operationStep=step;inFlight=true;
                    SuppressTradeAutoConfirm();
                    if(!receive){
                        state.DispatchAuthorized=true;store.Save(state);
                        if(StartSender() is {} rejected)return rejected;
                    }
                }
                OperationResult? result;
                if(receive){
                    if(stalledStep==step && !io.IsTradeOpen && !io.IsRemoteLocked){
                        stalledStep=Guid.Empty;
                        var unverified=string.Join(", ",state.Items.Where(i=>i.Quantity>0).Select(i=>$"{i.ItemName ?? i.ItemId.ToString()}{(i.IsHighQuality?" HQ":"")} x{i.Quantity:N0}"));
                        var detail=$"Sender finished; receive {(state.Arm!=null?"armed on an unverified window":"not armed")}, unverified {(unverified.Length>0?unverified:"nothing")}.";
                        log.Warning($"OwnerReceiveStalledAfterSenderFinished: {d.Name} step {step}: {detail} The armed baseline stays for settlement.");
                        receiver?.Stop();
                        return End(CourierOutcome.Indeterminate,"OwnerReceiveStalledAfterSenderFinished",detail);
                    }
                    if(receiver==null){
                        if(stopped) return End(CourierOutcome.Cancelled,lost?.Code??"ReceiveStopped",lost?.Message??d.Name);
                        if(game.Bags() is not {} baseline) {
                            if(DateTimeOffset.UtcNow>=d.Expires)return End(CourierOutcome.Failed,"ReceiveBaselineUnavailable",d.Name);
                            return null;
                        }
                        state.Arm=new(step,p,baseline);state.DispatchAuthorized=true;store.Save(state);
                        receiver=new(io,partner!,state.Items.Where(i=>i.Quantity>0).Select(i=>new TradeOfferedItem(i.ItemId,i.IsHighQuality,i.Quantity)).ToArray(),d.Expires,s=>log.Warning(s));
                    }
                    started?.Invoke();started=null;
                    result=receiver.Tick(DateTimeOffset.UtcNow);
                    if(result.Status==OperationStatus.Succeeded){
                        var bag=game.Bags();
                        var arm=state.Arm!;
                        if(bag==null || !CourierPlanner.Exact(arm.Before.Character,bag.Character) ||
                            arm.Before.Stock.Concat(bag.Stock).Select(x=>new TradeInventoryKey(x.ItemId,x.Hq))
                            .Concat(receiver.Received!.Keys).Append(new TradeInventoryKey(TradePlanner.GilItemId,false)).Distinct().Any(k=>
                                (long)bag.Count(k.ItemId,k.IsHighQuality)-arm.Before.Count(k.ItemId,k.IsHighQuality)!=receiver.Received!.GetValueOrDefault(k)))
                            return End(CourierOutcome.Indeterminate,"ReceiveBaselineMismatch",d.Name);
                        state.LastSettledArm=arm;state.Arm=null;state.ReceiveAfter=bag;
                        foreach(var (k,n) in receiver.Received!)state.Items.Single(i=>i.ItemId==k.ItemId && i.IsHighQuality==k.IsHighQuality).Quantity-=n;
                        store.Save(state);receiver=null;
                        if(state.Items.Any(i=>i.Quantity>0) && !stopped)return null;
                        if(state.Items.Any(i=>i.Quantity>0))return End(CourierOutcome.Indeterminate,"PartialReceiveStopped",d.Name);
                    }else if(result.Code=="ClosedBeforeConfirm" && !stopped && DateTimeOffset.UtcNow<d.Expires){state.Arm=null;store.Save(state);receiver=null;return null;}
                }else{
                    if(sender==null){
                        // Backing off after a bounced window; nothing is open and nothing was credited.
                        if(stopped)return End(lost!=null?CourierOutcome.Indeterminate:CourierOutcome.Cancelled,lost?.Code??"SenderStopped",lost?.Message??d.Name);
                        if(DateTimeOffset.UtcNow<retryAt)return null;
                        if(StartSender() is {} rejected)return rejected;
                    }
                    if(DateTimeOffset.UtcNow>=d.Expires && !stopped){stopped=true;sender!.Stop("Sender deadline "+d.Expires.ToString("O"));}
                    var phase=sender!.Snapshot.State;
                    sender.Tick(); result=sender.Result;
                    if(sender.IsActive)return null;
                    // The partner's window opened and closed again before this side locked anything (e.g. the
                    // partner was still settling after a login). A fresh runner offers the same unchanged queue.
                    // Only the window that bounced must be untouched: windows this step already verified stay credited,
                    // and the fresh runner offers just the remaining queue.
                    if(result is {Status:OperationStatus.Failed} bounced && phase is TradeExecutionState.OfferingItems or TradeExecutionState.WaitingForPartner &&
                        !locked && !io.IsRemoteLocked && !io.IsTradeOpen && BatchUnsettled(state))
                    {
                        var now=DateTimeOffset.UtcNow;
                        if(bounces<MaxTradeOpenBounces && !stopped && allowed?.Invoke()!=false && now+BounceBackoff<d.Expires)
                        {
                            bounces++;
                            log.Warning($"TradeOpenBounced: {d.Name} step {step} window closed in {phase} before lock ({bounced.Code}: {bounced.Message}); " +
                                $"retry {bounces}/{MaxTradeOpenBounces} in {BounceBackoff.TotalSeconds:0}s.");
                            // The finished runner released the auto-confirm suppression; keep it through the backoff.
                            SuppressTradeAutoConfirm();
                            sender=null;retryAt=now+BounceBackoff;return null;
                        }
                        var reason=bounces>=MaxTradeOpenBounces?$"retries exhausted ({bounces}/{MaxTradeOpenBounces})":
                            stopped || allowed?.Invoke()==false?"stopped":$"deadline {d.Expires:O} leaves no {BounceBackoff.TotalSeconds:0}s backoff";
                        log.Warning($"TradeOpenBounced: {d.Name} step {step} window closed in {phase} before lock ({bounced.Code}: {bounced.Message}); {reason}.");
                        result=new(OperationStatus.Failed,"TradeOpenBounced",$"Trade window closed before lock {bounces+1} time(s); {reason}. Last: {bounced.Message}");
                    }
                }
                if(result?.IsTerminal!=true)return null;
                if (receive && result.Status==OperationStatus.Cancelled && state.Arm is {} cancelledArm) {
                    var after=game.Bags();
                    if(after==null || !CourierPlanner.Exact(after.Character,cancelledArm.Before.Character) ||
                        after.Gil!=cancelledArm.Before.Gil ||
                        after.Stock.Concat(cancelledArm.Before.Stock).Any(x=>after.Count(x.ItemId,x.Hq)!=cancelledArm.Before.Count(x.ItemId,x.Hq)))
                        return End(CourierOutcome.Indeterminate,"ReceiveCancellationUnsettled",d.Name);
                    state.LastSettledArm=cancelledArm; state.Arm=null;state.ReceiveAfter=after;store.Save(state);
                }
                if (lost != null && result.Status != OperationStatus.Succeeded) return End(CourierOutcome.Indeterminate,lost.Code,lost.Message);
                return End(result.Status switch { OperationStatus.Succeeded=>CourierOutcome.Succeeded,OperationStatus.Cancelled=>CourierOutcome.Cancelled,
                    OperationStatus.Indeterminate=>CourierOutcome.Indeterminate,_=>CourierOutcome.Failed },result.Code,result.Message);
            }catch(Exception e){return End(CourierOutcome.Indeterminate,"TradeAdapterFault",e.Message);}
        },true,CourierGame.TradePollMilliseconds);
    }
    public Task<Result<bool>> EndStalledReceiveAsync(Guid step,Deadline d)=>game.Poll<bool>(d,()=>{
        if(receiver==null && sender==null)return Result<bool>.Fail("ReceiveNotRunning",step.ToString());
        stalledStep=step;
        return Result<bool>.Ok(true);
    });
    // The sender's evidence must show exactly the receive's original manifest left its bags, nothing else (gil included).
    public Task<Result<bool>> AttestReceived(Guid step,OwnerScope partner,TradeEvidence peer,string disposition,Deadline d,bool commit=true)=>game.Poll<bool>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || !state.Receiving || io.IsTradeOpen || receiver!=null || sender!=null || state.Recovery.CancellationPending)
            return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",step.ToString());
        if(!CourierPlanner.Exact(partner,peer.Before.Character) || !CourierPlanner.Exact(partner,peer.After.Character) ||
            state.Original.Concat(peer.Before.Stock).Concat(peer.After.Stock).Select(x=>(x.ItemId,x.Hq)).Append((ItemId: Custody.GilKey.Item, Hq: Custody.GilKey.Hq)).Distinct().Any(k=>
                state.Original.Where(x=>x.ItemId==k.ItemId && x.Hq==k.Hq).Sum(x=>(long)x.Units)!=(long)peer.Before.Count(k.ItemId,k.Hq)-peer.After.Count(k.ItemId,k.Hq)))
            return new(CourierOutcome.Indeterminate,"AttestedSenderDeltaMismatch",step.ToString());
        if(!commit)return Result<bool>.Ok(true); // Preflight: the checkpoint would close on this evidence.
        if(state.Arm!=null)state.LastSettledArm=state.Arm;
        state.Arm=null;state.SenderEvidence=peer;state.Disposition=disposition;
        foreach(var item in state.Items)item.Quantity=0;
        store.Save(state);
        log.Warning($"CourierReceiveAttested: step {step} closed as {disposition}; the sender's evidence shows the full manifest left its bags.");
        return Result<bool>.Ok(true);
    });
    // The step's record at rest: no trade of it running here, no window open, nothing armed, unverified or being cancelled.
    public Task<Result<IReadOnlyDictionary<(uint Item,bool Hq),long>>> CreditedAsync(Guid step,Deadline d)=>game.Poll<IReadOnlyDictionary<(uint Item,bool Hq),long>>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || state.Original.Count==0)return Result<IReadOnlyDictionary<(uint Item,bool Hq),long>>.Fail("TradeRecordMissing",step.ToString());
        if(io.IsTradeOpen || io.IsRemoteLocked || sender!=null || receiver!=null || inFlight && operationStep==step || state.Arm!=null ||
            state.Recovery.AwaitingVerification!=null || state.Recovery.CancellationPending)
            return Result<IReadOnlyDictionary<(uint Item,bool Hq),long>>.Fail("TradeRecordUnsettled",step.ToString());
        return Result<IReadOnlyDictionary<(uint Item,bool Hq),long>>.Ok(
            Custody.Credited(state.Original,state.Items.Select(i=>new BagStock(i.ItemId,i.IsHighQuality,i.Quantity))));
    });
    // Called only after the run has reconciled both clients' complete fresh deltas into credit.
    public Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,ImmutableArray<Shipment> credit,Deadline d)=>game.Poll<bool>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || io.IsTradeOpen)return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",step.ToString());
        if(!CourierPlanner.Exact(partner,peer.Before.Character) || !CourierPlanner.Exact(partner,peer.After.Character))
            return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",$"{step}: the peer's evidence is not {partner.CharacterName}'s.");
        if(state.Receiving && state.Arm!=null){
            if(game.Bags() is not {} fresh) return null;
            var received=CourierReceiveRecovery.Settle(state,step,partner,fresh,peer,()=>store.Save(state));
            if(received.Outcome!=CourierOutcome.Succeeded || state.Original.Count==0)return received;
        }
        if(state.Recovery.AwaitingVerification!=null){
            using var runner=new TradeRunner(new Intent(store,state),Timing,io,this,log);
            if(runner.IsActive)runner.Tick(); // Observe persisted closure on a now-closed window.
            if(runner.IsActive || runner.ObserveSettlement() is not {} decision)
                return new(CourierOutcome.Indeterminate,"TradeSettlementUnproven",step.ToString());
            runner.AcknowledgeSettlement(decision);
        }
        if(state.Recovery.CancellationPending)return new(CourierOutcome.Indeterminate,"TradeManifestUnsettled",$"{step}: a cancellation is still pending.");
        if(state.Original.Count==0)
            return state.Items.All(i=>i.Quantity==0) ? Result<bool>.Ok(true) : new(CourierOutcome.Indeterminate,"TradeManifestUnsettled",step.ToString());
        // The record must have credited exactly what both sides' bags were reconciled to, including partial and zero
        // deliveries. Keys outside the manifest were reconciled by the run (only mirrored moves refuse there).
        var record=Custody.Credited(state.Original,state.Items.Select(i=>new BagStock(i.ItemId,i.IsHighQuality,i.Quantity)));
        var differs=record.Keys.Concat(credit.Select(s=>(s.Item,s.Hq))).Distinct().Select(k=>(Key:k,Record:record.GetValueOrDefault(k),
            Credit:credit.Where(s=>s.Item==k.Item && s.Hq==k.Hq).Sum(s=>(long)s.Units))).Where(x=>x.Record!=x.Credit).ToArray();
        return differs.Length==0 ? Result<bool>.Ok(true) : new(CourierOutcome.Indeterminate,"TradeManifestUnsettled",
            $"{step}: trade record and reconciled bags differ: "+string.Join("; ",differs.Select(x=>
                $"{(x.Key==Custody.GilKey?"Gil":$"item {x.Key.Item}{(x.Key.Hq?" HQ":"")}")} record {x.Record:#,0}, bags {x.Credit:#,0}")));
    });
    // A cleared run's checkpoint goes with it, so no later trade waits on its pending trade or receive arm. Its steps'
    // trades end on their next tick without a write; on the next framework tick this side's trade (when running one
    // of those steps) is stopped and dropped and the checkpoint, when it is one of theirs, is emptied. The auto-confirm
    // suppression is lifted only once no trade window is open or locked, so YesAlready or Dropbox never confirms a
    // window with this run's items in it; that wait has no deadline, and an unload during it keeps the suppression.
    public void Forget(IReadOnlySet<Guid> steps)
    {
        if(steps.Count==0)return;
        foreach(var s in steps)forgotten.TryAdd(s,0);
        bool? ours=null;
        _=Task.Run(async()=>{
            try{
                var done=await game.Poll<bool>(new("CourierTradeForget",DateTimeOffset.MaxValue),()=>{
                    if(ours==null){
                        var running=inFlight && steps.Contains(operationStep);
                        if(running)DropTrade();
                        var checkpoint=steps.Contains(store.Load().Step);
                        if(checkpoint)store.Save(new());
                        ours=running || checkpoint;
                        if(ours!=true)return Result<bool>.Ok(false);
                        retainSuppression=true;
                    }
                    if(!WindowClosed())return null;
                    retainSuppression=false;
                    if(!inFlight)RestoreTradeAutoConfirm();
                    return Result<bool>.Ok(true);
                },true);
                if(done.Outcome!=CourierOutcome.Succeeded)log.Warning($"CourierTradeForget {done.Code}: {done.Detail}");
            }
            catch(OperationCanceledException){log.Warning("CourierTradeForget: unloaded"+(retainSuppression?" with a trade window open; the auto-confirm suppression is kept.":"."));}
            catch(Exception e){log.Warning($"CourierTradeForget {e.GetType().Name}: {e.Message}");}
        });
    }
    // Unloading keeps the auto-confirm suppression while a trade window is open, locked or unreadable, whatever holds
    // it; the next load lifts it once the window is closed.
    public void Dispose(){try{hold?.Dispose();sender?.Dispose();if(!locked && !io.IsRemoteLocked)receiver?.Stop();}finally{if(WindowClosed())RestoreTradeAutoConfirm();}}
}
