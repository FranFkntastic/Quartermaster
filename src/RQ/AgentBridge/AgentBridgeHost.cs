using System.IO.Pipes;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace RQ.AgentBridge;

public sealed class AgentBridgeHost : IDisposable
{
    private const int MaxRequestCharacters = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PluginConfiguration configuration;
    private readonly string configDirectory;
    private readonly Action saveConfiguration;
    private readonly Func<Action, CancellationToken, Task> dispatchOnFramework;
    private readonly QuartermasterBridgeProvider provider;
    private CancellationTokenSource? cancellation;
    private Task? listenTask;
    private string? accessToken;

    public AgentBridgeHost(
        PluginConfiguration configuration,
        string configDirectory,
        Action saveConfiguration,
        Func<Action, CancellationToken, Task> dispatchOnFramework,
        QuartermasterBridgeProvider provider)
    {
        this.configuration = configuration;
        this.configDirectory = configDirectory;
        this.saveConfiguration = saveConfiguration;
        this.dispatchOnFramework = dispatchOnFramework;
        this.provider = provider;
    }

    public string PipeName => $"RQ.AgentBridge.{Environment.ProcessId}";

    public void Tick()
    {
#if DEBUG
        if (configuration.EnableAgentBridge)
        {
            EnsureStarted();
            return;
        }
#endif
        Stop();
    }

    public void Dispose() => Stop();

    private void EnsureStarted()
    {
        if (listenTask is not null)
            return;

        accessToken = GetOrCreateAccessToken();
        Directory.CreateDirectory(BridgeDirectory);
        if (!configuration.EnableAgentBridgeAudit && File.Exists(AuditPath))
            File.Delete(AuditPath);
        File.WriteAllText(DiscoveryPath, JsonSerializer.Serialize(new AgentBridgeDiscovery
        {
            SchemaVersion = 1,
            PipeName = PipeName,
            ProcessId = Environment.ProcessId,
            PluginInstanceId = configuration.PluginInstanceId,
        }, JsonOptions));
        cancellation = new CancellationTokenSource();
        listenTask = Task.Run(() => ListenLoopAsync(cancellation.Token));
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var requestJson = await ReadBoundedLineAsync(reader, connectionTimeout.Token).ConfigureAwait(false);
                var response = await HandleRequestAsync(requestJson, connectionTimeout.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppendAudit("host-error", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentBridgeResponse> HandleRequestAsync(string? requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestJson) || requestJson.Length > MaxRequestCharacters)
            return AgentBridgeResponse.Fail("Invalid bridge request.");

        AgentBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentBridgeRequest>(requestJson, JsonOptions);
        }
        catch (JsonException)
        {
            return AgentBridgeResponse.Fail("Bridge request JSON is invalid.");
        }

        if (request is null || !string.Equals(request.Token, accessToken, StringComparison.Ordinal))
            return AgentBridgeResponse.Fail("Bridge authentication failed.");

        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "hello":
                return AgentBridgeResponse.Ok("Bridge is ready.");
            case "get-snapshot":
                QuartermasterBridgeTruth? truth = null;
                await dispatchOnFramework(() => truth = provider.CreateTruth(), cancellationToken).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Quartermaster truth captured.", truth);
            case "get-review-surfaces":
                return AgentBridgeResponse.Ok("Review surfaces captured.", provider.GetReviewSurfaces());
            case "open-main-window":
                var opened = false;
                await dispatchOnFramework(() => opened = provider.TryOpenMainWindow(request.Target ?? "stock"), cancellationToken).ConfigureAwait(false);
                AppendAudit("open-main-window", opened ? request.Target ?? "stock" : "rejected");
                return opened ? AgentBridgeResponse.Ok("Quartermaster opened.") : AgentBridgeResponse.Fail("Requested Quartermaster view is not registered.");
            case "close-main-window":
                await dispatchOnFramework(provider.CloseMainWindow, cancellationToken).ConfigureAwait(false);
                AppendAudit("close-main-window", "accepted");
                return AgentBridgeResponse.Ok("Quartermaster closed.");
            default:
                return AgentBridgeResponse.Fail("Bridge command is not allowed.");
        }
    }

    private void Stop()
    {
        var activeCancellation = Interlocked.Exchange(ref cancellation, null);
        var activeListener = Interlocked.Exchange(ref listenTask, null);
        if (activeCancellation is not null)
        {
            activeCancellation.Cancel();
            if (activeListener is not null)
            {
                try { activeListener.Wait(TimeSpan.FromSeconds(1)); }
                catch (Exception exception) when (exception is AggregateException or OperationCanceledException) { }
            }
            activeCancellation.Dispose();
        }

        accessToken = null;
        if (File.Exists(DiscoveryPath))
            File.Delete(DiscoveryPath);
    }

    private string GetOrCreateAccessToken()
    {
        if (!string.IsNullOrWhiteSpace(configuration.AgentBridgeProtectedAccessToken))
        {
            try
            {
                return AgentBridgeDataProtection.UnprotectToken(
                    configuration.AgentBridgeProtectedAccessToken,
                    configuration.PluginInstanceId);
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                configuration.AgentBridgeProtectedAccessToken = string.Empty;
            }
        }

        var token = Guid.NewGuid().ToString("N");
        configuration.AgentBridgeProtectedAccessToken = AgentBridgeDataProtection.ProtectToken(
            token,
            configuration.PluginInstanceId);
        saveConfiguration();
        return token;
    }

    private void AppendAudit(string action, string result)
    {
        if (!configuration.EnableAgentBridgeAudit)
            return;
        Directory.CreateDirectory(BridgeDirectory);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow,
            action,
            result,
        }, JsonOptions) + Environment.NewLine);
    }

    private string BridgeDirectory => Path.Combine(configDirectory, "agent-bridge");
    private string DiscoveryPath => Path.Combine(BridgeDirectory, $"discovery-{Environment.ProcessId}.json");
    private string AuditPath => Path.Combine(BridgeDirectory, "audit.jsonl");

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return builder.Length == 0 ? null : builder.ToString();
            var newline = Array.IndexOf(buffer, '\n', 0, read);
            var length = newline >= 0 ? newline : read;
            if (newline >= 0 && length > 0 && buffer[length - 1] == '\r')
                length--;
            builder.Append(buffer, 0, length);
            if (builder.Length > MaxRequestCharacters)
                throw new InvalidDataException("Bridge request exceeds the maximum length.");
            if (newline >= 0)
                return builder.ToString();
        }
    }
}
