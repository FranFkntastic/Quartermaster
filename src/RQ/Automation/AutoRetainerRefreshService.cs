using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
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
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly RetainerCaptureService captures;
    private readonly AutomationLease automation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ActiveTaskTracker activeTasks = new();
    private readonly AutoRetainerPostprocessState postprocess = new();
    private IDisposable? automationLease;
    private bool queued;
    private bool refreshing;
    private bool postprocessOwned;
    private int expected;
    private int processed;
    private volatile bool disposed;

    public AutoRetainerRefreshService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        RetainerCaptureService captures,
        AutomationLease? automation = null)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
        this.captures = captures;
        this.automation = automation ?? new AutomationLease();
        pluginInterface.GetIpcSubscriber<object>(DrawButtons).Subscribe(DrawButton);
        pluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Subscribe(OnAdditionalTask);
        pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Subscribe(OnReady);
    }

    public bool IsRefreshing => refreshing || postprocessOwned;
    public bool IsQueued => queued;
    public string Status { get; private set; } = "Retainer refresh has not run.";

    public bool IsAvailable
    {
        get
        {
            try
            {
                pluginInterface.GetIpcSubscriber<object>(Init).InvokeAction();
                return true;
            }
            catch { return false; }
        }
    }

    public bool Start()
    {
        if (disposed)
            return false;
        if (queued || refreshing || postprocessOwned)
        {
            Status = "Retainer refresh is already queued or running.";
            return false;
        }
        if (!IsAvailable)
        {
            Status = "AutoRetainer is unavailable.";
            return false;
        }
        if (!IsReady("RetainerList"))
        {
            Status = "Open retainer list before starting refresh.";
            return false;
        }
        expected = GetAvailableRetainerCount();
        if (expected == 0)
        {
            Status = "No available retainers were found.";
            return false;
        }
        if (!automation.TryAcquire("retainer refresh", out automationLease))
        {
            Status = $"Automation is busy with {automation.Holder}.";
            return false;
        }
        processed = 0;
        queued = true;
        Status = "Retainer refresh queued.";
        return true;
    }

    private void DrawButton()
    {
        if (disposed)
            return;
        try
        {
            ImGui.SameLine();
            if (IsRefreshing || queued || (automation.IsHeld && automation.Holder is not ("retainer refresh" or "retainer postprocess")))
                ImGui.BeginDisabled();
            if (ImGui.SmallButton("Quartermaster refresh"))
                Start();
            if (IsRefreshing || queued || (automation.IsHeld && automation.Holder is not ("retainer refresh" or "retainer postprocess")))
                ImGui.EndDisabled();
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
            await WaitUntilAsync(IsCommandMenuReady, $"command menu for {retainerName}", cancellationToken).ConfigureAwait(false);
            var selected = await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                return postprocess.IsCurrent(request) && SelectInventoryCommand();
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!selected)
                throw new InvalidOperationException("Retainer inventory command was not available.");
            await WaitUntilAsync(() => IsReady("InventoryRetainerLarge") || IsReady("InventoryRetainer"), $"inventory for {retainerName}", cancellationToken).ConfigureAwait(false);
            var captureWait = await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                return captures.GetWaitSnapshot();
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (captureWait.Session is null)
                throw new InvalidOperationException("Retainer inventory opened without a stable capture session.");
            await framework.RunOnTick(() =>
            {
                ThrowIfStopped(cancellationToken);
                if (postprocess.IsCurrent(request))
                    CloseInventory();
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task WaitUntilAsync(Func<bool> predicate, string state, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            ThrowIfStopped(cancellationToken);
            if (await framework.RunOnTick(predicate, cancellationToken: cancellationToken).ConfigureAwait(false))
                return;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Timed out waiting for {state}.");
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
        queued = false;
        refreshing = false;
        postprocessOwned = false;
        ReleaseAutomationLease();
        Status = $"{message} {exception.Message}";
        if (readyRequest is not null)
        {
            try { pluginInterface.GetIpcSubscriber<object>(FinishPostprocess).InvokeAction(); }
            catch { }
        }
    }

    private unsafe bool IsCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString", 1);
        return addon is not null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && FindEntry(addon, AddonText(2378)) >= 0;
    }

    private unsafe bool SelectInventoryCommand()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString", 1);
        if (addon is null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;
        var index = FindEntry(addon, AddonText(2378));
        if (index < 0)
            return false;
        addon->AtkUnitBase.FireCallbackInt(index);
        return true;
    }

    private static unsafe int FindEntry(AddonSelectString* addon, string target)
    {
        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
            if (RetainerUiAutomationText.IsSelectStringEntryMatch(popup.EntryNames[index].ToString(), target))
                return index;
        return -1;
    }

    private unsafe bool IsReady(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon is not null && addon->IsReady && addon->IsVisible;
    }

    private unsafe void CloseInventory()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("InventoryRetainerLarge", 1);
        if (addon == null)
            addon = gameGui.GetAddonByName<AtkUnitBase>("InventoryRetainer", 1);
        if (addon != null)
            addon->Close(true);
    }

    private static unsafe int GetAvailableRetainerCount()
    {
        var manager = RetainerManager.Instance();
        if (manager is null)
            return 0;
        var count = 0;
        for (var index = 0; index < manager->GetRetainerCount(); index++)
            if (manager->Retainers[index].Available)
                count++;
        return count;
    }

    private string AddonText(uint id) => dataManager.GetExcelSheet<Addon>().GetRow(id).Text.ExtractText();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        activeTasks.Stop();
        pluginInterface.GetIpcSubscriber<object>(DrawButtons).Unsubscribe(DrawButton);
        pluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Unsubscribe(OnAdditionalTask);
        pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Unsubscribe(OnReady);
        lifetime.Cancel();
        if (!activeTasks.Wait(DisposeJoinTimeout))
            log.Warning("Timed out waiting for AutoRetainer refresh tasks to stop during plugin disposal.");
        var readyRequest = postprocess.Cancel();
        queued = false;
        refreshing = false;
        postprocessOwned = false;
        if (readyRequest is not null)
        {
            try { CloseInventory(); }
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
