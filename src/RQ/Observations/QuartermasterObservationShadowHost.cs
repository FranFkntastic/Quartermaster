using System.Threading.Channels;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Hosting;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Observations;

internal sealed class QuartermasterObservationShadowHost : IDisposable
{
    private readonly object gate = new();
    private readonly ObservationCollectorCoordinator coordinator;
    private readonly SharedObservationPaths paths;
    private readonly Action<string, Exception?> diagnostic;
    private readonly ObservationProvenance provenance;
    private WriterSession? writer;
    private long sourceRevision;
    private long enqueued;
    private long queueFull;
    private long acceptedChanged;
    private long acceptedConfirmed;
    private long preservedStale;
    private long rejected;
    private long ignored;
    private long writerFaults;
    private long divergenceFaults;
    private string? lastOutcome;
    private long lastOutcomeUtcTicks;
    private bool started;
    private bool disposed;

    public QuartermasterObservationShadowHost(
        string pluginConfigDirectory,
        string pluginInstanceId,
        string gameBuild,
        Action<string, Exception?> diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        if (string.Equals(gameBuild, "unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exact game build is unavailable; shared observation shadowing is blocked.");
        GamePatchCompatibilityGate.Require(
            "Quartermaster.SharedObservationShadow.V1",
            DalamudSharedObservationHost.ApprovedGameBuild,
            gameBuild);
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        var version = typeof(ObservationContract).Assembly.GetName().Version ?? new Version(0, 0);
        provenance = new ObservationProvenance("Quartermaster", pluginInstanceId, version.ToString(), gameBuild);
        paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfigDirectory);
        var storeOptions = CreateStoreOptions();
        coordinator = new ObservationCollectorCoordinator(new ObservationCollectorCoordinatorOptions
        {
            ProfileId = paths.ProfileId,
            CandidatesDirectory = paths.CandidatesDirectory,
            PluginName = "Quartermaster",
            PluginInstanceId = pluginInstanceId,
            FranthropyVersion = version,
            WriterCapability = 1,
            DatabaseProbe = () => ObservationDatabaseProbe.ReadAsync(storeOptions).AsTask().GetAwaiter().GetResult(),
            StartCollector = StartWriter,
            StopCollector = StopWriter,
        });
        coordinator.LeadershipChanged += OnLeadershipChanged;
    }

    public event Action? CollectorActivated;
    public ObservationLeadershipSnapshot Leadership => coordinator.State;
    public QuartermasterObservationShadowDiagnostics Diagnostics => new(
        Leadership,
        Interlocked.Read(ref enqueued),
        Interlocked.Read(ref queueFull),
        Interlocked.Read(ref acceptedChanged),
        Interlocked.Read(ref acceptedConfirmed),
        Interlocked.Read(ref preservedStale),
        Interlocked.Read(ref rejected),
        Interlocked.Read(ref ignored),
        Interlocked.Read(ref writerFaults),
        Interlocked.Read(ref divergenceFaults),
        lastOutcome,
        Interlocked.Read(ref lastOutcomeUtcTicks) == 0
            ? null
            : new DateTimeOffset(Interlocked.Read(ref lastOutcomeUtcTicks), TimeSpan.Zero));

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        coordinator.Start();
        started = true;
    }

    public void ObservePlayerInventory(OwnerScope owner, PlayerStorageCapture capture, DateTime observedAtUtc) =>
        Enqueue(revision => QuartermasterObservationMapper.PlayerInventory(owner, capture, revision, observedAtUtc, provenance));

    public void ObserveRetainerInventory(CachedRetainer retainer) =>
        Enqueue(revision => QuartermasterObservationMapper.RetainerInventory(retainer, revision, provenance));

    public void ObserveRetainerListings(CachedRetainer retainer, DateTime observedAtUtc) =>
        Enqueue(revision => QuartermasterObservationMapper.RetainerListings(retainer, observedAtUtc, revision, provenance));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        coordinator.LeadershipChanged -= OnLeadershipChanged;
        coordinator.Dispose();
    }

    private void Enqueue(Func<long, ObservationEnvelope> create)
    {
        WriterSession? current;
        lock (gate)
            current = writer;
        if (current is null)
            return;

        ObservationEnvelope observation;
        try
        {
            observation = create(Interlocked.Increment(ref sourceRevision));
        }
        catch (Exception ex)
        {
            RecordOutcome("Rejected invalid shadow input.");
            Interlocked.Increment(ref rejected);
            diagnostic("Quartermaster rejected an invalid shared-observation shadow input.", ex);
            return;
        }

        if (!current.Channel.Writer.TryWrite(observation))
        {
            Interlocked.Increment(ref queueFull);
            const string message = "Quartermaster shared-observation shadow queue is full; collection stopped before evidence could be dropped silently.";
            RecordOutcome(message);
            diagnostic(message, null);
            coordinator.ReportCollectorFault(message);
        }
        else
        {
            Interlocked.Increment(ref enqueued);
        }
    }

    private void StartWriter()
    {
        var result = SqliteObservationStore.OpenAsync(CreateStoreOptions()).AsTask().GetAwaiter().GetResult();
        if (!result.IsReady)
            throw new InvalidOperationException(result.Message);

        var channel = Channel.CreateBounded<ObservationEnvelope>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var session = new WriterSession(result.Store!, channel);
        session.Worker = Task.Run(() => ProcessAsync(session));
        lock (gate)
            writer = session;
        PublishCollectorActivated();
    }

    private void StopWriter()
    {
        WriterSession? current;
        lock (gate)
        {
            current = writer;
            writer = null;
        }
        if (current is null)
            return;
        current.Channel.Writer.TryComplete();
        current.Worker.GetAwaiter().GetResult();
        current.Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ProcessAsync(WriterSession session)
    {
        try
        {
            await foreach (var observation in session.Channel.Reader.ReadAllAsync())
            {
                var result = await session.Store.WriteAsync(observation).ConfigureAwait(false);
                RecordWriteResult(result);
                if (result.Status is ObservationWriteStatus.Busy or ObservationWriteStatus.Unavailable or ObservationWriteStatus.UnsupportedDatabaseVersion)
                {
                    Interlocked.Increment(ref writerFaults);
                    diagnostic($"Quartermaster shared-observation shadow write failed: {result.Message}", null);
                    coordinator.ReportCollectorFault(result.Message);
                    return;
                }
                if (result.Status == ObservationWriteStatus.Rejected)
                    diagnostic($"Quartermaster shared-observation shadow evidence was rejected: {result.Message}", null);
                if (result.Status is ObservationWriteStatus.AcceptedChanged or ObservationWriteStatus.AcceptedConfirmed)
                {
                    var read = await session.Store.ReadCurrentAsync(observation.Scope).ConfigureAwait(false);
                    if (read.Status != ObservationReadStatus.Found ||
                        read.Observation is null ||
                        observation.Payload is null ||
                        read.Observation.Payload.Contract != observation.Payload.Contract ||
                        read.Observation.Payload.Version != observation.Payload.Version ||
                        read.Observation.Payload.Json != observation.Payload.Json)
                    {
                        const string message = "Quartermaster shared-observation shadow projection diverged from the legacy capture.";
                        Interlocked.Increment(ref divergenceFaults);
                        RecordOutcome(message);
                        diagnostic(message, null);
                        coordinator.ReportCollectorFault(message);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var message = $"Quartermaster shared-observation shadow writer stopped unexpectedly: {ex.Message}";
            Interlocked.Increment(ref writerFaults);
            RecordOutcome(message);
            diagnostic(message, ex);
            coordinator.ReportCollectorFault(message);
        }
    }

    private ObservationStoreOptions CreateStoreOptions() => new()
    {
        DatabasePath = paths.DatabasePath,
        BackupDirectory = paths.BackupsDirectory,
        MigrationLockPath = paths.MigrationLockPath,
        ChangeSignalPath = paths.ChangeSignalPath,
        WriterCapability = 1,
    };

    private void RecordWriteResult(ObservationWriteResult result)
    {
        switch (result.Status)
        {
            case ObservationWriteStatus.AcceptedChanged:
                Interlocked.Increment(ref acceptedChanged);
                break;
            case ObservationWriteStatus.AcceptedConfirmed:
                Interlocked.Increment(ref acceptedConfirmed);
                break;
            case ObservationWriteStatus.PreservedAsStale:
                Interlocked.Increment(ref preservedStale);
                break;
            case ObservationWriteStatus.Rejected:
                Interlocked.Increment(ref rejected);
                break;
            case ObservationWriteStatus.IgnoredOlderRevision:
            case ObservationWriteStatus.IgnoredRepeatedRevision:
                Interlocked.Increment(ref ignored);
                break;
        }
        RecordOutcome($"{result.Status}: {result.Message}");
    }

    private void RecordOutcome(string outcome)
    {
        lastOutcome = outcome.Length <= 512 ? outcome : outcome[..512];
        Interlocked.Exchange(ref lastOutcomeUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void PublishCollectorActivated()
    {
        var subscribers = CollectorActivated;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                diagnostic("Quartermaster shared-observation activation subscriber failed.", ex);
            }
        }
    }

    private void OnLeadershipChanged(object? sender, ObservationLeadershipSnapshot snapshot)
    {
        if (snapshot.State is ObservationLeadershipState.Faulted or ObservationLeadershipState.Incompatible)
            diagnostic($"Quartermaster shared-observation host: {snapshot.Message}", null);
    }

    private sealed class WriterSession(
        SqliteObservationStore store,
        Channel<ObservationEnvelope> channel)
    {
        public SqliteObservationStore Store { get; } = store;
        public Channel<ObservationEnvelope> Channel { get; } = channel;
        public Task Worker { get; set; } = Task.CompletedTask;
    }
}

internal sealed record QuartermasterObservationShadowDiagnostics(
    ObservationLeadershipSnapshot Leadership,
    long Enqueued,
    long QueueFull,
    long AcceptedChanged,
    long AcceptedConfirmed,
    long PreservedStale,
    long Rejected,
    long Ignored,
    long WriterFaults,
    long DivergenceFaults,
    string? LastOutcome,
    DateTimeOffset? LastOutcomeAtUtc);
