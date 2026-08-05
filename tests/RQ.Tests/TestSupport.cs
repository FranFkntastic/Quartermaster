using System.Text.Json;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rq-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestData
{
    public static readonly OwnerScope Owner = new()
    {
        LocalContentId = 9001,
        HomeWorldId = 406,
        CharacterName = "Current Character",
        HomeWorldName = "Maduin",
    };

    public static CachedRetainer Retainer(ulong id, string name, params (uint ItemId, string Name, uint Quantity)[] items) => new()
    {
        RetainerId = id,
        RetainerName = name,
        Owner = Owner,
        ObservedAtUtc = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
        Bags =
        [
            new CachedBag
            {
                BagName = "RetainerPage1",
                ObservedAtUtc = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
                Items = items.Select(item => new CachedItem { ItemId = item.ItemId, ItemName = item.Name, Quantity = item.Quantity }).ToList(),
            },
        ],
    };

    public static StateRepository Repository(string directory) => new(new QuartermasterStateStore(System.IO.Path.Combine(directory, "state.json")));

    public static ItemMetadata Metadata(uint itemId, string? name = null) => new(
        itemId,
        name ?? $"Item {itemId}",
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        999);

    public static ShortageRequest Request(string requestId = "request-1", string operationId = "operation-1", OwnerScope? owner = null) => new()
    {
        Schema = ShortageSubmissionService.RequestSchema,
        ProviderInstanceId = "provider-1",
        RequestId = requestId,
        OperationId = operationId,
        SubmittedAtUtc = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
        Owner = new RequestOwner
        {
            LocalContentId = (owner ?? Owner).LocalContentId ?? 0,
            HomeWorldId = (owner ?? Owner).HomeWorldId ?? 0,
            CharacterName = (owner ?? Owner).CharacterName,
        },
        Items =
        [
            new ShortageRequestItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 50, ShortageQuantity = 30 },
        ],
    };

    public static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

internal sealed class TestWorkQueue : IFrameworkWorkQueue
{
    private readonly Queue<Action> actions = new();
    public void Enqueue(Action action) => actions.Enqueue(action);
    public int Count => actions.Count;
    public void Drain()
    {
        while (actions.TryDequeue(out var action))
            action();
    }
}

internal sealed class RecordingIpcRegistrar : IIpcRegistrar
{
    public Dictionary<string, Delegate?> Registrations { get; } = new();
    public List<string> Unregistered { get; } = [];
    public List<(string Channel, string Json)> Notifications { get; } = [];
    public void Register(string channel, Func<string> callback) => Registrations[channel] = callback;
    public void Register(string channel, Func<string, string> callback) => Registrations[channel] = callback;
    public void RegisterNotification(string channel) => Registrations[channel] = null;
    public void SendNotification(string channel, string json) => Notifications.Add((channel, json));
    public void Unregister(string channel) => Unregistered.Add(channel);
}
