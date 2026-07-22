using Dalamud.Plugin;

namespace RQ.Interop;

public sealed class DalamudIpcRegistrar : IIpcRegistrar
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Dictionary<string, Action> unregister = new(StringComparer.Ordinal);
    private Action<string>? sendChanged;

    public DalamudIpcRegistrar(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Register(string channel, Func<string> callback)
    {
        var provider = pluginInterface.GetIpcProvider<string>(channel);
        provider.RegisterFunc(callback);
        unregister[channel] = provider.UnregisterFunc;
    }

    public void Register(string channel, Func<string, string> callback)
    {
        var provider = pluginInterface.GetIpcProvider<string, string>(channel);
        provider.RegisterFunc(callback);
        unregister[channel] = provider.UnregisterFunc;
    }

    public void RegisterNotification(string channel)
    {
        var provider = pluginInterface.GetIpcProvider<string, object>(channel);
        sendChanged = json => provider.SendMessage(json);
        // SendMessage events have no registered callback to unregister.
        unregister[channel] = () => sendChanged = null;
    }

    public void SendNotification(string channel, string json)
    {
        if (channel == IpcChannels.Changed)
            sendChanged?.Invoke(json);
    }

    public void Unregister(string channel)
    {
        if (!unregister.Remove(channel, out var action))
            return;
        action();
    }
}
