using RQ.Domain;

namespace RQ.Planning;

/// <summary>A placement editor action, applied to the destinations one character shows for an item.</summary>
public enum PlacementOperation
{
    MakeFirst,
    AddFallback,
    Remove,
    Toggle,
    UseCurrentHolders,
    ClearDestinations,
}

/// <summary>
/// A reserve item's homes after an edit on one shown character. <see cref="RemoteMovedFrom"/> names the character
/// whose remote homes the edit replaced; <see cref="RemoteKeptOn"/> names the character whose remote homes a clear
/// shown elsewhere left in place.
/// </summary>
public sealed record PlacementResolution(
    StowageRoutingPolicy Local,
    ReserveRemoteHomes? Remote,
    bool LocalChanged,
    bool RemoteChanged,
    OwnerScope? RemoteMovedFrom,
    OwnerScope? RemoteKeptOn)
{
    public bool Changed => LocalChanged || RemoteChanged;
}

/// <summary>
/// The placement editor's rules for one reserve item, shared by Quartermaster's Placement window and the
/// placement IPC so the two cannot diverge. The editor shows one character at a time: the owner's own ranked homes
/// (then Any space), or the item's remote homes when they are on the shown character. Another character offers no
/// Any space, and an item has remote homes on one other character at most: editing it on a second character moves
/// them there, while clearing it on a character it has no homes on leaves them where they are.
/// </summary>
public static class ReservePlacement
{
    /// <summary>The destinations the editor shows for the item on <paramref name="shown"/>.</summary>
    public static StowageRoutingPolicy Shown(OwnerScope owner, OwnerScope shown, StowageRoutingPolicy? local, ReserveRemoteHomes? remote) =>
        shown.Matches(owner)
            ? StowageRoutingPolicy.CopyOf(local)
            : remote is { RetainerIds.Count: > 0 } && remote.Character.Matches(shown)
                ? new StowageRoutingPolicy { PreferredRetainerIds = remote.RetainerIds.Distinct().ToList() }
                : new StowageRoutingPolicy();

    /// <summary>
    /// The editor action on the shown destinations. <paramref name="destination"/> is a retainer id or
    /// <see cref="PlacementEdits.AnySpace"/>; <paramref name="holdings"/> is units per retainer of the shown character.
    /// </summary>
    public static StowageRoutingPolicy Operate(
        PlacementOperation operation,
        StowageRoutingPolicy shown,
        ulong destination,
        IReadOnlyDictionary<ulong, int> holdings) => operation switch
        {
            PlacementOperation.MakeFirst => PlacementEdits.MakeFirst(shown, destination),
            PlacementOperation.AddFallback => PlacementEdits.AddFallback(shown, destination),
            PlacementOperation.Remove => PlacementEdits.Remove(shown, destination),
            PlacementOperation.Toggle => PlacementEdits.Toggle(shown, destination),
            PlacementOperation.UseCurrentHolders => PlacementEdits.WhereItLives(shown, holdings),
            PlacementOperation.ClearDestinations => PlacementEdits.Clear(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    /// <summary>Whether the operation targets one destination.</summary>
    public static bool NeedsDestination(PlacementOperation operation) =>
        operation is PlacementOperation.MakeFirst or PlacementOperation.AddFallback or PlacementOperation.Remove or PlacementOperation.Toggle;

    /// <summary>The item's next local and remote homes once <paramref name="edited"/> replaces what <paramref name="shown"/> showed.</summary>
    public static PlacementResolution Resolve(
        OwnerScope owner,
        OwnerScope shown,
        StowageRoutingPolicy? local,
        ReserveRemoteHomes? remote,
        StowageRoutingPolicy edited)
    {
        var storedLocal = StowageRoutingPolicy.CopyOf(local);
        var storedRemote = ReserveRemoteHomes.CopyOf(remote);
        if (shown.Matches(owner))
        {
            var nextLocal = StowageRoutingPolicy.CopyOf(edited);
            return new(nextLocal, storedRemote, !nextLocal.SameAs(local ?? new StowageRoutingPolicy()), false, null, null);
        }

        // Another character offers no Any space: its homes are its ranked retainers only.
        var ids = edited.PreferredRetainerIds.Where(id => id != PlacementEdits.AnySpace).Distinct().ToList();
        var nextRemote = ids.Count == 0 ? null : new ReserveRemoteHomes { Character = shown with { }, RetainerIds = ids };
        // Clearing an item shown here leaves the homes it has on another character alone.
        if (nextRemote is null && storedRemote is not null && !storedRemote.Character.Matches(shown))
            return new(storedLocal, storedRemote, false, false, null, storedRemote.Character);
        var movedFrom = nextRemote is not null && storedRemote is not null && !storedRemote.Character.Matches(shown)
            ? storedRemote.Character
            : null;
        return new(storedLocal, nextRemote, false, !ReserveRemoteHomes.Same(storedRemote, nextRemote), movedFrom, null);
    }
}
