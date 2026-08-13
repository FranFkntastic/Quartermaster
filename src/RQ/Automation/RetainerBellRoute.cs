using System.Numerics;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Travel;

namespace RQ.Automation;

internal interface IRetainerBellRoute
{
    Task<RetainerBellRouteResult> EnsureBellInRangeAsync(CancellationToken cancellationToken);

    void Cancel();
}

internal sealed record RetainerBellRouteResult(bool Success, string Code, string Message)
{
    public static RetainerBellRouteResult Succeeded(string message) => new(true, "BellReady", message);

    public static RetainerBellRouteResult Failed(string code, string message) => new(false, code, message);
}

internal interface IRetainerBellRoutePort
{
    SummoningBellNavigationTargetObservation ObserveBell();

    VNavmeshLifecycleObservation ObserveNavigation();

    VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range);

    bool SubmitAutomaticRetainerTravel();

    bool TryStopNavigation();
}

internal enum RetainerBellRouteStepState
{
    Waiting,
    Succeeded,
    Failed,
}

internal sealed record RetainerBellRouteStep(
    RetainerBellRouteStepState State,
    string Code,
    string Message);

internal sealed class RetainerBellRouteState
{
    private static readonly TimeSpan RouteBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PathStartBudget = TimeSpan.FromSeconds(2);
    private const float ArrivalMargin = 0.75f;

    private readonly IRetainerBellRoutePort port;
    private readonly Func<DateTime> utcNow;
    private readonly DateTime deadline;
    private bool automaticTravelSubmitted;
    private bool bellPathSubmitted;
    private bool bellPathObservedRunning;
    private bool ownsNavigation;
    private DateTime bellPathSubmittedAt;

    public RetainerBellRouteState(
        IRetainerBellRoutePort port,
        Func<DateTime>? utcNow = null)
    {
        this.port = port;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        deadline = this.utcNow().Add(RouteBudget);
    }

    public RetainerBellRouteStep Advance()
    {
        var now = utcNow();
        if (now >= deadline)
            return Fail("BellRouteTimedOut", "Timed out while acquiring an accessible summoning bell.");

        var bell = port.ObserveBell();
        var navigation = port.ObserveNavigation();
        if (bell.Available && bell.Distance < bell.OrdinaryInteractionDistance)
        {
            if (automaticTravelSubmitted && navigation.IsRunning && !ownsNavigation)
                return Wait("AutomaticTravelFinishing", "Waiting for automatic retainer travel to finish at the loaded bell.");

            StopOwnedNavigation();
            return Succeed($"Summoning bell {bell.BellGameObjectId:X} is within interaction range.");
        }

        if (navigation.State == VNavmeshLifecycleState.IpcFailure)
            return Fail("BellNavigationIpcFailure", navigation.Message);
        if (navigation.State == VNavmeshLifecycleState.Unavailable)
            return Fail("BellNavigationUnavailable", "vnavmesh is required to reach a summoning bell.");

        if (!bell.Available)
            return AdvanceWithoutLoadedBell(navigation);

        if (navigation.IsRunning)
        {
            if (ownsNavigation)
            {
                bellPathObservedRunning = true;
                return Wait("ApproachingBell", "Following the owned path to the loaded summoning bell.");
            }

            if (automaticTravelSubmitted)
                return Wait("AutomaticTravelRunning", "Automatic retainer travel is still running.");

            return Fail("NavigationAlreadyRunning", "Another vnavmesh path is already running; Quartermaster did not take control of it.");
        }

        if (navigation.IsTransient)
            return Wait("BellNavigationLoading", navigation.Message);

        if (bellPathSubmitted)
        {
            if (!bellPathObservedRunning && now < bellPathSubmittedAt.Add(PathStartBudget))
                return Wait("BellPathStarting", "Waiting for the submitted bell path to start.");

            return Fail("BellPathEndedEarly", "The path to the summoning bell ended before reaching interaction range.");
        }

        var range = MathF.Max(1f, bell.OrdinaryInteractionDistance - ArrivalMargin);
        var submission = port.TryMoveCloseTo(bell.Position, range);
        if (submission.Retryable)
            return Wait("BellNavigationLoading", submission.Message);
        if (!submission.Submitted)
            return Fail(submission.Code, submission.Message);

        bellPathSubmitted = true;
        bellPathSubmittedAt = now;
        ownsNavigation = true;
        return Wait("BellPathSubmitted", "Submitted a path to the nearest loaded summoning bell.");
    }

    public void StopOwnedNavigation()
    {
        if (!ownsNavigation)
            return;

        ownsNavigation = false;
        port.TryStopNavigation();
    }

    private RetainerBellRouteStep AdvanceWithoutLoadedBell(VNavmeshLifecycleObservation navigation)
    {
        if (bellPathSubmitted)
            return Fail("BellTargetLost", "The loaded summoning bell disappeared while Quartermaster was approaching it.");

        if (automaticTravelSubmitted)
            return Wait(
                navigation.IsRunning ? "AutomaticTravelRunning" : "WaitingForLoadedBell",
                navigation.IsRunning
                    ? "Automatic retainer travel is running."
                    : "Waiting for automatic retainer travel to load a summoning bell.");

        if (navigation.IsRunning)
            return Fail("NavigationAlreadyRunning", "Another vnavmesh path is already running; Quartermaster did not take control of it.");
        if (navigation.IsTransient)
            return Wait("BellNavigationLoading", navigation.Message);
        if (!port.SubmitAutomaticRetainerTravel())
            return Fail("AutomaticRetainerTravelUnavailable", "Lifestream did not accept /li auto; no travel command was retried.");

        automaticTravelSubmitted = true;
        return Wait("AutomaticTravelSubmitted", "Submitted /li auto once and is waiting for a summoning bell to load.");
    }

    private RetainerBellRouteStep Succeed(string message) =>
        new(RetainerBellRouteStepState.Succeeded, "BellReady", message);

    private RetainerBellRouteStep Wait(string code, string message) =>
        new(RetainerBellRouteStepState.Waiting, code, message);

    private RetainerBellRouteStep Fail(string code, string message)
    {
        StopOwnedNavigation();
        return new(RetainerBellRouteStepState.Failed, code, message);
    }
}

internal sealed class RetainerBellRoute : IRetainerBellRoute, IDisposable
{
    private readonly IFramework framework;
    private readonly IRetainerBellRoutePort port;
    private readonly object gate = new();
    private CancellationTokenSource? activeCancellation;

    public RetainerBellRoute(IFramework framework, IRetainerBellRoutePort port)
    {
        this.framework = framework;
        this.port = port;
    }

    public async Task<RetainerBellRouteResult> EnsureBellInRangeAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate)
        {
            if (activeCancellation is not null)
                return RetainerBellRouteResult.Failed("BellRouteAlreadyRunning", "A summoning-bell route is already active.");
            activeCancellation = linkedCancellation;
        }

        var state = new RetainerBellRouteState(port);
        try
        {
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var step = await framework.RunOnTick(
                    state.Advance,
                    cancellationToken: linkedCancellation.Token).ConfigureAwait(false);
                if (step.State == RetainerBellRouteStepState.Succeeded)
                    return RetainerBellRouteResult.Succeeded(step.Message);
                if (step.State == RetainerBellRouteStepState.Failed)
                    return RetainerBellRouteResult.Failed(step.Code, step.Message);
            }
        }
        finally
        {
            state.StopOwnedNavigation();
            lock (gate)
            {
                if (ReferenceEquals(activeCancellation, linkedCancellation))
                    activeCancellation = null;
            }
        }
    }

    public void Cancel()
    {
        lock (gate)
            activeCancellation?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
        if (port is IDisposable disposable)
            disposable.Dispose();
    }
}

internal sealed class DalamudRetainerBellRoutePort : IRetainerBellRoutePort, IDisposable
{
    private readonly DalamudSummoningBellInteractor bellInteractor;
    private readonly DalamudVNavmeshTravel navigation;
    private readonly ICommandManager commands;

    public DalamudRetainerBellRoutePort(
        DalamudSummoningBellInteractor bellInteractor,
        DalamudVNavmeshTravel navigation,
        ICommandManager commands)
    {
        this.bellInteractor = bellInteractor;
        this.navigation = navigation;
        this.commands = commands;
    }

    public SummoningBellNavigationTargetObservation ObserveBell() => bellInteractor.ObserveNavigationTarget();

    public VNavmeshLifecycleObservation ObserveNavigation() => navigation.Observe();

    public VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range) =>
        navigation.TryMoveCloseTo(destination, range, VNavmeshTravelMode.Ground);

    public bool SubmitAutomaticRetainerTravel() => commands.ProcessCommand("/li auto");

    public bool TryStopNavigation() => navigation.TryStop();

    public void Dispose() => bellInteractor.Dispose();
}
