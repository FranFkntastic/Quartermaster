using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using RQ.Operations;

namespace RQ.Automation;

/// <summary>
/// Runs Franthropy's verified saddlebag retrieval on the framework thread. One call
/// dispatches at most one native command; after dispatch only observation continues.
/// </summary>
public sealed class SaddlebagLiveDriver(IFramework framework, DalamudInventoryMover mover) : ISaddlebagRetrievalDriver
{
    // Pre-dispatch waits (native validation, container load, busy native queue) are
    // bounded locally; nothing has been sent, so giving up is a plain refusal.
    private static readonly TimeSpan PreDispatchBudget = TimeSpan.FromSeconds(15);
    // The mover bounds acknowledgement and repair itself (about 40s worst case).
    // This outer bound only guarantees the run cannot wait forever.
    private static readonly TimeSpan PostDispatchBudget = TimeSpan.FromSeconds(90);

    public Task<SaddlebagInventoryRead> ReadAsync(CancellationToken cancellationToken) =>
        framework.RunOnTick(Read, cancellationToken: cancellationToken);

    private static SaddlebagInventoryRead Read()
    {
        var bags = DalamudInventoryStackScanner.ReadComplete(DalamudInventoryMover.Bags);
        if (!bags.IsComplete)
            return new(false, false, bags.Message, [], []);
        var standard = DalamudInventoryStackScanner.ReadComplete(DalamudInventoryMover.Saddlebags[..2]);
        if (!standard.IsComplete)
            return new(true, false, standard.Message, bags.Stacks, []);
        var premium = DalamudInventoryStackScanner.ReadComplete(DalamudInventoryMover.Saddlebags[2..]);
        return new(
            true,
            true,
            premium.IsComplete ? "Bags and saddlebag read." : "Bags and standard saddlebag read; premium saddlebag not loaded.",
            bags.Stacks,
            premium.IsComplete ? [.. standard.Stacks, .. premium.Stacks] : standard.Stacks);
    }

    public async Task<SaddlebagMoveResult> RetrieveAsync(
        DalamudInventoryStack source,
        int quantity,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        DateTime? dispatchedAt = null;
        var lastWaiting = InventoryMoveOutcome.None;
        var firstPoll = true;
        while (true)
        {
            var observation = await framework.RunOnTick(
                () =>
                {
                    if (dispatchedAt is not null)
                        return mover.Observe();
                    // Begin would return the observation of an older unresolved command;
                    // never credit that to this request.
                    if (mover.HasOutstanding)
                        return null;
                    return mover.BeginSaddlebagRetrieval(source, quantity, stillAuthorized);
                },
                delayTicks: firstPoll ? 0 : 1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            firstPoll = false;
            if (observation is null)
                return new(
                    InventoryMoveOutcome.Indeterminate,
                    "PriorMoveUnresolved",
                    "An earlier saddlebag or bag command is still unresolved in the inventory mover; nothing was dispatched.");
            if (observation.Dispatched || observation.Outcome == InventoryMoveOutcome.AwaitingReceipt)
                dispatchedAt ??= DateTime.UtcNow;
            if (!observation.Waiting)
                return new(
                    observation.Outcome,
                    observation.Outcome.ToString(),
                    observation.Message,
                    observation.SplitDestination,
                    observation.Destination);
            lastWaiting = observation.Outcome;
            if (dispatchedAt is null && DateTime.UtcNow - started > PreDispatchBudget)
                return new(
                    lastWaiting == InventoryMoveOutcome.None ? InventoryMoveOutcome.InventoryNotLoaded : lastWaiting,
                    $"{lastWaiting}TimedOut",
                    $"Still waiting before dispatch after {PreDispatchBudget.TotalSeconds:N0}s: {observation.Message} Nothing was dispatched.");
            if (dispatchedAt is { } sent && DateTime.UtcNow - sent > PostDispatchBudget)
                return new(
                    InventoryMoveOutcome.Indeterminate,
                    "ObservationTimedOut",
                    $"Dispatched command unresolved after {PostDispatchBudget.TotalSeconds:N0}s: {observation.Message} Not resent.");
        }
    }
}
