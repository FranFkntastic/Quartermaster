using System.Text.Json;
using Dalamud.Plugin;

namespace RQ.Interop;

public sealed record SavedJobImportResponse(bool Accepted, string Message, string? Name);

public sealed class MarketMafiosoSavedJobClient(Func<string, string> invoke)
{
    internal const string Channel = "MarketMafioso.v1.ImportWorkshopSavedJob";
    internal const string Schema = "gooseworks-workshop-saved-job-import-response/v1";

    public MarketMafiosoSavedJobClient(IDalamudPluginInterface pluginInterface)
        : this(json => pluginInterface.GetIpcSubscriber<string, string>(Channel).InvokeFunc(json)) { }

    // Invoked synchronously by the button on the draw thread; importing starts nothing.
    public SavedJobImportResponse Send(string json)
    {
        string response;
        try { response = invoke(json); }
        catch (Exception ex) when (ex.GetType().Name.Contains("IpcNotReady", StringComparison.Ordinal))
        { return new(false, "MarketMafioso is not available.", null); }
        catch (Exception) { return new(false, "MarketMafioso import failed.", null); }
        return Parse(response);
    }

    internal static SavedJobImportResponse Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("Schema").GetString() != Schema) throw new JsonException();
            var accepted = root.GetProperty("Accepted").GetBoolean();
            var message = root.GetProperty("Message").GetString();
            var name = root.GetProperty("Name");
            if (string.IsNullOrWhiteSpace(message)) throw new JsonException();
            return new(accepted, message, name.ValueKind == JsonValueKind.Null ? null : name.GetString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return new(false, "Invalid response from MarketMafioso.", null); }
    }
}
