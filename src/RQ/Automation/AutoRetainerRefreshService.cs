using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Inventory;

namespace RQ.Automation;

public sealed class AutoRetainerRefreshService : IDisposable
{
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);
    private const string Consumer = "Quartermaster";
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly RetainerCaptureService captures;
    private readonly IRetainerAutomationSession session;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly AutomationLease automation;
    private readonly Func<int> countAvailableRetainers;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ActiveTaskTracker activeTasks = new();
    private readonly AutoRetainerPostprocessState postprocess = new();
    private IDisposable? automationLease;
    private AutoRetainerRefreshPhase phase;
    private int expected;
    private int processed;
    private bool automaticRefreshPending = true;
    private volatile bool disposed;

    public AutoRetainerRefreshService(
        IFramework framework,
        IPluginLog log,
        RetainerCaptureService captures,
        IRetainerAutomationSession session,
        IAutoRetainerIpc autoRetainer,
        AutomationLease? automation = null)
        : this(framework, log, captures, session, autoRetainer, automation, null)
    {
    }

    internal AutoRetainerRefreshService(
        IFramework framework,
        IPluginLog log,
        RetainerCaptureService captures,
        IRetainerAutomationSession session,
        IAutoRetainerIpc autoRetainer,
        AutomationLease? automation,
        Func<int>? countAvailableRetainers)
    {
        this.framework = framework;
        this.log = log;
        this.captures = captures;
        this.session = session;
        this.autoRetainer = autoRetainer;
        this.automation = automation ?? new AutomationLease();
        this.countAvailableRetainers = countAvailableRetainers ?? DalamudRetainerInventory.CountAvailableRetainers;
    }

    public void Register()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        autoRetainer.Register(new(DrawButton, OnAdditionalTask, OnReady));
    }

    public bool IsRefreshing => phase is AutoRetainerRefreshPhase.Refreshing or AutoRetainerRefreshPhase.PassivePostprocess;
    public bool IsQueued => phase is AutoRetainerRefreshPhase.Preparing or AutoRetainerRefreshPhase.Queued;
    public string Status { get; private set; } = "Retainer refresh has not run.";

    public bool IsAvailable => autoRetainer.IsAvailable;

    public void TickAutomatic(bool stockBrowserVisible)
    {
        if (disposed ||
            !automaticRefreshPending ||
            !stockBrowserVisible ||
            phase != AutoRetainerRefreshPhase.Idle ||
            !IsAvailable)
            return;

        if (autoRetainer.IsBusy)
        {
            // AutoRetainer's normal postprocess callbacks will refresh the cache
            // without Quartermaster queueing a second pass behind it.
            automaticRefreshPending = false;
            return;
        }

        // Automatic refresh is deliberately opportunistic: it joins an already
        // open retainer-list context, but never opens UI or starts travel merely
        // because the browser is visible.
        if (!session.IsRetainerListReady)
            return;

        if (Start())
            automaticRefreshPending = false;
    }

    public bool Start()
    {
        if (disposed)
            return false;
        if (phase != AutoRetainerRefreshPhase.Idle)
        {
            Status = "Retainer refresh is already queued or running.";
            return false;
        }
        if (!IsAvailable)
        {
            Status = "AutoRetainer is unavailable.";
            return false;
        }
        if (!automation.TryAcquire("retainer refresh", out automationLease))
        {
            Status = $"Automation is busy with {automation.Holder}.";
            return false;
        }
        if (autoRetainer.IsBusy)
        {
            phase = AutoRetainerRefreshPhase.Queued;
            Status = "Retainer refresh queued behind AutoRetainer.";
            return true;
        }
        phase = AutoRetainerRefreshPhase.Preparing;
        Status = session.IsRetainerListReady ? "Preparing retainer refresh." : "Opening retainer list.";
        Track(() => PrepareRefreshAsync(lifetime.Token));
        return true;
    }

    private async Task PrepareRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
            if (!list.Success && string.Equals(list.Code, "RetainerInteractionAlreadyOpen", StringComparison.Ordinal))
            {
                if (autoRetainer.IsBusy)
                {
                    await framework.RunOnTick(() => QueuePreparedRefresh(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                else
                {
                    var closed = await session.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
                    if (!closed.Success)
                        throw new InvalidOperationException($"{closed.Code}: {closed.Message}");
                    list = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (!list.Success)
                throw new InvalidOperationException($"{list.Code}: {list.Message}");
            await framework.RunOnTick(() => QueuePreparedRefresh(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await FailAsync("Unable to prepare AutoRetainer refresh.", exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private void QueuePreparedRefresh(CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        if (phase == AutoRetainerRefreshPhase.Preparing)
            phase = AutoRetainerRefreshPhase.Queued;
        Status = "Retainer refresh queued.";
    }

    private void DrawButton()
    {
        if (disposed)
            return;
        try
        {
            var disabled = phase != AutoRetainerRefreshPhase.Idle ||
                           (automation.IsHeld && automation.Holder is not ("retainer refresh" or "retainer postprocess"));
            if (disabled)
                ImGui.BeginDisabled();
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("QuartermasterRefresh", FontAwesomeIcon.BookOpen))
                Start();
            if (disabled)
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh all Quartermaster retainer inventory caches.");
            if (phase == AutoRetainerRefreshPhase.Queued)
            {
                expected = countAvailableRetainers();
                processed = 0;
                if (expected == 0)
                {
                    phase = AutoRetainerRefreshPhase.Idle;
                    ReleaseAutomationLease();
                    Status = "No available retainers were found.";
                    return;
                }
                phase = AutoRetainerRefreshPhase.Refreshing;
                Status = $"Refreshing retainers: 0/{expected}.";
                autoRetainer.QueueRetainerListTask(Consumer);
            }
        }
        catch (Exception exception)
        {
            FailOnFramework("Unable to start AutoRetainer refresh.", exception);
        }
    }

    private void OnAdditionalTask(string retainerName)
    {
        Track(() => RequestPostprocessAsync(retainerName, lifetime.Token));
    }

    private void OnReady(string consumer, string retainerName)
    {
        if (consumer == Consumer)
            Track(() => CaptureCurrentAsync(retainerName, lifetime.Token));
    }

    private async Task RequestPostprocessAsync(string retainerName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                if (postprocess.HasOutstanding)
                    return;
                var partOfFullRefresh = phase == AutoRetainerRefreshPhase.Refreshing;
                if (!partOfFullRefresh)
                {
                    if (phase is AutoRetainerRefreshPhase.Preparing or AutoRetainerRefreshPhase.Queued)
                        return;
                    if (phase != AutoRetainerRefreshPhase.Idle)
                        return;
                    if (!automation.TryAcquire("retainer postprocess", out automationLease))
                    {
                        Status = $"Skipped AutoRetainer postprocess because automation is busy with {automation.Holder}.";
                        return;
                    }
                    phase = AutoRetainerRefreshPhase.PassivePostprocess;
                }
                if (!postprocess.TryBegin(retainerName, partOfFullRefresh, out var request))
                    return;
                try
                {
                    autoRetainer.RequestPostprocess(Consumer);
                }
                catch
                {
                    postprocess.TryComplete(request);
                    if (!partOfFullRefresh)
                    {
                        phase = AutoRetainerRefreshPhase.Idle;
                        ReleaseAutomationLease();
                    }
                    throw;
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await FailAsync("Unable to request AutoRetainer postprocess.", exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CaptureCurrentAsync(string retainerName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                return postprocess.TryMarkReady(retainerName, out var ready) ? ready : null;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (request is null)
                return;
            var menu = await session.WaitForCurrentRetainerMenuAsync(cancellationToken).ConfigureAwait(false);
            if (!menu.Success)
                throw new InvalidOperationException($"{menu.Code}: {menu.Message}");
            var isCurrent = await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                return postprocess.IsCurrent(request);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!isCurrent)
                return;
            var opened = await session.OpenInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (!opened.Success)
                throw new InvalidOperationException($"{opened.Code}: {opened.Message}");
            var captureWait = await WaitForCaptureSessionAsync(retainerName, cancellationToken).ConfigureAwait(false);
            isCurrent = await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                return postprocess.IsCurrent(request);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!isCurrent)
                return;
            var closed = await session.CloseInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (!closed.Success)
                throw new InvalidOperationException($"{closed.Code}: {closed.Message}");
            await WaitForPersistedCaptureAsync(captureWait, retainerName, cancellationToken).ConfigureAwait(false);
            await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                if (!postprocess.IsCurrent(request))
                    return;
                if (request.PartOfFullRefresh)
                {
                    processed++;
                    Status = $"Refreshing retainers: {processed}/{expected}.";
                }
                else
                {
                    Status = $"Refreshed {retainerName} during AutoRetainer postprocess.";
                }
                autoRetainer.FinishPostprocess();
                postprocess.TryComplete(request);
                if (request.PartOfFullRefresh && processed >= expected)
                {
                    phase = AutoRetainerRefreshPhase.Idle;
                    ReleaseAutomationLease();
                    Status = $"Retainer refresh complete: {processed}/{expected}.";
                }
                else if (!request.PartOfFullRefresh)
                {
                    phase = AutoRetainerRefreshPhase.Idle;
                    ReleaseAutomationLease();
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await FailAsync($"Refresh failed for {retainerName}.", exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForPersistedCaptureAsync(CaptureWaitSnapshot captureWait, string retainerName, CancellationToken cancellationToken)
    {
        var expectedRetainerId = captureWait.Session?.RetainerId
            ?? throw new ArgumentException("Capture wait requires a stable retainer session.", nameof(captureWait));
        for (var attempt = 0; attempt < 180; attempt++)
        {
            ThrowIfStopped(cancellationToken);
            var receipts = await framework.RunOnTick(
                () => captures.ReceiptsAfter(captureWait.Checkpoint),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var receipt in receipts.Where(receipt => receipt.RetainerId == expectedRetainerId))
            {
                if (receipt.Outcome == CaptureOutcome.Persisted)
                    return;
                throw new InvalidOperationException($"Capture for {retainerName} was rejected ({receipt.Outcome}): {receipt.Message}");
            }
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Timed out waiting for persisted capture of {retainerName} ({expectedRetainerId}).");
    }

    private async Task<CaptureWaitSnapshot> WaitForCaptureSessionAsync(string retainerName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            ThrowIfStopped(cancellationToken);
            var snapshot = await framework.RunOnTick(
                () => captures.GetWaitSnapshot(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (snapshot.Session is not null)
                return snapshot;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Timed out waiting for a stable capture session after opening {retainerName}'s inventory.");
    }

    private async Task FailAsync(string message, Exception exception, CancellationToken cancellationToken)
    {
        if (disposed || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            await framework.RunOnTick(() =>
            {
                if (!disposed)
                    FailOnFramework(message, exception);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (disposed || cancellationToken.IsCancellationRequested)
        {
            log.Debug(failure, "AutoRetainer refresh stopped during plugin disposal.");
        }
    }

    private void FailOnFramework(string message, Exception exception)
    {
        if (disposed)
            return;
        log.Error(exception, message);
        var readyRequest = postprocess.Cancel();
        phase = AutoRetainerRefreshPhase.Idle;
        ReleaseAutomationLease();
        Status = $"{message} {exception.Message}";
        if (readyRequest is not null)
        {
            try { session.CancelActive(); }
            catch (Exception cleanupException) { log.Warning(cleanupException, "Unable to close Quartermaster retainer UI after refresh failure."); }
            try { autoRetainer.FinishPostprocess(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        activeTasks.Stop();
        lifetime.Cancel();
        if (!activeTasks.Wait(DisposeJoinTimeout))
            log.Warning("Timed out waiting for AutoRetainer refresh tasks to stop during plugin disposal.");
        var readyRequest = postprocess.Cancel();
        phase = AutoRetainerRefreshPhase.Disposed;
        if (readyRequest is not null)
        {
            try { session.CancelActive(); }
            catch { }
            try { autoRetainer.FinishPostprocess(); }
            catch { }
        }
        autoRetainer.Dispose();
        ReleaseAutomationLease();
        lifetime.Dispose();
    }

    private void Track(Func<Task> taskFactory) => activeTasks.TryRun(() => ObserveAsync(taskFactory()));

    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { log.Error(exception, "Unhandled AutoRetainer refresh task failure."); }
    }

    private void ReleaseAutomationLease()
    {
        automationLease?.Dispose();
        automationLease = null;
    }

    private void ThrowIfStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed)
            throw new OperationCanceledException(cancellationToken);
    }
}

internal enum AutoRetainerRefreshPhase
{
    Idle,
    Preparing,
    Queued,
    Refreshing,
    PassivePostprocess,
    Disposed,
}

internal sealed record AutoRetainerPostprocessRequest(long Sequence, string RetainerName, bool PartOfFullRefresh, bool Ready);

internal sealed class AutoRetainerPostprocessState
{
    private AutoRetainerPostprocessRequest? current;
    private long sequence;

    public bool HasOutstanding => current is not null;

    public bool TryBegin(string retainerName, bool partOfFullRefresh, out AutoRetainerPostprocessRequest request)
    {
        if (current is not null)
        {
            request = current;
            return false;
        }
        request = new(++sequence, retainerName, partOfFullRefresh, false);
        current = request;
        return true;
    }

    public bool TryMarkReady(string retainerName, out AutoRetainerPostprocessRequest? request)
    {
        if (current is null || current.Ready || !string.Equals(current.RetainerName, retainerName, StringComparison.OrdinalIgnoreCase))
        {
            request = null;
            return false;
        }
        request = current with { Ready = true };
        current = request;
        return true;
    }

    public bool IsCurrent(AutoRetainerPostprocessRequest request) =>
        current is { Ready: true } active && active.Sequence == request.Sequence;

    public bool TryComplete(AutoRetainerPostprocessRequest request)
    {
        if (current?.Sequence != request.Sequence)
            return false;
        current = null;
        return true;
    }

    public AutoRetainerPostprocessRequest? Cancel()
    {
        var ready = current is { Ready: true } ? current : null;
        current = null;
        return ready;
    }
}

internal sealed class ActiveTaskTracker
{
    private readonly object gate = new();
    private readonly HashSet<Task> active = [];
    private bool stopping;

    public bool TryRun(Func<Task> taskFactory)
    {
        Task task;
        lock (gate)
        {
            if (stopping)
                return false;
            task = taskFactory();
            active.Add(task);
        }
        _ = RemoveWhenCompleteAsync(task);
        return true;
    }

    public void Stop()
    {
        lock (gate)
            stopping = true;
    }

    public bool Wait(TimeSpan timeout)
    {
        Task[] snapshot;
        lock (gate)
            snapshot = active.ToArray();
        if (snapshot.Length == 0)
            return true;
        try
        {
            return Task.WhenAll(snapshot).Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private async Task RemoveWhenCompleteAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (gate)
                active.Remove(task);
        }
    }
}
