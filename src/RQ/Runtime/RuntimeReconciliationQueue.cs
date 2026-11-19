using RQ.Domain;
using RQ.Inventory;

namespace RQ.Runtime;

[Flags]
public enum RuntimeDomain
{
    None = 0,
    PlayerInventory = 1 << 0,
    RetainerStock = 1 << 1,
    Plans = 1 << 2,
    Listings = 1 << 3,
    Operations = 1 << 4,
    All = PlayerInventory | RetainerStock | Plans | Listings | Operations,
}

public sealed record RuntimeChangeNotice(RuntimeDomain Domain, string Kind, string? OperationId);

public static class RuntimeChangeNotificationPolicy
{
    public const string RetainerManagement = "retainer_management";
    public const string RetainerListings = "retainer_listings";
    public const string Operation = "operation";
    public const string Owner = "owner";

    public static bool TryGetPublishedKind(RuntimeChangeNotice notice, out string kind)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.Kind is "periodic" or "opened")
        {
            kind = string.Empty;
            return false;
        }

        if (notice.Kind == "owner_transition")
        {
            kind = Owner;
            return true;
        }

        if ((notice.Domain & (RuntimeDomain.PlayerInventory | RuntimeDomain.RetainerStock | RuntimeDomain.Plans)) != 0)
        {
            kind = RetainerManagement;
            return true;
        }

        if ((notice.Domain & RuntimeDomain.Listings) != 0)
        {
            kind = RetainerListings;
            return true;
        }

        if ((notice.Domain & RuntimeDomain.Operations) != 0)
        {
            kind = Operation;
            return true;
        }

        kind = string.Empty;
        return false;
    }
}

public sealed record RuntimeReconciliationBatch(
    RuntimeDomain Domains,
    PlayerInventoryCacheChange? PlayerInventoryChange,
    IReadOnlyList<RuntimeChangeNotice> Notices)
{
    public int RequestCount { get; init; }
    public TimeSpan Waited { get; init; }
    public static RuntimeReconciliationBatch Empty { get; } = new(RuntimeDomain.None, null, []);
    public bool HasWork => Domains != RuntimeDomain.None;
}

/// <summary>
/// Coalesces change notifications until the framework thread reaches a safe
/// reconciliation checkpoint. The queue stores invalidation intent, not a copy
/// of the derived runtime model that may already be stale when it is drained.
/// </summary>
public sealed class RuntimeReconciliationQueue
{
    public static readonly TimeSpan BackgroundQuietPeriod = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BackgroundMaximumDelay = TimeSpan.FromSeconds(2);
    private readonly TimeProvider clock;
    private sealed record PendingNotice(RuntimeChangeNotice Notice, long First, long Last, int Count, bool Immediate);

    public RuntimeReconciliationQueue(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    private readonly object gate = new();
    private RuntimeDomain pendingDomains;
    private readonly Dictionary<(RuntimeDomain Domain, string Kind, string? OperationId), PendingNotice> notices = [];
    private OwnerScope? playerOwner;
    private DateTime playerObservedAtUtc;
    private bool playerRequiresFullRefresh;
    private readonly Dictionary<(string Container, int Slot), PlayerInventorySlotMutation> playerSlots = [];

    public void Request(RuntimeDomain domain, string kind, string? operationId = null, bool interactive = false)
    {
        if (domain == RuntimeDomain.None)
            return;

        lock (gate)
        {
            pendingDomains |= domain;
            AddNotice(domain, kind, operationId, interactive);
        }
    }

    public void Request(PlayerInventoryCacheChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            pendingDomains |= RuntimeDomain.PlayerInventory;
            AddNotice(RuntimeDomain.PlayerInventory, "player_inventory", null, false);

            // A baseline, owner transition, or uncertain sequence is repaired from
            // the authoritative cache once. Ordinary slot events retain the first
            // before-value and final after-value for each slot in the burst.
            if (change.IsBaseline || playerOwner is not null && !playerOwner.Matches(change.Owner))
            {
                playerRequiresFullRefresh = true;
                playerSlots.Clear();
            }

            playerOwner = change.Owner with { };
            playerObservedAtUtc = change.ObservedAtUtc > playerObservedAtUtc
                ? change.ObservedAtUtc
                : playerObservedAtUtc;
            if (playerRequiresFullRefresh)
                return;

            foreach (var mutation in change.Slots)
            {
                var key = (mutation.ContainerKey, mutation.SlotIndex);
                playerSlots[key] = playerSlots.TryGetValue(key, out var previous)
                    ? new(mutation.ContainerKey, mutation.SlotIndex, previous.Previous, mutation.Current)
                    : mutation;
            }
        }
    }

    private void AddNotice(RuntimeDomain domain, string kind, string? operationId, bool interactive)
    {
        var now = clock.GetTimestamp();
        var key = (domain, kind, operationId);
        var immediate = interactive || kind is not ("player_inventory" or "cache" or "state" or "retainer_listings" or "periodic");
        notices[key] = notices.TryGetValue(key, out var previous)
            ? previous with { Last = now, Count = previous.Count + 1, Immediate = previous.Immediate || immediate }
            : new(new(domain, kind, operationId), now, now, 1, immediate);
    }

    public bool HasPending(RuntimeDomain domains = RuntimeDomain.All)
    {
        lock (gate)
            return (pendingDomains & domains) != 0;
    }

    public RuntimeReconciliationBatch Drain(RuntimeDomain allowedDomains = RuntimeDomain.All, bool force = true)
    {
        lock (gate)
        {
            var domains = pendingDomains & allowedDomains;
            if (domains == RuntimeDomain.None)
                return RuntimeReconciliationBatch.Empty;

            var now = clock.GetTimestamp();
            if (!force)
            {
                var immediate = RuntimeDomain.Operations;
                var first = now;
                var last = long.MinValue;
                foreach (var pending in notices.Values)
                {
                    if ((pending.Notice.Domain & domains) == 0)
                        continue;
                    if (pending.Immediate)
                        immediate |= pending.Notice.Domain;
                    // Operation churn must not restart the background quiet period.
                    if ((pending.Notice.Domain & domains & ~RuntimeDomain.Operations) == 0)
                        continue;
                    first = Math.Min(first, pending.First);
                    last = Math.Max(last, pending.Last);
                }
                if (last != long.MinValue && clock.GetElapsedTime(first, now) < BackgroundMaximumDelay &&
                    clock.GetElapsedTime(last, now) < BackgroundQuietPeriod)
                    domains &= immediate;
                if (domains == RuntimeDomain.None)
                    return RuntimeReconciliationBatch.Empty;
            }

            pendingDomains &= ~domains;
            PlayerInventoryCacheChange? playerChange = null;
            if ((domains & RuntimeDomain.PlayerInventory) != 0)
            {
                if (!playerRequiresFullRefresh && playerOwner is not null)
                {
                    playerChange = new(
                        playerOwner with { },
                        playerObservedAtUtc,
                        false,
                        playerSlots.Values
                            .OrderBy(slot => slot.ContainerKey, StringComparer.Ordinal)
                            .ThenBy(slot => slot.SlotIndex)
                            .ToArray());
                }

                playerOwner = null;
                playerObservedAtUtc = default;
                playerRequiresFullRefresh = false;
                playerSlots.Clear();
            }

            var drainedNotices = new List<RuntimeChangeNotice>();
            var retainedNotices = new List<PendingNotice>();
            var requestCount = 0;
            var oldest = now;
            foreach (var pair in notices.ToArray())
            {
                var notice = pair.Value.Notice;
                var drainedDomain = notice.Domain & domains;
                if (drainedDomain == RuntimeDomain.None)
                    continue;

                notices.Remove(pair.Key);
                drainedNotices.Add(notice with { Domain = drainedDomain });
                requestCount += pair.Value.Count;
                oldest = Math.Min(oldest, pair.Value.First);
                var remainingDomain = notice.Domain & ~domains;
                if (remainingDomain != RuntimeDomain.None)
                {
                    var remaining = notice with { Domain = remainingDomain };
                    retainedNotices.Add(pair.Value with { Notice = remaining });
                }
            }

            // Merge only after removing drained keys: a split multi-domain notice
            // can otherwise collide with a key still in the enumeration snapshot.
            foreach (var pending in retainedNotices)
            {
                var notice = pending.Notice;
                var key = (notice.Domain, notice.Kind, notice.OperationId);
                var retained = pending;
                if (notices.TryGetValue(key, out var other))
                    retained = retained with { First = Math.Min(retained.First, other.First), Last = Math.Max(retained.Last, other.Last), Count = retained.Count + other.Count, Immediate = retained.Immediate || other.Immediate };
                notices[key] = retained;
            }

            return new(domains, playerChange, drainedNotices) { RequestCount = requestCount, Waited = clock.GetElapsedTime(oldest, now) };
        }
    }
}
