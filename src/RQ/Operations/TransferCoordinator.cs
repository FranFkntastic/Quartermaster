using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Automation;
using RQ.Inventory;
using RQ.Planning;

namespace RQ.Operations;

public sealed record RetainerRouteCandidate(ulong RetainerId, string RetainerName, DateTime ObservedAtUtc);
public sealed record RetrievalResult(bool Success, int Transferred, string Code, string Message);
public sealed record TransferExecutionResult(bool Started, string Message);

public interface IRetainerTransferDriver
{
    Task RequireRetainerListAsync(CancellationToken cancellationToken);
    Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken);
    Task OpenInventoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken);
    Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken);
    Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken);
    Task CloseRetainerAsync(CancellationToken cancellationToken);
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
    private readonly Func<bool> clearRetrievalPlansAsActioned;
    private readonly object activeGate = new();
    private CancellationTokenSource? activeCancellation;
    private string? activeOperationId;
    private int running;

    public TransferCoordinator(
        OperationJournal journal,
        IRetainerTransferDriver driver,
        RetainerCacheRepository cache,
        Func<OwnerScope> currentOwner,
        Func<IReadOnlyDictionary<uint, int>> playerInventory,
        AutomationLease? automation = null,
        Func<DateTime>? utcNow = null,
        Func<bool>? clearRetrievalPlansAsActioned = null)
    {
        this.journal = journal;
        this.driver = driver;
        this.cache = cache;
        this.currentOwner = currentOwner;
        this.playerInventory = playerInventory;
        this.automation = automation ?? new AutomationLease();
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.clearRetrievalPlansAsActioned = clearRetrievalPlansAsActioned ?? (() => false);
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public bool CanStart => !automation.IsHeld && !IsRunning;

    public async Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!automation.TryAcquire("retainer transfer", out var lease))
            return new(false, $"Automation is busy with {automation.Holder}.");
        using (lease)
        {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(false, "Another retainer transfer is already running.");
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(operationId, linkedCancellation);
        var token = linkedCancellation.Token;
        var retainerOpen = false;
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
                Enabled = true,
            }).ToArray();
            var plan = RestockPlanner.Build(rows, playerInventory(), cache.Snapshot(), owner, utcNow());
            var remaining = plan.Lines.GroupBy(line => line.ItemId).ToDictionary(group => group.Key, group => group.Sum(line => line.NeededQuantity));
            journal.Transition(
                operationId,
                OperationStatuses.Running,
                "ExecutionStarted",
                operation.ExecuteImmediately ? "Automatic live retrieval verification started." : "Reviewed live retrieval verification started.");
            if (clearRetrievalPlansAsActioned())
                journal.ClearSatisfiedRetrievalPlanItems(operationId, remaining.Where(entry => entry.Value <= 0).Select(entry => entry.Key).ToHashSet());
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
            var candidates = plan.Lines.SelectMany(line => line.Candidates.Select(candidate => (line.ItemId, Candidate: candidate)))
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
            token.ThrowIfCancellationRequested();
            foreach (var candidate in candidates)
            {
                await driver.OpenRetainerAsync(candidate.Route, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                retainerOpen = true;
                await driver.OpenInventoryAsync(token).ConfigureAwait(false);
                var wanted = candidate.ItemIds.Where(itemId => remaining.GetValueOrDefault(itemId) > 0).ToHashSet();
                foreach (var stack in await driver.ScanRetainerAsync(wanted, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    var quantity = Math.Min(stack.Quantity, remaining.GetValueOrDefault(stack.ItemId));
                    if (quantity <= 0)
                        continue;
                    journal.ArmCacheInvalidation(operationId, candidate.Route.RetainerId, operation.Owner);
                    movementAttempted = true;
                    var result = await driver.RetrieveAsync(stack, quantity, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!result.Success)
                        throw new InvalidOperationException(result.Message);
                    remaining[stack.ItemId] -= result.Transferred;
                    transferred += result.Transferred;
                    RecordInvalidation(operationId, candidate.Route.RetainerId);
                    journal.RecordTransfer(
                        operationId,
                        stack.ItemId,
                        candidate.Route.RetainerId,
                        result.Transferred,
                        result.Code,
                        result.Message,
                        clearRetrievalPlansAsActioned() && remaining[stack.ItemId] <= 0);
                    journal.ResolveCacheInvalidation(operationId, candidate.Route.RetainerId);
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
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
            MarkCancelled(operationId);
            return new(true, "Transfer cancelled; any operation that began live movement is indeterminate.");
        }
        catch (Exception exception)
        {
            if (retainerOpen)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            var operation = journal.Get(operationId);
            if (operation is { Status: not OperationStatuses.Failed } && !OperationStatuses.IsTerminal(operation.Status))
            {
                if (movementAttempted)
                    InvalidateOwnerEvidence(operationId, operation.Owner);
                journal.Transition(
                    operationId,
                    movementAttempted ? OperationStatuses.Indeterminate : OperationStatuses.Failed,
                    movementAttempted ? "BookkeepingIndeterminate" : "ExecutionFailed",
                    movementAttempted ? $"Live movement may have occurred, but durable bookkeeping failed: {exception.Message}" : exception.Message);
            }
            return new(true, exception.Message);
        }
        finally
        {
            ClearActive(linkedCancellation);
            Volatile.Write(ref running, 0);
        }
        }
    }

    public async Task<TransferExecutionResult> ExecuteDepositAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!automation.TryAcquire("retainer transfer", out var lease))
            return new(false, $"Automation is busy with {automation.Holder}.");
        using (lease)
        {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(false, "Another retainer transfer is already running.");
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(operationId, linkedCancellation);
        var token = linkedCancellation.Token;
        var retainerOpen = false;
        var movementAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var operation = journal.Get(operationId) ?? throw new KeyNotFoundException($"Operation '{operationId}' was not found.");
            if (operation.Kind != OperationKinds.Deposit)
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Kind}', not deposit.");
            if (operation.Status != OperationStatuses.Accepted)
                throw new InvalidOperationException($"Operation '{operationId}' is '{operation.Status}', not accepted.");
            var owner = currentOwner();
            if (!operation.Owner.HasStableIdentity || !owner.HasStableIdentity || !operation.Owner.Matches(owner))
                throw new InvalidOperationException("Operation owner no longer matches current character.");
            if (operation.DepositCandidates.Count == 0 || operation.Lines.Count == 0)
                throw new InvalidOperationException("Deposit operation has no persisted reviewed authorization.");
            journal.Transition(operationId, OperationStatuses.Running, "DepositStarted", "Live crystal deposit verification started.");
            var remaining = operation.Lines.ToDictionary(line => line.ItemId, line => line.TargetQuantity);
            var transferred = 0;
            await driver.RequireRetainerListAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var source in operation.DepositCandidates)
            {
                var candidateCapacity = source.CapacityByItem.ToDictionary(entry => entry.Key, entry => entry.Value);
                var candidate = new RetainerRouteCandidate(source.RetainerId, source.RetainerName, source.ObservedAtUtc);
                await driver.OpenRetainerAsync(candidate, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                retainerOpen = true;
                await driver.OpenInventoryAsync(token).ConfigureAwait(false);
                var wanted = remaining.Where(entry => entry.Value > 0 && candidateCapacity.GetValueOrDefault(entry.Key) > 0).Select(entry => entry.Key).ToHashSet();
                foreach (var stack in await driver.ScanPlayerCrystalsAsync(wanted, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    var authorized = Math.Min(remaining.GetValueOrDefault(stack.ItemId), candidateCapacity.GetValueOrDefault(stack.ItemId));
                    if (authorized <= 0)
                        continue;
                    journal.ArmCacheInvalidation(operationId, candidate.RetainerId, operation.Owner);
                    movementAttempted = true;
                    var result = await driver.DepositCrystalAsync(stack, Math.Min(stack.Quantity, authorized), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!result.Success)
                        throw new InvalidOperationException(result.Message);
                    if (result.Transferred == 0)
                        continue;
                    remaining[stack.ItemId] -= result.Transferred;
                    candidateCapacity[stack.ItemId] -= result.Transferred;
                    transferred += result.Transferred;
                    RecordInvalidation(operationId, candidate.RetainerId);
                    journal.RecordTransfer(operationId, stack.ItemId, candidate.RetainerId, result.Transferred, result.Code, result.Message);
                    journal.ResolveCacheInvalidation(operationId, candidate.RetainerId);
                }
                await driver.CloseRetainerAsync(token).ConfigureAwait(false);
                retainerOpen = false;
                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }
            var missing = remaining.Values.Where(quantity => quantity > 0).Sum();
            journal.Transition(operationId,
                missing == 0 ? OperationStatuses.Succeeded : transferred > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                missing == 0 ? "DepositComplete" : "DepositPartial",
                $"Deposited {transferred:N0} elemental units; {missing:N0} remain on character.");
            return new(true, missing == 0 ? "Deposit completed." : "Deposit completed with remaining units.");
        }
        catch (OperationCanceledException)
        {
            MarkCancelled(operationId);
            return new(true, "Transfer cancelled; any operation that began live movement is indeterminate.");
        }
        catch (Exception exception)
        {
            if (retainerOpen)
            {
                try { await driver.CloseRetainerAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            var operation = journal.Get(operationId);
            if (operation is not null && !OperationStatuses.IsTerminal(operation.Status))
            {
                if (movementAttempted)
                    InvalidateOwnerEvidence(operationId, operation.Owner);
                journal.Transition(
                    operationId,
                    movementAttempted ? OperationStatuses.Indeterminate : OperationStatuses.Failed,
                    movementAttempted ? "BookkeepingIndeterminate" : "DepositFailed",
                    movementAttempted ? $"Live movement may have occurred, but durable bookkeeping failed: {exception.Message}" : exception.Message);
            }
            return new(true, exception.Message);
        }
        finally
        {
            ClearActive(linkedCancellation);
            Volatile.Write(ref running, 0);
        }
        }
    }

    public void CancelActive()
    {
        string? operationId;
        lock (activeGate)
        {
            activeCancellation?.Cancel();
            operationId = activeOperationId;
        }
        driver.CancelActive();
        if (operationId is null)
            return;
        MarkCancelled(operationId);
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
                InvalidateOwnerEvidence(operationId, operation.Owner);
                journal.Transition(operationId, OperationStatuses.Indeterminate, "CancelledDuringExecution", "Transfer was cancelled during live movement; involved cache evidence was invalidated.");
            }
        }
        catch (InvalidOperationException) when (journal.Get(operationId) is { Status: var status } && OperationStatuses.IsTerminal(status))
        {
        }
    }

    private void RecordInvalidation(string operationId, ulong retainerId)
    {
        var invalidation = cache.Invalidate(retainerId);
        if (!invalidation.Persisted)
            throw new IOException($"Retainer {retainerId} cache invalidation did not persist: {invalidation.Error}");
    }

    private void SetActive(string operationId, CancellationTokenSource cancellation)
    {
        lock (activeGate)
        {
            activeOperationId = operationId;
            activeCancellation = cancellation;
        }
    }

    private void ClearActive(CancellationTokenSource cancellation)
    {
        lock (activeGate)
        {
            if (ReferenceEquals(activeCancellation, cancellation))
            {
                activeCancellation = null;
                activeOperationId = null;
            }
        }
        cancellation.Dispose();
    }

    private void InvalidateOwnerEvidence(string operationId, OwnerScope owner)
    {
        foreach (var retainer in cache.Snapshot().Values.Where(retainer => retainer.Owner.Matches(owner)).ToArray())
        {
            journal.ArmCacheInvalidation(operationId, retainer.RetainerId, owner);
            var result = cache.Invalidate(retainer.RetainerId);
            if (result.Persisted)
                journal.ResolveCacheInvalidation(operationId, retainer.RetainerId);
        }
    }
}
