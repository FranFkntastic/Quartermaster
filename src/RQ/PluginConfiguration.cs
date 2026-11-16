using Dalamud.Configuration;
using Franthropy.Dalamud.Automation.Vendors.Coordination;

namespace RQ;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public bool EnableAgentBridge { get; set; }
    /// <summary>Explicit `/rq bridge off`; the only thing that keeps the bridge down on a dev-loaded install.</summary>
    public bool AgentBridgeDisabledByUser { get; set; }
    public bool EnableAgentBridgeAudit { get; set; }
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;
    public bool IncludeArmoury { get; set; }
    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeEquipped { get; set; }
    public bool LegacyStorageSettingsImported { get; set; }
    public GilVendorBuyRunSnapshot? ActiveTransferPlanVendorBuy { get; set; }
}
