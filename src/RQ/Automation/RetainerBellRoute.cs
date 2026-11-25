using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Travel;

namespace RQ.Automation;

internal interface IRetainerBellRoute
{
    Task<RetainerBellRouteResult> EnsureBellInRangeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits, within <paramref name="budget"/>, until the character can act (loaded, not between areas or in a
    /// cutscene, Lifestream idle) and has stayed that way briefly. Nothing is dispatched while waiting.
    /// </summary>
    Task<RetainerBellRouteResult> WaitUntilPlayerCanActAsync(TimeSpan budget, CancellationToken cancellationToken) =>
        Task.FromResult(RetainerBellRouteResult.Succeeded("Readiness is not observed by this route."));

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

    /// <summary>Whether Lifestream reports a running task; a missing Lifestream IPC reads as idle.</summary>
    bool LifestreamBusy() => false;

    /// <summary>Why the character cannot act yet (loading, between areas, cutscene), or null when it can.</summary>
    string? PlayerNotReady() => null;
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
    // /li auto is swallowed when sent while the game is still settling; resend if Lifestream never picked it up.
    private static readonly TimeSpan AutomaticTravelStartBudget = TimeSpan.FromSeconds(5);
    private const int AutomaticTravelSubmissionLimit = 3;
    private const float ArrivalMargin = 0.75f;

    private readonly IRetainerBellRoutePort port;
    private readonly Func<DateTime> utcNow;
    private readonly DateTime deadline;
    private bool automaticTravelSubmitted;
    private int automaticTravelSubmissions;
    private DateTime automaticTravelSubmittedAt;
    private bool automaticTravelObservedBusy;
    private RetainerBellRouteStep? lastWait;
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
            return Fail("BellRouteTimedOut", lastWait is { } waited
                ? $"Timed out while acquiring an accessible summoning bell; last waiting on {waited.Code}: {waited.Message}"
                : "Timed out while acquiring an accessible summoning bell.");

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
            {
                automaticTravelObservedBusy = true;
                return Wait("AutomaticTravelRunning", "Automatic retainer travel is still running.");
            }

            return Wait("NavigationAlreadyRunning", "Another vnavmesh path is running; waiting for it to finish without taking control of it.");
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
        {
            var lifestreamBusy = port.LifestreamBusy();
            if (navigation.IsRunning || lifestreamBusy)
                automaticTravelObservedBusy = true;
            // Lifestream never started: the command was swallowed, so nothing travelled. Send it again.
            if (!automaticTravelObservedBusy &&
                utcNow() - automaticTravelSubmittedAt >= AutomaticTravelStartBudget &&
                automaticTravelSubmissions < AutomaticTravelSubmissionLimit &&
                port.PlayerNotReady() is null)
                return SubmitAutomaticTravel();
            return Wait(
                navigation.IsRunning || lifestreamBusy ? "AutomaticTravelRunning" : "WaitingForLoadedBell",
                navigation.IsRunning || lifestreamBusy
                    ? "Automatic retainer travel is running."
                    : "Waiting for automatic retainer travel to load a summoning bell.");
        }

        if (navigation.IsRunning)
            return Wait("NavigationAlreadyRunning", "Another vnavmesh path is running; waiting for it to finish without taking control of it.");
        if (navigation.IsTransient)
            return Wait("BellNavigationLoading", navigation.Message);
        if (port.PlayerNotReady() is { } notReady)
            return Wait("PlayerNotReady", notReady);
        if (port.LifestreamBusy())
            return Wait("LifestreamBusy", "Lifestream is still running another task; waiting before sending /li auto.");
        return SubmitAutomaticTravel();
    }

    private RetainerBellRouteStep SubmitAutomaticTravel()
    {
        if (!port.SubmitAutomaticRetainerTravel())
            return Fail("AutomaticRetainerTravelUnavailable", "Lifestream did not accept /li auto; no travel command was retried.");

        automaticTravelSubmitted = true;
        automaticTravelSubmissions++;
        automaticTravelSubmittedAt = utcNow();
        return Wait("AutomaticTravelSubmitted", $"Submitted /li auto (attempt {automaticTravelSubmissions} of {AutomaticTravelSubmissionLimit}) and is waiting for a summoning bell to load.");
    }

    private RetainerBellRouteStep Succeed(string message) =>
        new(RetainerBellRouteStepState.Succeeded, "BellReady", message);

    private RetainerBellRouteStep Wait(string code, string message) =>
        (lastWait = new(RetainerBellRouteStepState.Waiting, code, message));

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

    public async Task<RetainerBellRouteResult> WaitUntilPlayerCanActAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        DateTime? readySince = null;
        var sawNotReady = false;
        string? lastReason = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = await framework.RunOnTick(
                () => port.PlayerNotReady() ?? (port.LifestreamBusy() ? "Lifestream is still running a task." : null),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            if (reason is null)
            {
                readySince ??= now;
                // Input sent in the first moments after a load or Lifestream task is often swallowed.
                if (!sawNotReady || now - readySince.Value >= ReadySettle)
                    return RetainerBellRouteResult.Succeeded("The character can act.");
            }
            else
            {
                sawNotReady = true;
                readySince = null;
                lastReason = reason;
            }
            if (now >= deadline)
                return RetainerBellRouteResult.Failed(
                    "PlayerNotReady",
                    $"The character could not act within {budget.TotalSeconds:N0}s; last: {lastReason ?? "not settled"}. Nothing was sent.");
            await framework.DelayTicks(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan ReadySettle = TimeSpan.FromSeconds(2);

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
    private readonly IDalamudPluginInterface? pluginInterface;
    private readonly ICondition? condition;
    private readonly IObjectTable? objects;

    public DalamudRetainerBellRoutePort(
        DalamudSummoningBellInteractor bellInteractor,
        DalamudVNavmeshTravel navigation,
        ICommandManager commands,
        IDalamudPluginInterface? pluginInterface = null,
        ICondition? condition = null,
        IObjectTable? objects = null)
    {
        this.bellInteractor = bellInteractor;
        this.navigation = navigation;
        this.commands = commands;
        this.pluginInterface = pluginInterface;
        this.condition = condition;
        this.objects = objects;
    }

    public SummoningBellNavigationTargetObservation ObserveBell() => bellInteractor.ObserveNavigationTarget();

    public VNavmeshLifecycleObservation ObserveNavigation() => navigation.Observe();

    public VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range) =>
        navigation.TryMoveCloseTo(destination, range, VNavmeshTravelMode.Ground);

    public bool SubmitAutomaticRetainerTravel() => commands.ProcessCommand("/li auto");

    public bool TryStopNavigation() => navigation.TryStop();

    public bool LifestreamBusy()
    {
        if (pluginInterface is null)
            return false;
        try { return pluginInterface.GetIpcSubscriber<bool>(DalamudLifestreamAetheryteTravel.IsBusyChannel).InvokeFunc(); }
        catch { return false; }
    }

    public string? PlayerNotReady()
    {
        if (objects is not null && objects.LocalPlayer is null)
            return "No character is loaded yet.";
        if (condition is null)
            return null;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return "The character is between areas.";
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78])
            return "A cutscene is playing.";
        return null;
    }

    public void Dispose() => bellInteractor.Dispose();
}
