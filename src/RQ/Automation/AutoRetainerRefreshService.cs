using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Inventory;

namespace RQ.Automation;

public sealed class AutoRetainerRefreshService : IDisposable
{
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);
    private const string Consumer = "Quartermaster";
    private const string Init = "AutoRetainer.Init";
    private const string DrawButtons = "AutoRetainer.OnRetainerListTaskButtonsDraw";
    private const string CustomTask = "AutoRetainer.OnRetainerListCustomTask";
    private const string AdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
    private const string RequestPostprocess = "AutoRetainer.RequestPostprocess";
    private const string ReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string FinishPostprocess = "AutoRetainer.FinishPostprocessRequest";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly RetainerCaptureService captures;
    private readonly IRetainerAutomationSession session;
    private readonly AutomationLease automation;
    private readonly Func<bool> autoRetainerAvailable;
    private readonly Func<int> countAvailableRetainers;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ActiveTaskTracker activeTasks = new();
    private readonly AutoRetainerPostprocessState postprocess = new();
    private IDisposable? automationLease;
    private bool preparing;
    private bool queued;
    private bool refreshing;
    private bool postprocessOwned;
    private int expected;
    private int processed;
    private bool registered;
    private volatile bool disposed;

    public AutoRetainerRefreshService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog log,
        RetainerCaptureService captures,
        IRetainerAutomationSession session,
        AutomationLease? automation = null)
        : this(pluginInterface, framework, log, captures, session, automation, null, null)
    {
    }

    internal AutoRetainerRefreshService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog log,
        RetainerCaptureService captures,
        IRetainerAutomationSession session,
        AutomationLease? automation,
        Func<bool>? autoRetainerAvailable,
        Func<int>? countAvailableRetainers)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        this.captures = captures;
        this.session = session;
        this.automation = automation ?? new AutomationLease();
        this.autoRetainerAvailable = autoRetainerAvailable ?? CheckAutoRetainerAvailable;
        this.countAvailableRetainers = countAvailableRetainers ?? DalamudRetainerInventory.CountAvailableRetainers;
    }

    public void Register()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (registered)
            return;

        var draw = false;
        var additional = false;
        var ready = false;
        try
        {
            pluginInterface.GetIpcSubscriber<object>(DrawButtons).Subscribe(DrawButton);
            draw = true;
            pluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Subscribe(OnAdditionalTask);
            additional = true;
            pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Subscribe(OnReady);
            ready = true;
            registered = true;
        }
        catch
        {
            if (ready)
                pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Unsubscribe(OnReady);
            if (additional)
                pluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Unsubscribe(OnAdditionalTask);
            if (draw)
                pluginInterface.GetIpcSubscriber<object>(DrawButtons).Unsubscribe(DrawButton);
            throw;
        }
    }

    public bool IsRefreshing => refreshing || postprocessOwned;
    public bool IsQueued => preparing || queued;
    public string Status { get; private set; } = "Retainer refresh has not run.";

    public bool IsAvailable => autoRetainerAvailable();

    private bool CheckAutoRetainerAvailable()
    {
        try
        {
            pluginInterface.GetIpcSubscriber<object>(Init).InvokeAction();
            return true;
        }
        catch { return false; }
    }

    public bool Start()
    {
        if (disposed)
            return false;
        if (preparing || queued || refreshing || postprocessOwned)
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
        preparing = true;
        Status = session.IsRetainerListReady ? "Preparing retainer refresh." : "Opening retainer list.";
        Track(() => PrepareRefreshAsync(lifetime.Token));
        return true;
    }

    private async Task PrepareRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
            if (!list.Success)
                throw new InvalidOperationException($"{list.Code}: {list.Message}");

            var available = await framework.RunOnTick(
                countAvailableRetainers,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                if (available == 0)
                {
                    preparing = false;
                    ReleaseAutomationLease();
                    Status = "No available retainers were found.";
                    return;
                }

                expected = available;
                processed = 0;
                preparing = false;
                queued = true;
                Status = "Retainer refresh queued.";
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await FailAsync("Unable to prepare AutoRetainer refresh.", exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private void DrawButton()
    {
        if (disposed)
            return;
        try
        {
            if (IsRefreshing || queued || (automation.IsHeld && automation.Holder is not ("retainer refresh" or "retainer postprocess")))
                ImGui.BeginDisabled();
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("QuartermasterRefresh", FontAwesomeIcon.BookOpen))
                Start();
            if (IsRefreshing || queued || (automation.IsHeld && automation.Holder is not ("retainer refresh" or "retainer postprocess")))
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh all Quartermaster retainer inventory caches.");
            if (queued)
            {
                queued = false;
                refreshing = true;
                Status = $"Refreshing retainers: 0/{expected}.";
                pluginInterface.GetIpcSubscriber<string, object>(CustomTask).InvokeAction(Consumer);
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
                var partOfFullRefresh = refreshing;
                if (!partOfFullRefresh)
                {
                    if (postprocessOwned)
                        return;
                    if (!automation.TryAcquire("retainer postprocess", out automationLease))
                    {
                        Status = $"Skipped AutoRetainer postprocess because automation is busy with {automation.Holder}.";
                        return;
                    }
                    postprocessOwned = true;
                }
                if (!postprocess.TryBegin(retainerName, partOfFullRefresh, out var request))
                    return;
                try
                {
                    pluginInterface.GetIpcSubscriber<string, object>(RequestPostprocess).InvokeAction(Consumer);
                }
                catch
                {
                    postprocess.TryComplete(request);
                    if (!partOfFullRefresh)
                    {
                        postprocessOwned = false;
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
                pluginInterface.GetIpcSubscriber<object>(FinishPostprocess).InvokeAction();
                postprocess.TryComplete(request);
                if (request.PartOfFullRefresh && processed >= expected)
                {
                    refreshing = false;
                    ReleaseAutomationLease();
                    Status = $"Retainer refresh complete: {processed}/{expected}.";
                }
                else if (!request.PartOfFullRefresh)
                {
                    postprocessOwned = false;
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
        preparing = false;
        queued = false;
        refreshing = false;
        postprocessOwned = false;
        ReleaseAutomationLease();
        Status = $"{message} {exception.Message}";
        if (readyRequest is not null)
        {
            try { session.CancelActive(); }
            catch (Exception cleanupException) { log.Warning(cleanupException, "Unable to close Quartermaster retainer UI after refresh failure."); }
            try { pluginInterface.GetIpcSubscriber<object>(FinishPostprocess).InvokeAction(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        activeTasks.Stop();
        if (registered)
        {
            pluginInterface.GetIpcSubscriber<object>(DrawButtons).Unsubscribe(DrawButton);
            pluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Unsubscribe(OnAdditionalTask);
            pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Unsubscribe(OnReady);
            registered = false;
        }
        lifetime.Cancel();
        if (!activeTasks.Wait(DisposeJoinTimeout))
            log.Warning("Timed out waiting for AutoRetainer refresh tasks to stop during plugin disposal.");
        var readyRequest = postprocess.Cancel();
        preparing = false;
        queued = false;
        refreshing = false;
        postprocessOwned = false;
        if (readyRequest is not null)
        {
            try { session.CancelActive(); }
            catch { }
            try { pluginInterface.GetIpcSubscriber<object>(FinishPostprocess).InvokeAction(); }
            catch { }
        }
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
