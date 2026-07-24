using System.Reflection;
using Franthropy.Dalamud.AgentBridge;
using SharedAgentBridgeHost = Franthropy.Dalamud.AgentBridge.AgentBridgeHost;

namespace RQ.AgentBridge;

/// <summary>Quartermaster policy layered on the shared authenticated bridge host.</summary>
public sealed class AgentBridgeHost : IDisposable
{
    private readonly PluginConfiguration configuration;
    private readonly Func<Action, CancellationToken, Task> dispatchOnFramework;
    private readonly QuartermasterBridgeProvider provider;
    private readonly AgentBridgeCommandRouter router = new();
    private readonly AgentBridgeOperationRegistry operations = new();
    private readonly SharedAgentBridgeHost host;
    private readonly AgentBridgeManifest manifest;
    private string? activeRefreshOperationId;
    private bool activeRefreshObserved;

    public AgentBridgeHost(
        PluginConfiguration configuration,
        string configDirectory,
        string mainDllPath,
        Action saveConfiguration,
        Func<Action, CancellationToken, Task> dispatchOnFramework,
        QuartermasterBridgeProvider provider)
    {
        this.configuration = configuration;
        this.dispatchOnFramework = dispatchOnFramework;
        this.provider = provider;
        var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(configDirectory);
        var reviewSurfaces = provider.GetReviewSurfaces();
        manifest = new AgentBridgeManifest(
            2,
            AgentBridgeRuntimeIdentity.FromAssembly("RQ", Assembly.GetExecutingAssembly(), mainDllPath),
            profile.Id,
            profile.Alias,
            "Quartermaster.truth.v5",
            [new("snapshot"), new("reviewed-actions"), new("operations"), new("encrypted-capture")],
            reviewSurfaces,
            reviewSurfaces
                .Select(surface => new AgentBridgeCaptureSurfaceDescriptor(
                    surface.Id,
                    surface.Label,
                    surface.Order,
                    IsDefault: surface.Id == "stock"))
                .ToArray(),
            [new(
                "quartermaster.refresh-retainers",
                "Refresh retainers",
                "stock",
                AgentBridgeUiControlKind.Button,
                true,
                CompletionOperationKind: "retainer-refresh")]);
        RegisterCommands();
        host = new SharedAgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = configDirectory,
            PluginInstanceId = configuration.PluginInstanceId,
            PipeName = $"RQ.AgentBridge.{Environment.ProcessId}",
            GetProtectedAccessToken = () => configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = saveConfiguration,
            CreateManifest = () => manifest,
            HandleRequestAsync = router.HandleAsync,
            EnableAudit = configuration.EnableAgentBridgeAudit,
        });
    }

    public string PipeName => $"RQ.AgentBridge.{Environment.ProcessId}";

    public void Tick()
    {
#if DEBUG
        if (configuration.EnableAgentBridge)
            host.Start();
        else
            host.Stop();
#else
        host.Stop();
#endif
        UpdateRefreshOperation();
    }

    public void Dispose() => host.Dispose();

    private void RegisterCommands()
    {
        router.Register("get-snapshot", async (_, cancellationToken) =>
            AgentBridgeResponse.Ok("Quartermaster truth captured.", await OnFrameworkAsync(provider.CreateTruth, cancellationToken).ConfigureAwait(false)));
        router.Register("get-control-surface", _ => AgentBridgeResponse.Ok("Control surface captured.", provider.GetControlSurface()));
        router.Register("get-review-surfaces", _ => AgentBridgeResponse.Ok("Review surfaces captured.", manifest.ReviewSurfaces));
        router.Register("get-control", request =>
        {
            if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A control ID is required.");
            var review = provider.ReviewControl(request.Target);
            return review.Control is null
                ? new AgentBridgeResponse { Success = false, Message = "The requested control is not rendered.", Receipt = review }
                : AgentBridgeResponse.Ok("Reviewed control captured.", review);
        });
        router.Register("invoke-control", InvokeControlAsync);
        router.Register("get-operation", request =>
        {
            if (string.IsNullOrWhiteSpace(request.OperationId)) return AgentBridgeResponse.Fail("An operation ID is required.");
            var operation = operations.Get(request.OperationId);
            return operation is null ? AgentBridgeResponse.Fail("Operation was not found.") : AgentBridgeResponse.Ok("Operation captured.", operation);
        });
        router.Register("open-main-window", async (request, cancellationToken) =>
        {
            var opened = false;
            await dispatchOnFramework(() => opened = provider.TryOpenMainWindow(request.Target ?? "stock"), cancellationToken).ConfigureAwait(false);
            return opened ? AgentBridgeResponse.Ok("Quartermaster opened.") : AgentBridgeResponse.Fail("Requested Quartermaster view is not registered.");
        });
        router.Register("close-main-window", async (_, cancellationToken) =>
        {
            await dispatchOnFramework(provider.CloseMainWindow, cancellationToken).ConfigureAwait(false);
            return AgentBridgeResponse.Ok("Quartermaster closed.");
        });
    }

    private async ValueTask<AgentBridgeResponse> InvokeControlAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is null)
            return AgentBridgeResponse.Fail("Control ID and reviewed frame ID are required.");
        AgentBridgeUiControlInvocation? invocation = null;
        await dispatchOnFramework(
            () => invocation = provider.InvokeControl(request.Target, request.FrameId.Value, request.Arguments),
            cancellationToken).ConfigureAwait(false);
        if (invocation is null)
            return AgentBridgeResponse.Fail("Control invocation did not complete on the framework thread.");
        if (!invocation.Success)
            return new AgentBridgeResponse { Success = false, Message = invocation.Message, Receipt = invocation };
        var operationId = invocation.Action?.OperationId;
        if (request.Target == "quartermaster.refresh-retainers" && string.IsNullOrWhiteSpace(operationId))
        {
            var operation = operations.Begin("retainer-refresh", "Quartermaster accepted the refresh request.");
            activeRefreshOperationId = operation.Id;
            activeRefreshObserved = false;
            operationId = operation.Id;
        }
        return AgentBridgeResponse.Ok(invocation.Message, invocation, operationId);
    }

    private void UpdateRefreshOperation()
    {
        if (activeRefreshOperationId is not { } operationId || operations.Get(operationId) is not { } operation)
            return;
        var truth = provider.CreateTruth();
        if (truth.RefreshActive)
        {
            activeRefreshObserved = true;
            if (operation.State is AgentBridgeOperationState.Queued)
                operations.Update(operationId, AgentBridgeOperationState.Running, truth.RefreshStatus);
            return;
        }
        var failed = IsRefreshFailure(truth.RefreshStatus);
        if (!activeRefreshObserved && !failed)
            return;
        operations.Update(
            operationId,
            failed ? AgentBridgeOperationState.Failed : AgentBridgeOperationState.Succeeded,
            truth.RefreshStatus,
            postconditions: new Dictionary<string, string> { ["refreshActive"] = "false" });
        activeRefreshOperationId = null;
        activeRefreshObserved = false;
    }

    private static bool IsRefreshFailure(string status) =>
        status.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || status.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
        || status.Contains("could not", StringComparison.OrdinalIgnoreCase)
        || status.Contains("requires", StringComparison.OrdinalIgnoreCase)
        || status.Contains("disabled", StringComparison.OrdinalIgnoreCase);

    private async Task<T> OnFrameworkAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        T? result = default;
        await dispatchOnFramework(() => result = action(), cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
