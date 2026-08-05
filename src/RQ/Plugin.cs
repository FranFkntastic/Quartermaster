using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Dalamud.Observations;
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
    private readonly RetainerCaptureService captures;
    private readonly AutoRetainerRefreshService autoRetainer;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly ListingNavigationCoordinator listingNavigation;
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
    private readonly PlayerInventoryObservationConsumer observationConsumer;
    private readonly WindowSystem windows = new("RQ");
    private readonly QuartermasterWindow window;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Kind, string? OperationId)> pendingChanges = new();
    private DateTime nextSnapshotAt;
    private DateTime nextPlayerInventoryFlushAt;
    private int snapshotDirty;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IPlayerState playerState,
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
        captures = new(addonLifecycle, log, scanner, cache, CurrentOwner);
        var automation = new AutomationLease();
        // Keep retrieval verification bound to the exact inventory changes emitted
        // while its native command is in flight; this survives retainer slot compaction.
        var retainerSession = new DalamudRetainerAutomationSession(
            framework, gameGui, dataManager, log, objects, targets, sigScanner, gameInventory);
        var autoRetainerIpc = new DalamudAutoRetainerIpc(pluginInterface);
        autoRetainer = new(framework, log, captures, retainerSession, autoRetainerIpc, automation);
        journal = new OperationJournal(state);
        RetainerStockMutationPersistence.RecoverPending(journal, cache);
        journal.ReconcileInterruptedOperations();
        var driver = new RetainerLiveDriver(retainerSession);
        listingNavigation = new(retainerSession, autoRetainerIpc, automation);
        transfers = new TransferCoordinator(
            journal,
            driver,
            cache,
            CurrentOwner,
            CountCachedPlayerItems,
            automation);
        automaticRetrievals = new(journal, transfers, CurrentOwner, autoRetainerIpc);
        runtimeSnapshots = new(scanner, playerInventory, cache, state, CurrentOwner);
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
            autoRetainer,
            listingNavigation,
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
            agentBridgeViewportCapture.CaptureAsync);
        windows.AddWindow(window);

        try
        {
            captures.Register();
            autoRetainer.Register();
            state.Changed += OnStateChanged;
            playerInventory.Changed += OnPlayerInventoryChanged;
            cache.Changed += OnCacheChanged;
            cache.ListingCaptured += OnListingCaptured;
            captures.CaptureCompleted += OnRetainerCaptureCompleted;
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
            pluginInterface.UiBuilder.Draw += windows.Draw;
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
        StowagePlanMigration.EnsureOwnerPlan(state, CurrentOwner());
        TransferPlanMigration.EnsureOwnerPlans(state, CurrentOwner());
        workQueue.Drain();
        playerInventoryReconciler.ReconcileIfDue(DateTime.UtcNow);
        captures.TickPassive();
        automaticRetrievals.Tick();
        autoRetainer.TickAutomatic(window.StockBrowserVisible);
        agentBridge.Tick();
        FlushPlayerInventoryIfDue();
        if (Interlocked.Exchange(ref snapshotDirty, 0) != 0 || DateTime.UtcNow >= nextSnapshotAt)
            RefreshSnapshot();
        window.Tick();
        while (pendingChanges.TryDequeue(out var change))
            ipc.PublishChanged(snapshots.CreateChanged(change.Kind, change.OperationId, CurrentOwner()));
    }

    private void RefreshSnapshot()
    {
        snapshots.Refresh(runtimeSnapshots.Refresh());
        nextSnapshotAt = DateTime.UtcNow.Add(SnapshotRefreshInterval);
    }

    private void FlushPlayerInventoryIfDue()
    {
        var now = DateTime.UtcNow;
        if (now < nextPlayerInventoryFlushAt)
            return;
        nextPlayerInventoryFlushAt = now.Add(PlayerInventoryFlushInterval);
        playerInventory.Flush();
    }

    private void OnStateChanged()
    {
        MarkChanged("state", null);
    }

    private void OnCacheChanged()
    {
        MarkChanged("cache", null);
    }

    private void OnListingCaptured(RetainerListingCaptureReceipt receipt)
    {
        state.Mutate(document => document.LatestRetainerListingCapture = receipt);
        MarkChanged("retainer_listings", null);
    }

    private void OnRetainerCaptureCompleted(CaptureReceipt receipt)
    {
        if (receipt.Outcome != CaptureOutcome.Persisted ||
            cache.Snapshot().GetValueOrDefault(receipt.RetainerId) is not { } retainer)
            return;
    }

    private void OnPlayerInventoryChanged(PlayerInventoryCacheChange change)
    {
        snapshots.Refresh(runtimeSnapshots.ApplyPlayerInventoryChange(change));
        nextSnapshotAt = DateTime.UtcNow.Add(SnapshotRefreshInterval);
        pendingChanges.Enqueue(("player_inventory", null));
    }

    private void OnOperationChanged(OperationRecord operation)
    {
        MarkChanged("operation", operation.OperationId);
    }

    private void OnSubmittedOperationChanged(string operationId) => MarkChanged("operation", operationId);

    private void MarkChanged(string kind, string? operationId)
    {
        pendingChanges.Enqueue((kind, operationId));
        Interlocked.Exchange(ref snapshotDirty, 1);
    }

    private void OpenMainUi()
    {
        RefreshSnapshot();
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
        RefreshSnapshot();
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
        var retainers = runtime.Retainers.Values.Where(retainer => runtime.Owner.Matches(retainer.Owner)).ToArray();
        var operation = runtime.State.Operations
            .Where(candidate => candidate.Owner.Matches(runtime.Owner))
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .FirstOrDefault();
        var selectedPlanId = window.SelectedStowagePlanId;
        var selectedPlanRules = selectedPlanId is { } planId
            ? runtime.State.PlanItems.Where(item => item.StowagePlanId == planId).ToArray()
            : [];
        return new QuartermasterBridgeTruth(
            12,
            configuration.PluginInstanceId,
            Environment.ProcessId,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            window.IsOpen,
            window.CurrentWorkspace,
            window.StockFilter,
            window.VisibleStockCount,
            window.RenderedStockRowCount,
            window.StockProjectionBuildCount,
            window.StockTableApplyCount,
            window.TransferProjectionBuildCount,
            window.RenderedTransferRowCount,
            window.WindowDrawMilliseconds,
            window.ContentDrawMilliseconds,
            window.StockDrawMilliseconds,
            window.PlanDrawMilliseconds,
            window.ReviewFinalizeMilliseconds,
            window.CurrentTransferDirection,
            window.RestockEditorOpen || window.StowageEditorOpen,
            window.PlanEditorHasUnsavedChanges,
            runtime.State.ItemGroups.Count,
            window.SelectedItemGroupId,
            window.SelectedItemGroupName,
            window.ItemGroupEditorOpen,
            window.ItemGroupEditorHasUnsavedChanges,
            runtime.Owner.HasStableIdentity ? $"{runtime.Owner.CharacterName} @ {runtime.Owner.HomeWorldName}" : "Unavailable",
            runtime.Owner.HasStableIdentity,
            retainers.Length,
            retainers.Length == 0 ? null : retainers.Min(retainer => new DateTimeOffset(DateTime.SpecifyKind(retainer.ObservedAtUtc, DateTimeKind.Utc))),
            selectedPlanRules.Length,
            selectedPlanRules.Count(item => item.Enabled),
            StowagePlanCatalog.OwnerPlans(runtime.State, runtime.Owner).Count,
            window.SelectedStowagePlanId,
            window.SelectedStowagePlanName,
            window.SelectedRestockNeededQuantity,
            window.SelectedTransferDepositQuantity,
            window.StowageEditorOpen,
            operation?.OperationId,
            operation?.Status,
            autoRetainer.IsAvailable,
            autoRetainer.IsRefreshing || autoRetainer.IsQueued,
            autoRetainer.Status,
            transfers.IsRunning,
            listingNavigation.IsRunning,
            listingNavigation.Status);
    }

    private void ApplyInventoryObservation(PlayerInventoryObservationDelivery delivery)
    {
        var owner = CurrentOwner();
        if (!owner.HasStableIdentity ||
            owner.LocalContentId != delivery.Owner.LocalContentId ||
            owner.HomeWorldId != delivery.Owner.HomeWorldId)
            return;

        if (delivery.Baselines.Count > 0)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var bags = new Dictionary<string, InventoryBag>(StringComparer.Ordinal);
            var observedAtUtc = DateTime.MinValue;
            foreach (var baseline in delivery.Baselines.Where(candidate =>
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
            return;
        }

        playerInventory.ApplyChanges(owner, delivery.Changes, scanner.ResolveItemMetadata);
    }

    private async ValueTask DeliverInventoryObservationAsync(
        PlayerInventoryObservationDelivery delivery,
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
        agentBridge.Dispose();
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commands.RemoveHandler(Command);
        submissions.OperationChanged -= OnSubmittedOperationChanged;
        deposits.OperationChanged -= OnSubmittedOperationChanged;
        state.Changed -= OnStateChanged;
        playerInventory.Changed -= OnPlayerInventoryChanged;
        cache.Changed -= OnCacheChanged;
        cache.ListingCaptured -= OnListingCaptured;
        captures.CaptureCompleted -= OnRetainerCaptureCompleted;
        journal.OperationChanged -= OnOperationChanged;
        observationConsumer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ipc.Dispose();
        autoRetainer.Dispose();
        captures.Dispose();
        observationHost.Dispose();
        windows.RemoveAllWindows();
    }
}
