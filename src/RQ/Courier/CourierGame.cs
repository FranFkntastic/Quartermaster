using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
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
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;
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
    // Trade operations step through several UI states per slot; a coarse poll would add its full interval to each one.
    internal const int TradePollMilliseconds = 16;
    internal async Task<Result<T>> Poll<T>(Deadline d, Func<Result<T>?> tick, bool primitiveDeadline = false, int pollMilliseconds = 100)
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
            await Task.Delay(pollMilliseconds, token).ConfigureAwait(false);
        }
        throw new OperationCanceledException(token);
    }
    internal InventoryView? Bags()
    {
        if (!client.IsLoggedIn || !player.IsLoaded || !owner().HasStableIdentity) return null;
        var bags = scanner.CapturePlayerStorage();
        if (!CourierSession.PlayerBags.All(b => bags.ObservedSources.Contains(b))) return null;
        var slots = bags.Bags.Where(b => CourierSession.PlayerBags.Contains(b.BagName)).SelectMany(b => b.Items).ToArray();
        // Carried gil is counted apart from the stacks: it takes no bag slot and is never a BagStock.
        return new(owner(), 140-slots.Length, [..slots.Select(s => new BagStock(s.ItemId,s.IsHq,checked((int)s.Quantity)))], CarriedGil());
    }
    private static unsafe int CarriedGil()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : checked((int)inventory->GetInventoryItemCount(TradePlanner.GilItemId, false, true, true, 0));
    }
    private const string BridgeWorld = "Ravana", BridgeShard = "Sapphire Avenue Exchange";
    private (bool Resolved, MeetingBell? Bell) bridge;
    /// <summary>
    /// The cross-region rendezvous: Ul'dah's main aetheryte plaza on Ravana (Materia, Oceania), which
    /// every region can reach by data-center travel. Resolved from the game data: the Ul'dah aetheryte is
    /// the one sharing an aethernet group with the Sapphire Avenue Exchange shard. Null when unresolved.
    /// </summary>
    internal MeetingBell? Bridge()
    {
        if (bridge.Resolved) return bridge.Bell;
        MeetingBell? found = null;
        try
        {
            var world = data.GetExcelSheet<World>().FirstOrDefault(w => w.IsPublic && w.Name.ToString() == BridgeWorld);
            var aetherytes = data.GetExcelSheet<Aetheryte>(Dalamud.Game.ClientLanguage.English);
            var shard = aetherytes.FirstOrDefault(a => !a.IsAetheryte && a.AethernetGroup != 0 &&
                a.AethernetName.ValueNullable?.Name.ToString() == BridgeShard);
            var plaza = shard.RowId == 0 ? default : aetherytes.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == shard.AethernetGroup);
            if (world.RowId != 0 && plaza.RowId != 0 && plaza.Territory.RowId != 0)
                found = new MeetingBell(plaza.Territory.RowId, world.RowId, 0, 0, 0, 0, "Ravana — Ul'dah");
            else warn($"CourierBridgeUnresolved: world {world.RowId}, shard {shard.RowId}, plaza {plaza.RowId}");
        }
        catch (Exception e) { warn("CourierBridgeUnresolved: " + e.Message); }
        bridge = (true, found);
        return found;
    }
    /// <summary>The main aetheryte (not an aethernet shard) serving a territory; null when it has none.</summary>
    private uint? MainAetheryte(uint territory) =>
        data.GetExcelSheet<Aetheryte>().FirstOrDefault(a => a.IsAetheryte && a.Territory.RowId == territory) is { RowId: > 0 } a ? a.RowId : null;
    internal uint DataCenter(uint world) => data.GetExcelSheet<World>().GetRowOrDefault(world)?.DataCenter.RowId ?? 0;
    public Task<Result<InventoryView>> ObserveAsync(Deadline d) => Poll<InventoryView>(d, () => Bags() is {} b ? Result<InventoryView>.Ok(b) : null);
    private CourierPosition? Position() => objects.LocalPlayer is {} p && player.IsLoaded && client.IsLoggedIn
        ? new(player.CurrentWorld.RowId,client.TerritoryType,p.Position.X,p.Position.Y,p.Position.Z) : null;
    public Task<Result<CourierPosition>> PositionAsync(Deadline d) => Poll<CourierPosition>(d, () => Position() is not {} p ? null :
        // A start inside a housing ward is only reachable again by its ward address.
        Result<CourierPosition>.Ok(WardCity(p.Territory)!=null && CurrentWard() is {} w ? p with {Ward=w.Number,Subdivision=w.Subdivision} : p));
    /// <summary>Why this place cannot hold a meeting bell at all; null where one could be captured.</summary>
    internal string? BellBlock() => Position() is {} p ? BellLocation(p,out _) : null;
    // Lifestream's address City is the gateway aetheryte, not the district territory id.
    private static int? WardCity(uint territory) => territory switch
    {
        339 => 8, 340 => 2, 341 => 9, 641 => 111, 979 => 70, _ => null
    };
    private float DestinationDistanceSquared(uint nodeId, Vector3 target)
    {
        if (data.GetExcelSheet<Aetheryte>().GetRowOrDefault(nodeId) is not { } node)
            return float.MaxValue;
        foreach (var reference in node.Level)
        {
            if (reference.RowId == 0 || reference.ValueNullable is not { } level)
                continue;
            var position = new Vector3(level.X, level.Y, level.Z);
            var distance = Vector3.DistanceSquared(position, target);
            if (position != Vector3.Zero && float.IsFinite(distance))
                return distance;
        }
        // City aethernet shards leave Aetheryte.Level empty (and often Aetheryte.Map too); their
        // positions live in MapMarker (DataType 4 = aethernet shard keyed by AethernetName,
        // DataType 3 = main aetheryte keyed by RowId).
        var mapRef = node.Map.RowId != 0 ? node.Map : node.Territory.ValueNullable?.Map ?? default;
        if (mapRef.RowId != 0 && mapRef.ValueNullable is { SizeFactor: > 0 } map &&
            data.GetSubrowExcelSheet<Lumina.Excel.Sheets.MapMarker>().GetRowOrDefault(map.MapMarkerRange) is { } markers)
        {
            var expectedType = node.IsAetheryte ? (byte)3 : (byte)4;
            var expectedKey = node.IsAetheryte ? node.RowId : node.AethernetName.RowId;
            var scale = map.SizeFactor / 100f;
            foreach (var marker in markers)
            {
                if (marker.DataType != expectedType || marker.DataKey.RowId != expectedKey)
                    continue;
                var worldX = ((marker.X - 1024f) / scale) - map.OffsetX;
                var worldZ = ((marker.Y - 1024f) / scale) - map.OffsetY;
                var distance = Vector2.DistanceSquared(new Vector2(worldX, worldZ), new Vector2(target.X, target.Z));
                if (float.IsFinite(distance))
                    return distance;
            }
        }
        return float.MaxValue;
    }
    private static unsafe (int Number,bool Subdivision)? CurrentWard()
    {
        var housing=HousingManager.Instance();
        if(housing==null || housing->CurrentTerritory==null)return null;
        var index=housing->GetCurrentWard();
        var division=housing->GetCurrentDivision();
        return index is >=0 and <30 && division is 1 or 2 ? (index+1,division==2) : null;
    }
    private string? BellLocation(CourierPosition p,out (int Number,bool Subdivision)? ward)
    {
        ward=null;
        if(p.World!=player.HomeWorld.RowId)return "Not on home world";
        return data.GetExcelSheet<TerritoryType>().GetRowOrDefault(p.Territory)?.TerritoryIntendedUse.RowId switch
        {
            0 or 1 => null,
            13 when WardCity(p.Territory)!=null => (ward=CurrentWard())==null ? "Ward unavailable" : null,
            _ => "Not a public bell zone"
        };
    }
    internal MeetingBell? StandingAt()
    {
        if (Position() is not {} p || BellLocation(p,out var ward)!=null) return null;
        var b = bells.ObserveNavigationTarget();
        if (!b.Available || b.Distance > b.OrdinaryInteractionDistance || objects.SearchById(b.BellGameObjectId) is not {} obj) return null;
        var label=data.GetExcelSheet<TerritoryType>().GetRow(p.Territory).PlaceName.Value.Name.ToString();
        if(ward is {} w)label+=$", Ward {w.Number}"+(w.Subdivision?", Subdivision":"");
        return new(p.Territory,p.World,obj.BaseId,b.Position.X,b.Position.Y,b.Position.Z,label,ward?.Number,ward?.Subdivision??false);
    }
    internal bool InWard => WardCity(client.TerritoryType)!=null && CurrentWard()!=null;
    private (ulong Owner,uint World,uint Territory,(int,bool)? Ward) Location =>
        (player.ContentId,player.CurrentWorld.RowId,client.TerritoryType,CurrentWard());
    private (ulong Owner,uint World,uint Territory,(int,bool)? Ward) recordingAt;
    private readonly List<WardPoint> trail=[];
    private uint recordingShard;
    internal int PointCount=>trail.Count;
    internal string? RouteError {get;private set;}
    // The shard table carries no positions; each placed shard's EObj row points at its table row instead.
    private (uint Id,Vector3 Position)? NearestShard(Vector3 from)
    {
        var shards=data.GetExcelSheet<Lumina.Excel.Sheets.HousingAethernet>();
        var eobj=data.GetExcelSheet<Lumina.Excel.Sheets.EObj>();
        (uint,Vector3)? nearest=null;
        var best=float.MaxValue;
        foreach(var o in objects)
        {
            if(o.ObjectKind!=ObjectKind.EventObj || !eobj.TryGetRow(o.BaseId,out var e) ||
                !shards.TryGetRow(e.Data.RowId,out var row) || row.TerritoryType.RowId!=client.TerritoryType)continue;
            var distance=Vector3.DistanceSquared(o.Position,from);
            if(distance<best){best=distance;nearest=(row.RowId,o.Position);}
        }
        return nearest;
    }
    internal void CancelRoute(){trail.Clear();RouteError=null;}
    private void RouteRefusal(string reason){CancelRoute();RouteError=reason;warn("RouteRecording: "+reason);}
    internal void StartRoute()
    {
        if(trail.Count!=0)return;
        if(!InWard || objects.LocalPlayer is not {} me || BellBlock()!=null ||
            NearestShard(me.Position) is not {} shard || Vector3.Distance(me.Position,shard.Position)>10)
        {RouteRefusal("No eligible shard in reach");return;}
        RouteError=null;recordingAt=Location;recordingShard=shard.Id;
        trail.Add(new(me.Position.X,me.Position.Y,me.Position.Z));
    }
    internal void SampleRoute()
    {
        if(trail.Count==0)return;
        if(objects.LocalPlayer is not {} me || recordingAt!=Location){RouteRefusal("Ward or character changed");return;}
        if(Vector3.Distance(me.Position,trail[^1].Position)<1.5f)return;
        if(trail.Count>=199){RouteRefusal("Route too long");return;}
        trail.Add(new(me.Position.X,me.Position.Y,me.Position.Z));
    }
    internal MeetingBell? CaptureHere(ulong character)
    {
        if(!player.IsLoaded || player.ContentId!=character)return null;
        var recording=trail.Count>0;
        SampleRoute();
        if(StandingAt() is not {} bell || recording && trail.Count==0)return null;
        if(!recording)return bell;
        var at=objects.LocalPlayer!.Position;
        return bell with {Shard=recordingShard,Trail=[..trail,new(at.X,at.Y,at.Z)]};
    }
    private const float TradeRange=3.5f, MeetRange=3.2f, ReturnArrivalRange=3f;
    private (TradePartner Partner,Vector3 Position,float Distance)? Sighting(OwnerScope expected) =>
        io.TryGetPartner(expected.CharacterName,expected.HomeWorldName,out var p) && p.HomeWorldId == expected.HomeWorldId &&
        objects.LocalPlayer is {} me && objects.SearchById(p.GameObjectId) is {} other
            ? (p,other.Position,Vector3.Distance(me.Position,other.Position)) : null;
    internal TradePartner? Partner(OwnerScope expected) =>
        Sighting(expected) is {} seen && seen.Distance<=TradeRange && io.PartnerIsAvailable(seen.Partner) ? seen.Partner : null;
    private sealed class WalkWatch
    {
        private Vector3? anchor;
        private DateTimeOffset since;
        internal string? Stalled(CourierGame game,Vector3? position,Vector3 target)
        {
            if(!game.ownsPath || position is not {} at || !game.Busy("vnavmesh.Path.IsRunning"))
            {anchor=null;return null;}
            if(anchor==null || Vector3.Distance(anchor.Value,at)>0.5f)
            {anchor=at;since=DateTimeOffset.UtcNow;}
            return DateTimeOffset.UtcNow-since<TimeSpan.FromSeconds(8)?null:
                $"At {at}, target {target}, remaining {Vector3.Distance(at,target):0.0} yalms";
        }
    }
    public async Task<Result<bool>> ObserveTradeableAsync(OwnerScope who, Deadline d)
    {
        // Both characters stop near the bell on their own, which can leave them just outside trade range of each other.
        // Whoever sees the other out of range walks up; each client's view of the other's position differs slightly.
        var allowed=CanContinue.Value;
        bool walking=false, met=false;
        var watch=new WalkWatch();Vector3 walkTarget=default;
        var last="partner not visible";
        var walkedAt=DateTimeOffset.MinValue;
        // Tradeable but outside the meeting margin: proceed after a short grace whether or not an approach could run.
        DateTimeOffset? edgeSince=null;
        static DateTimeOffset now()=>DateTimeOffset.UtcNow;
        try
        {
            var outcome=await Poll<bool>(d, () =>
            {
                var seenNow=Sighting(who);
                if(walking && watch.Stalled(this,objects.LocalPlayer?.Position,seenNow?.Position??walkTarget) is {} stalled)
                {warn("NavigationStalled: "+stalled);return Result<bool>.Fail("NavigationStalled",stalled);}
                // Meet inside a margin of trade range, so the other client's slightly different view is in range too.
                var close=seenNow is {Distance:<=MeetRange};
                if (Partner(who) != null)
                {
                    edgeSince??=now();
                    if (close || now()-edgeSince>TimeSpan.FromSeconds(6)) return Result<bool>.Ok(true);
                }
                else edgeSince=null;
                if (allowed?.Invoke()==false) return new(CourierOutcome.Cancelled,"RendezvousStopped",d.Name);
                if (seenNow is not {} seen) { last="partner not visible"; return null; }
                last=close ? $"partner in range ({seen.Distance:0.00}) but not available to trade"
                    : $"partner {seen.Distance:0.00} away, meeting range {MeetRange:0.0}";
                if (close || seen.Distance>30 || now()-walkedAt<TimeSpan.FromSeconds(3) || !IdleCharacter()) return null;
                bool busy;
                try { busy=Busy("vnavmesh.Path.IsRunning") || Busy("vnavmesh.SimpleMove.PathfindInProgress"); }
                catch { return null; }
                if (busy) return null;
                var path=nav.TryMoveCloseTo(seen.Position,2f);
                if (path.Submitted) { walking=ownsPath=true; walkedAt=now(); walkTarget=seen.Position;watch=new(); }
                else last+=$"; approach refused: {path.Code}";
                return null;
            });
            met=outcome.Outcome==CourierOutcome.Succeeded;
            return outcome;
        }
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError e)
        {warn("NavigationIpcUnavailable: "+e.Message);return Result<bool>.Fail("NavigationIpcUnavailable",e.Message);}
        finally
        {
            if (!met) warn($"{d.Name} not met for {who.CharacterName}: {last}");
            if (walking && !unload.IsCancellationRequested) await dispatch(()=>{ if(ownsPath)nav.TryStop(); ownsPath=false; },unload.Token);
        }
    }
    public Task<Result<bool>> ReturnToAsync(CourierPosition p, Deadline d) => Navigate(p,null,d);
    public Task<Result<bool>> GoInRangeAsync(MeetingBell b, Deadline d) =>
        b.IsPlaza ? NavigatePlaza(b,d) : Navigate(new(b.World,b.Territory,b.X,b.Y,b.Z),b,d);
    private const float PlazaArrivalRange = 12f, PlazaWalkRange = 8f;
    /// <summary>
    /// Reaches a plaza rendezvous: teleports to its territory's attuned main aetheryte, then walks to within
    /// a few yalms of the live crystal. The partner observation then closes the last few yalms to trade range.
    /// </summary>
    private async Task<Result<bool>> NavigatePlaza(MeetingBell bell, Deadline d)
    {
        var teleportMarker=Marker("Teleport",Step.Value);var pathMarker=Marker("Approach",Step.Value);
        bool teleported=teleportMarker.Already, moving=pathMarker.Already, arrived=false;
        var idle=new IdleWindow();var watch=new WalkWatch();var walkTarget=default(Vector3);
        var allowed=CanContinue.Value;
        var crystal=MainAetheryte(bell.Territory);
        Result<bool> Failure(string code,string detail){warn($"{code}: {bell.Label} on world {bell.World}: {detail}");return Result<bool>.Fail(code,detail);}
        try { var outcome=await Poll<bool>(d, () =>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"RouteStopped",d.Name);
            if (Position() is not {} here) return null;
            var at=new Vector3(here.X,here.Y,here.Z);
            if(ownsPath && watch.Stalled(this,at,walkTarget) is {} stalled)
            {
                nav.TryStop();ownsPath=moving=false;
                return Failure("NavigationStalled",stalled);
            }
            if ((teleported || moving) && idle.Ready(this,moving))
            {if(moving)ownsPath=false;teleported=moving=false;}
            if (here.World != bell.World) return Result<bool>.Fail("WrongRendezvousWorld",d.Name);
            if (crystal is not {} aetheryte) return Failure("PlazaAetheryteUnknown",$"Territory {bell.Territory} has no main aetheryte.");
            if (here.Territory != bell.Territory)
            {
                if (teleported) return null;
                if (!attuned.Any(a => a.AetheryteId == aetheryte)) return Result<bool>.Fail("NoAttunedAetheryte",bell.Territory.ToString());
                teleportMarker.Save();
                var result=teleport.TrySubmit(aetheryte);
                teleported=result.Submitted;
                return teleported || result.Retryable ? null : Result<bool>.Fail(result.Code,result.Message);
            }
            // The crystal streams in shortly after the zone loads; until then there is nothing to walk to.
            if (objects.FirstOrDefault(o => o.ObjectKind == ObjectKind.Aetheryte && o.BaseId == aetheryte) is not {} live) return null;
            if (Vector3.Distance(at,live.Position) <= PlazaArrivalRange) return Result<bool>.Ok(true);
            if (moving || !IdleCharacter()) return null;
            if (Busy("vnavmesh.Path.IsRunning") || Busy("vnavmesh.SimpleMove.PathfindInProgress")) return null;
            pathMarker.Save();walkTarget=live.Position;
            var path=nav.TryMoveCloseTo(walkTarget,PlazaWalkRange);
            ownsPath=moving=path.Submitted;
            if(moving)watch=new();
            return moving || path.Retryable ? null : Result<bool>.Fail(path.Code,path.Message);
        }); arrived=outcome.Outcome==CourierOutcome.Succeeded; if(!arrived)warn($"{outcome.Code}: {outcome.Detail}");return outcome; }
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError e){return Failure("NavigationIpcUnavailable",e.Message);}
        finally
        {
            if (ownsPath && !unload.IsCancellationRequested) await dispatch(()=>{ if(ownsPath)nav.TryStop(); ownsPath=false; },unload.Token);
        }
    }
    private sealed class IdleWindow
    {
        CourierPosition? last;
        DateTimeOffset since;
        internal bool Ready(CourierGame game,bool path=false)
        {
            var now=DateTimeOffset.UtcNow;var here=game.Position();bool idle=false;
            if(here!=null && game.IdleCharacter())
                try { idle=!game.Busy("Lifestream.IsBusy") && (!path || !game.Busy("vnavmesh.Path.IsRunning") && !game.Busy("vnavmesh.SimpleMove.PathfindInProgress")); }
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
        var recorded=bell?.HasRoute==true;
        var wardHop=recorded?Marker("WardAethernet",Step.Value):default;
        var shardWalk=recorded?Marker("WardShardApproach",Step.Value):default;
        bool hopPending=wardHop.Already, walkingToShard=recorded && shardWalk.Already && !moving && !hopPending;
        bool following=recorded && moving,rebuilt=false,rebuilding=false;
        var hopIdle=new IdleWindow();var shardIdle=new IdleWindow();
        var walkTarget=target;var watch=new WalkWatch();
        var allowed=CanContinue.Value;
        var wardNumber=bell?.Ward ?? p.Ward;
        var subdivision=bell?.Subdivision ?? p.Subdivision;
        var place=bell?.Label ?? $"territory {p.Territory}";
        var wardMarker=wardNumber!=null ? Marker("Ward",Step.Value) : default;
        var wardSubmitted=wardMarker.Already;
        bool wardWasBusy=false;
        var wardIdle=new IdleWindow();
        Result<bool> WardFailure(string code,string detail)
        {
            warn($"{code}: {place} on world {p.World}: {detail}");
            return Result<bool>.Fail(code,detail);
        }
        bool wardOwned=false, arrived=false;
        // A return to a plain position (no bell) walks to it; one walk may be cut short and is resubmitted once.
        int returnWalks=0;
        try { var outcome=await Poll<bool>(d, () =>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"RouteStopped",d.Name);
            if (Position() is not {} here) return null;
            if(ownsPath && watch.Stalled(this,new(here.X,here.Y,here.Z),walkTarget) is {} stalled)
            {
                if(!nav.TryStop())return WardFailure("NavigationStalled",stalled+"; path stop refused");
                ownsPath=moving=walkingToShard=false;
                if(following || rebuilt)return WardFailure("NavigationStalled",stalled);
                warn("NavigationStalled: "+stalled+"; retrying after rebuild");
                rebuilt=rebuilding=true;watch=new();
                try{if(!Busy("vnavmesh.Nav.Rebuild"))warn("NavRebuildRefused: "+stalled);}
                catch(Exception e){warn("NavRebuildUnavailable: "+e.Message);}
                return null;
            }
            if(rebuilding)
            {
                try{if(!Busy("vnavmesh.Nav.IsReady"))return null;}
                catch(Exception e){return WardFailure("NavReadyUnavailable",e.Message);}
                rebuilding=false;
            }
            if ((teleported || hopped || moving) && idle.Ready(this,moving))
            {if(moving)ownsPath=false;teleported=hopped=moving=false;}
            if (here.World != p.World) return Result<bool>.Fail("WrongRendezvousWorld",d.Name);
            if(wardNumber is {} ward)
            {
                if(WardCity(p.Territory) is not {} city || ward is <1 or >30)
                    return WardFailure("InvalidWardAddress",$"Territory {p.Territory}, ward {ward}");
                var current=here.Territory==p.Territory ? CurrentWard() : null;
                bool busy;
                try { busy=Busy("Lifestream.IsBusy"); }
                catch(Exception e){return WardFailure("WardTravelUnavailable",e.Message);}
                // Address travel can keep walking to its plot after the zone transition.
                // Do not race it with our own path, or attest arrival while it owns movement.
                if(wardSubmitted && busy)wardWasBusy=true;
                if(busy || !IdleCharacter()){wardIdle=new();return null;}
                // A recorded shard hop below crosses into the subdivision itself, so only the ward number must match.
                if(current is not {} at || at.Number!=ward || at.Subdivision!=subdivision && !recorded)
                {
                    if(wardSubmitted && !wardIdle.Ready(this))return null;
                    if(wardWasBusy && current.HasValue)
                        return WardFailure("WrongWard",$"Expected ward {ward} (subdivision {subdivision}); observed {current}");
                    var gate=pi.GetIpcSubscriber<(string,int,int,int,int,int,int,bool,bool,string),object>("Lifestream.GoToHousingAddress");
                    if(!gate.HasAction)return WardFailure("WardTravelUnavailable","GoToHousingAddress is not registered");
                    wardMarker.Save!();
                    // Lifestream 2.6 address tuple: (Name, World, City, Ward 1-30, PropertyType 0=House/1=Apartment,
                    // Plot 1-60, Apartment, ApartmentSubdivision, AliasEnabled, Alias). For a house it enters the ward,
                    // then hops to the shard serving plot Plot-1 and walks to that plot; an out-of-range Plot (0) stops
                    // at ward entry. A recorded bell hops straight to its own shard, so it must not take a plot detour.
                    // Without one, plot 31 is the only way across into the subdivision.
                    var plot=recorded || !subdivision ? 0 : 31;
                    try { gate.InvokeAction(("",checked((int)p.World),city,ward,0,plot,1,false,false,"")); }
                    catch(Exception e){return WardFailure("WardTravelSubmissionFailed",e.Message);}
                    wardSubmitted=wardOwned=true;wardIdle=new();return null;
                }
            }
            if (here.Territory != p.Territory)
            {
                if(WardCity(p.Territory)!=null)return WardFailure("WardUnknown","The destination is in a housing ward whose number was not recorded.");
                route??=DalamudGilVendorCatalogBuilder.ResolveTravelRoutesForTerritory(data,p.Territory)
                    .Where(r => attuned.Any(a => a.AetheryteId == r.AetheryteId))
                    .MinBy(r => DestinationDistanceSquared(r.AethernetId ?? r.AetheryteId, target));
                if(route==null)return bell==null ? WardFailure("ReturnStartUnreachable","No attuned aetheryte serves this territory")
                    : Result<bool>.Fail("NoAttunedAetheryte",p.Territory.ToString());
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
            if (bell == null)
            {
                // Arrival is standing at the position, not entering its territory or ward.
                var gap=Vector3.Distance(new(here.X,here.Y,here.Z),target);
                if (gap<=ReturnArrivalRange) return Result<bool>.Ok(true);
                if (moving) return null;
                if (returnWalks>=2) return WardFailure("ReturnPositionUnreached",$"Walk ended {gap:0.0} yalms from {target}");
            }
            var b=bell == null ? null : bells.ObserveNavigationTarget();
            if (bell != null && b is {Available:true} && b.Distance <= b.OrdinaryInteractionDistance &&
                objects.SearchById(b.BellGameObjectId)?.BaseId == bell.ObjectId && Vector3.Distance(target,b.Position)<0.5f)
                return Result<bool>.Ok(true);
            if (moving) return null;
            if(recorded)
            {
                var points=bell!.Trail!;
                var pos=new Vector3(here.X,here.Y,here.Z);
                if(walkingToShard)
                {
                    if(Busy("vnavmesh.Path.IsRunning") || Busy("vnavmesh.SimpleMove.PathfindInProgress") || !IdleCharacter())return null;
                    // Stopped beside the shard: hop on this tick. Stopping short retries the walk after a settled pause.
                    if(!(NearestShard(pos) is {} reached && Vector3.Distance(pos,reached.Position)<=10) && !shardIdle.Ready(this,true))return null;
                    walkingToShard=ownsPath=false;
                }
                if(!following && Vector3.Distance(pos,points[0].Position)>6)
                {
                    if(hopPending && !hopIdle.Ready(this))return null;
                    var nearest=NearestShard(pos);
                    if(nearest is not {} near)return WardFailure("WardShardMissing",place);
                    if(Vector3.Distance(pos,near.Position)>10)
                    {
                        shardWalk.Save!();walkTarget=near.Position;
                        var approach=nav.TryMoveCloseTo(walkTarget,2);
                        ownsPath=walkingToShard=approach.Submitted;shardIdle=new();
                        return walkingToShard || approach.Retryable ? null : WardFailure(approach.Code,approach.Message);
                    }
                    if(near.Id!=bell.Shard)
                    {
                        wardHop.Save!();
                        try{hopPending=pi.GetIpcSubscriber<uint,bool>("Lifestream.HousingAethernetTeleportById").InvokeFunc(bell.Shard!.Value);}
                        catch(Exception e){return WardFailure("WardHopUnavailable",e.Message);}
                        if(!hopPending)return WardFailure("WardHopRefused",$"Shard {bell.Shard}");
                        wardOwned=true;hopIdle=new();return null;
                    }
                }
                if(Busy("vnavmesh.Path.IsRunning") || Busy("vnavmesh.SimpleMove.PathfindInProgress"))return null;
                // A fresh replay walks the trail in order; only a resumed one continues from the nearest point,
                // since a trail may pass close to its own start again.
                var start=following ? Enumerable.Range(0,points.Length).MinBy(i=>Vector3.DistanceSquared(pos,points[i].Position)) : 0;
                pathMarker.Save();
                try{pi.GetIpcSubscriber<List<Vector3>,bool,object>("vnavmesh.Path.MoveTo").InvokeAction([..points.Skip(start).Select(x=>x.Position)],false);}
                catch(Exception e){return WardFailure("WardRouteUnavailable",e.Message);}
                walkTarget=target;ownsPath=moving=following=true;watch=new();return null;
            }
            pathMarker.Save();
            var path=nav.TryMoveCloseTo(target,1.5f);
            ownsPath=moving=path.Submitted;
            if (moving && bell == null) returnWalks++;
            return moving || path.Retryable ? null : Result<bool>.Fail(path.Code,path.Message);
        }); arrived=outcome.Outcome==CourierOutcome.Succeeded; if(!arrived)warn($"{outcome.Code}: {outcome.Detail}");return outcome; }
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError e){return WardFailure("NavigationIpcUnavailable",e.Message);}
        finally
        {
            // A stopped, failed or timed-out approach must not leave Lifestream walking the character on its own.
            if ((ownsPath || wardOwned && !arrived) && !unload.IsCancellationRequested) await dispatch(()=>
            {
                if(ownsPath)nav.TryStop();ownsPath=false;
                if(!wardOwned || arrived)return;
                try { if(Busy("Lifestream.IsBusy"))pi.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
                catch(Exception e){warn("WardTravelAbort: "+e.Message);}
            },unload.Token);
        }
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
    /// <summary>
    /// Whether a visit must return home first: data-center travel starts only from the home world, so a
    /// character visiting any other world (on its home data center or another one) that must cross to a
    /// different data center goes home before moving on.
    /// </summary>
    private bool HomeFirst(uint here, uint destination, uint home) =>
        here != home && destination != home && DataCenter(here) != DataCenter(destination);
    // Each leg keeps its own durable submission marker: a step that returns home and then visits a world
    // (or returns one character home and visits with the next) must not read one leg's submission as the other's.
    private Task<Result<bool>> Visit(uint? world,Deadline d)
    {
        WorldVisitOperation? op=null;var idle=new IdleWindow();var marker=Marker(world==null?"VisitHome":"Visit",Step.Value);bool submitted=marker.Already;
        void Save(){marker.Save();submitted=true;}
        var homeMarker=Marker("VisitReturnHome",Step.Value);bool homeSubmitted=homeMarker.Already;
        void SaveHome(){homeMarker.Save();homeSubmitted=true;}
        WorldVisitOperation? homeOp=null;var homeIdle=new IdleWindow();bool homeChecked=world==null,homeFirst=false;
        var allowed=CanContinue.Value;
        return Poll<bool>(d,()=>
        {
            if(allowed?.Invoke()==false)return new(CourierOutcome.Cancelled,"VisitStopped",d.Name);
            if(!homeChecked)
            {
                if(Position() is not {} start)return null; // The home-first check needs a position.
                homeChecked=true;
                homeFirst=HomeFirst(start.World,world!.Value,player.HomeWorld.RowId);
                if(homeFirst)warn($"VisitReturnHome: world {start.World} is on another data center than home {player.HomeWorld.RowId} and destination {world}; returning home first.");
            }
            if(homeFirst)
            {
                var homeRetry=Position() is {} away && away.World!=player.HomeWorld.RowId && homeSubmitted && homeIdle.Ready(this);
                if(homeOp==null || homeRetry)
                    homeOp=WorldVisitOperation.ReturnHome(worlds,player,commands,client,conditions,d.Expires,warn,SaveHome,homeSubmitted && !homeRetry);
                var home=homeOp.Tick(DateTimeOffset.UtcNow);
                if(!home.IsTerminal)return null;
                if(home.Status!=OperationStatus.Succeeded)
                {
                    if(home.Status==OperationStatus.Indeterminate)WatchLate(d.Name,()=>{homeOp.Tick(DateTimeOffset.UtcNow);return homeOp.LateArrivalObserved;});
                    return Translate(home,()=>true);
                }
                homeFirst=false;
                return null;
            }
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
