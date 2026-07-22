using RQ.AgentBridge;
using Franthropy.Dalamud.AgentBridge;
using System.Numerics;

namespace RQ.Tests;

public sealed class AgentBridgeTests
{
    [Fact]
    public void Provider_ExposesQuartermasterReviewSurfacesWithoutTransferActions()
    {
        var truth = new QuartermasterBridgeTruth(
            1, "provider", 42, "1.0", false, "Wei Ning @ Maduin", true,
            2, DateTimeOffset.UtcNow, 3, 2, "operation", "accepted", true, false, false);
        var openedTarget = string.Empty;
        var invoked = false;
        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register(
            "quartermaster.refresh-retainers",
            "Refresh retainers",
            AgentBridgeUiControlKind.Button,
            Vector2.Zero,
            Vector2.One,
            enabled: true,
            selected: false,
            "Ready",
            () => invoked = true);
        registry.EndFrame();
        var provider = new QuartermasterBridgeProvider(() => truth, target => openedTarget = target, () => { }, registry);

        Assert.Same(truth, provider.CreateTruth());
        Assert.Equal(["stock-and-plan", "listings", "operation"], provider.GetReviewSurfaces().Select(surface => surface.Id));
        Assert.All(provider.GetReviewSurfaces(), surface => Assert.Equal("open-main-window", surface.Command));
        Assert.True(provider.TryOpenMainWindow("listings"));
        Assert.Equal("listings", openedTarget);
        Assert.False(provider.TryOpenMainWindow("unknown"));
        Assert.Equal("listings", openedTarget);
        var review = provider.ReviewControl("quartermaster.refresh-retainers");
        Assert.NotNull(review.Control);
        Assert.True(provider.InvokeControl("quartermaster.refresh-retainers", review.FrameId).Success);
        Assert.True(invoked);
    }
}
