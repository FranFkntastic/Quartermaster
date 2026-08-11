using System.Reflection;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Observations.V1;
using RQ.Automation;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class ListingNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenRetainerListings_CompletesAfterExactEmptyOrUnchangedListingEvidence(bool unchanged)
    {
        using var directory = new TemporaryDirectory();
        using var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json")));
        var captureSessions = new ObservationCaptureSessionRegistry();
        var owner = TestData.Owner;
        var observationOwner = new ObservationOwner(owner.LocalContentId!.Value, owner.HomeWorldId!.Value);
        var scope = new ObservationScope(
            observationOwner,
            ObservationSubject.Retainer(44, observationOwner),
            ObservationContainerKind.RetainerMarketListings);
        var original = TestData.Retainer(44, "Eris");
        original.ListingsObservedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        original.ObservedSources.Add("RetainerMarket");
        original.Listings.Add(new CachedMarketListing
        {
            ItemId = 200,
            ItemName = "Whale-class Pressure Hull",
            Quantity = 16,
            UnitPrice = 1_045_000,
        });
        cache.Upsert(original);
        IReadOnlyList<CachedMarketListing> freshListings = unchanged
            ? [new CachedMarketListing { ItemId = 200, ItemName = "Whale-class Pressure Hull", Quantity = 16, UnitPrice = 1_045_000 }]
            : [];
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
            nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                Task.FromResult(RetainerAutomationResult.Succeeded("opened", "opened")),
            nameof(IRetainerAutomationSession.OpenSellingListAsync) =>
                Task.FromResult(PublishEvidence()),
            _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
        };
        using var coordinator = new ListingNavigationCoordinator(
            session,
            new FakeAutoRetainer { IsAvailable = false },
            new AutomationLease(),
            cache,
            captureSessions,
            () => owner);

        var result = await coordinator.OpenRetainerListingsAsync(new(44, "Eris"));

        Assert.True(result.Success);
        Assert.Contains("fresh listings", result.Message, StringComparison.Ordinal);
        Assert.Equal((ulong)44, coordinator.LastRefreshTiming?.RetainerId);
        Assert.InRange(coordinator.LastRefreshTiming!.ObservedToAppliedMilliseconds, 0, 250);
        Assert.Equal(unchanged ? 1 : 0, cache.Snapshot()[44].Listings.Count);

        RetainerAutomationResult PublishEvidence()
        {
            cache.ReplaceListings(new RetainerListingsObservation(
                44,
                "Eris",
                owner,
                DateTime.UtcNow,
                freshListings,
                captureSessions.Resolve(scope)));
            return RetainerAutomationResult.Succeeded("opened", "opened");
        }
    }

    [Fact]
    public async Task OpenRetainerListings_IgnoresExactEvidenceCapturedBeforeTheSellingListAction()
    {
        using var directory = new TemporaryDirectory();
        using var cache = new RetainerCacheRepository(new RetainerCacheStore(Path.Combine(directory.Path, "retainer-cache.json")));
        var captureSessions = new ObservationCaptureSessionRegistry();
        var owner = TestData.Owner;
        var observationOwner = new ObservationOwner(owner.LocalContentId!.Value, owner.HomeWorldId!.Value);
        var scope = new ObservationScope(
            observationOwner,
            ObservationSubject.Retainer(44, observationOwner),
            ObservationContainerKind.RetainerMarketListings);
        DateTime? listingActionEnteredAtUtc = null;
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, _) => method.Name switch
        {
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
            nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                Task.FromResult(PublishEvidence(16)),
            nameof(IRetainerAutomationSession.OpenSellingListAsync) =>
                CompleteSellingListAfterFreshEvidenceAsync(),
            _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
        };
        using var coordinator = new ListingNavigationCoordinator(
            session,
            new FakeAutoRetainer { IsAvailable = false },
            new AutomationLease(),
            cache,
            captureSessions,
            () => owner);

        var result = await coordinator.OpenRetainerListingsAsync(new(44, "Eris"));

        Assert.True(result.Success);
        Assert.NotNull(listingActionEnteredAtUtc);
        Assert.True(coordinator.LastRefreshTiming!.EvidenceObservedAtUtc >= listingActionEnteredAtUtc);
        Assert.Empty(cache.Snapshot()[44].Listings);

        RetainerAutomationResult PublishEvidence(uint quantity)
        {
            cache.ReplaceListings(new RetainerListingsObservation(
                44,
                "Eris",
                owner,
                DateTime.UtcNow,
                quantity == 0
                    ? []
                    : [new CachedMarketListing { ItemId = 200, ItemName = "Whale-class Pressure Hull", Quantity = quantity }],
                captureSessions.Resolve(scope)));
            return RetainerAutomationResult.Succeeded("opened", "opened");
        }

        async Task<RetainerAutomationResult> CompleteSellingListAfterFreshEvidenceAsync()
        {
            listingActionEnteredAtUtc = DateTime.UtcNow;
            await Task.Delay(25);
            return PublishEvidence(0);
        }
    }

    [Fact]
    public async Task OpenRetainerListings_StopsAtTheReusableSellingList()
    {
        var calls = new List<string>();
        RetainerAutomationTarget? openedRetainer = null;
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, arguments) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
                nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                    Task.FromResult(CaptureRetainer(arguments!, out openedRetainer)),
                nameof(IRetainerAutomationSession.OpenSellingListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("opened", "opened")),
                _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
            };
        };
        var autoRetainer = new FakeAutoRetainer();
        using var coordinator = new ListingNavigationCoordinator(session, autoRetainer, new AutomationLease());

        var result = await coordinator.OpenRetainerListingsAsync(new(44, "Eris"));

        Assert.True(result.Started);
        Assert.True(result.Success);
        Assert.Equal(
            [
                nameof(IRetainerAutomationSession.EnsureRetainerListAsync),
                nameof(IRetainerAutomationSession.OpenRetainerAsync),
                nameof(IRetainerAutomationSession.OpenSellingListAsync),
            ],
            calls);
        Assert.Equal(new RetainerAutomationTarget(44, "Eris"), openedRetainer);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
    }

    [Fact]
    public async Task ReturnToRetainerList_UsesTheGeneralRecoveryAndRestoresAutoRetainer()
    {
        var calls = new List<string>();
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, _) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IRetainerAutomationSession.ReturnToRetainerListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
                _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
            };
        };
        var autoRetainer = new FakeAutoRetainer();
        using var coordinator = new ListingNavigationCoordinator(session, autoRetainer, new AutomationLease());

        var result = await coordinator.ReturnToRetainerListAsync();

        Assert.True(result.Started);
        Assert.True(result.Success);
        Assert.Equal([nameof(IRetainerAutomationSession.ReturnToRetainerListAsync)], calls);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
    }

    [Fact]
    public async Task Open_ReconcilesThroughTheGeneralRetainerSessionAndRestoresAutoRetainer()
    {
        var calls = new List<string>();
        RetainerAutomationTarget? openedRetainer = null;
        RetainerMarketListingTarget? openedListing = null;
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, arguments) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
                nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                    Task.FromResult(CaptureRetainer(arguments!, out openedRetainer)),
                nameof(IRetainerAutomationSession.OpenSellingListingAsync) =>
                    Task.FromResult(CaptureListing(arguments!, out openedListing)),
                _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
            };
        };
        var autoRetainer = new FakeAutoRetainer();
        using var coordinator = new ListingNavigationCoordinator(session, autoRetainer, new AutomationLease());

        var result = await coordinator.OpenAsync(new(
            44,
            "Eris",
            3,
            100,
            "Darksteel Ore",
            2,
            true,
            99));

        Assert.True(result.Started);
        Assert.True(result.Success);
        Assert.Equal(
            [
                nameof(IRetainerAutomationSession.EnsureRetainerListAsync),
                nameof(IRetainerAutomationSession.OpenRetainerAsync),
                nameof(IRetainerAutomationSession.OpenSellingListingAsync),
            ],
            calls);
        Assert.Equal(new RetainerAutomationTarget(44, "Eris"), openedRetainer);
        Assert.Equal(new RetainerMarketListingTarget(3, 100, 2, true, 99), openedListing);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
    }

    [Fact]
    public async Task Open_PreservesUnknownPriceInsteadOfRefusingNavigation()
    {
        RetainerMarketListingTarget? openedListing = null;
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, arguments) => method.Name switch
        {
            nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
            nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                Task.FromResult(RetainerAutomationResult.Succeeded("opened", "opened")),
            nameof(IRetainerAutomationSession.OpenSellingListingAsync) =>
                Task.FromResult(CaptureListing(arguments!, out openedListing)),
            _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
        };
        using var coordinator = new ListingNavigationCoordinator(
            session,
            new FakeAutoRetainer { IsAvailable = false },
            new AutomationLease());

        var result = await coordinator.OpenAsync(new(44, "Eris", null, 100, "Darksteel Ore", 2, false, null));

        Assert.True(result.Success);
        Assert.Equal(new RetainerMarketListingTarget(-1, 100, 2, false, null), openedListing);
    }

    [Fact]
    public async Task Open_RecoversToRetainerListWhenListingSurfaceFails()
    {
        var calls = new List<string>();
        var session = DispatchProxy.Create<IRetainerAutomationSession, SessionProxy>();
        ((SessionProxy)(object)session).Handler = (method, _) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IRetainerAutomationSession.EnsureRetainerListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("ready", "ready")),
                nameof(IRetainerAutomationSession.OpenRetainerAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("opened", "opened")),
                nameof(IRetainerAutomationSession.OpenSellingListingAsync) =>
                    Task.FromResult(RetainerAutomationResult.Failed("missing", "missing")),
                nameof(IRetainerAutomationSession.ReturnToRetainerListAsync) =>
                    Task.FromResult(RetainerAutomationResult.Succeeded("recovered", "recovered")),
                _ => throw new InvalidOperationException($"Unexpected call: {method.Name}"),
            };
        };
        using var coordinator = new ListingNavigationCoordinator(
            session,
            new FakeAutoRetainer { IsAvailable = false },
            new AutomationLease());

        var result = await coordinator.OpenAsync(new(44, "Eris", 3, 100, "Darksteel Ore", 2, false, 99));

        Assert.False(result.Success);
        Assert.Equal(nameof(IRetainerAutomationSession.ReturnToRetainerListAsync), calls[^1]);
    }

    private static RetainerAutomationResult CaptureRetainer(
        object?[] arguments,
        out RetainerAutomationTarget target)
    {
        target = Assert.IsType<RetainerAutomationTarget>(arguments[0]);
        return RetainerAutomationResult.Succeeded("opened", "opened");
    }

    private static RetainerAutomationResult CaptureListing(
        object?[] arguments,
        out RetainerMarketListingTarget target)
    {
        target = Assert.IsType<RetainerMarketListingTarget>(arguments[0]);
        return RetainerAutomationResult.Succeeded("opened", "opened");
    }

    public class SessionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class FakeAutoRetainer : IAutoRetainerIpc
    {
        public bool IsAvailable { get; init; } = true;
        public bool IsBusy { get; init; }
        public bool IsSuppressed { get; private set; }
        public List<bool> SuppressionChanges { get; } = [];
        public void SetSuppressed(bool suppressed)
        {
            IsSuppressed = suppressed;
            SuppressionChanges.Add(suppressed);
        }
        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void Dispose() { }
    }
}
