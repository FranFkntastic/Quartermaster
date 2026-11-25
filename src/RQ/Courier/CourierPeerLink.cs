using System.Collections.Concurrent;
using System.Text.Json;
using Franthropy.PeerLink;
using RQ.Domain;
using RQ.UI;

namespace RQ.Courier;

internal sealed class CourierPeerLink : ICourierPeerPort, ICourierLink, IDisposable
{
    private sealed record Wire(string Kind, Guid Run, Guid Step, CourierMessage? Command = null, Deadline? Limit = null, OwnerScope? Owner = null);
    private sealed record Reply(Result<CourierReceipt>? Result,CourierProgress? Progress);
    private sealed record Job(CourierMessage Message, string Fingerprint, Task<Result<CourierReceipt>> Task, DateTimeOffset Expires);
    private readonly Task<PeerEndpoint> endpoint;
    private readonly Func<CourierSettings> settings;
    private readonly Func<Func<CourierSettings, CourierSettings>, bool> updateSettings;
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
    // The character last seen logged in. A carrier mid-run logs out for data-center travel; its presence keeps
    // naming it (with no bags) so the owner still finds exactly one endpoint for it when it logs back in.
    private OwnerScope? lastCharacter;
    public MeetingBell? StandingAt {get;private set;}
    public string? BellBlock {get;private set;}
    public bool InWard=>game.InWard;
    public int RoutePoints=>game.PointCount;
    public string? RouteError=>game.RouteError;
    public void StartRoute()=>game.StartRoute();
    public void CancelRoute()=>game.CancelRoute();
    public MeetingBell? CaptureHere(ulong character)=>game.CaptureHere(character);
    public IReadOnlyList<CourierPeer> Peers => peers;
    /// <summary>The run this client's owner journal holds (<see cref="Guid.Empty"/> for none), advertised so a carrier forgets a run cleared here.</summary>
    internal Func<Guid?>? OwnerRun { get; set; }
    /// <summary>The logged-in character's bags as last observed by the presence tick; null when logged out.</summary>
    public InventoryView? LocalBags {get;private set;}
    public event Action<CourierProgress>? Progress;
    public event Action? LinkLost;
    public event Action<Guid>? StopRequested;
    internal CourierPeerLink(CourierGame game,CourierFollower follower,Func<Action,CancellationToken,Task> dispatch,Action<string> warn,Func<CourierSettings> settings,
        Func<Func<CourierSettings, CourierSettings>, bool> updateSettings)
    {
        this.settings=settings;this.updateSettings=updateSettings;this.game=game; this.follower=follower; this.warn=warn;
        endpoint=PeerEndpoint.StartAsync(new("Quartermaster.Courier.v2",dispatch,Handle,warn),JsonSerializer.SerializeToElement<CourierPeer?>(null));
        follower.StopRequested+=NotifyStop;
    }
    internal void Tick(bool identityChanged)
    {
        game.SampleRoute();
        var now=DateTimeOffset.UtcNow;
        if(identityChanged)next=DateTimeOffset.MinValue;
        if(now<next || !refresh.IsCompleted) return;
        next=now.AddSeconds(2);
        InventoryView? bags=null;
        try{ StandingAt=game.StandingAt();BellBlock=game.BellBlock();bags=game.Bags(); }
        catch(Exception e){ StandingAt=null;BellBlock=null;warn("CourierPresenceObservation: "+e.Message); }
        LocalBags=bags;
        if(bags!=null)lastCharacter=bags.Character;
        else if(lastCharacter!=null && !CarryingRun())lastCharacter=null;
        var who=bags?.Character ?? lastCharacter;
        var identity=JsonSerializer.SerializeToElement(who!=null ? Identity(who,bags) : null);
        refresh=Refresh(identity,who);
        foreach(var p in jobs.Where(e=>e.Value.Task.IsCompleted && e.Value.Expires<now)){ jobs.TryRemove(p.Key,out _); acknowledgements.TryRemove(p.Key,out _); }
    }
    // Pairing rides on every identity (including the bag-less one published mid data-center travel) so owners
    // see whether this character carries for them without the status flickering while it travels.
    private CourierPeer Identity(OwnerScope who,InventoryView? bags)
    {
        var cfg=settings();
        var carrying=cfg.CarrierOptIn && cfg.Carrier is {} self && CourierPlanner.Exact(self,who);
        Guid? run=null;
        try{run=OwnerRun?.Invoke();}catch(Exception e){warn("CourierPresenceRun: "+e.Message);}
        return new CourierPeer(who,bags,cfg.LocalRoster,carrying,carrying?[..cfg.Owners]:Array.Empty<OwnerScope>(),carrying?true:null,run);
    }
    private bool CarryingRun()
    {
        try { return follower.View().Run != null; }
        catch (Exception e) { warn("CourierPresenceLedger: " + e.Message); return false; }
    }
    private async Task Refresh(JsonElement identity,OwnerScope? self)
    {
        CourierPeer[] view; (Guid,CourierPeer)[] seen;
        try{
            var ep=await endpoint.ConfigureAwait(false);
            if(published!=identity.GetRawText()){await ep.UpdateIdentityAsync(identity);published=identity.GetRawText();}
            var list=(await ep.DiscoverAsync()).OrderBy(p=>p.InstanceId).ToArray();
            seen=list.Select(p=>(p.InstanceId,Peer:p.Identity.Deserialize<CourierPeer>())).Where(x=>x.Peer!=null).Select(x=>(x.InstanceId,x.Peer!)).ToArray();
            view=seen.Select(x=>x.Item2).ToArray();
            presences=list; var json=JsonSerializer.Serialize(view);
            if(json!=projected){ peers=view; projected=json; }
        }catch(Exception e){presences=[];peers=[];projected=""; if(!unload.IsCancellationRequested)warn("PeerDiscovery: "+e.Message);return;}
        // A run its owner client cleared while this carrier was unreachable is forgotten once that client is linked again.
        follower.ForgetOrphaned(seen.Where(x=>!CourierPlanner.Exact(x.Item2.Character,self)));
    }
    private PeerResult Delivered(Result<CourierReceipt>? result,CourierProgress? ack=null)=>new PeerResult.Delivered(JsonSerializer.SerializeToElement(new Reply(result,ack??follower.Progress)));
    private PeerResult Handle(PeerMessage envelope)
    {
        var w=envelope.Payload.Deserialize<Wire>();
        if(unload.IsCancellationRequested || w==null) return new PeerResult.Refused("Unavailable","Courier endpoint stopped.");
        var sender=envelope.SenderInstanceId;
        if(w.Kind=="stop" && carriers.TryGetValue(w.Run,out var carrier) && carrier==sender)
        { StopRequested?.Invoke(w.Run);return Delivered(null); }
        if(w.Kind=="pair") return Pair(sender,w.Owner);
        var key=(sender,w.Step);
        if(w.Kind=="query")
            return jobs.TryGetValue(key,out var j) && j.Message.Run==w.Run ? Delivered(j.Task.IsCompletedSuccessfully?j.Task.Result:
j.Task.IsFaulted?Result<CourierReceipt>.Fail("CarrierStepFault",j.Task.Exception!.GetBaseException().Message):null,acknowledgements.GetValueOrDefault(key))
                :new PeerResult.Refused("UnknownStep","No active step handle.");
        if(w.Kind!="start" || w.Command is not {} m || w.Limit is not {} d || m.Step!=w.Step || m.Run!=w.Run ||
            d.Expires<=DateTimeOffset.UtcNow || d.Expires>DateTimeOffset.UtcNow.AddSeconds(Deadline.PeerSeconds(m.Bell, m.Command, m.Manifest)))
            return new PeerResult.Refused("InvalidEnvelope","Invalid or expired courier step.");
        var p=presences.FirstOrDefault(p=>p.InstanceId==sender);
        if(p==null) return new PeerResult.Refused("UnknownOwnerEndpoint","No matching peer presence.");
        var presence=p.Identity.Deserialize<CourierPeer>();
        var who=presence?.Character ?? new OwnerScope();
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
    // An owner client already paired here may pair another character of its own advertised roster. An unpaired
    // client can never authorize itself, and only a carrier that has opted in accepts any pairing.
    private PeerResult Pair(Guid sender,OwnerScope? owner)
    {
        if(owner is not { HasStableIdentity: true } requested)
            return new PeerResult.Refused("InvalidOwner","A complete owner identity is required.");
        var cfg=settings();
        if(!(cfg.CarrierOptIn && cfg.Carrier is {} self && LocalBags?.Character is {} local && CourierPlanner.Exact(self,local)))
            return new PeerResult.Refused("CarrierOptInOff","Carrier opt-in is not active on this character.");
        var p=presences.FirstOrDefault(x=>x.InstanceId==sender);
        CourierPeer? peer=null;
        try{peer=p?.Identity.Deserialize<CourierPeer>();}catch(JsonException){}
        if(peer?.Character is not { HasStableIdentity: true } senderChar || !cfg.IsPaired(senderChar))
            return new PeerResult.Refused("OwnerNotPaired",$"{peer?.Character?.CharacterName ?? "The requesting character"} is not paired on this carrier.");
        if(peer.Roster?.Any(o=>CourierPlanner.Exact(o,requested))!=true)
            return new PeerResult.Refused("OutsideClientRoster",$"{requested.CharacterName} is not in the requesting client's roster.");
        if(!updateSettings(current=>current.IsPaired(requested)?current:current with { PairedOwner=null,PairedOwners=[..current.Owners,requested with { }] }))
            return new PeerResult.Refused("SettingsSaveFailed","Could not save carrier pairing.");
        next=DateTimeOffset.MinValue;
        return Delivered(null);
    }
    public async Task<Result<bool>> PairOwnerAsync(OwnerScope carrier,OwnerScope owner,CancellationToken ct=default)
    {
        var targets=presences.Where(p=>CourierPlanner.Exact(p.Identity.Deserialize<CourierPeer>()?.Character,carrier)).ToArray();
        if(targets.Length!=1)return Result<bool>.Fail("CarrierEndpointAmbiguous",$"Matched {targets.Length} carriers.");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,unload.Token);
        var conn=Connect(await endpoint.ConfigureAwait(false),targets[0]);
        var json=JsonSerializer.SerializeToElement(new Wire("pair",Guid.Empty,Guid.Empty,Owner:owner));
        var response=await conn.RequestAsync(Guid.NewGuid(),json,"PairOwner",TimeSpan.FromSeconds(5),linked.Token).ConfigureAwait(false);
        if(response is PeerResult.Delivered){next=DateTimeOffset.MinValue;return Result<bool>.Ok(true);}
        return response switch {
            PeerResult.Refused refusal=>Result<bool>.Fail(refusal.Code,refusal.Reason),
            PeerResult.TimedOut timeout=>Result<bool>.Fail(timeout.Threshold+"Deadline",timeout.Limit.ToString()),
            PeerResult.Cancelled=>Result<bool>.Fail("PeerExchangeCancelled","PairOwner"),
            _=>Result<bool>.Fail("PeerLinkLost",response.ToString()!)
        };
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
    // A carrier missing from one discovery pass (its presence refreshing, or a discovery error) is looked for this long
    // before the step is refused; nothing has been sent to it yet.
    private static readonly TimeSpan DiscoveryGrace=TimeSpan.FromSeconds(30);
    public async Task<Result<CourierReceipt>> SendAsync(CourierMessage m,Deadline d)
    {
        PeerPresence[] targets;
        var looking=DateTimeOffset.UtcNow+DiscoveryGrace;
        while(true)
        {
            targets=presences.Where(p=>CourierPlanner.Exact(p.Identity.Deserialize<CourierPeer>()?.Character,m.Carrier)).ToArray();
            if(targets.Length==1)break;
            // Two matching carriers are a real ambiguity; none is waited out briefly.
            if(targets.Length>1 || DateTimeOffset.UtcNow>=looking || DateTimeOffset.UtcNow>=d.Expires)
                return Result<CourierReceipt>.Fail("CarrierEndpointAmbiguous",$"Matched {targets.Length} carriers.");
            await Task.Delay(500,unload.Token);
        }
        var ep=await endpoint;var conn=Connect(ep,targets[0]);carriers[m.Run]=targets[0].InstanceId;
        var start=true;string? lastTimeout=null;
        while(DateTimeOffset.UtcNow<d.Expires){
            var w=start?new Wire("start",m.Run,m.Step,m,d):new Wire("query",m.Run,m.Step);
            var json=JsonSerializer.SerializeToElement(w);
            var response=start?await conn.RequestAsync(Guid.NewGuid(),json,"StepStart",TimeSpan.FromSeconds(5),unload.Token):
                await conn.EventAsync(json,"StepQuery",TimeSpan.FromSeconds(5),unload.Token);
            // A step query only reads the carrier's step; one that timed out is asked again until the deadline. A lost
            // link ends the step at once (its heartbeat said so), and a timed-out start is never sent twice.
            if(!start && response is PeerResult.TimedOut slow)
            {
                lastTimeout=$"{slow.Threshold} {slow.Limit}";
                await Task.Delay(200,unload.Token);
                continue;
            }
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
        return new(CourierOutcome.Indeterminate,"PeerStepDeadline",lastTimeout==null?d.Name:$"{d.Name}; last step query timed out ({lastTimeout})");
    }
    public void Dispose(){follower.StopRequested-=NotifyStop;unload.Cancel();_=Close();}
    private async Task Close(){try{await (await endpoint.ConfigureAwait(false)).DisposeAsync();}catch(Exception e){warn("PeerUnload: "+e.Message);}}
}
