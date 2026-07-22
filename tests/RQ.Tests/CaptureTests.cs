using RQ.Inventory;

namespace RQ.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void PublishSubscribersSafely_ContinuesAfterThrowingSubscriberAndContainsDiagnosticFailure()
    {
        var received = 0;
        var diagnosticAttempts = 0;
        Action<CaptureReceipt> throwing = _ => throw new InvalidOperationException("subscriber failure");
        Action<CaptureReceipt> succeeding = _ => received++;
        var receipt = new CaptureReceipt(
            1,
            100,
            CaptureOutcome.Persisted,
            "Captured.",
            DateTime.UtcNow);

        var exception = Record.Exception(() => RetainerCaptureService.PublishSubscribersSafely(
            throwing + succeeding,
            receipt,
            _ =>
            {
                diagnosticAttempts++;
                throw new InvalidOperationException("diagnostic failure");
            }));

        Assert.Null(exception);
        Assert.Equal(1, diagnosticAttempts);
        Assert.Equal(1, received);
    }
}
