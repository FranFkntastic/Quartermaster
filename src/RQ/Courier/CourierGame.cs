using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Automation.Trades;
using Franthropy.Dalamud.Automation.Vendors;
using Dalamud.Game.ClientState.Objects.Enums;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Worlds;
using World = Lumina.Excel.Sheets.World;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;
using RQ.Domain;
using RQ.Inventory;
using RQ.UI;

namespace RQ.Courier;

internal sealed class CourierGame(IDalamudPluginInterface pi, IPlayerState player, IClientState client,
    IObjectTable objects, ICondition conditions, ICommandManager commands, IDataManager data, IAetheryteList attuned,
    DalamudSummoningBellInteractor bells, ITradeIo io, InventoryScanner scanner, Func<OwnerScope> owner,
    Func<Action,CancellationToken,Task> dispatch, string directory, Action<string> warn) :
    IInventoryObservationPort, ICharacterSwitchPort, IWorldVisitPort, IMeetingBellPort, IDisposable
{
    private readonly CancellationTokenSource unload = new();
    private readonly DalamudVNavmeshTravel nav = new(pi);
    private bool ownsPath;
    private readonly DalamudLifestreamAetheryteTravel teleport = new(pi);
    private readonly DalamudLifestreamAethernetTravel aethernet = new(pi);
    private readonly DalamudLifestreamLogin login = new(pi);
    private readonly WorldCatalog worlds = new(data.GetExcelSheet<World>().Where(w => w.IsPublic)
        .Select(w => new WorldInfo(w.Name.ToString(), "", "", w.RowId)));
    internal static readonly AsyncLocal<Action?> TradeStarted=new();
    internal static readonly AsyncLocal<Guid> Step = new();
    internal static readonly AsyncLocal<Func<bool>?> CanContinue = new();
    private readonly CourierStore<Dictionary<string,Guid>> submissions = new(Path.Combine(directory,"courier-travel.json"));
    internal async Task<Result<T>> Poll<T>(Deadline d, Func<Result<T>?> tick, bool primitiveDeadline = false)
    {
        var token = unload.Token;
        while (!token.IsCancellationRequested)
        {
            Result<T>? value = null;
            await dispatch(() =>
            {
                token.ThrowIfCancellationRequested();
                value = !primitiveDeadline && DateTimeOffset.UtcNow >= d.Expires
                    ? new(CourierOutcome.Indeterminate,d.Name+"Deadline",d.Expires.ToString("O")) : tick();
            }, token).WaitAsync(token).ConfigureAwait(false);
            if (value != null) return value;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new OperationCanceledException(token);
    }
    internal InventoryView? Bags()
    {
        if (!client.IsLoggedIn || !player.IsLoaded || !owner().HasStableIdentity) return null;
        var bags = scanner.CapturePlayerStorage();
        if (!CourierSession.PlayerBags.All(b => bags.ObservedSources.Contains(b))) return null;
        var slots = bags.Bags.Where(b => CourierSession.PlayerBags.Contains(b.BagName)).SelectMany(b => b.Items).ToArray();
        return new(owner(), 140-slots.Length, [..slots.Select(s => new BagStock(s.ItemId,s.IsHq,checked((int)s.Quantity)))]);
    }
    public Task<Result<InventoryView>> ObserveAsync(Deadline d) => Poll<InventoryView>(d, () => Bags() is {} b ? Result<InventoryView>.Ok(b) : null);
    private CourierPosition? Position() => objects.LocalPlayer is {} p && player.IsLoaded && client.IsLoggedIn
        ? new(player.CurrentWorld.RowId,client.TerritoryType,p.Position.X,p.Position.Y,p.Position.Z) : null;
    public Task<Result<CourierPosition>> PositionAsync(Deadline d) => Poll<CourierPosition>(d, () => Position() is {} p ? Result<CourierPosition>.Ok(p) : null);
    /// <summary>Why this place cannot hold a meeting bell at all; null where one could be captured.</summary>
    internal string? BellBlock() => Position() is not {} p ? null :
        p.World != player.HomeWorld.RowId ? "Not on home world" :
        data.GetExcelSheet<TerritoryType>().GetRowOrDefault(p.Territory)?.TerritoryIntendedUse.RowId is not (0 or 1) ? "Not in a town or field zone" : null;
    internal MeetingBell? StandingAt()
    {
        if (Position() is not {} p || p.World != player.HomeWorld.RowId ||
            data.GetExcelSheet<TerritoryType>().GetRowOrDefault(p.Territory)?.TerritoryIntendedUse.RowId is not (0 or 1)) return null;
        var b = bells.ObserveNavigationTarget();
        if (!b.Available || b.Distance > b.OrdinaryInteractionDistance || objects.SearchById(b.BellGameObjectId) is not {} obj) return null;
        return new(p.Territory,p.World,obj.BaseId,b.Position.X,b.Position.Y,b.Position.Z,
            data.GetExcelSheet<TerritoryType>().GetRow(p.Territory).PlaceName.Value.Name.ToString());
    }
    internal TradePartner? Partner(OwnerScope expected) =>
        io.TryGetPartner(expected.CharacterName,expected.HomeWorldName,out var p) && p.HomeWorldId == expected.HomeWorldId &&
        objects.LocalPlayer is {} me && objects.SearchById(p.GameObjectId) is {} other &&
        Vector3.Distance(me.Position,other.Position)<=3.5f && io.PartnerIsAvailable(p) ? p : null;
    public Task<Result<bool>> ObserveTradeableAsync(OwnerScope who, Deadline d) => Poll<bool>(d, () => Partner(who) != null ? Result<bool>.Ok(true) : null);
    public Task<Result<bool>> ReturnToAsync(CourierPosition p, Deadline d) => Navigate(p,null,d);
    public Task<Result<bool>> GoInRangeAsync(MeetingBell b, Deadline d) => Navigate(new(b.World,b.Territory,b.X,b.Y,b.Z),b,d);
    private sealed class IdleWindow
    {
        CourierPosition? last;
        DateTimeOffset since;
        internal bool Ready(CourierGame game,bool path=false)
        {
            var now=DateTimeOffset.UtcNow;var here=game.Position();bool idle=false;
            if(here!=null && game.IdleCharacter())
                try { idle=!game.Busy("Lifestream.IsBusy") && (!path || !game.Busy("vnavmesh.Path.IsRunning") && !game.Busy("vnavmesh.Nav.PathfindInProgress")); }
                catch { /* Missing IPC is unknown, not idle. */ }
            if(!idle || here!=last){last=here;since=now;return false;}
            if(now-since<TimeSpan.FromSeconds(5))return false;
            since=now;return true;
        }
    }
    private bool Busy(string gate)=>pi.GetIpcSubscriber<bool>(gate).InvokeFunc();
    private bool IdleCharacter()=>CharacterSwitchPolicy.GetRefusal(CharacterSwitchGameState.Observe(client,objects,conditions,false,null))==null;
    private async Task<Result<bool>> Navigate(CourierPosition p, MeetingBell? bell, Deadline d)
    {
        var teleportMarker=Marker("Teleport",Step.Value);var pathMarker=Marker("Approach",Step.Value);
        var shardMarker=Marker("Aethernet",Step.Value);
        bool hopped=shardMarker.Already;
        GilVendorTravelRoute? route=null;
        bool teleported=teleportMarker.Already, moving=pathMarker.Already;
        var idle=new IdleWindow();
        var target=new Vector3(p.X,p.Y,p.Z);
        var allowed=CanContinue.Value;
        try { return await Poll<bool>(d, () =>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"RouteStopped",d.Name);
            if (Position() is not {} here) return null;
            if ((teleported || hopped || moving) && idle.Ready(this,moving)) teleported=hopped=moving=false;
            if (here.World != p.World) return Result<bool>.Fail("WrongRendezvousWorld",d.Name);
            if (here.Territory != p.Territory)
            {
                route??=DalamudGilVendorCatalogBuilder.ResolveTravelRoutesForTerritory(data,p.Territory)
                    .FirstOrDefault(r=>attuned.Any(a=>a.AetheryteId==r.AetheryteId));
                if(route==null)return Result<bool>.Fail("NoAttunedAetheryte",p.Territory.ToString());
                if(hopped)return null;
                if(route.AethernetId is {} shard && here.Territory==route.AetheryteTerritoryId &&
                    objects.Any(o=>o.ObjectKind==ObjectKind.Aetheryte && o.BaseId==route.AetheryteId &&
                        Vector3.Distance(o.Position,new(here.X,here.Y,here.Z))<12))
                {
                    shardMarker.Save();var hop=aethernet.TrySubmit(shard);hopped=hop.Submitted;
                    return hopped || hop.Retryable ? null : Result<bool>.Fail(hop.Code,hop.Message);
                }
                if (teleported) return null;
                teleportMarker.Save();
                var result=teleport.TrySubmit(route.AetheryteId);
                teleported=result.Submitted;
                return teleported || result.Retryable ? null : Result<bool>.Fail(result.Code,result.Message);
            }
            var b=bell == null ? null : bells.ObserveNavigationTarget();
            if (bell == null ? Vector3.Distance(target,new(here.X,here.Y,here.Z)) <= 2 :
                b is {Available:true} && b.Distance <= b.OrdinaryInteractionDistance &&
                objects.SearchById(b.BellGameObjectId)?.BaseId == bell.ObjectId && Vector3.Distance(target,b.Position)<0.5f)
                return Result<bool>.Ok(true);
            if (moving) return null;
            pathMarker.Save();
            var path=nav.TryMoveCloseTo(target,1.5f);
            ownsPath=moving=path.Submitted;
            return moving || path.Retryable ? null : Result<bool>.Fail(path.Code,path.Message);
        }); }
        finally { if (moving && !unload.IsCancellationRequested) await dispatch(()=>{if(ownsPath)nav.TryStop();ownsPath=false;},unload.Token); }
    }
    public Task<Result<string>> SwitchRefusalAsync(Deadline deadline)=>Poll<string>(deadline,()=>
        Result<string>.Ok(CharacterSwitchPolicy.GetRefusal(CharacterSwitchGameState.Observe(client,objects,conditions,false,null))??""));
    private static Result<T>? Translate<T>(OperationResult result, Func<T> success) => !result.IsTerminal ? null :
        result.Status == OperationStatus.Succeeded ? Result<T>.Ok(success()) :
        new(result.Status == OperationStatus.Indeterminate ? CourierOutcome.Indeterminate : CourierOutcome.Failed,result.Code,result.Message);
    internal async void WatchLate(string name,Func<bool> observe)
    {
        try{await Poll<bool>(Deadline.After(name+"LateEvidence",30),()=>observe()?Result<bool>.Ok(true):null);}
        catch(OperationCanceledException) when(unload.IsCancellationRequested){}
        catch(Exception e){warn(name+"LateObservation: "+e.Message);}
    }
    private (bool Already,Action Save) Marker(string leg,Guid step)
    {
        if (step==Guid.Empty) throw new InvalidOperationException("TravelStepMissing");
        var record=submissions.Load();
        return (record.GetValueOrDefault(leg)==step,()=>{var latest=submissions.Load();latest[leg]=step; submissions.Save(latest);});
    }
    public Task<Result<OwnerScope>> SwitchAsync(OwnerScope target, Deadline d)
    {
        CharacterSwitchOperation? op=null;var idle=new IdleWindow();var marker=Marker("Switch",Step.Value);bool submitted=marker.Already;
        void Save(){marker.Save();submitted=true;}
        var step=Step.Value;
        var allowed=CanContinue.Value;
        return Poll<OwnerScope>(d,()=>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"SwitchStopped",d.Name);
            var retry=submitted && !CourierPlanner.Exact(owner(),target) && idle.Ready(this);
            if (op==null || retry) { op=new(login,new(target.CharacterName,target.HomeWorldName),d.Expires,warn,Save,submitted && !retry); }
            OperationResult Tick()=>op.Tick(CharacterSwitchGameState.Observe(client,objects,conditions,false,null),DateTimeOffset.UtcNow);
            var result=Tick();
            if(result.Status==OperationStatus.Indeterminate)WatchLate(d.Name,()=>{Tick();return op.LateIdentityObserved;});
            return Translate(result,owner);
        },true);
    }
    public Task<Result<bool>> EnsureWorldAsync(uint world, Deadline d) => Visit(world,d);
    public Task<Result<bool>> ReturnHomeAsync(Deadline d) => Visit(null,d);
    private Task<Result<bool>> Visit(uint? world,Deadline d)
    {
        WorldVisitOperation? op=null;var idle=new IdleWindow();var marker=Marker("Visit",Step.Value);bool submitted=marker.Already;
        void Save(){marker.Save();submitted=true;}
        var allowed=CanContinue.Value;
        return Poll<bool>(d,()=>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"VisitStopped",d.Name);
            var retry=Position() is {} here && here.World!=(world??player.HomeWorld.RowId) && submitted && idle.Ready(this);
            if(op==null || retry){
                op=world is {} id ? new(worlds,data.GetExcelSheet<World>().GetRow(id).Name.ToString(),player,commands,client,conditions,d.Expires,player.ContentId,warn,Save,submitted && !retry) :
                    WorldVisitOperation.ReturnHome(worlds,player,commands,client,conditions,d.Expires,warn,Save,submitted && !retry); }
            var result=op.Tick(DateTimeOffset.UtcNow);
            if(result.Status==OperationStatus.Indeterminate)WatchLate(d.Name,()=>{op.Tick(DateTimeOffset.UtcNow);return op.LateArrivalObserved;});
            return Translate(result,()=>true);
        },true);
    }
    public void Dispose(){ unload.Cancel(); if(ownsPath)nav.TryStop();bells.Dispose(); }
}
