using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Automation.Transactions;
using RQ.Domain;
using RQ.Automation;
using RQ.Inventory;
using RQ.Planning;

namespace RQ.Operations;

public sealed record RetainerRouteCandidate(ulong RetainerId, string RetainerName, DateTime ObservedAtUtc);
public sealed record RetrievalResult(
    bool Success,
    int Transferred,
    string Code,
    string Message,
    bool MovementMayHaveOccurred = false);
public sealed record TransferExecutionResult(bool Started, string Message);

public interface IRetainerTransferDriver
{
    Task<MarketPlayerInventory> ReadPlacementBagsAsync(CancellationToken token) =>
        throw new NotSupportedException("Live player bag evidence is unavailable.");
    Task RequireRetainerListAsync(CancellationToken cancellationToken);
    Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken);
    Task OpenInventoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken);
    Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken);
    /// <summary>
    /// Supplies the affected variant's total from the route scan that selected this
    /// stack. Drivers can use it to reconcile a reordered source without rescanning
    /// the retainer before every transfer.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken) =>
        RetrieveAsync(stack, quantity, cancellationToken);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
    Task<RetainerDepositResult> DepositAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) =>
        Task.FromResult(new RetainerDepositResult(false, 0, "Unsupported", "Ordinary item deposits are not supported by this driver."));
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken);
    Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken);
    Task CloseRetainerAsync(CancellationToken cancellationToken);
    Task CloseRetainerListAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    void CancelActive();
}

public sealed partial class TransferCoordinator : IRetrievalOperationExecutor
{
    private readonly OperationJournal journal;
    private readonly Action<string>? warning;
    private readonly IRetainerTransferDriver driver;
    private readonly RetainerCacheRepository cache;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<IReadOnlyDictionary<uint, int>> playerInventory;
    private readonly AutomationLease automation;
    private readonly Func<DateTime> utcNow;
    private readonly RetainerStockMutationPersistence stockMutations;
    private readonly SaddlebagRetrievalStep? saddlebag;
    // The borrowing caller's mutation authority (run cancel, plan change), checked before every saddlebag dispatch.
    private readonly Func<bool>? callerAuthority;
    private readonly Func<ulong, bool>? retrievalSource;
    private readonly AutoRetainerSuppression? autoRetainerSuppression;
    private readonly TimeSpan autoRetainerWait;
    private readonly TimeSpan autoRetainerPoll;
    private readonly object activeGate = new();
    private readonly SemaphoreSlim coordinationGate = new(1, 1);
    private readonly AsyncLocal<bool> coordinationAmbient = new();
    private CancellationTokenSource? activeCancellation;
    private int running;

    public TransferCoordinator(
        OperationJournal journal,
        IRetainerTransferDriver driver,
        RetainerCacheRepository cache,
        Func<OwnerScope> currentOwner,
        Func<IReadOnlyDictionary<uint, int>> playerInventory,
        AutomationLease? automation = null,
        Func<DateTime>? utcNow = null,
        AutoRetainerSuppression? autoRetainerSuppression = null,
        TimeSpan? autoRetainerWait = null,
        TimeSpan? autoRetainerPoll = null,
        Action<string>? warning = null,
        ISaddlebagRetrievalDriver? saddlebagDriver = null,
        Func<IReadOnlyDictionary<uint, int>>? cachedSaddlebag = null,
        Action<string>? information = null,
        Func<bool>? callerAuthority = null,
        Action<SaddlebagAttemptReport>? saddlebagAttempt = null,
        Func<ulong, bool>? retrievalSource = null)
    {
        this.retrievalSource = retrievalSource;
        this.callerAuthority = callerAuthority;
        this.journal = journal;
        this.warning = warning;
        this.driver = driver;
        this.cache = cache;
        this.currentOwner = currentOwner;
        this.playerInventory = playerInventory;
        this.automation = automation ?? new AutomationLease();
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.autoRetainerSuppression = autoRetainerSuppression;
        this.autoRetainerWait = autoRetainerWait ?? TimeSpan.FromSeconds(30);
        this.autoRetainerPoll = autoRetainerPoll ?? TimeSpan.FromMilliseconds(250);
        stockMutations = new(journal, cache, RecoverStockObservationAsync);
        saddlebag = saddlebagDriver is null
            ? null
            : new SaddlebagRetrievalStep(saddlebagDriver, journal, cachedSaddlebag, warning, information, saddlebagAttempt);
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public bool CanStart => automation.CanAcquire && !IsRunning;

    public Task<TransferExecutionResult> ExecutePlanAsync(
        string? retrievalOperationId,
        string? depositOperationId,
        CancellationToken cancellationToken = default) =>
        WithAutoRetainerCoordinationAsync(
            () => ExecutePlanCoreAsync(retrievalOperationId, depositOperationId, cancellationToken),
            cancellationToken);

    private async Task<TransferExecutionResult> ExecutePlanCoreAsync(
        string? retrievalOperationId,
        string? depositOperationId,
        CancellationToken cancellationToken)
    {
        if (retrievalOperationId is null && depositOperationId is null)
            return new(false, "The transfer plan has no movement to execute.");

        if (retrievalOperationId is not null)
        {
            var retrieval = await ExecuteRetrievalAsync(retrievalOperationId, cancellationToken).ConfigureAwait(false);
            if (!retrieval.Started)
            {
                CancelAccepted(depositOperationId, "The deposit actions were cancelled because the transfer plan could not start.");
                return retrieval;
            }
            if (journal.Get(retrievalOperationId)?.Status != OperationStatuses.Succeeded)
            {
                CancelAccepted(depositOperationId, "The deposit actions were cancelled because retrieval did not complete.");
                return new(true, $"{retrieval.Message} Deposit actions were not started.");
            }
        }

        if (depositOperationId is null)
            return new(true, "Transfer plan completed.");

        var deposit = await ExecuteDepositAsync(depositOperationId, cancellationToken).ConfigureAwait(false);
        return deposit.Started
            ? new(true, deposit.Message)
            : deposit;
    }

    /// <summary>
    /// Executes a persisted, reviewed retrieval operation retainer by retainer.
    /// Every successful movement is durably reconciled before the next stack begins.
    /// </summary>
    public Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default) =>
        WithAutoRetainerCoordinationAsync(
            () => ExecuteRetrievalCoreAsync(operationId, cancellationToken),
            cancellationToken);

    internal Task<TransferExecutionResult> ExecuteRetrievalUnderLeaseAsync(
        string operationId, IDisposable lease, CancellationToken cancellationToken) =>
        ExecuteRetrievalUnderLeaseAsync(operationId, lease, cancellationToken, null);

    private Task<TransferExecutionResult> ExecuteRetrievalUnderLeaseAsync(
        string operationId, IDisposable lease, CancellationToken cancellationToken, PlacementLeg? placement)
    {
        if (!automation.Owns(lease))
            throw new InvalidOperationException("The caller does not own the automation lease.");
        return ExecuteRetrievalCoreAsync(operationId, cancellationToken, lease, placement);
    }

    private async Task<TransferExecutionResult> ExecuteRetrievalCoreAsync(string operationId, CancellationToken cancellationToken, IDisposable? borrowedLease = null, PlacementLeg? placement = null)
    {
        IDisposable? lease = null;
        if (borrowedLease is null && !automation.TryAcquire("retainer transfer", out lease))
            return new(false, automation.AcquisitionBlockReason ?? "Automation is unavailable.");
        using (lease)
        {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(false, "Another retainer transfer is already running.");
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(linkedCancellation);
        var token = linkedCancellation.Token;
        var retainerOpen = false;
        var retainerSessionOpen = false;
        var movementAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var operation = journal.Get(operationId) ?? throw new KeyNotFoundException($"Operation '{operationId}' was not found.");
            if (operation.Kind != OperationKinds.Retrieval)
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Kind}', not retrieval.");
            if (operation.Status != OperationStatuses.Accepted)
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Status}', not accepted.");
            var owner = currentOwner();
            if (!operation.Owner.HasStableIdentity || !owner.HasStableIdentity || !operation.Owner.Matches(owner))
                throw new InvalidOperationException("Operation owner no longer matches current character.");
            // A requester may limit a line to named retainers (and the saddlebag only when allowed). The restriction
            // rides on the persisted line and binds the saddlebag leg, retainer candidates and every stack taken.
            var restrictions = new Dictionary<Guid, RetrievalSourceRestriction>();
            var rows = operation.Lines.Select(line =>
            {
                var row = new TargetPlanItem
                {
                    Id = Guid.NewGuid(),
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    TargetQuantity = line.TargetQuantity,
                    Quality = line.Quality,
                    Enabled = true,
                };
                if (line.SourceRetainerIds is { } allowed)
                    restrictions[row.Id] = new(allowed.ToHashSet(), line.SourceSaddlebag);
                return row;
            }).ToArray();
            var restrictionByKey = SourceRestrictionsByKey(operation.Lines);
            var baselineInventory = placement is null ? playerInventory() : new Dictionary<uint, int>();
            var sources = cache.Snapshot();
            if (retrievalSource is not null) sources = sources.Where(p => retrievalSource(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            if (placement?.Source is { } sourceId)
                sources = sources.Where(p => p.Key == sourceId && p.Value is
                    { Owner.HasStableIdentity: true, IsCurrentlyAssigned: not false, IsUiAccessible: not false }).ToDictionary();
            var plan = RestockPlanner.Build(rows, baselineInventory, sources, owner, utcNow(), placementBaselines: placement?.RetrievalBaselines,
                sourceRestrictions: restrictions);
            var remaining = plan.Lines
                .GroupBy(line => RetrievalKey(line.ItemId, line.Quality))
                .ToDictionary(group => group.Key, group => group.Sum(line => line.NeededQuantity));
            journal.Transition(
                operationId,
                OperationStatuses.Running,
                "ExecutionStarted",
                operation.ExecuteImmediately ? "Automatic live retrieval verification started." : "Reviewed live retrieval verification started.");
            var transferred = 0;
            // Retainers whose live stock this run scanned, and whether the saddlebag leg could run at all: a restricted
            // line is only reported as short of stock when every allowed source was reached or verified empty.
            var visited = new HashSet<ulong>();
            var saddlebagReachable = saddlebag is not null && placement?.Source is null;
            // Saddlebag leg first: the retainers cover only what it leaves short. Under a
            // borrowed market-run lease every dispatch also requires that the lease is still held.
            // Lines restricted away from the saddlebag never reach its leg.
            var saddlebagPlan = restrictions.Count == 0
                ? plan
                : plan with { Lines = plan.Lines.Where(line => !restrictions.TryGetValue(line.PlanItemId, out var only) || only.Saddlebag).ToArray() };
            if (saddlebag is not null && placement?.Source is null && saddlebagPlan.NeededQuantity > 0)
            {
                movementAttempted = true;
                var leg = await saddlebag.ExecuteAsync(
                    operationId,
                    saddlebagPlan,
                    () => !token.IsCancellationRequested && operation.Owner.Matches(currentOwner()) &&
                        (borrowedLease is null || automation.Owns(borrowedLease)) && (placement?.Authorized() ?? true) && (callerAuthority?.Invoke() ?? true),
                    token).ConfigureAwait(false);
                movementAttempted = leg.Dispatched;
                saddlebagReachable &= leg.SaddlebagObserved;
                transferred = leg.VerifiedRetrieved;
                if (leg.Unreconciled is { } unreconciled)
                {
                    journal.Transition(operationId, OperationStatuses.Indeterminate, "SaddlebagReconciliationIncomplete", unreconciled);
                    return new(true, "Saddlebag retrieval could not be reconciled; retainer retrieval was not started.");
                }
                // Re-plan per line (item + quality) from the fresh bag reads, never from receipts alone.
                foreach (var ((itemId, quality), gained) in leg.Credit)
                {
                    var key = RetrievalKey(itemId, quality);
                    if (remaining.ContainsKey(key))
                        remaining[key] = Math.Max(0, remaining[key] - gained);
                }
            }
            var remainingNeed = remaining.Values.Sum();
            if (remainingNeed == 0 && transferred > 0)
            {
                journal.Transition(
                    operationId,
                    OperationStatuses.Succeeded,
                    "RetrievalComplete",
                    $"Retrieved {transferred:N0} units from the saddlebag and satisfied every target.");
                return new(true, "Retrieval completed from the saddlebag.");
            }
            if (remainingNeed == 0)
            {
                journal.Transition(
                    operationId,
                    OperationStatuses.Succeeded,
                    "AlreadySatisfied",
                    operation.ExecuteImmediately
                        ? "Automatic execution confirmed player inventory already satisfies every target."
                        : "Reviewed execution confirmed player inventory already satisfies every target.");
                return new(true, "Player inventory already satisfies every target.");
            }
            var candidates = plan.Lines
                .Where(line => remaining.GetValueOrDefault(RetrievalKey(line.ItemId, line.Quality)) > 0)
                .SelectMany(line => line.Candidates.Select(candidate => (line.ItemId, line.Quality, Candidate: candidate)))
                .GroupBy(entry => entry.Candidate.RetainerId)
                .Select(group => new
                {
                    Route = new RetainerRouteCandidate(group.Key, group.First().Candidate.RetainerName, group.First().Candidate.ObservedAtUtc),
                    ItemIds = group.Select(entry => entry.ItemId).ToHashSet(),
                    Quantity = group.Sum(entry => entry.Candidate.CachedQuantity),
                })
                .OrderByDescending(candidate => candidate.Quantity)
                .ThenByDescending(candidate => candidate.Route.ObservedAtUtc)
                .ToArray();
            if (candidates.Length == 0)
            {
                var shortfall = RecordRestrictedShort(operationId, operation, remaining, restrictionByKey, sources, owner, plan.Lines, visited, saddlebagReachable);
                if (shortfall.Unreached.Count == 0 && RestrictedShortOnly(remaining, restrictionByKey))
                {
                    journal.Transition(
                        operationId,
                        transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                        SourceRestrictionShortCode,
                        $"Retrieved {transferred:N0} units; {remainingNeed:N0} units remain because the allowed sources hold no more. Other sources were not used.");
                    return new(true, "Retrieval completed with missing units.");
                }
                var unreachedNote = UnreachedNote(shortfall.Unreached);
                if (transferred > 0)
                {
                    journal.Transition(
                        operationId,
                        OperationStatuses.PartiallySucceeded,
                        "RetrievalPartial",
                        $"Retrieved {transferred:N0} units from the saddlebag; {remainingNeed:N0} units remain and no owner-scoped cached retainer candidates cover them.{unreachedNote}");
                    return new(true, "Retrieval completed with missing units.");
                }
                journal.Transition(operationId, OperationStatuses.Failed, "NoCachedCandidates", $"No owner-scoped cached retainer candidates cover this operation.{unreachedNote}");
                return new(true, "No owner-scoped cached retainer candidates cover this operation.");
            }

            var retainerLegMessage = default(string);
            var inventoryFull = false;
            foreach (var candidate in candidates)
            {
                if (StopRequested(operationId))
                    break;
                if (!retainerSessionOpen)
                {
                    try
                    {
                        await driver.RequireRetainerListAsync(token).ConfigureAwait(false);
                        retainerSessionOpen = true;
                    }
                    catch (InvalidOperationException failure) when (!movementAttempted && IsBellUnavailable(failure.Message))
                    {
                        retainerLegMessage = failure.Message;
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                await driver.OpenRetainerAsync(candidate.Route, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                retainerOpen = true;
                await driver.OpenInventoryAsync(token).ConfigureAwait(false);
                var wanted = candidate.ItemIds.Where(itemId =>
                    operation.Lines.Any(line =>
                        line.ItemId == itemId &&
                        remaining.GetValueOrDefault(RetrievalKey(line.ItemId, line.Quality)) > 0)).ToHashSet();
                var liveStacks = await driver.ScanRetainerAsync(wanted, token).ConfigureAwait(false);
                visited.Add(candidate.Route.RetainerId);
                // The route scan is already required to choose physical source stacks.
                // Reuse its per-variant totals as verification baselines instead of
                // adding another retainer-wide scan to every retrieval command.
                var liveVariantQuantities = liveStacks
                    .GroupBy(stack => OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality))
                    .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));
                foreach (var stack in liveStacks)
                {
                    token.ThrowIfCancellationRequested();
                    var exactQuality = stack.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
                    var key = RetrievalKey(stack.ItemId, exactQuality);
                    if (remaining.GetValueOrDefault(key) <= 0)
                        key = RetrievalKey(stack.ItemId, ItemQualityPolicy.Any);
                    var quantity = Math.Min(stack.Quantity, remaining.GetValueOrDefault(key));
                    if (quantity <= 0)
                        continue;
                    if (restrictionByKey.TryGetValue(key, out var allowedHere) && !allowedHere.Contains(candidate.Route.RetainerId))
                    {
                        // The planner never offers a disallowed retainer for a restricted line; reaching here is a bug.
                        warning?.Invoke($"[Quartermaster] SourceRestrictionGuard: operation {operationId} skipped item {stack.ItemId} at retainer " +
                                        $"{candidate.Route.RetainerName} ({candidate.Route.RetainerId}), which the request did not allow.");
                        continue;
                    }
                    var itemName = operation.Lines.First(line => line.ItemId == stack.ItemId).ItemName;
                    var variantKey = OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality);
                    var previousMovementAttempted = movementAttempted;
                    var attempt = await VerifiedMutationTransaction.ExecuteAsync(
                        new RetainerStockMutationIntent(operationId, candidate.Route.RetainerId, operation.Owner, stack.ItemId, itemName, stack.IsHighQuality),
                        stockMutations,
                        async mutationToken =>
                        {
                            placement?.Check();
                            if (borrowedLease is not null && !automation.Owns(borrowedLease))
                                return VerifiedMutationAttempt<RetrievalResult, RetainerVariantObservation>.Unchanged(
                                    new(false, 0, "LeaseLost", "The caller no longer owns the automation lease."));
                            movementAttempted = true;
                            var result = await DiagnoseMutationAsync(operationId, "Retrieval", itemName, candidate.Route.RetainerName, () => driver.RetrieveAsync(
                                stack,
                                quantity,
                                liveVariantQuantities.GetValueOrDefault(variantKey),
                                mutationToken)).ConfigureAwait(false);
                            if (!result.Success)
                            {
                                return result.MovementMayHaveOccurred
                                    ? VerifiedMutationAttempt<RetrievalResult, RetainerVariantObservation>.Indeterminate(result)
                                    : VerifiedMutationAttempt<RetrievalResult, RetainerVariantObservation>.Unchanged(result);
                            }
                            if (result.Transferred <= 0)
                                return VerifiedMutationAttempt<RetrievalResult, RetainerVariantObservation>.Unchanged(result);
                            var observation = await ObserveVariantAsync(
                                candidate.Route.RetainerId,
                                stack.ItemId,
                                itemName,
                                stack.IsHighQuality,
                                mutationToken).ConfigureAwait(false);
                            return VerifiedMutationAttempt<RetrievalResult, RetainerVariantObservation>.Verified(result, observation);
                        },
                        token).ConfigureAwait(false);
                    var result = attempt.Result;
                    movementAttempted = previousMovementAttempted ||
                                        attempt.Evidence != VerifiedMutationEvidence.Unchanged;
                    if (!result.Success)
                    {
                        if (result.Code == "InventoryFull" && !result.MovementMayHaveOccurred)
                        {
                            inventoryFull = true;
                            journal.RecordWarning(operationId, result.Code, result.Message);
                            // The market gather caller reports once across all of its targets.
                            if (borrowedLease is null)
                                warning?.Invoke($"Retrieval blocked by inventory space; {remaining.Count(pair => pair.Value > 0)} targets remain unmet.");
                            break;
                        }
                        warning?.Invoke($"Retrieval {itemName} at {candidate.Route.RetainerName}: {result.Code}: {result.Message}");
                        journal.RecordWarning(operationId, result.Code, result.Message);
                        if (borrowedLease is not null && !result.MovementMayHaveOccurred)
                        {
                            movementAttempted = false;
                            if (result.Code == "GatherCancelled") throw new OperationCanceledException();
                            if (placement is null && result.Code is "SourceSlotChanged")
                            {
                                continue;
                            }
                        }
                        throw new InvalidOperationException(result.Message);
                    }
                    if (placement?.Source is not null && result.Transferred == 0)
                        throw new InvalidOperationException($"PlacementSourceRefused: {candidate.Route.RetainerName} supplied 0 of {quantity} {itemName}. {result.Message}");
                    remaining[key] -= result.Transferred;
                    liveVariantQuantities[variantKey] -= result.Transferred;
                    transferred += result.Transferred;
                    journal.RecordRetrievalTransfer(
                        operationId,
                        stack.ItemId,
                        stack.IsHighQuality,
                        candidate.Route.RetainerId,
                        result.Transferred,
                        result.Code,
                        result.Message);
                    // This movement is verified and recorded; only an unresolved attempt keeps the operation uncertain.
                    movementAttempted = false;
                    if (placement?.Source is not null && result.Transferred < quantity) break;
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                if (inventoryFull || remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
            if (placement?.KeepListOpen != true) await driver.CloseRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = false;
            var missing = remaining.Values.Where(quantity => quantity > 0).Sum();
            var bypassedRetainerLeg = retainerLegMessage is not null;
            // Allowed sources ran dry (not a full inventory, a stop, or a missing bell): say so per restricted line.
            var shortfallAfter = missing > 0 && !inventoryFull && !bypassedRetainerLeg && !StopRequested(operationId)
                ? RecordRestrictedShort(operationId, operation, remaining, restrictionByKey, sources, owner, plan.Lines, visited, saddlebagReachable)
                : (ShortLines: 0, Unreached: (IReadOnlyList<string>)[]);
            var sourceShort = shortfallAfter.ShortLines > 0 && shortfallAfter.Unreached.Count == 0 && RestrictedShortOnly(remaining, restrictionByKey);
            var unreachedAfter = UnreachedNote(shortfallAfter.Unreached);
            if (bypassedRetainerLeg)
            {
                journal.RecordWarning(
                    operationId,
                    "RetrievalSkippedBellUnavailable",
                    $"Retainer retrieval could not start: {retainerLegMessage} Vendor procurement may still cover the shortfall.");
            }
            journal.Transition(
                operationId,
                missing == 0 ? OperationStatuses.Succeeded : bypassedRetainerLeg ? OperationStatuses.Failed : transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                missing == 0 ? "RetrievalComplete" : inventoryFull ? "InventoryFull" : bypassedRetainerLeg ? "RetrievalLegUnavailable"
                    : sourceShort ? SourceRestrictionShortCode : transferred > 0 ? "RetrievalPartial" : "NoLiveStock",
                missing == 0
                    ? $"Retrieved {transferred:N0} units and satisfied every target."
                    : sourceShort
                        ? $"Retrieved {transferred:N0} units; {missing:N0} units remain because the allowed sources hold no more. Other sources were not used."
                    : inventoryFull
                        ? $"Retrieved {transferred:N0} units; inventory space blocked the remaining {missing:N0} units."
                    : bypassedRetainerLeg
                        ? $"Retainer retrieval could not start ({retainerLegMessage}); remaining units are left for vendor procurement."
                        : $"Retrieved {transferred:N0} units; {missing:N0} units remain missing.{unreachedAfter}");
            return new(true, missing == 0 ? "Retrieval completed." : "Retrieval completed with missing units.");
        }
        catch (OperationCanceledException)
        {
            Exception? releaseFailure = null;
            if (retainerOpen && !MarketRunDeadline.HasUnsettled)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure = closeException; }
            }
            if (retainerSessionOpen && !MarketRunDeadline.HasUnsettled)
            {
                try { await driver.CloseRetainerListAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure ??= closeException; }
            }
            if (borrowedLease is not null && !movementAttempted && journal.Get(operationId)?.Status == OperationStatuses.Running)
                journal.Transition(operationId, OperationStatuses.Cancelled, "CancelledBeforeMovement", "Gather cancelled with no unresolved movement.");
            else MarkCancelled(operationId);
            return new(
                true,
                releaseFailure is null
                    ? "Transfer cancelled; any operation that began live movement is indeterminate."
                    : $"Transfer cancelled and retainer session release failed: {releaseFailure.Message}");
        }
        catch (Exception exception)
        {
            Exception? releaseFailure = null;
            if (retainerOpen && !MarketRunDeadline.HasUnsettled)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure = closeException; }
            }
            if (retainerSessionOpen && !MarketRunDeadline.HasUnsettled)
            {
                try { await driver.CloseRetainerListAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure ??= closeException; }
            }
            var failure = releaseFailure is null
                ? exception.Message
                : $"{exception.Message} Retainer session release also failed: {releaseFailure.Message}";
            var operation = journal.Get(operationId);
            if (operation is { Status: not OperationStatuses.Failed } && !OperationStatuses.IsTerminal(operation.Status))
            {
                journal.Transition(
                    operationId,
                    movementAttempted ? OperationStatuses.Indeterminate : OperationStatuses.Failed,
                    movementAttempted ? "BookkeepingIndeterminate" : "ExecutionFailed",
                    movementAttempted ? $"Live movement may have occurred, but execution did not finish cleanly: {failure}" : failure);
            }
            return new(true, failure);
        }
        finally
        {
            ClearActive(linkedCancellation);
            Volatile.Write(ref running, 0);
        }
        }
    }

    public Task<TransferExecutionResult> ExecuteDepositAsync(string operationId, CancellationToken cancellationToken = default) =>
        WithAutoRetainerCoordinationAsync(
            () => ExecuteDepositCoreAsync(operationId, cancellationToken),
            cancellationToken);

    internal Task<TransferExecutionResult> ExecuteDepositUnderLeaseAsync(
        string operationId, IDisposable lease, CancellationToken cancellationToken) =>
        ExecuteDepositUnderLeaseAsync(operationId, lease, cancellationToken, null);

    private Task<TransferExecutionResult> ExecuteDepositUnderLeaseAsync(
        string operationId, IDisposable lease, CancellationToken cancellationToken, PlacementLeg? placement)
    {
        if (!automation.Owns(lease))
            throw new InvalidOperationException("The caller does not own the automation lease.");
        return ExecuteDepositCoreAsync(operationId, cancellationToken, lease, placement);
    }

    private async Task<TransferExecutionResult> ExecuteDepositCoreAsync(string operationId, CancellationToken cancellationToken, IDisposable? borrowedLease = null, PlacementLeg? placement = null)
    {
        IDisposable? lease = null;
        if (borrowedLease is null && !automation.TryAcquire("retainer transfer", out lease))
            return new(false, automation.AcquisitionBlockReason ?? "Automation is unavailable.");
        using (lease)
        {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(false, "Another retainer transfer is already running.");
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(linkedCancellation);
        var token = linkedCancellation.Token;
        var retainerOpen = false;
        var retainerSessionOpen = false;
        var movementAttempted = false;
        string? lastZeroMovement = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var operation = journal.Get(operationId) ?? throw new KeyNotFoundException($"Operation '{operationId}' was not found.");
            if (operation.Kind is not (OperationKinds.Deposit or OperationKinds.QuickDeposit or OperationKinds.StowageSurplus))
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Kind}', not deposit.");
            if (operation.Status != OperationStatuses.Accepted)
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Status}', not accepted.");
            var owner = currentOwner();
            if (!operation.Owner.HasStableIdentity || !owner.HasStableIdentity || !operation.Owner.Matches(owner))
                throw new InvalidOperationException("Operation owner no longer matches current character.");
            if (operation.DepositCandidates.Count == 0 || operation.Lines.Count == 0)
                throw new InvalidOperationException("Deposit operation has no persisted reviewed authorization.");
            var legacyCrystalDeposit = operation.Kind == OperationKinds.Deposit;
            journal.Transition(operationId, OperationStatuses.Running, "DepositStarted", "Live stowage verification started.");
            var remaining = operation.Lines.ToDictionary(
                OperationJournal.AuthorizationKey,
                line => line.TargetQuantity);
            var transferred = 0;
            await driver.RequireRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = true;
            token.ThrowIfCancellationRequested();
            var orderedSources = legacyCrystalDeposit
                ? operation.DepositCandidates
                : operation.DepositCandidates
                    .OrderBy(source => source.PriorityByVariant.Count == 0
                        ? int.MaxValue
                        : source.PriorityByVariant.Values.Min())
                    .ThenBy(source => source.RetainerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.RetainerId)
                    .ToList();
            var remainingCandidateIds = orderedSources.Select(source => source.RetainerId).ToHashSet();
            foreach (var source in orderedSources)
            {
                if (StopRequested(operationId))
                    break;
                var candidateCapacity = legacyCrystalDeposit
                    ? source.CapacityByItem.ToDictionary(
                        entry => OperationJournal.VariantKey(entry.Key, false),
                        entry => entry.Value)
                    : LiveCandidateCapacity(operation, source, owner);
                if (!remaining.Any(entry => entry.Value > 0 && candidateCapacity.GetValueOrDefault(entry.Key) > 0))
                {
                    remainingCandidateIds.Remove(source.RetainerId);
                    continue;
                }
                var candidate = new RetainerRouteCandidate(source.RetainerId, source.RetainerName, source.ObservedAtUtc);
                await driver.OpenRetainerAsync(candidate, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                retainerOpen = true;
                await driver.OpenInventoryAsync(token).ConfigureAwait(false);
                var authorizedKeys = remaining
                    .Where(entry =>
                        entry.Value > 0 &&
                        candidateCapacity.GetValueOrDefault(entry.Key) > 0 &&
                        (legacyCrystalDeposit || IsBestRemainingCandidate(
                            operation,
                            source,
                            entry.Key,
                            owner,
                            remainingCandidateIds)))
                    .Select(entry => entry.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var wanted = operation.Lines
                    .Where(line => authorizedKeys.Contains(OperationJournal.AuthorizationKey(line)))
                    .Select(line => line.ItemId)
                    .ToHashSet();
                var stacks = legacyCrystalDeposit
                    ? await driver.ScanPlayerCrystalsAsync(wanted, token).ConfigureAwait(false)
                    : (await driver.ScanPlayerInventoryAsync(wanted, token).ConfigureAwait(false))
                        .Concat(await driver.ScanPlayerCrystalsAsync(wanted, token).ConfigureAwait(false))
                        .ToArray();
                foreach (var stack in stacks)
                {
                    token.ThrowIfCancellationRequested();
                    var stackRemaining = stack.Quantity;
                    var matchingLines = operation.Lines
                        .Where(line => line.ItemId == stack.ItemId && line.IsHighQuality == stack.IsHighQuality)
                        .Where(line => authorizedKeys.Contains(OperationJournal.AuthorizationKey(line)))
                        .OrderBy(line => source.PriorityByVariant.GetValueOrDefault(OperationJournal.AuthorizationKey(line), int.MaxValue))
                        .ToArray();
                    foreach (var operationLine in matchingLines)
                    {
                        var key = OperationJournal.AuthorizationKey(operationLine);
                        var authorized = Math.Min(remaining.GetValueOrDefault(key), candidateCapacity.GetValueOrDefault(key));
                        if (authorized <= 0 || stackRemaining <= 0)
                            continue;
                        var quantity = Math.Min(stackRemaining, authorized);
                        if (placement is not null)
                        {
                            var current = ElementalCurrencyCatalog.IsElementalCurrency(stack.ItemId)
                                ? await driver.ScanPlayerCrystalsAsync(wanted, token).ConfigureAwait(false)
                                : await driver.ScanPlayerInventoryAsync(wanted, token).ConfigureAwait(false);
                            var floor = placement.DepositFloors![(stack.ItemId, stack.IsHighQuality)];
                            var held = current.Where(s => s.ItemId == stack.ItemId && Matches(floor.Quality, s.IsHighQuality)).Sum(s => s.Quantity);
                            quantity = Math.Min(quantity, Math.Max(0, held - floor.Quantity));
                            if (quantity == 0) continue;
                        }
                        // Ordinary partial deposits use Entrust Quantity: the exact amount can top up a
                        // partial stack and spill into new slots. Only crystals need the additional clamp.
                        if (!legacyCrystalDeposit &&
                            stack.Container == FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Crystals)
                        {
                            var partialCapacity = LivePartialStackCapacity(source, operationLine, owner);
                            if (partialCapacity > 0)
                                quantity = Math.Min(quantity, partialCapacity);
                        }
                        var itemName = operationLine.ItemName;
                        var previousMovementAttempted = movementAttempted;
                        var attempt = await VerifiedMutationTransaction.ExecuteAsync(
                            new RetainerStockMutationIntent(operationId, candidate.RetainerId, operation.Owner, stack.ItemId, itemName, stack.IsHighQuality),
                            stockMutations,
                            async mutationToken =>
                            {
                                placement?.Check();
                                movementAttempted = true;
                                var currentStack = stack with { Quantity = stackRemaining };
                                (bool Success, int Transferred, string Code, string Message) result;
                                if (stack.Container == FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Crystals)
                                {
                                    var crystal = await DiagnoseMutationAsync(operationId, "Deposit", itemName, candidate.RetainerName,
                                        () => driver.DepositCrystalAsync(currentStack, quantity, mutationToken)).ConfigureAwait(false);
                                    result = (crystal.Success, crystal.Transferred, crystal.Code, crystal.Message);
                                }
                                else
                                {
                                    var item = await DiagnoseMutationAsync(operationId, "Deposit", itemName, candidate.RetainerName,
                                        () => driver.DepositAsync(currentStack, quantity, mutationToken)).ConfigureAwait(false);
                                    // The game occasionally drops a deposit after a retainer opens: the verifier saw no
                                    // movement on either side through its window, settle and late tail. While the source
                                    // slot proves untouched, retry: once for any move, and up to three times for a
                                    // whole-stack move, which an earlier dispatch can never move twice. Retries stay under
                                    // this mutation's marker. Exhausted retries with an untouched slot moved nothing.
                                    var zeroMovementRetries = 0;
                                    var zeroMovementRetryLimit = quantity >= currentStack.Quantity ? 3 : 1;
                                    while (!item.Success && item.Transferred == 0 && item.Code == "VerificationWindowExpired" &&
                                           (await driver.ScanPlayerInventoryAsync(new HashSet<uint> { stack.ItemId }, mutationToken).ConfigureAwait(false))
                                               .Any(live => live.Container == currentStack.Container && live.SlotIndex == currentStack.SlotIndex &&
                                                   live.ItemId == currentStack.ItemId && live.IsHighQuality == currentStack.IsHighQuality &&
                                                   live.Quantity == currentStack.Quantity))
                                    {
                                        if (zeroMovementRetries >= zeroMovementRetryLimit)
                                        {
                                            var unmoved = $"Deposit {itemName} at {candidate.RetainerName} moved nothing in {zeroMovementRetries + 1} attempts and its source slot is untouched; leaving it in bags. Last miss {item.Code}: {item.Message}";
                                            item = new(false, 0, DepositZeroMovementCode, unmoved);
                                            break;
                                        }
                                        zeroMovementRetries++;
                                        var retried = $"Deposit {itemName} at {candidate.RetainerName} moved nothing and its source slot is untouched; retry {zeroMovementRetries} of {zeroMovementRetryLimit}. {item.Code}: {item.Message}";
                                        warning?.Invoke(retried);
                                        journal.RecordWarning(operationId, "DepositZeroMovementRetried", retried);
                                        await Task.Delay(zeroMovementRetries == 1 ? 250 : 1_000, mutationToken).ConfigureAwait(false);
                                        item = await DiagnoseMutationAsync(operationId, "Deposit", itemName, candidate.RetainerName,
                                            () => driver.DepositAsync(currentStack, quantity, mutationToken)).ConfigureAwait(false);
                                    }
                                    result = (item.Success, item.Transferred, item.Code, item.Message);
                                }
                                if (!result.Success)
                                {
                                    if (result.Code is "NoCapacity" or DepositZeroMovementCode)
                                        return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Unchanged(result);
                                    return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Indeterminate(result);
                                }
                                if (result.Transferred <= 0)
                                    return result.Code == "NoCapacity"
                                        ? VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Unchanged(result)
                                        : VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Indeterminate(result);
                                var observation = await ObserveVariantAsync(
                                    candidate.RetainerId,
                                    stack.ItemId,
                                    itemName,
                                    stack.IsHighQuality,
                                    mutationToken).ConfigureAwait(false);
                                return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Verified(result, observation);
                            },
                            token).ConfigureAwait(false);
                        var result = attempt.Result;
                        if (!result.Success || result.Transferred <= 0)
                        {
                            warning?.Invoke($"Deposit {itemName} at {candidate.RetainerName}: {result.Code}: {result.Message}");
                            journal.RecordWarning(operationId, result.Code, result.Message);
                        }
                        movementAttempted = previousMovementAttempted ||
                                            attempt.Evidence != VerifiedMutationEvidence.Unchanged;
                        if (!result.Success &&
                            attempt.Evidence == VerifiedMutationEvidence.Unchanged &&
                            result.Code == "NoCapacity")
                        {
                            if (placement is not null) candidateCapacity[key] = 0;
                            journal.RecordWarning(
                                operationId,
                                "DepositSkippedNoCapacity",
                                $"{candidate.RetainerName} reported no live capacity for {itemName}; its remaining quantity will try other candidates. {result.Code}: {result.Message}");
                            continue;
                        }
                        if (!result.Success &&
                            attempt.Evidence == VerifiedMutationEvidence.Unchanged &&
                            result.Code == DepositZeroMovementCode)
                        {
                            // Proven no-op: leave this stack in bags and carry on with the next stack or candidate.
                            lastZeroMovement = result.Message;
                            break;
                        }
                        if (!result.Success || attempt.Evidence == VerifiedMutationEvidence.Indeterminate)
                            throw new InvalidOperationException(result.Message);
                        if (result.Transferred == 0)
                        {
                            if (placement is not null) candidateCapacity[key] = 0;
                            if (result.Code == "NoCapacity")
                            {
                                journal.RecordWarning(
                                    operationId,
                                    "DepositSkippedNoCapacity",
                                    $"{candidate.RetainerName} crystal storage is full for {itemName}; its remaining quantity will try other candidates.");
                            }
                            continue;
                        }
                        remaining[key] -= result.Transferred;
                        candidateCapacity[key] = Math.Max(0, candidateCapacity.GetValueOrDefault(key) - result.Transferred);
                        if (result.Transferred < quantity)
                        {
                            if (placement is not null) candidateCapacity[key] = 0;
                            journal.RecordWarning(
                                operationId,
                                "DepositCapacityClamped",
                                $"{candidate.RetainerName} accepted only {result.Transferred:N0} of the requested {quantity:N0} {itemName}; later stacks may still merge into any compatible capacity that remains.");
                        }
                        stackRemaining -= result.Transferred;
                        transferred += result.Transferred;
                        if (legacyCrystalDeposit)
                            journal.RecordTransfer(operationId, stack.ItemId, candidate.RetainerId, result.Transferred, result.Code, result.Message);
                        else
                            journal.RecordDepositTransfer(operationId, key, stack.ItemId, stack.IsHighQuality, candidate.RetainerId, result.Transferred, result.Code, result.Message);
                        // This movement is verified and recorded; only an unresolved attempt keeps the operation uncertain.
                        movementAttempted = false;
                    }
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                remainingCandidateIds.Remove(source.RetainerId);
                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
            if (placement?.KeepListOpen != true) await driver.CloseRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = false;
            var missing = remaining.Values.Where(quantity => quantity > 0).Sum();
            journal.Transition(operationId,
                missing == 0 ? OperationStatuses.Succeeded : transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                missing == 0 ? "DepositComplete" : "DepositPartial",
                $"Stowed {transferred:N0} units; {missing:N0} remain on character." +
                (missing > 0 && lastZeroMovement is not null ? $" {DepositZeroMovementCode}: {lastZeroMovement}" : string.Empty));
            return new(true, missing == 0 ? "Deposit completed." : "Deposit completed with remaining units.");
        }
        catch (OperationCanceledException)
        {
            Exception? releaseFailure = null;
            if (retainerOpen)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure = closeException; }
            }
            if (retainerSessionOpen)
            {
                try { await driver.CloseRetainerListAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure ??= closeException; }
            }
            MarkCancelled(operationId);
            return new(
                true,
                releaseFailure is null
                    ? "Transfer cancelled; any operation that began live movement is indeterminate."
                    : $"Transfer cancelled and retainer session release failed: {releaseFailure.Message}");
        }
        catch (Exception exception)
        {
            Exception? releaseFailure = null;
            if (retainerOpen)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure = closeException; }
            }
            if (retainerSessionOpen)
            {
                try { await driver.CloseRetainerListAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception closeException) { releaseFailure ??= closeException; }
            }
            var failure = releaseFailure is null
                ? exception.Message
                : $"{exception.Message} Retainer session release also failed: {releaseFailure.Message}";
            var operation = journal.Get(operationId);
            if (operation is not null && !OperationStatuses.IsTerminal(operation.Status))
            {
                journal.Transition(
                    operationId,
                    movementAttempted ? OperationStatuses.Indeterminate : OperationStatuses.Failed,
                    movementAttempted ? "BookkeepingIndeterminate" : "DepositFailed",
                    movementAttempted ? $"Live movement may have occurred, but execution did not finish cleanly: {failure}" : failure);
            }
            return new(true, failure);
        }
        finally
        {
            ClearActive(linkedCancellation);
            Volatile.Write(ref running, 0);
        }
        }
    }

    private Dictionary<string, int> LiveCandidateCapacity(
        OperationRecord operation,
        DepositCandidateAuthorization source,
        OwnerScope owner)
    {
        if (!source.UsesLiveCapacity)
            return source.CapacityByVariant.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!cache.Snapshot().TryGetValue(source.RetainerId, out var retainer) || !retainer.Owner.Matches(owner))
            return source.CapacityByVariant.ToDictionary(entry => entry.Key, _ => 0, StringComparer.Ordinal);

        var legacyAuthorization = source.PriorityByVariant.Count == 0;
        var capacity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in operation.Lines)
        {
            var key = OperationJournal.AuthorizationKey(line);
            if (!source.CapacityByVariant.ContainsKey(key) ||
                !legacyAuthorization && !source.PriorityByVariant.ContainsKey(key))
                continue;
            var live = StowageRouter.Capacity(
                retainer,
                line.ItemId,
                line.IsHighQuality,
                Math.Max(1, line.MaxStackSize));
            capacity[key] = live.TotalCapacity;
        }
        return capacity;
    }

    private int LivePartialStackCapacity(
        DepositCandidateAuthorization source,
        OperationLine line,
        OwnerScope owner)
    {
        if (!source.UsesLiveCapacity ||
            !cache.Snapshot().TryGetValue(source.RetainerId, out var retainer) ||
            !retainer.Owner.Matches(owner))
            return 0;
        return StowageRouter.Capacity(
            retainer,
            line.ItemId,
            line.IsHighQuality,
            Math.Max(1, line.MaxStackSize)).PartialStackCapacity;
    }

    private bool IsBestRemainingCandidate(
        OperationRecord operation,
        DepositCandidateAuthorization source,
        string key,
        OwnerScope owner,
        IReadOnlySet<ulong> remainingCandidateIds)
    {
        var best = operation.DepositCandidates
            .Where(candidate => remainingCandidateIds.Contains(candidate.RetainerId))
            .Where(candidate => candidate.PriorityByVariant.ContainsKey(key))
            .Where(candidate => LiveCandidateCapacity(operation, candidate, owner).GetValueOrDefault(key) > 0)
            .OrderBy(candidate => candidate.PriorityByVariant[key])
            .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.RetainerId)
            .FirstOrDefault();
        return best?.RetainerId == source.RetainerId;
    }

    public void CancelActive()
    {
        lock (activeGate)
        {
            if (placementCancellation is not null)
            {
                // The move loop cancels new dispatches but finishes a verified transit deposit.
                placementCancellation.Cancel();
                return;
            }
            activeCancellation?.Cancel();
        }
        driver.CancelActive();
    }

    /// <summary>
    /// The requester asked this operation to stop (Quartermaster.v1.CancelRequestedOperation): the retainer
    /// already open has finished, so no further retainer is opened and the operation settles normally.
    /// </summary>
    private bool StopRequested(string operationId)
    {
        if (!journal.IsStopRequested(operationId))
            return false;
        const string message = "Stopped by its requester after the current retainer; remaining units were not moved.";
        warning?.Invoke($"{operationId}: StoppedByRequester: {message}");
        journal.RecordWarning(operationId, "StoppedByRequester", message);
        return true;
    }

    private void MarkCancelled(string operationId)
    {
        if (journal.Get(operationId) is not { } operation)
            return;
        try
        {
            if (operation.Status == OperationStatuses.Accepted)
            {
                journal.Transition(operationId, OperationStatuses.Cancelled, "CancelledBeforeExecution", "Transfer was cancelled before live movement began.");
            }
            else if (operation.Status == OperationStatuses.Running)
            {
                journal.Transition(
                    operationId,
                    OperationStatuses.Indeterminate,
                    "CancelledDuringExecution",
                    "Transfer was cancelled during execution; any active mutation evidence was reconciled by its durable transaction.");
            }
        }
        catch (InvalidOperationException) when (journal.Get(operationId) is { Status: var status } && OperationStatuses.IsTerminal(status))
        {
        }
    }

    private void CancelAccepted(string? operationId, string message)
    {
        if (operationId is null || journal.Get(operationId) is not { Status: OperationStatuses.Accepted })
            return;
        journal.Transition(operationId, OperationStatuses.Cancelled, "PlanSequenceStopped", message);
    }

    private void SetActive(CancellationTokenSource cancellation)
    {
        lock (activeGate)
            activeCancellation = cancellation;
    }

    private void ClearActive(CancellationTokenSource cancellation)
    {
        lock (activeGate)
        {
            if (ReferenceEquals(activeCancellation, cancellation))
                activeCancellation = null;
        }
        cancellation.Dispose();
    }

    private static string RetrievalKey(uint itemId, ItemQualityPolicy quality) =>
        $"{itemId}:{quality}";

    /// <summary>Outcome and per-line receipt code: a source-restricted line's allowed sources could not cover it.</summary>
    public const string SourceRestrictionShortCode = "SourceRestrictionShort";

    /// <summary>Deposit result code: every zero-movement retry missed and the source slot stayed untouched, so nothing moved.</summary>
    public const string DepositZeroMovementCode = "DepositZeroMovement";

    /// <summary>Allowed retainers per retrieval key; a key with any unrestricted line is unrestricted.</summary>
    private static Dictionary<string, HashSet<ulong>> SourceRestrictionsByKey(IEnumerable<OperationLine> lines) =>
        lines.GroupBy(line => RetrievalKey(line.ItemId, line.Quality))
            .Where(group => group.All(line => line.SourceRetainerIds is not null))
            .ToDictionary(group => group.Key, group => group.SelectMany(line => line.SourceRetainerIds!).ToHashSet());

    /// <summary>True when every unit still missing belongs to a source-restricted line.</summary>
    private static bool RestrictedShortOnly(IReadOnlyDictionary<string, int> remaining, IReadOnlyDictionary<string, HashSet<ulong>> restrictionByKey) =>
        restrictionByKey.Count > 0 &&
        remaining.Where(pair => pair.Value > 0).All(pair => restrictionByKey.ContainsKey(pair.Key));

    /// <summary>Per-source receipt code: an allowed source of a short restricted line was never reached or verified.</summary>
    public const string SourceRetainerUnreachedCode = "SourceRetainerUnreached";

    /// <summary>
    /// For each restricted line still short: when every allowed source was reached (its live stock scanned) or verified
    /// to hold none of the item (assigned, accessible, fully observed, no cached units), records one
    /// <see cref="SourceRestrictionShortCode"/> receipt (item, units left). Otherwise records one
    /// <see cref="SourceRetainerUnreachedCode"/> receipt per source that was not (item, retainer, units left, why).
    /// Everything is logged at Warning. Returns the short line count and a "Name (id): why" entry per unreached source.
    /// </summary>
    private (int ShortLines, IReadOnlyList<string> Unreached) RecordRestrictedShort(string operationId, OperationRecord operation,
        IReadOnlyDictionary<string, int> remaining, IReadOnlyDictionary<string, HashSet<ulong>> restrictionByKey,
        IReadOnlyDictionary<ulong, CachedRetainer> sources, OwnerScope owner, IEnumerable<PlanLine> planLines,
        IReadOnlySet<ulong> visited, bool saddlebagReachable)
    {
        var count = 0;
        var unreachedAll = new List<string>();
        foreach (var line in operation.Lines)
        {
            var key = RetrievalKey(line.ItemId, line.Quality);
            if (line.SourceRetainerIds is not { } allowed || !restrictionByKey.ContainsKey(key) || remaining.GetValueOrDefault(key) <= 0)
                continue;
            count++;
            var left = remaining[key];
            var offered = planLines.Where(planned => RetrievalKey(planned.ItemId, planned.Quality) == key)
                .SelectMany(planned => planned.Candidates).Select(candidate => candidate.RetainerId).ToHashSet();
            var unreached = new List<(ulong? RetainerId, string Label, string Reason)>();
            foreach (var id in allowed.Distinct())
            {
                var known = sources.TryGetValue(id, out var retainer) && retainer.Owner.Matches(owner) ? retainer : null;
                var reason = known is null ? "is not among this character's observed retainers on this run"
                    : known.IsCurrentlyAssigned is false ? "is no longer assigned"
                    : known.IsUiAccessible is false ? "could not be opened from the retainer list"
                    : !StockCoverage.CoversItem(known, line.ItemId) ? "has stock that was never fully observed"
                    : offered.Contains(id) && !visited.Contains(id) ? "was not visited"
                    : null;
                if (reason is not null)
                    unreached.Add((id, known is null ? $"Retainer {id}" : $"{known.RetainerName} ({id})", reason));
            }
            if (line.SourceSaddlebag && !saddlebagReachable)
                unreached.Add((null, "the saddlebag", "could not be used on this run"));
            if (unreached.Count == 0)
            {
                var message = $"{line.ItemName} ({line.ItemId}): {left:N0} units not retrieved; the allowed retainers [{string.Join(", ", allowed)}]" +
                              $"{(line.SourceSaddlebag ? " and the saddlebag" : string.Empty)} hold no more. Other sources were not used.";
                warning?.Invoke($"[Quartermaster] {SourceRestrictionShortCode}: operation {operationId}: {message}");
                journal.RecordWarning(operationId, SourceRestrictionShortCode, message, line.ItemId, left);
                continue;
            }
            foreach (var (retainerId, label, reason) in unreached)
            {
                var message = $"{line.ItemName} ({line.ItemId}): {left:N0} units not retrieved; allowed source {label} {reason}. Other sources were not used.";
                warning?.Invoke($"[Quartermaster] {SourceRetainerUnreachedCode}: operation {operationId}: {message}");
                journal.RecordWarning(operationId, SourceRetainerUnreachedCode, message, line.ItemId, left, retainerId);
                unreachedAll.Add($"{label} {reason}");
            }
        }
        return (count, unreachedAll.Distinct().ToArray());
    }

    private static string UnreachedNote(IReadOnlyList<string> unreached) =>
        unreached.Count == 0 ? string.Empty : $" Allowed sources not reached: {string.Join("; ", unreached)}. Other sources were not used.";

    /// <summary>
    /// True when the retainer list could not be opened because no summoning bell is
    /// reachable. This makes the retainer-retrieval leg unavailable without implying
    /// anything about the other legs of the plan (stowage, vendor procurement).
    /// </summary>
    private static bool IsBellUnavailable(string message) =>
        message.StartsWith("NoNearbySummoningBell", StringComparison.Ordinal) ||
        message.StartsWith("NoInteractableSummoningBell", StringComparison.Ordinal) ||
        message.StartsWith("BellNavigationUnavailable", StringComparison.Ordinal) ||
        message.StartsWith("BellRouteTimedOut", StringComparison.Ordinal) ||
        message.StartsWith("SummoningBellInteractionFailed", StringComparison.Ordinal);

    /// <summary>
    /// Serializes live movement with AutoRetainer: waits a bounded time for it to
    /// go idle, then holds the shared suppression scope for the movement. Nested
    /// scopes (plan sequence around retrieval and deposit, automatic queue around
    /// the coordinator) add references to the same acquisition, so suppression is
    /// restored exactly once when the outermost owner finishes.
    /// </summary>
    private async Task<TransferExecutionResult> WithAutoRetainerCoordinationAsync(
        Func<Task<TransferExecutionResult>> body,
        CancellationToken cancellationToken)
    {
        if (automation.RefusalReason is { } refusal) return new(false, refusal);
        var suppression = autoRetainerSuppression;
        if (suppression is null || !suppression.IsAvailable || coordinationAmbient.Value)
            return await body().ConfigureAwait(false);

        // One coordination scope at a time so CancelActive always targets the
        // live wait or movement; nested plan/queue wrappers rejoin the ambient
        // scope instead of deadlocking on this gate.
        await coordinationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            coordinationAmbient.Value = true;
            try
            {
                return await WithAutoRetainerCoordinationCoreAsync(suppression, body, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                coordinationAmbient.Value = false;
            }
        }
        finally
        {
            coordinationGate.Release();
        }
    }

    private async Task<TransferExecutionResult> WithAutoRetainerCoordinationCoreAsync(
        AutoRetainerSuppression suppression,
        Func<Task<TransferExecutionResult>> body,
        CancellationToken cancellationToken)
    {
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(linkedCancellation);
        try
        {
            var deadline = utcNow() + autoRetainerWait;
            while (suppression.IsBusy)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (utcNow() >= deadline)
                    return new(false, "AutoRetainer remained busy beyond the coordination wait; the transfer was not started.");
                await Task.Delay(autoRetainerPoll, linkedCancellation.Token).ConfigureAwait(false);
            }

            AutoRetainerSuppression.Scope scope;
            try
            {
                scope = suppression.Acquire();
            }
            catch (Exception exception)
            {
                return new(false, $"AutoRetainer coordination failed before the transfer started: {exception.Message}");
            }

            TransferExecutionResult result;
            try
            {
                result = await body().ConfigureAwait(false);
            }
            finally
            {
                scope.Dispose();
            }
            return scope.RestoreFailure is null
                ? result
                : result with { Message = $"{result.Message} AutoRetainer suppression could not be restored: {scope.RestoreFailure}" };
        }
        finally
        {
            ClearActive(linkedCancellation);
        }
    }

    private async Task<T> DiagnoseMutationAsync<T>(string operationId, string kind, string item, string retainer, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception exception)
        {
            var code = kind + "Faulted";
            warning?.Invoke($"{kind} {item} at {retainer}: {code}: {exception.Message}");
            journal.RecordWarning(operationId, code, exception.Message);
            throw;
        }
    }

    private async Task<RetainerVariantObservation> RecoverStockObservationAsync(RetainerStockMutationIntent intent)
    {
        void RequireAuthority()
        {
            if (MarketRunDeadline.HasUnsettled || intent.ItemId == 0 || !intent.Owner.HasStableIdentity ||
                !intent.Owner.Matches(currentOwner()) ||
                !cache.Snapshot().TryGetValue(intent.RetainerId, out var retained) || !intent.Owner.Matches(retained.Owner))
                throw new InvalidOperationException("Stock observation recovery has no settled owner-bound session.");
        }
        RequireAuthority();
        var observation = await ObserveVariantAsync(intent.RetainerId, intent.ItemId, intent.ItemName,
            intent.IsHighQuality, CancellationToken.None).ConfigureAwait(false);
        RequireAuthority();
        return observation;
    }

    private async Task<RetainerVariantObservation> ObserveVariantAsync(
        ulong retainerId,
        uint itemId,
        string itemName,
        bool isHighQuality,
        CancellationToken cancellationToken)
    {
        var stacks = await driver.ScanRetainerAsync(
            new HashSet<uint> { itemId },
            cancellationToken).ConfigureAwait(false);
        return new(
            retainerId,
            itemId,
            itemName,
            isHighQuality,
            utcNow(),
            stacks.Where(stack =>
                    stack.ItemId == itemId &&
                    stack.IsHighQuality == isHighQuality)
                .ToArray());
    }

}
