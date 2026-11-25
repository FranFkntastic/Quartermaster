using RQ.Domain;

namespace RQ.Planning;

/// <summary>A placement editor action on a reserve item's ranked homes.</summary>
public enum PlacementOperation
{
    MakeFirst,
    AddFallback,
    Remove,
    Toggle,
    SetCap,
    UseCurrentHolders,
    ClearDestinations,
}

/// <summary>Units one retainer of one character holds of an item, for <see cref="PlacementOperation.UseCurrentHolders"/>.</summary>
public sealed record PlacementHolding(OwnerScope Character, ulong RetainerId, int Units);

/// <summary>
/// The placement editor's rules for a reserve item's homes: one ranked chain across every character of the account
/// (rank = index + 1), each home an explicit retainer with an optional cap. Shared by Quartermaster's placement
/// surfaces and the placement IPC so they cannot diverge. Every operation returns a new list; none ever lists a
/// retainer id of 0 (there is no "any space" home) or the same retainer twice.
/// </summary>
public static class ReservePlacement
{
    /// <summary>The same character for placement (by content id when both carry one, else by name and world).</summary>
    public static bool SameCharacter(OwnerScope left, OwnerScope right) => PlacementHome.SameCharacter(left, right);

    /// <summary>1-based global rank of the home, or 0 when not listed.</summary>
    public static int Rank(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId)
    {
        for (var index = 0; index < homes.Count; index++)
            if (homes[index].SameHome(character, retainerId))
                return index + 1;
        return 0;
    }

    /// <summary>Whether the operation targets one home.</summary>
    public static bool NeedsDestination(PlacementOperation operation) =>
        operation is PlacementOperation.MakeFirst or PlacementOperation.AddFallback or PlacementOperation.Remove or
            PlacementOperation.Toggle or PlacementOperation.SetCap;

    /// <summary>
    /// Applies one operation. <paramref name="character"/> and <paramref name="retainerId"/> name the home for the
    /// single-home operations; <paramref name="maxUnits"/> is the cap for SetCap (null clears it); <paramref name="holdings"/>
    /// is what each observed retainer holds, for UseCurrentHolders.
    /// </summary>
    public static List<PlacementHome> Operate(
        PlacementOperation operation,
        IReadOnlyList<PlacementHome> homes,
        OwnerScope? character,
        ulong retainerId,
        int? maxUnits = null,
        IEnumerable<PlacementHolding>? holdings = null)
    {
        if (NeedsDestination(operation) && (character is null || retainerId == 0))
            throw new ArgumentException("A single-home placement operation needs a character and a retainer.", nameof(retainerId));
        return operation switch
        {
            PlacementOperation.MakeFirst => MakeFirst(homes, character!, retainerId),
            PlacementOperation.AddFallback => AddFallback(homes, character!, retainerId),
            PlacementOperation.Remove => Remove(homes, character!, retainerId),
            PlacementOperation.Toggle => Toggle(homes, character!, retainerId),
            PlacementOperation.SetCap => SetCap(homes, character!, retainerId, maxUnits),
            PlacementOperation.UseCurrentHolders => UseCurrentHolders(homes, holdings ?? []),
            PlacementOperation.ClearDestinations => [],
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    /// <summary>Puts the home first, keeping its cap when it was already listed.</summary>
    public static List<PlacementHome> MakeFirst(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId)
    {
        var next = Copy(homes);
        var index = next.FindIndex(home => home.SameHome(character, retainerId));
        var moved = index >= 0 ? next[index] : new PlacementHome(character with { }, retainerId);
        if (index >= 0)
            next.RemoveAt(index);
        next.Insert(0, moved);
        return next;
    }

    /// <summary>Appends the home uncapped; one already listed keeps its rank and cap.</summary>
    public static List<PlacementHome> AddFallback(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId)
    {
        var next = Copy(homes);
        if (!next.Any(home => home.SameHome(character, retainerId)))
            next.Add(new(character with { }, retainerId));
        return next;
    }

    /// <summary>Removes the home; the ranks after it close up.</summary>
    public static List<PlacementHome> Remove(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId)
    {
        var next = Copy(homes);
        next.RemoveAll(home => home.SameHome(character, retainerId));
        return next;
    }

    public static List<PlacementHome> Toggle(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId) =>
        homes.Any(home => home.SameHome(character, retainerId))
            ? Remove(homes, character, retainerId)
            : AddFallback(homes, character, retainerId);

    /// <summary>
    /// Sets the home's cap (a home not listed yet is appended with it), or clears it when <paramref name="maxUnits"/>
    /// is null (nothing to do for a home not listed). A negative cap is refused.
    /// </summary>
    public static List<PlacementHome> SetCap(IReadOnlyList<PlacementHome> homes, OwnerScope character, ulong retainerId, int? maxUnits)
    {
        if (maxUnits is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxUnits), maxUnits, "A cap cannot be negative.");
        var next = Copy(homes);
        var index = next.FindIndex(home => home.SameHome(character, retainerId));
        if (index >= 0)
            next[index] = next[index] with { MaxUnits = maxUnits };
        else if (maxUnits is not null)
            next.Add(new(character with { }, retainerId, maxUnits));
        return next;
    }

    /// <summary>
    /// The retainers observed holding the item, across every shown character and most units first, ahead of the
    /// homes already listed. A holder already listed keeps its cap. Unchanged when no retainer holds any.
    /// </summary>
    public static List<PlacementHome> UseCurrentHolders(IReadOnlyList<PlacementHome> homes, IEnumerable<PlacementHolding> holdings)
    {
        var holders = holdings
            .Where(holding => holding.RetainerId != 0 && holding.Units > 0)
            .GroupBy(holding => (Content: holding.Character.LocalContentId ?? 0, Name: holding.Character.CharacterName, holding.RetainerId))
            .Select(group => group.First() with { Units = group.Max(holding => holding.Units) })
            .OrderByDescending(holding => holding.Units)
            .ThenBy(holding => holding.RetainerId)
            .ToArray();
        if (holders.Length == 0)
            return Copy(homes);
        var next = new List<PlacementHome>();
        foreach (var holder in holders)
        {
            if (next.Any(home => home.SameHome(holder.Character, holder.RetainerId)))
                continue;
            next.Add(homes.FirstOrDefault(home => home.SameHome(holder.Character, holder.RetainerId))?.Copy()
                     ?? new PlacementHome(holder.Character with { }, holder.RetainerId));
        }
        next.AddRange(homes.Where(home => !next.Any(listed => listed.SameHome(home.Character, home.RetainerId))).Select(home => home.Copy()));
        return next;
    }

    /// <summary>Same homes, same order, same caps.</summary>
    public static bool Same(IReadOnlyList<PlacementHome>? left, IReadOnlyList<PlacementHome>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].SameHome(right[index].Character, right[index].RetainerId) || left[index].MaxUnits != right[index].MaxUnits)
                return false;
        }
        return true;
    }

    /// <summary>A deep copy without retainer id 0 or repeated homes (the first of a repeat keeps its rank).</summary>
    public static List<PlacementHome> Copy(IEnumerable<PlacementHome>? homes)
    {
        var next = new List<PlacementHome>();
        foreach (var home in homes ?? [])
            if (home.RetainerId != 0 && !next.Any(listed => listed.SameHome(home.Character, home.RetainerId)))
                next.Add(home.Copy());
        return next;
    }
}
