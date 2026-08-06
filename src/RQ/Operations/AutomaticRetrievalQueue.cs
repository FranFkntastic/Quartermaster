using RQ.Automation;
using RQ.Domain;

namespace RQ.Operations;

public interface IRetrievalOperationExecutor
{
    bool CanStart { get; }
    Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default);
    Task<TransferExecutionResult> ExecuteDepositAsync(string operationId, CancellationToken cancellationToken = default);
    void CancelActive();
}

public sealed class AutomaticRetrievalQueue : IDisposable
{
    private readonly OperationJournal journal;
    private readonly IRetrievalOperationExecutor executor;
    private readonly Func<OwnerScope> currentOwner;
    private readonly AutoRetainerSuppression? autoRetainer;
    private readonly CancellationTokenSource lifetime = new();
    private Task<TransferExecutionResult>? activeTask;
    private AutoRetainerSuppression.Scope? suppressionScope;
    private bool stopping;
    private bool disposed;

    public AutomaticRetrievalQueue(
        OperationJournal journal,
        IRetrievalOperationExecutor executor,
        Func<OwnerScope> currentOwner,
        AutoRetainerSuppression? autoRetainer = null)
    {
        this.journal = journal;
        this.executor = executor;
        this.currentOwner = currentOwner;
        this.autoRetainer = autoRetainer;
    }

    public string? ActiveOperationId { get; private set; }
    public string LastMessage { get; private set; } = "No automatic transfer has run.";

    public void Tick()
    {
        if (stopping)
            return;
        if (activeTask is { } completed)
        {
            if (!completed.IsCompleted)
                return;
            Observe(completed);
            activeTask = null;
            ActiveOperationId = null;
            ReleaseAutoRetainer();
            return;
        }
        if (!executor.CanStart)
            return;
        var owner = currentOwner();
        if (!owner.HasStableIdentity || journal.NextAutomaticOperation(owner) is not { } operation)
            return;
        if (!TrySuppressAutoRetainer())
            return;
        ActiveOperationId = operation.OperationId;
        try
        {
            activeTask = operation.Kind == OperationKinds.Deposit
                ? executor.ExecuteDepositAsync(operation.OperationId, lifetime.Token)
                : executor.ExecuteRetrievalAsync(operation.OperationId, lifetime.Token);
        }
        catch
        {
            ActiveOperationId = null;
            ReleaseAutoRetainer();
            throw;
        }
    }

    public bool CancelAndWait(TimeSpan timeout)
    {
        stopping = true;
        var task = activeTask;
        if (task is null)
        {
            lifetime.Cancel();
            ReleaseAutoRetainer();
            return true;
        }
        if (task.IsCompleted)
        {
            Observe(task);
            activeTask = null;
            ActiveOperationId = null;
            lifetime.Cancel();
            ReleaseAutoRetainer();
            return true;
        }
        lifetime.Cancel();
        try { executor.CancelActive(); }
        catch { }
        try
        {
            var completed = task.Wait(timeout);
            if (completed)
                Observe(task);
            return completed;
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            return true;
        }
        finally
        {
            // The executor's own nested suppression scope keeps AutoRetainer held
            // until any in-flight movement finishes, even when this release wins
            // the race against a timed-out task.
            ReleaseAutoRetainer();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CancelAndWait(TimeSpan.FromSeconds(2));
        lifetime.Dispose();
    }

    private void Observe(Task<TransferExecutionResult> task)
    {
        try { LastMessage = task.GetAwaiter().GetResult().Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { LastMessage = "Automatic transfer cancelled during plugin disposal."; }
        catch (Exception exception) { LastMessage = $"Automatic transfer failed: {exception.Message}"; }
    }

    private bool TrySuppressAutoRetainer()
    {
        if (autoRetainer is null || !autoRetainer.IsAvailable)
            return true;
        try
        {
            if (autoRetainer.IsBusy)
                return false;
            suppressionScope = autoRetainer.Acquire();
            return true;
        }
        catch (Exception exception)
        {
            LastMessage = $"Automatic transfer is waiting for AutoRetainer coordination: {exception.Message}";
            return false;
        }
    }

    private void ReleaseAutoRetainer()
    {
        var scope = suppressionScope;
        suppressionScope = null;
        if (scope is null)
            return;
        scope.Dispose();
        if (scope.RestoreFailure is { } failure)
            LastMessage = $"Transfer ended, but AutoRetainer suppression could not be restored: {failure}";
    }
}
