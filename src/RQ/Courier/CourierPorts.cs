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
    public static Deadline After(string name, int seconds) => new(name, DateTimeOffset.UtcNow.AddSeconds(seconds));
}
public enum CourierCommand { Status, Travel, Receive, Send, Confirm, ReturnHome, Stop, Begin, Finish }
public sealed record TradeEvidence(InventoryView Before, InventoryView After);
public sealed record CourierMessage(int Version, Guid Run, Guid Step, CourierCommand Command, OwnerScope Owner,
    OwnerScope Partner, OwnerScope Carrier, ImmutableArray<Shipment> Manifest, MeetingBell? Bell = null,
    Guid? ConfirmStep = null, TradeEvidence? OwnerEvidence = null);
public sealed record CourierReceipt(Guid Run, Guid Step, InventoryView? Bags = null, TradeEvidence? Evidence = null,
    ImmutableArray<Shipment> Custody = default, Guid? Unconfirmed = null, Guid? LastConfirmed = null);
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
public interface ICharacterSwitchPort
{
    string? MaySwitchNow();
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
public sealed record CourierPosition(uint World, uint Territory, float X, float Y, float Z);
public interface IInventoryObservationPort
{
    Task<Result<InventoryView>> ObserveAsync(Deadline deadline);
}

internal static class Custody
{
    internal static bool Matches(TradeEvidence? e, OwnerScope who, ImmutableArray<Shipment> manifest, int sign)
        => e != null && CourierPlanner.Exact(e.Before.Character, who) && CourierPlanner.Exact(e.After.Character, who)
        && manifest.Length > 0 && manifest.All(s => s.Units > 0) &&
        e.Before.Stock.Concat(e.After.Stock).Select(s => (Item: s.ItemId, s.Hq)).Concat(manifest.Select(s => (s.Item, s.Hq)))
            .Distinct().All(key => (long)e.After.Count(key.Item, key.Hq) - e.Before.Count(key.Item, key.Hq)
            == sign * manifest.Where(s => s.Item == key.Item && s.Hq == key.Hq).Sum(s => (long)s.Units));
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
