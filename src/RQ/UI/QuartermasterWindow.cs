using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.UI.Filtering;
using Lumina.Excel.Sheets;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.UI;

public sealed class QuartermasterWindow : Window
{
    private readonly StateRepository state;
    private readonly RetainerCacheRepository cache;
    private readonly InventoryScanner scanner;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly AutoRetainerRefreshService autoRetainer;
    private readonly IDataManager dataManager;
    private readonly Func<OwnerScope> currentOwner;
    private readonly PluginConfiguration configuration;
    private readonly System.Action saveConfiguration;
    private readonly WorkbenchState workbench = new();
    private readonly BrowserQueryController queries = new();
    private string itemSearch = string.Empty;
    private int newTarget = 1;
    private ItemChoice? selectedChoice;
    private string transferStatus = "No transfer has run.";
    private WorkbenchView? requestedView;
    private Task? activeTransferTask;

    public QuartermasterWindow(
        StateRepository state,
        RetainerCacheRepository cache,
        InventoryScanner scanner,
        OperationJournal journal,
        TransferCoordinator transfers,
        AutoRetainerRefreshService autoRetainer,
        IDataManager dataManager,
        Func<OwnerScope> currentOwner,
        PluginConfiguration configuration,
        System.Action saveConfiguration)
        : base("Quartermaster###RQMain", ImGuiWindowFlags.NoScrollbar)
    {
        this.state = state;
        this.cache = cache;
        this.scanner = scanner;
        this.journal = journal;
        this.transfers = transfers;
        this.autoRetainer = autoRetainer;
        this.dataManager = dataManager;
        this.currentOwner = currentOwner;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(1280, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public override void Draw()
    {
        var owner = currentOwner();
        var playerBags = scanner.ScanPlayerBags();
        var cacheSnapshot = cache.Snapshot();
        var projection = BrowserProjectionBuilder.Build(playerBags, cacheSnapshot, owner, scanner.ResolveItemMetadata);
        var stateSnapshot = state.Snapshot();
        var counts = playerBags.SelectMany(bag => bag.Items).GroupBy(item => item.ItemId).ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var plan = RestockPlanner.Build(stateSnapshot.PlanItems, counts, cacheSnapshot, owner, DateTime.UtcNow);
        workbench.EnsureScope(projection);
        var scopedRetainerCount = cacheSnapshot.Values.Count(retainer => owner.Matches(retainer.Owner));

        ImGui.TextUnformatted(owner.HasStableIdentity ? $"{owner.CharacterName} @ {owner.HomeWorldName}" : "Owner scope unavailable");
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
        ImGui.SameLine();
        ImGui.TextDisabled(autoRetainer.Status);

        if (ImGui.BeginTabBar("RQViews"))
        {
            if (ImGui.BeginTabItem("Stock & plan", requestedView == WorkbenchView.StockAndPlan ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.StockAndPlan;
                DrawWorkbench(projection, plan, stateSnapshot, owner, cacheSnapshot);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Listings ({projection.Listings.Count:N0})", requestedView == WorkbenchView.Listings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.Listings;
                DrawListings(projection);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Operation", requestedView == WorkbenchView.Operation ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                workbench.View = WorkbenchView.Operation;
                DrawOperation(owner);
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
    }

    private void DrawWorkbench(BrowserProjection projection, RetrievalPlan plan, QuartermasterState snapshot, OwnerScope owner, IReadOnlyDictionary<ulong, CachedRetainer> cacheSnapshot)
    {
        var available = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Clamp(available.X * 0.45f, 340f, available.X - 360f);
        if (ImGui.BeginChild("RQStock", new Vector2(leftWidth, 0), true))
            DrawStock(projection);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("RQPlan", Vector2.Zero, true))
            DrawPlan(plan, snapshot, owner, cacheSnapshot);
        ImGui.EndChild();
    }

    private void DrawStock(BrowserProjection projection)
    {
        DrawBrowserToolbar(projection, listings: false);
        var result = queries.QueryItems(projection, workbench.ItemFilter, workbench.ScopeKey);
        if (!result.Filter.IsValid)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");

        DrawNameFirstAdd();
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
                state.Mutate(document => WithdrawalPlanStager.TryUpsert(document.PlanItems, selected, target));
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
        ImGui.TableSetupColumn("Observed", ImGuiTableColumnFlags.WidthFixed, 72);
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
            ImGui.TableNextColumn(); DrawAge(item.Stacks.Where(stack => stack.ObservedAtUtc is not null).Select(stack => stack.ObservedAtUtc!.Value).DefaultIfEmpty().Min());
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
                    ImGui.TableNextColumn(); DrawAge(stack.ObservedAtUtc ?? default);
                }
            }
        }
        ImGui.EndTable();
    }

    private void DrawNameFirstAdd()
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
                    var existing = document.PlanItems.FirstOrDefault(item => item.ItemId == choice.ItemId);
                    if (existing is null)
                        document.PlanItems.Add(new TargetPlanItem { ItemId = choice.ItemId, ItemName = choice.Name, TargetQuantity = newTarget });
                    else
                    {
                        existing.ItemName = choice.Name;
                        existing.TargetQuantity = newTarget;
                        existing.Enabled = true;
                    }
                });
                selectedChoice = null;
                itemSearch = string.Empty;
                newTarget = 1;
            }
        }
    }

    private void DrawPlan(RetrievalPlan plan, QuartermasterState snapshot, OwnerScope owner, IReadOnlyDictionary<ulong, CachedRetainer> cacheSnapshot)
    {
        ImGui.TextUnformatted("Retrieval plan");
        ImGui.SameLine();
        ImGui.TextDisabled($"need {plan.NeededQuantity:N0} | covered {plan.CoveredQuantity:N0} | missing {plan.MissingQuantity:N0}");
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
            var operation = journal.CreateManual(owner, snapshot.PlanItems, OperationKinds.Retrieval);
            StartTransfer(transfers.ExecuteRetrievalAsync(operation.OperationId));
        }
        if (!canExecute)
            ImGui.EndDisabled();
        ImGui.TextDisabled(transferStatus);
        var deposit = ElementalDepositPlanner.Build(scanner.CountPlayerCrystals(), cacheSnapshot, owner, scanner.ResolveItemName, DateTime.UtcNow);
        DrawDepositReview(deposit, owner);

        var lines = plan.Lines.ToDictionary(line => line.PlanItemId);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RQPlanTable", 7, flags, new Vector2(0, Math.Max(220, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Have / target", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableSetupColumn("Retrieval", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableHeadersRow();
            foreach (var item in snapshot.PlanItems)
            {
                lines.TryGetValue(item.Id, out var line);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = item.Enabled;
                if (ImGui.Checkbox($"##on{item.Id}", ref enabled))
                    UpdatePlan(item.Id, target => target.Enabled = enabled);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                var notes = item.Notes;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint($"##notes{item.Id}", "Notes", ref notes, 240))
                    UpdatePlan(item.Id, target => target.Notes = notes);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line?.PlayerQuantity ?? 0:N0} /"); ImGui.SameLine();
                var targetQuantity = item.TargetQuantity;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##target{item.Id}", ref targetQuantity))
                    UpdatePlan(item.Id, target => target.TargetQuantity = Math.Max(1, targetQuantity));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line is null ? "Disabled" : $"{line.NeededQuantity:N0} need / {line.MissingQuantity:N0} missing");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line is null ? "-" : string.Join(", ", line.Candidates.Select(candidate => candidate.RetainerName).Take(2)));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Remove##{item.Id}"))
                    state.Mutate(document => document.PlanItems.RemoveAll(target => target.Id == item.Id));
            }
            ImGui.EndTable();
        }
    }

    private void DrawListings(BrowserProjection projection)
    {
        DrawBrowserToolbar(projection, listings: true);
        var result = queries.QueryListings(projection, workbench.ListingFilter, workbench.ScopeKey);
        if (!result.Filter.IsValid)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), result.Filter.Diagnostics.FirstOrDefault()?.Message ?? "Invalid filter");
        if (!ImGui.BeginTable("RQListings", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;
        ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Retainer"); ImGui.TableSetupColumn("Qty"); ImGui.TableSetupColumn("Quality"); ImGui.TableSetupColumn("Unit price"); ImGui.TableSetupColumn("Total"); ImGui.TableSetupColumn("Observed");
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
            ImGui.TableNextColumn(); DrawAge(listing.ObservedAtUtc ?? default);
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
        var options = listings ? new[] { "Item", "Retainer", "Quantity", "Price", "Observed" } : new[] { "Name", "Total", "Player", "Retainers", "Observed" };
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
            "Observed" => rows.OrderBy(row => row.Stacks.Where(stack => stack.ObservedAtUtc is not null).Select(stack => stack.ObservedAtUtc).DefaultIfEmpty().Min()),
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
            "Observed" => rows.OrderBy(row => row.ObservedAtUtc),
            _ => rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase),
        };
        return (workbench.ListingSortDescending ? sorted.Reverse() : sorted).ToArray();
    }

    private void DrawDepositReview(ElementalDepositPlan deposit, OwnerScope owner)
    {
        if (!ImGui.CollapsingHeader(
                $"Crystal deposit review | {deposit.PlayerQuantity:N0} carried | {deposit.PlannedQuantity:N0} planned | {deposit.Lines.Sum(line => line.RemainingQuantity):N0} remain",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if (deposit.UnknownCrystalCacheCount > 0)
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), $"{deposit.UnknownCrystalCacheCount:N0} retainers have unknown crystal capacity and are excluded until refreshed.");
        if (ImGui.BeginTable("RQDepositReview", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Carried"); ImGui.TableSetupColumn("Capacity"); ImGui.TableSetupColumn("Planned"); ImGui.TableSetupColumn("Remain"); ImGui.TableHeadersRow();
            foreach (var line in deposit.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.PlayerQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.Capacity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.PlannedQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.RemainingQuantity.ToString("N0"));
            }
            ImGui.EndTable();
        }
        var canDeposit = owner.HasStableIdentity && deposit.CanRun && transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
        if (!canDeposit)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Deposit reviewed crystals ({deposit.PlannedQuantity:N0})"))
        {
            var operation = journal.CreateDeposit(owner, deposit);
            StartTransfer(transfers.ExecuteDepositAsync(operation.OperationId));
        }
        if (!canDeposit)
            ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"{deposit.Candidates.Count:N0} candidate retainers");
    }

    private void DrawOperation(OwnerScope owner)
    {
        var operation = journal.Current(owner);
        if (operation is null)
        {
            ImGui.TextDisabled("No operation has been accepted or run.");
            return;
        }
        ImGui.TextUnformatted($"{operation.Kind} | {operation.Status}");
        ImGui.SameLine(); ImGui.TextDisabled($"revision {operation.Revision}");
        ImGui.TextWrapped(operation.Message);
        if (operation.Kind == OperationKinds.Retrieval && operation.Status == OperationStatuses.Accepted)
        {
            var canExecute = transfers.CanStart && !autoRetainer.IsRefreshing && !autoRetainer.IsQueued;
            if (!canExecute)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Execute this operation##{operation.OperationId}"))
                StartTransfer(transfers.ExecuteRetrievalAsync(operation.OperationId));
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

    private void UpdatePlan(Guid id, Action<TargetPlanItem> update) => state.Mutate(document => update(document.PlanItems.Single(item => item.Id == id)));

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
}
