using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.AgentBridge;
using RQ.Automation;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.UI;

namespace RQ;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/rq";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly PluginConfiguration configuration;
    private readonly string providerInstanceId = Guid.NewGuid().ToString("N");
    private readonly FrameworkWorkQueue workQueue;
    private readonly InventoryScanner scanner;
    private readonly RetainerCacheRepository cache;
    private readonly StateRepository state;
    private readonly RetainerCaptureService captures;
    private readonly AutoRetainerRefreshService autoRetainer;
    private readonly OperationJournal journal;
    private readonly TransferCoordinator transfers;
    private readonly AutomaticRetrievalQueue automaticRetrievals;
    private readonly SnapshotPublisher snapshots;
    private readonly ShortageSubmissionService submissions;
    private readonly QuartermasterIpcProvider ipc;
    private readonly AgentBridgeHost agentBridge;
    private readonly AgentBridgeUiReviewRegistry agentReviewRegistry = new();
    private readonly WindowSystem windows = new("RQ");
    private readonly QuartermasterWindow window;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Kind, string? OperationId)> pendingChanges = new();
    private DateTime nextSnapshotAt;
    private int snapshotDirty;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IPlayerState playerState,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
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
        cache = new(new RetainerCacheStore(cachePath));
        state = new(new QuartermasterStateStore(statePath));
        captures = new(addonLifecycle, log, scanner, cache, CurrentOwner);
        var automation = new AutomationLease();
        var retainerSession = new DalamudRetainerAutomationSession(framework, gameGui, dataManager, log, objects, targets, sigScanner);
        autoRetainer = new(pluginInterface, framework, log, captures, retainerSession, automation);
        journal = new OperationJournal(state);
        RecoverPendingCacheInvalidations();
        foreach (var operation in state.Snapshot().Operations.Where(operation => operation.Status == OperationStatuses.Running))
            InvalidateOwnerEvidence(operation);
        journal.ReconcileInterruptedOperations();
        var driver = new RetainerLiveDriver(retainerSession);
        transfers = new TransferCoordinator(
            journal,
            driver,
            cache,
            CurrentOwner,
            scanner.CountPlayerItems,
            automation,
            clearRetrievalPlansAsActioned: () => configuration.ClearRetrievalPlansAsActioned);
        automaticRetrievals = new(journal, transfers, CurrentOwner);
        workQueue = new();
        snapshots = new(providerInstanceId, state, cache.Snapshot);
        submissions = new ShortageSubmissionService(providerInstanceId, state, workQueue, CurrentOwner);
        ipc = new(new DalamudIpcRegistrar(pluginInterface), snapshots, submissions);
        window = new(state, cache, scanner, journal, transfers, autoRetainer, dataManager, CurrentOwner, configuration, SaveConfiguration, agentReviewRegistry);
        agentBridge = new(
            configuration,
            configDirectory,
            SaveConfiguration,
            DispatchOnFramework,
            new QuartermasterBridgeProvider(CreateAgentBridgeTruth, window.OpenReviewSurface, () => window.IsOpen = false, agentReviewRegistry));
        windows.AddWindow(window);

        try
        {
            captures.Register();
            autoRetainer.Register();
            state.Changed += OnStateChanged;
            cache.Changed += OnCacheChanged;
            journal.OperationChanged += OnOperationChanged;
            submissions.OperationChanged += OnSubmittedOperationChanged;
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

    private void RecoverPendingCacheInvalidations()
    {
        foreach (var pending in journal.PendingCacheInvalidations())
        {
            var result = cache.Invalidate(pending.RetainerId);
            if (result.Persisted)
                journal.ResolveCacheInvalidation(pending.OperationId, pending.RetainerId);
        }
    }

    private void InvalidateOwnerEvidence(OperationRecord operation)
    {
        foreach (var retainer in cache.Snapshot().Values.Where(retainer => retainer.Owner.Matches(operation.Owner)).ToArray())
        {
            journal.ArmCacheInvalidation(operation.OperationId, retainer.RetainerId, operation.Owner);
            var result = cache.Invalidate(retainer.RetainerId);
            if (result.Persisted)
                journal.ResolveCacheInvalidation(operation.OperationId, retainer.RetainerId);
        }
    }

    private OwnerScope CurrentOwner() => new()
    {
        LocalContentId = playerState.ContentId == 0 ? null : playerState.ContentId,
        HomeWorldId = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
        CharacterName = playerState.CharacterName ?? string.Empty,
        HomeWorldName = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty,
    };

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
        workQueue.Drain();
        automaticRetrievals.Tick();
        agentBridge.Tick();
        if (Interlocked.Exchange(ref snapshotDirty, 0) != 0 || DateTime.UtcNow >= nextSnapshotAt)
            RefreshSnapshot();
        while (pendingChanges.TryDequeue(out var change))
            ipc.PublishChanged(snapshots.CreateChanged(change.Kind, change.OperationId, CurrentOwner()));
    }

    private void RefreshSnapshot()
    {
        snapshots.Refresh(CurrentOwner(), scanner.CapturePlayerStorage());
        nextSnapshotAt = DateTime.UtcNow.AddSeconds(1);
    }

    private void OnStateChanged()
    {
        MarkChanged("state", null);
    }

    private void OnCacheChanged()
    {
        MarkChanged("cache", null);
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

    private void OpenMainUi() => window.IsOpen = true;

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
        var owner = CurrentOwner();
        var retainers = cache.Snapshot().Values.Where(retainer => owner.Matches(retainer.Owner)).ToArray();
        var stateSnapshot = state.Snapshot();
        var operation = stateSnapshot.Operations
            .Where(candidate => candidate.Owner.Matches(owner))
            .OrderByDescending(candidate => candidate.UpdatedAtUtc)
            .FirstOrDefault();
        return new QuartermasterBridgeTruth(
            1,
            configuration.PluginInstanceId,
            Environment.ProcessId,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            window.IsOpen,
            owner.HasStableIdentity ? $"{owner.CharacterName} @ {owner.HomeWorldName}" : "Unavailable",
            owner.HasStableIdentity,
            retainers.Length,
            retainers.Length == 0 ? null : retainers.Min(retainer => new DateTimeOffset(DateTime.SpecifyKind(retainer.ObservedAtUtc, DateTimeKind.Utc))),
            stateSnapshot.PlanItems.Count,
            stateSnapshot.PlanItems.Count(item => item.Enabled),
            operation?.OperationId,
            operation?.Status,
            autoRetainer.IsAvailable,
            autoRetainer.IsRefreshing || autoRetainer.IsQueued,
            transfers.IsRunning);
    }

    public void Dispose()
    {
        automaticRetrievals.Dispose();
        window.CancelAndWaitForActiveTransfer(TimeSpan.FromSeconds(2));
        agentBridge.Dispose();
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commands.RemoveHandler(Command);
        submissions.OperationChanged -= OnSubmittedOperationChanged;
        state.Changed -= OnStateChanged;
        cache.Changed -= OnCacheChanged;
        journal.OperationChanged -= OnOperationChanged;
        ipc.Dispose();
        autoRetainer.Dispose();
        captures.Dispose();
        windows.RemoveAllWindows();
    }
}
