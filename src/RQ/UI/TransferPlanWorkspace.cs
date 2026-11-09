using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record TransferPlanWorkspaceSnapshot(int ProjectionBuildCount, int RenderedRowCount);

/// <summary>
/// Owns the Transfer Plan workspace, projection cache, route editing, and review entry points.
/// </summary>
internal sealed class TransferPlanWorkspace
{
    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly TransferCoordinator transfers;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly WorkbenchState workbench;
    private readonly TransferPlanEditor transferPlanEditor;
    private readonly TransferExecutionController transferExecution;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action<Guid, string> requestTransferReview;
    private readonly Action<TransferVendorProcurementReview> requestVendorReview;
    private readonly Action<StowagePlan, OwnerScope> requestDeletePlan;
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private int transferProjectionBuildCount;
    private string vendorStatus = string.Empty;

    public TransferPlanWorkspace(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        TransferCoordinator transfers,
        RetainerRefreshCoordinator retainerRefresh,
        TransferVendorProcurementService vendorProcurement,
        WorkbenchState workbench,
        TransferPlanEditor transferPlanEditor,
        TransferExecutionController transferExecution,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action<Guid, string> requestTransferReview,
        Action<TransferVendorProcurementReview> requestVendorReview,
        Action<StowagePlan, OwnerScope> requestDeletePlan)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.transfers = transfers;
        this.retainerRefresh = retainerRefresh;
        this.vendorProcurement = vendorProcurement;
        this.workbench = workbench;
        this.transferPlanEditor = transferPlanEditor;
        this.transferExecution = transferExecution;
        this.reviewRegistry = reviewRegistry;
        this.requestTransferReview = requestTransferReview;
        this.requestVendorReview = requestVendorReview;
        this.requestDeletePlan = requestDeletePlan;
    }

    public TransferPlanWorkspaceSnapshot Snapshot() =>
        new(transferProjectionBuildCount, transferPlanEditor.RenderedRowCount);

    public void ClearVendorStatus() => vendorStatus = string.Empty;

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;


    public void DrawRibbon(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = StowagePlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);

        ImGui.TextUnformatted("Transfer plan");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(300, Math.Max(160, ImGui.GetContentRegionAvail().X - 360)));
        if (ImGui.BeginCombo("##RQTransferPlan", transferPlanEditor.Draft?.Name ?? selected?.Name ?? "New Transfer Plan"))
        {
            foreach (var plan in plans)
            {
                if (ImGui.Selectable($"{plan.Name}##transfer:{plan.Id}", selected?.Id == plan.Id))
                {
                    workbench.SelectedStowagePlanId = plan.Id;
                    selected = plan;
                    transferPlanEditor.Ensure(runtime);
                }
            }
            foreach (var draft in transferPlanEditor.Drafts.Where(d => d.IsNew))
                if (ImGui.Selectable($"{draft.Name} (draft)##{draft.PlanId}", transferPlanEditor.Draft?.PlanId == draft.PlanId))
                {
                    workbench.SelectedStowagePlanId = draft.PlanId;
                    transferPlanEditor.Ensure(runtime);
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (!owner.HasStableIdentity)
            ImGui.BeginDisabled();
        if (ImGui.Button("New"))
            transferPlanEditor.Open(StowagePlanCatalog.NewDraft(state.Snapshot(), owner));
        if (!owner.HasStableIdentity)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (transferPlanEditor.Draft is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate")) transferPlanEditor.Duplicate();
        if (transferPlanEditor.Draft is null)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("…##RQTransferPlanMenu"))
            ImGui.OpenPopup("RQTransferPlanMenu");
        if (ImGui.BeginPopup("RQTransferPlanMenu"))
        {
            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Rename or edit details") && selected is not null)
                transferPlanEditor.Open(selected.Id, owner);
            if (ImGui.Selectable("Delete plan") && selected is not null)
                requestDeletePlan(selected, owner);
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }

        selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null)
        {
            transferExecution.ClearInlineErrorContext();
            ImGui.Spacing();
            ImGui.TextDisabled("Add items to the draft, then save it.");
            return;
        }
        transferExecution.EnsureInlineErrorContext(owner, selected.Id);

        var projection = ResolveProjection(runtime, selected);
        var ownerRules = projection.Rules;
        var retrieval = projection.Retrieval;
        var surplusBatch = projection.Deposit;
        var movements = projection.Movements;
        var hasMovement = projection.HasMovement;
        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement || projection.HasUnknownListingDemand,
            owner.HasStableIdentity,
            transfers.CanStart,
            retainerRefresh.IsRefreshing || retainerRefresh.IsQueued);
        var canExecute = owner.HasStableIdentity && (availability.CanExecute || transferPlanEditor.HasChanges);

        ImGui.SameLine();
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button(transferPlanEditor.HasChanges ? "Save & review moves" : "Review moves"))
            SaveAndRequestTransferReview();
        if (!canExecute)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.transfer.execute",
            "Review and execute the selected Transfer Plan",
            canExecute,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentPlan = ResolveSelectedStowagePlan(current.State, current.Owner);
                if (currentPlan is not null)
                    SaveAndRequestTransferReview();
            },
            canExecute
                ? projection.HasUnknownListingDemand ? "Listing demand will be verified first" : $"{movements:N0} movements"
                : availability.BlockReason);

    }

    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null) { transferPlanEditor.Draw(runtime); return; }
        var projection = ResolveProjection(runtime, selected);
        var retrieval = projection.Retrieval;
        var surplusBatch = projection.Deposit;
        var ownerRules = projection.Rules;
        var movements = projection.Movements;
        var vendor = projection.Vendor;
        var recovery = runtime.State.TransferPlanRecovery;
        var hasCurrentRecovery = recovery is not null &&
            recovery.Owner.Matches(owner) &&
            recovery.PlanId == selected.Id &&
            recovery.PlanRevision == selected.Revision;

        ImGui.TextDisabled(projection.HasUnknownListingDemand
            ? "Saved plan · listing demand needs verification"
            : $"Saved plan · retrieve {retrieval.NeededQuantity:N0} · stow {surplusBatch.RequestedQuantity:N0}");
        var shortfall = Math.Max(0, retrieval.MissingQuantity - vendor.ApprovedQuantity);
        if (!projection.HasUnknownListingDemand && (shortfall > 0 || surplusBatch.RemainingQuantity > 0))
            ImGui.TextWrapped($"Saved plan constraints: {shortfall:N0} short · no room for {surplusBatch.RemainingQuantity:N0}");
        var canReviewVendor = vendor.CanStart &&
                              !projection.HasUnknownListingDemand &&
                              !vendorProcurement.HasActiveRun &&
                              transfers.CanStart &&
                              !retainerRefresh.IsRefreshing &&
                              !retainerRefresh.IsQueued;
        if (vendor.Lines.Count > 0)
        {
            if (!canReviewVendor)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Review vendor buy ({vendor.ApprovedQuantity:N0})"))
                requestVendorReview(vendor);
            if (!canReviewVendor)
                ImGui.EndDisabled();
            reviewRegistry.RegisterLastButton(
                "quartermaster.vendor.review",
                "Review vendor coverage for the selected Transfer Plan",
                canReviewVendor,
                () =>
                {
                    var current = runtimeSnapshots.Current;
                    var currentPlan = ResolveSelectedStowagePlan(current.State, current.Owner);
                    if (currentPlan is null)
                        return;
                    var currentProjection = ResolveProjection(current, currentPlan);
                    requestVendorReview(currentProjection.Vendor);
                },
                canReviewVendor
                    ? $"{vendor.ApprovedQuantity:N0} units · maximum {vendor.MaximumGil:N0} gil"
                    : vendorProcurement.HasActiveRun
                        ? "A vendor run is already active"
                        : projection.HasUnknownListingDemand
                            ? "Listing demand must be verified first"
                            : "No reviewed vendor-purchasable shortfall");
        }
        var planProgress = hasCurrentRecovery && retainerRefresh.IsRefreshing
            ? retainerRefresh.Status
            : string.Empty;
        var planNotice = !string.IsNullOrWhiteSpace(transferExecution.InlineError)
            ? transferExecution.InlineError
            : hasCurrentRecovery && !string.IsNullOrWhiteSpace(recovery!.FailureMessage)
                ? recovery.FailureMessage
                : hasCurrentRecovery && !retainerRefresh.IsRefreshing
                    ? "Retainer evidence refresh did not complete. Retry plan to continue."
                    : string.Empty;
        DrawTransferPlanNotice(selected, hasCurrentRecovery, planProgress, planNotice);
        DrawVendorRunStatus();
        transferPlanEditor.Draw(runtime);
    }

    private void DrawVendorRunStatus()
    {
        var run = vendorProcurement.ActiveRun;
        if (run is null)
            return;

        ImGui.TextUnformatted($"Vendor buy · {run.Phase}");
        if (run.Receipts.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(.53f, .83f, .64f, 1f),
                $"{run.Receipts.Sum(receipt => receipt.Quantity):N0} bought · {run.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil)):N0} gil");
        }

        if (vendorProcurement.IsRunning)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Pause##RQVendorRun"))
                vendorProcurement.Pause();
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##RQVendorRun"))
                vendorProcurement.Stop();
        }
        else if (run.Phase == GilVendorBuyPhase.Paused)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Resume##RQVendorRun"))
            {
                if (!vendorProcurement.Resume(out var error))
                    vendorStatus = error;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##RQVendorRun"))
                vendorProcurement.Stop();
        }

        if (!string.IsNullOrWhiteSpace(run.Message))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(run.Message);
            ImGui.PopStyleColor();
        }
        if (!string.IsNullOrWhiteSpace(vendorStatus))
            DrawWrappedStatus(vendorStatus, new Vector4(1f, .4f, .4f, 1f));
        if (!string.IsNullOrWhiteSpace(vendorProcurement.CoordinationWarning))
            DrawWrappedStatus(vendorProcurement.CoordinationWarning, new Vector4(1f, .4f, .4f, 1f));
    }

    private static void DrawWrappedStatus(string message, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }

    private void DrawTransferPlanNotice(
        StowagePlan plan,
        bool hasCurrentRecovery,
        string progress,
        string notice)
    {
        var isProgress = !string.IsNullOrWhiteSpace(progress);
        if (!isProgress && string.IsNullOrWhiteSpace(notice))
            return;

        var title = isProgress
            ? "Refreshing retainer stock"
            : hasCurrentRecovery
                ? "Retainer refresh stopped"
                : "Plan couldn't continue";
        var body = isProgress
            ? progress
            : hasCurrentRecovery
                ? $"{notice} The plan is still intact; Retry recalculates remaining work from current evidence."
                : notice;
        var accent = isProgress
            ? new Vector4(.52f, .79f, .94f, 1f)
            : new Vector4(1f, .4f, .4f, 1f);
        var background = isProgress
            ? new Vector4(.06f, .13f, .17f, .92f)
            : new Vector4(.16f, .07f, .08f, .92f);

        var flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable("RQTransferPlanNotice", 2, flags))
            return;
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(background));
        ImGui.TableNextColumn();
        ImGui.TextColored(accent, title);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(220, ImGui.GetContentRegionAvail().X));
        ImGui.TextWrapped(body);
        ImGui.PopTextWrapPos();

        ImGui.TableNextColumn();
        if (isProgress)
        {
            if (retainerRefresh.CanCancel && ImGui.Button("Cancel##TransferPlanRecovery"))
                retainerRefresh.Cancel();
        }
        else if (hasCurrentRecovery)
        {
            if (ImGui.Button("Retry plan##TransferPlanRecovery"))
                transferExecution.RetryRecovery(plan);
            ImGui.SameLine();
            if (ImGui.Button("Dismiss##TransferPlanRecovery"))
                transferExecution.DismissRecovery();
        }
        else if (ImGui.Button("Dismiss##TransferPlanNotice"))
        {
            transferExecution.ClearInlineError();
        }
        ImGui.EndTable();
    }

    public TransferWorkbenchProjection ResolveProjection(
        QuartermasterRuntimeSnapshot runtime,
        StowagePlan plan)
    {
        if (transferWorkbenchProjection is { } cached &&
            cached.RuntimeRevision == runtime.Revision &&
            cached.PlanId == plan.Id)
            return cached;

        var rules = runtime.State.PlanItems
            .Where(rule => rule.StowagePlanId == plan.Id)
            .OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectiveRules = ListingPlanEvaluator.ComposeRules(runtime.State, runtime.Browser, runtime.Owner, plan.Id);
        var listingEvaluation = ListingPlanEvaluator.Evaluate(ListingPlanCatalog.OwnerPlan(runtime.State, runtime.Owner), runtime.Browser);
        var listingContributions = ListingPlanEvaluator.Contributions(runtime.State, plan.Id, listingEvaluation)
            .ToDictionary(contribution => (contribution.Link.ItemId, contribution.Link.Quality));
        var stowage = StowageEvaluator.BuildPlan(
            runtime.State,
            runtime.Browser,
            runtime.Owner,
            plan.Id);
        var retrieval = BuildTransferRetrievalEvaluation(runtime, effectiveRules);
        var vendor = vendorProcurement.BuildReview(runtime, plan, effectiveRules, retrieval);
        var deposit = TransferPlanEvaluation.BuildSurplusBatch(runtime, stowage);
        var evaluated = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var retrievalLines = retrieval.Lines.ToDictionary(line => line.PlanItemId);
        var vendorLines = vendor.Lines.ToDictionary(line => line.RuleId);
        var movements = evaluated.Values.Count(line =>
            line.Action is StowageAction.Retrieve or StowageAction.Deposit);
        var rows = rules
            .Select(rule =>
            {
                evaluated.TryGetValue(rule.Id, out var line);
                retrievalLines.TryGetValue(rule.Id, out var retrievalLine);
                vendorLines.TryGetValue(rule.Id, out var vendorLine);
                var routedDepositQuantity = deposit.Routes
                    .Where(route => route.Request.SourceRuleId == rule.Id)
                    .Sum(route => route.RoutedQuantity);
                var playerQuantity = StowageEvaluator.PlayerQuantity(
                    rule,
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId));
                var accessibleStorageQuantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
                    runtime.Browser.Items.FirstOrDefault(item => item.ItemId == rule.ItemId),
                    rule.Quality,
                    runtime.Retainers,
                    runtime.Owner);
                listingContributions.TryGetValue((rule.ItemId, rule.Quality), out var listingContribution);
                return new TransferWorkbenchRow(
                    rule,
                    line,
                    retrievalLine,
                    vendorLine,
                    routedDepositQuantity,
                    playerQuantity,
                    accessibleStorageQuantity,
                    (line?.DesiredPlayerQuantity ?? rule.TargetQuantity) - playerQuantity,
                    listingContribution?.Quantity ?? Evidence.Known(0),
                    listingContribution?.Link,
                    runtime.Owner,
                    plan.Id,
                    runtime);
            })
            .ToArray();
        transferWorkbenchProjection = new(
            runtime.Revision,
            plan.Id,
            rules,
            stowage,
            retrieval,
            vendor,
            deposit,
            movements,
            TransferExecutionPolicy.HasMovement(retrieval.NeededQuantity, deposit),
            ListingPlanEvaluator.HasUnknownLinkedDemand(runtime.State, runtime.Browser, runtime.Owner, plan.Id),
            rows);
        transferProjectionBuildCount++;
        return transferWorkbenchProjection;
    }

    public void RequestSelectedTransferReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(state.Snapshot(), current.Owner);
        if (plan is null) return;
        requestTransferReview(plan.Id, plan.Name);
    }

    // Capture/presentation uses RequestSelectedTransferReview and must never save.
    // Only the explicitly labelled user action commits the draft before review.
    private void SaveAndRequestTransferReview()
    {
        if (transferPlanEditor.HasChanges && !transferPlanEditor.Save(runtimeSnapshots.Current.Owner)) return;
        RequestSelectedTransferReview();
    }

    public void RequestSelectedVendorReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        requestVendorReview(ResolveProjection(current, plan).Vendor);
    }

    private static RetrievalPlan BuildTransferRetrievalEvaluation(
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
