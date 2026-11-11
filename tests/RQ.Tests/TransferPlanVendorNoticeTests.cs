using Franthropy.Dalamud.Automation.Vendors.Coordination;
using RQ.UI;

namespace RQ.Tests;

public sealed class TransferPlanVendorNoticeTests
{
    [Fact]
    public void CompletedNoticeSuppressesOnlyTheRedundantMessage()
    {
        Assert.False(TransferPlanWorkspace.ShowVendorMessage(GilVendorBuyPhase.Completed, "Vendor buy completed."));
        Assert.True(TransferPlanWorkspace.ShowVendorMessage(GilVendorBuyPhase.Completed, "Completed with partial results."));
        Assert.True(TransferPlanWorkspace.ShowVendorMessage(GilVendorBuyPhase.Failed, "Vendor buy completed."));
        Assert.True(TransferPlanWorkspace.ShowVendorMessage(GilVendorBuyPhase.Paused, "Waiting for confirmation."));
        Assert.False(TransferPlanWorkspace.ShowVendorMessage(GilVendorBuyPhase.Completed, null));
    }
}
