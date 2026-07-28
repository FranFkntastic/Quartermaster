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
            8, "provider", 42, "1.0", false, "transfer", string.Empty, 0,
            "mixed", false, false, 2, null, null, false, false,
            "Wei Ning @ Maduin", true,
            2, DateTimeOffset.UtcNow, 3, 2, 1, null, "General", 12, 4, false,
            "operation", "accepted", true, false, "Ready", false, false, string.Empty);
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
        Assert.Equal(["transfer", "transfer-review", "item-groups", "listings", "activity"], provider.GetReviewSurfaces().Select(surface => surface.Id));
        Assert.Equal(["transfer", "transfer-review", "item-groups", "listings", "activity"], provider.GetCaptureSurfaces().Select(surface => surface.Id));
        Assert.True(provider.GetCaptureSurfaces().Single(surface => surface.Id == "transfer").IsDefault);
        Assert.All(provider.GetReviewSurfaces(), surface => Assert.Equal("open-main-window", surface.Command));
        Assert.True(provider.TryOpenMainWindow("listings"));
        Assert.Equal("listings", openedTarget);
        Assert.True(provider.TryOpenMainWindow("stowage"));
        Assert.Equal("stowage", openedTarget);
        Assert.True(provider.TryOpenMainWindow("transfer"));
        Assert.Equal("transfer", openedTarget);
        Assert.True(provider.TryOpenMainWindow("transfer-review"));
        Assert.Equal("transfer-review", openedTarget);
        Assert.True(provider.TryOpenMainWindow("item-groups"));
        Assert.Equal("item-groups", openedTarget);
        Assert.True(provider.TryOpenMainWindow("stock-and-plan"));
        Assert.Equal("stock-and-plan", openedTarget);
        Assert.False(provider.TryOpenMainWindow("unknown"));
        Assert.Equal("stock-and-plan", openedTarget);
        var review = provider.ReviewControl("quartermaster.refresh-retainers");
        Assert.NotNull(review.Control);
        Assert.True(provider.InvokeControl("quartermaster.refresh-retainers", review.FrameId).Success);
        Assert.True(invoked);
    }
}
