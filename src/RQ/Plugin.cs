using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Travel;
using Franthropy.Observations.V1;
using RQ.AgentBridge;
using RQ.Automation;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Observations;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;
using RQ.UI;

namespace RQ;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PlayerInventoryFlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlayerInventoryReconciliationInterval = TimeSpan.FromSeconds(1);

    private const string Command = "/rq";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly PluginConfiguration configuration;
    private readonly string providerInstanceId = Guid.NewGuid().ToString("N");
    private readonly FrameworkWorkQueue workQueue;
    private readonly InventoryScanner scanner;
    private readonly PlayerInventoryCacheRepository playerInventory;
    private readonly PlayerInventoryReconciler playerInventoryReconciler;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly ListingNavigationCoordinator listingNavigation;
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly DalamudExternalUiAutomationSuppression externalUiSuppression;
    private readonly AutomaticRetrievalQueue automaticRetrievals;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly SnapshotPublisher snapshots;
    private readonly ShortageSubmissionService submissions;
    private readonly ElementalDepositSubmissionService deposits;
    private readonly QuartermasterIpcProvider ipc;
    private readonly RQ.AgentBridge.AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;
    private readonly AgentBridgeUiReviewRegistry agentReviewRegistry = new();
    private readonly DalamudSharedObservationHost observationHost;
    private readonly QuartermasterObservationConsumer observationConsumer;
    private readonly WindowSystem windows = new("RQ");
    private readonly QuartermasterWindow window;
    private readonly RuntimeReconciliationQueue reconciliation = new();
    private DateTime nextSnapshotAt;
    private DateTime nextPlayerInventoryFlushAt;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IPlayerState playerState,
        IAetheryteList aetheryteList,
        IAddonLifecycle addonLifecycle,
        IGameInventory gameInventory,
        IGameGui gameGui,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        ITextureReadbackProvider textureReadbackProvider,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.playerState = playerState;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (string.IsNullOrWhiteSpace(configuration.PluginInstanceId))
            configuration.PluginInstanceId = Guid.NewGuid().ToString("N");

        var configDirectory = pluginInterface.GetPluginConfigDirectory();
        var pluginConfigs = Directory.GetParent(configDirectory)?.FullName ?? throw new InvalidOperationException("Plugin configuration root is unavailable.");
        var legacyConfigurationPath = Path.Combine(pluginConfigs, "MarketMafioso.json");
        ImportLegacyStorageSettings(configuration, legacyConfigurationPath);
        pluginInterface.SavePluginConfig(configuration);
        var cachePath = Path.Combine(configDirectory, "retainer-cache.json");
        var playerInventoryCachePath = Path.Combine(configDirectory, "player-inventory-cache.json");
        var statePath = Path.Combine(configDirectory, "quartermaster-state.json");
        new LegacyMigrationService(new LegacyMigrationPaths(
            Path.Combine(pluginConfigs, "MarketMafioso", "retainer-cache.json"),
            legacyConfigurationPath,
            cachePath,
            statePath,
            Path.Combine(configDirectory, "migration-receipt.json"))).Run();

        scanner = new(dataManager, log, () => new PlayerStorageOptions(
            configuration.IncludeArmoury,
            configuration.IncludeCrystals,
            configuration.IncludeEquipped,
            configuration.IncludeSaddlebag));
        playerInventory = new(new PlayerInventoryCacheStore(playerInventoryCachePath));
        playerInventoryReconciler = new(
            playerInventory,
            CurrentOwner,
            scanner.CapturePlayerStorage,
            PlayerInventoryReconciliationInterval);
        cache = new(new RetainerCacheStore(cachePath));
        cache.ListingPersistenceFailed += exception =>
            log.Error(exception, "Quartermaster could not persist its latest retainer listings; the in-memory projection remains current.");
        state = new(new QuartermasterStateStore(statePath));
        workQueue = new();
        try
        {
            observationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = configDirectory,
                PluginName = "Quartermaster",
                PluginInstanceId = providerInstanceId,
                GameBuild = GamePatchCompatibilityGate.ReadCurrentGameVersion(),
                GameInventory = gameInventory,
                PlayerState = playerState,
                AddonLifecycle = addonLifecycle,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null)
                        log.Warning(message);
                    else
                        log.Error(exception, message);
                },
            });
            observationConsumer = new(
                configDirectory,
                CurrentOwner,
                DeliverInventoryObservationAsync,
                (message, exception) =>
                {
                    if (exception is null)
                        log.Warning(message);
                    else
                        log.Error(exception, message);
                });
        }
        catch (Exception exception)
        {
            log.Error(exception, "Quartermaster shared inventory observations are unavailable.");
            throw;
        }
        StowagePlanMigration.EnsureOwnerPlan(state, CurrentOwner());
        TransferPlanMigration.EnsureOwnerPlans(state, CurrentOwner());
        var automation = new AutomationLease();
        // Keep retrieval verification bound to the exact inventory changes emitted
        // while its native command is in flight; this survives retainer slot compaction.
        var retainerSession = new DalamudRetainerAutomationSession(
            framework, gameGui, dataManager, log, objects, targets, sigScanner, gameInventory);
        var autoRetainerIpc = new DalamudAutoRetainerIpc(pluginInterface);
        retainerRefresh = new(framework, log, cache, state, retainerSession, observationHost.CaptureSessions, autoRetainerIpc, automation, CurrentOwner);
        journal = new OperationJournal(state);
        RetainerStockMutationPersistence.RecoverPending(journal, cache);
        journal.ReconcileInterruptedOperations();
        var driver = new RetainerLiveDriver(retainerSession);
        var autoRetainerSuppression = new AutoRetainerSuppression(autoRetainerIpc);
        listingNavigation = new(
            retainerSession,
            autoRetainerIpc,
            automation,
            cache,
            observationHost.CaptureSessions,
            CurrentOwner,
            autoRetainerSuppression);
        transfers = new TransferCoordinator(
            journal,
            driver,
            cache,
            CurrentOwner,
            CountCachedPlayerItems,
            automation,
            autoRetainerSuppression: autoRetainerSuppression);
        automaticRetrievals = new(journal, transfers, CurrentOwner, autoRetainerSuppression);
        runtimeSnapshots = new(scanner, playerInventory, cache, state, CurrentOwner);
        var vendorAccess = new DalamudGilVendorAccessReader(
            clientState,
            playerState,
            objects,
            aetheryteList);
        var vendorPlanner = new TransferVendorProcurementPlanner(
            DalamudGilVendorCatalogBuilder.Build(dataManager),
            vendorAccess.Assess);
        externalUiSuppression = new(pluginInterface, log, "Quartermaster");
        var vendorOwnership = new VendorAutomationOwnership(
            automation,
            autoRetainerSuppression,
            externalUiSuppression);
        var vendorRuntime = new DalamudGilVendorBuyRuntime(
            vendorAccess,
            new DalamudOrdinaryGilShop(gameGui, dataManager),
            new DalamudVNavmeshTravel(pluginInterface),
            new DalamudLifestreamAetheryteTravel(pluginInterface),
            new DalamudLifestreamAethernetTravel(pluginInterface),
            new DalamudLifestreamObjectInteractor(pluginInterface),
            new DalamudTravelReadiness(
                condition,
                gameGui,
                objects,
                [
                    "RetainerList",
                    "SelectString",
                    "Talk",
                    "InventoryRetainer",
                    "InventoryRetainerLarge",
                    "InventoryRetainerSmall",
                ]),
            dataManager,
            clientState,
            objects,
            condition,
            commands,
            beginAutomation: () =>
            {
                if (!vendorOwnership.TryAcquire(out var error))
                    throw new InvalidOperationException(error);
            },
            endAutomation: vendorOwnership.Release);
        vendorProcurement = new(
            configuration,
            SaveConfiguration,
            runtimeSnapshots,
            vendorPlanner,
            vendorAccess,
            vendorRuntime,
            vendorOwnership);
        playerInventoryReconciler.ReconcileIfDue(DateTime.UtcNow, force: true);
        var initialSnapshot = runtimeSnapshots.Refresh();
        nextPlayerInventoryFlushAt = DateTime.UtcNow.Add(PlayerInventoryFlushInterval);
        snapshots = new(providerInstanceId, state, cache.Snapshot);
        snapshots.Refresh(initialSnapshot);
        nextSnapshotAt = DateTime.UtcNow.Add(SnapshotRefreshInterval);
        submissions = new ShortageSubmissionService(providerInstanceId, state, workQueue, CurrentOwner);
        deposits = new ElementalDepositSubmissionService(providerInstanceId, state, workQueue, journal, cache.Snapshot, CurrentOwner);
        ipc = new(new DalamudIpcRegistrar(pluginInterface), snapshots, submissions, deposits);
        window = new(
            state,
            runtimeSnapshots,
            journal,
            transfers,
            retainerRefresh,
            listingNavigation,
            vendorProcurement,
            dataManager,
            configuration,
            SaveConfiguration,
            agentReviewRegistry);
        agentBridgeViewportCapture = new(
            configDirectory,
            configuration.PluginInstanceId,
            "Quartermaster",
            () => window.AgentCaptureRegion,
            DispatchOnFramework,
            textureProvider,
            textureReadbackProvider);
        agentBridge = new(
            configuration,
            configDirectory,
            pluginInterface.AssemblyLocation.FullName,
            SaveConfiguration,
            DispatchOnFramework,
            new QuartermasterBridgeProvider(CreateAgentBridgeTruth, window.OpenReviewSurface, window.CloseReviewSurface, agentReviewRegistry),
            window.BeginAgentCapturePresentation,
            window.CompleteAgentCapturePresentation,
            window.CancelAgentCapturePresentation,
            (fullViewport, cancellationToken) => fullViewport
                ? agentBridgeViewportCapture.CaptureAsync(true, cancellationToken)
                : agentBridgeViewportCapture.CaptureWindowAsync(
                    () => window.AgentCaptureWindowName,
                    "PluginWindow",
                    cancellationToken));
        windows.AddWindow(window);

        try
        {
            retainerRefresh.Register();
            state.Changed += OnStateChanged;
            playerInventory.Changed += OnPlayerInventoryChanged;
            cache.Changed += OnCacheChanged;
            cache.ListingCaptured += OnListingCaptured;
            observationHost.Start();
            observationConsumer.Start();
            journal.OperationChanged += OnOperationChanged;
            submissions.OperationChanged += OnSubmittedOperationChanged;
            deposits.OperationChanged += OnSubmittedOperationChanged;
            var commandHelp = "Open Quartermaster.";
#if DEBUG
            commandHelp += " Use '/rq bridge on|off' to control the local development bridge.";
#endif
            commands.AddHandler(Command, new CommandInfo((_, arguments) => HandleCommand(arguments)) { HelpMessage = commandHelp });
            pluginInterface.UiBuilder.Draw += DrawWindows;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            framework.Update += OnFrameworkUpdate;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private OwnerScope CurrentOwner() => new()
    {
        LocalContentId = playerState.ContentId == 0 ? null : playerState.ContentId,
        HomeWorldId = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
        CharacterName = playerState.CharacterName ?? string.Empty,
        HomeWorldName = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty,
    };

    private void DrawWindows()
    {
        try
        {
            windows.Draw();
        }
        finally
        {
            agentBridgeViewportCapture.RenderPendingWindowCapture();
        }
    }

    private IReadOnlyDictionary<uint, int> CountCachedPlayerItems() =>
        playerInventory.Snapshot(CurrentOwner(), scanner.RequestedPlayerStorageSources()).Bags
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));

    private static void ImportLegacyStorageSettings(PluginConfiguration target, string legacyPath)
    {
        if (target.LegacyStorageSettingsImported)
            return;
        if (File.Exists(legacyPath))
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(legacyPath));
            target.IncludeArmoury = ReadBoolean(document.RootElement, "IncludeArmoury", target.IncludeArmoury);
            target.IncludeCrystals = ReadBoolean(document.RootElement, "IncludeCrystals", target.IncludeCrystals);
            target.IncludeEquipped = ReadBoolean(document.RootElement, "IncludeEquipped", target.IncludeEquipped);
            target.IncludeSaddlebag = ReadBoolean(document.RootElement, "IncludeSaddlebag", target.IncludeSaddlebag);
        }
        target.LegacyStorageSettingsImported = true;
    }

    private static bool ReadBoolean(System.Text.Json.JsonElement source, string name, bool fallback)
    {
        foreach (var property in source.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                return property.Value.GetBoolean();
        return fallback;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var liveOwner = CurrentOwner();
        StowagePlanMigration.EnsureOwnerPlan(state, liveOwner);
        TransferPlanMigration.EnsureOwnerPlans(state, liveOwner);
        if (RuntimeOwnerTransition.RequiresReconciliation(runtimeSnapshots.Current.Owner, liveOwner))
            reconciliation.Request(RuntimeDomain.All, "owner_transition");
        workQueue.Drain();
        playerInventoryReconciler.ReconcileIfDue(DateTime.UtcNow);
        automaticRetrievals.Tick();
        vendorProcurement.Tick();
        retainerRefresh.TickRosterDiscovery(window.StockBrowserVisible);
        agentBridge.Tick();
        FlushPlayerInventoryIfDue();
        if (DateTime.UtcNow >= nextSnapshotAt)
            reconciliation.Request(RuntimeDomain.All, "periodic");

        // A running transfer already maintains exact per-command evidence and
        // route-local quantities. Hold expensive stock/planner projection work
        // until that workflow releases inventory ownership; operation progress
        // and listings can still advance independently.
        var allowedDomains = transfers.IsRunning
            ? RuntimeDomain.Listings | RuntimeDomain.Operations
            : RuntimeDomain.All;
        var reconciliationBatch = reconciliation.Drain(allowedDomains);
        if (reconciliationBatch.HasWork)
            ReconcileRuntime(reconciliationBatch);
        window.Tick();
    }

    private void ReconcileRuntime(RuntimeReconciliationBatch batch)
    {
        var runtime = runtimeSnapshots.Refresh(batch.Domains, batch.PlayerInventoryChange);
        if (batch.Domains == RuntimeDomain.Operations)
            snapshots.RefreshOperations(
                runtime.Owner,
                batch.Notices
                    .Select(notice => notice.OperationId)
                    .Where(operationId => !string.IsNullOrWhiteSpace(operationId))
                    .Select(operationId => operationId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        else
            snapshots.Refresh(runtime);
        nextSnapshotAt = DateTime.UtcNow.Add(SnapshotRefreshInterval);
        foreach (var notice in batch.Notices)
            ipc.PublishChanged(snapshots.CreateChanged(notice.Kind, notice.OperationId, CurrentOwner()));
    }

    private void FlushPlayerInventoryIfDue()
    {
        var now = DateTime.UtcNow;
        if (now < nextPlayerInventoryFlushAt)
            return;
        nextPlayerInventoryFlushAt = now.Add(PlayerInventoryFlushInterval);
        playerInventory.Flush();
    }

    private void OnStateChanged(StateChangeKind changeKind)
    {
        var domain = changeKind switch
        {
            StateChangeKind.Listings => RuntimeDomain.Listings,
            StateChangeKind.Operations => RuntimeDomain.Operations,
            StateChangeKind.Recovery => RuntimeDomain.None,
            _ => RuntimeDomain.Plans,
        };
        reconciliation.Request(domain, "state");
    }

    private void OnCacheChanged(RetainerCacheChangeKind changeKind)
    {
        var domain = changeKind switch
        {
            RetainerCacheChangeKind.Stock => RuntimeDomain.RetainerStock,
            RetainerCacheChangeKind.Listings => RuntimeDomain.Listings,
            _ => RuntimeDomain.RetainerStock | RuntimeDomain.Listings,
        };
        reconciliation.Request(domain, "cache");
    }

    private void OnListingCaptured(RetainerListingCaptureReceipt receipt)
    {
        state.Mutate(StateChangeKind.Listings, document => document.LatestRetainerListingCapture = receipt);
        reconciliation.Request(RuntimeDomain.Listings, "retainer_listings");
    }

    private void OnPlayerInventoryChanged(PlayerInventoryCacheChange change)
    {
        reconciliation.Request(change);
    }

    private void OnOperationChanged(OperationRecord operation)
    {
        reconciliation.Request(RuntimeDomain.Operations, "operation", operation.OperationId);
    }

    private void OnSubmittedOperationChanged(string operationId) =>
        reconciliation.Request(RuntimeDomain.Operations, "operation", operationId);

    private void OpenMainUi()
    {
        reconciliation.Request(RuntimeDomain.All, "opened");
        window.IsOpen = true;
    }

    private void HandleCommand(string arguments)
    {
#if DEBUG
        var normalized = arguments.Trim();
        if (normalized.Equals("bridge on", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("bridge off", StringComparison.OrdinalIgnoreCase))
        {
            configuration.EnableAgentBridge = normalized.EndsWith("on", StringComparison.OrdinalIgnoreCase);
            SaveConfiguration();
            return;
        }
#endif
        reconciliation.Request(RuntimeDomain.All, "opened");
        window.IsOpen = true;
    }

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);

    private Task DispatchOnFramework(Action action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workQueue.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    private QuartermasterBridgeTruth CreateAgentBridgeTruth()
    {
        var runtime = runtimeSnapshots.Current;
        var ui = window.CreateUiSnapshot();
        var retainers = runtime.Retainers.Values
            .Where(retainer => runtime.Owner.Matches(retainer.Owner) && retainer.IsCurrentlyAssigned is not false)
            .ToArray();
        // Operation history advances independently from the heavier runtime
        // projection, so bridge truth reads it from the journal authority.
        var operation = journal.Current(runtime.Owner);
        var selectedPlanId = ui.SelectedTransferPlanId;
        var selectedPlanRules = selectedPlanId is { } planId
            ? runtime.State.PlanItems.Where(item => item.StowagePlanId == planId).ToArray()
            : [];
        var listingTiming = listingNavigation.LastRefreshTiming;
        var listingPersistence = cache.LastListingPersistence;
        var vendorRun = vendorProcurement.ActiveRun;
        return new QuartermasterBridgeTruth(
            14,
            configuration.PluginInstanceId,
            Environment.ProcessId,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            ui.MainWindowOpen,
            ui.CurrentWorkspace,
            ui.StockFilter,
            ui.VisibleStockCount,
            ui.RenderedStockRowCount,
            ui.StockProjectionBuildCount,
            ui.StockTableApplyCount,
            ui.TransferProjectionBuildCount,
            ui.RenderedTransferRowCount,
            ui.WindowDrawMilliseconds,
            ui.ContentDrawMilliseconds,
            ui.StockDrawMilliseconds,
            ui.PlanDrawMilliseconds,
            ui.ReviewFinalizeMilliseconds,
            ui.TransferDirection,
            ui.PlanEditorOpen,
            ui.PlanEditorHasUnsavedChanges,
            runtime.State.ItemGroups.Count,
            ui.SelectedItemGroupId,
            ui.SelectedItemGroupName,
            ui.ItemGroupEditorOpen,
            ui.ItemGroupEditorHasUnsavedChanges,
            runtime.Owner.HasStableIdentity ? $"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName}" : "Unavailable",
            runtime.Owner.HasStableIdentity,
            retainers.Length,
            retainers.Length == 0 ? null : retainers.Min(retainer => new DateTimeOffset(DateTime.SpecifyKind(retainer.ObservedAtUtc, DateTimeKind.Utc))),
            selectedPlanRules.Length,
            selectedPlanRules.Count(item => item.Enabled),
            StowagePlanCatalog.OwnerPlans(runtime.State, runtime.Owner).Count,
            ui.SelectedTransferPlanId,
            ui.SelectedTransferPlanName,
            ui.SelectedTransferRetrieveQuantity,
            ui.SelectedTransferDepositQuantity,
            ui.TransferEditorOpen,
            operation?.OperationId,
            operation?.Status,
            retainerRefresh.IsAvailable,
            retainerRefresh.IsRefreshing || retainerRefresh.IsQueued,
            retainerRefresh.Status,
            transfers.IsRunning,
            listingNavigation.IsRunning,
            listingNavigation.Status,
            listingTiming?.RetainerId,
            listingTiming is null ? null : new DateTimeOffset(DateTime.SpecifyKind(listingTiming.CompletedAtUtc, DateTimeKind.Utc)),
            listingTiming?.ObservedToAppliedMilliseconds,
            listingTiming?.ActionToAppliedMilliseconds,
            listingPersistence is null ? null : new DateTimeOffset(DateTime.SpecifyKind(listingPersistence.PersistedAtUtc, DateTimeKind.Utc)),
            listingPersistence is null ? null : Math.Max(0, (listingPersistence.PersistedAtUtc - listingPersistence.ObservedAtUtc).TotalMilliseconds),
            listingPersistence?.WriteMilliseconds,
            vendorRun?.Phase.ToString(),
            vendorRun?.Message,
            vendorRun?.Receipts.Sum(receipt => receipt.Quantity) ?? 0,
            vendorRun?.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil)) ?? 0);
    }

    private void ApplyInventoryObservation(QuartermasterObservationDelivery delivery)
    {
        var owner = CurrentOwner();
        if (!owner.HasStableIdentity ||
            owner.LocalContentId != delivery.Owner.LocalContentId ||
            owner.HomeWorldId != delivery.Owner.HomeWorldId)
            return;

        if (delivery.PlayerBaselines.Count > 0)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var bags = new Dictionary<string, InventoryBag>(StringComparer.Ordinal);
            var observedAtUtc = DateTime.MinValue;
            foreach (var baseline in delivery.PlayerBaselines.Where(candidate =>
                         candidate.Scope.Subject.Kind == ObservationSubjectKind.Character &&
                         candidate.Scope.Container is ObservationContainerKind.PlayerInventory or ObservationContainerKind.Saddlebag))
            {
                var payload = baseline.Payload.Deserialize<InventoryObservationPayload>(
                    baseline.Scope.Container == ObservationContainerKind.Saddlebag
                        ? ObservationPayloadContracts.Saddlebag
                        : ObservationPayloadContracts.PlayerInventory,
                    ObservationPayloadContracts.Version);
                foreach (var containerId in payload.RequestedContainerIds)
                    requested.Add(((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)containerId).ToString());
                foreach (var containerId in payload.ObservedContainerIds)
                {
                    var key = ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)containerId).ToString();
                    observed.Add(key);
                    bags.TryAdd(key, new InventoryBag { BagName = key, Location = key });
                }
                foreach (var row in payload.Items)
                {
                    var key = ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)row.ContainerId).ToString();
                    if (!bags.TryGetValue(key, out var bag))
                        bags[key] = bag = new InventoryBag { BagName = key, Location = key };
                    var metadata = scanner.ResolveItemMetadata(row.ItemId);
                    bag.Items.Add(new Domain.InventoryItem
                    {
                        ItemId = row.ItemId,
                        ItemName = metadata.Name,
                        Quantity = checked((uint)row.Quantity),
                        IsHq = row.IsHighQuality,
                        ItemType = metadata.ItemType,
                        ContainerKey = key,
                        SlotIndex = row.SlotIndex,
                        Equipped = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)row.ContainerId == FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems,
                    });
                }
                var captured = baseline.Capture.ObservedAtUtc.UtcDateTime;
                observedAtUtc = captured > observedAtUtc ? captured : observedAtUtc;
            }
            if (observed.Count > 0)
                playerInventory.Observe(
                    owner,
                    new(
                        bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToArray(),
                        requested.Order(StringComparer.Ordinal).ToArray(),
                        observed.Order(StringComparer.Ordinal).ToArray()),
                    observedAtUtc == DateTime.MinValue ? DateTime.UtcNow : observedAtUtc);
        }
        else if (delivery.PlayerChanges.Count > 0)
        {
            playerInventory.ApplyChanges(owner, delivery.PlayerChanges, scanner.ResolveItemMetadata);
        }

        foreach (var observation in delivery.RetainerObservations.OrderBy(observation => observation.Revision))
            ApplyRetainerObservation(owner, observation);
    }

    private void ApplyRetainerObservation(OwnerScope owner, TrustedObservation observation)
    {
        var observedAtUtc = observation.Capture.ObservedAtUtc.UtcDateTime;
        switch (observation.Scope.Container)
        {
            case ObservationContainerKind.RetainerRoster:
            {
                var payload = observation.Payload.Deserialize<RetainerRosterPayload>(
                    ObservationPayloadContracts.RetainerRoster,
                    ObservationPayloadContracts.Version);
                cache.ReconcileRoster(
                    owner,
                    payload.Retainers
                        .Where(retainer => retainer.WorldId == owner.HomeWorldId)
                        .Select(retainer => new RetainerRosterProjectionEntry(
                            retainer.RetainerId,
                            retainer.Name,
                            retainer.DisplayOrder,
                            retainer.IsUiAccessible,
                            retainer.ClassJobId,
                            retainer.Level,
                            retainer.MarketItemCount,
                            retainer.IsGameAvailable))
                        .ToArray(),
                    observedAtUtc);
                break;
            }
            case ObservationContainerKind.RetainerInventory:
            {
                var payload = observation.Payload.Deserialize<InventoryObservationPayload>(
                    ObservationPayloadContracts.RetainerInventory,
                    ObservationPayloadContracts.Version);
                var bags = payload.ObservedContainerIds
                    .Select(containerId => ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)containerId).ToString())
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(
                        key => key,
                        key => new CachedBag { BagName = key, Location = key, ObservedAtUtc = observedAtUtc },
                        StringComparer.Ordinal);
                foreach (var item in payload.Items)
                {
                    var key = ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)item.ContainerId).ToString();
                    if (!bags.TryGetValue(key, out var bag))
                        bags[key] = bag = new CachedBag { BagName = key, Location = key, ObservedAtUtc = observedAtUtc };
                    var metadata = scanner.ResolveItemMetadata(item.ItemId);
                    bag.Items.Add(new CachedItem
                    {
                        ItemId = item.ItemId,
                        ItemName = metadata.Name,
                        ItemType = metadata.ItemType,
                        Quantity = checked((uint)item.Quantity),
                        IsHq = item.IsHighQuality,
                        ContainerKey = key,
                        SlotIndex = item.SlotIndex,
                    });
                }
                cache.ReplaceInventoryObservation(
                    observation.Scope.Subject.Id,
                    owner,
                    observedAtUtc,
                    bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToArray(),
                    payload.RequestedContainerIds
                        .Select(containerId => ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)containerId).ToString())
                        .ToArray(),
                    payload.ObservedContainerIds
                        .Select(containerId => ((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)containerId).ToString())
                        .ToArray(),
                    observation.Capture.SessionId);
                break;
            }
            case ObservationContainerKind.RetainerGil:
            {
                var payload = observation.Payload.Deserialize<RetainerGilPayload>(
                    ObservationPayloadContracts.RetainerGil,
                    ObservationPayloadContracts.Version);
                cache.ReplaceGilObservation(
                    observation.Scope.Subject.Id,
                    owner,
                    observedAtUtc,
                    payload.Gil,
                    observation.Capture.SessionId);
                break;
            }
            case ObservationContainerKind.RetainerMarketListings:
            {
                var payload = observation.Payload.Deserialize<RetainerMarketListingsPayload>(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version);
                var retainerId = observation.Scope.Subject.Id;
                var retainerName = cache.Snapshot().GetValueOrDefault(retainerId)?.RetainerName;
                cache.ReplaceListings(new RetainerListingsObservation(
                    retainerId,
                    string.IsNullOrWhiteSpace(retainerName) ? $"Retainer {retainerId}" : retainerName,
                    owner,
                    observedAtUtc,
                    payload.Listings.Select(listing =>
                    {
                        var metadata = scanner.ResolveItemMetadata(listing.ItemId);
                        return new CachedMarketListing
                        {
                            ItemId = listing.ItemId,
                            ItemName = metadata.Name,
                            ItemType = metadata.ItemType,
                            Quantity = checked((uint)listing.Quantity),
                            IsHq = listing.IsHighQuality,
                            ContainerKey = FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket.ToString(),
                            SlotIndex = listing.SlotIndex,
                            UnitPrice = checked((uint)listing.UnitPrice),
                            ListedAtUtc = observedAtUtc,
                        };
                    }).ToArray(),
                    observation.Capture.SessionId));
                break;
            }
        }
    }

    private async ValueTask DeliverInventoryObservationAsync(
        QuartermasterObservationDelivery delivery,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        workQueue.Enqueue(() =>
        {
            if (completion.Task.IsCompleted)
                return;
            try
            {
                ApplyInventoryObservation(delivery);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        playerInventory.Flush();
        automaticRetrievals.Dispose();
        listingNavigation.Dispose();
        window.CancelAndWaitForActiveTransfer(TimeSpan.FromSeconds(2));
        vendorProcurement.Dispose();
        externalUiSuppression.Dispose();
        agentBridge.Dispose();
        agentBridgeViewportCapture.Dispose();
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= DrawWindows;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commands.RemoveHandler(Command);
        submissions.OperationChanged -= OnSubmittedOperationChanged;
        deposits.OperationChanged -= OnSubmittedOperationChanged;
        state.Changed -= OnStateChanged;
        playerInventory.Changed -= OnPlayerInventoryChanged;
        cache.Changed -= OnCacheChanged;
        cache.ListingCaptured -= OnListingCaptured;
        journal.OperationChanged -= OnOperationChanged;
        observationConsumer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ipc.Dispose();
        retainerRefresh.Dispose();
        observationHost.Dispose();
        cache.Dispose();
        windows.RemoveAllWindows();
    }
}
