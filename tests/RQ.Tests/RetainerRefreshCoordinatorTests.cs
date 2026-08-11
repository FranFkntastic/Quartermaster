using System.Reflection;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class RetainerRefreshCoordinatorTests
{
    [Fact]
    public void Construction_DoesNotRegisterAutoRetainerCallbacks()
    {
        using var directory = new TemporaryDirectory();
        var ipc = new FakeAutoRetainerIpc();
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            CreateProxy<IRetainerAutomationSession>((method, _) => throw new InvalidOperationException(method.Name)),
            ipc);

        Assert.Null(ipc.Callbacks);
        coordinator.Dispose();
        Assert.Equal(0, ipc.FinishCalls);
    }

    [Fact]
    public void RosterDiscovery_IsNativeAndDoesNotOpenRetainerUi()
    {
        using var directory = new TemporaryDirectory();
        var roster = Enumerable.Range(1, 9)
            .Select(index => new RetainerRosterEntry(
                (ulong)index,
                $"Retainer {index}",
                index - 1,
                null,
                index == 9 ? (byte)0 : (byte)1,
                index == 9 ? (byte)0 : (byte)90,
                0,
                index != 9))
            .ToArray();
        var refreshCalls = 0;
        var unexpectedUiCalls = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) =>
        {
            if (method.Name == nameof(IRetainerAutomationSession.ScanRetainerRosterAsync))
                return Task.FromResult((++refreshCalls, RetainerRosterResult.Succeeded(roster)).Item2);
            unexpectedUiCalls++;
            throw new InvalidOperationException(method.Name);
        });
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            session,
            new FakeAutoRetainerIpc());

        coordinator.TickRosterDiscovery(stockBrowserVisible: true);

        Assert.True(SpinWait.SpinUntil(() => refreshCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, unexpectedUiCalls);
        var cache = new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")).Load();
        Assert.Equal(9, cache.Count);
        Assert.Null(cache[9].IsUiAccessible);
        Assert.False(cache[9].IsGameAvailable);
        Assert.Equal((byte)0, cache[9].ClassJobId);
    }

    [Theory]
    [InlineData("RetainerRosterNotReady")]
    [InlineData("RetainerManagerUnavailable")]
    public void RosterDiscovery_QuietlyRetriesExpectedNotReadyEvidence(string code)
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var scans = 0;
        var warnings = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.ScanRetainerRosterAsync) => Task.FromResult(
                ++scans == 1
                    ? RetainerRosterResult.Failed(code, "The assigned retainer roster is not ready.")
                    : RetainerRosterResult.Succeeded([new(1, "Retainer 1", 0, null, 1, 90, 0, true)])),
            _ => throw new InvalidOperationException(method.Name),
        });
        var log = CreateProxy<IPluginLog>((method, _) =>
        {
            if (method.Name.Contains("Warning", StringComparison.Ordinal))
                warnings++;
            return null;
        });
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            session,
            new FakeAutoRetainerIpc(),
            log,
            () => now);

        coordinator.TickRosterDiscovery(stockBrowserVisible: true);
        Assert.True(SpinWait.SpinUntil(() => scans == 1, TimeSpan.FromSeconds(2)));
        Assert.Empty(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")).Load());
        Assert.Equal(0, warnings);

        now = now.AddSeconds(5);
        coordinator.TickRosterDiscovery(stockBrowserVisible: true);
        Assert.True(SpinWait.SpinUntil(() => scans == 2, TimeSpan.FromSeconds(2)));
        Assert.Single(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")).Load());
        Assert.Equal(0, warnings);
    }

    [Fact]
    public void Start_DoesNotRequireAutoRetainerPresence()
    {
        using var directory = new TemporaryDirectory();
        var rosterCalls = 0;
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.ScanRetainerRosterAsync) => Task.FromResult(
                (++rosterCalls, RetainerRosterResult.Failed("TestStop", "Stopped after native entry.")).Item2),
            nameof(IRetainerAutomationSession.CancelActive) => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            session,
            new FakeAutoRetainerIpc { IsAvailable = false });

        Assert.True(coordinator.Start());
        Assert.True(SpinWait.SpinUntil(() => rosterCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => coordinator.LastCompletedRunId is not null, TimeSpan.FromSeconds(2)));
        Assert.True(coordinator.HasRecovery);

        coordinator.DismissRecovery();

        Assert.False(coordinator.HasRecovery);
    }

    [Fact]
    public void Active_refresh_is_not_persisted_until_it_reaches_a_retryable_terminal_failure()
    {
        using var directory = new TemporaryDirectory();
        var scan = new TaskCompletionSource<RetainerRosterResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.ScanRetainerRosterAsync) => scan.Task,
            nameof(IRetainerAutomationSession.CancelActive) => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            session,
            new FakeAutoRetainerIpc { IsAvailable = false });

        Assert.True(coordinator.Start());
        Assert.False(coordinator.HasRecovery);

        scan.SetResult(RetainerRosterResult.Failed("TestStop", "Terminal test failure."));
        Assert.True(SpinWait.SpinUntil(() => coordinator.LastCompletedRunId is not null, TimeSpan.FromSeconds(2)));
        Assert.True(coordinator.HasRecovery);
    }

    [Fact]
    public void Plan_refresh_completion_is_exactly_identified_and_never_creates_standalone_recovery()
    {
        using var directory = new TemporaryDirectory();
        var session = CreateProxy<IRetainerAutomationSession>((method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.ScanRetainerRosterAsync) =>
                Task.FromResult(RetainerRosterResult.Failed("TestStop", "Terminal test failure.")),
            nameof(IRetainerAutomationSession.CancelActive) => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        using var coordinator = CreateCoordinator(
            directory,
            CreateProxy<IFramework>((method, _) => throw new InvalidOperationException(method.Name)),
            session,
            new FakeAutoRetainerIpc { IsAvailable = false });

        Assert.True(coordinator.StartForPlan(out var runId));
        Assert.NotEmpty(runId);
        Assert.True(SpinWait.SpinUntil(() => coordinator.LastCompletedRunId is not null, TimeSpan.FromSeconds(2)));
        Assert.Equal(runId, coordinator.LastCompletedRunId);
        Assert.False(coordinator.HasRecovery);
    }

    private static RetainerRefreshCoordinator CreateCoordinator(
        TemporaryDirectory directory,
        IFramework framework,
        IRetainerAutomationSession session,
        IAutoRetainerIpc ipc,
        IPluginLog? log = null,
        Func<DateTime>? utcNow = null)
    {
        var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainers.json")));
        var state = new StateRepository(new QuartermasterStateStore(Path.Combine(directory.Path, "state.json")));
        return new(
            framework,
            log ?? CreateProxy<IPluginLog>((_, _) => null),
            cache,
            state,
            session,
            new Franthropy.Observations.V1.ObservationCaptureSessionRegistry(),
            ipc,
            new AutomationLease(),
            () => TestData.Owner,
            utcNow);
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ConfigurableDispatchProxy>();
        ((ConfigurableDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class FakeAutoRetainerIpc : IAutoRetainerIpc
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsBusy { get; set; }
        public bool IsSuppressed { get; set; }
        public AutoRetainerIpcCallbacks? Callbacks { get; private set; }
        public int FinishCalls { get; private set; }
        public void Register(AutoRetainerIpcCallbacks callbacks) => Callbacks = callbacks;
        public void QueueRetainerListTask(string consumer) => throw new InvalidOperationException("Native refresh must not queue AutoRetainer work.");
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() => FinishCalls++;
        public void SetSuppressed(bool suppressed) => IsSuppressed = suppressed;
        public void Dispose() => Callbacks = null;
    }
}
