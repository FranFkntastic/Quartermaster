using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;
using RQ.UI;

namespace RQ.Tests;

public sealed class ListingInlineTests
{
    private static readonly OwnerScope Owner = new() { LocalContentId = 100, HomeWorldId = 93, CharacterName = "Fixture", HomeWorldName = "World" };
    private static ListingPlanAssignment A(int count = 1, int quantity = 1, int price = 100, uint item = 1, ulong retainer = 10) =>
        new() { ItemId = item, ItemName = $"Item {item}", RetainerId = retainer, RetainerName = $"Retainer {retainer}", ListingCount = count, QuantityPerListing = quantity, UnitPrice = price };
    private static ListingRow L(int quantity = 1, int? price = 100, uint item = 1, int slot = 0, ulong retainer = 10) =>
        new(BrowserScope.RetainerKey(retainer), retainer, $"Retainer {retainer}", slot, item, $"Item {item}", quantity,
            FfxivItemQuality.NQ, Evidence.Unknown<decimal>("fixture"), price.HasValue ? Evidence.Known((decimal)price.Value) : Evidence.Unknown<decimal>("fixture"), Evidence.Unknown<decimal>("fixture"), DateTime.UnixEpoch);
    private static StockStack S(int quantity, uint item = 1, ulong retainer = 10) =>
        new(BrowserScope.RetainerKey(retainer), BrowserScopeKind.Retainer, retainer, $"Retainer {retainer}", "Bag", 0, item, $"Item {item}", quantity, FfxivItemQuality.NQ, DateTime.UnixEpoch);
    private static BrowserProjection Browser(IReadOnlyList<ListingRow>? listings = null, IReadOnlyList<StockStack>? stock = null, bool observed = true)
    {
        BrowserScope[] scopes = [new(BrowserScope.AllKey, "All", BrowserScopeKind.All, null), new(BrowserScope.PlayerKey, "Player", BrowserScopeKind.Player, null), new(BrowserScope.RetainerKey(10), "Retainer 10", BrowserScopeKind.Retainer, 10), new(BrowserScope.RetainerKey(11), "Retainer 11", BrowserScopeKind.Retainer, 11)];
        return new() { Owner = Owner, Scopes = scopes, Items = BrowserProjection.Aggregate(stock ?? []), Listings = listings ?? [],
            RetainerInventoryCompleteByScope = scopes.ToDictionary(s => s.Key, _ => true), RetainerListingsCompleteByScope = scopes.ToDictionary(s => s.Key, _ => observed) };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rq-listing-inline-" + Guid.NewGuid().ToString("N"));
        public string PathToState => Path.Combine(directory, "state.json");
        public StateRepository Repository { get; }
        public ListingPlanDraftSession Session { get; }
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Repository = new(new QuartermasterStateStore(PathToState));
            Session = new(Repository, (_, id) => id == 1 ? 1 : 999, _ => false);
        }
        public QuartermasterRuntimeSnapshot Runtime(BrowserProjection? browser = null) => new(Repository.Snapshot().Revision,
            0, 0, 0, 0, DateTime.UtcNow, Owner, null!, new Dictionary<ulong, CachedRetainer>
            { [10] = new() { RetainerId = 10, RetainerName = "Retainer 10", Owner = Owner, IsCurrentlyAssigned = true },
              [11] = new() { RetainerId = 11, RetainerName = "Retainer 11", Owner = Owner, IsCurrentlyAssigned = true } },
            Repository.Snapshot(), browser ?? Browser(), null!, null!, []);
        public ListingPlanAssignment Add(int count = 1)
        {
            var runtime = Runtime(); Session.Ensure(runtime);
            var a = Session.Add(10, "Retainer 10", 1, "Item 1", ItemQualityPolicy.NqOnly, runtime.Browser);
            Session.SetListingCount(a.Id, count.ToString()); Session.SetUnitPrice(a.Id, "100"); return a;
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConflictBaselineIsBoundToTheMergedRevision()
    {
        using var f = new Fixture(); var a = f.Add(); f.Session.Save(f.Runtime());
        f.Session.SetUnitPrice(a.Id, "200");
        f.Repository.Mutate(s => { s.ListingPlans[0].Assignments[0].UnitPrice = 300; s.ListingPlans[0].Revision++; });
        f.Session.Save(f.Runtime());
        Assert.Equal(1, f.Session.ConflictCount); Assert.Equal(300, f.Session.Draft!.BaselineAssignments[0].UnitPrice);
        var extra = A(item: 2);
        f.Repository.Mutate(s => { s.ListingPlans[0].Assignments.Add(extra); s.ListingPlans[0].Revision++; });
        f.Session.Save(f.Runtime());
        Assert.Null(f.Session.Error); Assert.Contains(f.Repository.Snapshot().ListingPlans[0].Assignments, x => x.Id == extra.Id);
        Assert.Equal(200, f.Repository.Snapshot().ListingPlans[0].Assignments.First(x => x.Id == a.Id).UnitPrice);
    }

}
