using RQ.Domain;
using RQ.Planning;
using RQ.UI;
using RQ.Inventory;
using Franthropy.FFXIV.Filtering;

namespace RQ.Tests;

public sealed class BrowserTests
{
    [Fact]
    public void Projection_KeepsPhysicalStacksStableScopesAndOwnerListings()
    {
        var first = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var second = TestData.Retainer(11, "Eris", (100, "Darksteel Ore", 4), (200, "Spruce Log", 5));
        first.Listings.Add(new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 2, UnitPrice = 40, ListedAtUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc) });
        var other = TestData.Retainer(12, "Other", (100, "Darksteel Ore", 99));
        other.Owner = new OwnerScope { LocalContentId = 55, HomeWorldId = 406, CharacterName = "Other", HomeWorldName = "Maduin" };

        var projection = BrowserProjectionBuilder.Build(
            [Bag((100, "Darksteel Ore", 2))],
            new Dictionary<ulong, CachedRetainer> { [10] = first, [11] = second, [12] = other },
            TestData.Owner);

        Assert.Equal(["all", "player", "retainer:10", "retainer:11"], projection.Scopes.Select(scope => scope.Key));
        var ore = projection.Items.Single(item => item.ItemId == 100);
        Assert.Equal(9, ore.TotalQuantity);
        Assert.Equal(2, ore.PlayerQuantity);
        Assert.Equal(7, ore.RetainerQuantity);
        Assert.Equal(3, ore.Stacks.Count);
        Assert.Single(projection.Listings);
        Assert.Equal(first.Listings[0].ListedAtUtc, projection.Listings[0].ObservedAtUtc);
    }

    [Fact]
    public void Projection_MissingOwnerDoesNotWidenToCachedRetainers()
    {
        var projection = BrowserProjectionBuilder.Build(
            [Bag((100, "Ore", 2))],
            new Dictionary<ulong, CachedRetainer> { [10] = TestData.Retainer(10, "Eris", (100, "Ore", 99)) },
            new OwnerScope());

        Assert.Equal(["all", "player"], projection.Scopes.Select(scope => scope.Key));
        Assert.Equal(2, Assert.Single(projection.Items).TotalQuantity);
        Assert.Empty(projection.Listings);
    }

    [Fact]
    public void Projection_UsesContainingBagTimestampForRetainerStacks()
    {
        var retainer = TestData.Retainer(10, "Eris", (100, "Ore", 3));
        retainer.ObservedAtUtc = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        retainer.Bags[0].ObservedAtUtc = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);

        var projection = BrowserProjectionBuilder.Build(
            [],
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner);

        var stack = Assert.Single(Assert.Single(projection.Items).Stacks);
        Assert.Equal(retainer.Bags[0].ObservedAtUtc, stack.ObservedAtUtc);
        Assert.NotEqual(retainer.ObservedAtUtc, stack.ObservedAtUtc);
    }

    [Fact]
    public void FranthropyQuery_FiltersItemsAndListings()
    {
        var retainer = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 7), (200, "Spruce Log", 1));
        retainer.Listings.Add(new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 2, IsHq = true, UnitPrice = 40 });
        var projection = BrowserProjectionBuilder.Build([Bag((100, "Darksteel Ore", 2))], new Dictionary<ulong, CachedRetainer> { [10] = retainer }, TestData.Owner);
        var controller = new BrowserQueryController();

        Assert.Equal([100U], controller.QueryItems(projection, "darksteel ownership.quantity>=9 retainer:Eris").Items.Select(item => item.ItemId));
        Assert.Equal([100U], controller.QueryListings(projection, "is:hq price=40 retainer:Eris").Listings.Select(item => item.ItemId));
    }

    [Fact]
    public void InvalidFilterEdit_KeepsLastValidResultsUntilContextChanges()
    {
        var projection = BrowserProjectionBuilder.Build([], new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3), (200, "Spruce Log", 3)),
        }, TestData.Owner);
        var controller = new BrowserQueryController();
        Assert.Equal([100U], controller.QueryItems(projection, "darksteel").Items.Select(item => item.ItemId));

        var invalid = controller.QueryItems(projection, "ownership.quantity:");

        Assert.False(invalid.Filter.IsValid);
        Assert.True(invalid.Filter.ShowingLastValid);
        Assert.Equal([100U], invalid.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void FranthropyQuery_UsesResolvedItemMetadata()
    {
        var metadata = new ItemMetadata(
            100,
            "Darksteel Pickaxe",
            "Tools",
            true,
            610,
            90,
            [new ItemJob(new FfxivJobKey(16), "Miner", "MIN")],
            [FfxivEquipmentSlot.MainHand],
            FfxivItemRarity.Rare,
            new FfxivUiCategoryKey(1),
            "Tools",
            false,
            true,
            true,
            true,
            1);
        var projection = BrowserProjectionBuilder.Build(
            [Bag((100, "Darksteel Pickaxe", 1))],
            new Dictionary<ulong, CachedRetainer>(),
            TestData.Owner,
            _ => metadata);

        var result = new BrowserQueryController().QueryItems(
            projection,
            "ilvl>=600 job:Miner rarity:Rare category:Tools");

        Assert.True(result.Filter.IsValid);
        Assert.Equal(100U, Assert.Single(result.Items).ItemId);
    }

    [Fact]
    public void WorkbenchState_ValidatesAndUpsertsWithoutDuplicateRows()
    {
        var stock = new StockGroup(100, "Darksteel Ore", [new StockStack("player", BrowserScopeKind.Player, null, "Player", "Inventory1", 0, 100, "Darksteel Ore", 2, Franthropy.FFXIV.Filtering.FfxivItemQuality.NQ, null)]);
        var state = new WorkbenchState();
        var plan = new List<TargetPlanItem>();
        state.Select(stock);
        state.StagedTargetText = "0";
        Assert.False(state.Apply(plan));
        state.StagedTargetText = "20";
        Assert.True(state.Apply(plan));
        state.Select(stock);
        state.StagedTargetText = "40";
        Assert.True(state.Apply(plan));

        var item = Assert.Single(plan);
        Assert.Equal(40, item.TargetQuantity);
        Assert.True(item.Enabled);
    }

    private static InventoryBag Bag(params (uint Id, string Name, uint Quantity)[] items) => new()
    {
        BagName = "Inventory1",
        Items = items.Select(item => new InventoryItem { ItemId = item.Id, ItemName = item.Name, Quantity = item.Quantity }).ToList(),
    };
}
