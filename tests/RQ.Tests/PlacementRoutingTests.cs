using RQ.Domain;
using RQ.Planning;

namespace RQ.Tests;

public sealed class PlacementRoutingTests
{
    // Stored pre-matrix policies (numeric enums, as the state store wrote them) must keep their
    // meaning on load and must not be written back in the old shape: the old fields would
    // otherwise be re-read on the next load and silently re-decide where items go.
    [Fact]
    public void StoredRoutingPolicies_LoadAsExplicitDestinations_AndSaveWithoutOldFields()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        File.WriteAllText(statePath, """
        {
          "planItems": [
            { "id": "00000000-0000-0000-0000-00000000000a", "itemId": 1, "itemName": "A", "routing": { "mode": 0, "preferredRetainerIds": [], "overflow": 0 } },
            { "id": "00000000-0000-0000-0000-00000000000b", "itemId": 2, "itemName": "B", "routing": { "mode": 1, "preferredRetainerIds": [11, 10], "overflow": 0 } },
            { "id": "00000000-0000-0000-0000-00000000000c", "itemId": 3, "itemName": "C", "routing": { "mode": 1, "preferredRetainerIds": [10], "overflow": 1 } },
            { "id": "00000000-0000-0000-0000-00000000000d", "itemId": 4, "itemName": "D", "routing": { "mode": 0, "preferredRetainerIds": [12], "overflow": "HoldOnPlayer" } }
          ],
          "listingPlans": [
            { "reserves": [ { "itemId": 5, "itemName": "E", "location": { "mode": 0, "preferredRetainerIds": [12], "overflow": 1 } } ] }
          ]
        }
        """);

        static void AssertMapped(QuartermasterState state)
        {
            var rules = state.PlanItems.ToDictionary(rule => rule.ItemId, rule => rule.Routing);
            Expect(rules[1], true);
            Expect(rules[2], true, 11, 10);
            Expect(rules[3], false, 10);
            Expect(rules[4], false, 12);
            // A reserve's legacy location becomes ranked placement homes (explicit retainers only; any space is
            // dropped), and the reserve itself, which kept no units, is gone.
            Assert.Empty(Assert.Single(state.ListingPlans).Reserves);
            var placement = Assert.Single(state.Placements);
            Assert.Equal(5U, placement.ItemId);
            var home = Assert.Single(placement.Homes);
            Assert.Equal(12UL, home.RetainerId);
            Assert.Null(home.MaxUnits);
        }

        var repository = TestData.Repository(directory.Path);
        AssertMapped(repository.FullSnapshot());
        repository.Mutate(state => state.PlanItems[0].Notes = "saved");

        var saved = File.ReadAllText(statePath);
        Assert.DoesNotContain("\"overflow\"", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"mode\"", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"location\"", saved, StringComparison.OrdinalIgnoreCase);
        AssertMapped(TestData.Repository(directory.Path).FullSnapshot());
    }

    // Homes used to live on Listing Plan reserves, including 0-unit "placement only" stubs. Upgrading must keep every
    // home (lost homes silently strand stock in the bags) and every real reserve level, drop only the stubs, survive
    // the operation-store combine every reader goes through, and never duplicate on the next load.
    [Fact]
    public void LegacyReserveHomes_MoveToPlacements_StubsGo_RealReservesStay_Idempotently()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        const string owner = "\"owner\": { \"localContentId\": 9001, \"homeWorldId\": 406 }";
        const string character = "\"character\": { \"localContentId\": 9001, \"homeWorldId\": 406 }";
        File.WriteAllText(statePath, $$"""
        {
          "listingPlans": [
            {
              {{owner}},
              "reserves": [
                { "itemId": 5, "itemName": "Real", "reserveUnits": 20, "homes": [ { {{character}}, "retainerId": 12, "maxUnits": 40 } ] },
                { "itemId": 6, "itemName": "Stub", "reserveUnits": 0, "homes": [ { {{character}}, "retainerId": 13 } ] }
              ]
            }
          ]
        }
        """);

        static IReadOnlyList<Guid> AssertMigrated(QuartermasterState state)
        {
            var plan = Assert.Single(state.ListingPlans);
            Assert.Equal("Default", plan.Name);
            var reserve = Assert.Single(plan.Reserves);
            Assert.Equal((5U, 20), (reserve.ItemId, reserve.ReserveUnits));
            Assert.Null(reserve.Homes);
            var placements = state.Placements.OrderBy(rule => rule.ItemId).ToArray();
            Assert.Equal([(5U, 12UL, (int?)40), (6U, 13UL, null)],
                placements.Select(rule => (rule.ItemId, Assert.Single(rule.Homes).RetainerId, rule.Homes[0].MaxUnits)));
            Assert.All(placements, rule => Assert.Equal(9001UL, rule.Owner.LocalContentId));
            return placements.Select(rule => rule.Id).ToArray();
        }

        var first = TestData.Repository(directory.Path);
        var ids = AssertMigrated(first.FullSnapshot());
        Assert.Equal(ids, AssertMigrated(first.Read(state => state)));
        using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath)))
            Assert.All(saved.RootElement.GetProperty("listingPlans")[0].GetProperty("reserves").EnumerateArray(),
                reserve => Assert.False(reserve.TryGetProperty("homes", out _)));

        Assert.Equal(ids, AssertMigrated(TestData.Repository(directory.Path).FullSnapshot()));
    }

    // Listed order is strict and an unlisted retainer is never a destination without Any space,
    // even when it already holds a partial stack (the old consolidate ordering preferred it).
    [Fact]
    public void Route_UsesOnlyListedRetainersInOrder_AndAnySpaceOnlyAfterThem()
    {
        var cache = new Dictionary<ulong, CachedRetainer>
        {
            [10] = Observed(TestData.Retainer(10, "Ten")),
            [11] = Observed(TestData.Retainer(11, "Eleven", (5095, "Darksteel Rivets", 10))),
            [12] = Observed(TestData.Retainer(12, "Twelve")),
        };
        var oneRetainerFull = 175 * 999;

        var listedOnly = StowageRouter.Route(
            Request(new StowageRoutingPolicy { PreferredRetainerIds = [12, 10] }, oneRetainerFull * 3),
            cache, TestData.Owner, 999);
        Assert.Equal(new ulong[] { 12, 10 }, listedOnly.Allocations.Select(allocation => allocation.RetainerId));
        Assert.True(listedOnly.RemainingQuantity > 0);

        var nothingListed = StowageRouter.Route(Request(new StowageRoutingPolicy(), 50), cache, TestData.Owner, 999);
        Assert.Empty(nothingListed.Allocations);
        Assert.Equal(50, nothingListed.RemainingQuantity);

        var withAnySpace = StowageRouter.Route(
            Request(new StowageRoutingPolicy { PreferredRetainerIds = [12, 10], AnySpace = true }, oneRetainerFull * 3),
            cache, TestData.Owner, 999);
        Assert.Equal(new ulong[] { 12, 10, 11 }, withAnySpace.Allocations.Select(allocation => allocation.RetainerId));
    }

    private static void Expect(StowageRoutingPolicy routing, bool anySpace, params ulong[] retainers)
    {
        Assert.Equal(retainers, routing.PreferredRetainerIds);
        Assert.Equal(anySpace, routing.AnySpace);
    }

    private static StowageDepositRequest Request(StowageRoutingPolicy routing, int quantity) =>
        new(null, null, 5095, "Darksteel Rivets", false, quantity, routing);

    private static CachedRetainer Observed(CachedRetainer retainer)
    {
        retainer.ObservedSources = Enumerable.Range(1, 7).Select(page => $"RetainerPage{page}").ToList();
        return retainer;
    }
}
