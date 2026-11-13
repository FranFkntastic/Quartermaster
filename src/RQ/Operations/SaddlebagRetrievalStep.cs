using Franthropy.Dalamud.Automation.Inventory;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

/// <summary>
/// One complete live read of the player bags and saddlebag. <paramref name="SaddlebagLoaded"/>
/// is false when the client has not loaded the saddlebag this session.
/// </summary>
public sealed record SaddlebagInventoryRead(
    bool BagsComplete,
    bool SaddlebagLoaded,
    string Message,
    IReadOnlyList<DalamudInventoryStack> Bags,
    IReadOnlyList<DalamudInventoryStack> Saddlebag);

/// <summary>
/// Terminal result of one saddlebag retrieval command. <paramref name="Code"/> is the
/// Franthropy outcome name, or a Quartermaster code when the command was bounded locally.
/// </summary>
public sealed record SaddlebagMoveResult(
    InventoryMoveOutcome Outcome,
    string Code,
    string Message,
    DalamudInventoryStack? StagedPiece = null,
    DalamudInventoryStack? Destination = null);

/// <summary>
/// Terminal result of one saddlebag stack attempt, reported to the caller. <paramref name="MayHaveMoved"/>
/// is true when items may have left the source slot without a proven arrival.
/// </summary>
public sealed record SaddlebagAttemptReport(
    uint ItemId,
    string ItemName,
    DalamudInventoryStack Source,
    int Quantity,
    InventoryMoveOutcome Outcome,
    string Code,
    string Message,
    bool MayHaveMoved);

public interface ISaddlebagRetrievalDriver
{
    Task<SaddlebagInventoryRead> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches at most one native command for <paramref name="source"/> and observes it to a
    /// terminal outcome. Never resends.
    /// </summary>
    Task<SaddlebagMoveResult> RetrieveAsync(
        DalamudInventoryStack source,
        int quantity,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken);
}

/// <summary>
/// The saddlebag leg of a retrieval operation. It runs before any retainer is opened and
/// moves only the shortfall the saddlebag can cover. Uncertain outcomes stop the leg
/// without a resend; the caller re-plans from the live bag delta this step reports.
/// </summary>
internal sealed class SaddlebagRetrievalStep(
    ISaddlebagRetrievalDriver driver,
    OperationJournal journal,
    Func<IReadOnlyDictionary<uint, int>>? cachedSaddlebag,
    Action<string>? warning,
    Action<string>? information,
    Action<SaddlebagAttemptReport>? attempted = null)
{
    private static readonly IReadOnlySet<InventoryMoveOutcome> StopsLeg = new HashSet<InventoryMoveOutcome>
    {
        InventoryMoveOutcome.UnresearchedExecutable,
        InventoryMoveOutcome.ValidatingNative,
        InventoryMoveOutcome.InventoryNotLoaded,
        InventoryMoveOutcome.NativeQueueBusy,
        InventoryMoveOutcome.SaddlebagNotLoaded,
        InventoryMoveOutcome.MovementNotPermitted,
        InventoryMoveOutcome.NotAuthorized,
        InventoryMoveOutcome.Indeterminate,
    };

    /// <param name="Credit">
    /// Observed bag increase per plan line (item + quality policy), measured only over the
    /// variants that line may draw, from fresh complete reads around that line's moves.
    /// </param>
    /// <param name="Unreconciled">
    /// Set when a move may have happened but the bags could not be reread completely. The
    /// caller must stop the operation instead of planning retainers from the pre-leg count.
    /// </param>
    public sealed record LegResult(
        IReadOnlyDictionary<(uint ItemId, ItemQualityPolicy Quality), int> Credit,
        int VerifiedRetrieved,
        bool Dispatched,
        string? Unreconciled = null);

    /// <summary>
    /// Retrieves saddlebag stock for every plan line still short. Returns the observed
    /// per-line bag increase and the quantity proven by retrieval receipts.
    /// </summary>
    public async Task<LegResult> ExecuteAsync(
        string operationId,
        RetrievalPlan plan,
        Func<bool> stillAuthorized,
        CancellationToken token)
    {
        var credit = new Dictionary<(uint ItemId, ItemQualityPolicy Quality), int>();
        var verified = 0;
        var dispatched = false;
        var shortLines = plan.Lines.Where(line => line.NeededQuantity > 0).ToArray();
        if (shortLines.Length == 0)
            return new(credit, 0, false);

        var read = await driver.ReadAsync(token).ConfigureAwait(false);
        if (!read.BagsComplete || !read.SaddlebagLoaded)
        {
            var cached = cachedSaddlebag?.Invoke() ?? new Dictionary<uint, int>();
            var code = read.BagsComplete ? nameof(InventoryMoveOutcome.SaddlebagNotLoaded) : nameof(InventoryMoveOutcome.InventoryNotLoaded);
            foreach (var line in shortLines.Where(line => cached.GetValueOrDefault(line.ItemId) > 0))
            {
                Report(operationId, "SaddlebagRetrievalRefused", code, line.ItemName, line.ItemId, line.Quality,
                    Math.Min(line.NeededQuantity, cached[line.ItemId]),
                    $"Cached saddlebag stock {cached[line.ItemId]:N0} was not retrieved; retainers cover the full shortfall {line.NeededQuantity:N0}. {read.Message}");
            }
            return new(credit, 0, false);
        }

        foreach (var line in shortLines)
        {
            token.ThrowIfCancellationRequested();
            var key = (line.ItemId, line.Quality);
            var need = Math.Max(0, line.NeededQuantity - credit.GetValueOrDefault(key));
            if (need == 0)
                continue;
            var before = read;
            var sources = SelectSources(before.Saddlebag, line.ItemId, line.Quality, need);
            if (sources.Count == 0)
                continue;

            var stop = false;
            var mayHaveMoved = false;
            foreach (var (stack, quantity) in sources)
            {
                // No new stack starts once the caller's authority is gone (run cancelled, plan
                // changed, lease lost). Moves already dispatched were observed to an outcome above.
                if (!stillAuthorized())
                {
                    Report(operationId, "SaddlebagRetrievalStopped", nameof(InventoryMoveOutcome.NotAuthorized), line.ItemName, line.ItemId, line.Quality, quantity,
                        "caller authority ended (run cancelled, plan changed or lease lost); no further saddlebag stack is started.");
                    attempted?.Invoke(new(line.ItemId, line.ItemName, stack, quantity, InventoryMoveOutcome.NotAuthorized,
                        nameof(InventoryMoveOutcome.NotAuthorized), "Caller authority ended before this stack; nothing dispatched.", false));
                    stop = true;
                    break;
                }
                dispatched = true;
                var result = await RetrieveOneAsync(operationId, line, stack, quantity, stillAuthorized, token).ConfigureAwait(false);
                verified += result.Retrieved;
                mayHaveMoved |= result.MayHaveMoved;
                if (result.Stop is not null)
                {
                    stop = result.Stop == StopScope.Leg;
                    break;
                }
            }

            // Re-plan only from a fresh complete read, whatever the receipts say.
            read = await driver.ReadAsync(token).ConfigureAwait(false);
            if (!read.BagsComplete)
            {
                if (!mayHaveMoved)
                    break; // Nothing could have moved for this line; earlier lines are already reconciled.
                // A move may have landed, but its effect is unknown: planning retainers from the
                // pre-leg count could exceed the target. Stop the operation; never resend.
                var message =
                    $"SaddlebagReconciliationIncomplete: item={line.ItemName} ({line.ItemId} {line.Quality}) shortfall={line.NeededQuantity:N0} " +
                    $"receiptsVerified={verified:N0}; the bags could not be reread completely after a saddlebag move, so retainer retrieval is not planned. {read.Message}";
                warning?.Invoke(message);
                journal.RecordWarning(operationId, "SaddlebagReconciliationIncomplete", message);
                return new(credit, verified, true, message);
            }
            var gained = CountVariant(read.Bags, line.ItemId, line.Quality) - CountVariant(before.Bags, line.ItemId, line.Quality);
            if (gained > 0)
                credit[key] = credit.GetValueOrDefault(key) + Math.Min(gained, need);
            if (stop || !read.SaddlebagLoaded)
                break;
        }
        return new(credit, verified, dispatched);
    }

    private enum StopScope { Line, Leg }

    private readonly record struct OneResult(int Retrieved, StopScope? Stop, bool MayHaveMoved = true);

    // Outcomes after which items may have left the source slot.
    private static bool MayHaveMoved(InventoryMoveOutcome outcome) =>
        outcome is InventoryMoveOutcome.Retrieved or InventoryMoveOutcome.Staged or
            InventoryMoveOutcome.Indeterminate or InventoryMoveOutcome.OutsideMutation;

    private async Task<OneResult> RetrieveOneAsync(
        string operationId,
        PlanLine line,
        DalamudInventoryStack stack,
        int quantity,
        Func<bool> stillAuthorized,
        CancellationToken token)
    {
        var first = await driver.RetrieveAsync(stack, quantity, stillAuthorized, token).ConfigureAwait(false);
        if (first.Outcome == InventoryMoveOutcome.Retrieved)
        {
            Attempted(line, stack, quantity, first, mayHaveMoved: false);
            return new(Complete(operationId, line, stack, quantity, first), null);
        }
        if (first.Outcome != InventoryMoveOutcome.Staged)
        {
            Attempted(line, stack, quantity, first, MayHaveMoved(first.Outcome));
            return new(0, Fail(operationId, line, stack, quantity, first, staged: false), MayHaveMoved(first.Outcome));
        }

        information?.Invoke(Describe("SaddlebagRetrievalStaged", first.Code, line, stack, quantity,
            $"split to {Slot(first.StagedPiece)}; retrieving that piece once."));
        if (first.StagedPiece is not { } piece || piece.Quantity != quantity)
        {
            var unexpected = first with
            {
                Outcome = InventoryMoveOutcome.Indeterminate,
                Code = "StagedPieceUnexpected",
                Message = $"Staged piece {Slot(first.StagedPiece)} x{first.StagedPiece?.Quantity ?? 0} does not match requested {quantity:N0}. {first.Message}",
            };
            Attempted(line, stack, quantity, unexpected, mayHaveMoved: true);
            return new(0, Fail(operationId, line, stack, quantity, unexpected, staged: true));
        }

        // Exactly one follow-up: the staged piece as a whole stack.
        var second = await driver.RetrieveAsync(piece, piece.Quantity, stillAuthorized, token).ConfigureAwait(false);
        if (second.Outcome == InventoryMoveOutcome.Retrieved)
        {
            Attempted(line, piece, piece.Quantity, second, mayHaveMoved: false);
            return new(Complete(operationId, line, piece, piece.Quantity, second), null);
        }
        if (second.Outcome == InventoryMoveOutcome.Staged)
            second = second with
            {
                Outcome = InventoryMoveOutcome.Indeterminate,
                Code = "StagedTwice",
                Message = $"A whole-stack retrieval of the staged piece staged again. {second.Message}",
            };
        Attempted(line, piece, piece.Quantity, second, MayHaveMoved(second.Outcome));
        return new(0, Fail(operationId, line, piece, piece.Quantity, second, staged: true));
    }

    private void Attempted(PlanLine line, DalamudInventoryStack source, int quantity, SaddlebagMoveResult result, bool mayHaveMoved) =>
        attempted?.Invoke(new(line.ItemId, line.ItemName, source, quantity, result.Outcome, result.Code, result.Message, mayHaveMoved));

    private int Complete(string operationId, PlanLine line, DalamudInventoryStack source, int quantity, SaddlebagMoveResult result)
    {
        var message = Describe("SaddlebagRetrievalCompleted", result.Code, line, source, quantity,
            $"arrived in {Slot(result.Destination)}.");
        information?.Invoke(message);
        journal.RecordSaddlebagRetrieval(operationId, line.ItemId, source.IsHighQuality, quantity, "SaddlebagRetrievalCompleted", message);
        return quantity;
    }

    private StopScope Fail(string operationId, PlanLine line, DalamudInventoryStack source, int quantity, SaddlebagMoveResult result, bool staged)
    {
        var (eventName, consequence) = result.Outcome switch
        {
            InventoryMoveOutcome.Indeterminate => ("SaddlebagRetrievalIndeterminate",
                "not resent; saddlebag leg stopped; retainers plan from a fresh bag read."),
            InventoryMoveOutcome.OutsideMutation => ("SaddlebagRetrievalOutsideMutation",
                "not resent; this item's saddlebag work stopped; retainers plan from a fresh bag read."),
            InventoryMoveOutcome.NotEnqueued => ("SaddlebagRetrievalNotEnqueued",
                "nothing moved; this item's saddlebag work stopped; retainers cover the rest."),
            _ => ("SaddlebagRetrievalRefused",
                "nothing dispatched; retainers cover the rest."),
        };
        if (staged)
            consequence = $"after staging {quantity:N0} inside the saddlebag; the staged piece stays there; {consequence}";
        var message = Describe(eventName, result.Code, line, source, quantity, consequence + " " + result.Message);
        warning?.Invoke(message);
        journal.RecordWarning(operationId, eventName, message);
        return StopsLeg.Contains(result.Outcome) ? StopScope.Leg : StopScope.Line;
    }

    /// <summary>
    /// Whole stacks that fit the shortfall first (largest first), then one partial from the
    /// smallest stack that covers the remainder, so at most one native split is needed.
    /// </summary>
    internal static IReadOnlyList<(DalamudInventoryStack Stack, int Quantity)> SelectSources(
        IReadOnlyList<DalamudInventoryStack> saddlebag,
        uint itemId,
        ItemQualityPolicy quality,
        int need)
    {
        var candidates = saddlebag
            .Where(stack => stack.ItemId == itemId && stack.IsOrdinary && QualityMatches(quality, stack.IsHighQuality))
            .OrderByDescending(stack => stack.Quantity)
            .ThenBy(stack => stack.Container)
            .ThenBy(stack => stack.SlotIndex)
            .ToList();
        var selected = new List<(DalamudInventoryStack, int)>();
        foreach (var stack in candidates.ToArray())
        {
            if (need == 0)
                break;
            if (stack.Quantity > need)
                continue;
            selected.Add((stack, stack.Quantity));
            candidates.Remove(stack);
            need -= stack.Quantity;
        }
        if (need > 0 && candidates.Where(stack => stack.Quantity > need).MinBy(stack => stack.Quantity) is { } partial)
            selected.Add((partial, need));
        return selected;
    }

    private static int CountVariant(IReadOnlyList<DalamudInventoryStack> stacks, uint itemId, ItemQualityPolicy quality) =>
        stacks.Where(stack => stack.ItemId == itemId && QualityMatches(quality, stack.IsHighQuality)).Sum(stack => stack.Quantity);

    private static bool QualityMatches(ItemQualityPolicy policy, bool isHighQuality) => policy switch
    {
        ItemQualityPolicy.NqOnly => !isHighQuality,
        ItemQualityPolicy.HqOnly => isHighQuality,
        _ => true,
    };

    private static string Slot(DalamudInventoryStack? stack) =>
        stack is null ? "unknown slot" : $"{stack.Container}/{stack.SlotIndex}";

    private static string Describe(string eventName, string code, PlanLine line, DalamudInventoryStack source, int quantity, string detail) =>
        $"{eventName}: {code} item={line.ItemName} ({line.ItemId} {(source.IsHighQuality ? "HQ" : "NQ")}) quantity={quantity:N0} " +
        $"shortfall={line.NeededQuantity:N0} stack={source.Container}/{source.SlotIndex} x{source.Quantity:N0}; {detail}";

    private void Report(string operationId, string eventName, string code, string itemName, uint itemId, ItemQualityPolicy quality, int quantity, string detail)
    {
        var message = $"{eventName}: {code} item={itemName} ({itemId} {quality}) quantity={quantity:N0} stack=unread; {detail}";
        warning?.Invoke(message);
        journal.RecordWarning(operationId, eventName, message);
    }
}
