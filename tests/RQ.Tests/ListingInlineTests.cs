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
    private static ListingPlanDraft Draft(params ListingPlanAssignment[] assignments) => new() { Owner = Owner, PlanId = Guid.NewGuid(), IsNew = true, Assignments = assignments.ToList() };
    private static ListingRetainerRow View(ListingPlanDraft draft, BrowserProjection browser, Func<uint, bool?>? listable = null, IReadOnlySet<uint>? saved = null) =>
        ListingRetainerProjection.Build(draft, browser, new HashSet<ulong> { 10, 11 }, _ => 999, listable ?? (_ => true), saved).First(r => r.RetainerId == 10);

    [Theory]
    [InlineData("", 20, 0)] [InlineData("abc", 20, 0)] [InlineData("0", 20, 1)]
    [InlineData("1,234", 999, 999)] [InlineData("9", 3, 3)] [InlineData("3", 0, 0)]
    [InlineData("999999999999999999999999999999", 20, 20)]
    public void InputClampsWithoutOverflow(string text, int max, int expected) => Assert.Equal(expected, ListingPlanInput.Commit(text, max));

    [Fact]
    public void EmptyPlanShowsEveryRetainerAndTwentyFreeCells()
    {
        var rows = ListingRetainerProjection.Build(Draft(), Browser(), new HashSet<ulong> { 10, 11 }, _ => 1, _ => true);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal("—", r.State.Text); Assert.Equal(20, r.Cells.Count); Assert.All(r.Cells, c => Assert.Equal(SlotCellKind.Free, c.Kind)); });
    }

    [Fact]
    public void PriceDifferenceOccupiesPlannedSlotOnlyOnce()
    {
        var row = View(Draft(A(2)), Browser([L(price: 101)], [S(1)]));
        Assert.Equal("List 1", row.PlanRows.Single().State.Text);
        Assert.Equal(1, row.Cells.Count(c => c.Kind == SlotCellKind.Differs));
        Assert.Equal(1, row.Cells.Count(c => c.Kind == SlotCellKind.Planned));
        Assert.Equal(2, row.PlannedSlots + row.UnplannedSlots);
        Assert.Equal("Price differs", row.ObservedGroups.Single().State.Text);
    }

    [Theory]
    [InlineData(1, "Shape differs")] [InlineData(50, "List 1")]
    public void SplitNeededDoesNotClaimAnUnfillableListing(int held, string expected)
    {
        var row = View(Draft(A(2, 50)), Browser([L(99)], [S(held)]));
        Assert.Equal(expected, row.PlanRows.Single().State.Text);
    }

    [Fact]
    public void SharedShortageIsCountedOnceAndSiblingDoesNotSayList()
    {
        var row = View(Draft(A(), A(quantity: 99)), Browser());
        Assert.Equal("Missing 100", row.State.Text);
        Assert.Equal("Missing 100", row.PlanRows[0].State.Text);
        Assert.Equal("1 unlisted", row.PlanRows[1].State.Text);
    }

    [Fact]
    public void UndersizedListingShowsMissingUnitsAndShapeEvidence()
    {
        var row = View(Draft(A(quantity: 99)), Browser([L()]));
        Assert.Equal("Missing 98", row.State.Text);
        Assert.Equal("Shape differs", row.ObservedGroups.Single().State.Text);
    }

    [Fact]
    public void PlannedCapacityAndUnplannedOccupancyRemainSeparate()
    {
        var row = View(Draft(A(18)), Browser(Enumerable.Range(0, 4).Select(n => L(item: 2, slot: n)).ToArray()));
        Assert.Equal(18, row.PlannedSlots); Assert.Equal(4, row.UnplannedSlots);
        Assert.Equal(20, row.Cells.Count); Assert.Equal(18, row.Cells.Count(c => c.Kind == SlotCellKind.Planned));
    }

    [Fact]
    public void UnknownListingsAreNotEmptyListings()
    {
        var row = View(Draft(A(3)), Browser(observed: false));
        Assert.Equal("Listings unknown", row.State.Text);
        Assert.Equal(3, row.Cells.Count(c => c.Kind == SlotCellKind.PlannedUnknown));
    }

    [Fact]
    public void AdoptionReservesUnknownPriceAndCompletionRematchesExactSibling()
    {
        var unknown = L(price: null, slot: 1);
        var a = ListingPlanCatalog.AssignmentFromListings([unknown]);
        var browser = Browser([L(), unknown]);
        var row = View(Draft(a), browser);
        Assert.Equal("Needs price", row.PlanRows.Single().State.Text);
        Assert.Equal(100m, row.ObservedGroups.Single().UnitPrice.Value);
        a.UnitPrice = 100;
        row = View(Draft(a), browser);
        Assert.Equal("On plan", row.PlanRows.Single().State.Text);
        Assert.False(row.ObservedGroups.Single().UnitPrice.IsKnown);
        Assert.Throws<ArgumentException>(() => ListingPlanCatalog.AssignmentFromListings([L(), unknown]));
    }

    [Fact]
    public void UnavailableRetainerRemainsVisibleAndDisabledRowsDoNotContribute()
    {
        var a = A(retainer: 99);
        var rows = ListingRetainerProjection.Build(Draft(a), Browser(), new HashSet<ulong> { 10, 11 }, _ => 1, _ => true);
        Assert.Equal("Retainer unavailable", rows.Last().State.Text);
        a.Enabled = false;
        Assert.Equal(0, View(Draft(a), Browser()).PlannedSlots);
    }

    [Fact]
    public void UnknownMetadataIsNotNotSellableButPickerNeverOffersIt()
    {
        var a = A();
        Assert.Equal("Not sellable", View(Draft(a), Browser(), _ => false).PlanRows.Single().State.Text);
        Assert.Equal("—", View(Draft(a), Browser(), _ => false).State.Text);
        Assert.Equal("Missing 1", View(Draft(a), Browser(), _ => null).PlanRows.Single().State.Text);
        Assert.Empty(ListingWorkspace.BuildAddChoices("Item", 10, Browser(stock: [S(1)]), [new(1, "Item 1", "Item 1")], _ => false, _ => null));
    }

    [Fact]
    public void SavedDemandWithoutOwnedStockIsAMovementDestination()
    {
        var row = View(Draft(A()), Browser(), saved: new HashSet<uint> { 1 });
        Assert.Equal(ListingStateAction.FocusStock, row.PlanRows.Single().State.Action);
        Assert.Equal(ListingStateAction.None, View(Draft(A()), Browser()).PlanRows.Single().State.Action);
    }

    [Fact]
    public void PickerOffersMoreThanEightQualityExpandedChoicesAndExactId()
    {
        var catalog = Enumerable.Range(1, 9).Select(id => new ItemChoice((uint)id, "Duplicate", "Duplicate")).ToArray();
        var choices = ListingWorkspace.BuildAddChoices("Duplicate", 10, Browser(), catalog, _ => true, _ => true);
        Assert.Equal(18, choices.Count);
        Assert.All(choices, c => Assert.Contains("#", c.Label));
        Assert.Equal(2, ListingWorkspace.BuildAddChoices("#9", 10, Browser(), catalog, _ => true, _ => true).Count);
        Assert.Equal(9, ListingWorkspace.BuildAddChoices("Duplicate", 10, Browser(), catalog, _ => false, _ => true).Count);
    }

    [Fact]
    public void EvaluatorMatchesPreservePhysicalOrderAndAssignmentIdentity()
    {
        var a = A(2);
        var plan = new ListingPlan { Owner = Owner, Assignments = [a] };
        var evaluated = ListingPlanEvaluator.Evaluate(plan, Browser([L(price: 101, slot: 1), L(slot: 0), L(item: 2, slot: 2)]));
        var matches = evaluated.Find(1, ItemQualityPolicy.NqOnly)!.Matches;
        Assert.Equal(ListingMatchKind.Exact, matches[0].Kind);
        Assert.Equal(ListingMatchKind.PriceDiffers, matches[1].Kind);
        Assert.All(matches, m => Assert.Equal(a.Id, m.AssignmentId));
        Assert.Equal(ListingMatchKind.Unplanned, evaluated.Find(2, ItemQualityPolicy.NqOnly)!.Matches.Single().Kind);
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
    public void SessionImmediateEditsSaveReloadAndDiscard()
    {
        using var f = new Fixture();
        var a = f.Add(3);
        Assert.True(f.Session.IsDirty); Assert.Equal(1, f.Session.ChangeCount);
        f.Session.Save(f.Runtime());
        Assert.Null(f.Session.Error); Assert.False(f.Session.IsDirty);
        var reloaded = new StateRepository(new QuartermasterStateStore(f.PathToState));
        Assert.Equal(3, reloaded.Snapshot().ListingPlans.Single().Assignments.Single().ListingCount);
        f.Session.SetUnitPrice(a.Id, "200"); f.Session.Discard(f.Runtime());
        Assert.Equal(100, f.Session.Draft!.Assignments.Single().UnitPrice);
    }

    [Fact]
    public void NewPlanRaceMergesIntentWithoutPersistingUntilNextSave()
    {
        using var f = new Fixture(); var a = f.Add();
        var external = A(item: 2);
        f.Session.BeforeSaveMutate = () => f.Repository.Mutate(s => s.ListingPlans.Add(new ListingPlan { Owner = Owner, Assignments = [external] }));
        f.Session.Save(f.Runtime());
        Assert.Null(f.Session.Error); Assert.True(f.Session.MergedElsewhere);
        Assert.Equal(2, f.Session.Draft!.Assignments.Count);
        Assert.Single(f.Repository.Snapshot().ListingPlans.Single().Assignments);
        f.Session.BeforeSaveMutate = null; f.Session.Save(f.Runtime());
        Assert.Equal(2, f.Repository.Snapshot().ListingPlans.Single().Assignments.Count);
        Assert.Contains(f.Session.Draft.Assignments, x => x.Id == a.Id);
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

    [Fact]
    public void NonCollidingOverCapacityMergePreservesBothSides()
    {
        using var f = new Fixture(); f.Add(16); f.Session.Save(f.Runtime()); f.Add(3);
        f.Repository.Mutate(s => { s.ListingPlans[0].Assignments.Add(A(3, item: 2)); s.ListingPlans[0].Revision++; });
        f.Session.Save(f.Runtime());
        Assert.Equal(22, f.Session.Draft!.Assignments.Sum(a => a.ListingCount));
        Assert.Equal(19, f.Repository.Snapshot().ListingPlans[0].Assignments.Sum(a => a.ListingCount));
        Assert.Contains("22 / 20", f.Session.SaveBlockedTooltip(f.Runtime()));
    }

    [Fact]
    public void WriteFailurePreservesDraftAndCanonicalStateAndRetrySucceeds()
    {
        using var f = new Fixture(); f.Add(); var before = File.ReadAllBytes(f.PathToState);
        using (File.Open(f.PathToState, FileMode.Open, FileAccess.Read, FileShare.Read)) f.Session.Save(f.Runtime());
        Assert.StartsWith("Listing Plan not saved:", f.Session.Error);
        Assert.True(f.Session.IsDirty); Assert.Empty(f.Repository.Snapshot().ListingPlans);
        Assert.Equal(before, File.ReadAllBytes(f.PathToState));
        f.Session.Save(f.Runtime()); Assert.Null(f.Session.Error); Assert.Single(f.Repository.Snapshot().ListingPlans);
    }

    [Fact]
    public void TransientOwnerLossKeepsDraftButDifferentOwnerReplacesIt()
    {
        using var f = new Fixture(); var a = f.Add();
        f.Session.Ensure(f.Runtime() with { Owner = new() }); Assert.Equal(a.Id, f.Session.Draft!.Assignments[0].Id);
        f.Session.Ensure(f.Runtime()); Assert.True(f.Session.IsDirty);
        f.Session.Ensure(f.Runtime() with { Owner = Owner with { LocalContentId = 999 } }); Assert.Empty(f.Session.Draft!.Assignments);
    }

    [Fact]
    public void CapacityIgnoresUnplannedAndEnablingNormalizesLegacyValues()
    {
        using var f = new Fixture(); var a = f.Add(17); var b = f.Add();
        f.Session.SetListingCount(b.Id, "9"); Assert.Equal(3, b.ListingCount);
        f.Session.SetEnabled(a.Id, false, Browser()); a.QuantityPerListing = -1; a.UnitPrice = int.MaxValue;
        f.Session.SetEnabled(a.Id, true, Browser()); Assert.Equal(1, a.QuantityPerListing); Assert.Equal(999999999, a.UnitPrice);
        Assert.False(f.Session.CanAdopt([L()], new HashSet<ulong>()));
    }

    [Fact]
    public void PriceEchoRequiresCompletedNonzeroEditAndOnlyChangesEligibleSiblings()
    {
        using var f = new Fixture(); var a = f.Add();
        var b = f.Session.Add(11, "Retainer 11", 1, "Item 1", ItemQualityPolicy.NqOnly, Browser());
        f.Session.SetListingCount(b.Id, "1"); f.Session.SetUnitPrice(b.Id, "100");
        f.Session.SetUnitPrice(a.Id, "");
        f.Session.EndUnitPriceEdit(a.Id, 100);
        Assert.Null(f.Session.PriceEchoAssignmentId);
        f.Session.SetUnitPrice(a.Id, "200");
        Assert.Null(f.Session.PriceEchoAssignmentId);
        f.Session.EndUnitPriceEdit(a.Id, 0);
        Assert.Equal(a.Id, f.Session.PriceEchoAssignmentId);
        f.Session.ApplyPriceToSiblings(a.Id, new HashSet<ulong> { 10, 11 });
        Assert.Equal(200, b.UnitPrice);
        Assert.Null(f.Session.PriceEchoAssignmentId);
    }

    [Fact]
    public void CleanDraftReloadsExternalRevisionButDirtyDraftKeepsIntent()
    {
        using var f = new Fixture(); var a = f.Add(); f.Session.Save(f.Runtime());
        f.Repository.Mutate(s => { s.ListingPlans[0].Assignments[0].UnitPrice = 150; s.ListingPlans[0].Revision++; });
        f.Session.Ensure(f.Runtime());
        Assert.Equal(150, f.Session.Draft!.Assignments[0].UnitPrice);
        f.Session.SetUnitPrice(a.Id, "200");
        f.Repository.Mutate(s => { s.ListingPlans[0].Assignments[0].UnitPrice = 300; s.ListingPlans[0].Revision++; });
        f.Session.Ensure(f.Runtime());
        Assert.Equal(200, f.Session.Draft.Assignments[0].UnitPrice);
        Assert.Equal(150, f.Session.Draft.BaselineAssignments[0].UnitPrice);
    }

    [Fact]
    public void UnavailableAssignmentCanBeDisabledToUnblockSave()
    {
        using var f = new Fixture(); var a = f.Add();
        var runtime = f.Runtime() with { Retainers = new Dictionary<ulong, CachedRetainer>() };
        Assert.Contains("retainer unavailable", f.Session.SaveBlockedTooltip(runtime));
        f.Session.SetEnabled(a.Id, false, runtime.Browser);
        Assert.Empty(f.Session.Issues(runtime));
        f.Session.Save(runtime);
        Assert.Null(f.Session.Error);
        Assert.False(f.Repository.Snapshot().ListingPlans[0].Assignments[0].Enabled);
    }

    [Fact]
    public void StockIncludesSavedDemandButNeverDraftOnlyDemand()
    {
        using var f = new Fixture(); f.Add();
        Assert.Empty(StockWorkspace.StockItemsWithListingDemand(f.Runtime(), BrowserScope.AllKey));
        f.Session.Save(f.Runtime());
        Assert.Equal(1u, Assert.Single(StockWorkspace.StockItemsWithListingDemand(f.Runtime(), BrowserScope.AllKey)).ItemId);
    }

    [Fact]
    public void UnknownPriceAdoptionRemovesTheSecondAdoptAction()
    {
        using var f = new Fixture(); var browser = Browser([L(price: null)]);
        f.Session.Ensure(f.Runtime(browser));
        Assert.False(f.Session.IsDirty);
        f.Session.Adopt(browser.Listings, new HashSet<ulong> { 10, 11 });
        Assert.Single(f.Session.Draft!.Assignments);
        Assert.Empty(View(f.Session.Draft, browser).ObservedGroups);
        f.Session.SetUnitPrice(f.Session.Draft.Assignments[0].Id, "200");
        Assert.Equal("Price unknown", View(f.Session.Draft, browser).PlanRows.Single().State.Text);
        Assert.DoesNotContain(View(f.Session.Draft, browser).ObservedGroups, g => g.Kind == ListingMatchKind.Unplanned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmergedValidationFailureCarriesNoRebasePayload(bool existing)
    {
        var plan = new ListingPlan { Owner = Owner, Assignments = [A()] };
        var document = new QuartermasterState();
        if (existing) document.ListingPlans.Add(plan);
        var draft = ListingPlanCatalog.Draft(document, Owner, Browser());
        if (!existing) draft.Assignments.Add(A());
        draft.Assignments[0].UnitPrice = 0;
        var error = Assert.Throws<ListingPlanValidationException>(() =>
            ListingPlanCatalog.Apply(document, Owner, draft, _ => 999, new HashSet<ulong> { 10, 11 }, DateTime.UtcNow));
        Assert.Null(error.Candidate); Assert.Null(error.CanonicalAssignments); Assert.Equal(0, error.CanonicalRevision);
        Assert.Equal(existing ? 1 : 0, document.ListingPlans.Count);
        if (existing) Assert.Equal(100, document.ListingPlans[0].Assignments[0].UnitPrice);
    }
}
