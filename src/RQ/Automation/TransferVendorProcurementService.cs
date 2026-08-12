using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.Automation;

public sealed class TransferVendorProcurementService : IDisposable
{
    private readonly PluginConfiguration configuration;
    private readonly Action saveConfiguration;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly TransferVendorProcurementPlanner planner;
    private readonly DalamudGilVendorAccessReader access;
    private readonly VendorAutomationOwnership ownership;
    private readonly GilVendorBuyCoordinator coordinator;
    private DateTime nextIndeterminateReconciliationAt;
    private bool disposed;

    public TransferVendorProcurementService(
        PluginConfiguration configuration,
        Action saveConfiguration,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        TransferVendorProcurementPlanner planner,
        DalamudGilVendorAccessReader access,
        IGilVendorBuyRuntime runtime,
        VendorAutomationOwnership ownership)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        this.runtimeSnapshots = runtimeSnapshots ?? throw new ArgumentNullException(nameof(runtimeSnapshots));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));

        if (IsRunningPhase(configuration.ActiveTransferPlanVendorBuy?.Phase) &&
            !ownership.TryAcquire(out _))
        {
            configuration.ActiveTransferPlanVendorBuy!.Phase = GilVendorBuyPhase.Paused;
            configuration.ActiveTransferPlanVendorBuy.Message = "Vendor procurement was paused because another Quartermaster automation owns the client.";
            saveConfiguration();
        }
        coordinator = new(
            new ConfigurationVendorBuyRunStore(configuration, saveConfiguration),
            runtime ?? throw new ArgumentNullException(nameof(runtime)));
    }

    public GilVendorBuyRunSnapshot? ActiveRun => coordinator.ActiveRun;
    public bool IsRunning => coordinator.IsRunning;
    public bool HasActiveRun => ActiveRun is { Phase: not GilVendorBuyPhase.Completed and not GilVendorBuyPhase.Stopped and not GilVendorBuyPhase.Failed };
    public string CoordinationWarning => ownership.LastReleaseError;

    public TransferVendorProcurementReview BuildReview(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan plan,
        IReadOnlyList<TargetPlanItem> effectiveRules,
        RetrievalPlan retrieval) =>
        planner.Build(runtime.Owner, plan, runtime.Revision, effectiveRules, retrieval);

    public bool TryStart(TransferVendorProcurementReview review, out string error)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (HasActiveRun)
        {
            error = ActiveRun?.Phase == GilVendorBuyPhase.Indeterminate
                ? "The previous vendor purchase is indeterminate and is still being reconciled; no new purchase was started."
                : "A vendor procurement run is already active.";
            return false;
        }
        var runtime = runtimeSnapshots.Current;
        var plan = runtime.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == review.PlanId && candidate.Owner.Matches(runtime.Owner));
        if (plan is null || !review.Owner.Matches(runtime.Owner))
        {
            error = "The reviewed Transfer Plan does not belong to the current character.";
            return false;
        }
        if (plan.Revision != review.PlanRevision || runtime.Revision != review.RuntimeRevision)
        {
            error = "The Transfer Plan or inventory changed after review; review the current vendor shortfall again.";
            return false;
        }
        var effectiveRules = ListingPlanEvaluator.ComposeRules(runtime.State, runtime.Browser, runtime.Owner, plan.Id);
        var currentSignature = TransferVendorProcurementPlanner.BuildContextSignature(runtime.Owner, plan, effectiveRules);
        if (!string.Equals(currentSignature, review.ContextSignature, StringComparison.Ordinal))
        {
            error = "The effective Transfer Plan target changed after review; review the current vendor shortfall again.";
            return false;
        }
        if (!review.CanStart)
        {
            error = "No reviewed vendor-purchasable shortfall remains.";
            return false;
        }
        if (!ownership.TryAcquire(out error))
            return false;
        try
        {
            if (coordinator.TryStart(review.ToBuyPlan(), review.ContextSignature, out error))
                return true;
            ownership.Release();
            return false;
        }
        catch (Exception exception)
        {
            ownership.Release();
            error = $"Vendor procurement could not start: {exception.Message}";
            return false;
        }
    }

    public void Tick()
    {
        access.RefreshAttunedAetherytes();
        if (coordinator.ActiveRun?.Phase == GilVendorBuyPhase.Indeterminate)
        {
            ownership.Release();
            if (!string.Equals(
                    CurrentContextSignature(),
                    coordinator.ActiveRun.ContextSignature,
                    StringComparison.Ordinal))
                return;
            if (DateTime.UtcNow >= nextIndeterminateReconciliationAt)
            {
                nextIndeterminateReconciliationAt = DateTime.UtcNow.AddSeconds(1);
                coordinator.TryReconcileIndeterminate(out _);
            }
            return;
        }
        if (!coordinator.IsRunning)
            return;
        coordinator.Tick(CurrentContextSignature());
        if (!coordinator.IsRunning)
            ownership.Release();
    }

    public bool Pause()
    {
        var paused = coordinator.Pause("Vendor procurement paused.");
        if (paused)
            ownership.Release();
        return paused;
    }

    public bool Resume(out string error)
    {
        if (!ownership.TryAcquire(out error))
            return false;
        if (coordinator.Resume(CurrentContextSignature(), out error))
            return true;
        ownership.Release();
        return false;
    }

    public bool Stop()
    {
        var stopped = coordinator.Stop("Vendor procurement stopped.");
        if (stopped && !coordinator.IsRunning)
            ownership.Release();
        return stopped;
    }

    private string CurrentContextSignature()
    {
        var runtime = runtimeSnapshots.Current;
        var run = coordinator.ActiveRun;
        if (run is null || !runtime.Owner.HasStableIdentity)
            return string.Empty;
        var plan = runtime.State.StowagePlans.FirstOrDefault(candidate =>
            candidate.Owner.Matches(runtime.Owner) &&
            string.Equals(
                TransferVendorProcurementPlanner.BuildContextSignature(
                    runtime.Owner,
                    candidate,
                    ListingPlanEvaluator.ComposeRules(runtime.State, runtime.Browser, runtime.Owner, candidate.Id)),
                run.ContextSignature,
                StringComparison.Ordinal));
        return plan is null ? string.Empty : run.ContextSignature;
    }

    private static bool IsRunningPhase(GilVendorBuyPhase? phase) => phase is
        GilVendorBuyPhase.RefreshPreconditions or
        GilVendorBuyPhase.ReachVendor or
        GilVendorBuyPhase.ValidateShop or
        GilVendorBuyPhase.PurchaseLine or
        GilVendorBuyPhase.VerifyReceipt;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        coordinator.Dispose();
        ownership.Release();
    }

    private sealed class ConfigurationVendorBuyRunStore(
        PluginConfiguration configuration,
        Action saveConfiguration) : IGilVendorBuyRunStore
    {
        public GilVendorBuyRunSnapshot? LoadCurrent() => configuration.ActiveTransferPlanVendorBuy;

        public void Save(GilVendorBuyRunSnapshot snapshot)
        {
            configuration.ActiveTransferPlanVendorBuy = snapshot;
            saveConfiguration();
        }
    }
}

public sealed class VendorAutomationOwnership
{
    private readonly AutomationLease automation;
    private readonly AutoRetainerSuppression autoRetainer;
    private readonly DalamudExternalUiAutomationSuppression externalUi;
    private IDisposable? automationLease;
    private AutoRetainerSuppression.Scope? autoRetainerScope;
    private DalamudExternalUiAutomationSuppression.Scope? externalUiScope;

    public string LastReleaseError { get; private set; } = string.Empty;

    public VendorAutomationOwnership(
        AutomationLease automation,
        AutoRetainerSuppression autoRetainer,
        DalamudExternalUiAutomationSuppression externalUi)
    {
        this.automation = automation;
        this.autoRetainer = autoRetainer;
        this.externalUi = externalUi;
    }

    public bool TryAcquire(out string error)
    {
        if (automationLease is not null)
        {
            error = string.Empty;
            return true;
        }
        if (autoRetainer.IsBusy)
        {
            error = "AutoRetainer is busy; vendor procurement was not started.";
            return false;
        }
        if (!automation.TryAcquire("vendor procurement", out var lease))
        {
            error = $"Quartermaster automation is busy with {automation.Holder ?? "another operation"}.";
            return false;
        }
        try
        {
            LastReleaseError = string.Empty;
            automationLease = lease;
            if (autoRetainer.IsAvailable)
                autoRetainerScope = autoRetainer.Acquire();
            externalUiScope = externalUi.Acquire();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Release();
            error = $"Automation coordination failed before vendor procurement started: {exception.Message}";
            return false;
        }
    }

    public void Release()
    {
        if (externalUiScope is null && autoRetainerScope is null && automationLease is null)
            return;
        var failures = new List<string>();
        if (externalUiScope is { } externalScope)
        {
            externalScope.Dispose();
            failures.AddRange(externalScope.RestoreFailures.Select(failure => $"UI automation suppression: {failure}"));
        }
        externalUiScope = null;
        if (autoRetainerScope is { } autoScope)
        {
            autoScope.Dispose();
            if (autoScope.RestoreFailure is { } failure)
                failures.Add($"AutoRetainer suppression: {failure}");
        }
        autoRetainerScope = null;
        automationLease?.Dispose();
        automationLease = null;
        LastReleaseError = failures.Count == 0
            ? string.Empty
            : $"Automation coordination could not be fully restored: {string.Join(" ", failures)}";
    }
}
