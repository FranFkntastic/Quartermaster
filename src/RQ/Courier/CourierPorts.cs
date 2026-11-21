using System.Collections.Immutable;
using RQ.Domain;

namespace RQ.Courier;

public enum CourierOutcome { Succeeded, Failed, Cancelled, Indeterminate }
public sealed record Result<T>(CourierOutcome Outcome, string Code, string Detail, T? Value = default)
{
    public static Result<T> Ok(T value) => new(CourierOutcome.Succeeded, "Succeeded", "", value);
    public static Result<T> Fail(string code, string detail) => new(CourierOutcome.Failed, code, detail);
}
public sealed record Deadline(string Name, DateTimeOffset Expires)
{
    /// <summary>
    /// One data-center transfer, or a return home from another data center. A world visit to a bell may
    /// cross data centers (regional DC travel), so every visit gets this budget.
    /// </summary>
    internal const int DataCenterSeconds = 360;
    /// <summary>
    /// A world visit toward <paramref name="bell"/>. The cross-region bridge may need two data-center hops
    /// (from a visited data center home first, then on to the bridge's), so it gets twice the budget.
    /// </summary>
    internal static int WorldSeconds(MeetingBell? bell) => bell?.IsPlaza == true ? 2 * DataCenterSeconds : DataCenterSeconds;
    // A return may lead into a housing ward only the carrier knows about, so it gets the ward budget.
    // A trade step must outlive the carrier's whole multi-window trade plus its partner observation.
    // Travel must outlive the carrier's world visit (two data-center hops for the bridge) and its approach;
    // a return home crosses back from another data center and may still walk to a ward bell.
    internal static int PeerSeconds(MeetingBell? bell, CourierCommand command, ImmutableArray<Shipment> manifest = default) => command switch
    {
        CourierCommand.ReturnHome => 780,
        CourierCommand.Travel => Math.Max(bell?.IsPlaza == true ? 780 : 360, WorldSeconds(bell) + (bell?.ApproachSeconds ?? 150) + 60),
        CourierCommand.Send or CourierCommand.Receive => Math.Max(bell?.Ward != null ? 540 : 360, TradeSeconds(manifest) + 120),
        _ => bell?.Ward != null ? 540 : 360,
    };
    /// <summary>
    /// One trade step may span several five-slot game windows (fragmented bag slots pack into
    /// more), so its budget grows generously with the manifest instead of tearing the trade down per window.
    /// Gil crosses at most a million per window, so every started million is a window of its own.
    /// </summary>
    internal static int TradeSeconds(ImmutableArray<Shipment> manifest)
    {
        if (manifest.IsDefault) return 180;
        var stacks = manifest.Count(s => !s.IsGil);
        var gil = manifest.Where(s => s.IsGil).Sum(s => (long)s.Units);
        var windows = (gil + Franthropy.Dalamud.Automation.Trades.TradePlanner.MaximumGilPerTrade - 1) / Franthropy.Dalamud.Automation.Trades.TradePlanner.MaximumGilPerTrade;
        return (int)Math.Clamp(stacks * 30L + windows * 40L, 180, 1200);
    }
    public static Deadline After(string name, int seconds) => new(name, DateTimeOffset.UtcNow.AddSeconds(seconds));
}
public enum CourierCommand { Status, Travel, Receive, Send, Confirm, ReturnHome, Stop, Begin, Finish, Settle, Clear, VerifySettlement, NotDelivered }
public sealed record TradeEvidence(InventoryView Before, InventoryView After);
public sealed record CourierMessage(int Version, Guid Run, Guid Step, CourierCommand Command, OwnerScope Owner,
    OwnerScope Partner, OwnerScope Carrier, ImmutableArray<Shipment> Manifest, MeetingBell? Bell = null,
    Guid? ConfirmStep = null, TradeEvidence? OwnerEvidence = null, string? RunKey = null, [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] InventoryView? CarrierBaseline=null);
public sealed record CourierReceipt(Guid Run, Guid Step, InventoryView? Bags = null, TradeEvidence? Evidence = null,
    ImmutableArray<Shipment> Custody = default, Guid? Unconfirmed = null, Guid? LastConfirmed = null, Guid? ActiveRun = null);
public sealed record CourierProgress(Guid Run, Guid Step, string Phase);
public interface ICourierPeerPort
{
    event Action<CourierProgress> Progress;
    event Action LinkLost;
    event Action<Guid> StopRequested;
    Task<Result<CourierReceipt>> SendAsync(CourierMessage command, Deadline deadline);
}
public interface ICourierTradePort
{
    // Succeeded attests observed exact partner AND locked offer (including quality and no gil).
    Task<Result<bool>> SendAsync(OwnerScope partner, ImmutableArray<Shipment> manifest, Deadline deadline);
    Task<Result<bool>> ReceiveAsync(OwnerScope partner, ImmutableArray<Shipment> manifest, Deadline deadline);
    Task<Result<bool>> CancelPreLockAsync(Deadline deadline);
}
// No dispatch: can only advance a durable, fully delivered checkpoint for this exact step.
internal interface ICourierDispatchEvidence
{
    Task<Result<bool>> WasNotOpened(Guid step,Deadline deadline);
}
internal interface ICourierSettlement
{
    Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,Deadline deadline);
}
public interface ICharacterSwitchPort
{
    Task<Result<string>> SwitchRefusalAsync(Deadline deadline);
    Task<Result<OwnerScope>> SwitchAsync(OwnerScope character, Deadline deadline);
}
public interface IWorldVisitPort
{
    Task<Result<bool>> EnsureWorldAsync(uint world, Deadline deadline);
    Task<Result<bool>> ReturnHomeAsync(Deadline deadline);
}
public interface IMeetingBellPort
{
    Task<Result<CourierPosition>> PositionAsync(Deadline deadline);
    Task<Result<bool>> ReturnToAsync(CourierPosition position, Deadline deadline);
    Task<Result<bool>> GoInRangeAsync(MeetingBell bell, Deadline deadline);
    Task<Result<bool>> ObserveTradeableAsync(OwnerScope character, Deadline deadline);
}
public sealed record CourierPosition(uint World, uint Territory, float X, float Y, float Z,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Ward = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Subdivision = false);
public interface IInventoryObservationPort
{
    Task<Result<InventoryView>> ObserveAsync(Deadline deadline);
}

internal static class Custody
{
    // Carried gil is always in the key universe: an item trade must leave gil unchanged on both sides,
    // and a gil trade must move exactly the manifest's gil and no stack.
    internal static readonly (uint Item, bool Hq) GilKey = (CourierPlanner.GilItemId, false);
    internal static bool Matches(TradeEvidence? e, OwnerScope who, ImmutableArray<Shipment> manifest, int sign)
        => e != null && CourierPlanner.Exact(e.Before.Character, who) && CourierPlanner.Exact(e.After.Character, who)
        && manifest.Length > 0 && manifest.All(s => s.Units > 0) &&
        e.Before.Stock.Concat(e.After.Stock).Select(s => (Item: s.ItemId, s.Hq)).Concat(manifest.Select(s => (s.Item, s.Hq))).Append(GilKey)
            .Distinct().All(key => (long)e.After.Count(key.Item, key.Hq) - e.Before.Count(key.Item, key.Hq)
            == sign * manifest.Where(s => s.Item == key.Item && s.Hq == key.Hq).Sum(s => (long)s.Units));
    // Allocate observed units in manifest order only after comparing every stock key,
    // including unrelated decreases, both identities and both quality variants.
    internal static bool TrySlice(TradeEvidence local, OwnerScope who, TradeEvidence remote, OwnerScope other,
        ImmutableArray<Shipment> batch, int sign, out ImmutableArray<Shipment> moved, out ImmutableArray<Shipment> rest)
    {
        moved=rest=[];
        if (!CourierPlanner.Exact(local.Before.Character,who) || !CourierPlanner.Exact(local.After.Character,who) ||
            !CourierPlanner.Exact(remote.Before.Character,other) || !CourierPlanner.Exact(remote.After.Character,other)) return false;
        var counts=new Dictionary<(uint,bool),long>();
        foreach(var k in local.Before.Stock.Concat(local.After.Stock).Concat(remote.Before.Stock).Concat(remote.After.Stock)
            .Select(x=>(x.ItemId,x.Hq)).Append((ItemId: GilKey.Item, Hq: GilKey.Hq)).Distinct())
        {
            var n=sign*((long)local.After.Count(k.ItemId,k.Hq)-local.Before.Count(k.ItemId,k.Hq));
            if(n<0 || n>batch.Where(x=>x.Item==k.ItemId && x.Hq==k.Hq).Sum(x=>(long)x.Units) ||
                n!=sign*((long)remote.Before.Count(k.ItemId,k.Hq)-remote.After.Count(k.ItemId,k.Hq)))return false;
            counts[k]=n;
        }
        foreach(var s in batch){
            var key=(s.Item,s.Hq);int n=(int)Math.Min(s.Units,counts.GetValueOrDefault(key));
            if(n>0){moved=moved.Add(s with {Units=n});counts[key]-=n;}
            if(n<s.Units)rest=rest.Add(s with {Units=s.Units-n});
        }
        return true;
    }
    internal static bool Covers(IEnumerable<Shipment> held, IEnumerable<Shipment> wanted)
        => wanted.GroupBy(s => s with { Units = 0 }).All(g => held.Where(s => (s with { Units = 0 }) == g.Key)
            .Sum(s => (long)s.Units) >= g.Sum(s => (long)s.Units));
    internal static ImmutableArray<Shipment> Apply(IEnumerable<Shipment> held, ImmutableArray<Shipment> batch, int sign)
        => [.. held.Concat(batch.Select(s => s with {Units = sign*s.Units})).GroupBy(s => s with {Units=0})
            .Select(g => g.Key with {Units=g.Sum(s=>s.Units)}).Where(s => s.Units != 0)];
}
internal sealed class CourierFault(CourierOutcome outcome, string code, string detail) : Exception(detail)
{
    internal CourierOutcome Outcome => outcome;
    internal string Code => code;
}
internal sealed class CourierCalls(Action<string> warning)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> late = new();
    internal void WhenSettled(Action release)
    {
        var tasks = late.Keys.ToArray();
        if (tasks.Length == 0) release();
        else _ = Task.WhenAll(tasks).ContinueWith(t => { _ = t.Exception; release(); }, TaskScheduler.Default);
    }
    internal async Task<T> Call<T>(string name, int seconds, Func<Deadline, Task<Result<T>>> dispatch)
    {
        var deadline = Deadline.After(name, seconds);
        var pending = dispatch(deadline);
        Result<T> r;
        try
        {
            var left = deadline.Expires - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException();
            r = await pending.WaitAsync(left).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            late.TryAdd(pending, 0);
            _ = pending.ContinueWith(t => { late.TryRemove(t, out _); warning($"LateArrival {name}: {t.Exception?.Message ?? t.Status.ToString()}"); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new CourierFault(CourierOutcome.Indeterminate, name + "Deadline", $"{name}: {seconds}s elapsed.");
        }
        if (r.Outcome != CourierOutcome.Succeeded || r.Value == null || r.Value is false)
            throw new CourierFault(r.Outcome == CourierOutcome.Succeeded ? CourierOutcome.Failed : r.Outcome, r.Code,
                $"{name} ({seconds}s): {r.Detail}");
        return r.Value;
    }
}
