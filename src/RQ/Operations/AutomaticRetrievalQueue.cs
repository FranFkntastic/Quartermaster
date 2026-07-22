using RQ.Domain;

namespace RQ.Operations;

public interface IRetrievalOperationExecutor
{
    bool CanStart { get; }
    Task<TransferExecutionResult> ExecuteRetrievalAsync(string operationId, CancellationToken cancellationToken = default);
    void CancelActive();
}

public sealed class AutomaticRetrievalQueue : IDisposable
{
    private readonly OperationJournal journal;
    private readonly IRetrievalOperationExecutor executor;
    private readonly Func<OwnerScope> currentOwner;
    private readonly CancellationTokenSource lifetime = new();
    private Task<TransferExecutionResult>? activeTask;
    private bool stopping;
    private bool disposed;

    public AutomaticRetrievalQueue(OperationJournal journal, IRetrievalOperationExecutor executor, Func<OwnerScope> currentOwner)
    {
        this.journal = journal;
        this.executor = executor;
        this.currentOwner = currentOwner;
    }

    public string? ActiveOperationId { get; private set; }
    public string LastMessage { get; private set; } = "No automatic retrieval has run.";

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
            return;
        }
        if (!executor.CanStart)
            return;
        var owner = currentOwner();
        if (!owner.HasStableIdentity || journal.NextAutomaticRetrieval(owner) is not { } operation)
            return;
        ActiveOperationId = operation.OperationId;
        activeTask = executor.ExecuteRetrievalAsync(operation.OperationId, lifetime.Token);
    }

    public bool CancelAndWait(TimeSpan timeout)
    {
        stopping = true;
        var task = activeTask;
        if (task is null)
        {
            lifetime.Cancel();
            return true;
        }
        if (task.IsCompleted)
        {
            Observe(task);
            activeTask = null;
            ActiveOperationId = null;
            lifetime.Cancel();
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
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { LastMessage = "Automatic retrieval cancelled during plugin disposal."; }
        catch (Exception exception) { LastMessage = $"Automatic retrieval failed: {exception.Message}"; }
    }
}
