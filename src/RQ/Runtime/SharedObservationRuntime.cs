using Franthropy.Dalamud.Observations;
using Franthropy.Observations.V1;

namespace RQ.Runtime;

internal sealed record SharedObservationRuntime(
    DalamudSharedObservationHost? Host,
    ObservationCaptureSessionRegistry CaptureSessions)
{
    public static SharedObservationRuntime Create(
        ObservationCaptureSessionRegistry fallbackCaptureSessions,
        Func<DalamudSharedObservationHost> createHost,
        Action<Exception> hostUnavailable)
    {
        ArgumentNullException.ThrowIfNull(fallbackCaptureSessions);
        ArgumentNullException.ThrowIfNull(createHost);
        ArgumentNullException.ThrowIfNull(hostUnavailable);
        try
        {
            var host = createHost();
            return new SharedObservationRuntime(host, host.CaptureSessions);
        }
        catch (Exception exception)
        {
            hostUnavailable(exception);
            return new SharedObservationRuntime(null, fallbackCaptureSessions);
        }
    }
}
