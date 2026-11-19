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
    public bool Receiving {get;set;}
    public List<TradeItem> Items {get;set;}=[];
    public TradeRecoveryState Recovery {get;set;}=new();
}
internal sealed class CourierTrade(CourierGame game,ITradeIo io,AutomationLease lease,IDalamudPluginInterface pi,
    DalamudExternalUiAutomationSuppression external,IPluginLog log,string directory) : ICourierTradePort, ICourierSettlement, ITradeSuppression, IDisposable
{
    private sealed class Intent(CourierStore<TradeCheckpoint> store, TradeCheckpoint current) : ITradeIntentStore
    {
        public IList<TradeItem> Remaining => current.Items;
        public TradeRecoveryState Recovery => current.Recovery;
        public void Save()=>store.Save(current);
    }
    private readonly CourierStore<TradeCheckpoint> store=new(Path.Combine(directory,"courier-trade.json"));
    private TradeRunner? sender;
    private TradeReceiveOperation? receiver;
    private IDisposable? prompts;
    private HashSet<string>? dropbox;
    private bool stopped,locked;
    internal string? IncompatibleAcceptor() => pi.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName=="MarketMafioso")
        ? "MarketMafioso exposes no trade suppression acknowledgement." : null;
    public void SuppressTradeAutoConfirm()
    {
        // This acceptor does not honor a shared trade ownership request. Do not arm
        // either side on a client where an independent confirmer can race our manifest.
        if(IncompatibleAcceptor() is {} reason)
            throw new InvalidOperationException("UncoordinatedTradeAcceptor: "+reason);
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
    public Task<Result<bool>> SendAsync(OwnerScope p,ImmutableArray<Shipment> m,Deadline d)=>Execute(p,m,d,false);
    public Task<Result<bool>> ReceiveAsync(OwnerScope p,ImmutableArray<Shipment> m,Deadline d)=>Execute(p,m,d,true);
    private Task<Result<bool>> Execute(OwnerScope p,ImmutableArray<Shipment> manifest,Deadline d,bool receive)
    {
        var step=CourierGame.Step.Value; TradePartner? partner=null;TradeCheckpoint? state=null;
        var allowed=CourierGame.CanContinue.Value;
        return game.Poll<bool>(d,()=>{
            Result<bool> End(CourierOutcome outcome,string code,string detail)
            {
                if(outcome==CourierOutcome.Indeterminate && sender is {} sent)
                    game.WatchLate(d.Name,()=>sent.ObserveLateEvidence()!=null);
                if(outcome==CourierOutcome.Indeterminate && receiver is {} received && code=="ReceiveDeadline")
                    game.WatchLate(d.Name,()=>{received.Tick(DateTimeOffset.UtcNow);return received.Evidence!=null;});
                sender?.Dispose();sender=null;receiver=null;locked=false;
                if(state?.Recovery.CancellationPending!=true)RestoreTradeAutoConfirm();
                return new(outcome,code,detail,outcome==CourierOutcome.Succeeded);
            }
            try{
                if(allowed?.Invoke()==false){
                    if(state==null)return new(CourierOutcome.Cancelled,"TradeDisarmed",d.Name);
                    stopped=true;
                    if(!locked && !io.IsRemoteLocked){sender?.Stop();receiver?.Stop();}
                }
                if(state==null){
                    if(lease.Holder is not ("courier owner" or "courier carrier") || step==Guid.Empty)
                        return Result<bool>.Fail("TradeAuthorityMissing",d.Name);
                    if(DateTimeOffset.UtcNow>=d.Expires)return Result<bool>.Fail("TradeStartExpired",d.Name);
                    if(manifest.IsDefaultOrEmpty || manifest.Any(s=>s.Item<=TradePlanner.GilItemId || s.Units<=0))
                        return Result<bool>.Fail("InvalidItemManifest",d.Name);
                    var prior=store.Load();
                    if(prior.Recovery.AwaitingVerification!=null || prior.Recovery.CancellationPending || prior.Step==step)
                        return new(CourierOutcome.Indeterminate,"SettlementRequired",prior.Step.ToString());
                    partner=game.Partner(p);
                    if(partner==null)return Result<bool>.Fail("PartnerNotTradeable",p.CharacterName);
                    state=new(){Step=step,Receiving=receive,Items=[..manifest.GroupBy(s=>(s.Item,s.Hq)).Select(g=>new TradeItem{
                        ItemId=g.Key.Item,IsHighQuality=g.Key.Hq,Quantity=g.Sum(s=>s.Units),ItemName=g.First().Name})]};
                    store.Save(state);stopped=locked=false;
                    SuppressTradeAutoConfirm();
                    if(!receive){
                        sender=new(new Intent(store,state),new(),io,this,log);
                        sender.BatchLocking+=(_,_)=>locked=true;
                        sender.BatchVerified+=(_,_)=>locked=false;
                        var start=sender.Start(partner);
                        if(!start.Success)return End(CourierOutcome.Failed,start.Code??"SenderRejected",start.Message);
                    }
                }
                OperationResult? result;
                if(receive){
                    receiver??=new(io,partner!,state.Items.Where(i=>i.Quantity>0).Select(i=>new TradeOfferedItem(i.ItemId,i.IsHighQuality,i.Quantity)).ToArray(),d.Expires,s=>log.Warning(s));
                    result=receiver.Tick(DateTimeOffset.UtcNow);
                    if(result.Status==OperationStatus.Succeeded){
                        foreach(var (k,n) in receiver.Received!)state.Items.Single(i=>i.ItemId==k.ItemId && i.IsHighQuality==k.IsHighQuality).Quantity-=n;
                        store.Save(state);receiver=null;
                        if(state.Items.Any(i=>i.Quantity>0) && !stopped)return null;
                        if(state.Items.Any(i=>i.Quantity>0))return End(CourierOutcome.Indeterminate,"PartialReceiveStopped",d.Name);
                    }else if(result.Code=="ClosedBeforeConfirm" && !stopped && DateTimeOffset.UtcNow<d.Expires){receiver=null;return null;}
                }else{
                    if(DateTimeOffset.UtcNow>=d.Expires && !stopped){stopped=true;sender!.Stop("Sender deadline "+d.Expires.ToString("O"));}
                    sender!.Tick(); result=sender.Result;
                    if(sender.IsActive)return null;
                }
                if(result?.IsTerminal!=true)return null;
                return End(result.Status switch { OperationStatus.Succeeded=>CourierOutcome.Succeeded,OperationStatus.Cancelled=>CourierOutcome.Cancelled,
                    OperationStatus.Indeterminate=>CourierOutcome.Indeterminate,_=>CourierOutcome.Failed },result.Code,result.Message);
            }catch(Exception e){return End(CourierOutcome.Indeterminate,"TradeAdapterFault",e.Message);}
        },true);
    }
    // Called only after the run owner has checked both clients' complete fresh deltas.
    public Task<Result<bool>> Settle(Guid step,Deadline d)=>game.Poll<bool>(d,()=>{
        var state=store.Load();
        if(state.Step!=step || io.IsTradeOpen)return new(CourierOutcome.Indeterminate,"TradeCheckpointMismatch",step.ToString());
        if(state.Recovery.AwaitingVerification!=null){
            using var runner=new TradeRunner(new Intent(store,state),new(),io,this,log);
            if(runner.IsActive)runner.Tick(); // Observe persisted closure on a now-closed window.
            if(runner.IsActive || runner.ObserveSettlement()!=TradeSettlement.Delivered)
                return new(CourierOutcome.Indeterminate,"TradeSettlementUnproven",step.ToString());
            runner.AcknowledgeSettlement(TradeSettlement.Delivered);
        }
        return !state.Recovery.CancellationPending && state.Items.All(i=>i.Quantity==0) ? Result<bool>.Ok(true) :
            new(CourierOutcome.Indeterminate,"TradeManifestUnsettled",step.ToString());
    });
    public void Dispose(){sender?.Dispose();receiver?.Stop();if(store.Load().Recovery.CancellationPending==false)RestoreTradeAutoConfirm();}
}
