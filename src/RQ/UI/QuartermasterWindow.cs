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
    private string itemSearch = string.Empty;
    private int newTarget = 1;
    private ItemChoice? selectedChoice;
    private string transferStatus = "No transfer has run.";
    private WorkbenchView? requestedView;
    private Task? activeTransferTask;
    private bool clearAgentReviewWindowOverride;

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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

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

        if (ImGui.BeginTabBar("RQViews"))
        {
            if (ImGui.BeginTabItem("Stock & plan", requestedView == WorkbenchView.StockAndPlan ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.StockAndPlan;
                DrawWorkbench(runtime);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Listings ({runtime.Browser.Listings.Count:N0})", requestedView == WorkbenchView.Listings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.Listings;
                DrawListings(runtime.Browser, runtime.Revision);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Operation", requestedView == WorkbenchView.Operation ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.Operation;
                DrawOperation(runtime.Owner);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            requestedView = null;
        }
    }

    public void OpenReviewSurface(string target)
    {
        requestedView = target.Trim().ToLowerInvariant() switch
        {
            "listings" => WorkbenchView.Listings,
            "operation" => WorkbenchView.Operation,
            _ => WorkbenchView.StockAndPlan,
        };
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        clearAgentReviewWindowOverride = true;
    }

    public void CloseReviewSurface()
    {
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    public override void OnClose()
    {
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

    private void DrawWorkbench(QuartermasterRuntimeSnapshot runtime)
    {
        var available = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Clamp(available.X * 0.45f, 340f, available.X - 360f);
        if (ImGui.BeginChild("RQStock", new Vector2(leftWidth, 0), true))
            DrawStock(runtime);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("RQPlan", Vector2.Zero, true))
            DrawPlan(runtime);
        ImGui.EndChild();
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

        DrawNameFirstAdd(runtime.Owner);
        if (workbench.SelectedStock is { } selected)
        {
            ImGui.TextUnformatted(selected.ItemName);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var stagedTarget = workbench.StagedTargetText;
            if (ImGui.InputText("Target##RQStockTarget", ref stagedTarget, 12, ImGuiInputTextFlags.CharsDecimal))
                workbench.StagedTargetText = stagedTarget;
            ImGui.SameLine();
            if (ImGui.Button("Add / update") && workbench.StagedTarget is { } target && target > 0)
            {
                state.Mutate(document =>
                {
                    var plan = StowagePlanMigration.OwnerPlan(document, runtime.Owner)
                        ?? throw new InvalidOperationException("Owner Stowage Plan is unavailable.");
                    var existing = document.PlanItems.FirstOrDefault(rule =>
                        rule.StowagePlanId == plan.Id && rule.ItemId == selected.ItemId);
                    if (existing is null)
                    {
                        document.PlanItems.Add(new TargetPlanItem
                        {
                            StowagePlanId = plan.Id,
                            ItemId = selected.ItemId,
                            ItemName = selected.ItemName,
                            TargetQuantity = target,
                        });
                    }
                    else
                    {
                        existing.ItemName = selected.ItemName;
                        existing.TargetQuantity = target;
                        existing.Enabled = true;
                    }
                    plan.Revision = checked(plan.Revision + 1);
                });
                workbench.ClearSelection();
            }
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
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.TotalQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.PlayerQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.RetainerQuantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Join(", ", item.Stacks.Select(stack => stack.OwnerName).Distinct().Take(2)));
            ImGui.TableNextColumn();
            var playerStacks = item.Stacks.Where(stack => stack.ScopeKind == BrowserScopeKind.Player).ToArray();
            if (playerStacks.Length > 0 && ImGui.SmallButton($"Stage##quick{item.ItemId}"))
                StageQuickDeposit(item);
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
                    if (stack.ScopeKind == BrowserScopeKind.Player && ImGui.SmallButton($"Stage##quick{item.ItemId}:{stack.Storage}:{stack.SlotIndex}"))
                        StageQuickDeposit(item, stack.Quality == Franthropy.FFXIV.Filtering.FfxivItemQuality.HQ);
                }
            }
        }
        ImGui.EndTable();
    }

    private void DrawNameFirstAdd(OwnerScope owner)
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##RQItemName", "Add item by name", ref itemSearch, 96))
            selectedChoice = null;
        if (itemSearch.Trim().Length >= 2 && selectedChoice is null)
        {
            foreach (var choice in SearchItems(itemSearch, 6))
                if (ImGui.Selectable($"{choice.Label}##choice{choice.ItemId}"))
                {
                    selectedChoice = choice;
                    itemSearch = choice.Name;
                }
        }
        if (selectedChoice is not null)
        {
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("Target##RQNewTarget", ref newTarget);
            ImGui.SameLine();
            if (ImGui.Button("Add item") && newTarget > 0)
            {
                var choice = selectedChoice;
                state.Mutate(document =>
                {
                    var plan = StowagePlanMigration.OwnerPlan(document, owner)
                        ?? throw new InvalidOperationException("Owner Stowage Plan is unavailable.");
                    var existing = document.PlanItems.FirstOrDefault(item =>
                        item.StowagePlanId == plan.Id && item.ItemId == choice.ItemId);
                    if (existing is null)
                        document.PlanItems.Add(new TargetPlanItem
                        {
                            StowagePlanId = plan.Id,
                            ItemId = choice.ItemId,
                            ItemName = choice.Name,
                            TargetQuantity = newTarget,
                        });
                    else
                    {
                        existing.ItemName = choice.Name;
                        existing.TargetQuantity = newTarget;
                        existing.Enabled = true;
                    }
                    plan.Revision = checked(plan.Revision + 1);
                });
                selectedChoice = null;
                itemSearch = string.Empty;
                newTarget = 1;
            }
        }
    }

    private void DrawPlan(QuartermasterRuntimeSnapshot runtime)
    {
        var snapshot = runtime.State;
        var owner = runtime.Owner;
        var plan = runtime.Retrieval;
        var stowage = runtime.Stowage.FirstOrDefault();
        var ownerRules = StowagePlanMigration.OwnerRules(snapshot, owner, enabledPlansOnly: false);
        DrawQuickDeposit(runtime, ownerRules);
        ImGui.Separator();
        ImGui.TextUnformatted($"Stowage plan · {stowage?.PlanName ?? "General"}");
        ImGui.SameLine();
        ImGui.TextDisabled($"retrieve {stowage?.RetrieveQuantity ?? 0:N0} | stow {stowage?.DepositQuantity ?? 0:N0}");
        var clearAsActioned = configuration.ClearRetrievalPlansAsActioned;
        if (ImGui.Checkbox("Clear satisfied lines as actioned", ref clearAsActioned))
        {
            configuration.ClearRetrievalPlansAsActioned = clearAsActioned;
            saveConfiguration();
        }
        var canExecute = plan.NeededQuantity > 0 && owner.HasStableIdentity && transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
        if (!canExecute)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Execute reviewed plan ({plan.NeededQuantity:N0})"))
        {
            var operation = journal.CreateManual(owner, ownerRules, OperationKinds.Retrieval);
            StartTransfer(transfers.ExecuteRetrievalAsync(operation.OperationId));
        }
        if (!canExecute)
            ImGui.EndDisabled();
        ImGui.SameLine();
        var surplusBatch = BuildSurplusBatch(runtime, stowage);
        var canStow = surplusBatch.PlannedQuantity > 0 && owner.HasStableIdentity && transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
        if (!canStow)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Stow surplus ({surplusBatch.PlannedQuantity:N0})"))
        {
            var operation = journal.CreateDeposit(owner, surplusBatch, OperationKinds.StowageSurplus);
            StartTransfer(transfers.ExecuteDepositAsync(operation.OperationId));
        }
        if (!canStow)
            ImGui.EndDisabled();
        ImGui.TextDisabled(transferStatus);

        var lines = plan.Lines.ToDictionary(line => line.PlanItemId);
        var stowageLines = stowage?.Lines.ToDictionary(line => line.RuleId) ?? [];
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQPlanTable", 7, flags, new Vector2(0, Math.Max(220, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("Have / target", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableHeadersRow();
            foreach (var item in ownerRules)
            {
                lines.TryGetValue(item.Id, out var line);
                stowageLines.TryGetValue(item.Id, out var stowageLine);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = item.Enabled;
                if (ImGui.Checkbox($"##on{item.Id}", ref enabled))
                    UpdatePlan(item.Id, target => target.Enabled = enabled);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.SameLine();
                DrawRuleQuality(item);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{stowageLine?.PlayerQuantity ?? 0:N0} /"); ImGui.SameLine();
                var targetQuantity = item.TargetQuantity;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##target{item.Id}", ref targetQuantity))
                    UpdatePlan(item.Id, target => target.TargetQuantity = Math.Max(1, targetQuantity));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stowageLine?.Action switch
                {
                    StowageAction.Retrieve => $"Retrieve {stowageLine.RetrieveQuantity:N0}",
                    StowageAction.Deposit => $"Stow {stowageLine.DepositQuantity:N0}",
                    _ => "Hold",
                });
                ImGui.TableNextColumn();
                DrawRuleDestination(item, runtime.Retainers, owner);
                ImGui.TableNextColumn();
                var notes = item.Notes;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint($"##notes{item.Id}", "Notes", ref notes, 240))
                    UpdatePlan(item.Id, target => target.Notes = notes);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Remove##{item.Id}"))
                    state.Mutate(document =>
                    {
                        document.PlanItems.RemoveAll(target => target.Id == item.Id);
                        var changedPlan = document.StowagePlans.FirstOrDefault(candidate => candidate.Id == item.StowagePlanId);
                        if (changedPlan is not null)
                            changedPlan.Revision = checked(changedPlan.Revision + 1);
                    });
            }
            ImGui.EndTable();
        }
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

    private void DrawRuleDestination(
        TargetPlanItem rule,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        OwnerScope owner)
    {
        var choices = retainers.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(retainer => retainer.RetainerId)
            .ToArray();
        var preferred = rule.Routing?.PreferredRetainerIds.FirstOrDefault() ?? 0;
        var label = preferred == 0
            ? "Consolidate anywhere"
            : $"{choices.FirstOrDefault(retainer => retainer.RetainerId == preferred)?.RetainerName ?? "Preferred unavailable"}{(rule.Routing?.Overflow == StowageOverflowPolicy.HoldOnPlayer ? " only" : " + overflow")}";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##ruledestination{rule.Id}", label))
            return;
        if (ImGui.Selectable("Consolidate anywhere", preferred == 0))
            UpdatePlan(rule.Id, target =>
            {
                target.Routing = new StowageRoutingPolicy();
            });
        foreach (var retainer in choices)
        {
            if (ImGui.Selectable($"{retainer.RetainerName} + overflow", preferred == retainer.RetainerId && rule.Routing?.Overflow == StowageOverflowPolicy.AnyOwnerRetainer))
                UpdatePlan(rule.Id, target =>
                {
                    target.Routing ??= new StowageRoutingPolicy();
                    target.Routing.Mode = StowageRoutingMode.HomeFirst;
                    target.Routing.Overflow = StowageOverflowPolicy.AnyOwnerRetainer;
                    target.Routing.PreferredRetainerIds = [retainer.RetainerId];
                });
            if (ImGui.Selectable($"{retainer.RetainerName} only", preferred == retainer.RetainerId && rule.Routing?.Overflow == StowageOverflowPolicy.HoldOnPlayer))
                UpdatePlan(rule.Id, target =>
                {
                    target.Routing ??= new StowageRoutingPolicy();
                    target.Routing.Mode = StowageRoutingMode.HomeFirst;
                    target.Routing.Overflow = StowageOverflowPolicy.HoldOnPlayer;
                    target.Routing.PreferredRetainerIds = [retainer.RetainerId];
                });
        }
        ImGui.EndCombo();
    }

    private void DrawRuleQuality(TargetPlanItem rule)
    {
        ImGui.SetNextItemWidth(62);
        if (!ImGui.BeginCombo($"##rulequality{rule.Id}", rule.Quality switch
            {
                ItemQualityPolicy.NqOnly => "NQ",
                ItemQualityPolicy.HqOnly => "HQ",
                _ => "Any",
            }))
            return;
        foreach (var quality in Enum.GetValues<ItemQualityPolicy>())
        {
            var label = quality switch
            {
                ItemQualityPolicy.NqOnly => "NQ only",
                ItemQualityPolicy.HqOnly => "HQ only",
                _ => "Any quality",
            };
            if (ImGui.Selectable(label, rule.Quality == quality))
                UpdatePlan(rule.Id, target => target.Quality = quality);
        }
        ImGui.EndCombo();
    }

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

    private void UpdatePlan(Guid id, Action<TargetPlanItem> update) => state.Mutate(document =>
    {
        var rule = document.PlanItems.Single(item => item.Id == id);
        update(rule);
        var plan = document.StowagePlans.FirstOrDefault(candidate => candidate.Id == rule.StowagePlanId);
        if (plan is not null)
            plan.Revision = checked(plan.Revision + 1);
    });

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

    private static void DrawAge(DateTime observed)
    {
        if (observed == default)
        {
            ImGui.TextDisabled("Unknown");
            return;
        }
        var age = DateTime.UtcNow - observed.ToUniversalTime();
        ImGui.TextDisabled(age.TotalHours >= 1 ? $"{age.TotalHours:F1}h" : $"{Math.Max(0, age.TotalMinutes):F0}m");
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
