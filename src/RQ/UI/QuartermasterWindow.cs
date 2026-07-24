using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Filtering;
using Lumina.Excel.Sheets;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

public sealed class QuartermasterWindow : Window
{
    private readonly StateRepository state;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly AutoRetainerRefreshService autoRetainer;
    private readonly IDataManager dataManager;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly WorkbenchState workbench = new();
    private readonly BrowserQueryController queries = new();
    private readonly Dictionary<(uint ItemId, bool IsHighQuality), QuickDepositSelection> quickDeposits = [];
    private string transferStatus = "No transfer has run.";
    private WorkbenchView? requestedView;
    private Task? activeTransferTask;
    private bool clearAgentReviewWindowOverride;
    private StowagePlanDraft? stowageDraft;
    private readonly HashSet<Guid> selectedStowageRuleIds = [];
    private Guid? activeStowageRuleId;
    private Guid? selectedItemGroupId;
    private string stowageItemSearch = string.Empty;
    private string stowageRuleFilter = string.Empty;
    private ItemChoice? selectedStowageChoice;
    private string stowageEditorError = string.Empty;
    private int bulkQuality = -1;
    private int bulkRoutingMode = -1;
    private int bulkOverflow = -1;
    private int bulkStowageTarget = -1;
    private bool requestStowageEditorOpen;
    private bool stowageEditorVisible;
    private RestockPlanDraft? restockDraft;
    private readonly HashSet<Guid> selectedRestockItemIds = [];
    private Guid? activeRestockItemId;
    private Guid? selectedRestockItemGroupId;
    private string restockItemSearch = string.Empty;
    private string restockItemFilter = string.Empty;
    private ItemChoice? selectedRestockChoice;
    private string restockEditorError = string.Empty;
    private int bulkRestockQuality = -1;
    private int bulkRestockTarget = -1;
    private int bulkRestockNoteMode = -1;
    private bool requestRestockEditorOpen;
    private bool restockEditorVisible;
    private Guid? requestDeleteRestockPlanId;
    private Guid? requestDeleteStowagePlanId;
    private ItemGroupDraft? itemGroupDraft;
    private WorkbenchView? itemGroupEditorOrigin;
    private readonly HashSet<ItemGroupItem> selectedItemGroupItems = [];
    private string itemGroupFilter = string.Empty;
    private string itemGroupItemSearch = string.Empty;
    private ItemChoice? selectedItemGroupChoice;
    private ItemQualityPolicy itemGroupAddQuality = ItemQualityPolicy.Any;
    private string itemGroupEditorError = string.Empty;
    private bool requestDeleteItemGroup;

    public QuartermasterWindow(
        StateRepository state,
        QuartermasterRuntimeSnapshotSource runtimeSnapshots,
        OperationJournal journal,
        TransferCoordinator transfers,
        AutoRetainerRefreshService autoRetainer,
        IDataManager dataManager,
        PluginConfiguration configuration,
        System.Action saveConfiguration,
        AgentBridgeUiReviewRegistry reviewRegistry)
        : base("Quartermaster###RQMain", ImGuiWindowFlags.NoScrollbar)
    {
        this.state = state;
        this.runtimeSnapshots = runtimeSnapshots;
        this.journal = journal;
        this.transfers = transfers;
        this.autoRetainer = autoRetainer;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.reviewRegistry = reviewRegistry;
        Size = new Vector2(1280, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(980, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public Guid? SelectedRestockPlanId =>
        ResolveSelectedRestockPlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Id;

    public string? SelectedRestockPlanName =>
        ResolveSelectedRestockPlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Name;

    public int SelectedRestockNeededQuantity
    {
        get
        {
            var runtime = runtimeSnapshots.Current;
            var plan = ResolveSelectedRestockPlan(runtime.State, runtime.Owner);
            return plan is null ? 0 : BuildRestockEvaluation(runtime, plan).NeededQuantity;
        }
    }

    public string CurrentWorkspace => workbench.View.ToString().ToLowerInvariant();
    public string CurrentTransferDirection =>
        workbench.View == WorkbenchView.Stowage ? "stowage" : "restock";
    public bool StowageEditorOpen => stowageDraft is not null && (requestStowageEditorOpen || stowageEditorVisible);
    public bool RestockEditorOpen => restockDraft is not null && (requestRestockEditorOpen || restockEditorVisible);
    public bool ItemGroupEditorOpen => itemGroupDraft is not null;
    public Guid? SelectedItemGroupId => itemGroupDraft is { IsNew: false } ? itemGroupDraft.GroupId : null;
    public string? SelectedItemGroupName => itemGroupDraft?.Name;
    public bool ItemGroupEditorHasUnsavedChanges =>
        itemGroupDraft is not null && ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft);
    public bool PlanEditorHasUnsavedChanges
    {
        get
        {
            var snapshot = state.Snapshot();
            var owner = runtimeSnapshots.Current.Owner;
            return (restockDraft is not null && RestockPlanCatalog.HasChanges(snapshot, owner, restockDraft)) ||
                   (stowageDraft is not null && StowagePlanCatalog.HasChanges(snapshot, owner, stowageDraft));
        }
    }

    public Guid? SelectedStowagePlanId =>
        ResolveSelectedStowagePlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Id;

    public string? SelectedStowagePlanName =>
        ResolveSelectedStowagePlan(runtimeSnapshots.Current.State, runtimeSnapshots.Current.Owner)?.Name;

    public override void Draw()
    {
        reviewRegistry.BeginFrame();
        try
        {
            DrawContent();
        }
        finally
        {
            reviewRegistry.EndFrame();
        }
    }

    private void DrawContent()
    {
        var runtime = runtimeSnapshots.Current;
        workbench.EnsureScope(runtime.Browser);
        var scopedRetainerCount = runtime.Retainers.Values.Count(retainer => runtime.Owner.Matches(retainer.Owner));

        ImGui.TextUnformatted(runtime.Owner.HasStableIdentity ? $"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName}" : "Owner scope unavailable");
        ImGui.SameLine();
        ImGui.TextDisabled($"{scopedRetainerCount:N0} cached retainers");
        ImGui.SameLine();
        var automationBusy = transfers.IsRunning || autoRetainer.IsRefreshing || autoRetainer.IsQueued;
        if (automationBusy)
            ImGui.BeginDisabled();
        if (ImGui.Button("Refresh retainers"))
            autoRetainer.Start();
        if (automationBusy)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.refresh-retainers",
            "Refresh retainers",
            !automationBusy,
            () => autoRetainer.Start(),
            autoRetainer.Status);
        ImGui.SameLine();
        ImGui.TextDisabled(autoRetainer.Status);

        if (requestedView is { } requested)
        {
            if (requested != WorkbenchView.Stowage)
                CloseStowageEditor();
            if (requested != WorkbenchView.Restock)
                CloseRestockEditor();
        }

        if (ImGui.BeginTabBar("RQViews"))
        {
            var plansRequested = requestedView is WorkbenchView.Stock or WorkbenchView.Restock or WorkbenchView.Stowage;
            var stockOpen = ImGui.BeginTabItem("Stock & plans", plansRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            reviewRegistry.RegisterLastButton(
                "quartermaster.workspace.stock",
                "Show stock and plans",
                true,
                () => requestedView = WorkbenchView.Stock,
                workbench.View is WorkbenchView.Restock or WorkbenchView.Stowage ? "Selected" : "Available");
            if (stockOpen)
            {
                if (workbench.View is not (WorkbenchView.Restock or WorkbenchView.Stowage))
                    workbench.View = requestedView == WorkbenchView.Stowage ? WorkbenchView.Stowage : WorkbenchView.Restock;
                DrawStockAndPlans(runtime);
                ImGui.EndTabItem();
            }
            var listingsOpen = ImGui.BeginTabItem($"Listings ({runtime.Browser.Listings.Count:N0})", requestedView == WorkbenchView.Listings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            RegisterWorkspaceControl("listings", WorkbenchView.Listings);
            if (listingsOpen)
            {
                workbench.View = WorkbenchView.Listings;
                DrawListings(runtime.Browser, runtime.Revision);
                ImGui.EndTabItem();
            }
            var activityOpen = ImGui.BeginTabItem("Activity", requestedView == WorkbenchView.Activity ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
            RegisterWorkspaceControl("activity", WorkbenchView.Activity);
            if (activityOpen)
            {
                workbench.View = WorkbenchView.Activity;
                DrawOperation(runtime.Owner);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
        DrawRestockEditorModal(runtime);
        DrawStowageEditorModal(runtime);
    }

    private void DrawStockAndPlans(QuartermasterRuntimeSnapshot runtime)
    {
        var flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQStockAndPlans", 2, flags, new Vector2(0, Math.Max(260, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Stock", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Plans", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQStockPane", Vector2.Zero, false))
            DrawStock(runtime);
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("RQPlanPane", Vector2.Zero, false))
        {
            if (requestedView is WorkbenchView.Restock or WorkbenchView.Stowage)
                workbench.View = requestedView.Value;
            ImGui.TextUnformatted("Transfer plans");
            ImGui.SameLine();
            DrawTransferModeButton("Restock <- from retainers", "restock", WorkbenchView.Restock);
            ImGui.SameLine();
            DrawTransferModeButton("Stowage -> to retainers", "stowage", WorkbenchView.Stowage);
            ImGui.SameLine();
            ImGui.TextDisabled(workbench.View == WorkbenchView.Stowage
                ? "Put carried surplus away using explicit routing."
                : "Bring missing stock onto this character.");
            ImGui.Separator();
            if (workbench.View == WorkbenchView.Stowage)
                DrawStowageWorkspace(runtime);
            else
                DrawRestockPlan(runtime);
        }
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void DrawTransferModeButton(string label, string id, WorkbenchView view)
    {
        var active = workbench.View == view;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.18f, .35f, .46f, 1f));
        if (ImGui.Button(label))
            workbench.View = view;
        if (active)
            ImGui.PopStyleColor();
        reviewRegistry.RegisterLastButton(
            $"quartermaster.workspace.{id}",
            $"Show {view} transfer direction",
            true,
            () =>
            {
                workbench.View = view;
                requestedView = view;
            },
            active ? "Selected" : "Available");
    }

    private void RegisterWorkspaceControl(string id, WorkbenchView view) =>
        reviewRegistry.RegisterLastButton(
            $"quartermaster.workspace.{id}",
            $"Show {view} workspace",
            true,
            () => requestedView = view,
            workbench.View == view ? "Selected" : "Available");

    public void OpenReviewSurface(string target)
    {
        var view = target.Trim().ToLowerInvariant() switch
        {
            "listings" => WorkbenchView.Listings,
            "operation" or "activity" => WorkbenchView.Activity,
            "restock" => WorkbenchView.Restock,
            "stowage" => WorkbenchView.Stowage,
            _ => WorkbenchView.Stock,
        };
        if (view != WorkbenchView.Stowage)
            CloseStowageEditor();
        if (view != WorkbenchView.Restock)
            CloseRestockEditor();
        requestedView = view;
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        clearAgentReviewWindowOverride = true;
    }

    public void CloseReviewSurface()
    {
        ClosePlanEditors();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    public override void OnClose()
    {
        ClosePlanEditors();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    private void ClearAgentReviewWindowOverride()
    {
        if (!clearAgentReviewWindowOverride)
            return;
        Collapsed = null;
        CollapsedCondition = ImGuiCond.None;
        clearAgentReviewWindowOverride = false;
    }

    private void DrawStock(QuartermasterRuntimeSnapshot runtime)
    {
        var projection = runtime.Browser;
        DrawBrowserToolbar(projection, listings: false);
        var result = queries.QueryItems(
            projection,
            workbench.ItemFilter,
            workbench.ScopeKey,
            workbench.ItemFilterState.IsInputActive,
            runtime.Revision);
        if (!result.Filter.IsValid)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        if (workbench.SelectedStock is { } selected)
        {
            var stowageMode = workbench.View == WorkbenchView.Stowage;
            ImGui.TextUnformatted(selected.ItemName);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var stagedTarget = workbench.StagedTargetText;
            if (ImGui.InputText(
                    $"{(stowageMode ? "Keep on player" : "Target on player")}##RQStockTarget",
                    ref stagedTarget,
                    12,
                    ImGuiInputTextFlags.CharsDecimal))
                workbench.StagedTargetText = stagedTarget;
            var canAdd = runtime.Owner.HasStableIdentity &&
                         workbench.StagedTarget is { } target &&
                         (stowageMode ? target >= 0 : target > 0);
            if (!canAdd)
                ImGui.BeginDisabled();
            if (stowageMode)
            {
                var plan = ResolveSelectedStowagePlan(runtime.State, runtime.Owner)
                           ?? StowagePlanCatalog.OwnerPlans(runtime.State, runtime.Owner).FirstOrDefault();
                var label = plan is null ? "Add to new Stowage Plan" : $"Add to {plan.Name}";
                if (ImGui.Button(label, new Vector2(-1, 0)) && workbench.StagedTarget is { } keep)
                    AddStockSelectionToStowage(runtime, selected, keep, plan);
                reviewRegistry.RegisterLastButton(
                    $"quartermaster.stowage.stage-rule.{selected.ItemId}",
                    $"{label}: {selected.ItemName}",
                    canAdd,
                    () =>
                    {
                        var current = runtimeSnapshots.Current;
                        if (workbench.SelectedStock is { } stock && workbench.StagedTarget is { } keep)
                            AddStockSelectionToStowage(current, stock, keep, ResolveSelectedStowagePlan(current.State, current.Owner));
                    },
                    canAdd ? "Opens a review draft" : "Enter a carried keep quantity");
            }
            else
            {
                var plan = ResolveSelectedRestockPlan(runtime.State, runtime.Owner)
                           ?? RestockPlanCatalog.OwnerPlans(runtime.State, runtime.Owner).FirstOrDefault();
                var label = plan is null ? "Add to new Restock Plan" : $"Add to {plan.Name}";
                if (ImGui.Button(label, new Vector2(-1, 0)) && workbench.StagedTarget is { } targetQuantity)
                    AddStockSelectionToRestock(runtime, selected, targetQuantity, plan);
                reviewRegistry.RegisterLastButton(
                    $"quartermaster.restock.stage.{selected.ItemId}",
                    $"{label}: {selected.ItemName}",
                    canAdd,
                    () =>
                    {
                        var current = runtimeSnapshots.Current;
                        if (workbench.SelectedStock is { } stock && workbench.StagedTarget is { } targetQuantity)
                            AddStockSelectionToRestock(current, stock, targetQuantity, ResolveSelectedRestockPlan(current.State, current.Owner));
                    },
                    canAdd ? "Opens a review draft" : "Enter a carried target");
            }
            if (!canAdd)
                ImGui.EndDisabled();
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQStockTable", 6, flags, new Vector2(0, Math.Max(180, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Quick deposit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableHeadersRow();
        foreach (var item in SortItems(result.Items))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{(workbench.IsExpanded(item.ItemId) ? "v" : "> ")}##expand{item.ItemId}"))
                workbench.ToggleExpanded(item.ItemId);
            ImGui.SameLine();
            if (ImGui.Selectable($"{item.ItemName}##stock{item.ItemId}", workbench.SelectedStock?.ItemId == item.ItemId))
                workbench.Select(item);
            reviewRegistry.RegisterLastButton(
                $"quartermaster.stock.select.{item.ItemId}",
                $"Select {item.ItemName} in Stock",
                true,
                () => workbench.Select(item),
                workbench.SelectedStock?.ItemId == item.ItemId ? "Selected" : "Available");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.TotalQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.PlayerQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.RetainerQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Join(", ", item.Stacks.Select(stack => stack.OwnerName).Distinct().Take(2)));
            ImGui.TableNextColumn();
            var playerStacks = item.Stacks.Where(stack => stack.ScopeKind == BrowserScopeKind.Player).ToArray();
            if (playerStacks.Length > 0)
            {
                if (ImGui.SmallButton($"Stage##quick{item.ItemId}"))
                    StageQuickDeposit(item);
                reviewRegistry.RegisterLastButton(
                    $"quartermaster.quick-deposit.stage.{item.ItemId}",
                    $"Stage {item.ItemName} for Quick Deposit",
                    true,
                    () => StageQuickDeposit(item),
                    $"{playerStacks.Sum(stack => stack.Quantity):N0} carried");
            }
            if (workbench.IsExpanded(item.ItemId))
            {
                foreach (var stack in item.Stacks)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextDisabled($"  {stack.Storage} · slot {(stack.SlotIndex is { } slot ? slot + 1 : 0)}");
                    ImGui.TableNextColumn(); ImGui.TextDisabled(stack.Quantity.ToString("N0"));
                    ImGui.TableNextColumn(); ImGui.TextDisabled(stack.ScopeKind == BrowserScopeKind.Player ? stack.Quantity.ToString("N0") : "-");
                    ImGui.TableNextColumn(); ImGui.TextDisabled(stack.ScopeKind == BrowserScopeKind.Retainer ? stack.Quantity.ToString("N0") : "-");
                    ImGui.TableNextColumn(); ImGui.TextDisabled($"{stack.OwnerName} · {stack.Quality}");
                    ImGui.TableNextColumn();
                    if (stack.ScopeKind == BrowserScopeKind.Player)
                    {
                        var highQuality = stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ;
                        if (ImGui.SmallButton($"Stage##quick{item.ItemId}:{stack.Storage}:{stack.SlotIndex}"))
                            StageQuickDeposit(item, highQuality);
                        reviewRegistry.RegisterLastButton(
                            $"quartermaster.quick-deposit.stage.{item.ItemId}.{(highQuality ? "hq" : "nq")}.{stack.Storage}.{stack.SlotIndex}",
                            $"Stage {item.ItemName}{(highQuality ? " HQ" : string.Empty)} stack for Quick Deposit",
                            true,
                            () => StageQuickDeposit(item, highQuality),
                            $"{stack.Quantity:N0} carried");
                    }
                }
            }
        }
        ImGui.EndTable();
    }

    private void DrawRestockPlan(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = RestockPlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedRestockPlan(runtime.State, owner);
        if (selected is null && plans.Count > 0)
        {
            selected = plans[0];
            workbench.SelectedRestockPlanId = selected.Id;
        }

        ImGui.SetNextItemWidth(Math.Max(170, ImGui.GetContentRegionAvail().X - 230));
        if (ImGui.BeginCombo("##RQRestockPlan", selected?.Name ?? "Choose a Restock Plan"))
        {
            foreach (var plan in plans)
                if (ImGui.Selectable($"{plan.Name}##restock{plan.Id}", selected?.Id == plan.Id))
                    workbench.SelectedRestockPlanId = plan.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Edit plan") && selected is not null)
            OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), owner, selected.Id));
        if (selected is null)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.restock.edit",
            "Open the selected Restock Plan editor",
            selected is not null,
            () =>
            {
                var current = runtimeSnapshots.Current;
                var currentPlan = ResolveSelectedRestockPlan(state.Snapshot(), current.Owner);
                if (currentPlan is not null)
                    OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), current.Owner, currentPlan.Id));
            },
            selected?.Name ?? "No plan selected");
        ImGui.SameLine();
        if (ImGui.Button("Manage..."))
            ImGui.OpenPopup("RQRestockPlanManage");
        if (ImGui.BeginPopup("RQRestockPlanManage"))
        {
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("New plan"))
                OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();

            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Duplicate plan") && selected is not null)
                OpenRestockEditor(RestockPlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
            if (selected is null)
                ImGui.EndDisabled();

            var canCreateFromStowage = owner.HasStableIdentity &&
                                       StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Create from Stowage") && canCreateFromStowage)
                OpenRestockEditor(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
            if (!canCreateFromStowage)
                ImGui.EndDisabled();

            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Delete plan") && selected is not null)
                requestDeleteRestockPlanId = selected.Id;
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        DrawDeleteRestockPlanPopup(owner, runtime.State);

        selected = ResolveSelectedRestockPlan(state.Snapshot(), owner);
        if (selected is null)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("No Restock Plans yet.");
            ImGui.TextDisabled("Create one here, then add items from Stock or by name.");
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Button("New Restock Plan"))
                OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), owner));
            reviewRegistry.RegisterLastButton(
                "quartermaster.restock.new",
                "Open a new Restock Plan draft",
                owner.HasStableIdentity,
                () => OpenRestockEditor(RestockPlanCatalog.NewDraft(state.Snapshot(), runtimeSnapshots.Current.Owner)),
                owner.HasStableIdentity ? "Nothing is saved until Apply" : "Owner unavailable");
            ImGui.SameLine();
            var canCreateFromStowage = StowagePlanMigration.OwnerPlan(runtime.State, owner) is not null;
            if (!canCreateFromStowage)
                ImGui.BeginDisabled();
            if (ImGui.Button("Create from Stowage") && canCreateFromStowage)
                OpenRestockEditor(RestockPlanCatalog.FromStowageDraft(state.Snapshot(), owner));
            if (!canCreateFromStowage)
                ImGui.EndDisabled();
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();
            return;
        }

        var evaluation = BuildRestockEvaluation(runtime, selected);
        ImGui.TextUnformatted(selected.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(selected.Enabled ? "Enabled" : "Disabled");
        ImGui.SameLine();
        ImGui.TextDisabled($"{selected.Items.Count:N0} items | need {evaluation.NeededQuantity:N0} | stored {evaluation.CoveredQuantity:N0}");
        var canExecute = selected.Enabled &&
                         evaluation.NeededQuantity > 0 &&
                         owner.HasStableIdentity &&
                         transfers.CanStart &&
                         !autoRetainer.IsRefreshing &&
                         !autoRetainer.IsQueued;
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Retrieve missing ({evaluation.NeededQuantity:N0})"))
        {
            var operation = journal.CreateRestock(owner, selected);
            StartTransfer(transfers.ExecuteRetrievalAsync(operation.OperationId));
        }
        if (!canExecute)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(transferStatus);

        var lines = evaluation.Lines.ToDictionary(line => line.PlanItemId);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQRestockPlanTable", 5, flags, new Vector2(0, Math.Max(220, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Have / goal", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableSetupColumn("Need / stored", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableHeadersRow();
            foreach (var item in selected.Items)
            {
                lines.TryGetValue(item.Id, out var line);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.Enabled ? "On" : "Off");
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{item.ItemName}##restockrow{item.Id}"))
                {
                    OpenRestockEditor(RestockPlanCatalog.Draft(state.Snapshot(), owner, selected.Id));
                    activeRestockItemId = item.Id;
                    selectedRestockItemIds.Clear();
                    selectedRestockItemIds.Add(item.Id);
                }
                ImGui.TextDisabled(QualityLabel(item.Quality));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line?.PlayerQuantity ?? 0:N0} / {item.TargetQuantity:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line?.NeededQuantity ?? 0:N0} / {line?.CachedRetainerQuantity ?? 0:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Notes);
            }
            ImGui.EndTable();
        }
    }

    private void OpenRestockEditor(RestockPlanDraft draft)
    {
        CloseStowageEditor();
        CloseItemGroupEditor();
        restockDraft = draft;
        selectedRestockItemIds.Clear();
        activeRestockItemId = draft.Items.FirstOrDefault()?.Id;
        selectedRestockItemGroupId = null;
        restockItemSearch = string.Empty;
        restockItemFilter = string.Empty;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
        bulkRestockQuality = -1;
        bulkRestockTarget = -1;
        bulkRestockNoteMode = -1;
        requestRestockEditorOpen = true;
        workbench.View = WorkbenchView.Restock;
        requestedView = WorkbenchView.Restock;
    }

    private void DrawRestockEditorModal(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Restock Plan##RQRestockEditor";
        restockEditorVisible = false;
        if (requestRestockEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestRestockEditorOpen = false;
        }
        if (restockDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1160, available.X - 48), Math.Min(700, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            CloseRestockEditor();
            return;
        }
        restockEditorVisible = true;
        if (itemGroupDraft is not null && itemGroupEditorOrigin == WorkbenchView.Restock)
        {
            DrawItemGroupEditor(runtime);
            ImGui.EndPopup();
            return;
        }

        var draft = restockDraft;
        ImGui.TextUnformatted("Edit plan");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.55f, .8f, .95f, 1f), "<- Restock");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##restockdraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        var planEnabled = draft.Enabled;
        if (ImGui.Checkbox("Enabled##restockdraft", ref planEnabled))
            draft.Enabled = planEnabled;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedRestockItemIds.Count:N0} selected of {draft.Items.Count:N0} items");

        DrawRestockEditorToolbar(draft);
        DrawRestockEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(restockEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), restockEditorError);

        if (ImGui.BeginChild("RQRestockItemTable", new Vector2(0, -42), true))
            DrawRestockDraftItems(draft);
        ImGui.EndChild();

        var snapshot = state.Snapshot();
        var canApply = RestockPlanCatalog.CanApply(snapshot, runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Items.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Items.Count:N0} items | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##restockeditor"))
        {
            CloseRestockEditor();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.restock.editor.cancel",
            "Discard the open Restock Plan draft",
            true,
            CloseRestockEditor,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##restockeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    RestockPlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedRestockPlanId = appliedId;
                CloseRestockEditor();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                restockEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawRestockEditorToolbar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawRestockDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var groups = ItemGroupCatalog.All(state.Snapshot());
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedRestockItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##restockgroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##restockgroup{group.Id}", selectedGroup?.Id == group.Id))
                    selectedRestockItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group##restock") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeRestockItemId ??= draft.Items.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##restock"))
            OpenItemGroupEditor(WorkbenchView.Restock);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.restock",
            "Open Item Groups from the Restock Plan editor",
            true,
            () => OpenItemGroupEditor(WorkbenchView.Restock),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##restockitemfilter", "Filter plan items", ref restockItemFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible##restock"))
        {
            foreach (var item in FilteredRestockDraftItems(draft))
                selectedRestockItemIds.Add(item.Id);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection##restock"))
            selectedRestockItemIds.Clear();
        ImGui.SameLine();
        if (selectedRestockItemIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group##restock"))
            OpenNewItemGroupFromRestockSelection();
        if (selectedRestockItemIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftAdd(RestockPlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQRestockItemName", "Add item by name", ref restockItemSearch, 96))
            selectedRestockChoice = null;
        if (restockItemSearch.Trim().Length >= 2 && selectedRestockChoice is null)
        {
            foreach (var choice in SearchItems(restockItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##restockchoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedRestockChoice = choice;
                    restockItemSearch = choice.Name;
                }
        }
        if (selectedRestockChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##restockdraft"))
        {
            var choice = selectedRestockChoice;
            var existing = draft.Items.FirstOrDefault(item =>
                item.ItemId == choice.ItemId && item.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new RestockPlanItem
                {
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 1,
                };
                draft.Items.Add(existing);
            }
            activeRestockItemId = existing.Id;
            selectedRestockItemIds.Clear();
            selectedRestockItemIds.Add(existing.Id);
            selectedRestockChoice = null;
            restockItemSearch = string.Empty;
        }
    }

    private void OpenItemGroupEditor(WorkbenchView origin)
    {
        itemGroupEditorOrigin = origin;
        var snapshot = state.Snapshot();
        var preferredId = origin == WorkbenchView.Restock
            ? selectedRestockItemGroupId
            : selectedItemGroupId;
        var group = snapshot.ItemGroups.FirstOrDefault(candidate => candidate.Id == preferredId)
                    ?? ItemGroupCatalog.All(snapshot).FirstOrDefault();
        itemGroupDraft = group is null
            ? ItemGroupCatalog.NewDraft(snapshot)
            : ItemGroupCatalog.Draft(snapshot, group.Id);
        ResetItemGroupEditorInput();
    }

    private void OpenNewItemGroupFromRestockSelection()
    {
        if (restockDraft is null)
            return;
        var selected = restockDraft.Items
            .Where(item => selectedRestockItemIds.Contains(item.Id))
            .ToArray();
        itemGroupEditorOrigin = WorkbenchView.Restock;
        itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", selected);
        ResetItemGroupEditorInput();
    }

    private void OpenNewItemGroupFromStowageSelection()
    {
        if (stowageDraft is null)
            return;
        var selected = stowageDraft.Rules
            .Where(rule => selectedStowageRuleIds.Contains(rule.Id))
            .ToArray();
        itemGroupEditorOrigin = WorkbenchView.Stowage;
        itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot(), "Item group", selected);
        ResetItemGroupEditorInput();
    }

    private void ResetItemGroupEditorInput()
    {
        selectedItemGroupItems.Clear();
        itemGroupFilter = string.Empty;
        itemGroupItemSearch = string.Empty;
        selectedItemGroupChoice = null;
        itemGroupAddQuality = ItemQualityPolicy.Any;
        itemGroupEditorError = string.Empty;
        requestDeleteItemGroup = false;
    }

    private void CloseItemGroupEditor()
    {
        itemGroupDraft = null;
        itemGroupEditorOrigin = null;
        selectedItemGroupItems.Clear();
        selectedItemGroupChoice = null;
        itemGroupEditorError = string.Empty;
        requestDeleteItemGroup = false;
    }

    private void DrawItemGroupEditor(QuartermasterRuntimeSnapshot runtime)
    {
        var draft = itemGroupDraft;
        if (draft is null)
            return;
        var hasChanges = ItemGroupCatalog.HasChanges(state.Snapshot(), draft);

        if (ImGui.Button("<- Back to plan##itemgroups"))
        {
            CloseItemGroupEditor();
            return;
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.back",
            "Discard the open Item Group draft and return to the Transfer Plan",
            true,
            CloseItemGroupEditor,
            hasChanges ? "Unsaved Item Group changes will be discarded" : "The Transfer Plan draft remains open");
        ImGui.SameLine();
        ImGui.TextUnformatted("Item groups");
        ImGui.SameLine();
        ImGui.TextDisabled("Shared by Restock and Stowage");
        if (hasChanges)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "Returning to the plan will discard unsaved Item Group changes.");

        if (!string.IsNullOrWhiteSpace(itemGroupEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), itemGroupEditorError);

        var bodyHeight = Math.Max(260, ImGui.GetContentRegionAvail().Y - 42);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQItemGroupEditor", 2, flags, new Vector2(0, bodyHeight)))
        {
            ImGui.TableSetupColumn("Groups", ImGuiTableColumnFlags.WidthFixed, 290);
            ImGui.TableSetupColumn("Members", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupList", Vector2.Zero, false))
                DrawItemGroupList(draft);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("RQItemGroupMembers", Vector2.Zero, false))
                DrawItemGroupMembers(draft, runtime);
            ImGui.EndChild();
            ImGui.EndTable();
        }

        var snapshot = state.Snapshot();
        var canApply = ItemGroupCatalog.CanApply(snapshot, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Items.Count == 0
            ? "Add at least one item to save this group."
            : canApply ? "Unsaved changes - groups remember item and quality; plans own quantities and routing." : "No unsaved changes.");
        var saveButtonWidth = ImGui.CalcTextSize("Save group").X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine(Math.Max(
            ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X,
            ImGui.GetWindowContentRegionMax().X - saveButtonWidth));
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save group##itemgroupeditor"))
        {
            try
            {
                var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, draft).Id);
                if (itemGroupEditorOrigin == WorkbenchView.Restock)
                    selectedRestockItemGroupId = groupId;
                else
                    selectedItemGroupId = groupId;
                itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
            }
            catch (InvalidOperationException exception)
            {
                itemGroupEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.save",
            "Save the open Item Group draft",
            canApply,
            () =>
            {
                if (itemGroupDraft is null)
                    return;
                var groupId = state.Mutate(document => ItemGroupCatalog.Apply(document, itemGroupDraft).Id);
                if (itemGroupEditorOrigin == WorkbenchView.Restock)
                    selectedRestockItemGroupId = groupId;
                else
                    selectedItemGroupId = groupId;
                itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
            },
            canApply ? "Changes are saved together" : "No valid changes");

        DrawDeleteItemGroupPopup();
    }

    private void DrawItemGroupList(ItemGroupDraft draft)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##itemgroupfilter", "Filter groups", ref itemGroupFilter, 80);
        ImGui.Separator();
        var groups = ItemGroupCatalog.All(state.Snapshot())
            .Where(group => itemGroupFilter.Trim().Length == 0 ||
                            group.Name.Contains(itemGroupFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            group.Items.Any(item => item.ItemName.Contains(itemGroupFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var group in groups)
        {
            var preview = string.Join(", ", group.Items.Select(item => item.ItemName).Take(3));
            if (group.Items.Count > 3)
                preview += "...";
            var selected = !draft.IsNew && draft.GroupId == group.Id;
            if (ImGui.Selectable(
                    $"@{group.Name}  ({group.Items.Count:N0})##itemgroup{group.Id}",
                    selected,
                    ImGuiSelectableFlags.AllowDoubleClick))
                TrySwitchItemGroup(group.Id);
            var groupId = group.Id;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.item-groups.select.{group.Id}",
                $"Edit Item Group {group.Name}",
                true,
                () => TrySwitchItemGroup(groupId),
                selected ? "Selected" : $"{group.Items.Count:N0} items");
            if (!string.IsNullOrWhiteSpace(preview))
                ImGui.TextDisabled(preview);
        }
        ImGui.Separator();
        if (ImGui.Button("New item group", new Vector2(-1, 0)))
        {
            if (ItemGroupCatalog.HasChanges(state.Snapshot(), draft))
                itemGroupEditorError = "Save or discard this Item Group before creating another.";
            else
            {
                itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot());
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
            }
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.new",
            "Open a new Item Group draft",
            true,
            () =>
            {
                if (itemGroupDraft is not null &&
                    !ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft))
                    itemGroupDraft = ItemGroupCatalog.NewDraft(state.Snapshot());
            },
            "Nothing is saved until Save group");
    }

    private void TrySwitchItemGroup(Guid groupId)
    {
        if (itemGroupDraft is null || itemGroupDraft.GroupId == groupId)
            return;
        if (ItemGroupCatalog.HasChanges(state.Snapshot(), itemGroupDraft))
        {
            itemGroupEditorError = "Save or discard this Item Group before switching.";
            return;
        }
        itemGroupDraft = ItemGroupCatalog.Draft(state.Snapshot(), groupId);
        selectedItemGroupItems.Clear();
        itemGroupItemSearch = string.Empty;
        selectedItemGroupChoice = null;
        itemGroupEditorError = string.Empty;
    }

    private void DrawItemGroupMembers(ItemGroupDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.TextDisabled("Name");
        ImGui.SameLine();
        var name = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##itemgroupname", ref name, 80))
            draft.Name = name;
        ImGui.SameLine();
        ImGui.TextDisabled($"{draft.Items.Count:N0} items");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100);
        if (draft.IsNew)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete group##itemgroup"))
            requestDeleteItemGroup = true;
        if (draft.IsNew)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Add member");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##itemgroupitemsearch", "Search by item name", ref itemGroupItemSearch, 96))
            selectedItemGroupChoice = null;
        if (itemGroupItemSearch.Trim().Length >= 2 && selectedItemGroupChoice is null)
        {
            foreach (var match in SearchItems(itemGroupItemSearch, 5))
                if (ImGui.Selectable(
                        $"{match.Label}##itemgroupchoice{match.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedItemGroupChoice = match;
                    itemGroupItemSearch = match.Name;
                }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##itemgroupaddquality", QualityChoiceLabel(itemGroupAddQuality)))
        {
            foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
                if (ImGui.Selectable(QualityChoiceLabel(quality), itemGroupAddQuality == quality))
                    itemGroupAddQuality = quality;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var canAddItem = selectedItemGroupChoice is not null;
        if (!canAddItem)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add item##itemgroup") && selectedItemGroupChoice is { } choice)
        {
            ItemGroupCatalog.AddMissing(draft,
            [
                new ItemGroupItem
                {
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    Quality = itemGroupAddQuality,
                },
            ]);
            selectedItemGroupChoice = null;
            itemGroupItemSearch = string.Empty;
        }
        if (!canAddItem)
            ImGui.EndDisabled();

        var planSelectionCount = ItemGroupPlanSelectionCount();
        ImGui.SameLine();
        if (planSelectionCount == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Add {planSelectionCount:N0} selected from plan##itemgroup"))
            AddPlanSelectionToItemGroup(draft);
        if (planSelectionCount == 0)
            ImGui.EndDisabled();

        var hasSelectedMembers = selectedItemGroupItems.Count > 0;
        if (!hasSelectedMembers)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Remove selected ({selectedItemGroupItems.Count:N0})##itemgroup"))
        {
            draft.Items.RemoveAll(selectedItemGroupItems.Contains);
            selectedItemGroupItems.Clear();
        }
        if (!hasSelectedMembers)
            ImGui.EndDisabled();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQItemGroupMembersTable", 4, flags, new Vector2(0, Math.Max(180, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn("Quality identity", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableHeadersRow();
        foreach (var item in draft.Items.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = selectedItemGroupItems.Contains(item);
            if (ImGui.Checkbox($"##selectgroupitem{item.ItemId}:{(int)item.Quality}:{item.GetHashCode()}", ref selected))
            {
                if (selected)
                    selectedItemGroupItems.Add(item);
                else
                    selectedItemGroupItems.Remove(item);
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.ItemName);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##groupitemquality{item.GetHashCode()}", QualityChoiceLabel(item.Quality)))
            {
                foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
                    if (ImGui.Selectable(QualityChoiceLabel(quality), item.Quality == quality))
                        item.Quality = quality;
                ImGui.EndCombo();
            }
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"X##removegroupitem{item.GetHashCode()}"))
            {
                draft.Items.Remove(item);
                selectedItemGroupItems.Remove(item);
            }
        }
        ImGui.EndTable();
    }

    private int ItemGroupPlanSelectionCount() =>
        itemGroupEditorOrigin switch
        {
            WorkbenchView.Restock when restockDraft is not null =>
                restockDraft.Items.Count(item => selectedRestockItemIds.Contains(item.Id)),
            WorkbenchView.Stowage when stowageDraft is not null =>
                stowageDraft.Rules.Count(rule => selectedStowageRuleIds.Contains(rule.Id)),
            _ => 0,
        };

    private void AddPlanSelectionToItemGroup(ItemGroupDraft draft)
    {
        if (itemGroupEditorOrigin == WorkbenchView.Restock && restockDraft is not null)
            ItemGroupCatalog.AddMissing(
                draft,
                restockDraft.Items.Where(item => selectedRestockItemIds.Contains(item.Id)));
        else if (itemGroupEditorOrigin == WorkbenchView.Stowage && stowageDraft is not null)
            ItemGroupCatalog.AddMissing(
                draft,
                stowageDraft.Rules.Where(rule => selectedStowageRuleIds.Contains(rule.Id)));
    }

    private void DrawDeleteItemGroupPopup()
    {
        if (requestDeleteItemGroup)
        {
            ImGui.OpenPopup("Delete Item Group##RQ");
            requestDeleteItemGroup = false;
        }
        if (!ImGui.BeginPopupModal("Delete Item Group##RQ", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var draft = itemGroupDraft;
        if (draft is null || draft.IsNew)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted($"Delete \"@{draft.Name}\"?");
        ImGui.TextDisabled("Existing plan items are not changed.");
        if (ImGui.Button("Delete##itemgroupconfirm"))
        {
            try
            {
                state.Mutate(document => ItemGroupCatalog.Delete(document, draft.GroupId, draft.SourceRevision));
                if (selectedRestockItemGroupId == draft.GroupId)
                    selectedRestockItemGroupId = null;
                if (selectedItemGroupId == draft.GroupId)
                    selectedItemGroupId = null;
                var next = ItemGroupCatalog.All(state.Snapshot()).FirstOrDefault();
                itemGroupDraft = next is null
                    ? ItemGroupCatalog.NewDraft(state.Snapshot())
                    : ItemGroupCatalog.Draft(state.Snapshot(), next.Id);
                selectedItemGroupItems.Clear();
                itemGroupEditorError = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                itemGroupEditorError = exception.Message;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##itemgroupdelete"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRestockEditorBulkBar(RestockPlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedRestockItemIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Target");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##restockbulktarget", ref bulkRestockTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##restockbulkquality", ref bulkRestockQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Note");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(125);
        if (ImGui.BeginCombo("##restockbulknote", bulkRestockNoteMode < 0 ? "Leave unchanged" : "Clear notes"))
        {
            if (ImGui.Selectable("Leave unchanged", bulkRestockNoteMode < 0))
                bulkRestockNoteMode = -1;
            if (ImGui.Selectable("Clear notes", bulkRestockNoteMode == 0))
                bulkRestockNoteMode = 0;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var canApply = selectedRestockItemIds.Count > 0 &&
                       (bulkRestockQuality >= 0 || bulkRestockTarget >= 0 || bulkRestockNoteMode >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##restockbulk"))
        {
            foreach (var item in draft.Items.Where(item => selectedRestockItemIds.Contains(item.Id)))
            {
                if (bulkRestockQuality >= 0)
                    item.Quality = (ItemQualityPolicy)bulkRestockQuality;
                if (bulkRestockTarget >= 0)
                    item.TargetQuantity = bulkRestockTarget;
                if (bulkRestockNoteMode == 0)
                    item.Notes = string.Empty;
            }
            bulkRestockQuality = bulkRestockTarget = bulkRestockNoteMode = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedRestockItemIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##restockbulk"))
        {
            draft.Items.RemoveAll(item => selectedRestockItemIds.Contains(item.Id));
            selectedRestockItemIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private void DrawRestockDraftItems(RestockPlanDraft draft)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQRestockDraftItems", 8, flags, new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Item group", ImGuiTableColumnFlags.WidthStretch, .8f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableHeadersRow();
        var filtered = FilteredRestockDraftItems(draft);
        var groups = ItemGroupCatalog.All(state.Snapshot());
        if (filtered.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(draft.Items.Count == 0
                ? "No items yet. Search by name above or add a selected Stock item."
                : "No items match this filter.");
        }
        foreach (var item in filtered.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = selectedRestockItemIds.Contains(item.Id);
            if (ImGui.Checkbox($"##selectrestock{item.Id}", ref selected))
            {
                if (selected)
                    selectedRestockItemIds.Add(item.Id);
                else
                    selectedRestockItemIds.Remove(item.Id);
            }
            ImGui.TableNextColumn();
            item.Enabled = DrawRuleToggle($"restock{item.Id}", item.Enabled);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.ItemName);
            ImGui.TableNextColumn();
            var target = item.TargetQuantity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##draftrestocktarget{item.Id}", ref target))
                item.TargetQuantity = Math.Max(0, target);
            ImGui.TableNextColumn();
            DrawRestockDraftQuality(item);
            ImGui.TableNextColumn();
            var note = item.Notes;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText($"##draftrestocknote{item.Id}", ref note, 160))
                item.Notes = note;
            ImGui.TableNextColumn();
            var matchingGroups = groups
                .Where(group => group.Items.Any(member =>
                    member.ItemId == item.ItemId && member.Quality == item.Quality))
                .Select(group => $"@{group.Name}")
                .Take(2)
                .ToArray();
            ImGui.TextDisabled(matchingGroups.Length == 0 ? "Ungrouped" : string.Join(", ", matchingGroups));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"X##draftrestockremove{item.Id}"))
            {
                draft.Items.Remove(item);
                selectedRestockItemIds.Remove(item.Id);
                if (activeRestockItemId == item.Id)
                    activeRestockItemId = draft.Items.FirstOrDefault()?.Id;
            }
        }
        ImGui.EndTable();
    }

    private static void DrawRestockDraftQuality(RestockPlanItem item)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftrestockquality{item.Id}", QualityLabel(item.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), item.Quality == quality))
                item.Quality = quality;
        ImGui.EndCombo();
    }

    private IReadOnlyList<RestockPlanItem> FilteredRestockDraftItems(RestockPlanDraft draft)
    {
        var filter = restockItemFilter.Trim();
        return draft.Items
            .Where(item => filter.Length == 0 || item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }

    private void DrawDeleteRestockPlanPopup(OwnerScope owner, QuartermasterState snapshot)
    {
        if (requestDeleteRestockPlanId is { } requestedId)
        {
            ImGui.OpenPopup($"Delete Restock Plan##{requestedId}");
            requestDeleteRestockPlanId = null;
        }
        var plan = snapshot.RestockPlans.FirstOrDefault(candidate =>
            candidate.Owner.Matches(owner) &&
            ImGui.IsPopupOpen($"Delete Restock Plan##{candidate.Id}"));
        if (plan is null || !ImGui.BeginPopupModal($"Delete Restock Plan##{plan.Id}", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.TextUnformatted($"Delete \"{plan.Name}\"?");
        if (ImGui.Button("Delete"))
        {
            var planId = plan.Id;
            workbench.SelectedRestockPlanId = state.Mutate(document =>
            {
                document.RestockPlans.RemoveAll(candidate => candidate.Id == planId && candidate.Owner.Matches(owner));
                return RestockPlanCatalog.OwnerPlans(document, owner).FirstOrDefault()?.Id;
            });
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void AddStockSelectionToRestock(
        QuartermasterRuntimeSnapshot runtime,
        StockGroup stock,
        int target,
        RestockPlan? selectedPlan)
    {
        var snapshot = state.Snapshot();
        var draft = selectedPlan is null
            ? RestockPlanCatalog.NewDraft(snapshot, runtime.Owner)
            : RestockPlanCatalog.Draft(snapshot, runtime.Owner, selectedPlan.Id);
        var item = draft.Items.FirstOrDefault(candidate =>
            candidate.ItemId == stock.ItemId && candidate.Quality == ItemQualityPolicy.Any);
        if (item is null)
        {
            item = new RestockPlanItem
            {
                ItemId = stock.ItemId,
                ItemName = stock.ItemName,
                TargetQuantity = target,
            };
            draft.Items.Add(item);
        }
        else
        {
            item.ItemName = stock.ItemName;
            item.TargetQuantity = target;
            item.Enabled = true;
        }
        workbench.SelectedRestockPlanId = selectedPlan?.Id;
        OpenRestockEditor(draft);
        activeRestockItemId = item.Id;
        selectedRestockItemIds.Add(item.Id);
        workbench.ClearSelection();
    }

    private void AddStockSelectionToStowage(
        QuartermasterRuntimeSnapshot runtime,
        StockGroup stock,
        int target,
        StowagePlan? selectedPlan)
    {
        var snapshot = state.Snapshot();
        var draft = selectedPlan is null
            ? StowagePlanCatalog.NewDraft(snapshot, runtime.Owner)
            : StowagePlanCatalog.Draft(snapshot, runtime.Owner, selectedPlan.Id);
        var rule = draft.Rules.FirstOrDefault(candidate =>
            candidate.ItemId == stock.ItemId && candidate.Quality == ItemQualityPolicy.Any);
        if (rule is null)
        {
            rule = new TargetPlanItem
            {
                StowagePlanId = draft.PlanId,
                ItemId = stock.ItemId,
                ItemName = stock.ItemName,
                TargetQuantity = target,
            };
            draft.Rules.Add(rule);
        }
        else
        {
            rule.ItemName = stock.ItemName;
            rule.TargetQuantity = target;
            rule.Enabled = true;
        }
        workbench.SelectedStowagePlanId = selectedPlan?.Id;
        OpenStowageEditor(draft);
        activeStowageRuleId = rule.Id;
        selectedStowageRuleIds.Add(rule.Id);
        workbench.ClearSelection();
    }

    private void DrawDeleteStowagePlanPopup(OwnerScope owner, QuartermasterState snapshot)
    {
        if (requestDeleteStowagePlanId is { } requestedId)
        {
            ImGui.OpenPopup($"Delete Stowage Plan##{requestedId}");
            requestDeleteStowagePlanId = null;
        }
        var plan = snapshot.StowagePlans.FirstOrDefault(candidate =>
            candidate.Owner.Matches(owner) &&
            ImGui.IsPopupOpen($"Delete Stowage Plan##{candidate.Id}"));
        if (plan is null || !ImGui.BeginPopupModal($"Delete Stowage Plan##{plan.Id}", ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.TextUnformatted($"Delete \"{plan.Name}\"?");
        if (ImGui.Button("Delete"))
        {
            var planId = plan.Id;
            workbench.SelectedStowagePlanId = state.Mutate(document =>
            {
                document.PlanItems.RemoveAll(rule => rule.StowagePlanId == planId);
                document.StowagePlans.RemoveAll(candidate => candidate.Id == planId && candidate.Owner.Matches(owner));
                return StowagePlanCatalog.OwnerPlans(document, owner).FirstOrDefault()?.Id;
            });
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void CloseRestockEditor()
    {
        if (itemGroupEditorOrigin == WorkbenchView.Restock)
            CloseItemGroupEditor();
        restockDraft = null;
        requestRestockEditorOpen = false;
        restockEditorVisible = false;
        selectedRestockItemIds.Clear();
        activeRestockItemId = null;
        selectedRestockChoice = null;
        restockEditorError = string.Empty;
    }

    private void CloseStowageEditor()
    {
        if (itemGroupEditorOrigin == WorkbenchView.Stowage)
            CloseItemGroupEditor();
        stowageDraft = null;
        requestStowageEditorOpen = false;
        stowageEditorVisible = false;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = null;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
    }

    private void ClosePlanEditors()
    {
        CloseRestockEditor();
        CloseStowageEditor();
    }

    private void DrawStowageWorkspace(QuartermasterRuntimeSnapshot runtime)
    {
        var owner = runtime.Owner;
        var plans = StowagePlanCatalog.OwnerPlans(runtime.State, owner);
        var selected = ResolveSelectedStowagePlan(runtime.State, owner);
        if (selected is null && plans.Count > 0)
        {
            selected = plans[0];
            workbench.SelectedStowagePlanId = selected.Id;
        }

        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 230));
        if (ImGui.BeginCombo("##RQStowagePlan", selected?.Name ?? "Choose a Stowage Plan"))
        {
            foreach (var plan in plans)
                if (ImGui.Selectable($"{plan.Name}##stowage{plan.Id}", selected?.Id == plan.Id))
                    workbench.SelectedStowagePlanId = plan.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selected is null)
            ImGui.BeginDisabled();
        if (ImGui.Button("Edit plan") && selected is not null)
            OpenStowageEditor(selected.Id, owner);
        if (selected is null)
            ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.stowage.edit",
            "Open the selected Stowage Plan editor",
            selected is not null,
            () =>
            {
                var current = ResolveSelectedStowagePlan(state.Snapshot(), runtimeSnapshots.Current.Owner);
                if (current is not null)
                    OpenStowageEditor(current.Id, runtimeSnapshots.Current.Owner);
            },
            selected is null ? "No plan selected" : selected.Name);
        ImGui.SameLine();
        if (ImGui.Button("Manage...##stowage"))
            ImGui.OpenPopup("RQStowagePlanManage");
        if (ImGui.BeginPopup("RQStowagePlanManage"))
        {
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("New plan"))
                OpenStowageEditor(StowagePlanCatalog.NewDraft(state.Snapshot(), owner));
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();
            if (selected is null)
                ImGui.BeginDisabled();
            if (ImGui.Selectable("Duplicate plan") && selected is not null)
                OpenStowageEditor(StowagePlanCatalog.DuplicateDraft(state.Snapshot(), owner, selected.Id));
            if (ImGui.Selectable("Delete plan") && selected is not null)
                requestDeleteStowagePlanId = selected.Id;
            if (selected is null)
                ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        DrawDeleteStowagePlanPopup(owner, runtime.State);

        selected = ResolveSelectedStowagePlan(state.Snapshot(), owner);
        if (selected is null)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("No Stowage Plans yet.");
            ImGui.TextDisabled("Create one here, then add standing rules from Stock or by name.");
            if (!owner.HasStableIdentity)
                ImGui.BeginDisabled();
            if (ImGui.Button("New Stowage Plan"))
                OpenStowageEditor(StowagePlanCatalog.NewDraft(state.Snapshot(), owner));
            reviewRegistry.RegisterLastButton(
                "quartermaster.stowage.new",
                "Open a new Stowage Plan draft",
                owner.HasStableIdentity,
                () => OpenStowageEditor(StowagePlanCatalog.NewDraft(state.Snapshot(), runtimeSnapshots.Current.Owner)),
                owner.HasStableIdentity ? "Nothing is saved until Apply" : "Owner unavailable");
            if (!owner.HasStableIdentity)
                ImGui.EndDisabled();
            return;
        }

        var ownerRules = runtime.State.PlanItems
            .Where(rule => rule.StowagePlanId == selected.Id)
            .ToArray();
        var stowage = StowageEvaluator.BuildPlan(runtime.State, runtime.Browser, owner, selected.Id);
        DrawQuickDeposit(runtime, ownerRules);
        ImGui.Separator();

        ImGui.TextUnformatted(selected.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(selected.Enabled ? "Enabled" : "Disabled");
        ImGui.SameLine();
        ImGui.TextDisabled($"{ownerRules.Length:N0} rules | stow {stowage?.DepositQuantity ?? 0:N0}");

        var surplusBatch = BuildSurplusBatch(runtime, stowage);
        var canStow = selected.Enabled &&
                      surplusBatch.PlannedQuantity > 0 &&
                      owner.HasStableIdentity &&
                      transfers.CanStart &&
                      !autoRetainer.IsRefreshing &&
                      !autoRetainer.IsQueued;
        if (!canStow)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Stow reviewed items ({surplusBatch.PlannedQuantity:N0})"))
        {
            var operation = journal.CreateDeposit(owner, surplusBatch, OperationKinds.StowageSurplus);
            StartTransfer(transfers.ExecuteDepositAsync(operation.OperationId));
        }
        if (!canStow)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(transferStatus);

        var evaluated = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQStowageOverview", 4, flags, new Vector2(0, Math.Max(220, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Carried / target", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableHeadersRow();
        foreach (var rule in ownerRules.OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            evaluated.TryGetValue(rule.Id, out var line);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.ItemName);
            ImGui.SameLine();
            ImGui.TextDisabled(QualityLabel(rule.Quality));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{line?.PlayerQuantity ?? 0:N0} / {rule.TargetQuantity:N0}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(!rule.Enabled ? "Off" : line?.Action switch
            {
                StowageAction.Deposit => $"Stow {line.DepositQuantity:N0}",
                _ => "Hold",
            });
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RouteSummary(rule.Routing, runtime.Retainers, owner));
        }
        ImGui.EndTable();
    }

    private void OpenStowageEditor(Guid planId, OwnerScope owner) =>
        OpenStowageEditor(StowagePlanCatalog.Draft(state.Snapshot(), owner, planId));

    private void OpenStowageEditor(StowagePlanDraft draft)
    {
        CloseRestockEditor();
        CloseItemGroupEditor();
        stowageDraft = draft;
        selectedStowageRuleIds.Clear();
        activeStowageRuleId = stowageDraft.Rules.FirstOrDefault()?.Id;
        selectedItemGroupId = null;
        stowageItemSearch = string.Empty;
        stowageRuleFilter = string.Empty;
        selectedStowageChoice = null;
        stowageEditorError = string.Empty;
        bulkQuality = -1;
        bulkRoutingMode = -1;
        bulkOverflow = -1;
        bulkStowageTarget = -1;
        requestStowageEditorOpen = true;
        workbench.View = WorkbenchView.Stowage;
        requestedView = WorkbenchView.Stowage;
    }

    private void DrawStowageEditorModal(QuartermasterRuntimeSnapshot runtime)
    {
        const string popup = "Edit Stowage Plan##RQStowageEditor";
        stowageEditorVisible = false;
        if (requestStowageEditorOpen)
        {
            ImGui.OpenPopup(popup);
            requestStowageEditorOpen = false;
        }
        if (stowageDraft is null)
            return;

        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(1220, available.X - 48), Math.Min(720, available.Y - 48)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.NoScrollbar))
        {
            CloseStowageEditor();
            return;
        }
        stowageEditorVisible = true;
        if (itemGroupDraft is not null && itemGroupEditorOrigin == WorkbenchView.Stowage)
        {
            DrawItemGroupEditor(runtime);
            ImGui.EndPopup();
            return;
        }

        var draft = stowageDraft;
        ImGui.TextUnformatted("Edit plan");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.55f, .8f, .95f, 1f), "Stowage ->");
        ImGui.SameLine();
        var planName = draft.Name;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##stowagedraftname", ref planName, 80))
            draft.Name = planName;
        ImGui.SameLine();
        var planEnabled = draft.Enabled;
        if (ImGui.Checkbox("Enabled##stowagedraft", ref planEnabled))
            draft.Enabled = planEnabled;
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedStowageRuleIds.Count:N0} selected of {draft.Rules.Count:N0} rules");

        DrawStowageEditorToolbar(draft);
        DrawStowageEditorBulkBar(draft);
        if (!string.IsNullOrWhiteSpace(stowageEditorError))
            ImGui.TextColored(new Vector4(1f, .45f, .4f, 1f), stowageEditorError);

        if (ImGui.BeginChild("RQStowageRuleTable", new Vector2(0, -42), true))
            DrawStowageDraftRules(draft, runtime);
        ImGui.EndChild();

        var canApply = StowagePlanCatalog.CanApply(state.Snapshot(), runtime.Owner, draft);
        ImGui.TextDisabled(draft.IsNew && draft.Rules.Count == 0
            ? "Add at least one item to save this plan."
            : canApply ? $"{draft.Rules.Count:N0} rules | changes apply together" : "No unsaved changes.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 160);
        if (ImGui.Button("Cancel##stowageeditor"))
        {
            CloseStowageEditor();
            ImGui.CloseCurrentPopup();
        }
        reviewRegistry.RegisterLastButton(
            "quartermaster.stowage.editor.cancel",
            "Discard the open Stowage Plan draft",
            true,
            CloseStowageEditor,
            "No saved plan changes");
        ImGui.SameLine();
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save plan##stowageeditor"))
        {
            try
            {
                var appliedId = state.Mutate(document =>
                    StowagePlanCatalog.Apply(document, runtime.Owner, draft).Id);
                workbench.SelectedStowagePlanId = appliedId;
                CloseStowageEditor();
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidOperationException exception)
            {
                stowageEditorError = exception.Message;
            }
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawStowageEditorToolbar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Add items");
        ImGui.SameLine();
        DrawStowageDraftAdd(draft);
        ImGui.SameLine();
        ImGui.TextDisabled("or group");
        ImGui.SameLine();
        var snapshot = state.Snapshot();
        var groups = ItemGroupCatalog.All(snapshot);
        var selectedGroup = groups.FirstOrDefault(group => group.Id == selectedItemGroupId);
        ImGui.SetNextItemWidth(185);
        if (ImGui.BeginCombo("##stowagegroup", selectedGroup is null ? "@item group" : $"@{selectedGroup.Name}"))
        {
            foreach (var group in groups)
                if (ImGui.Selectable($"@{group.Name}##group{group.Id}", selectedGroup?.Id == group.Id))
                    selectedItemGroupId = group.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (selectedGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add group") && selectedGroup is not null)
        {
            ItemGroupCatalog.AddMissing(selectedGroup, draft);
            activeStowageRuleId ??= draft.Rules.FirstOrDefault()?.Id;
        }
        if (selectedGroup is null)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Manage groups...##stowage"))
            OpenItemGroupEditor(WorkbenchView.Stowage);
        reviewRegistry.RegisterLastButton(
            "quartermaster.item-groups.open.stowage",
            "Open Item Groups from the Stowage Plan editor",
            true,
            () => OpenItemGroupEditor(WorkbenchView.Stowage),
            "Plan draft remains open");

        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("##stowagerulefilter", "Filter plan items", ref stowageRuleFilter, 80);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select visible"))
        {
            foreach (var rule in FilteredDraftRules(draft))
                selectedStowageRuleIds.Add(rule.Id);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection"))
            selectedStowageRuleIds.Clear();
        ImGui.SameLine();
        if (selectedStowageRuleIds.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save as @group"))
            OpenNewItemGroupFromStowageSelection();
        if (selectedStowageRuleIds.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawStowageDraftAdd(StowagePlanDraft draft)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##RQStowageItemName", "Add item by name", ref stowageItemSearch, 96))
            selectedStowageChoice = null;
        if (stowageItemSearch.Trim().Length >= 2 && selectedStowageChoice is null)
        {
            foreach (var choice in SearchItems(stowageItemSearch, 5))
                if (ImGui.Selectable(
                        $"{choice.Label}##stowagechoice{choice.ItemId}",
                        false,
                        ImGuiSelectableFlags.DontClosePopups))
                {
                    selectedStowageChoice = choice;
                    stowageItemSearch = choice.Name;
                }
        }
        if (selectedStowageChoice is null)
            return;
        ImGui.SameLine();
        if (ImGui.Button("Add item##stowagedraft"))
        {
            var choice = selectedStowageChoice;
            var existing = draft.Rules.FirstOrDefault(rule =>
                rule.ItemId == choice.ItemId && rule.Quality == ItemQualityPolicy.Any);
            if (existing is null)
            {
                existing = new TargetPlanItem
                {
                    StowagePlanId = draft.PlanId,
                    ItemId = choice.ItemId,
                    ItemName = choice.Name,
                    TargetQuantity = 0,
                    Enabled = false,
                };
                draft.Rules.Add(existing);
            }
            activeStowageRuleId = existing.Id;
            selectedStowageRuleIds.Clear();
            selectedStowageRuleIds.Add(existing.Id);
            selectedStowageChoice = null;
            stowageItemSearch = string.Empty;
        }
    }

    private void DrawStowageEditorBulkBar(StowagePlanDraft draft)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{selectedStowageRuleIds.Count:N0} selected");
        ImGui.SameLine();
        ImGui.TextDisabled("Keep");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##stowagebulktarget", ref bulkStowageTarget);
        ImGui.SameLine();
        ImGui.TextDisabled("Quality");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkquality", ref bulkQuality, Enum.GetValues<ItemQualityPolicy>(), QualityChoiceLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Destination");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkdestination", ref bulkRoutingMode, Enum.GetValues<StowageRoutingMode>(), RoutingModeLabel);
        ImGui.SameLine();
        ImGui.TextDisabled("Overflow");
        ImGui.SameLine();
        DrawOptionalEnumCombo("##stowagebulkoverflow", ref bulkOverflow, Enum.GetValues<StowageOverflowPolicy>(), OverflowLabel);
        ImGui.SameLine();
        var canApply = selectedStowageRuleIds.Count > 0 &&
                       (bulkStowageTarget >= 0 || bulkQuality >= 0 || bulkRoutingMode >= 0 || bulkOverflow >= 0);
        if (!canApply)
            ImGui.BeginDisabled();
        if (ImGui.Button("Apply to selected##stowagebulk"))
        {
            foreach (var rule in draft.Rules.Where(rule => selectedStowageRuleIds.Contains(rule.Id)))
            {
                if (bulkStowageTarget >= 0)
                    rule.TargetQuantity = bulkStowageTarget;
                if (bulkQuality >= 0)
                    rule.Quality = (ItemQualityPolicy)bulkQuality;
                if (bulkRoutingMode >= 0)
                    rule.Routing.Mode = (StowageRoutingMode)bulkRoutingMode;
                if (bulkOverflow >= 0)
                    rule.Routing.Overflow = (StowageOverflowPolicy)bulkOverflow;
            }
            bulkStowageTarget = bulkQuality = bulkRoutingMode = bulkOverflow = -1;
        }
        if (!canApply)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var hasSelection = selectedStowageRuleIds.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##stowagebulk"))
        {
            draft.Rules.RemoveAll(rule => selectedStowageRuleIds.Contains(rule.Id));
            selectedStowageRuleIds.Clear();
        }
        if (!hasSelection)
            ImGui.EndDisabled();
    }

    private static void DrawOptionalEnumCombo<T>(
        string label,
        ref int selected,
        IReadOnlyList<T> values,
        Func<T, string> display)
        where T : struct, Enum
    {
        ImGui.SetNextItemWidth(155);
        var preview = selected < 0 ? "No change" : display((T)Enum.ToObject(typeof(T), selected));
        if (!ImGui.BeginCombo(label, preview))
            return;
        if (ImGui.Selectable("No change", selected < 0))
            selected = -1;
        foreach (var value in values)
        {
            var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (ImGui.Selectable(display(value), selected == numeric))
                selected = numeric;
        }
        ImGui.EndCombo();
    }

    private void DrawStowageDraftRules(StowagePlanDraft draft, QuartermasterRuntimeSnapshot runtime)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("RQStowageDraftRules", 8, flags, new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Overflow", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableHeadersRow();
        var filtered = FilteredDraftRules(draft);
        if (filtered.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(draft.Rules.Count == 0
                ? "No rules yet. Search by name above or add a selected Stock item."
                : "No rules match this filter.");
        }
        foreach (var rule in filtered.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = selectedStowageRuleIds.Contains(rule.Id);
            if (ImGui.Checkbox($"##selectstowage{rule.Id}", ref selected))
            {
                if (selected)
                    selectedStowageRuleIds.Add(rule.Id);
                else
                    selectedStowageRuleIds.Remove(rule.Id);
            }
            ImGui.TableNextColumn();
            rule.Enabled = DrawRuleToggle($"stowage{rule.Id}", rule.Enabled);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.ItemName);
            ImGui.TableNextColumn();
            var target = rule.TargetQuantity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##drafttarget{rule.Id}", ref target))
                rule.TargetQuantity = Math.Max(0, target);
            ImGui.TableNextColumn();
            DrawDraftQuality(rule);
            ImGui.TableNextColumn();
            DrawStowageRouteCombo(rule, runtime);
            ImGui.TableNextColumn();
            DrawStowageOverflowCombo(rule);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"X##draftremove{rule.Id}"))
            {
                draft.Rules.Remove(rule);
                selectedStowageRuleIds.Remove(rule.Id);
                if (activeStowageRuleId == rule.Id)
                    activeStowageRuleId = draft.Rules.FirstOrDefault()?.Id;
            }
        }
        ImGui.EndTable();
    }

    private void DrawDraftQuality(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftquality{rule.Id}", QualityLabel(rule.Quality)))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
            if (ImGui.Selectable(QualityChoiceLabel(quality), rule.Quality == quality))
                rule.Quality = quality;
        ImGui.EndCombo();
    }

    private static bool DrawRuleToggle(string id, bool enabled)
    {
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            enabled ? new Vector4(.16f, .34f, .24f, 1f) : new Vector4(.13f, .15f, .17f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            enabled ? new Vector4(.2f, .43f, .3f, 1f) : new Vector4(.22f, .25f, .28f, 1f));
        if (ImGui.SmallButton($"{(enabled ? "On" : "Off")}##rule{id}"))
            enabled = !enabled;
        ImGui.PopStyleColor(2);
        return enabled;
    }

    private void DrawStowageRouteCombo(TargetPlanItem rule, QuartermasterRuntimeSnapshot runtime)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(
                $"##draftdestination{rule.Id}",
                RouteSummary(rule.Routing, runtime.Retainers, runtime.Owner)))
            return;

        ImGui.TextDisabled("Placement");
        foreach (var mode in Enum.GetValues<StowageRoutingMode>())
            if (ImGui.Selectable(RoutingModeLabel(mode), rule.Routing.Mode == mode))
                rule.Routing.Mode = mode;

        ImGui.Separator();
        ImGui.TextDisabled("Preferred retainers - in order");
        var ownerRetainers = runtime.Retainers.Values
            .Where(retainer => retainer.Owner.Matches(runtime.Owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        for (var index = 0; index < rule.Routing.PreferredRetainerIds.Count; index++)
        {
            var retainerId = rule.Routing.PreferredRetainerIds[index];
            var retainer = ownerRetainers.FirstOrDefault(candidate => candidate.RetainerId == retainerId);
            ImGui.PushID($"inline-route{rule.Id}:{retainerId}:{index}");
            ImGui.TextUnformatted(retainer?.RetainerName ?? $"Retainer {retainerId}");
            ImGui.SameLine();
            if (index > 0 && ImGui.SmallButton("Up"))
                (rule.Routing.PreferredRetainerIds[index - 1], rule.Routing.PreferredRetainerIds[index]) =
                    (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index - 1]);
            ImGui.SameLine();
            if (index + 1 < rule.Routing.PreferredRetainerIds.Count && ImGui.SmallButton("Down"))
                (rule.Routing.PreferredRetainerIds[index], rule.Routing.PreferredRetainerIds[index + 1]) =
                    (rule.Routing.PreferredRetainerIds[index + 1], rule.Routing.PreferredRetainerIds[index]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                rule.Routing.PreferredRetainerIds.RemoveAt(index);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        var available = ownerRetainers
            .Where(retainer => !rule.Routing.PreferredRetainerIds.Contains(retainer.RetainerId))
            .ToArray();
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo($"##addinlinepreferred{rule.Id}", "Add preferred retainer"))
        {
            foreach (var retainer in available)
                if (ImGui.Selectable($"{retainer.RetainerName}##addinline{retainer.RetainerId}"))
                    rule.Routing.PreferredRetainerIds.Add(retainer.RetainerId);
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Note");
        var notes = rule.Notes;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText($"##draftstowagenote{rule.Id}", ref notes, 160))
            rule.Notes = notes;
        ImGui.EndCombo();
    }

    private static void DrawStowageOverflowCombo(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##draftoverflow{rule.Id}", OverflowLabel(rule.Routing.Overflow)))
            return;
        foreach (var overflow in Enum.GetValues<StowageOverflowPolicy>())
            if (ImGui.Selectable(OverflowLabel(overflow), rule.Routing.Overflow == overflow))
                rule.Routing.Overflow = overflow;
        ImGui.EndCombo();
    }

    private IReadOnlyList<TargetPlanItem> FilteredDraftRules(StowagePlanDraft draft)
    {
        var filter = stowageRuleFilter.Trim();
        return draft.Rules
            .Where(rule => filter.Length == 0 || rule.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.ItemId)
            .ToArray();
    }

    private void DrawListings(BrowserProjection projection, long revision)
    {
        DrawBrowserToolbar(projection, listings: true);
        var result = queries.QueryListings(
            projection,
            workbench.ListingFilter,
            workbench.ScopeKey,
            workbench.ListingFilterState.IsInputActive,
            revision);
        if (!result.Filter.IsValid)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");
        if (!ImGui.BeginTable("RQListings", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;
        ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Retainer"); ImGui.TableSetupColumn("Qty"); ImGui.TableSetupColumn("Quality"); ImGui.TableSetupColumn("Unit price"); ImGui.TableSetupColumn("Total");
        ImGui.TableHeadersRow();
        foreach (var listing in SortListings(result.Listings))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.ItemName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.RetainerName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.Quantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.Quality.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.UnitPrice.IsKnown ? $"{listing.UnitPrice.Value:N0}" : "Unknown");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.TotalPrice.IsKnown ? $"{listing.TotalPrice.Value:N0}" : "Unknown");
        }
        ImGui.EndTable();
    }

    private void DrawBrowserToolbar(BrowserProjection projection, bool listings)
    {
        ImGui.SetNextItemWidth(190);
        var selectedScope = projection.Scopes.First(scope => scope.Key == workbench.ScopeKey);
        if (ImGui.BeginCombo($"##RQScope{(listings ? "Listings" : "Stock")}", selectedScope.Label))
        {
            foreach (var scope in projection.Scopes)
                if (ImGui.Selectable($"{scope.Label}##{(listings ? "listing" : "stock")}{scope.Key}", scope.Key == workbench.ScopeKey))
                    workbench.ScopeKey = scope.Key;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var sourceItems = projection.GetItems(workbench.ScopeKey);
        var sourceListings = projection.GetListings(workbench.ScopeKey);
        if (listings)
        {
            var context = BrowserQueryController.CreateListingContext(sourceListings, projection.Owner);
            if (DalamudFilterAutocompleteRenderer.Draw("RQListings", "Filter: is:hq, price<1000, retainer:name", context, workbench.ListingFilterState, Math.Max(220, ImGui.GetContentRegionAvail().X - 190)))
                workbench.ListingFilter = workbench.ListingFilterState.Expression;
        }
        else
        {
            var context = BrowserQueryController.CreateItemContext(sourceItems, projection.Owner);
            if (DalamudFilterAutocompleteRenderer.Draw("RQStock", "Filter: darksteel, ilvl>=600, job:miner", context, workbench.ItemFilterState, Math.Max(220, ImGui.GetContentRegionAvail().X - 190)))
                workbench.ItemFilter = workbench.ItemFilterState.Expression;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"?##RQFilterHelp{listings}"))
            ImGui.OpenPopup($"RQFilterHelp{listings}");
        if (ImGui.BeginPopup($"RQFilterHelp{listings}"))
        {
            ImGui.TextUnformatted("Filter reference");
            ImGui.Separator();
            ImGui.TextDisabled("item/name, ilvl, level, job, slot, rarity, category");
            ImGui.TextDisabled("unique, tradable, desynth, quantity, retainer");
            if (listings)
                ImGui.TextDisabled("is:hq, condition, price, totalPrice");
            ImGui.TextDisabled("Use AND/OR, comparisons, quotes, and ! for negation.");
            ImGui.EndPopup();
        }
        if (!listings)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Storage..."))
                ImGui.OpenPopup("RQStorageSettings");
            if (ImGui.BeginPopup("RQStorageSettings"))
            {
                var includeCrystals = configuration.IncludeCrystals;
                var includeEquipped = configuration.IncludeEquipped;
                var includeArmoury = configuration.IncludeArmoury;
                var includeSaddlebag = configuration.IncludeSaddlebag;
                var changed = false;
                changed |= ImGui.Checkbox("Crystals", ref includeCrystals);
                changed |= ImGui.Checkbox("Equipped", ref includeEquipped);
                changed |= ImGui.Checkbox("Armoury", ref includeArmoury);
                changed |= ImGui.Checkbox("Saddlebags", ref includeSaddlebag);
                if (changed)
                {
                    configuration.IncludeCrystals = includeCrystals;
                    configuration.IncludeEquipped = includeEquipped;
                    configuration.IncludeArmoury = includeArmoury;
                    configuration.IncludeSaddlebag = includeSaddlebag;
                    saveConfiguration();
                }
                ImGui.EndPopup();
            }
        }
        DrawSortControl(listings);
    }

    private void DrawSortControl(bool listings)
    {
        var options = listings ? new[] { "Item", "Retainer", "Quantity", "Price" } : new[] { "Name", "Total", "Player", "Retainers" };
        var selected = listings ? workbench.ListingSort : workbench.ItemSort;
        ImGui.SetNextItemWidth(125);
        if (ImGui.BeginCombo($"Sort##{listings}", selected))
        {
            foreach (var option in options)
                if (ImGui.Selectable(option, selected == option))
                {
                    selected = option;
                    if (listings)
                        workbench.ListingSort = selected;
                    else
                        workbench.ItemSort = selected;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var descending = listings ? workbench.ListingSortDescending : workbench.ItemSortDescending;
        if (ImGui.SmallButton($"{(descending ? "Desc" : "Asc")}##SortDirection{listings}"))
        {
            descending = !descending;
            if (listings)
                workbench.ListingSortDescending = descending;
            else
                workbench.ItemSortDescending = descending;
        }
    }

    private IReadOnlyList<StockGroup> SortItems(IReadOnlyList<StockGroup> rows)
    {
        IEnumerable<StockGroup> sorted = workbench.ItemSort switch
        {
            "Total" => rows.OrderBy(row => row.TotalQuantity),
            "Player" => rows.OrderBy(row => row.PlayerQuantity),
            "Retainers" => rows.OrderBy(row => row.RetainerQuantity),
            _ => rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase),
        };
        return (workbench.ItemSortDescending ? sorted.Reverse() : sorted).ToArray();
    }

    private IReadOnlyList<ListingRow> SortListings(IReadOnlyList<ListingRow> rows)
    {
        IEnumerable<ListingRow> sorted = workbench.ListingSort switch
        {
            "Retainer" => rows.OrderBy(row => row.RetainerName, StringComparer.OrdinalIgnoreCase),
            "Quantity" => rows.OrderBy(row => row.Quantity),
            "Price" => rows.OrderBy(row => row.UnitPrice.IsKnown ? row.UnitPrice.Value : decimal.MaxValue),
            _ => rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase),
        };
        return (workbench.ListingSortDescending ? sorted.Reverse() : sorted).ToArray();
    }

    private void StageQuickDeposit(StockGroup item, bool? highQuality = null)
    {
        foreach (var variant in item.Stacks
                     .Where(stack => stack.ScopeKind == BrowserScopeKind.Player)
                     .Where(stack => highQuality is null ||
                                     (stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ) == highQuality)
                     .GroupBy(stack => stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ))
        {
            var quantity = variant.Sum(stack => stack.Quantity);
            if (quantity <= 0)
                continue;
            var key = (item.ItemId, variant.Key);
            quickDeposits[key] = new QuickDepositSelection(
                item.ItemId,
                item.ItemName,
                variant.Key,
                quantity,
                quantity,
                null);
        }
    }

    private void DrawQuickDeposit(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<TargetPlanItem> ownerRules)
    {
        var batch = BuildQuickDepositBatch(runtime, ownerRules);
        ImGui.TextUnformatted("Quick Deposit");
        ImGui.SameLine();
        ImGui.TextDisabled(quickDeposits.Count == 0
            ? "Stage carried items from the stock table."
            : $"{batch.PlannedQuantity:N0} ready · {batch.RemainingQuantity:N0} stay with you");
        if (quickDeposits.Count == 0)
            return;

        if (ImGui.BeginTable("RQQuickDeposit", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("Destination");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableHeadersRow();
            foreach (var entry in quickDeposits.ToArray())
            {
                var selection = entry.Value;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{selection.ItemName}{(selection.IsHighQuality ? " HQ" : string.Empty)}");
                ImGui.TableNextColumn();
                var quantity = selection.Quantity;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##quickqty{selection.ItemId}:{selection.IsHighQuality}", ref quantity))
                    quickDeposits[entry.Key] = selection with { Quantity = Math.Clamp(quantity, 1, selection.AvailableQuantity) };
                ImGui.TableNextColumn();
                var after = Math.Max(0, selection.AvailableQuantity - selection.Quantity);
                ImGui.TextUnformatted(after.ToString("N0"));
                var matchingRule = ownerRules.FirstOrDefault(rule =>
                    rule.ItemId == selection.ItemId &&
                    rule.Enabled &&
                    (rule.Quality == ItemQualityPolicy.Any ||
                     rule.Quality == (selection.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly)));
                if (matchingRule is not null && after < matchingRule.TargetQuantity)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), "below target");
                }
                ImGui.TableNextColumn();
                DrawQuickDestination(entry.Key, selection, runtime.Retainers, runtime.Owner);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Remove##quick{selection.ItemId}:{selection.IsHighQuality}"))
                    quickDeposits.Remove(entry.Key);
            }
            ImGui.EndTable();
        }

        batch = BuildQuickDepositBatch(runtime, ownerRules);
        var canDeposit = runtime.Owner.HasStableIdentity &&
                         batch.PlannedQuantity > 0 &&
                         transfers.CanStart &&
                         !autoRetainer.IsRefreshing &&
                         !autoRetainer.IsQueued;
        if (!canDeposit)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Deposit selected ({batch.PlannedQuantity:N0})"))
        {
            var operation = journal.CreateDeposit(runtime.Owner, batch, OperationKinds.QuickDeposit);
            quickDeposits.Clear();
            StartTransfer(transfers.ExecuteDepositAsync(operation.OperationId));
        }
        if (!canDeposit)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            quickDeposits.Clear();
    }

    private void DrawQuickDestination(
        (uint ItemId, bool IsHighQuality) key,
        QuickDepositSelection selection,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var choices = retainers.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        var label = selection.DestinationOverride is { } id
            ? choices.FirstOrDefault(retainer => retainer.RetainerId == id)?.RetainerName ?? "Unavailable"
            : "Use plan routing";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##quickdestination{selection.ItemId}:{selection.IsHighQuality}", label))
            return;
        if (ImGui.Selectable("Use plan routing", selection.DestinationOverride is null))
            quickDeposits[key] = selection with { DestinationOverride = null };
        foreach (var retainer in choices)
            if (ImGui.Selectable(retainer.RetainerName, selection.DestinationOverride == retainer.RetainerId))
                quickDeposits[key] = selection with { DestinationOverride = retainer.RetainerId };
        ImGui.EndCombo();
    }

    private static string QualityLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ",
        ItemQualityPolicy.HqOnly => "HQ",
        _ => "Any",
    };

    private static string QualityChoiceLabel(ItemQualityPolicy quality) => quality switch
    {
        ItemQualityPolicy.NqOnly => "NQ only",
        ItemQualityPolicy.HqOnly => "HQ only",
        _ => "Any quality",
    };

    private StowageDepositBatch BuildQuickDepositBatch(
        QuartermasterRuntimeSnapshot runtime,
        IReadOnlyList<TargetPlanItem> ownerRules)
    {
        var requests = quickDeposits.Values.Select(selection =>
        {
            var matchingRule = ownerRules.FirstOrDefault(rule =>
                rule.ItemId == selection.ItemId &&
                rule.Enabled &&
                (rule.Quality == ItemQualityPolicy.Any ||
                 rule.Quality == (selection.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly)));
            return new StowageDepositRequest(
                null,
                matchingRule?.Id,
                selection.ItemId,
                selection.ItemName,
                selection.IsHighQuality,
                Math.Min(selection.Quantity, selection.AvailableQuantity),
                CopyRouting(matchingRule?.Routing),
                selection.DestinationOverride);
        });
        return StowageRouter.BuildBatch(
            requests,
            runtime.Retainers,
            runtime.Owner,
            itemId => ResolveMaxStack(runtime.Browser, itemId),
            DateTime.UtcNow);
    }

    private StowageDepositBatch BuildSurplusBatch(
        QuartermasterRuntimeSnapshot runtime,
        StowageEvaluation? evaluation)
    {
        if (evaluation is null)
            return new(DateTime.UtcNow, []);
        var requests = new List<StowageDepositRequest>();
        foreach (var line in evaluation.Lines.Where(line => line.DepositQuantity > 0))
        {
            var stock = runtime.Browser.Items.FirstOrDefault(item => item.ItemId == line.ItemId);
            var nq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player &&
                    stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var hq = stock?.Stacks.Where(stack =>
                    stack.ScopeKind == BrowserScopeKind.Player &&
                    stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ)
                .Sum(stack => stack.Quantity) ?? 0;
            var remaining = line.DepositQuantity;
            if (line.Quality != ItemQualityPolicy.HqOnly)
            {
                var quantity = Math.Min(remaining, nq);
                if (quantity > 0)
                    requests.Add(new(line.PlanId, line.RuleId, line.ItemId, line.ItemName, false, quantity, CopyRouting(line.Routing)));
                remaining -= quantity;
            }
            if (line.Quality != ItemQualityPolicy.NqOnly)
            {
                var quantity = Math.Min(remaining, hq);
                if (quantity > 0)
                    requests.Add(new(line.PlanId, line.RuleId, line.ItemId, line.ItemName, true, quantity, CopyRouting(line.Routing)));
            }
        }
        return StowageRouter.BuildBatch(
            requests,
            runtime.Retainers,
            runtime.Owner,
            itemId => ResolveMaxStack(runtime.Browser, itemId),
            DateTime.UtcNow);
    }

    private static int ResolveMaxStack(BrowserProjection browser, uint itemId) =>
        checked((int)Math.Clamp(browser.Items.FirstOrDefault(item => item.ItemId == itemId)?.Definition?.MaxStackSize ?? 999, 1, int.MaxValue));

    private static StowageRoutingPolicy CopyRouting(StowageRoutingPolicy? routing) => new()
    {
        Mode = routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
        Overflow = routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
        PreferredRetainerIds = routing?.PreferredRetainerIds.ToList() ?? [],
    };

    private void DrawOperation(OwnerScope owner)
    {
        var operation = journal.Current(owner);
        if (operation is null)
        {
            ImGui.TextDisabled("No operation has been accepted or run.");
            return;
        }
        ImGui.TextUnformatted($"{operation.Kind} | {operation.Status}");
        if (!string.IsNullOrWhiteSpace(operation.SourcePlanName))
            ImGui.TextDisabled($"From Restock Plan: {operation.SourcePlanName}");
        ImGui.TextWrapped(operation.Message);
        if (operation.Status == OperationStatuses.Accepted)
        {
            var canExecute = transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
            if (!canExecute)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Execute this operation##{operation.OperationId}"))
                StartTransfer(operation.Kind == OperationKinds.Retrieval
                    ? transfers.ExecuteRetrievalAsync(operation.OperationId)
                    : transfers.ExecuteDepositAsync(operation.OperationId));
            if (!canExecute)
                ImGui.EndDisabled();
        }
        if (ImGui.BeginTable("RQOperationLines", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Target"); ImGui.TableSetupColumn("Submitted shortage"); ImGui.TableSetupColumn("Transferred"); ImGui.TableSetupColumn("Remaining"); ImGui.TableHeadersRow();
            foreach (var line in operation.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.TargetQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ShortageQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.TransferredQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Math.Max(0, line.ShortageQuantity - line.TransferredQuantity).ToString("N0"));
            }
            ImGui.EndTable();
        }
    }

    private IReadOnlyList<ItemChoice> SearchItems(string search, int limit)
    {
        var needle = search.Trim();
        var matches = dataManager.GetExcelSheet<Item>()
            .Select(row => new { row.RowId, Name = row.Name.ExtractText() })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RowId)
            .Take(limit * 2)
            .ToArray();
        return matches
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select((item, index) => new ItemChoice(
                item.RowId,
                item.Name,
                group.Count() > 1 ? $"{item.Name} ({index + 1})" : item.Name)))
            .Take(limit)
            .ToArray();
    }

    private RestockPlan? ResolveSelectedRestockPlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedRestockPlanId is { } selectedId
            ? document.RestockPlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    private StowagePlan? ResolveSelectedStowagePlan(QuartermasterState document, OwnerScope owner) =>
        workbench.SelectedStowagePlanId is { } selectedId
            ? document.StowagePlans.FirstOrDefault(plan => plan.Id == selectedId && plan.Owner.Matches(owner))
            : null;

    private static RetrievalPlan BuildRestockEvaluation(
        QuartermasterRuntimeSnapshot runtime,
        RestockPlan plan)
    {
        var playerCounts = runtime.PlayerStorage.Bags
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        return RestockPlanner.Build(
            RestockPlanCatalog.ToExecutionRows(plan),
            playerCounts,
            runtime.Retainers,
            runtime.Owner,
            DateTime.UtcNow,
            runtime.Browser);
    }

    private static string RouteSummary(
        StowageRoutingPolicy routing,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var names = routing.PreferredRetainerIds
            .Select(id => retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(owner)
                ? retainer.RetainerName
                : $"Retainer {id}")
            .ToArray();
        if (names.Length == 0)
            return routing.Mode == StowageRoutingMode.ConsolidateFirst ? "Consolidate anywhere" : "Preferred first";
        var preferred = string.Join(" -> ", names);
        return routing.Overflow == StowageOverflowPolicy.AnyOwnerRetainer
            ? $"{preferred} -> any"
            : preferred;
    }

    private static string RoutingModeLabel(StowageRoutingMode mode) => mode switch
    {
        StowageRoutingMode.HomeFirst => "Preferred retainers first",
        _ => "Consolidate first",
    };

    private static string OverflowLabel(StowageOverflowPolicy overflow) => overflow switch
    {
        StowageOverflowPolicy.HoldOnPlayer => "Keep on player",
        _ => "Any retainer",
    };

    private void StartTransfer(Task<TransferExecutionResult> transfer) => _ = ObserveTransferAsync(transfer);

    private async Task ObserveTransferAsync(Task<TransferExecutionResult> transfer)
    {
        activeTransferTask = transfer;
        try
        {
            var result = await transfer;
            transferStatus = result.Message;
        }
        catch (Exception exception)
        {
            transferStatus = $"Transfer failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(activeTransferTask, transfer))
                activeTransferTask = null;
        }
    }

    public void CancelActiveTransfer()
    {
        if (activeTransferTask is not null || transfers.IsRunning)
        {
            try { transfers.CancelActive(); }
            catch { }
        }
    }

    public bool CancelAndWaitForActiveTransfer(TimeSpan timeout)
    {
        var task = Volatile.Read(ref activeTransferTask);
        if (task is null && !transfers.IsRunning)
            return true;
        try { transfers.CancelActive(); }
        catch { }
        if (task is null)
            return !transfers.IsRunning;
        try { return task.Wait(timeout); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException)) { return true; }
    }

    private sealed record ItemChoice(uint ItemId, string Name, string Label);
    private sealed record QuickDepositSelection(
        uint ItemId,
        string ItemName,
        bool IsHighQuality,
        int AvailableQuantity,
        int Quantity,
        ulong? DestinationOverride);
}
