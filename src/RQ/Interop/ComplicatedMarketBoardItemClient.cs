using System.Text.Json;
using Dalamud.Plugin;

namespace RQ.Interop;

/// <summary>Explicit item navigation only; never a price assessment or alternate search route.</summary>
public sealed class ComplicatedMarketBoardItemClient(Func<uint, string> invoke)
{
    public const string Channel = "ComplicatedMarketBoard.v1.OpenItem";
    public const string ResponseSchema = "complicated-market-board-open-item-response/v1";
    public ComplicatedMarketBoardItemClient(IDalamudPluginInterface pluginInterface)
        : this(id => pluginInterface.GetIpcSubscriber<uint, string>(Channel).InvokeFunc(id)) { }

    public string Open(uint itemId)
    {
        try
        {
            using var response = JsonDocument.Parse(invoke(itemId));
            var root = response.RootElement;
            if (root.GetProperty("schema").GetString() != ResponseSchema || root.GetProperty("itemId").GetUInt32() != itemId)
                return "CMB returned an incompatible item-open response.";
            return root.GetProperty("opened").GetBoolean()
                ? "Opened item search in CMB. No undercut assessment was requested."
                : "CMB could not open this item.";
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("IpcNotReady", StringComparison.Ordinal))
        { return "CMB item search unavailable: the versioned item-open provider is not installed."; }
        catch (Exception) { return "CMB item search failed or returned an invalid response."; }
    }
}
