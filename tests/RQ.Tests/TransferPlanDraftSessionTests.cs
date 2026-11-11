using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;
using RQ.UI;
using Franthropy.Filtering.Evaluation;

namespace RQ.Tests;

public sealed class TransferPlanDraftSessionTests
{
    private static readonly OwnerScope Owner = new() { LocalContentId = 100, HomeWorldId = 93 };
    private static readonly OwnerScope Other = new() { LocalContentId = 200, HomeWorldId = 93 };
    private static ListingPlanItemEvaluation Demand(bool known = true) => new(2, "Hull", ItemQualityPolicy.NqOnly,
        20, Evidence.Known(9), Evidence.Known(11), 3, Evidence.Known(8), Evidence.Known(0),
        known ? Evidence.Known(11) : Evidence.Unknown<int>("fixture"), Evidence.Known(8), Evidence.Known(8),
        Evidence.Known(0), ListingCoverageState.Retrievable, [], [], [], []);
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rq-transfer-draft-" + Guid.NewGuid().ToString("N"));
        public string PathToState => Path.Combine(directory, "state.json");
        public StateRepository Repository { get; }
        public TransferPlanDraftSession Session { get; }
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Repository = new(new QuartermasterStateStore(PathToState));
            Session = new(Repository); Session.Ensure(Owner, null);
        }
        public Guid SaveInitial()
        {
            Session.Add(1, "Silver Ingot", ItemQualityPolicy.Any, 30);
            Assert.True(Session.Save(Owner), Session.Error);
            var id = Session.Draft!.PlanId;
            Repository.Mutate(s => StowagePlanCatalog.PromoteScratch(s, Owner, id, "Saved fixture"));
            Session.Forget(id);
            Session.Ensure(Owner, id);
            return id;
        }
        public void External(Action<StowagePlanDraft> edit)
        {
            Repository.Mutate(s => { var d = StowagePlanCatalog.Draft(s, Owner, Session.Draft!.PlanId); edit(d); StowagePlanCatalog.Apply(s, Owner, d); });
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void EmptyOwnerGetsPersistedScratchWithoutUnsavedEdits()
    {
        using var f = new Fixture();
        Assert.NotNull(f.Session.Draft); Assert.False(f.Session.HasChanges);
        Assert.True(Assert.Single(f.Repository.Snapshot().StowagePlans).IsEphemeral);
    }

    [Fact]
    public void LinkOnlyRemovalCanSaveAndDiscardWithoutUnrelatedEdits()
    {
        using var f = new Fixture(); var planId = f.SaveInitial();
        var linkId = f.Repository.Mutate(s =>
        {
            var listing = new ListingPlan { Owner = Owner };
            s.ListingPlans.Add(listing);
            var link = new TransferPlanListingLink { StowagePlanId = planId, ListingPlanId = listing.Id, ItemId = 1 };
            s.TransferPlanListingLinks.Add(link);
            return link.Id;
        });
        Assert.False(f.Session.CanSave(Owner));
        f.Session.Unlink(linkId);
        Assert.True(f.Session.CanSave(Owner));
        Assert.False(f.Session.CanSave(Other));
        Assert.Single(f.Repository.Snapshot().TransferPlanListingLinks);
        f.Session.Discard(Owner);
        Assert.False(f.Session.CanSave(Owner));
        Assert.Single(f.Repository.Snapshot().TransferPlanListingLinks);
        f.Session.Unlink(linkId);
        var savedName = f.Session.Draft!.Name;
        f.Session.Draft!.Name = " ";
        Assert.False(f.Session.CanSave(Owner));
        f.Session.Draft.Name = savedName;
        Assert.True(f.Session.Save(Owner), f.Session.Error);
        var reloaded = new StateRepository(new QuartermasterStateStore(f.PathToState));
        Assert.Empty(reloaded.Snapshot().TransferPlanListingLinks);
        Assert.Equal(30, Assert.Single(reloaded.Snapshot().PlanItems).TargetQuantity);
        Assert.False(f.Session.CanSave(Owner));
    }

    [Fact]
    public void AddSaveReloadAndDiscardAreTransactional()
    {
        using var f = new Fixture();
        f.Session.Add(1, "Silver Ingot", ItemQualityPolicy.Any, 30);
        Assert.True(f.Session.HasChanges); Assert.Empty(f.Repository.Snapshot().PlanItems);
        Assert.True(f.Session.Save(Owner), f.Session.Error);
        var reloaded = new StateRepository(new QuartermasterStateStore(f.PathToState));
        Assert.Equal(30, Assert.Single(reloaded.Snapshot().PlanItems).TargetQuantity);
        f.Session.Draft!.Rules[0].TargetQuantity = 99;
        f.Session.Discard(Owner);
        Assert.Equal(30, f.Session.Draft!.Rules[0].TargetQuantity); Assert.False(f.Session.HasChanges);
    }

    [Fact]
    public void PlanSwitchRetainsBothDirtyDrafts()
    {
        using var f = new Fixture(); var first = f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        var second = f.Repository.Mutate(s => StowagePlanCatalog.Create(s, Owner, "Second").Id);
        f.Session.Ensure(Owner, second); f.Session.Add(2, "Bridge", ItemQualityPolicy.NqOnly, 11);
        f.Session.Ensure(Owner, first); Assert.Equal(45, f.Session.Draft!.Rules[0].TargetQuantity);
        f.Session.Ensure(Owner, second); Assert.Equal(11, Assert.Single(f.Session.Draft!.Rules).TargetQuantity);
        Assert.Equal(30, Assert.Single(f.Repository.Snapshot().PlanItems).TargetQuantity);
    }

    [Fact]
    public void TemporaryOwnerLossRetainsDraftButForeignOwnerCannotSaveIt()
    {
        using var f = new Fixture(); var id = f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        f.Session.Ensure(new(), id);
        Assert.False(f.Session.Save(new())); Assert.False(f.Session.Save(Other));
        f.Session.Ensure(Owner, id); Assert.Equal(45, f.Session.Draft!.Rules[0].TargetQuantity);
        f.Session.Ensure(Other, id); Assert.Empty(f.Session.Draft!.Rules);
        Assert.NotEqual(id, f.Session.Draft.PlanId);
    }

    [Fact]
    public void RepeatedAddsRetainTargetAndQualityIdentity()
    {
        using var f = new Fixture();
        var first = f.Session.Add(1, "Hull", ItemQualityPolicy.NqOnly, 11);
        Assert.Same(first, f.Session.Add(1, "Hull", ItemQualityPolicy.NqOnly, 1));
        f.Session.Add(1, "Hull", ItemQualityPolicy.HqOnly, 3);
        Assert.Equal(2, f.Session.Draft!.Rules.Count); Assert.Equal(11, first.TargetQuantity);
        f.Session.Add(1, "Hull", ItemQualityPolicy.NqOnly, 0, replaceTarget: true);
        Assert.Equal(0, first.TargetQuantity);
    }

    [Fact]
    public void CleanDraftAutomaticallyLoadsExternalRevision()
    {
        using var f = new Fixture(); var id = f.SaveInitial();
        f.External(d => d.Rules[0].TargetQuantity = 44);
        f.Session.Ensure(Owner, id);
        Assert.Equal(44, f.Session.Draft!.Rules[0].TargetQuantity); Assert.False(f.Session.HasChanges);
    }

    [Fact]
    public void IndependentExternalEditsMergeWithoutLosingDraft()
    {
        using var f = new Fixture(); var id = f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        f.External(d => d.Name = "Renamed elsewhere");
        Assert.True(f.Session.Save(Owner), f.Session.Error);
        Assert.Equal("Renamed elsewhere", f.Session.Draft!.Name);
        Assert.Equal(45, f.Session.Draft.Rules[0].TargetQuantity);
        Assert.Equal(id, f.Session.Draft.PlanId);
    }

    [Fact]
    public void CollisionKeepsDraftAndRequiresReviewBeforeRetry()
    {
        using var f = new Fixture(); f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        f.External(d => d.Rules[0].TargetQuantity = 70);
        Assert.False(f.Session.Save(Owner)); Assert.Contains("conflicts", f.Session.Error);
        Assert.Equal(70, f.Repository.Snapshot().PlanItems[0].TargetQuantity);
        Assert.Equal(45, f.Session.Draft!.Rules[0].TargetQuantity);
        Assert.True(f.Session.Save(Owner), f.Session.Error);
        Assert.Equal(45, f.Repository.Snapshot().PlanItems[0].TargetQuantity);
    }

    [Fact]
    public void ConcurrentSameItemAddCannotBeSilentlyDeduplicated()
    {
        using var f = new Fixture(); f.SaveInitial();
        f.Session.Add(2, "Hull", ItemQualityPolicy.NqOnly, 11);
        f.External(d => d.Rules.Add(new() { ItemId = 2, ItemName = "Hull", Quality = ItemQualityPolicy.NqOnly, TargetQuantity = 9 }));
        Assert.False(f.Session.Save(Owner)); Assert.Contains("conflicts", f.Session.Error);
        Assert.Equal(11, Assert.Single(f.Session.Draft!.Rules, r => r.ItemId == 2).TargetQuantity);
        Assert.True(f.Session.Save(Owner), f.Session.Error);
    }

    [Fact]
    public void WriteFailureRetainsDraftAndRetryPersistsIt()
    {
        using var f = new Fixture(); f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        using (var locked = File.Open(f.PathToState, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(f.Session.Save(Owner)); Assert.Equal(45, f.Session.Draft.Rules[0].TargetQuantity);
            Assert.Equal(30, f.Repository.Snapshot().PlanItems[0].TargetQuantity);
        }
        Assert.True(f.Session.Save(Owner), f.Session.Error);
    }

    [Fact]
    public void ExternalDeletionRetainsDraftForExplicitRecovery()
    {
        using var f = new Fixture(); var id = f.SaveInitial();
        f.Repository.Mutate(s => s.StowagePlans.RemoveAll(p => p.Id == id));
        Assert.False(f.Session.Save(Owner)); Assert.Contains("deleted", f.Session.Error);
        Assert.NotEmpty(f.Session.Draft!.Rules);
        f.Session.Forget(id); f.Session.Ensure(Owner, null);
        Assert.NotEqual(id, f.Session.Draft!.PlanId);
    }

    [Fact]
    public void DuplicateQualityRowsFailWithoutDroppingEither()
    {
        using var f = new Fixture(); f.SaveInitial();
        f.Session.Draft!.Rules.Add(new() { ItemId = 1, ItemName = "Silver Ingot", TargetQuantity = 40 });
        Assert.False(f.Session.Save(Owner)); Assert.Equal(2, f.Session.Draft.Rules.Count);
        Assert.Single(f.Repository.Snapshot().PlanItems);
    }

    [Fact]
    public void ListingPreparationUsesAbsoluteQualityTargetAndRetainsOtherDraftEdits()
    {
        using var f = new Fixture(); f.SaveInitial();
        f.Session.Draft!.Rules[0].TargetQuantity = 45;
        Assert.True(f.Session.Prepare(Demand(), f.Repository.Snapshot()));
        Assert.True(f.Session.Prepare(Demand(), f.Repository.Snapshot()));
        Assert.Equal(11, Assert.Single(f.Session.Draft.Rules, r => r.ItemId == 2).TargetQuantity);
        Assert.Equal(45, f.Session.Draft.Rules[0].TargetQuantity);
        Assert.Single(f.Repository.Snapshot().PlanItems);
        Assert.Empty(f.Repository.Snapshot().ListingPlans);
        Assert.True(f.Session.Save(Owner), f.Session.Error);
    }

    [Fact]
    public void ListingPreparationDoesNotInventUnknownDemandOrRewriteAnyQuality()
    {
        using var f = new Fixture();
        Assert.False(f.Session.Prepare(Demand(false), f.Repository.Snapshot()));
        Assert.Empty(f.Session.Draft!.Rules);
        f.Session.Add(2, "Hull", ItemQualityPolicy.Any, 30);
        Assert.False(f.Session.Prepare(Demand(), f.Repository.Snapshot()));
        Assert.Equal(30, Assert.Single(f.Session.Draft.Rules).TargetQuantity);
    }
}
