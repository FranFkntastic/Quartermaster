using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Runtime;
using Franthropy.Dalamud.Travel;
using Franthropy.Observations.V1;
using RQ.AgentBridge;
using RQ.Courier;
using Franthropy.Dalamud.Automation.Trades;
using RQ.Automation;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
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
    private readonly FrameTimings updateTimings;
    private readonly IPlayerState playerState;
    private readonly ComplicatedMarketBoardCompetitionClient marketCompetition;
    private readonly PluginConfiguration configuration;
    private readonly string providerInstanceId = Guid.NewGuid().ToString("N");
    private readonly FrameworkWorkQueue workQueue;
    private readonly InventoryScanner scanner;
    // Courier wiring (Q2) sets Link, Conductor and Follower on this session.
    private readonly CourierSession courier;
    private readonly ExternalAccountStockCache externalAccountStock;
    private readonly CourierPeerLink courierPeer;
    private readonly CourierGame courierGame;
    private readonly CourierTrade courierTrade;
    private ulong courierActor;
    private readonly PlayerInventoryCacheRepository playerInventory;
    private readonly PlayerInventoryReconciler playerInventoryReconciler;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly RetainerRefreshCoordinator retainerRefresh;
    private readonly AutomationLease automation;
    private readonly IPluginLog pluginLog;
    private (long Cache, long State) retainerNameRefreshRevisions = (-1, -1);
    private readonly RetainerBellRoute retainerBellRoute;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly DalamudInventoryMover inventoryMover;
    private readonly ListingNavigationCoordinator listingNavigation;
    private readonly MarketRunExecutor marketRuns;
    private readonly DalamudOrdinaryGilShop vendorShop;
    private readonly TransferVendorProcurementService vendorProcurement;
    private readonly VendorCatalogLocationObserver vendorCatalogObserver;
    private readonly DalamudExternalUiAutomationSuppression externalUiSuppression;
    private readonly AutoRetainerSuppression autoRetainerSuppression;
    /// <summary>The cross-plugin AutoRetainer holder registry (Dalamud shared data); null when it could not be obtained.</summary>
    private readonly HashSet<string>? autoRetainerHolders;
    private readonly AutomaticRetrievalQueue automaticRetrievals;
    private readonly QuartermasterRuntimeSnapshotSource runtimeSnapshots;
    private readonly SnapshotPublisher snapshots;
    private readonly ShortageSubmissionService submissions;
    private readonly ElementalDepositSubmissionService deposits;
    private readonly QuartermasterIpcProvider ipc;
    private readonly DeliveredStowService deliveredStow;
    private readonly RQ.AgentBridge.AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;
    private readonly AgentBridgeUiReviewRegistry agentReviewRegistry = new();
    private readonly DalamudSharedObservationHost? observationHost;
    private readonly DalamudSharedObservationClient observationClient;
    private readonly WindowSystem windows = new("RQ");
    private readonly QuartermasterWindow window;
    private readonly RuntimeReconciliationQueue reconciliation = new();
    private DateTime nextSnapshotAt;
    private DateTime nextSuppressionCheckAt;
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
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        pluginLog = log;
        updateTimings = new(log, "OnFrameworkUpdate", 50);
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
            configuration.IncludeEquipped));
        playerInventory = new(new PlayerInventoryCacheStore(playerInventoryCachePath));
        playerInventory.PersistenceFailed += failure => LogCacheWriteFailure(log, "player inventory", failure);
        playerInventoryReconciler = new(
            playerInventory,
            CurrentOwner,
            scanner.CapturePlayerStorage,
            PlayerInventoryReconciliationInterval);
        cache = new(new RetainerCacheStore(cachePath));
        cache.ListingPersistenceFailed += exception =>
            log.Error(exception, "Quartermaster could not persist its latest retainer listings; the in-memory projection remains current.");
        cache.CachePersistenceFailed += failure => LogCacheWriteFailure(log, "retainer", failure);
        state = new(new QuartermasterStateStore(statePath));
        // Player bag baselines come from characters observed on this client; imported retainer caches do not.
        var priorActors=playerInventory.Owners().Where(o=>o.HasStableIdentity && !string.IsNullOrWhiteSpace(o.CharacterName)).ToArray();
        if(state.ReadConfiguration(s=>priorActors.Any(o=>!s.Courier.LocalRoster.Any(x=>CourierPlanner.Exact(x,o)))))
            state.Mutate(s=>s.Courier=s.Courier with {LocalRoster=[..priorActors.Concat(s.Courier.LocalRoster).DistinctBy(o=>o.LocalContentId)]});
        workQueue = new();
        var observationPaths = SharedObservationPaths.FromPluginConfigDirectory(configDirectory);
        var captureSessions = new ObservationCaptureSessionRegistry(observationPaths.CaptureSessionsPath);
        var observationRuntime = SharedObservationRuntime.Create(
            captureSessions,
            () => new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = configDirectory,
                PluginName = "Quartermaster",
                PluginInstanceId = providerInstanceId,
                GameBuild = GameClientVersion.ReadCurrentGameVersion(),
                GameInventory = gameInventory,
                PlayerState = playerState,
                AddonLifecycle = addonLifecycle,
                Framework = framework,
                InteropProvider = gameInterop,
                SigScanner = sigScanner,
                PluginLog = log,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null)
                        log.Warning(message);
                    else
                        log.Error(exception, message);
                },
            }),
            exception => log.Warning(exception, "Quartermaster cannot host the shared collector on this game build; it will consume Franthropy observations from another eligible plugin."));
        observationHost = observationRuntime.Host;
        captureSessions = observationRuntime.CaptureSessions;
        observationClient = new(new DalamudSharedObservationClientOptions
        {
            PluginConfigDirectory = configDirectory,
            CurrentOwner = () =>
            {
                var current = CurrentOwner();
                return current.HasStableIdentity
                    ? new ObservationOwner(current.LocalContentId!.Value, current.HomeWorldId!.Value)
                    : null;
            },
            Deliver = DeliverInventoryObservationAsync,
            Diagnostic = (message, exception) =>
            {
                if (exception is null)
                    log.Warning(message);
                else
                    log.Error(exception, message);
            },
        });
        StowagePlanMigration.EnsureOwnerPlan(state, CurrentOwner());
        TransferPlanMigration.EnsureOwnerPlans(state, CurrentOwner());
        MarketRunExecutor? activeMarketRuns = null;
        automation = new AutomationLease(() => activeMarketRuns?.Result?.RequiresAcknowledgement == true
            ? "Unresolved market run; review its result in Market before clearing it." : null);
        // Keep retrieval verification bound to the exact inventory changes emitted
        // while its native command is in flight; this survives retainer slot compaction.
        var retainerSession = new DalamudRetainerAutomationSession(
            framework, gameGui, dataManager, log, objects, targets, sigScanner, gameInventory);
        var autoRetainerIpc = new DalamudAutoRetainerIpc(pluginInterface);
        retainerRefresh = new(framework, log, cache, state, retainerSession, captureSessions, autoRetainerIpc, automation, CurrentOwner);
        journal = new OperationJournal(state);
        RetainerStockMutationPersistence.RecoverPending(journal, cache);
        journal.ReconcileInterruptedOperations();
        retainerBellRoute = new(
            framework,
            new DalamudRetainerBellRoutePort(
                new DalamudSummoningBellInteractor(objects, targets, dataManager),
                new DalamudVNavmeshTravel(pluginInterface),
                commands,
                pluginInterface,
                condition,
                objects));
        var driver = new RetainerLiveDriver(retainerSession, retainerBellRoute, message => log.Warning(message));
        // The marker records that this load suppressed AutoRetainer, so a load that dies holding it is reclaimed by the next.
        // The holder registry is shared with other Gooseworks plugins, so none unsuppresses AutoRetainer while another holds it.
        try { autoRetainerHolders = pluginInterface.GetOrCreateData(AutoRetainerSuppression.HolderRegistryKey, () => new HashSet<string>(StringComparer.Ordinal)); }
        catch (Exception exception)
        {
            log.Warning($"[Quartermaster] AutoRetainerHolderRegistryUnavailable: {AutoRetainerSuppression.HolderRegistryKey}: {exception.Message}; " +
                "AutoRetainer suppression works without it, but may lift another plugin's suppression.");
        }
        autoRetainerSuppression = new AutoRetainerSuppression(autoRetainerIpc,
            Path.Combine(configDirectory, "autoretainer-suppression.marker"), message => log.Warning(message), autoRetainerHolders,
            message => log.Information(message));
        listingNavigation = new(
            retainerSession,
            autoRetainerIpc,
            automation,
            cache,
            captureSessions,
            CurrentOwner,
            autoRetainerSuppression);
        // Lazy: constructing the mover installs no hooks and reads no native memory.
        inventoryMover = new DalamudInventoryMover(sigScanner, gameInterop, dataManager, log);
        var saddlebagDriver = new SaddlebagLiveDriver(framework, inventoryMover);
        transfers = new TransferCoordinator(
            journal,
            driver,
            cache,
            CurrentOwner,
            CountCachedPlayerItems,
            automation,
            autoRetainerSuppression: autoRetainerSuppression,
            warning: message => log.Warning(message),
            saddlebagDriver: saddlebagDriver,
            cachedSaddlebag: CountCachedSaddlebagItems,
            information: message => log.Information(message));
        var marketRunStore = new MarketRunStore(Path.Combine(configDirectory, "market-run.json"));
        marketRuns = new(new MarketRunLiveDriver(retainerSession, driver, journal, cache, playerInventory,
                automation, CurrentOwner, id => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id)?.Name.ToString() ?? $"Item {id}", message => log.Warning(message),
                saddlebagDriver, CountCachedSaddlebagItems, message => log.Information(message),
                id => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id)?.StackSize is > 0 and var size
                    ? checked((int)size) : throw new InvalidOperationException($"Stack size unavailable for item {id}.")),
            automation, planId => state.Read(s => ListingPlanCatalog.FindPlan(s, CurrentOwner(), planId)), marketRunStore.Save, autoRetainerSuppression, clear: marketRunStore.Clear, warning: message => log.Warning(message),
            information: message => log.Information(message), retainerRoster: cache.Snapshot);
        marketRuns.Restore(marketRunStore.Load());
        activeMarketRuns = marketRuns;
        automaticRetrievals = new(journal, transfers, CurrentOwner, autoRetainerSuppression, message => log.Warning(message));
        runtimeSnapshots = new(scanner, playerInventory, cache, state, CurrentOwner);
        var vendorAccess = new DalamudGilVendorAccessReader(
            clientState,
            playerState,
            objects,
            aetheryteList);
        var vendorLocationObserver = new DalamudVendorLocationObserver();
        var vendorCatalogSource = new MutableVendorCatalogSource(
            DalamudGilVendorCatalogBuilder.Build(dataManager));
        var vendorPlanner = new TransferVendorProcurementPlanner(
            () => vendorCatalogSource.Current,
            vendorAccess.Assess);
        vendorCatalogObserver = new VendorCatalogLocationObserver(
            objects,
            clientState,
            vendorCatalogSource,
            vendorLocationObserver,
            dataManager,
            log);
        externalUiSuppression = new(pluginInterface, log, "Quartermaster");
        var vendorOwnership = new VendorAutomationOwnership(
            automation,
            autoRetainerSuppression,
            externalUiSuppression);
        vendorShop = new DalamudOrdinaryGilShop(gameGui, dataManager, addonLifecycle);
        var vendorRuntime = new DalamudGilVendorBuyRuntime(
            vendorAccess,
            vendorShop,
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
        submissions = new ShortageSubmissionService(providerInstanceId, state, workQueue, CurrentOwner,
            retainers: cache.Snapshot, warn: message => log.Warning(message));
        deposits = new ElementalDepositSubmissionService(providerInstanceId, state, workQueue, journal, cache.Snapshot, CurrentOwner);
        ipc = new(new DalamudIpcRegistrar(pluginInterface), snapshots, submissions, deposits);
        var requestedOperationCancel = new RequestedOperationCancelService(providerInstanceId, journal, workQueue, message => log.Warning(message));
        ipc.AttachRequestedOperationCancel(requestedOperationCancel);
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
            agentReviewRegistry,
            () => vendorCatalogSource.Current,
            new ItemMetadataCatalog(dataManager, log).ResolveWorkshopProject,
            configDirectory,
            new MarketMafiosoSavedJobClient(pluginInterface),
            log,
            courier = new CourierSession(
                state,
                owner => playerInventory.Snapshot(owner, CourierSession.PlayerStorage),
                scanner.ResolveItemMetadata));
        var tradeIo = new DalamudTradeIo(gameGui,targets,objects,playerState,condition,sigScanner,dataManager,log);
        courierGame = new(pluginInterface,playerState,clientState,objects,condition,commands,dataManager,aetheryteList,
            new DalamudSummoningBellInteractor(objects,targets,dataManager),tradeIo,scanner,CurrentOwner,DispatchOnFramework,configDirectory,s=>log.Warning(s));
        courierTrade = new(courierGame,tradeIo,automation,pluginInterface,externalUiSuppression,log,configDirectory);
        courierTrade.RestoreLeftoverSuppression();
        bool AutoRetainerLoaded()=>pluginInterface.InstalledPlugins.Any(p=>p.IsLoaded && p.InternalName=="AutoRetainer");
        courier.Warn = s => log.Warning(s);
        courier.WorldRegion = worldId => dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(worldId)?.DataCenter.ValueNullable?.Region.RowId ?? 0;
        // Sibling XIVLauncher profiles' caches: read-only stock browser visibility, never courier stock.
        courier.LocalOwners = playerInventory.Owners;
        courier.ExternalStockCache = externalAccountStock = new ExternalAccountStockCache(configDirectory, s => log.Warning(s));
        courier.Follower = new(configDirectory,()=>state.ReadConfiguration(s=>s.Courier),automation,autoRetainerSuppression,courierTrade,
            courierGame,courierGame,courierGame,s=>log.Warning(s),s=>log.Information(s),AutoRetainerLoaded,courierGame.LifestreamBusy);
        courier.Follower.RestoreCustodyHold();
        courier.Link = courierPeer = new(courierGame,courier.Follower,DispatchOnFramework,s=>log.Warning(s),()=>state.ReadConfiguration(s=>s.Courier),
            change=>courier.UpdateSettings(change));
        courier.Conductor = new(configDirectory,courierPeer,courierTrade,courierGame,courierGame,courierGame,courierGame,
            transfers,automation,autoRetainerSuppression,s=>log.Warning(s),s=>log.Information(s),AutoRetainerLoaded,courierGame.LifestreamBusy);
        courierPeer.OwnerRun = courier.Conductor.CurrentRun;
        courierPeer.OwnerActiveRun = courier.Conductor.ActiveRun;
        var courierWorldRegion = courier.WorldRegion!;
        ipc.AttachCourier(new Interop.CourierIpcService(providerInstanceId, courier,
            () => state.ReadConfiguration(s => s.Courier), () => courierPeer.LocalBags, courierGame.Bridge,
            courierWorldRegion,
            worldId => dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(worldId)?.Name.ToString() ?? "",
            scanner.ResolveItemMetadata, s => log.Warning(s), () => courier.Locations(runtimeSnapshots.Current),
            retainerId => runtimeSnapshots.Current.Retainers.GetValueOrDefault(retainerId)?.RetainerName));
        int StackSize(uint id) => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id)?.StackSize is > 0 and var stack
            ? checked((int)stack) : throw new InvalidOperationException($"Stack size unavailable for item {id}.");
        deliveredStow = new DeliveredStowService(providerInstanceId, state, workQueue, journal, cache.Snapshot, CurrentOwner,
            // Live read: the cached snapshot can lag a delivery by a reconcile tick and omits the crystal pouch.
            _ => scanner.ScanCarried(),
            StackSize,
            message => log.Warning(message),
            courier: new Interop.DeliveredStowCourier(courier, () => state.ReadConfiguration(s => s.Courier), () => courierPeer.LocalBags,
                courierGame.Bridge, courierWorldRegion, scanner.ResolveItemMetadata, () => runtimeSnapshots.Current, automation),
            forwardStorePath: Path.Combine(configDirectory, "delivered-stow-forwards.json"));
        ipc.AttachDeliveredStow(deliveredStow);
        // A requester's cancel of a delivered stow is recorded on its forward too, so the forward never starts afterwards.
        requestedOperationCancel.RequesterCancelled = deliveredStow.RecordRequesterCancel;
        window.CancelRequestedOperation = requestedOperationCancel.CancelByOperator;
        var workbenchStock = new Interop.WorkbenchStockService(providerInstanceId, () => runtimeSnapshots.Current, playerInventory,
            () => externalAccountStock.Current, state, workQueue, s => log.Warning(s), StackSize);
        var placementEditor = new PlacementEditorWindow(() => runtimeSnapshots.Current, workbenchStock, courier);
        windows.AddWindow(placementEditor);
        workbenchStock.OpenPlacementEditor = placementEditor.Open;
        workbenchStock.PlacementCharacters = current => courier.DestinationCharacters(current);
        workbenchStock.CharacterStock = (current, character) => courier.Stock(current, character);
        // The placement channels run on the framework thread (inline when the caller is already on it).
        workbenchStock.RunOnFramework = work => framework.RunOnFrameworkThread(work).GetAwaiter().GetResult();
        workbenchStock.ItemNames = scanner.ResolveItemName;
        // Reserve IPC reads and writes the Market tab's active Listing Plan.
        workbenchStock.SelectedListingPlanId = () => window.SelectedListingPlanId;
        ipc.AttachWorkbench(workbenchStock);
        var marketHistory = new MarketRunHistoryWindow(marketRunStore.History, CurrentOwner, window.DrawTimings)
        {
            Size = new System.Numerics.Vector2(780, 650),
            SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.FirstUseEver,
        };
        windows.AddWindow(marketHistory);
        marketCompetition = new(pluginInterface);
        window.SetMarketRunExecutor(marketRuns, marketHistory, new ComplicatedMarketBoardItemClient(pluginInterface));
        window.SetMarketCompetition(marketCompetition, configuration, SaveConfiguration);
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
            new QuartermasterBridgeProvider(CreateAgentBridgeTruth, window.OpenReviewSurface, window.CloseReviewSurface, agentReviewRegistry)
                {SetCourierSlotCap=courier.SetTestSlotCap},
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
            observationHost?.Start();
            observationClient.Start();
            journal.OperationChanged += OnOperationChanged;
            submissions.OperationChanged += OnSubmittedOperationChanged;
            deposits.OperationChanged += OnSubmittedOperationChanged;
            deliveredStow.OperationChanged += OnSubmittedOperationChanged;
            var commandHelp = "Open Quartermaster.";
            commandHelp += " Use '/rq bridge on|off' to control the local agent bridge (off by default).";
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
        using var timing = window.DrawTimings.Start("window system");
        try
        {
            windows.Draw();
        }
        finally
        {
            window.DrawTimings.Section("viewport capture");
            agentBridgeViewportCapture.RenderPendingWindowCapture();
        }
    }

    private IReadOnlyDictionary<uint, int> CountCachedPlayerItems() =>
        playerInventory.Snapshot(CurrentOwner(), scanner.RequestedPlayerStorageSources()).Bags
            .Where(bag => !InventoryScanner.IsSaddlebag(bag.BagName))
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));

    private IReadOnlyDictionary<uint, int> CountCachedSaddlebagItems() =>
        playerInventory.Snapshot(CurrentOwner(), scanner.RequestedPlayerStorageSources()).Bags
            .Where(bag => InventoryScanner.IsSaddlebag(bag.BagName))
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

    /// <summary>
    /// Keeps Listing Plan retainer-name snapshots in step with renames seen on the roster, through the plan store with a
    /// revision bump. Never while automation holds the lease: a plan revision change stops a running market run.
    /// </summary>
    private void RefreshListingPlanRetainerNames()
    {
        var revisions = (cache.Revision, state.ReadConfiguration(s => s.Revision));
        if (revisions == retainerNameRefreshRevisions || automation.IsHeld) return;
        retainerNameRefreshRevisions = revisions;
        var roster = cache.Snapshot();
        if (!state.ReadConfiguration(s => ListingPlanCatalog.HasStaleRetainerNames(s, roster))) return;
        var renamed = state.Mutate(StateChangeKind.Listings, s => ListingPlanCatalog.RefreshRetainerNames(s, roster, DateTime.UtcNow));
        if (renamed > 0) pluginLog.Information($"ListingPlanRetainerNamesRefreshed: {renamed} assignment(s) now use the current roster name for their retainer ID.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var actor=playerState.IsLoaded?playerState.ContentId:0;
        if(actor!=0 && actor!=courierActor && CurrentOwner() is {HasStableIdentity:true} seen &&
            !state.ReadConfiguration(s=>s.Courier.LocalRoster.Any(o=>CourierPlanner.Exact(o,seen))))
            state.Mutate(s=>s.Courier=s.Courier with {LocalRoster=[..s.Courier.LocalRoster.Where(o=>o.LocalContentId!=actor),seen]});
        courierPeer.Tick(actor!=courierActor);
        courierActor=actor;
        using var timing = updateTimings.Start("owner and migrations");
        var liveOwner = CurrentOwner();
        StowagePlanMigration.EnsureOwnerPlan(state, liveOwner);
        TransferPlanMigration.EnsureOwnerPlans(state, liveOwner);
        RefreshListingPlanRetainerNames();
        // Courier lines written before sources and destinations were listed apart need observed stock to convert.
        courier.MigrateRoles(runtimeSnapshots.Current);
        if (RuntimeOwnerTransition.RequiresReconciliation(runtimeSnapshots.Current.Owner, liveOwner))
            reconciliation.Request(RuntimeDomain.All, "owner_transition");
        updateTimings.Section("work queue");
        workQueue.Drain();
        updateTimings.Section("player inventory");
        playerInventoryReconciler.ReconcileIfDue(DateTime.UtcNow);
        updateTimings.Section("automatic retrieval");
        automaticRetrievals.Tick();
        updateTimings.Section("delivered stow forwards");
        deliveredStow.Tick();
        updateTimings.Section("AutoRetainer suppression");
        TickAutoRetainerSuppression();
        updateTimings.Section("vendor observer");
        vendorCatalogObserver.Tick();
        updateTimings.Section("vendor procurement");
        vendorProcurement.Tick();
        updateTimings.Section("roster");
        retainerRefresh.TickRosterDiscovery(window.StockBrowserVisible);
        updateTimings.Section("agent bridge");
        agentBridge.Tick();
        updateTimings.Section("inventory flush");
        FlushPlayerInventoryIfDue();
        updateTimings.Section("reconciliation queue");
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
        updateTimings.Section("transfer execution");
        window.Tick();
    }

    private void ReconcileRuntime(RuntimeReconciliationBatch batch)
    {
        updateTimings.Section("runtime projections");
        var runtime = runtimeSnapshots.Refresh(batch.Domains, batch.PlayerInventoryChange);
        updateTimings.Section("IPC snapshot");
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
        updateTimings.Section("change notices");
        foreach (var notice in batch.Notices)
        {
            if (RuntimeChangeNotificationPolicy.TryGetPublishedKind(notice, out var kind))
                ipc.PublishChanged(snapshots.CreateChanged(kind, notice.OperationId, CurrentOwner()));
        }
    }

    private static void LogCacheWriteFailure(IPluginLog log, string cacheName, CacheWriteFailure failure)
    {
        const string template = "Quartermaster could not persist its {CacheName} cache to {Path} (revision {Revision}, {EntryCount} entries, attempt {Attempt}, retrying: {WillRetry}); the in-memory cache remains current.";
        if (failure.WillRetry)
            log.Warning(failure.Exception, template, cacheName, failure.Path, failure.Revision, failure.EntryCount, failure.Attempt, failure.WillRetry);
        else
            log.Error(failure.Exception, template, cacheName, failure.Path, failure.Revision, failure.EntryCount, failure.Attempt, failure.WillRetry);
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
        var normalized = arguments.Trim();
        if (normalized.Equals("bridge on", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("bridge off", StringComparison.OrdinalIgnoreCase))
        {
            configuration.EnableAgentBridge = normalized.EndsWith("on", StringComparison.OrdinalIgnoreCase);
            SaveConfiguration();
            return;
        }
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
            17,
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
            vendorRun?.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil)) ?? 0,
            courier.Truth(runtime,selectedPlanId));
    }

    private void ApplyInventoryObservation(SharedObservationDelivery delivery)
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
        SharedObservationDelivery delivery,
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

    /// <summary>
    /// Reclaims an AutoRetainer suppression an earlier load died holding (retried until AutoRetainer answers) and
    /// re-asserts the suppression while Quartermaster holds it, every couple of seconds.
    /// </summary>
    private void TickAutoRetainerSuppression()
    {
        var now = DateTime.UtcNow;
        if (now < nextSuppressionCheckAt)
            return;
        nextSuppressionCheckAt = now.AddSeconds(2);
        autoRetainerSuppression.ReclaimOrphaned();
        autoRetainerSuppression.Reassert();
    }

    public void Dispose()
    {
        // Every step runs whatever an earlier one threw. Everything that drives the game (courier, retrievals, market
        // runs, transfers, vendors) is stopped and drained first, and only then are other plugins' automations
        // (TextAdvance and the rest of the external UI suppression, AutoRetainer) given back, so none of them goes live
        // while Quartermaster still holds a retainer or trade window. The courier's trade pauses (YesAlready, Dropbox)
        // are lifted by the courier trade once the courier has drained. A step that throws never skips the restores.
        DisposalSteps.DisposeAll(
            (name, exception) => pluginLog.Warning($"[Quartermaster] UnloadStepFailed: {name}: {exception.GetType().Name}: {exception.Message}"),
            ("courier conductor unload", () => courier.Conductor?.Unload()),
            ("courier peer link", courierPeer.Dispose),
            ("courier game", courierGame.Dispose),
            ("courier conductor drain", () => courier.Conductor?.DrainUnload()),
            ("courier follower", () => courier.Follower?.Dispose()),
            ("courier trade", courierTrade.Dispose),
            ("player inventory flush", playerInventory.Flush),
            ("automatic retrievals", automaticRetrievals.Dispose),
            ("market competition", marketCompetition.Dispose),
            ("listing navigation", listingNavigation.Dispose),
            ("market runs", () => marketRuns.CancelAndWait(TimeSpan.FromSeconds(2))),
            ("active transfer", () => window.CancelAndWaitForActiveTransfer(TimeSpan.FromSeconds(2))),
            ("inventory mover", inventoryMover.Dispose),
            ("retainer bell route", retainerBellRoute.Dispose),
            ("vendor procurement", vendorProcurement.Dispose),
            ("vendor shop", vendorShop.Dispose),
            ("external UI suppression", externalUiSuppression.Dispose),
            ("AutoRetainer suppression", autoRetainerSuppression.RestoreOnUnload),
            ("agent bridge", agentBridge.Dispose),
            ("agent bridge viewport capture", agentBridgeViewportCapture.Dispose),
            ("framework update", () => framework.Update -= OnFrameworkUpdate),
            ("UI draw", () => pluginInterface.UiBuilder.Draw -= DrawWindows),
            ("open main UI", () => pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi),
            ("command", () => commands.RemoveHandler(Command)),
            ("operation events", () =>
            {
                submissions.OperationChanged -= OnSubmittedOperationChanged;
                deposits.OperationChanged -= OnSubmittedOperationChanged;
                deliveredStow.OperationChanged -= OnSubmittedOperationChanged;
            }),
            ("state events", () =>
            {
                state.Changed -= OnStateChanged;
                playerInventory.Changed -= OnPlayerInventoryChanged;
                cache.Changed -= OnCacheChanged;
                cache.ListingCaptured -= OnListingCaptured;
                journal.OperationChanged -= OnOperationChanged;
            }),
            ("observation client", () => observationClient.DisposeAsync().AsTask().GetAwaiter().GetResult()),
            ("IPC", ipc.Dispose),
            ("retainer refresh", retainerRefresh.Dispose),
            ("observation host", () => observationHost?.Dispose()),
            ("cache", cache.Dispose),
            ("player inventory", playerInventory.Dispose),
            ("external account stock", externalAccountStock.Dispose),
            // A scope a failed step never released still leaves AutoRetainer as this load found it.
            ("AutoRetainer suppression (final)", autoRetainerSuppression.RestoreOnUnload),
            ("AutoRetainer holder registry", () => { if (autoRetainerHolders is not null) pluginInterface.RelinquishData(AutoRetainerSuppression.HolderRegistryKey); }),
            ("windows", windows.RemoveAllWindows));
    }
}
