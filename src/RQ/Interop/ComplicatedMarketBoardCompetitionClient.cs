using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace RQ.Interop;

internal readonly record struct MarketPair(uint ItemId, bool Hq);
internal sealed record MarketScope(string Key, string Label, string Kind);
internal sealed record MarketListing(long PricePerUnit, int Quantity, bool Hq, string WorldName, string RetainerName);
internal sealed record MarketItem(uint ItemId, bool Hq, string Status, string? Reason, long? FetchedUtcMs,
    long? LastUploadUtcMs, bool Truncated, MarketListing[] Listings);
internal sealed record MarketSnapshot(MarketScope[] Scopes, string? Key, string Label,
    IReadOnlyDictionary<MarketPair, MarketItem> Items, string? Error);

// Only the Market view starts reads. The event is an invalidation, never a reason to fetch while hidden.
internal sealed class ComplicatedMarketBoardCompetitionClient : IDisposable
{
    private const string Prefix = "ComplicatedMarketBoard.v1.";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    private readonly ICallGateSubscriber<string> scopes;
    private readonly ICallGateSubscriber<string, string> competition;
    private readonly ICallGateSubscriber<string, uint[], object> changed;
    internal event Action<string, uint[]>? Changed;
    private void Notify(string key, uint[] ids) => Changed?.Invoke(key, ids);

    public ComplicatedMarketBoardCompetitionClient(IDalamudPluginInterface pi)
    {
        scopes = pi.GetIpcSubscriber<string>(Prefix + "GetMarketScopes");
        competition = pi.GetIpcSubscriber<string, string>(Prefix + "GetCompetition");
        changed = pi.GetIpcSubscriber<string, uint[], object>(Prefix + "CompetitionChanged");
        changed.Subscribe(Notify);
    }

    private sealed record Catalog(string Schema, string DefaultScopeKey, MarketScope[] Scopes);
    private sealed record Response(string Schema, string ScopeKey, string ScopeLabel, MarketItem[] Items);

    internal MarketSnapshot Read(string? choice, MarketPair[] pairs, bool force)
    {
        MarketScope[] catalog = [];
        MarketScope? scope = null;
        try
        {
            var c = JsonSerializer.Deserialize<Catalog>(scopes.InvokeFunc(), Json);
            if (c?.Schema != "cmb-market-scopes/v1") throw new JsonException();
            if (c.Scopes.Any(s => s is null || string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.Label)
                || s.Kind is not ("region" or "datacenter" or "world" or "custom"))) throw new JsonException();
            catalog = c.Scopes;
            scope = catalog.SingleOrDefault(s => s.Key == (choice ?? c.DefaultScopeKey));
            if (scope == null) return new(catalog, choice, "", new Dictionary<MarketPair, MarketItem>(), "Market scope unavailable");
            var items = new Dictionary<MarketPair, MarketItem>();
            if (pairs.Length > 0)
            {
                var request = JsonSerializer.Serialize(new { schema = "cmb-competition-request/v1", scopeKey = scope.Key, items = pairs, force }, Json);
                var r = JsonSerializer.Deserialize<Response>(competition.InvokeFunc(request), Json);
                if (r?.Schema != "cmb-competition/v1" || r.ScopeKey != scope.Key) throw new JsonException();
                foreach (var i in r.Items)
                {
                    if (i.Status is not ("pending" or "unavailable" or "fresh" or "stale") ||
                        i.Listings.Any(l => l.PricePerUnit <= 0 || l.Quantity <= 0 || string.IsNullOrWhiteSpace(l.WorldName) || string.IsNullOrWhiteSpace(l.RetainerName)))
                        throw new JsonException();
                    items.Add(new(i.ItemId, i.Hq), i);
                }
                if (pairs.Length != items.Count || !pairs.All(items.ContainsKey)) throw new JsonException();
            }
            return new(catalog, scope.Key, scope.Label, items, null);
        }
        catch (Exception ex)
        {
            return new(catalog, scope?.Key ?? choice, scope?.Label ?? "", new Dictionary<MarketPair, MarketItem>(),
                ex.GetType().Name.Contains("IpcNotReady", StringComparison.Ordinal) ? "CMB not installed / provider missing" : "Invalid CMB competition response");
        }
    }

    public void Dispose() => changed.Unsubscribe(Notify);
}
