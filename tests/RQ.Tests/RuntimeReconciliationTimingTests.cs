using RQ.Runtime;

namespace RQ.Tests;

public sealed class RuntimeReconciliationTimingTests
{
    [Fact]
    public void Background_bursts_wait_but_decisions_and_progress_do_not_lose_pending_work()
    {
        var clock = new ManualClock();
        var queue = new RuntimeReconciliationQueue(clock);
        queue.Request(RuntimeDomain.PlayerInventory, "player_inventory");
        clock.Advance(400);
        queue.Request(RuntimeDomain.RetainerStock, "cache");
        Assert.False(queue.Drain(force: false).HasWork);
        queue.Request(RuntimeDomain.Operations, "operation", "run");
        var progress = queue.Drain(force: false);
        Assert.Equal(RuntimeDomain.Operations, progress.Domains);
        Assert.Single(progress.Notices);
        clock.Advance(499);
        Assert.False(queue.Drain(force: false).HasWork);
        clock.Advance(1);
        var quiet = queue.Drain(force: false);
        Assert.Equal(RuntimeDomain.PlayerInventory | RuntimeDomain.RetainerStock, quiet.Domains);
        Assert.Equal(2, quiet.RequestCount);
        Assert.Equal(TimeSpan.FromMilliseconds(900), quiet.Waited);

        queue.Request(RuntimeDomain.Plans, "state");
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(400);
            queue.Request(RuntimeDomain.Plans, "state");
            Assert.False(queue.Drain(force: false).HasWork);
        }
        clock.Advance(400);
        queue.Request(RuntimeDomain.Plans, "state");
        Assert.Equal(6, queue.Drain(force: false).RequestCount);

        foreach (var kind in new[] { "opened", "owner_transition" })
        {
            queue.Request(RuntimeDomain.All, kind);
            Assert.Equal(RuntimeDomain.All, queue.Drain(force: false).Domains);
        }
        queue.Request(RuntimeDomain.Plans, "state", interactive: true);
        Assert.Equal(RuntimeDomain.Plans, queue.Drain(force: false).Domains);

        // Force bypasses time, never the caller's transfer-running domain hold.
        queue.Request(RuntimeDomain.All, "periodic");
        queue.Request(RuntimeDomain.Plans, "state");
        var allowed = RuntimeDomain.Listings | RuntimeDomain.Operations;
        Assert.Equal(allowed, queue.Drain(allowed).Domains);
        Assert.True(queue.HasPending(RuntimeDomain.Plans));
        Assert.False(queue.Drain(allowed, force: false).HasWork);
        var decision = queue.Drain();
        Assert.Equal(RuntimeDomain.All & ~allowed, decision.Domains);
        Assert.Equal(2, decision.Notices.Count);
        Assert.False(queue.HasPending());
        Assert.False(queue.Drain().HasWork);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public void Advance(int amount) => milliseconds += amount;
    }
}
