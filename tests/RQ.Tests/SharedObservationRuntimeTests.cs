using Franthropy.Observations.V1;
using RQ.Runtime;

namespace RQ.Tests;

public sealed class SharedObservationRuntimeTests
{
    [Fact]
    public void PatchBlockedHostFallsBackWithoutAbortingQuartermaster()
    {
        var fallback = new ObservationCaptureSessionRegistry();
        Exception? reported = null;

        var runtime = SharedObservationRuntime.Create(
            fallback,
            () => throw new InvalidOperationException("exact game build is not approved"),
            exception => reported = exception);

        Assert.Null(runtime.Host);
        Assert.Same(fallback, runtime.CaptureSessions);
        Assert.Equal("exact game build is not approved", reported?.Message);
    }
}
