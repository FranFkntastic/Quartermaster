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

public sealed record RuntimeReconciliationBatch(
    RuntimeDomain Domains,
    PlayerInventoryCacheChange? PlayerInventoryChange,
    IReadOnlyList<RuntimeChangeNotice> Notices)
{
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
    private readonly object gate = new();
    private RuntimeDomain pendingDomains;
    private readonly Dictionary<(RuntimeDomain Domain, string Kind, string? OperationId), RuntimeChangeNotice> notices = [];
    private OwnerScope? playerOwner;
    private DateTime playerObservedAtUtc;
    private bool playerRequiresFullRefresh;
    private readonly Dictionary<(string Container, int Slot), PlayerInventorySlotMutation> playerSlots = [];

    public void Request(RuntimeDomain domain, string kind, string? operationId = null)
    {
        if (domain == RuntimeDomain.None)
            return;

        lock (gate)
        {
            pendingDomains |= domain;
            var notice = new RuntimeChangeNotice(domain, kind, operationId);
            notices[(domain, kind, operationId)] = notice;
        }
    }

    public void Request(PlayerInventoryCacheChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            pendingDomains |= RuntimeDomain.PlayerInventory;
            notices[(RuntimeDomain.PlayerInventory, "player_inventory", null)] =
                new(RuntimeDomain.PlayerInventory, "player_inventory", null);

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

    public RuntimeReconciliationBatch Drain(RuntimeDomain allowedDomains = RuntimeDomain.All)
    {
        lock (gate)
        {
            var domains = pendingDomains & allowedDomains;
            if (domains == RuntimeDomain.None)
                return RuntimeReconciliationBatch.Empty;

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
            foreach (var pair in notices.ToArray())
            {
                var drainedDomain = pair.Value.Domain & domains;
                if (drainedDomain == RuntimeDomain.None)
                    continue;

                notices.Remove(pair.Key);
                drainedNotices.Add(pair.Value with { Domain = drainedDomain });
                var remainingDomain = pair.Value.Domain & ~domains;
                if (remainingDomain != RuntimeDomain.None)
                {
                    var remaining = pair.Value with { Domain = remainingDomain };
                    notices[(remainingDomain, remaining.Kind, remaining.OperationId)] = remaining;
                }
            }

            return new(domains, playerChange, drainedNotices);
        }
    }
}
