using System.Collections.Concurrent;
using System.Text.Json;
using Franthropy.PeerLink;
using RQ.Domain;
using RQ.UI;

namespace RQ.Courier;

internal sealed class CourierPeerLink : ICourierPeerPort, ICourierLink, IDisposable
{
    private sealed record Wire(string Kind, Guid Run, Guid Step, CourierMessage? Command = null, Deadline? Limit = null);
    private sealed record Reply(Result<CourierReceipt>? Result,CourierProgress? Progress);
    private sealed record Job(CourierMessage Message, string Fingerprint, Task<Result<CourierReceipt>> Task, DateTimeOffset Expires);
    private readonly Task<PeerEndpoint> endpoint;
    private readonly Func<IReadOnlyList<OwnerScope>> roster;
    private readonly CourierGame game;
    private readonly CourierFollower follower;
    private readonly Action<string> warn;
    private readonly CancellationTokenSource unload = new();
    private readonly ConcurrentDictionary<(Guid,Guid),Job> jobs=new();
    private readonly ConcurrentDictionary<(Guid,Guid),CourierProgress> acknowledgements=new();
    private readonly ConcurrentDictionary<Guid,Guid> owners=new(), carriers=new();
    private readonly ConcurrentDictionary<Guid,PeerConnection> connections=new();
    private volatile PeerPresence[] presences=[];
    private volatile CourierPeer[] peers=[];
    private DateTimeOffset next;
    private Task refresh=Task.CompletedTask;
    private string published="", projected="";
    public MeetingBell? StandingAt {get;private set;}
    public string? BellBlock {get;private set;}
    public IReadOnlyList<CourierPeer> Peers => peers;
    public event Action<CourierProgress>? Progress;
    public event Action? LinkLost;
    public event Action<Guid>? StopRequested;
    internal CourierPeerLink(CourierGame game,CourierFollower follower,Func<Action,CancellationToken,Task> dispatch,Action<string> warn,Func<IReadOnlyList<OwnerScope>> roster)
    {
        this.roster=roster;this.game=game; this.follower=follower; this.warn=warn;
        endpoint=PeerEndpoint.StartAsync(new("Quartermaster.Courier.v2",dispatch,Handle,warn),JsonSerializer.SerializeToElement<CourierPeer?>(null));
        follower.StopRequested+=NotifyStop;
    }
    internal void Tick(bool identityChanged)
    {
        var now=DateTimeOffset.UtcNow;
        if(identityChanged)next=DateTimeOffset.MinValue;
        if(now<next || !refresh.IsCompleted) return;
        next=now.AddSeconds(2);
        InventoryView? bags=null;
        try{ StandingAt=game.StandingAt();BellBlock=game.BellBlock();bags=game.Bags(); }
        catch(Exception e){ StandingAt=null;BellBlock=null;warn("CourierPresenceObservation: "+e.Message); }
        var identity=JsonSerializer.SerializeToElement(bags==null?null:new CourierPeer(bags.Character,bags,roster()));
        refresh=Refresh(identity);
        foreach(var p in jobs.Where(e=>e.Value.Task.IsCompleted && e.Value.Expires<now)){ jobs.TryRemove(p.Key,out _); acknowledgements.TryRemove(p.Key,out _); }
    }
    private async Task Refresh(JsonElement identity)
    {
        try{
            var ep=await endpoint.ConfigureAwait(false);
            if(published!=identity.GetRawText()){await ep.UpdateIdentityAsync(identity);published=identity.GetRawText();}
            var list=(await ep.DiscoverAsync()).OrderBy(p=>p.InstanceId).ToArray();
            var view=list.Select(p=>p.Identity.Deserialize<CourierPeer>()).OfType<CourierPeer>().ToArray();
            presences=list; var json=JsonSerializer.Serialize(view);
            if(json!=projected){ peers=view; projected=json; }
        }catch(Exception e){presences=[];peers=[];projected=""; if(!unload.IsCancellationRequested)warn("PeerDiscovery: "+e.Message);}
    }
    private PeerResult Delivered(Result<CourierReceipt>? result,CourierProgress? ack=null)=>new PeerResult.Delivered(JsonSerializer.SerializeToElement(new Reply(result,ack??follower.Progress)));
    private PeerResult Handle(PeerMessage envelope)
    {
        var w=envelope.Payload.Deserialize<Wire>();
        if(unload.IsCancellationRequested || w==null) return new PeerResult.Refused("Unavailable","Courier endpoint stopped.");
        var sender=envelope.SenderInstanceId;
        if(w.Kind=="stop" && carriers.TryGetValue(w.Run,out var carrier) && carrier==sender)
        { StopRequested?.Invoke(w.Run);return Delivered(null); }
        var key=(sender,w.Step);
        if(w.Kind=="query")
            return jobs.TryGetValue(key,out var j) && j.Message.Run==w.Run ? Delivered(j.Task.IsCompletedSuccessfully?j.Task.Result:
j.Task.IsFaulted?Result<CourierReceipt>.Fail("CarrierStepFault",j.Task.Exception!.GetBaseException().Message):null,acknowledgements.GetValueOrDefault(key))
                :new PeerResult.Refused("UnknownStep","No active step handle.");
        if(w.Kind!="start" || w.Command is not {} m || w.Limit is not {} d || m.Step!=w.Step || m.Run!=w.Run ||
            d.Expires<=DateTimeOffset.UtcNow || d.Expires>DateTimeOffset.UtcNow.AddSeconds(Deadline.PeerSeconds(m.Bell, m.Command)))
            return new PeerResult.Refused("InvalidEnvelope","Invalid or expired courier step.");
        var p=presences.FirstOrDefault(p=>p.InstanceId==sender);
        if(p==null) return new PeerResult.Refused("UnknownOwnerEndpoint","No matching peer presence.");
        var who=p.Identity.Deserialize<CourierPeer>()?.Character ?? new OwnerScope();
        var hash=JsonSerializer.Serialize(m);
        // A resumed step reuses its id. A finished attempt that did not succeed must not answer for the new one;
        // the follower's ledger decides whether the step may run again.
        if(jobs.TryGetValue(key,out var done) && done.Fingerprint==hash && done.Task.IsCompleted &&
            !(done.Task.IsCompletedSuccessfully && done.Task.Result.Outcome==CourierOutcome.Succeeded) &&
            jobs.TryRemove(new KeyValuePair<(Guid,Guid),Job>(key,done)))
            acknowledgements.TryRemove(key,out _);
        var job=jobs.GetOrAdd(key,unused=>new(m,hash,Task.Run(async ()=>{
            var result=await follower.HandleAsync(who,m,d,sender,p=>acknowledgements[key]=p);
            if(m.Command==CourierCommand.Begin && result.Outcome==CourierOutcome.Succeeded){owners[m.Run]=sender;Connect(await endpoint,p);}
            if(m.Command is CourierCommand.Finish or CourierCommand.Clear && result.Outcome==CourierOutcome.Succeeded)owners.TryRemove(m.Run,out _);
            return result;
        }),d.Expires.AddSeconds(30)));
        return job.Fingerprint==hash ? Delivered(null):new PeerResult.Refused("StepCollision","Step payload differs.");
    }
    private async void NotifyStop(Guid run)
    {
        try{if(!owners.TryGetValue(run,out var id))return;
            var p=presences.Single(p=>p.InstanceId==id);
            var result=await (await endpoint).Connect(p).EventAsync(JsonSerializer.SerializeToElement(new Wire("stop",run,Guid.Empty)),"StopNotice",TimeSpan.FromSeconds(5),unload.Token);
            if(result is not PeerResult.Delivered)warn("StopNotice: "+result);
        }catch(Exception e){warn("StopNotice: "+e.Message);}
    }
    private PeerConnection Connect(PeerEndpoint ep,PeerPresence p)=>connections.GetOrAdd(p.InstanceId,id=>{
        var connection=ep.Connect(p);
        connection.StateChanged+=state=>{
            if(state!=PeerState.Lost || unload.IsCancellationRequested)return;
            if(carriers.Values.Contains(id))LinkLost?.Invoke();
            if(owners.Values.Contains(id))_=PauseFollower();
        };
        return connection;
    });
    private async Task PauseFollower(){try{await follower.StopLocalAsync();}catch(Exception e){warn("PeerLostStop: "+e.Message);}}
    public async Task<Result<CourierReceipt>> SendAsync(CourierMessage m,Deadline d)
    {
        var targets=presences.Where(p=>CourierPlanner.Exact(p.Identity.Deserialize<CourierPeer>()?.Character,m.Carrier)).ToArray();
        if(targets.Length!=1)return Result<CourierReceipt>.Fail("CarrierEndpointAmbiguous",$"Matched {targets.Length} carriers.");
        var ep=await endpoint;var conn=Connect(ep,targets[0]);carriers[m.Run]=targets[0].InstanceId;
        var start=true;
        while(DateTimeOffset.UtcNow<d.Expires){
            var w=start?new Wire("start",m.Run,m.Step,m,d):new Wire("query",m.Run,m.Step);
            var json=JsonSerializer.SerializeToElement(w);
            var response=start?await conn.RequestAsync(Guid.NewGuid(),json,"StepStart",TimeSpan.FromSeconds(5),unload.Token):
                await conn.EventAsync(json,"StepQuery",TimeSpan.FromSeconds(5),unload.Token);
            if(response is not PeerResult.Delivered ok){
                if(response is PeerResult.LinkLost) LinkLost?.Invoke();
                return response switch {
                    PeerResult.Refused refusal=>Result<CourierReceipt>.Fail(refusal.Code,refusal.Reason),
                    PeerResult.TimedOut timeout=>new(CourierOutcome.Indeterminate,timeout.Threshold+"Deadline",timeout.Limit.ToString()),
                    PeerResult.Cancelled=>new(CourierOutcome.Indeterminate,"PeerExchangeCancelled",d.Name),
                    _=>new(CourierOutcome.Indeterminate,"PeerLinkLost",response.ToString()!)
                };
            }
            var reply=ok.Response.Deserialize<Reply>();
            if(reply?.Progress is {} progress && progress.Run==m.Run && progress.Step==m.Step)Progress?.Invoke(progress);
            if(reply?.Result is {} result){
                if(m.Command is CourierCommand.Finish or CourierCommand.Clear && result.Outcome==CourierOutcome.Succeeded)
                    carriers.TryRemove(m.Run,out _);
                return result;
            }
            start=false;await Task.Delay(200,unload.Token);
        }
        return new(CourierOutcome.Indeterminate,"PeerStepDeadline",d.Name);
    }
    public void Dispose(){follower.StopRequested-=NotifyStop;unload.Cancel();_=Close();}
    private async Task Close(){try{await (await endpoint.ConfigureAwait(false)).DisposeAsync();}catch(Exception e){warn("PeerUnload: "+e.Message);}}
}
