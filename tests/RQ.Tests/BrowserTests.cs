using RQ.Domain;
using RQ.Planning;
using RQ.UI;
using RQ.Inventory;
using Franthropy.FFXIV.Filtering;

namespace RQ.Tests;

public sealed class BrowserTests
{
    [Fact]
    public void PlayerDelta_RebuildsOnlyAffectedStockGroups()
    {
        var current = BrowserProjectionBuilder.Build(
            [new InventoryBag
            {
                BagName = "Inventory1",
                Items =
                [
                    new InventoryItem { ItemId = 100, ItemName = "Ore", Quantity = 10, ContainerKey = "Inventory1", SlotIndex = 0 },
                    new InventoryItem { ItemId = 200, ItemName = "Log", Quantity = 7, ContainerKey = "Inventory1", SlotIndex = 1 },
                ],
            }],
            new Dictionary<ulong, CachedRetainer>(),
            TestData.Owner,
            itemId => TestData.Metadata(itemId, itemId == 100 ? "Ore" : "Log"));
        var unaffected = current.Items.Single(item => item.ItemId == 200);
        var change = new PlayerInventoryCacheChange(
            TestData.Owner,
            new DateTime(2026, 8, 5, 10, 0, 1, DateTimeKind.Utc),
            false,
            [new PlayerInventorySlotMutation(
                "Inventory1",
                0,
                new InventoryItem { ItemId = 100, ItemName = "Ore", Quantity = 10, ContainerKey = "Inventory1", SlotIndex = 0 },
                new InventoryItem { ItemId = 100, ItemName = "Ore", Quantity = 4, ContainerKey = "Inventory1", SlotIndex = 0 })]);

        var updated = BrowserProjectionBuilder.ApplyPlayerChanges(current, change, itemId => TestData.Metadata(itemId));

        Assert.Equal(4, updated.Items.Single(item => item.ItemId == 100).PlayerQuantity);
        Assert.Same(unaffected, updated.Items.Single(item => item.ItemId == 200));
    }

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
    public void Projection_UnlistedQuantityUsesRetainerBagsAndScopeWithoutCountingPlayerStock()
    {
        var first = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var second = TestData.Retainer(11, "Nyx", (100, "Darksteel Ore", 4));
        MarkInventoryComplete(first);
        MarkInventoryComplete(second);
        first.Listings.Add(new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 2 });
        var projection = BrowserProjectionBuilder.Build(
            [Bag((100, "Darksteel Ore", 20))],
            new Dictionary<ulong, CachedRetainer> { [10] = first, [11] = second },
            TestData.Owner);

        var all = projection.GetUnlistedRetainerQuantity(100, BrowserScope.AllKey);
        var eris = projection.GetUnlistedRetainerQuantity(100, BrowserScope.RetainerKey(10));

        Assert.True(all.IsKnown);
        Assert.Equal(7, all.Value);
        Assert.True(eris.IsKnown);
        Assert.Equal(3, eris.Value);
    }

    [Fact]
    public void Projection_UnlistedQuantityRemainsUnknownUntilEveryRetainerInventoryIsObserved()
    {
        var complete = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var listingsOnly = TestData.Retainer(11, "Nyx");
        MarkInventoryComplete(complete);
        listingsOnly.Listings.Add(new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 2 });
        var projection = BrowserProjectionBuilder.Build(
            [],
            new Dictionary<ulong, CachedRetainer> { [10] = complete, [11] = listingsOnly },
            TestData.Owner);

        Assert.False(projection.GetUnlistedRetainerQuantity(100, BrowserScope.AllKey).IsKnown);
        Assert.True(projection.GetUnlistedRetainerQuantity(100, BrowserScope.RetainerKey(10)).IsKnown);
        Assert.False(projection.GetUnlistedRetainerQuantity(100, BrowserScope.RetainerKey(11)).IsKnown);
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

        var invalid = controller.QueryItems(projection, "ownership.quantity:", isEditing: true);

        Assert.False(invalid.Filter.IsValid);
        Assert.True(invalid.Filter.ShowingLastValid);
        Assert.Equal([100U], invalid.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void ValidIntermediateEdit_UpdatesRowsWhileTyping()
    {
        var projection = BrowserProjectionBuilder.Build([], new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3), (200, "Spruce Log", 3)),
        }, TestData.Owner);
        var controller = new BrowserQueryController();

        Assert.Equal([100U], controller.QueryItems(projection, "darksteel").Items.Select(item => item.ItemId));

        var editing = controller.QueryItems(projection, "spruce", isEditing: true);

        Assert.True(editing.Filter.IsValid);
        Assert.Equal([200U], editing.Items.Select(item => item.ItemId));
        Assert.Equal(2, controller.ItemCompilationCount);
        Assert.Equal(2, controller.ItemEvaluationCount);

        var committed = controller.QueryItems(projection, "spruce");

        Assert.Equal([200U], committed.Items.Select(item => item.ItemId));
        Assert.Equal(2, controller.ItemCompilationCount);
        Assert.Equal(2, controller.ItemEvaluationCount);
    }

    [Fact]
    public void DataChange_ReevaluatesCurrentCompilationDuringEdit()
    {
        var retainer = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3), (200, "Spruce Log", 3));
        var controller = new BrowserQueryController();
        var initial = BrowserProjectionBuilder.Build(
            [Bag((100, "Darksteel Ore", 2))],
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner);

        Assert.Equal([100U], controller.QueryItems(initial, "ownership.quantity>=5").Items.Select(item => item.ItemId));

        retainer.Bags.Single().Items.Single(item => item.ItemId == 100).Quantity = 1;
        var changed = BrowserProjectionBuilder.Build(
            [Bag((100, "Darksteel Ore", 2))],
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner);
        var editing = controller.QueryItems(changed, "spruce", isEditing: true, revision: 2);

        Assert.Equal([200U], editing.Items.Select(item => item.ItemId));
        Assert.Equal(2, controller.ItemCompilationCount);
        Assert.Equal(2, controller.ItemEvaluationCount);
    }

    [Fact]
    public void FirstValidQueryWhileEditing_ProducesResults()
    {
        var projection = BrowserProjectionBuilder.Build([], new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3)),
        }, TestData.Owner);
        var controller = new BrowserQueryController();

        var editing = controller.QueryItems(projection, "darksteel", isEditing: true);

        Assert.Equal([100U], editing.Items.Select(item => item.ItemId));
        Assert.Equal(1, controller.ItemCompilationCount);
        Assert.Equal(1, controller.ItemEvaluationCount);
    }

    [Fact]
    public void ListingsUpdateRowsWhileTyping()
    {
        var retainer = TestData.Retainer(10, "Eris");
        retainer.Listings.Add(new CachedMarketListing { ItemId = 100, ItemName = "Darksteel Ore", Quantity = 2, UnitPrice = 40 });
        retainer.Listings.Add(new CachedMarketListing { ItemId = 200, ItemName = "Spruce Log", Quantity = 1, UnitPrice = 20 });
        var projection = BrowserProjectionBuilder.Build(
            [],
            new Dictionary<ulong, CachedRetainer> { [10] = retainer },
            TestData.Owner);
        var controller = new BrowserQueryController();

        Assert.Equal([100U], controller.QueryListings(projection, "darksteel").Listings.Select(item => item.ItemId));

        var editing = controller.QueryListings(projection, "spruce", isEditing: true);

        Assert.Equal([200U], editing.Listings.Select(item => item.ItemId));
        Assert.Equal(2, controller.ListingCompilationCount);
        Assert.Equal(2, controller.ListingEvaluationCount);

        Assert.Equal([200U], controller.QueryListings(projection, "spruce").Listings.Select(item => item.ItemId));
        Assert.Equal(2, controller.ListingCompilationCount);
        Assert.Equal(2, controller.ListingEvaluationCount);
    }

    [Fact]
    public void ContextChange_DoesNotReuseLastValidResults()
    {
        var projection = BrowserProjectionBuilder.Build([], new Dictionary<ulong, CachedRetainer>
        {
            [10] = TestData.Retainer(10, "Eris", (100, "Darksteel Ore", 3)),
            [11] = TestData.Retainer(11, "Nyx", (200, "Spruce Log", 3)),
        }, TestData.Owner);
        var controller = new BrowserQueryController();

        Assert.Equal(
            [100U],
            controller.QueryItems(projection, "darksteel", BrowserScope.RetainerKey(10)).Items.Select(item => item.ItemId));

        var invalid = controller.QueryItems(
            projection,
            "ownership.quantity:",
            BrowserScope.RetainerKey(11),
            isEditing: true);

        Assert.False(invalid.Filter.IsValid);
        Assert.False(invalid.Filter.ShowingLastValid);
        Assert.Empty(invalid.Items);
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

    private static void MarkInventoryComplete(CachedRetainer retainer)
    {
        retainer.ObservedSources =
        [
            "RetainerPage1",
            "RetainerPage2",
            "RetainerPage3",
            "RetainerPage4",
            "RetainerPage5",
            "RetainerPage6",
            "RetainerPage7",
        ];
    }
}
