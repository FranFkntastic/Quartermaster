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
    private readonly ElementalDepositSubmissionService deposits;
    private bool disposed, courierAttached, workbenchAttached, deliveredStowAttached, cancelAttached;

    public QuartermasterIpcProvider(
        IIpcRegistrar registrar,
        SnapshotPublisher snapshots,
        ShortageSubmissionService submissions,
        ElementalDepositSubmissionService deposits)
    {
        this.registrar = registrar;
        this.snapshots = snapshots;
        this.submissions = submissions;
        this.deposits = deposits;
        registrar.Register(IpcChannels.GetCapabilities, snapshots.GetCapabilities);
        registrar.Register(IpcChannels.GetSnapshot, snapshots.GetSnapshot);
        registrar.Register(IpcChannels.SubmitShortages, submissions.Submit);
        registrar.Register(IpcChannels.SubmitElementalDeposit, deposits.Submit);
        registrar.Register(IpcChannels.GetOperation, operationId =>
            submissions.GetPendingOperation(operationId) ??
            deposits.GetPendingOperation(operationId) ??
            snapshots.GetOperation(operationId));
        registrar.RegisterNotification(IpcChannels.Changed);
    }

    /// <summary>Registers the courier channels once the courier wiring exists.</summary>
    public void AttachCourier(CourierIpcService courier)
    {
        if (disposed || courierAttached)
            return;
        courierAttached = true;
        registrar.Register(IpcChannels.GetCourierStatus, courier.GetStatus);
        registrar.Register(IpcChannels.SubmitCourierTransfer, courier.Submit);
        registrar.Register(IpcChannels.StopCourierRun, courier.Stop);
        registrar.Register(IpcChannels.ResumeCourierRun, courier.Resume);
        registrar.Register(IpcChannels.PairCourierOwner, courier.PairOwner);
    }

    /// <summary>Registers the stock-source and reserve-item channels for external buy-list builders.</summary>
    public void AttachWorkbench(WorkbenchStockService workbench)
    {
        if (disposed || workbenchAttached)
            return;
        workbenchAttached = true;
        registrar.Register(IpcChannels.GetStockSources, workbench.GetStockSources);
        registrar.Register(IpcChannels.GetReserveItems, workbench.GetReserveItems);
        registrar.Register(IpcChannels.OpenPlacement, workbench.OpenPlacement);
        registrar.Register(IpcChannels.SetReserveLevel, workbench.SetReserveLevel);
        registrar.Register(IpcChannels.PlanArrival, workbench.PlanArrival);
        registrar.Register(PlacementIpcChannels.GetPlacement, workbench.GetPlacement);
        registrar.Register(PlacementIpcChannels.EditPlacement, workbench.EditPlacement);
    }

    /// <summary>Registers the delivered-stow channels (capability deliveredStow.v1).</summary>
    public void AttachDeliveredStow(DeliveredStowService deliveredStow)
    {
        if (disposed || deliveredStowAttached)
            return;
        deliveredStowAttached = true;
        registrar.Register(DeliveredStowIpcChannels.SubmitDeliveredStow, deliveredStow.Submit);
        registrar.Register(DeliveredStowIpcChannels.GetDeliveredStow, deliveredStow.Get);
    }

    /// <summary>Registers the requester cancel channel (capability requestedOperationCancel.v1).</summary>
    public void AttachRequestedOperationCancel(RequestedOperationCancelService cancel)
    {
        if (disposed || cancelAttached)
            return;
        cancelAttached = true;
        registrar.Register(RequestedOperationCancelService.Channel, cancel.Cancel);
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
        registrar.Unregister(IpcChannels.SubmitElementalDeposit);
        registrar.Unregister(IpcChannels.GetOperation);
        registrar.Unregister(IpcChannels.Changed);
        if (courierAttached)
        {
            registrar.Unregister(IpcChannels.GetCourierStatus);
            registrar.Unregister(IpcChannels.SubmitCourierTransfer);
            registrar.Unregister(IpcChannels.StopCourierRun);
            registrar.Unregister(IpcChannels.ResumeCourierRun);
            registrar.Unregister(IpcChannels.PairCourierOwner);
        }
        if (workbenchAttached)
        {
            registrar.Unregister(IpcChannels.GetStockSources);
            registrar.Unregister(IpcChannels.GetReserveItems);
            registrar.Unregister(IpcChannels.OpenPlacement);
            registrar.Unregister(IpcChannels.SetReserveLevel);
            registrar.Unregister(IpcChannels.PlanArrival);
            registrar.Unregister(PlacementIpcChannels.GetPlacement);
            registrar.Unregister(PlacementIpcChannels.EditPlacement);
        }
        if (cancelAttached)
            registrar.Unregister(RequestedOperationCancelService.Channel);
        if (deliveredStowAttached)
        {
            registrar.Unregister(DeliveredStowIpcChannels.SubmitDeliveredStow);
            registrar.Unregister(DeliveredStowIpcChannels.GetDeliveredStow);
        }
    }
}
