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
        return (int)Math.Clamp(stacks * 30L + windows * 40L, 180, 3600);
    }
    public static Deadline After(string name, int seconds) => new(name, DateTimeOffset.UtcNow.AddSeconds(seconds));
}
// Messages and ledgers store commands by number: new commands go last. Claim takes units the carrier already holds into
// the run's custody (the carrier is the transfer's source); it changes only the carrier's ledger, never its bags.
// Unclaim returns claimed units no hand-in ever reached to the carrier's own stock, again by ledger alone.
// Persisted by number in carrier ledgers: new commands go last.
// Clear is unconditional: the carrier forgets the run's ledger, whatever it holds or awaits; held goods stay its own.
public enum CourierCommand { Status, Travel, Receive, Send, Confirm, ReturnHome, Stop, Begin, Finish, Settle, Clear, VerifySettlement, NotDelivered, Claim, Unclaim,
    // Settles a hand-in from the carrier's own completed send alone, for an owner whose receive cannot prove it.
    SettleSent }
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
    /// <summary>
    /// Settles this side's trade record for <paramref name="step"/> once the run has reconciled both sides' bags:
    /// <paramref name="credit"/> is what that reconciliation found moved, and the record must have credited exactly it.
    /// </summary>
    Task<Result<bool>> Settle(Guid step,OwnerScope partner,TradeEvidence peer,ImmutableArray<Shipment> credit,Deadline deadline);
}
/// <summary>Ends an owner receive that no longer tracks a trade its sender already finished; its armed window stays for settlement.</summary>
internal interface ICourierReceiveStall
{
    Task<Result<bool>> EndStalledReceiveAsync(Guid step,Deadline deadline);
}
/// <summary>
/// Closes the owner's receive checkpoint for a hand-in settled from evidence other than its own trade record:
/// goods the owner placed since the trade (QM-journaled). Only for a checkpoint
/// with no trade open, whose sender proved the full manifest left its bags.
/// </summary>
internal interface ICourierReceiveAttestation
{
    Task<Result<bool>> AttestReceived(Guid step,OwnerScope partner,TradeEvidence sender,string disposition,Deadline deadline,bool commit=true);
}
/// <summary>
/// Clear's forget: ends this client's trade for one of the run's steps and drops its checkpoint (pending trade and
/// receive arm) when it belongs to one of them. Another run's trade and checkpoint are left alone. Moves nothing.
/// </summary>
internal interface ICourierTradeForget
{
    void Forget(IReadOnlySet<Guid> steps);
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

/// <summary>
/// A hand-off settled from both sides' bags: Moved and Rest split its batch; Outside lists changes either side made outside
/// the trade (ignored, for a warning). A refusal (Code set) names every offending key, which side and both deltas.
/// </summary>
internal sealed record Reconciliation(ImmutableArray<Shipment> Moved, ImmutableArray<Shipment> Rest, ImmutableArray<string> Outside,
    string? Code = null, string Detail = "")
{
    internal bool Settled => Code == null;
    internal static Reconciliation Refused(string code, string detail, ImmutableArray<string> outside = default) =>
        new([], [], outside.IsDefault ? [] : outside, code, detail);
}
/// <summary>
/// The trade layer's own record of one step: every window it verified against bags as it closed. Read only while the step
/// is at rest (no trade running or window open, nothing armed, no unverified lock or pending cancellation).
/// </summary>
internal interface ICourierTradeRecord
{
    /// <summary>Units the step's trades credited, per key; refused when the record is not this step's or not at rest.</summary>
    Task<Result<IReadOnlyDictionary<(uint Item, bool Hq), long>>> CreditedAsync(Guid step, Deadline deadline);
}
internal static class Custody
{
    // Carried gil is always in the key universe. Matches (a fully verified trade) requires an item trade to leave gil
    // unchanged and a gil trade to move exactly the manifest's gil. Reconcile treats gil like any other key: outside a
    // gil manifest, a change on one side alone is an outside change, and only gil that moved between the two refuses.
    internal static readonly (uint Item, bool Hq) GilKey = (CourierPlanner.GilItemId, false);
    internal static bool Matches(TradeEvidence? e, OwnerScope who, ImmutableArray<Shipment> manifest, int sign)
        => e != null && CourierPlanner.Exact(e.Before.Character, who) && CourierPlanner.Exact(e.After.Character, who)
        && manifest.Length > 0 && manifest.All(s => s.Units > 0) &&
        e.Before.Stock.Concat(e.After.Stock).Select(s => (Item: s.ItemId, s.Hq)).Concat(manifest.Select(s => (s.Item, s.Hq))).Append(GilKey)
            .Distinct().All(key => (long)e.After.Count(key.Item, key.Hq) - e.Before.Count(key.Item, key.Hq)
            == sign * manifest.Where(s => s.Item == key.Item && s.Hq == key.Hq).Sum(s => (long)s.Units));
    internal static bool TrySlice(TradeEvidence local, OwnerScope who, TradeEvidence remote, OwnerScope other,
        ImmutableArray<Shipment> batch, int sign, out ImmutableArray<Shipment> moved, out ImmutableArray<Shipment> rest)
    {
        var settled = Reconcile(local, who, remote, other, batch, sign);
        (moved, rest) = (settled.Moved, settled.Rest);
        return settled.Settled;
    }
    /// <summary>
    /// What a hand-off between <paramref name="who"/> (local) and <paramref name="other"/> (remote) moved, from both sides'
    /// bags before and after it; <paramref name="sign"/> -1 when local sent, +1 when it received. Units are allocated in
    /// manifest order.
    /// <para>Only a change a trade between the two could have produced is held against the hand-off. A manifest key must
    /// move the same amount on both sides, between nothing and its batch. A key outside the manifest refuses only when it
    /// moved on both sides in the same direction (left one, reached the other): a change on one side alone, or both sides
    /// gaining or both losing, happened outside the trade and is reported in <see cref="Reconciliation.Outside"/>.</para>
    /// <para><paramref name="credited"/> is the local trade layer's own record of the step (units it verified window by
    /// window). When both sides' bags agree on a manifest key, the record must equal that amount, else the key refuses
    /// TradeRecordDisagrees. When they disagree, the key settles at the record only if it equals either side's bag change
    /// (local or remote), the other side's difference then being an outside change. The record need not match its own
    /// side's bags. Each client runs this against its own record, and the carrier's ledger acknowledgement of the settled
    /// amount proves the two agree, so what holds is: owner record = carrier record = one side's bag change.</para>
    /// </summary>
    internal static Reconciliation Reconcile(TradeEvidence local, OwnerScope who, TradeEvidence remote, OwnerScope other,
        ImmutableArray<Shipment> batch, int sign, IReadOnlyDictionary<(uint Item, bool Hq), long>? credited = null)
    {
        foreach (var (view, expected, side) in new[] { (local.Before, who, "local before"), (local.After, who, "local after"),
                     (remote.Before, other, "remote before"), (remote.After, other, "remote after") })
            if (!CourierPlanner.Exact(view.Character, expected))
                return Reconciliation.Refused("SettlementIdentityMismatch", $"{side} bags are {view.Character.CharacterName}'s, not {expected.CharacterName}'s.");
        string Name((uint Item, bool Hq) k) => k == GilKey ? "Gil" :
            (batch.FirstOrDefault(s => s.Item == k.Item && s.Hq == k.Hq)?.Name is { Length: > 0 } name ? $"{name} ({k.Item})" : $"item {k.Item}") + (k.Hq ? " HQ" : " NQ");
        string Deltas((uint Item, bool Hq) k) =>
            $"{who.CharacterName} {(long)local.After.Count(k.Item, k.Hq) - local.Before.Count(k.Item, k.Hq):+#,0;-#,0;0}, " +
            $"{other.CharacterName} {(long)remote.After.Count(k.Item, k.Hq) - remote.Before.Count(k.Item, k.Hq):+#,0;-#,0;0}";
        var counts = new Dictionary<(uint, bool), long>();
        var outside = ImmutableArray.CreateBuilder<string>();
        var refusals = new List<(int Rank, string Code, string Detail)>();
        // Manifest keys first, so details read in the order the run lists its lines.
        foreach (var k in batch.Select(s => (s.Item, s.Hq)).Concat(local.Before.Stock.Concat(local.After.Stock).Concat(remote.Before.Stock)
                     .Concat(remote.After.Stock).Select(x => (Item: x.ItemId, x.Hq))).Append(GilKey).Distinct())
        {
            // Both in the hand-off's direction: n left (sign -1) or reached (+1) the local side, m reached or left the remote side.
            long n = sign * ((long)local.After.Count(k.Item, k.Hq) - local.Before.Count(k.Item, k.Hq));
            long m = sign * ((long)remote.Before.Count(k.Item, k.Hq) - remote.After.Count(k.Item, k.Hq));
            long b = batch.Where(x => x.Item == k.Item && x.Hq == k.Hq).Sum(x => (long)x.Units);
            if (b == 0)
            {
                if (n == 0 && m == 0) continue;
                if (n != 0 && m != 0 && Math.Sign(n) == Math.Sign(m))
                    refusals.Add((0, "UnmanifestedTransfer", $"{Name(k)} is not in the hand-off but moved between them: {Deltas(k)}."));
                else outside.Add($"{Name(k)}: {Deltas(k)}");
                continue;
            }
            long? c = credited == null ? null : credited.GetValueOrDefault(k);
            if (n == m)
            {
                if (n < 0 || n > b) refusals.Add((2, "ManifestDeltaOutOfRange", $"{Name(k)} moved {n:#,0} of a {b:#,0} batch: {Deltas(k)}."));
                else if (c is { } recorded && recorded != n)
                    refusals.Add((3, "TradeRecordDisagrees", $"{Name(k)}: both bags moved {n:#,0}, {who.CharacterName}'s trade record {recorded:#,0}: {Deltas(k)}."));
                else counts[k] = n;
            }
            else if (c is { } recorded && recorded >= 0 && recorded <= b && (recorded == n || recorded == m))
            {
                // One side's bags match the verified trade record; the other side's difference is its own outside change.
                counts[k] = recorded;
                var (changed, beyond) = recorded == n ? (other.CharacterName, -sign * (m - recorded)) : (who.CharacterName, sign * (n - recorded));
                outside.Add($"{Name(k)}: {changed} changed {beyond:+#,0;-#,0;0} beyond the {recorded:#,0} {who.CharacterName}'s trade record verified ({Deltas(k)})");
            }
            else refusals.Add((1, "ManifestDeltaMismatch", $"{Name(k)}: {Deltas(k)}" +
                (c is { } r ? $"; {who.CharacterName}'s trade record {r:#,0}." : "; no settled trade record to break the tie.")));
        }
        if (refusals.Count > 0)
            return Reconciliation.Refused(refusals.MinBy(x => x.Rank).Code, string.Join(" ", refusals.Select(x => x.Detail)), outside.ToImmutable());
        ImmutableArray<Shipment> moved = [], rest = [];
        foreach (var s in batch)
        {
            var key = (s.Item, s.Hq); int n = (int)Math.Min(s.Units, counts.GetValueOrDefault(key));
            if (n > 0) { moved = moved.Add(s with { Units = n }); counts[key] -= n; }
            if (n < s.Units) rest = rest.Add(s with { Units = s.Units - n });
        }
        return new(moved, rest, outside.ToImmutable());
    }
    /// <summary>The trade port's record of <paramref name="step"/> at rest, or null when it has none (or none at rest) within 10s.</summary>
    internal static async Task<IReadOnlyDictionary<(uint Item, bool Hq), long>?> RecordOf(object trade, Guid step)
    {
        if (trade is not ICourierTradeRecord book) return null;
        try
        {
            var read = await book.CreditedAsync(step, Deadline.After("TradeRecord", 10)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return read is { Outcome: CourierOutcome.Succeeded, Value: { } credited } ? credited : null;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { return null; }
    }
    /// <summary>Units per key a trade record credited: its original queue less what is still queued.</summary>
    internal static IReadOnlyDictionary<(uint Item, bool Hq), long> Credited(IEnumerable<BagStock> original, IEnumerable<BagStock> remaining)
    {
        var credited = new Dictionary<(uint Item, bool Hq), long>();
        foreach (var x in original) credited[(x.ItemId, x.Hq)] = credited.GetValueOrDefault((x.ItemId, x.Hq)) + x.Units;
        foreach (var x in remaining) credited[(x.ItemId, x.Hq)] = credited.GetValueOrDefault((x.ItemId, x.Hq)) - x.Units;
        return credited;
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
    internal void Track(Task task)
    {
        if (task.IsCompleted) return;
        late.TryAdd(task, 0);
        _ = task.ContinueWith(t => { _ = t.Exception; late.TryRemove(t, out _); }, TaskScheduler.Default);
    }
    internal void WhenSettled(Action release)
    {
        var tasks = late.Keys.ToArray();
        if (tasks.Length == 0) release();
        else _ = Task.WhenAll(tasks).ContinueWith(t => { _ = t.Exception; release(); }, TaskScheduler.Default);
    }
    // A call's own poll sees the same deadline a tick later and names what it was still waiting for; it gets this long to say so.
    private static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(1);
    /// <summary>
    /// Runs one port call under its own deadline. <paramref name="observation"/> marks a pure read (bags, position,
    /// partner sighting, eligibility): one that runs out of time changed nothing, so it fails rather than reads as unverified.
    /// </summary>
    internal async Task<T> Call<T>(string name, int seconds, Func<Deadline, Task<Result<T>>> dispatch, bool observation = false)
    {
        var deadline = Deadline.After(name, seconds);
        var pending = dispatch(deadline);
        Result<T> r;
        try
        {
            var left = deadline.Expires - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException();
            try { r = await pending.WaitAsync(left).ConfigureAwait(false); }
            catch (TimeoutException) { r = await pending.WaitAsync(ReportGrace).ConfigureAwait(false); }
        }
        catch (TimeoutException)
        {
            late.TryAdd(pending, 0);
            _ = pending.ContinueWith(t => { late.TryRemove(t, out _); warning($"LateArrival {name}: {t.Exception?.Message ?? t.Status.ToString()}"); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new CourierFault(observation ? CourierOutcome.Failed : CourierOutcome.Indeterminate, name + "Deadline", $"{name}: {seconds}s elapsed.");
        }
        if (r.Outcome != CourierOutcome.Succeeded || r.Value == null || r.Value is false)
            throw new CourierFault(r.Outcome == CourierOutcome.Succeeded ? CourierOutcome.Failed : r.Outcome, r.Code,
                $"{name} ({seconds}s): {r.Detail}");
        return r.Value;
    }
}
