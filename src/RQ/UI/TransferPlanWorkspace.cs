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
    private readonly DalamudTableProjection<TransferWorkbenchRow> transferWorkbenchTable;
    private TransferWorkbenchProjection? transferWorkbenchProjection;
    private int transferProjectionBuildCount;
    private int renderedTransferRowCount;
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
        transferWorkbenchTable = CreateTable();
    }

    public TransferPlanWorkspaceSnapshot Snapshot() =>
        new(transferProjectionBuildCount, renderedTransferRowCount);

    public void ClearVendorStatus() => vendorStatus = string.Empty;

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;


    private DalamudTableProjection<TransferWorkbenchRow> CreateTable() => new(
    [
        new(
            "Item",
            1.5f,
            row => $"{row.Rule.ItemName} {QualityLabel(row.Rule.Quality)}",
            row => row.Rule.ItemName,
            ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
            Draw: row =>
            {
                ImGui.TextUnformatted(row.Rule.ItemName);
                ImGui.SameLine();
                ImGui.TextDisabled(QualityLabel(row.Rule.Quality));
            },
            Id: "item"),
        new(
            "On player",
            64,
            row => row.PlayerQuantity.ToString("N0"),
            row => row.PlayerQuantity,
            Id: "player"),
        new(
            "Target",
            184,
            TransferTargetText,
            row => row.ListingContribution.IsKnown ? row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity : row.Rule.TargetQuantity,
            Draw: DrawTransferTarget,
            Id: "target",
            HeaderTooltip: "Desired player quantity with its signed delta from current player stock."),
        new(
            "Accessible storage",
            128,
            row => row.AccessibleStorageQuantity.ToString("N0"),
            row => row.AccessibleStorageQuantity,
            Id: "accessible-storage",
            HeaderTooltip: "Current matching quantity in accessible retainer storage."),
        new(
            "Outcome",
            156,
            row => TransferOutcome(row).Text,
            row => TransferOutcome(row).Text,
            Draw: DrawTransferOutcome,
            Id: "outcome",
            HeaderTooltip: "Executable result under the stock and capacity evidence currently available."),
        new(
            "Vendor",
            148,
            VendorProcurementText,
            row => row.VendorLine?.ApprovedQuantity ?? 0,
            Draw: DrawVendorProcurement,
            Id: "vendor",
            HeaderTooltip: "Reviewed ordinary-gil coverage for the shortage left after accessible retainer stock."),
        new(
            "Route",
            1.1f,
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            row => TransferPresentation.RouteSummary(row.Rule.Routing, row.Runtime.Retainers, row.Owner),
            ImGuiTableColumnFlags.WidthStretch,
            Draw: row => DrawInlineTransferRoute(row.Owner, row.PlanId, row.Rule, row.Runtime),
            Id: "route"),
        new(
            "Listing shortfall",
            118,
            TransferListingShortfall,
            row => row.ListingContribution.IsKnown ? row.ListingContribution.Value : -1,
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide,
            Id: "listing-shortfall",
            HeaderTooltip: "Units still needed by the linked Listing Plan."),
        new("##remove", 28, _ => string.Empty, Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, Draw: row =>
        {
            if (ImGui.SmallButton($"X##remove-transfer:{row.Rule.Id}"))
                RemoveTransferRule(row.Owner, row.PlanId, row.Rule.Id);
        }, Id: "remove"),
    ]);


    private static string TransferTargetText(TransferWorkbenchRow row) =>
        TransferWorkbenchPresentation.Target(
            row.Line?.DesiredPlayerQuantity ?? row.Rule.TargetQuantity,
            row.PlayerQuantity,
            row.ListingContribution.IsKnown);

    private void DrawTransferTarget(TransferWorkbenchRow row)
    {
        var listingShortfall = row.ListingContribution.IsKnown ? row.ListingContribution.Value : 0;
        var target = row.ListingContribution.IsKnown
            ? row.Rule.TargetQuantity + listingShortfall
            : row.Rule.TargetQuantity;
        ImGui.SetNextItemWidth(66);
        if (ImGui.InputInt($"##target:{row.Rule.Id}", ref target, 0))
            UpdateTransferRule(row.Owner, row.PlanId, row.Rule.Id, draftRule =>
                draftRule.TargetQuantity = Math.Max(0, target - listingShortfall));
        var targetHovered = ImGui.IsItemHovered();
        ImGui.SameLine();
        if (!row.ListingContribution.IsKnown)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "(+?)");
        else
            ImGui.TextColored(
                TransferPresentation.ActionColor(row.Line?.Action),
                $"({TransferPresentation.SignedQuantity(row.Difference)})");
        if (targetHovered || ImGui.IsItemHovered())
            ImGui.SetTooltip(row.ListingContribution.IsKnown
                ? $"Target {row.Rule.TargetQuantity + listingShortfall:N0}: independent {row.Rule.TargetQuantity:N0} + Listing Plan {listingShortfall:N0}; current player stock {row.PlayerQuantity:N0}."
                : $"Independent target {row.Rule.TargetQuantity:N0}; Listing Plan demand is not yet known.");
        if (row.ListingLink is not null)
            DrawTransferSource(row);
    }

    private void DrawTransferSource(TransferWorkbenchRow row)
    {
        if (row.ListingLink is null)
        {
            ImGui.TextDisabled("Independent");
            return;
        }
        var contribution = row.ListingContribution.IsKnown
            ? row.ListingContribution.Value.ToString("N0")
            : "?";
        ImGui.TextDisabled($"Plan +{contribution}");
        ImGui.TextDisabled($"Independent {row.Rule.TargetQuantity:N0}");
        ImGui.SameLine();
        if (!ImGui.SmallButton($"Unlink##transfer-listing:{row.ListingLink.Id}"))
            return;
        try
        {
            state.Mutate(document => ListingPlanCatalog.Unlink(
                document,
                row.Owner,
                row.PlanId,
                row.ListingLink.ListingPlanId,
                row.ListingLink.ItemId,
                row.ListingLink.Quality));
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private static string TransferListingShortfall(TransferWorkbenchRow row) =>
        row.ListingLink is null
            ? "—"
            : row.ListingContribution.IsKnown
                ? row.ListingContribution.Value.ToString("N0")
                : "Unknown";

    private static TransferOutcomePresentation TransferOutcome(TransferWorkbenchRow row)
    {
        if (!row.Rule.Enabled)
            return new("Off");
        if (!row.ListingContribution.IsKnown)
            return new("Verify listing shortfall");
        var action = row.Line?.Action ?? StowageAction.None;
        return TransferWorkbenchPresentation.Outcome(
            action,
            Math.Abs(row.Difference),
            row.AccessibleStorageQuantity,
            row.RoutedDepositQuantity);
    }

    private static void DrawTransferOutcome(TransferWorkbenchRow row)
    {
        var outcome = TransferOutcome(row);
        var primaryColor = !row.Rule.Enabled
            ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
            : !row.ListingContribution.IsKnown
                ? new Vector4(1f, .7f, .3f, 1f)
                : TransferPresentation.ActionColor(row.Line?.Action);
        ImGui.TextColored(primaryColor, outcome.Primary);
        if (string.IsNullOrWhiteSpace(outcome.Constraint))
            return;
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"· {outcome.Constraint}");
    }

    private static string VendorProcurementText(TransferWorkbenchRow row)
    {
        var line = row.VendorLine;
        if (!row.Rule.AllowVendorPurchase)
            return "Off";
        if (line is null)
            return "Not needed";
        return line.IsReady
            ? $"Buy {line.ApprovedQuantity:N0} · {line.SelectedCandidate!.Offer.UnitPriceGil:N0} ea"
            : line.State switch
            {
                TransferVendorProcurementState.ExactQualityUnsupported => "Any quality required",
                TransferVendorProcurementState.OfferNotCataloged => "No gil vendor",
                _ => "Vendor unavailable",
            };
    }

    private static void DrawVendorProcurement(TransferWorkbenchRow row)
    {
        var text = VendorProcurementText(row);
        ImGui.TextColored(
            row.VendorLine?.IsReady == true
                ? new Vector4(.92f, .72f, .35f, 1f)
                : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            text);
        if (row.VendorLine is { } line && ImGui.IsItemHovered())
            ImGui.SetTooltip(line.Message);
    }


    private void DrawTableColumnsToolbar<TRow>(
        DalamudTableProjection<TRow> table,
        string id,
        string context)
    {
        ImGui.TextDisabled(context);
        ImGui.SameLine();
        var buttonWidth = ImGui.CalcTextSize("Columns").X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonWidth));
        table.DrawColumnMenuButton(id);
        reviewRegistry.RegisterLastButton(
            $"quartermaster.{id}.columns",
            $"Manage {context.ToLowerInvariant()} columns",
            true,
            table.RequestColumnMenu,
            "Available");
    }


    public void Draw(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = StowagePlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null && plans.Count > 0)
        {
            selected = plans[0];
            workbench.SelectedStowagePlanId = selected.Id;
        }

        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 330));
        if (ImGui.BeginCombo("##RQTransferPlan", selected?.Name ?? "Choose a Transfer Plan"))
        {
            foreach (var plan in plans)
            {
                if (ImGui.Selectable($"{plan.Name}##transfer:{plan.Id}", selected?.Id == plan.Id))
                {
                    workbench.SelectedStowagePlanId = plan.Id;
                    selected = plan;
                }
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
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate") && selected is not null)
            transferPlanEditor.Open(StowagePlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
        if (selected is null)
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
            ImGui.TextUnformatted("No Transfer Plans yet.");
            ImGui.TextDisabled("Create one, then select stock on the left or add items by name.");
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
        var canExecute = availability.CanExecute;

        ImGui.SameLine();
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button("Execute plan"))
            requestTransferReview(selected.Id, selected.Name);
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
                    requestTransferReview(currentPlan.Id, currentPlan.Name);
            },
            canExecute
                ? projection.HasUnknownListingDemand ? "Listing demand will be verified first" : $"{movements:N0} movements"
                : availability.BlockReason);

        var vendor = projection.Vendor;
        var recovery = runtime.State.TransferPlanRecovery;
        var hasCurrentRecovery = recovery is not null &&
            recovery.Owner.Matches(owner) &&
            recovery.PlanId == selected.Id &&
            recovery.PlanRevision == selected.Revision;

        ImGui.Separator();
        ImGui.TextUnformatted($"{ownerRules.Count:N0} items");
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.TextUnformatted(projection.HasUnknownListingDemand ? "Movements pending verification" : $"{movements:N0} movements");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.52f, .79f, .94f, 1f), projection.HasUnknownListingDemand ? "Retrieve —" : $"Retrieve {retrieval.NeededQuantity:N0}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.53f, .83f, .64f, 1f), projection.HasUnknownListingDemand ? "Stow —" : $"Stow {surplusBatch.RequestedQuantity:N0}");
        if (!projection.HasUnknownListingDemand && vendor.ApprovedQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.92f, .72f, .35f, 1f), $"Vendor {vendor.ApprovedQuantity:N0} · max {vendor.MaximumGil:N0} gil");
        }
        var remainingShort = Math.Max(0, retrieval.MissingQuantity - vendor.ApprovedQuantity);
        if (!projection.HasUnknownListingDemand && remainingShort > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"{remainingShort:N0} short");
        }
        if (!projection.HasUnknownListingDemand && surplusBatch.RemainingQuantity > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .4f, .4f, 1f), $"No room for {surplusBatch.RemainingQuantity:N0}");
        }
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
        DrawTableColumnsToolbar(transferWorkbenchTable, "RQTransferColumns", "Plan quantities use the latest accessible stock.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                     ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                     ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 4;
        IReadOnlyList<TransferWorkbenchRow> transferRows = projection.Rows;
        renderedTransferRowCount = 0;
        if (transferWorkbenchTable.Begin(
                "RQTransferWorkbenchV2",
                new DalamudTableLayout(
                    new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y - footerHeight)),
                    flags)))
        {
            unsafe
            {
                transferRows = transferWorkbenchTable.Apply(transferRows, ImGui.TableGetSortSpecs());
            }
            renderedTransferRowCount = transferWorkbenchTable.DrawClippedRows(
                transferRows,
                (row, _) =>
                {
                    transferWorkbenchTable.DrawRow(
                        row,
                        row.Rule.Enabled ? null : new Vector4(.38f, .12f, .14f, .42f),
                        id: $"transfer:{row.Rule.Id}");
                });
            transferWorkbenchTable.End();
        }

        ImGui.TextDisabled(
            availability.BlockReason ??
            "Balanced items stay visible and are skipped during execution.");
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
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        requestTransferReview(plan.Id, plan.Name);
    }

    public void RequestSelectedVendorReview()
    {
        var current = runtimeSnapshots.Current;
        var plan = ResolveSelectedStowagePlan(current.State, current.Owner);
        if (plan is null)
            return;
        requestVendorReview(ResolveProjection(current, plan).Vendor);
    }

    private void DrawInlineTransferRoute(
        OwnerScope owner,
        Guid planId,
        TargetPlanItem rule,
        QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##inline-route:{rule.Id}",
                TransferPresentation.RouteSummary(rule.Routing, runtime.Retainers, owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
        {
            if (ImGui.Selectable(TransferPresentation.RoutingModeLabel(mode), rule.Routing.Mode == mode))
                UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    draftRule.Routing.Mode = mode);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Fallback");
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
        {
            if (ImGui.Selectable(TransferPresentation.OverflowLabel(overflow), rule.Routing.Overflow == overflow))
                UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    draftRule.Routing.Overflow = overflow);
        }

        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        if (ownerRetainers.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Preferred first");
            foreach (var retainer in ownerRetainers)
            {
                var preferred = rule.Routing.PreferredRetainerIds.Contains(retainer.RetainerId);
                if (ImGui.Selectable(
                        $"{retainer.RetainerName}##inline-preferred:{rule.Id}:{retainer.RetainerId}",
                        preferred))
                {
                    UpdateTransferRule(owner, planId, rule.Id, draftRule =>
                    {
                        if (!draftRule.Routing.PreferredRetainerIds.Remove(retainer.RetainerId))
                            draftRule.Routing.PreferredRetainerIds.Add(retainer.RetainerId);
                        draftRule.Routing.Mode = StowageRoutingMode.HomeFirst;
                    });
                }
            }
        }
        ImGui.EndCombo();
    }

    private void UpdateTransferRule(
        OwnerScope owner,
        Guid planId,
        Guid ruleId,
        Action<TargetPlanItem> update)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                var draftRule = draft.Rules.Single(rule => rule.Id == ruleId);
                update(draftRule);
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }

    private void RemoveTransferRule(OwnerScope owner, Guid planId, Guid ruleId)
    {
        try
        {
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                var draft = StowagePlanCatalog.Draft(document, owner, planId);
                draft.Rules.RemoveAll(rule => rule.Id == ruleId);
                return StowagePlanCatalog.Apply(document, owner, draft).Id;
            });
            transferExecution.ClearInlineError();
        }
        catch (Exception exception)
        {
            transferExecution.ReportInlineError(exception.Message);
        }
    }


    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };


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
