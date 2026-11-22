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
    private readonly Action<string>? warn;
    private readonly Func<DateTime> utcNow;
    private readonly CancellationTokenSource lifetime = new();
    private DateTime nextExpirySweepUtc = DateTime.MinValue;
    private Task<TransferExecutionResult>? activeTask;
    private AutoRetainerSuppression.Scope? suppressionScope;
    private bool stopping;
    private bool disposed;

    public AutomaticRetrievalQueue(
        OperationJournal journal,
        IRetrievalOperationExecutor executor,
        Func<OwnerScope> currentOwner,
        AutoRetainerSuppression? autoRetainer = null,
        Action<string>? warn = null,
        Func<DateTime>? utcNow = null)
    {
        this.journal = journal;
        this.executor = executor;
        this.currentOwner = currentOwner;
        this.autoRetainer = autoRetainer;
        this.warn = warn;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Backstop for externally requested automatic operations: one that has not started within this window of
    /// its submission is cancelled instead of running later (for example on the owner's next login, long after
    /// the requester stopped waiting). Requesters time out well inside it and cancel explicitly.
    /// </summary>
    public static readonly TimeSpan RequestedOperationStartWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromSeconds(10);

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
        ExpireUnstartedRequests();
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
            // Requested stowage deposits (delivered-stow IPC) execute through the same deposit path.
            activeTask = operation.Kind is OperationKinds.Deposit or OperationKinds.QuickDeposit or OperationKinds.StowageSurplus
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

    private void ExpireUnstartedRequests()
    {
        var now = utcNow();
        if (now < nextExpirySweepUtc)
            return;
        nextExpirySweepUtc = now + ExpirySweepInterval;
        try
        {
            foreach (var expired in journal.ExpireUnstartedRequestedOperations(RequestedOperationStartWindow, ActiveOperationId))
                warn?.Invoke($"[Quartermaster] ExpiredBeforeStart: {expired.Kind} operation {expired.OperationId} (request {expired.RequestId}) " +
                             $"did not start within the {RequestedOperationStartWindow.TotalMinutes:N0}-minute start window of its submission; cancelled.");
        }
        catch (Exception exception)
        {
            warn?.Invoke($"[Quartermaster] Requested-operation expiry sweep failed: {exception.Message}");
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
