using System.Text.Json;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.UI;

namespace RQ.Tests;

public sealed class ScratchTransferPlanTests
{
    private static readonly OwnerScope Owner = new() { LocalContentId = 100, HomeWorldId = 93 };
    private static readonly OwnerScope Other = new() { LocalContentId = 200, HomeWorldId = 93 };

    [Fact]
    public void ExactlyOneScratchPerOwnerAndNoAutomaticEnable()
    {
        var state = new QuartermasterState();
        var first = StowagePlanCatalog.EnsureScratch(state, Owner);
        Assert.Same(first, StowagePlanCatalog.EnsureScratch(state, Owner));
        Assert.NotEqual(first.Id, StowagePlanCatalog.EnsureScratch(state, Other).Id);
        Assert.Equal(2, state.StowagePlans.Count);
        Assert.False(first.Enabled);
        Assert.Empty(StowagePlanCatalog.OwnerPlans(state, Owner));
        Assert.Throws<InvalidOperationException>(() => StowagePlanCatalog.EnsureScratch(state, new()));
    }

    [Fact]
    public void LegacyJsonDefaultsToSavedWithoutRewritingIdentityOrRules()
    {
        var state = new QuartermasterState();
        var saved = StowagePlanCatalog.Create(state, Owner, "Existing");
        var draft = StowagePlanCatalog.Draft(state, Owner, saved.Id);
        draft.Rules.Add(new() { ItemId = 1, TargetQuantity = 7 });
        StowagePlanCatalog.Apply(state, Owner, draft);
        var json = JsonSerializer.Serialize(state, AtomicDocumentStore<QuartermasterState>.JsonOptions)
            .Replace("\"isEphemeral\": false,", "");
        var loaded = JsonSerializer.Deserialize<QuartermasterState>(json, AtomicDocumentStore<QuartermasterState>.JsonOptions)!;
        var before = JsonSerializer.Serialize(loaded.StowagePlans[0]);
        StowagePlanCatalog.EnsureScratch(loaded, Owner);
        Assert.Equal(before, JsonSerializer.Serialize(StowagePlanCatalog.OwnerPlans(loaded, Owner).Single()));
        Assert.Equal(saved.Id, loaded.StowagePlans[0].Id);
        Assert.Equal(7, Assert.Single(loaded.PlanItems).TargetQuantity);
    }

    [Fact]
    public void ScratchSurvivesReloadAndPromotionPreservesRulesAndReplacesScratch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            var repository = new StateRepository(new(path));
            var session = new TransferPlanDraftSession(repository);
            session.Ensure(Owner, null);
            var id = session.Draft!.PlanId;
            session.Add(1, "Item", ItemQualityPolicy.Any, 12);
            Assert.True(session.Save(Owner), session.Error);
            var reloaded = new StateRepository(new(path));
            var loaded = reloaded.Snapshot();
            Assert.Equal(id, StowagePlanCatalog.EnsureScratch(loaded, Owner).Id);
            var ruleId = Assert.Single(loaded.PlanItems).Id;
            reloaded.Mutate(s => StowagePlanCatalog.PromoteScratch(s, Owner, id, "Glamour"));
            var promoted = new StateRepository(new(path)).Snapshot();
            Assert.Equal(id, Assert.Single(StowagePlanCatalog.OwnerPlans(promoted, Owner)).Id);
            Assert.Equal(ruleId, Assert.Single(promoted.PlanItems).Id);
            var replacement = Assert.Single(promoted.StowagePlans, p => p.IsEphemeral);
            Assert.NotEqual(id, replacement.Id);
            Assert.DoesNotContain(promoted.PlanItems, r => r.StowagePlanId == replacement.Id);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void PromotionRejectsOtherOwnerAndBlankNameWithoutChangingScratch()
    {
        var state = new QuartermasterState();
        var scratch = StowagePlanCatalog.EnsureScratch(state, Owner);
        Assert.Throws<InvalidOperationException>(() => StowagePlanCatalog.PromoteScratch(state, Other, scratch.Id, "Wrong"));
        Assert.Throws<InvalidOperationException>(() => StowagePlanCatalog.PromoteScratch(state, Owner, scratch.Id, " "));
        Assert.True(scratch.IsEphemeral);
        Assert.Single(state.StowagePlans);
    }

    [Fact]
    public void ScratchExcludedFromSavedRulesAndEnabledEvaluationButExplicitlyEvaluable()
    {
        var state = new QuartermasterState();
        var scratch = StowagePlanCatalog.EnsureScratch(state, Owner);
        var draft = StowagePlanCatalog.Draft(state, Owner, scratch.Id);
        draft.Rules.Add(new() { StowagePlanId = scratch.Id, ItemId = 1, TargetQuantity = 5 });
        StowagePlanCatalog.Apply(state, Owner, draft);
        var stock = new BrowserProjection { Owner = Owner, Scopes = [], Items = [], Listings = [] };
        Assert.Empty(StowagePlanMigration.OwnerRules(state, Owner, false));
        Assert.Empty(StowageEvaluator.Build(state, stock, Owner));
        Assert.Empty(ListingPlanEvaluator.ComposeOwnerRules(state, stock, Owner));
        Assert.Equal(5, Assert.Single(StowageEvaluator.BuildPlan(state, stock, Owner, scratch.Id)!.Lines).RetrieveQuantity);
    }

    [Fact]
    public void ScratchCannotAcquireListingDemandLinks()
    {
        var state = new QuartermasterState();
        var scratch = StowagePlanCatalog.EnsureScratch(state, Owner);
        var listing = new ListingPlan { Owner = Owner };
        state.ListingPlans.Add(listing);
        Assert.Throws<InvalidOperationException>(() =>
            ListingPlanCatalog.Link(state, Owner, scratch.Id, listing.Id, 1, ItemQualityPolicy.NqOnly));
        Assert.Empty(state.TransferPlanListingLinks);
    }

    [Fact]
    public void NoSavedSelectionDefaultsToScratchEvenWhenSavedPlansExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-scratch-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new StateRepository(new(Path.Combine(directory, "state.json")));
            var saved = repository.Mutate(s => StowagePlanCatalog.Create(s, Owner, "Saved"));
            var session = new TransferPlanDraftSession(repository);
            session.Ensure(Owner, null);
            var scratchId = session.Draft!.PlanId;
            Assert.NotEqual(saved.Id, scratchId);
            session.Ensure(Owner, saved.Id);
            Assert.Equal(saved.Id, session.Draft!.PlanId);
            session.Ensure(Owner, null);
            Assert.Equal(scratchId, session.Draft!.PlanId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void StockActionsKeepBaselineTargetAndExistingQualityRules()
    {
        var draft = new StowagePlanDraft { PlanId = Guid.NewGuid() };
        StowagePlanCatalog.SetStockRule(draft, 1, "Item", 3, false);
        var rule = Assert.Single(draft.Rules);
        Assert.Equal(3, rule.TargetQuantity);
        rule.TargetQuantity = 10;
        draft.Rules.Add(new() { ItemId = 1, Quality = ItemQualityPolicy.HqOnly, TargetQuantity = 2 });
        StowagePlanCatalog.SetStockRule(draft, 1, "Item", 3, false);
        Assert.Equal(10, rule.TargetQuantity);
        Assert.Equal(2, draft.Rules.Count);
        StowagePlanCatalog.SetStockRule(draft, 1, "Item", 3, true, 42);
        Assert.Equal(0, rule.TargetQuantity);
        Assert.Equal(StowageRoutingMode.HomeFirst, rule.Routing.Mode);
        Assert.Equal(42UL, Assert.Single(rule.Routing.PreferredRetainerIds));
        Assert.Equal(StowageOverflowPolicy.HoldOnPlayer, rule.Routing.Overflow);
        StowagePlanCatalog.SetStockRule(draft, 2, "Other", -3, false);
        Assert.Equal(0, draft.Rules.Last().TargetQuantity);
    }

    [Fact]
    public void ClearScratchDoesNotDeleteSavedRules()
    {
        var state = new QuartermasterState();
        var saved = StowagePlanCatalog.Create(state, Owner, "Saved");
        var scratch = StowagePlanCatalog.EnsureScratch(state, Owner);
        foreach (var plan in new[] { saved, scratch })
        {
            var draft = StowagePlanCatalog.Draft(state, Owner, plan.Id);
            StowagePlanCatalog.SetStockRule(draft, 1, "Item", 1, false);
            StowagePlanCatalog.Apply(state, Owner, draft);
        }
        var clear = StowagePlanCatalog.Draft(state, Owner, scratch.Id);
        clear.Rules.Clear();
        StowagePlanCatalog.Apply(state, Owner, clear);
        Assert.Equal(saved.Id, Assert.Single(state.PlanItems).StowagePlanId);
        Assert.Equal(scratch.Id, StowagePlanCatalog.EnsureScratch(state, Owner).Id);
    }

    [Fact]
    public void ScratchIsNotPublishedInEitherPlanContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rq-scratch-ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new StateRepository(new(Path.Combine(directory, "state.json")));
            repository.Mutate(s => { StowagePlanCatalog.EnsureScratch(s, Owner); StowagePlanCatalog.Create(s, Owner, "Saved"); });
            var publisher = new SnapshotPublisher("fixture", repository, () => new Dictionary<ulong, CachedRetainer>());
            publisher.Refresh(Owner, new PlayerStorageCapture([], [], []));
            using var snapshot = JsonDocument.Parse(publisher.GetSnapshot());
            foreach (var contract in new[] { "transferPlans", "stowagePlans" })
                Assert.Equal("Saved", Assert.Single(snapshot.RootElement.GetProperty(contract).GetProperty("plans").EnumerateArray()).GetProperty("name").GetString());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
