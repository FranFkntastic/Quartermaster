using System.Threading.Channels;
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
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        var version = typeof(ObservationContract).Assembly.GetName().Version ?? new Version(0, 0);
        provenance = new ObservationProvenance("Quartermaster", pluginInstanceId, version.ToString(), gameBuild);
        paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfigDirectory);
        coordinator = new ObservationCollectorCoordinator(new ObservationCollectorCoordinatorOptions
        {
            ProfileId = paths.ProfileId,
            CandidatesDirectory = paths.CandidatesDirectory,
            PluginName = "Quartermaster",
            PluginInstanceId = pluginInstanceId,
            FranthropyVersion = version,
            WriterCapability = 1,
            StartCollector = StartWriter,
            StopCollector = StopWriter,
        });
        coordinator.LeadershipChanged += OnLeadershipChanged;
    }

    public event Action? CollectorActivated;
    public ObservationLeadershipSnapshot Leadership => coordinator.State;

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
            diagnostic("Quartermaster rejected an invalid shared-observation shadow input.", ex);
            return;
        }

        if (!current.Channel.Writer.TryWrite(observation))
            diagnostic("Quartermaster shared-observation shadow queue is full; the observation was not written.", null);
    }

    private void StartWriter()
    {
        var result = SqliteObservationStore.OpenAsync(new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
        }).AsTask().GetAwaiter().GetResult();
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
        await foreach (var observation in session.Channel.Reader.ReadAllAsync())
        {
            var result = await session.Store.WriteAsync(observation).ConfigureAwait(false);
            if (result.Status is ObservationWriteStatus.Busy or ObservationWriteStatus.Unavailable or ObservationWriteStatus.UnsupportedDatabaseVersion)
            {
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
                    diagnostic(message, null);
                    coordinator.ReportCollectorFault(message);
                    return;
                }
            }
        }
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
