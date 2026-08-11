using System.Threading.Channels;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using RQ.Domain;

namespace RQ.Observations;

internal sealed record QuartermasterObservationDelivery(
    ObservationOwner Owner,
    long Revision,
    IReadOnlyList<TrustedObservation> PlayerBaselines,
    IReadOnlyList<InventoryChangeBatch> PlayerChanges,
    IReadOnlyList<TrustedObservation> RetainerObservations);

internal sealed class QuartermasterObservationConsumer : IAsyncDisposable
{
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<QuartermasterObservationDelivery, CancellationToken, ValueTask> deliver;
    private readonly Action<string, Exception?> diagnostic;
    private readonly ObservationStoreOptions options;
    private SqliteObservationReader? reader;
    private readonly ObservationDatabaseChangeMonitor monitor;
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource lifetime = new();
    private Task worker = Task.CompletedTask;
    private ObservationOwner? owner;
    private long revision;
    private readonly Dictionary<string, long> retainerRevisions = new(StringComparer.Ordinal);
    private TimeSpan retryDelay = MinimumRetryDelay;
    private bool started;
    private bool disposed;

    public QuartermasterObservationConsumer(
        string pluginConfigDirectory,
        Func<OwnerScope> currentOwner,
        Func<QuartermasterObservationDelivery, CancellationToken, ValueTask> deliver,
        Action<string, Exception?> diagnostic)
    {
        this.currentOwner = currentOwner ?? throw new ArgumentNullException(nameof(currentOwner));
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfigDirectory);
        options = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
            WriterCapability = 2,
        };
        monitor = new ObservationDatabaseChangeMonitor(options);
        monitor.Changed += OnDatabaseChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        started = true;
        worker = Task.Run(ProcessAsync);
        monitor.StartAsync(lifetime.Token).AsTask().GetAwaiter().GetResult();
        Signal();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        monitor.Changed -= OnDatabaseChanged;
        lifetime.Cancel();
        signals.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        await monitor.DisposeAsync().ConfigureAwait(false);
        if (reader is not null)
            await reader.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private void OnDatabaseChanged(object? sender, ObservationDatabaseChanged change) => Signal();

    private void Signal() => signals.Writer.TryWrite(true);

    private async Task ProcessAsync()
    {
        await foreach (var signal in signals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            _ = signal;
            while (signals.Reader.TryRead(out var ignored)) { _ = ignored; }
            try
            {
                var succeeded = await ConsumeAsync(lifetime.Token).ConfigureAwait(false);
                if (succeeded)
                {
                    retryDelay = MinimumRetryDelay;
                    continue;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                diagnostic("Quartermaster could not consume shared observations.", exception);
            }

            try
            {
                await Task.Delay(retryDelay, lifetime.Token).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(MaximumRetryDelay.TotalMilliseconds, retryDelay.TotalMilliseconds * 2));
                Signal();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> ConsumeAsync(CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            var open = await SqliteObservationReader.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            if (!open.IsReady)
                return open.Status == ObservationStoreOpenStatus.Missing;
            reader = open.Reader!;
        }
        var current = currentOwner();
        if (!current.HasStableIdentity)
            return true;
        var nextOwner = new ObservationOwner(current.LocalContentId!.Value, current.HomeWorldId!.Value);
        if (owner != nextOwner)
        {
            owner = nextOwner;
            revision = 0;
            retainerRevisions.Clear();
        }

        var changes = await reader.ReadInventoryChangesAsync(nextOwner, revision, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TrustedObservation> playerBaselines = [];
        IReadOnlyList<InventoryChangeBatch> playerChanges = [];
        switch (changes.Status)
        {
            case InventoryChangeReadStatus.SnapshotRequired:
            {
                var player = await reader.ReadCurrentByOwnerAsync(nextOwner, ObservationContainerKind.PlayerInventory, cancellationToken).ConfigureAwait(false);
                var saddlebag = await reader.ReadCurrentByOwnerAsync(nextOwner, ObservationContainerKind.Saddlebag, cancellationToken).ConfigureAwait(false);
                if (player.Status is ObservationReadStatus.Busy or ObservationReadStatus.Unavailable ||
                    saddlebag.Status is ObservationReadStatus.Busy or ObservationReadStatus.Unavailable)
                    return false;
                if (player.Observations.Any(observation => observation.IsStale) ||
                    saddlebag.Observations.Any(observation => observation.IsStale))
                {
                    diagnostic("Quartermaster is preserving its last truthful inventory projection while the shared collector obtains a fresh complete baseline.", null);
                    return false;
                }
                playerBaselines = [.. player.Observations, .. saddlebag.Observations];
                break;
            }
            case InventoryChangeReadStatus.Found:
                playerChanges = changes.Batches;
                break;
            case InventoryChangeReadStatus.NoChanges:
            case InventoryChangeReadStatus.NotObserved:
                break;
            case InventoryChangeReadStatus.Busy:
            case InventoryChangeReadStatus.Unavailable:
                diagnostic($"Quartermaster shared inventory read will retry: {changes.Message}", null);
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(changes.Status), changes.Status, null);
        }

        var retainerObservations = new List<TrustedObservation>();
        foreach (var container in new[]
                 {
                     ObservationContainerKind.RetainerRoster,
                     ObservationContainerKind.RetainerInventory,
                     ObservationContainerKind.RetainerGil,
                     ObservationContainerKind.RetainerMarketListings,
                 })
        {
            var read = await reader.ReadCurrentByOwnerAsync(nextOwner, container, cancellationToken).ConfigureAwait(false);
            if (read.Status is ObservationReadStatus.Busy or ObservationReadStatus.Unavailable)
            {
                diagnostic($"Quartermaster shared retainer read will retry: {read.Message}", null);
                return false;
            }
            foreach (var observation in read.Observations.Where(observation => !observation.IsStale))
            {
                var key = $"{observation.Scope.Container}:{observation.Scope.Subject.Kind}:{observation.Scope.Subject.Id}";
                if (observation.Revision > retainerRevisions.GetValueOrDefault(key))
                    retainerObservations.Add(observation);
            }
        }

        if (playerBaselines.Count > 0 || playerChanges.Count > 0 || retainerObservations.Count > 0)
            await deliver(
                new(nextOwner, changes.CurrentRevision, playerBaselines, playerChanges, retainerObservations),
                cancellationToken).ConfigureAwait(false);

        revision = changes.CurrentRevision;
        foreach (var observation in retainerObservations)
        {
            var key = $"{observation.Scope.Container}:{observation.Scope.Subject.Kind}:{observation.Scope.Subject.Id}";
            retainerRevisions[key] = observation.Revision;
        }
        return true;
    }
}
