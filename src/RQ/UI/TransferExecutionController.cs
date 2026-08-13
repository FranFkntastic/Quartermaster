using RQ.Automation;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// Owns transfer submission, cancellation, asynchronous result observation, and
/// the durable refresh-recovery handshake. UI components request operations and
/// read this controller's status; they never own task lifecycle.
/// </summary>
internal sealed class TransferExecutionController
{
    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private Task? activeTransferTask;
    private PendingTransferPlanRecovery? pendingRecovery;
    private OwnerScope? inlineErrorOwner;
    private Guid? inlineErrorPlanId;

    public TransferExecutionController(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        OperationJournal journal,
        TransferCoordinator transfers,
        RetainerRefreshCoordinator retainerRefresh)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.journal = journal;
        this.transfers = transfers;
        this.retainerRefresh = retainerRefresh;
    }

    public string Status { get; private set; } = "No transfer has run.";
    public string InlineError { get; private set; } = string.Empty;

    public void ExecutePlan(Guid planId, bool allowCapacityRecovery = true)
    {
        var current = runtimeSnapshots.Current;
        var plan = current.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(current.Owner));
        if (plan is null)
            return;
        var rules = current.State.PlanItems
            .Where(rule => rule.StowagePlanId == plan.Id)
            .ToArray();
        if (ListingPlanEvaluator.HasUnknownLinkedDemand(current.State, current.Browser, current.Owner, plan.Id))
        {
            if (allowCapacityRecovery && retainerRefresh.StartForPlan(out var refreshRunId))
            {
                PersistRecovery(plan, refreshRunId);
                pendingRecovery = new(planId, refreshRunId);
                Status = "Verifying listing demand before executing the plan.";
                return;
            }
            ReportInlineError(retainerRefresh.Status.Length == 0
                ? "Listing demand could not be verified."
                : retainerRefresh.Status);
            return;
        }
        rules = ListingPlanEvaluator.ComposeRules(current.State, current.Browser, current.Owner, plan.Id).ToArray();
        var currentStowage = StowageEvaluator.BuildPlan(current.State, current.Browser, current.Owner, plan.Id);
        var currentRetrieval = BuildRetrievalEvaluation(current, rules);
        var currentBatch = TransferPlanEvaluation.BuildSurplusBatch(current, currentStowage);
        if (allowCapacityRecovery && TransferExecutionPolicy.RequiresCapacityRecovery(currentBatch))
        {
            if (retainerRefresh.StartForPlan(out var refreshRunId))
            {
                PersistRecovery(plan, refreshRunId);
                pendingRecovery = new(planId, refreshRunId);
                Status = "Refreshing retainer capacity before executing the plan.";
                return;
            }
            ReportInlineError(retainerRefresh.Status);
            return;
        }
        if (!allowCapacityRecovery && currentBatch.RequestedQuantity > 0 && currentBatch.PlannedQuantity == 0)
        {
            ReportInlineError("No owner retainer has capacity for the items to stow.");
            return;
        }
        var retrievalOperationId = currentRetrieval.NeededQuantity > 0
            ? journal.CreateTransferRetrieval(current.Owner, plan, rules).OperationId
            : null;
        var depositOperationId = currentBatch.PlannedQuantity > 0
            ? journal.CreateTransferDeposit(current.Owner, plan, currentBatch).OperationId
            : null;
        Start(transfers.ExecutePlanAsync(retrievalOperationId, depositOperationId));
    }

    public void Tick()
    {
        if (pendingRecovery is not { } pending ||
            !string.Equals(retainerRefresh.LastCompletedRunId, pending.RefreshRunId, StringComparison.Ordinal))
            return;
        pendingRecovery = null;
        if (retainerRefresh.LastRunSucceeded != true)
        {
            ReportInlineError(retainerRefresh.Status);
            state.Mutate(StateChangeKind.Recovery, document =>
            {
                if (document.TransferPlanRecovery is { } recovery &&
                    recovery.PlanId == pending.PlanId &&
                    string.Equals(recovery.RefreshRunId, pending.RefreshRunId, StringComparison.Ordinal))
                    recovery.FailureMessage = retainerRefresh.Status;
            });
            return;
        }
        var currentState = state.Snapshot();
        var recovery = currentState.TransferPlanRecovery;
        var currentPlan = currentState.StowagePlans.FirstOrDefault(plan =>
            plan.Id == pending.PlanId && plan.Owner.Matches(runtimeSnapshots.Current.Owner));
        if (recovery is null || currentPlan is null ||
            recovery.PlanId != currentPlan.Id ||
            recovery.PlanRevision != currentPlan.Revision ||
            !string.Equals(recovery.RefreshRunId, pending.RefreshRunId, StringComparison.Ordinal))
        {
            state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
            ReportInlineError(currentPlan is null
                ? "The pending Transfer Plan no longer exists."
                : "The Transfer Plan changed while evidence was refreshed; run the current plan when ready.");
            return;
        }
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
        ExecutePlan(pending.PlanId, false);
    }

    public void RetryRecovery(StowagePlan plan)
    {
        if (!retainerRefresh.StartForPlan(out var refreshRunId))
        {
            ReportInlineError(retainerRefresh.Status, updateStatus: false);
            return;
        }
        PersistRecovery(plan, refreshRunId);
        pendingRecovery = new(plan.Id, refreshRunId);
        InlineError = string.Empty;
        Status = "Refreshing retainer evidence before retrying the plan.";
    }

    public void DismissRecovery()
    {
        pendingRecovery = null;
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = null);
        InlineError = string.Empty;
    }

    public void EnsureInlineErrorContext(OwnerScope owner, Guid planId)
    {
        if (inlineErrorPlanId == planId && inlineErrorOwner?.Matches(owner) == true)
            return;
        InlineError = string.Empty;
        inlineErrorOwner = owner with { };
        inlineErrorPlanId = planId;
    }

    public void ClearInlineErrorContext()
    {
        InlineError = string.Empty;
        inlineErrorOwner = null;
        inlineErrorPlanId = null;
    }

    public void ReportInlineError(string message, bool updateStatus = false)
    {
        InlineError = message;
        if (updateStatus)
            Status = message;
    }

    public void ClearInlineError() => InlineError = string.Empty;

    public void Start(Task<TransferExecutionResult> transfer) => _ = ObserveAsync(transfer);

    public void CancelActive()
    {
        if (activeTransferTask is null && !transfers.IsRunning)
            return;
        try
        {
            transfers.CancelActive();
        }
        catch
        {
            // Cancellation is best-effort during plugin shutdown.
        }
    }

    public bool CancelAndWait(TimeSpan timeout)
    {
        var task = Volatile.Read(ref activeTransferTask);
        if (task is null && !transfers.IsRunning)
            return true;
        try
        {
            transfers.CancelActive();
        }
        catch
        {
            // Cancellation is best-effort during plugin shutdown.
        }
        if (task is null)
            return !transfers.IsRunning;
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            return true;
        }
    }

    private void PersistRecovery(StowagePlan plan, string refreshRunId) =>
        state.Mutate(StateChangeKind.Recovery, document => document.TransferPlanRecovery = new()
        {
            Owner = runtimeSnapshots.Current.Owner with { },
            PlanId = plan.Id,
            PlanRevision = plan.Revision,
            RefreshRunId = refreshRunId,
            RequestedAtUtc = DateTime.UtcNow,
        });

    private async Task ObserveAsync(Task<TransferExecutionResult> transfer)
    {
        activeTransferTask = transfer;
        try
        {
            var result = await transfer;
            Status = result.Message;
        }
        catch (Exception exception)
        {
            Status = $"Transfer failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(activeTransferTask, transfer))
                activeTransferTask = null;
        }
    }

    private static RetrievalPlan BuildRetrievalEvaluation(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<TargetPlanItem> rules)
    {
        var playerCounts = runtime.PlayerStorage.Bags
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        return RestockPlanner.Build(
            rules,
            playerCounts,
            runtime.Retainers,
            runtime.Owner,
            runtime.CapturedAtUtc,
            runtime.Browser);
    }
}
