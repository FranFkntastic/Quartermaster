using System.Collections.Immutable;
using System.Text.Json;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

/// <summary>
/// Courier lines place their deliveries the way Transfer lines place stowage: a ranked list of
/// destination retainers, then Any space, else the destination's bags.
/// </summary>
public sealed class CourierPlacementParityTests
{
    static OwnerScope Who(ulong id) => new() { LocalContentId = id, HomeWorldId = 1, CharacterName = $"Character {id}" };
    readonly OwnerScope source = Who(1), destination = Who(2), carrier = Who(3);
    readonly StowagePlan plan = new() { Owner = Who(1), Kind = StowagePlanKind.Courier };
    readonly CourierSettings cfg;
    static readonly MeetingBell Bell = new(1, 1, 1, 0, 0, 0, "Fixture bell");

    public CourierPlacementParityTests()
    {
        cfg = new()
        {
            Carrier = carrier, LocalRoster = [source, destination], PairedOwner = source, CarrierOptIn = true,
            Bells = ImmutableDictionary<ulong, MeetingBell>.Empty.Add(1, Bell).Add(2, Bell),
        };
    }

    TargetPlanItem Line(int quantity, params ulong[] to) => new()
    {
        StowagePlanId = plan.Id,
        ItemId = 1,
        ItemName = "Titanium Ore",
        TargetQuantity = quantity,
        Quality = ItemQualityPolicy.NqOnly,
        Courier = new(destination, 11),
        Routing = new() { PreferredRetainerIds = [.. to] },
    };

    /// <summary>Source retainer 11 holds 25; destination retainers 21, 22 and 23 have the given free slots.</summary>
    CourierPlan Build(TargetPlanItem line, int free21 = 1, int free22 = 1, int free23 = 5, int bagFree = 10) =>
        CourierPlanner.Build(plan, [line],
            [
                new(source, null, new(source, 10, [])),
                new(source, 11, new(source, 0, [new(1, false, 25)])),
                new(destination, null, new(destination, bagFree, [])),
                new(destination, 21, new(destination, free21, [])),
                new(destination, 22, new(destination, free22, [])),
                new(destination, 23, new(destination, free23, [])),
            ],
            new(carrier, 10, []), cfg,
            id => new ItemMetadata(id, "Titanium Ore", null, false, null, null, null, null, null, null, null, false, true, null, null, 10));

    static IEnumerable<(ulong? To, int Units)> Landings(CourierPlan plan) =>
        plan.Trips.SelectMany(trip => trip.Manifest).GroupBy(stack => stack.To).Select(group => (group.Key, group.Sum(stack => stack.Units)));

    [Fact]
    public void RankOneFull_SpillsToRankTwo_ThenAnySpace()
    {
        var line = Line(25, 21, 22);
        line.Routing.AnySpace = true;
        var built = Build(line);
        Assert.Empty(built.Refusals);
        Assert.Equal([((ulong?)21, 10), (22, 10), (23, 5)], Landings(built));
    }

    [Fact]
    public void RankOneWithRoom_TakesEverything()
    {
        var built = Build(Line(25, 23, 21));
        Assert.Empty(built.Refusals);
        Assert.Equal([((ulong?)23, 25)], Landings(built));
    }

    [Fact]
    public void NoDestinations_LandInTheDestinationsBags()
    {
        var built = Build(Line(25));
        Assert.Empty(built.Refusals);
        Assert.Equal([((ulong?)null, 25)], Landings(built));
    }

    [Fact]
    public void AnySpaceAlone_FillsRetainersInDisplayOrder()
    {
        var line = Line(25);
        line.Routing.AnySpace = true;
        Assert.Equal([((ulong?)21, 10), (22, 10), (23, 5)], Landings(Build(line)));
    }

    // Refused only when the whole chain is short: the line then delivers nothing.
    [Fact]
    public void WholeChainShort_RefusesDestinationFull()
    {
        var built = Build(Line(25, 21, 22));
        Assert.Equal("DestinationFull", Assert.Single(built.Refusals).Code);
        Assert.Empty(built.Trips);
    }

    [Fact]
    public void UnobservedListedRetainer_IsSkipped_AndNoneObservedIsDestinationUnknown()
    {
        Assert.Equal([((ulong?)23, 25)], Landings(Build(Line(25, 99, 23))));
        var unknown = Build(Line(25, 99));
        Assert.Equal("DestinationUnknown", Assert.Single(unknown.Refusals).Code);
    }

    [Fact]
    public void Consolidation_IsPlannedForRankOneOfDeliveringLinesOnly()
    {
        var line = Line(25, 23);
        line.Routing.Consolidate = true;
        var gather = Assert.Single(Build(line).Consolidations);
        Assert.Equal((line.Id, 23ul, 10), (gather.Line, gather.Retainer, gather.StackSize));
        Assert.True(gather.Destination.Matches(destination));

        var refused = Line(25, 21);
        refused.Routing.Consolidate = true;
        Assert.Empty(Build(refused).Consolidations);
    }

    [Fact]
    public void Destinations_FollowTransferPlacementOrder()
    {
        ulong[] observed = [21, 22, 23];
        Assert.Equal([(ulong?)null], CourierPlanner.Destinations(new(), observed));
        Assert.Equal([(ulong?)22, 21], CourierPlanner.Destinations(new() { PreferredRetainerIds = [22, 21] }, observed));
        Assert.Equal([(ulong?)22, 21, 23], CourierPlanner.Destinations(new() { PreferredRetainerIds = [22], AnySpace = true }, observed));
        Assert.Empty(CourierPlanner.Destinations(new() { PreferredRetainerIds = [99] }, observed));
    }

    // ---- migration ------------------------------------------------------------------------

    [Fact]
    public void LegacyRetainer_ReadsFromJson_AndIsNeverWrittenOnceMigrated()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string legacy = """{"character":{"localContentId":2,"homeWorldId":1,"characterName":"Character 2"},"retainerId":77,"sourceRetainerId":11}""";
        var route = JsonSerializer.Deserialize<CourierRoute>(legacy, options)!;
        Assert.Equal(77ul, route.RetainerId);
        Assert.Equal(11ul, route.SourceRetainerId);
        Assert.Null(route.SourceCharacter);

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(route with { RetainerId = null }, options));
        Assert.False(written.RootElement.TryGetProperty("retainerId", out _));
        Assert.Equal(11ul, written.RootElement.GetProperty("sourceRetainerId").GetUInt64());
    }

    [Fact]
    public void LegacyRetainer_BecomesRankOne_AndLinesThatNowCollideMerge()
    {
        var state = new QuartermasterState();
        state.StowagePlans.Add(plan);
        var revision = plan.Revision;
        var first = Line(4);
        first.Courier = first.Courier! with { RetainerId = 77 };
        var second = Line(6, 88);
        second.Routing.Consolidate = true;
        second.Courier = second.Courier! with { RetainerId = 77 };
        var bags = Line(5);
        bags.ItemId = 2;
        var modern = Line(3, 66);
        modern.ItemId = 3;
        state.PlanItems.AddRange([first, second, bags, modern]);

        Assert.True(StowagePlanMigration.MoveCourierRetainersToRouting(state));

        Assert.Equal([first.Id, bags.Id, modern.Id], state.PlanItems.Select(rule => rule.Id));
        Assert.All(state.PlanItems, rule => Assert.Null(rule.Courier!.RetainerId));
        Assert.Equal(10, first.TargetQuantity);
        Assert.Equal([77ul, 88ul], first.Routing.PreferredRetainerIds);
        Assert.True(first.Routing.Consolidate);
        Assert.Empty(bags.Routing.PreferredRetainerIds);
        Assert.Equal([66ul], modern.Routing.PreferredRetainerIds);
        Assert.True(plan.Revision > revision);

        var settled = plan.Revision;
        Assert.False(StowagePlanMigration.MoveCourierRetainersToRouting(state));
        Assert.Equal(settled, plan.Revision);
    }

    [Fact]
    public void Repository_MigratesLegacyRetainersOnLoad()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "quartermaster.json");
        var store = new QuartermasterStateStore(path);
        var state = new QuartermasterState();
        state.StowagePlans.Add(plan);
        var line = Line(4);
        line.Courier = line.Courier! with { RetainerId = 77 };
        state.PlanItems.Add(line);
        store.Save(state);
        Assert.Contains("77", File.ReadAllText(path));

        _ = new StateRepository(store);

        var reloaded = Assert.Single(new QuartermasterStateStore(path).Load().PlanItems);
        Assert.Equal(line.Id, reloaded.Id);
        Assert.Null(reloaded.Courier!.RetainerId);
        Assert.Equal([77ul], reloaded.Routing.PreferredRetainerIds);
        Assert.Equal(11ul, reloaded.Courier.SourceRetainerId);
    }
}
