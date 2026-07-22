namespace RQ.Interop;

public interface IIpcRegistrar
{
    void Register(string channel, Func<string> callback);
    void Register(string channel, Func<string, string> callback);
    void RegisterNotification(string channel);
    void SendNotification(string channel, string json);
    void Unregister(string channel);
}

public sealed class QuartermasterIpcProvider : IDisposable
{
    private readonly IIpcRegistrar registrar;
    private readonly SnapshotPublisher snapshots;
    private readonly ShortageSubmissionService submissions;
    private bool disposed;

    public QuartermasterIpcProvider(IIpcRegistrar registrar, SnapshotPublisher snapshots, ShortageSubmissionService submissions)
    {
        this.registrar = registrar;
        this.snapshots = snapshots;
        this.submissions = submissions;
        registrar.Register(IpcChannels.GetCapabilities, snapshots.GetCapabilities);
        registrar.Register(IpcChannels.GetSnapshot, snapshots.GetSnapshot);
        registrar.Register(IpcChannels.SubmitShortages, submissions.Submit);
        registrar.Register(IpcChannels.GetOperation, operationId => submissions.GetPendingOperation(operationId) ?? snapshots.GetOperation(operationId));
        registrar.RegisterNotification(IpcChannels.Changed);
    }

    public void PublishChanged(string json)
    {
        if (!disposed)
            registrar.SendNotification(IpcChannels.Changed, json);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        registrar.Unregister(IpcChannels.GetCapabilities);
        registrar.Unregister(IpcChannels.GetSnapshot);
        registrar.Unregister(IpcChannels.SubmitShortages);
        registrar.Unregister(IpcChannels.GetOperation);
        registrar.Unregister(IpcChannels.Changed);
    }
}
