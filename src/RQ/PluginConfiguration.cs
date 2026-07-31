using Dalamud.Configuration;

namespace RQ;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public bool EnableAgentBridge { get; set; }
    public bool EnableAgentBridgeAudit { get; set; }
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;
    public bool IncludeArmoury { get; set; }
    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeEquipped { get; set; }
    public bool IncludeSaddlebag { get; set; }
    public bool LegacyStorageSettingsImported { get; set; }
    public bool EnableSharedObservationShadow { get; set; }
}
