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

public sealed class TransferCoordinator : IRetrievalOperationExecutor
{
    private readonly OperationJournal journal;
    private readonly IRetainerTransferDriver driver;
    private readonly RetainerCacheRepository cache;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<IReadOnlyDictionary<uint, int>> playerInventory;
    private readonly AutomationLease automation;
    private readonly Func<DateTime> utcNow;
    private readonly RetainerStockMutationPersistence stockMutations;
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
        TimeSpan? autoRetainerPoll = null)
    {
        this.journal = journal;
        this.driver = driver;
        this.cache = cache;
        this.currentOwner = currentOwner;
        this.playerInventory = playerInventory;
        this.automation = automation ?? new AutomationLease();
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.autoRetainerSuppression = autoRetainerSuppression;
        this.autoRetainerWait = autoRetainerWait ?? TimeSpan.FromSeconds(30);
        this.autoRetainerPoll = autoRetainerPoll ?? TimeSpan.FromMilliseconds(250);
        stockMutations = new(journal, cache);
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public bool CanStart => !automation.IsHeld && !IsRunning;

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

    private async Task<TransferExecutionResult> ExecuteRetrievalCoreAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!automation.TryAcquire("retainer transfer", out var lease))
            return new(false, $"Automation is busy with {automation.Holder}.");
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
            var rows = operation.Lines.Select(line => new TargetPlanItem
            {
                Id = Guid.NewGuid(),
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                TargetQuantity = line.TargetQuantity,
                Quality = line.Quality,
                Enabled = true,
            }).ToArray();
            var plan = RestockPlanner.Build(rows, playerInventory(), cache.Snapshot(), owner, utcNow());
            var remaining = plan.Lines
                .GroupBy(line => RetrievalKey(line.ItemId, line.Quality))
                .ToDictionary(group => group.Key, group => group.Sum(line => line.NeededQuantity));
            journal.Transition(
                operationId,
                OperationStatuses.Running,
                "ExecutionStarted",
                operation.ExecuteImmediately ? "Automatic live retrieval verification started." : "Reviewed live retrieval verification started.");
            if (plan.NeededQuantity == 0)
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
            var candidates = plan.Lines.SelectMany(line => line.Candidates.Select(candidate => (line.ItemId, line.Quality, Candidate: candidate)))
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
                journal.Transition(operationId, OperationStatuses.Failed, "NoCachedCandidates", "No owner-scoped cached retainer candidates cover this operation.");
                return new(true, "No owner-scoped cached retainer candidates cover this operation.");
            }

            var transferred = 0;
            await driver.RequireRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = true;
            token.ThrowIfCancellationRequested();
            foreach (var candidate in candidates)
            {
                await driver.OpenRetainerAsync(candidate.Route, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                retainerOpen = true;
                await driver.OpenInventoryAsync(token).ConfigureAwait(false);
                var wanted = candidate.ItemIds.Where(itemId =>
                    operation.Lines.Any(line =>
                        line.ItemId == itemId &&
                        remaining.GetValueOrDefault(RetrievalKey(line.ItemId, line.Quality)) > 0)).ToHashSet();
                var liveStacks = await driver.ScanRetainerAsync(wanted, token).ConfigureAwait(false);
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
                    var itemName = operation.Lines.First(line => line.ItemId == stack.ItemId).ItemName;
                    var variantKey = OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality);
                    var previousMovementAttempted = movementAttempted;
                    var attempt = await VerifiedMutationTransaction.ExecuteAsync(
                        new RetainerStockMutationIntent(operationId, candidate.Route.RetainerId, operation.Owner),
                        stockMutations,
                        async mutationToken =>
                        {
                            movementAttempted = true;
                            var result = await driver.RetrieveAsync(
                                stack,
                                quantity,
                                liveVariantQuantities.GetValueOrDefault(variantKey),
                                mutationToken).ConfigureAwait(false);
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
                        throw new InvalidOperationException(result.Message);
                    }
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
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
            await driver.CloseRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = false;
            var missing = remaining.Values.Where(quantity => quantity > 0).Sum();
            journal.Transition(
                operationId,
                missing == 0 ? OperationStatuses.Succeeded : transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                missing == 0 ? "RetrievalComplete" : transferred > 0 ? "RetrievalPartial" : "NoLiveStock",
                missing == 0
                    ? $"Retrieved {transferred:N0} units and satisfied every target."
                    : $"Retrieved {transferred:N0} units; {missing:N0} units remain missing.");
            return new(true, missing == 0 ? "Retrieval completed." : "Retrieval completed with missing units.");
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

    private async Task<TransferExecutionResult> ExecuteDepositCoreAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!automation.TryAcquire("retainer transfer", out var lease))
            return new(false, $"Automation is busy with {automation.Holder}.");
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
                var depositedSinceScan = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var stack in stacks)
                {
                    token.ThrowIfCancellationRequested();
                    var variantKey = OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality);
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
                        if (!legacyCrystalDeposit)
                        {
                            var partialCapacity = LivePartialStackCapacity(source, operationLine, owner);
                            if (partialCapacity > 0)
                                quantity = Math.Min(quantity, partialCapacity);
                        }
                        var itemName = operationLine.ItemName;
                        var previousMovementAttempted = movementAttempted;
                        var attempt = await VerifiedMutationTransaction.ExecuteAsync(
                            new RetainerStockMutationIntent(operationId, candidate.RetainerId, operation.Owner),
                            stockMutations,
                            async mutationToken =>
                            {
                                movementAttempted = true;
                                (bool Success, int Transferred, string Code, string Message) result;
                                if (stack.Container == FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Crystals)
                                {
                                    var crystal = await driver.DepositCrystalAsync(stack, quantity, mutationToken).ConfigureAwait(false);
                                    result = (crystal.Success, crystal.Transferred, crystal.Code, crystal.Message);
                                }
                                else
                                {
                                    var item = await driver.DepositAsync(stack, quantity, mutationToken).ConfigureAwait(false);
                                    result = (item.Success, item.Transferred, item.Code, item.Message);
                                }
                                if (!result.Success)
                                {
                                    if (result.Code == "NoCapacity")
                                        return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Unchanged(result);
                                    if (result.Code == "DepositNotObserved" &&
                                        await PlayerVariantUnchangedAsync(stacks, depositedSinceScan, variantKey, stack.ItemId, mutationToken).ConfigureAwait(false))
                                        return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Unchanged(result);
                                    return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Indeterminate(result);
                                }
                                if (result.Transferred <= 0)
                                    return VerifiedMutationAttempt<(bool Success, int Transferred, string Code, string Message), RetainerVariantObservation>.Unchanged(result);
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
                        movementAttempted = previousMovementAttempted ||
                                            attempt.Evidence != VerifiedMutationEvidence.Unchanged;
                        if (!result.Success &&
                            attempt.Evidence == VerifiedMutationEvidence.Unchanged &&
                            result.Code is "NoCapacity" or "DepositNotObserved")
                        {
                            if (result.Code == "DepositNotObserved")
                                candidateCapacity[key] = 0;
                            journal.RecordWarning(
                                operationId,
                                result.Code == "NoCapacity" ? "DepositSkippedNoCapacity" : "DepositSkippedUnobserved",
                                result.Code == "NoCapacity"
                                    ? $"{candidate.RetainerName} reported no live capacity for {itemName}; its remaining quantity will try other candidates."
                                    : $"Deposit of {itemName} at {candidate.RetainerName} could not be observed and the player stack is provably unchanged; its remaining quantity will try other candidates.");
                            continue;
                        }
                        if (!result.Success)
                            throw new InvalidOperationException(result.Message);
                        if (result.Transferred == 0)
                        {
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
                            journal.RecordWarning(
                                operationId,
                                "DepositCapacityClamped",
                                $"{candidate.RetainerName} accepted only {result.Transferred:N0} of the requested {quantity:N0} {itemName}; later stacks may still merge into any compatible capacity that remains.");
                        }
                        stackRemaining -= result.Transferred;
                        depositedSinceScan[variantKey] = depositedSinceScan.GetValueOrDefault(variantKey) + result.Transferred;
                        transferred += result.Transferred;
                        if (legacyCrystalDeposit)
                            journal.RecordTransfer(operationId, stack.ItemId, candidate.RetainerId, result.Transferred, result.Code, result.Message);
                        else
                            journal.RecordDepositTransfer(operationId, key, stack.ItemId, stack.IsHighQuality, candidate.RetainerId, result.Transferred, result.Code, result.Message);
                    }
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                remainingCandidateIds.Remove(source.RetainerId);
                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
            await driver.CloseRetainerListAsync(token).ConfigureAwait(false);
            retainerSessionOpen = false;
            var missing = remaining.Values.Where(quantity => quantity > 0).Sum();
            journal.Transition(operationId,
                missing == 0 ? OperationStatuses.Succeeded : transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                missing == 0 ? "DepositComplete" : "DepositPartial",
                $"Stowed {transferred:N0} units; {missing:N0} remain on character.");
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
            activeCancellation?.Cancel();
        driver.CancelActive();
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

    private async Task<bool> PlayerVariantUnchangedAsync(
        IReadOnlyList<DalamudInventoryStack> scanStacks,
        IReadOnlyDictionary<string, int> depositedSinceScan,
        string variantKey,
        uint itemId,
        CancellationToken cancellationToken)
    {
        var scanned = scanStacks
            .Where(stack => OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality) == variantKey)
            .Sum(stack => stack.Quantity);
        var expected = scanned - depositedSinceScan.GetValueOrDefault(variantKey);
        var itemIds = new HashSet<uint> { itemId };
        var current = (await driver.ScanPlayerInventoryAsync(itemIds, cancellationToken).ConfigureAwait(false))
            .Concat(await driver.ScanPlayerCrystalsAsync(itemIds, cancellationToken).ConfigureAwait(false))
            .Where(stack => OperationJournal.VariantKey(stack.ItemId, stack.IsHighQuality) == variantKey)
            .Sum(stack => stack.Quantity);
        return current == expected;
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
