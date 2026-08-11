using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Observations.V1;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Automation;

public sealed class RetainerRefreshCoordinator : IDisposable
{
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutoRetainerBusyBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EvidenceBudget = TimeSpan.FromSeconds(12);
    private static readonly RetainerEvidenceDomain RequiredEvidence =
        RetainerEvidenceDomain.Inventory | RetainerEvidenceDomain.Crystals | RetainerEvidenceDomain.Gil;
    private const string Consumer = "Quartermaster";

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly IRetainerAutomationSession session;
    private readonly ObservationCaptureSessionRegistry captureSessions;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly AutomationLease automation;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<DateTime> utcNow;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ActiveTaskTracker activeTasks = new();
    private CancellationTokenSource? activeRun;
    private CancellationTokenSource? rosterDiscovery;
    private IDisposable? automationLease;
    private RetainerRefreshPhase phase;
    private string? postprocessRetainerName;
    private int ownsPostprocessRequest;
    private string? discoveredOwnerKey;
    private DateTime nextRosterDiscoveryAtUtc;
    private bool rosterDiscoveryRunning;
    private volatile bool disposed;

    public RetainerRefreshCoordinator(
        IFramework framework,
        IPluginLog log,
        RetainerCacheRepository cache,
        StateRepository state,
        IRetainerAutomationSession session,
        ObservationCaptureSessionRegistry captureSessions,
        IAutoRetainerIpc autoRetainer,
        AutomationLease? automation,
        Func<OwnerScope> currentOwner,
        Func<DateTime>? utcNow = null)
    {
        this.framework = framework;
        this.log = log;
        this.cache = cache;
        this.state = state;
        this.session = session;
        this.captureSessions = captureSessions;
        this.autoRetainer = autoRetainer;
        this.automation = automation ?? new AutomationLease();
        this.currentOwner = currentOwner;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRefreshing => phase is RetainerRefreshPhase.Preparing or RetainerRefreshPhase.Refreshing;
    public bool IsQueued => false;
    public bool IsAvailable => !disposed;
    public bool CanStart => !disposed && phase == RetainerRefreshPhase.Idle && !automation.IsHeld;
    public bool CanCancel => phase is RetainerRefreshPhase.Preparing or RetainerRefreshPhase.Refreshing;
    public bool HasRecovery => state.Read(document => document.RetainerRefreshRecovery is not null);
    public string Status { get; private set; } = "Retainer refresh has not run.";
    public IReadOnlyList<RetainerRefreshItemResult> Results { get; private set; } = [];
    public string? LastCompletedRunId { get; private set; }
    public bool? LastRunSucceeded { get; private set; }

    public void Register() => autoRetainer.Register(new(DrawAutoRetainerButton, OnAutoRetainerAdditionalTask, OnAutoRetainerReady));

    public void TickRosterDiscovery(bool stockBrowserVisible)
    {
        if (disposed)
            return;
        if (!stockBrowserVisible)
        {
            rosterDiscovery?.Cancel();
            rosterDiscovery?.Dispose();
            rosterDiscovery = null;
            rosterDiscoveryRunning = false;
            return;
        }

        var owner = currentOwner();
        if (!owner.HasStableIdentity || rosterDiscoveryRunning || utcNow() < nextRosterDiscoveryAtUtc)
            return;
        if (string.Equals(discoveredOwnerKey, OwnerKey(owner), StringComparison.Ordinal))
            return;

        rosterDiscovery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        rosterDiscoveryRunning = true;
        Track(() => DiscoverRosterAsync(owner, rosterDiscovery.Token));
    }

    public bool Start() => StartCore(null, persistStandaloneRecovery: true, out _);

    public bool StartForPlan(out string runId) =>
        StartCore(null, persistStandaloneRecovery: false, out runId);

    public bool Retry()
    {
        var recovery = state.Read(document => document.RetainerRefreshRecovery);
        if (recovery is null)
        {
            Status = "There is no failed retainer refresh to retry.";
            return false;
        }
        if (!recovery.Owner.Matches(currentOwner()))
        {
            Status = "The failed refresh belongs to another character.";
            return false;
        }
        return StartCore(recovery.PendingRetainerIds, persistStandaloneRecovery: true, out _);
    }

    public void DismissRecovery()
    {
        state.Mutate(StateChangeKind.Recovery, document => document.RetainerRefreshRecovery = null);
        if (phase == RetainerRefreshPhase.Idle)
            Status = "Retainer refresh recovery dismissed.";
    }

    public void Cancel()
    {
        if (!CanCancel)
            return;
        Status = "Cancelling retainer refresh...";
        activeRun?.Cancel();
        try { session.CancelActive(); }
        catch (Exception exception) { log.Warning(exception, "Unable to cancel the active retainer interaction cleanly."); }
    }

    private bool StartCore(
        IReadOnlyCollection<ulong>? retryRetainerIds,
        bool persistStandaloneRecovery,
        out string runId)
    {
        runId = string.Empty;
        if (!CanStart)
        {
            Status = phase == RetainerRefreshPhase.Idle
                ? $"Automation is busy with {automation.Holder}."
                : "Retainer refresh is already running.";
            return false;
        }
        var owner = currentOwner();
        if (!owner.HasStableIdentity)
        {
            Status = "A stable character identity is required to refresh retainers.";
            return false;
        }
        if (!automation.TryAcquire("retainer refresh", out automationLease))
        {
            Status = $"Automation is busy with {automation.Holder}.";
            return false;
        }

        var startedRunId = Guid.NewGuid().ToString("N");
        runId = startedRunId;
        Results = [];
        activeRun = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        phase = RetainerRefreshPhase.Preparing;
        Status = "Discovering assigned retainers...";
        Track(() => RunRefreshAsync(startedRunId, owner, retryRetainerIds, persistStandaloneRecovery, activeRun.Token));
        return true;
    }

    private async Task DiscoverRosterAsync(OwnerScope owner, CancellationToken cancellationToken)
    {
        try
        {
            var roster = await session.ScanRetainerRosterAsync(cancellationToken).ConfigureAwait(false);
            if (!roster.Success || !roster.IsComplete)
            {
                nextRosterDiscoveryAtUtc = utcNow().AddSeconds(5);
                if (roster.Code is "RetainerManagerUnavailable" or "RetainerRosterNotReady")
                    return;
                throw new InvalidOperationException($"{roster.Code}: {roster.Message}");
            }
            if (!currentOwner().Matches(owner))
                return;
            ReconcileRoster(owner, roster.Retainers, utcNow());
            discoveredOwnerKey = OwnerKey(owner);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Warning(exception, "Quartermaster could not hydrate the assigned retainer roster.");
            nextRosterDiscoveryAtUtc = utcNow().AddSeconds(5);
        }
        finally
        {
            rosterDiscoveryRunning = false;
        }
    }

    private async Task RunRefreshAsync(
        string runId,
        OwnerScope owner,
        IReadOnlyCollection<ulong>? retryRetainerIds,
        bool persistStandaloneRecovery,
        CancellationToken cancellationToken)
    {
        var pending = new List<ulong>();
        var results = new List<RetainerRefreshItemResult>();
        try
        {
            await WaitForAutoRetainerIdleAsync(cancellationToken).ConfigureAwait(false);
            var roster = await session.ScanRetainerRosterAsync(cancellationToken).ConfigureAwait(false);
            RetainerAutomationResult? list = null;
            if (!roster.Success || !roster.IsComplete)
            {
                Status = "Opening the retainer list to populate the assigned roster...";
                list = await EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
                RequireSuccess(list);
                roster = await session.ScanRetainerRosterAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!roster.Success || !roster.IsComplete)
                throw new InvalidOperationException($"{roster.Code}: {roster.Message}");
            ReconcileRoster(owner, roster.Retainers, DateTime.UtcNow);

            var selectedIds = retryRetainerIds is { Count: > 0 }
                ? retryRetainerIds.ToHashSet()
                : null;
            var targets = roster.Retainers
                .Where(entry => selectedIds is null || selectedIds.Contains(entry.RetainerId))
                .OrderBy(entry => entry.DisplayOrder)
                .ToArray();
            if (targets.Length == 0)
                throw new InvalidOperationException("No assigned retainers matched the refresh request.");

            // Membership is the authority here. Raw availability/class/job metadata cannot
            // prove that an assigned retainer is inaccessible; only an exact list/open
            // refusal during this run may produce the terminal NotAccessible result.
            var attempts = targets;
            var inaccessible = 0;
            Results = results.ToArray();
            phase = RetainerRefreshPhase.Preparing;
            Status = session.IsRetainerListReady ? "Preparing retainer refresh..." : "Opening the retainer list...";
            if (list is null)
            {
                list = await EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
                RequireSuccess(list);
            }

            phase = RetainerRefreshPhase.Refreshing;
            var attempted = 0;
            foreach (var target in attempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Status = $"Refreshing retainers: {attempted}/{attempts.Length}.";
                try
                {
                    await RefreshOneAsync(target, cancellationToken).ConfigureAwait(false);
                    results.Add(new(target.RetainerId, target.RetainerName, "Refreshed", "Complete inventory evidence was accepted."));
                    Results = results.ToArray();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    log.Warning(exception, "Retainer refresh failed for {RetainerName} ({RetainerId}).", target.RetainerName, target.RetainerId);
                    if (exception is RetainerRefreshException { Code: "RetainerNotVisible" })
                    {
                        inaccessible++;
                        cache.ObserveUiAccessibility(owner, target.RetainerId, false, DateTime.UtcNow);
                        results.Add(new(
                            target.RetainerId,
                            target.RetainerName,
                            "NotAccessible",
                            "The assigned retainer is not currently exposed by the retainer list."));
                    }
                    else
                    {
                        pending.Add(target.RetainerId);
                        results.Add(new(target.RetainerId, target.RetainerName, "NeedsRetry", exception.Message));
                    }
                    Results = results.ToArray();
                    await RecoverRetainerListAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    attempted++;
                    Status = $"Refreshing retainers: {attempted}/{attempts.Length}.";
                }
            }

            await CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Count != 0)
            {
                if (persistStandaloneRecovery)
                    PersistRecovery(runId, owner, pending, $"{pending.Count} retainer(s) need retry.");
                Status = $"Retainer evidence refresh failed for {pending.Count} of {attempts.Length} retainers. Retry to try again.";
                CompleteRun(runId, false);
                return;
            }

            if (persistStandaloneRecovery)
                state.Mutate(StateChangeKind.Recovery, document => document.RetainerRefreshRecovery = null);
            Status = inaccessible == 0
                ? $"Retainer refresh complete: {attempted}/{attempts.Length}."
                : $"Retainer refresh complete: {attempted}/{attempts.Length}; {inaccessible} not accessible.";
            CompleteRun(runId, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Retainer refresh cancelled.";
            if (persistStandaloneRecovery)
                state.Mutate(StateChangeKind.Recovery, document => document.RetainerRefreshRecovery = null);
            CompleteRun(runId, false);
            await TryCloseEverythingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Retainer refresh failed.");
            var requested = retryRetainerIds?.ToArray() ?? [];
            if (persistStandaloneRecovery)
                PersistRecovery(runId, owner, requested, exception.Message);
            Status = $"Retainer refresh failed: {exception.Message}";
            CompleteRun(runId, false);
            await TryCloseEverythingAsync().ConfigureAwait(false);
        }
        finally
        {
            phase = disposed ? RetainerRefreshPhase.Disposed : RetainerRefreshPhase.Idle;
            activeRun?.Dispose();
            activeRun = null;
            ReleaseAutomationLease();
        }
    }

    private async Task RefreshOneAsync(RetainerRosterEntry target, CancellationToken cancellationToken)
    {
        var owner = currentOwner();
        using var evidenceSession = captureSessions.Begin(
            new ObservationOwner(owner.LocalContentId!.Value, owner.HomeWorldId!.Value),
            target.RetainerId);
        var evidence = new EvidenceWait(cache, owner, target.RetainerId, evidenceSession.SessionId, cache.Revision, RequiredEvidence);
        using (evidence)
        {
            RequireSuccess(await session.OpenRetainerAsync(new(target.RetainerId, target.RetainerName), cancellationToken).ConfigureAwait(false));
            cache.ObserveUiAccessibility(owner, target.RetainerId, true, DateTime.UtcNow);
            RequireSuccess(await session.WaitForCurrentRetainerMenuAsync(cancellationToken).ConfigureAwait(false));
            RequireSuccess(await session.OpenInventoryAsync(cancellationToken).ConfigureAwait(false));
            RequireSuccess(await session.CloseInventoryAsync(cancellationToken).ConfigureAwait(false));
            await WaitForEvidenceAsync(evidence, target.RetainerName, cancellationToken).ConfigureAwait(false);
            RequireSuccess(await session.ReturnToRetainerListAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task WaitForEvidenceAsync(EvidenceWait evidence, string retainerName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(EvidenceBudget);
        var delayTicks = 1;
        while (!evidence.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"Timed out waiting for complete inventory evidence from {retainerName}; received {evidence.ObservedDomains}.");
            await framework.DelayTicks(delayTicks, cancellationToken).ConfigureAwait(false);
            delayTicks = Math.Min(delayTicks * 2, 30);
        }
    }

    private async Task WaitForAutoRetainerIdleAsync(CancellationToken cancellationToken)
    {
        if (!autoRetainer.IsAvailable || !autoRetainer.IsBusy)
            return;
        Status = "Waiting for the current retainer task to finish...";
        var deadline = DateTime.UtcNow.Add(AutoRetainerBusyBudget);
        var delayTicks = 1;
        while (autoRetainer.IsBusy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("AutoRetainer remained busy beyond the refresh coordination budget.");
            await framework.DelayTicks(delayTicks, cancellationToken).ConfigureAwait(false);
            delayTicks = Math.Min(delayTicks * 2, 30);
        }
    }

    private async Task RecoverRetainerListAsync(CancellationToken cancellationToken)
    {
        try { session.CancelActive(); }
        catch { }
        var list = await EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RequireSuccess(list);
    }

    private async Task<RetainerAutomationResult> EnsureRetainerListAsync(CancellationToken cancellationToken)
    {
        var list = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
        if (list.Success || !string.Equals(list.Code, "RetainerInteractionAlreadyOpen", StringComparison.Ordinal))
            return list;
        var closed = await session.CloseRetainerAsync(cancellationToken).ConfigureAwait(false);
        if (!closed.Success)
            return closed;
        return await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseRetainerListAsync(CancellationToken cancellationToken)
    {
        var result = await session.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
        RequireSuccess(result);
    }

    private Task TryCloseEverythingAsync()
    {
        try { session.CancelActive(); }
        catch (Exception exception) { log.Warning(exception, "Unable to close retainer UI after refresh termination."); }
        return Task.CompletedTask;
    }

    private void PersistRecovery(string runId, OwnerScope owner, IReadOnlyCollection<ulong> pendingRetainerIds, string message) =>
        state.Mutate(StateChangeKind.Recovery, document => document.RetainerRefreshRecovery = new()
        {
            RunId = runId,
            Owner = owner with { },
            PendingRetainerIds = pendingRetainerIds.Distinct().ToList(),
            FailedAtUtc = DateTime.UtcNow,
            Message = message,
        });

    private void ReconcileRoster(OwnerScope owner, IReadOnlyList<RetainerRosterEntry> roster, DateTime observedAtUtc) =>
        cache.ReconcileRoster(
            owner,
            roster.Select(entry => new RetainerRosterProjectionEntry(
                entry.RetainerId,
                entry.RetainerName,
                entry.DisplayOrder,
                entry.IsUiAccessible,
                entry.ClassJobId,
                entry.Level,
                entry.MarketItemCount,
                entry.IsGameAvailable)).ToArray(),
            observedAtUtc);

    private void DrawAutoRetainerButton()
    {
        if (disposed)
            return;
        var disabled = !CanStart;
        if (disabled)
            ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("QuartermasterRefresh", FontAwesomeIcon.BookOpen))
            Start();
        if (disabled)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh all Quartermaster retainer inventory caches.");
    }

    private void OnAutoRetainerAdditionalTask(string retainerName)
    {
        if (disposed || phase != RetainerRefreshPhase.Idle || string.IsNullOrWhiteSpace(retainerName))
            return;
        if (!automation.TryAcquire("retainer postprocess", out automationLease))
            return;
        phase = RetainerRefreshPhase.PassivePostprocess;
        postprocessRetainerName = retainerName;
        Volatile.Write(ref ownsPostprocessRequest, 1);
        try { autoRetainer.RequestPostprocess(Consumer); }
        catch
        {
            Interlocked.Exchange(ref ownsPostprocessRequest, 0);
            postprocessRetainerName = null;
            phase = RetainerRefreshPhase.Idle;
            ReleaseAutomationLease();
            throw;
        }
    }

    private void OnAutoRetainerReady(string consumer, string retainerName)
    {
        if (!string.Equals(consumer, Consumer, StringComparison.Ordinal) ||
            !string.Equals(retainerName, postprocessRetainerName, StringComparison.OrdinalIgnoreCase))
            return;
        Track(() => CaptureAutoRetainerPostprocessAsync(retainerName, lifetime.Token));
    }

    private async Task CaptureAutoRetainerPostprocessAsync(string retainerName, CancellationToken cancellationToken)
    {
        try
        {
            var owner = currentOwner();
            var targets = cache.Snapshot().Values
                .Where(retainer => retainer.Owner.Matches(owner) &&
                                   retainer.IsCurrentlyAssigned is not false &&
                                   string.Equals(retainer.RetainerName, retainerName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targets.Length != 1)
                throw new InvalidOperationException($"Could not resolve AutoRetainer postprocess target {retainerName} to one assigned retainer.");
            var target = targets[0];
            using var evidenceSession = captureSessions.Begin(
                new ObservationOwner(owner.LocalContentId!.Value, owner.HomeWorldId!.Value),
                target.RetainerId);
            using var evidence = new EvidenceWait(cache, owner, target.RetainerId, evidenceSession.SessionId, cache.Revision, RequiredEvidence);
            RequireSuccess(await session.WaitForCurrentRetainerMenuAsync(cancellationToken).ConfigureAwait(false));
            RequireSuccess(await session.OpenInventoryAsync(cancellationToken).ConfigureAwait(false));
            RequireSuccess(await session.CloseInventoryAsync(cancellationToken).ConfigureAwait(false));
            await WaitForEvidenceAsync(evidence, retainerName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Warning(exception, "Quartermaster AutoRetainer postprocess failed for {RetainerName}.", retainerName);
        }
        finally
        {
            FinishOwnedPostprocess();
            postprocessRetainerName = null;
            phase = disposed ? RetainerRefreshPhase.Disposed : RetainerRefreshPhase.Idle;
            ReleaseAutomationLease();
        }
    }

    private static void RequireSuccess(RetainerAutomationResult result)
    {
        if (!result.Success)
            throw new RetainerRefreshException(result.Code, result.Message);
    }

    private static string OwnerKey(OwnerScope owner) => $"{owner.LocalContentId}:{owner.HomeWorldId}";

    private void CompleteRun(string runId, bool succeeded)
    {
        LastRunSucceeded = succeeded;
        LastCompletedRunId = runId;
    }

    private void Track(Func<Task> taskFactory) => activeTasks.TryRun(() => ObserveAsync(taskFactory()));

    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { log.Error(exception, "Unhandled retainer refresh task failure."); }
    }

    private void ReleaseAutomationLease()
    {
        automationLease?.Dispose();
        automationLease = null;
    }

    private void FinishOwnedPostprocess()
    {
        if (Interlocked.Exchange(ref ownsPostprocessRequest, 0) == 0)
            return;
        try { autoRetainer.FinishPostprocess(); }
        catch (Exception exception) { log.Warning(exception, "Quartermaster could not finish its AutoRetainer postprocess request."); }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        activeTasks.Stop();
        rosterDiscovery?.Cancel();
        activeRun?.Cancel();
        lifetime.Cancel();
        try { session.CancelActive(); }
        catch { }
        if (!activeTasks.Wait(DisposeJoinTimeout))
            log.Warning("Timed out waiting for retainer refresh tasks to stop during plugin disposal.");
        FinishOwnedPostprocess();
        autoRetainer.Dispose();
        ReleaseAutomationLease();
        rosterDiscovery?.Dispose();
        activeRun?.Dispose();
        lifetime.Dispose();
        phase = RetainerRefreshPhase.Disposed;
    }

    private sealed class EvidenceWait : IDisposable
    {
        private readonly object gate = new();
        private readonly RetainerCacheRepository cache;
        private readonly OwnerScope owner;
        private readonly ulong retainerId;
        private readonly string evidenceSessionId;
        private readonly long checkpoint;
        private readonly DateTime startedAtUtc;
        private readonly RetainerEvidenceDomain required;
        private RetainerEvidenceDomain observed;

        public EvidenceWait(
            RetainerCacheRepository cache,
            OwnerScope owner,
            ulong retainerId,
            string evidenceSessionId,
            long checkpoint,
            RetainerEvidenceDomain required)
        {
            this.cache = cache;
            this.owner = owner with { };
            this.retainerId = retainerId;
            this.evidenceSessionId = evidenceSessionId;
            this.checkpoint = checkpoint;
            startedAtUtc = DateTime.UtcNow;
            this.required = required;
            cache.EvidenceAccepted += OnEvidenceAccepted;
        }

        public bool IsComplete
        {
            get { lock (gate) return (observed & required) == required; }
        }

        public RetainerEvidenceDomain ObservedDomains
        {
            get { lock (gate) return observed; }
        }

        private void OnEvidenceAccepted(RetainerEvidenceReceipt receipt)
        {
            if (receipt.RetainerId != retainerId ||
                !receipt.Owner.Matches(owner) ||
                !string.Equals(receipt.EvidenceSessionId, evidenceSessionId, StringComparison.Ordinal) ||
                receipt.Revision <= checkpoint ||
                receipt.ObservedAtUtc < startedAtUtc)
                return;
            lock (gate)
                observed |= receipt.Domains;
        }

        public void Dispose() => cache.EvidenceAccepted -= OnEvidenceAccepted;
    }
}

internal enum RetainerRefreshPhase
{
    Idle,
    Preparing,
    Refreshing,
    PassivePostprocess,
    Disposed,
}

public sealed record RetainerRefreshItemResult(
    ulong RetainerId,
    string RetainerName,
    string Outcome,
    string Message);

internal sealed class RetainerRefreshException(string code, string message)
    : InvalidOperationException($"{code}: {message}")
{
    public string Code { get; } = code;
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
        try { return Task.WhenAll(snapshot).Wait(timeout); }
        catch (AggregateException) { return true; }
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
