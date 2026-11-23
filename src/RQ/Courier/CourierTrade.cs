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
    /// <summary>Set when the receive was closed by attestation rather than its own trade record (DeliveredThenPlaced, OperatorAttested).</summary>
    public string? Disposition {get;set;}
    public List<BagStock> Original {get;set;}=[];
    public bool Receiving {get;set;}
    public List<TradeItem> Items {get;set;}=[];
    public TradeRecoveryState Recovery {get;set;}=new();
}
internal sealed class CourierTrade(CourierGame game,ITradeIo io,AutomationLease lease,IDalamudPluginInterface pi,
    DalamudExternalUiAutomationSuppression external,IPluginLog log,string directory) : ICourierTradePort, ICourierDispatchEvidence, ICourierSettlement, ICourierHoldPort,
    ICourierReceiveStall, ICourierReceiveAttestation, ITradeSuppression, IDisposable
{
    private sealed class Intent(CourierStore<TradeCheckpoint> store, TradeCheckpoint current) : ITradeIntentStore
    {
        public IList<TradeItem> Remaining => current.Items;
        public TradeRecoveryState Recovery => current.Recovery;
        public void Save()=>store.Save(current);
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
    private HashSet<string>? dropbox;
    private bool stopped,locked;
    private Guid stalledStep;
    private CourierTradeHold? hold;
    public Task<Result<IDisposable>> BeginHold(Guid run, Deadline deadline) => game.Poll<IDisposable>(deadline, () => {
        var next = new CourierTradeHold(pi, $"Quartermaster.Courier/{run:N}/{Guid.NewGuid():N}", s=>log.Warning(s));
        var result=next.Begin();
        if(result.Outcome!=CourierOutcome.Succeeded)return result;
        hold=next;
        // Releasing the scope also forgets it, so a later trade refuses TradeHoldLost instead of checking a dead hold.
        return Result<IDisposable>.Ok(new HoldScope(()=>{try{next.Dispose();}finally{Interlocked.CompareExchange(ref hold,null,next);}}));
    });
    private sealed class HoldScope(Action release) : IDisposable
    {
        private Action? pending = release;
        public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
    }
    public void CheckHold() => hold?.Check(false);
    public void SuppressTradeAutoConfirm()
    {

        if(pi.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName=="YesAlready") &&
            !pi.TryGetData<HashSet<string>>("YesAlready.StopRequests",out _))
            throw new InvalidOperationException("YesAlreadySuppressionUnavailable");
        prompts??=external.Acquire();
        if(pi.TryGetData<HashSet<string>>("Dropbox.StopRequests",out var set) && set!=null)
        {lock(set){set.Add("Quartermaster.Courier");dropbox=set;}}
        else if(pi.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName.Equals("Dropbox",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("DropboxSuppressionUnavailable");
    }
    public void RestoreTradeAutoConfirm()
    {
        if(dropbox is {} set){lock(set)set.Remove("Quartermaster.Courier");dropbox=null;}
        prompts?.Dispose();prompts=null;
    }
    public void RetainTradeSuppressionOnDispose() { /* Preserve stop request ownership until closure. */ }
    public Task<Result<bool>> CancelPreLockAsync(Deadline d)=>game.Poll<bool>(d,()=>{
        stopped=true;
        if(!locked && !io.IsRemoteLocked){sender?.Stop();receiver?.Stop();}
        return Result<bool>.Ok(true);
    });
    // A step credited nothing when no receive window is armed, no confirmation marker was ever persisted (the
    // runner saves it before submitting confirmation) and every queued unit is still queued. Whether the step
    // ended Cancelled, Failed or otherwise, a window that closed in that state moved nothing.
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
        return game.Poll<bool>(d,()=>{
            Result<bool>? StartSender()
            {
                sender=new(new Intent(store,state!),Timing,io,this,log);
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
                sender?.Dispose();sender=null;receiver=null;locked=false;
                if(state?.Recovery.CancellationPending!=true)RestoreTradeAutoConfirm();
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
                    store.Save(state);stopped=locked=false;stalledStep=Guid.Empty;
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
                    if(result is {Status:OperationStatus.Failed} bounced && phase is TradeExecutionState.OfferingItems or TradeExecutionState.WaitingForPartner &&
                        !locked && !io.IsRemoteLocked && !io.IsTradeOpen && NothingCredited(state))
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
    public Task<Result<bool>> AttestReceived(Guid step,OwnerScope partner,TradeEvidence peer,string disposition,Deadline d)=>game.Poll<bool>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || !state.Receiving || io.IsTradeOpen || receiver!=null || sender!=null || state.Recovery.CancellationPending)
            return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",step.ToString());
        if(!CourierPlanner.Exact(partner,peer.Before.Character) || !CourierPlanner.Exact(partner,peer.After.Character) ||
            state.Original.Concat(peer.Before.Stock).Concat(peer.After.Stock).Select(x=>(x.ItemId,x.Hq)).Append((ItemId: Custody.GilKey.Item, Hq: Custody.GilKey.Hq)).Distinct().Any(k=>
                state.Original.Where(x=>x.ItemId==k.ItemId && x.Hq==k.Hq).Sum(x=>(long)x.Units)!=(long)peer.Before.Count(k.ItemId,k.Hq)-peer.After.Count(k.ItemId,k.Hq)))
            return new(CourierOutcome.Indeterminate,"AttestedSenderDeltaMismatch",step.ToString());
        if(state.Arm!=null)state.LastSettledArm=state.Arm;
        state.Arm=null;state.SenderEvidence=peer;state.Disposition=disposition;
        foreach(var item in state.Items)item.Quantity=0;
        store.Save(state);
        log.Warning($"CourierReceiveAttested: step {step} closed as {disposition}; the sender's evidence shows the full manifest left its bags.");
        return Result<bool>.Ok(true);
    });
    // Called only after the run owner has checked both clients' complete fresh deltas.
    public Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,Deadline d)=>game.Poll<bool>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || io.IsTradeOpen)return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",step.ToString());
        if(state.Receiving && state.Arm!=null){
            if(game.Bags() is not {} fresh) return null;
            return CourierReceiveRecovery.Settle(state,step,partner,fresh,peer,()=>store.Save(state));
        }
        if(state.Recovery.AwaitingVerification!=null){
            using var runner=new TradeRunner(new Intent(store,state),Timing,io,this,log);
            if(runner.IsActive)runner.Tick(); // Observe persisted closure on a now-closed window.
            if(runner.IsActive || runner.ObserveSettlement() is not {} decision)
                return new(CourierOutcome.Indeterminate,"TradeSettlementUnproven",step.ToString());
            runner.AcknowledgeSettlement(decision);
        }
        // For new checkpoints the peer's exact settled delta must equal the quantity
        // already debited/credited here, including partial and zero deliveries.
        bool accounted = state.Original.Count==0 ? state.Items.All(i=>i.Quantity==0) :
            CourierPlanner.Exact(partner,peer.Before.Character) && CourierPlanner.Exact(partner,peer.After.Character) &&
            state.Original.Concat(peer.Before.Stock).Concat(peer.After.Stock).Select(x=>(x.ItemId,x.Hq)).Append((ItemId: Custody.GilKey.Item, Hq: Custody.GilKey.Hq)).Distinct().All(k=>
                state.Original.Where(x=>x.ItemId==k.ItemId && x.Hq==k.Hq).Sum(x=>(long)x.Units)-
                state.Items.Where(x=>x.ItemId==k.ItemId && x.IsHighQuality==k.Hq).Sum(x=>(long)x.Quantity)==
                (state.Receiving ? -1 : 1)*((long)peer.After.Count(k.ItemId,k.Hq)-peer.Before.Count(k.ItemId,k.Hq)));
        return !state.Recovery.CancellationPending && accounted ? Result<bool>.Ok(true) :
            new(CourierOutcome.Indeterminate,"TradeManifestUnsettled",step.ToString());
    });
    public void Dispose(){try{hold?.Dispose();sender?.Dispose();if(!locked && !io.IsRemoteLocked)receiver?.Stop();}finally{RestoreTradeAutoConfirm();}}
}
