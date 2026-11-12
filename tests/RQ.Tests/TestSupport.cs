using RQ.Domain;
using RQ.Inventory;
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

}
