using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Interop;
using RQ.Inventory;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Tests;

public sealed class IpcTests
{
    [Fact]
    public void Capabilities_AdvertiseAutomaticRetrievalAndStowageSupport()
    {
        using var directory = new TemporaryDirectory();
        var snapshots = new SnapshotPublisher("provider", TestData.Repository(directory.Path), () => new Dictionary<ulong, CachedRetainer>());

        using var document = JsonDocument.Parse(snapshots.GetCapabilities());

        Assert.Contains(
            SnapshotPublisher.AutomaticRetrievalCapability,
            document.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            SnapshotPublisher.StowagePlansCapability,
            document.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            SnapshotPublisher.RestockPlansCapability,
            document.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["stock", "restock", "stowage", "listings", "activity"],
            document.RootElement.GetProperty("reviewSurfaces").EnumerateArray()
                .Select(surface => surface.GetProperty("id").GetString()));
    }

    [Fact]
    public void Snapshot_PublishesOwnerScopedStowageRulesWithoutInternalConfidenceFields()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var planId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        repository.Mutate(state =>
        {
            state.StowagePlans.Add(new StowagePlan
            {
                Id = planId,
                Owner = TestData.Owner,
                Name = "General",
                Revision = 3,
            });
            state.PlanItems.Add(new TargetPlanItem
            {
                Id = ruleId,
                StowagePlanId = planId,
                ItemId = 100,
                ItemName = "Darksteel Ore",
                TargetQuantity = 50,
                Notes = "private operator note",
            });
        });
        var snapshots = new SnapshotPublisher("provider", repository, () => new Dictionary<ulong, CachedRetainer>());

        snapshots.Refresh(TestData.Owner, []);
        using var document = JsonDocument.Parse(snapshots.GetSnapshot());

        var stowage = document.RootElement.GetProperty("stowagePlans");
        Assert.Equal("gooseworks-quartermaster-stowage-plans/v1", stowage.GetProperty("schema").GetString());
        var plan = Assert.Single(stowage.GetProperty("plans").EnumerateArray());
        Assert.Equal(planId, plan.GetProperty("id").GetGuid());
        Assert.Equal(3, plan.GetProperty("revision").GetInt32());
        var rule = Assert.Single(plan.GetProperty("rules").EnumerateArray());
        Assert.Equal(ruleId, rule.GetProperty("id").GetGuid());
        Assert.False(rule.TryGetProperty("notes", out _));
        Assert.False(rule.TryGetProperty("freshness", out _));
    }

    [Fact]
    public void Snapshot_PublishesOwnerScopedRestockIntentWithoutPrivateNotes()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => RestockPlanCatalog.Create(
            state,
            TestData.Owner,
            "Workshop",
            [new RestockPlanItem
            {
                ItemId = 100,
                ItemName = "Darksteel Ore",
                TargetQuantity = 50,
                Notes = "private operator note",
            }]));
        var snapshots = new SnapshotPublisher("provider", repository, () => new Dictionary<ulong, CachedRetainer>());

        snapshots.Refresh(TestData.Owner, []);
        using var document = JsonDocument.Parse(snapshots.GetSnapshot());

        var restock = document.RootElement.GetProperty("restockPlans");
        Assert.Equal("gooseworks-quartermaster-restock-plans/v1", restock.GetProperty("schema").GetString());
        var plan = Assert.Single(restock.GetProperty("plans").EnumerateArray());
        Assert.Equal("Workshop", plan.GetProperty("name").GetString());
        var line = Assert.Single(plan.GetProperty("lines").EnumerateArray());
        Assert.Equal(50, line.GetProperty("desiredPlayerQuantity").GetInt32());
        Assert.False(line.TryGetProperty("notes", out _));
        Assert.False(line.TryGetProperty("freshness", out _));
    }

    [Fact]
    public void Submit_QueuesFrameworkWorkThenPersistsAcceptedOperationWithoutExecution()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var driver = new RecordingDriver();

        var queued = Parse(service.Submit(TestData.Json(TestData.Request())));

        Assert.Equal(OperationStatuses.Queued, queued.GetProperty("status").GetString());
        Assert.Empty(repository.Snapshot().Operations);
        Assert.Equal(1, queue.Count);
        queue.Drain();
        var operation = Assert.Single(repository.Snapshot().Operations);
        Assert.Equal(OperationStatuses.Accepted, operation.Status);
        Assert.False(operation.ExecuteImmediately);
        Assert.Contains("review", operation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Darksteel Ore", Assert.Single(repository.Snapshot().PlanItems).ItemName);
        Assert.Equal(0, driver.Calls);
    }

    [Fact]
    public void Submit_ExecuteImmediatelyPersistsAndParticipatesInCanonicalHash()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var request = TestData.Request() with { ExecuteImmediately = true };

        var queued = Parse(service.Submit(TestData.Json(request)));
        Assert.Contains("automatic", queued.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        using (var pending = JsonDocument.Parse(service.GetPendingOperation(request.OperationId)!))
            Assert.True(pending.RootElement.GetProperty("executeImmediately").GetBoolean());
        queue.Drain();

        var persisted = Assert.Single(TestData.Repository(directory.Path).Snapshot().Operations);
        Assert.True(persisted.ExecuteImmediately);
        Assert.Contains("automatic", persisted.Message, StringComparison.OrdinalIgnoreCase);

        var conflict = Parse(service.Submit(TestData.Json(request with { ExecuteImmediately = false })));
        Assert.Equal(OperationStatuses.Rejected, conflict.GetProperty("status").GetString());
        Assert.Equal("request_conflict", conflict.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Submit_PreFlagCanonicalHashReplaysOnlyAsReviewRequired()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var request = TestData.Request();
        repository.Mutate(state =>
        {
            state.Requests.Add(new SubmittedRequestRecord
            {
                RequestId = request.RequestId,
                OperationId = request.OperationId,
                CanonicalHash = LegacyCanonicalHash(request),
            });
            state.Operations.Add(new OperationRecord
            {
                RequestId = request.RequestId,
                OperationId = request.OperationId,
                Owner = TestData.Owner,
                Status = OperationStatuses.Accepted,
                Revision = 1,
            });
        });
        var service = new ShortageSubmissionService("provider-1", repository, new TestWorkQueue(), () => TestData.Owner);

        Assert.Equal(OperationStatuses.Accepted, Parse(service.Submit(TestData.Json(request))).GetProperty("status").GetString());
        var automatic = Parse(service.Submit(TestData.Json(request with { ExecuteImmediately = true })));
        Assert.Equal(OperationStatuses.Rejected, automatic.GetProperty("status").GetString());
        Assert.Equal("request_conflict", automatic.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Submit_SameCanonicalRequestReplaysAndChangedPayloadConflicts()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var request = TestData.Request();
        service.Submit(TestData.Json(request));
        queue.Drain();

        var replay = Parse(service.Submit(TestData.Json(request)));
        var changed = request with { Items = [new ShortageRequestItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 60, ShortageQuantity = 40 }] };
        var conflict = Parse(service.Submit(TestData.Json(changed)));

        Assert.Equal(OperationStatuses.Accepted, replay.GetProperty("status").GetString());
        Assert.Equal(1, replay.GetProperty("revision").GetInt64());
        Assert.Equal(OperationStatuses.Rejected, conflict.GetProperty("status").GetString());
        Assert.Equal("request_conflict", conflict.GetProperty("errorCode").GetString());
        Assert.Single(repository.Snapshot().Operations);
    }

    [Fact]
    public void Submit_ReplayKeepsAcknowledgementStatusAboutAcceptance()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var request = TestData.Request();
        service.Submit(TestData.Json(request));
        queue.Drain();
        new OperationJournal(repository).Transition(request.OperationId, OperationStatuses.Running, "start", "running");

        var replay = Parse(service.Submit(TestData.Json(request)));

        Assert.Equal(OperationStatuses.Accepted, replay.GetProperty("status").GetString());
        Assert.Contains("running", replay.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_ReplaysPersistedRequestAcrossProviderReload()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var first = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        first.Submit(TestData.Json(TestData.Request()));
        queue.Drain();
        var reloadedRequest = TestData.Request() with { ProviderInstanceId = "provider-2" };
        var reloaded = new ShortageSubmissionService("provider-2", repository, new TestWorkQueue(), () => TestData.Owner);

        var replay = Parse(reloaded.Submit(TestData.Json(reloadedRequest)));

        Assert.Equal(OperationStatuses.Accepted, replay.GetProperty("status").GetString());
        Assert.Equal("provider-2", replay.GetProperty("providerInstanceId").GetString());
        Assert.Single(repository.Snapshot().Operations);
    }

    [Fact]
    public void Submit_DuplicateItemsAndInvalidQuantitiesAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var service = new ShortageSubmissionService("provider-1", TestData.Repository(directory.Path), new TestWorkQueue(), () => TestData.Owner);
        var duplicate = TestData.Request() with
        {
            Items =
            [
                new ShortageRequestItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10, ShortageQuantity = 5 },
                new ShortageRequestItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10, ShortageQuantity = 5 },
            ],
        };
        var invalid = TestData.Request("request-2", "operation-2") with
        {
            Items = [new ShortageRequestItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 0, ShortageQuantity = 1 }],
        };

        Assert.Equal("duplicate_item", Parse(service.Submit(TestData.Json(duplicate))).GetProperty("errorCode").GetString());
        Assert.Equal("invalid_quantity", Parse(service.Submit(TestData.Json(invalid))).GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Submit_MissingCurrentScopeAndOwnerMismatchAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var missing = new ShortageSubmissionService("provider-1", repository, new TestWorkQueue(), () => new OwnerScope());
        var mismatch = new ShortageSubmissionService("provider-1", repository, new TestWorkQueue(), () => TestData.Owner);
        var other = TestData.Request() with { Owner = new RequestOwner { LocalContentId = 99, HomeWorldId = 406, CharacterName = "Other" } };

        Assert.Equal("missing_owner_scope", Parse(missing.Submit(TestData.Json(TestData.Request()))).GetProperty("errorCode").GetString());
        Assert.Equal("owner_mismatch", Parse(mismatch.Submit(TestData.Json(other))).GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Submit_UsesStableOwnerIdsWhileTreatingNameAsDisplayEvidence()
    {
        using var directory = new TemporaryDirectory();
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", TestData.Repository(directory.Path), queue, () => TestData.Owner);
        var renamed = TestData.Request() with
        {
            Owner = new RequestOwner
            {
                LocalContentId = TestData.Owner.LocalContentId!.Value,
                HomeWorldId = TestData.Owner.HomeWorldId!.Value,
                CharacterName = "Updated Display Name",
            },
        };

        Assert.Equal(OperationStatuses.Queued, Parse(service.Submit(TestData.Json(renamed))).GetProperty("status").GetString());
    }

    [Fact]
    public void Submit_PreservesExistingNotesAndEnabledState()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        repository.Mutate(state => state.PlanItems.Add(new TargetPlanItem { ItemId = 100, ItemName = "Old", TargetQuantity = 10, Notes = "Keep this", Enabled = false }));
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);

        service.Submit(TestData.Json(TestData.Request()));
        queue.Drain();

        var item = Assert.Single(repository.Snapshot().PlanItems);
        Assert.Equal(50, item.TargetQuantity);
        Assert.Equal("Keep this", item.Notes);
        Assert.False(item.Enabled);
    }

    [Fact]
    public void Provider_DisposeUnregistersEveryExactChannel()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var snapshots = new SnapshotPublisher("provider", repository, () => new Dictionary<ulong, CachedRetainer>());
        snapshots.Refresh(TestData.Owner, []);
        var registrar = new RecordingIpcRegistrar();
        var provider = new QuartermasterIpcProvider(registrar, snapshots, new ShortageSubmissionService("provider-1", repository, new TestWorkQueue(), () => TestData.Owner));

        provider.Dispose();

        Assert.Equal(
            [IpcChannels.GetCapabilities, IpcChannels.GetSnapshot, IpcChannels.SubmitShortages, IpcChannels.GetOperation, IpcChannels.Changed],
            registrar.Unregistered);
    }

    [Fact]
    public void Provider_QueuedOperationIsImmediatelyQueryable()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var queue = new TestWorkQueue();
        var submissions = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var snapshots = new SnapshotPublisher("provider-1", repository, () => new Dictionary<ulong, CachedRetainer>());
        snapshots.Refresh(TestData.Owner, []);
        var registrar = new RecordingIpcRegistrar();
        using var provider = new QuartermasterIpcProvider(registrar, snapshots, submissions);
        submissions.Submit(TestData.Json(TestData.Request()));

        var getOperation = Assert.IsType<Func<string, string>>(registrar.Registrations[IpcChannels.GetOperation]);
        using var document = JsonDocument.Parse(getOperation("operation-1"));

        Assert.Equal(OperationStatuses.Queued, document.RootElement.GetProperty("status").GetString());
        Assert.Equal("request-1", document.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public void Submit_PersistenceFailureBecomesContainedTerminalOperation()
    {
        using var directory = new TemporaryDirectory();
        var blockedParent = Path.Combine(directory.Path, "blocked");
        var repository = new StateRepository(new QuartermasterStateStore(Path.Combine(blockedParent, "state.json")));
        File.WriteAllText(blockedParent, "not a directory");
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        service.Submit(TestData.Json(TestData.Request()));

        queue.Drain();
        using var document = JsonDocument.Parse(service.GetPendingOperation("operation-1")!);

        Assert.Equal(OperationStatuses.Failed, document.RootElement.GetProperty("status").GetString());
        Assert.Contains("could not persist", document.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_SuccessfulRetryClearsTransientPersistenceFailure()
    {
        using var directory = new TemporaryDirectory();
        var blockedParent = Path.Combine(directory.Path, "blocked");
        var repository = new StateRepository(new QuartermasterStateStore(Path.Combine(blockedParent, "state.json")));
        File.WriteAllText(blockedParent, "not a directory");
        var queue = new TestWorkQueue();
        var service = new ShortageSubmissionService("provider-1", repository, queue, () => TestData.Owner);
        var request = TestData.Json(TestData.Request());
        service.Submit(request);
        queue.Drain();
        Assert.NotNull(service.GetPendingOperation("operation-1"));
        File.Delete(blockedParent);
        Directory.CreateDirectory(blockedParent);

        service.Submit(request);
        queue.Drain();

        Assert.Null(service.GetPendingOperation("operation-1"));
        Assert.Equal(OperationStatuses.Accepted, Assert.Single(repository.Snapshot().Operations).Status);
    }

    [Fact]
    public void Snapshot_ExposesAllScopedRetainerEvidenceAndCurrentOperation()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var first = TestData.Retainer(10, "Eris", (100, "Ore", 3));
        first.GilObservedAtUtc = new DateTime(2026, 7, 21, 11, 55, 0, DateTimeKind.Utc);
        first.ListingsObservedAtUtc = new DateTime(2026, 7, 21, 11, 56, 0, DateTimeKind.Utc);
        first.Bags[0].ObservedAtUtc = new DateTime(2026, 7, 21, 11, 54, 0, DateTimeKind.Utc);
        first.RequestedSources = ["RetainerPage1", "RetainerMarket"];
        first.ObservedSources = ["RetainerPage1"];
        first.Listings.Add(new CachedMarketListing
        {
            ItemId = 100,
            ItemName = "Ore",
            Quantity = 2,
            UnitPrice = 44,
            ListedAtUtc = new DateTime(2026, 7, 20, 10, 30, 0, DateTimeKind.Utc),
        });
        var second = TestData.Retainer(11, "Alt Retainer", (200, "Log", 4));
        second.Owner = new OwnerScope { LocalContentId = 77, HomeWorldId = 406, CharacterName = "Alt", HomeWorldName = "Maduin" };
        var publisher = new SnapshotPublisher("provider", repository, () => new Dictionary<ulong, CachedRetainer> { [10] = first, [11] = second });

        publisher.Refresh(TestData.Owner, new RQ.Inventory.PlayerStorageCapture(
            [new InventoryBag { BagName = "Inventory1", Items = [new RQ.Domain.InventoryItem { ItemId = 100, ItemName = "Ore", Quantity = 2 }] }],
            ["Inventory1", "Crystals"],
            ["Inventory1"]));
        using var document = JsonDocument.Parse(publisher.GetSnapshot());

        Assert.Equal(1, document.RootElement.GetProperty("retainers").GetArrayLength());
        Assert.Equal(TestData.Owner.LocalContentId, document.RootElement.GetProperty("owner").GetProperty("localContentId").GetUInt64());
        Assert.Equal(
            first.Listings[0].ListedAtUtc,
            document.RootElement.GetProperty("retainers")[0].GetProperty("listings")[0].GetProperty("listedAt").GetDateTime());
        Assert.Equal(1, document.RootElement.GetProperty("playerBags").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("playerStorage").GetProperty("requestedSources").GetArrayLength());
        Assert.Equal("RetainerPage1", document.RootElement.GetProperty("retainers")[0].GetProperty("observedSources")[0].GetString());
        Assert.Equal(first.GilObservedAtUtc, document.RootElement.GetProperty("retainers")[0].GetProperty("gilObservedAtUtc").GetDateTime());
        Assert.Equal(first.Bags[0].ObservedAtUtc, document.RootElement.GetProperty("retainers")[0].GetProperty("bags")[0].GetProperty("observedAtUtc").GetDateTime());
        Assert.Equal(OperationStatuses.Accepted, document.RootElement.GetProperty("currentOperation").GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("revision").GetInt64() > 0);
    }

    [Fact]
    public void Operation_UsesFlatConsumerContract()
    {
        using var directory = new TemporaryDirectory();
        var repository = TestData.Repository(directory.Path);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner, [new TargetPlanItem { ItemId = 100, ItemName = "Ore", TargetQuantity = 10 }]);
        var publisher = new SnapshotPublisher("provider", repository, () => new Dictionary<ulong, CachedRetainer>());

        publisher.Refresh(TestData.Owner, []);
        using var document = JsonDocument.Parse(publisher.GetOperation(operation.OperationId));

        Assert.Equal(operation.OperationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(operation.RequestId, document.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(OperationStatuses.Accepted, document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("operation", out _));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string LegacyCanonicalHash(ShortageRequest request)
    {
        var canonical = new StringBuilder()
            .Append(request.Schema).Append('|')
            .Append(request.RequestId.Trim()).Append('|')
            .Append(request.OperationId.Trim()).Append('|')
            .Append(request.SubmittedAtUtc.ToUniversalTime().ToString("O")).Append('|')
            .Append(request.Owner.LocalContentId).Append('|')
            .Append(request.Owner.HomeWorldId);
        foreach (var item in request.Items.OrderBy(item => item.ItemId))
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.TargetQuantity).Append('|').Append(item.ShortageQuantity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private sealed class RecordingDriver : IRetainerTransferDriver
    {
        public int Calls { get; private set; }
        public Task RequireRetainerListAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task OpenInventoryAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]); }
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new RetrievalResult(true, quantity, "ok", "ok")); }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]); }
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new RetainerCrystalTransferResult(true, quantity, "ok", "ok")); }
        public Task CloseRetainerAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public void CancelActive() { Calls++; }
    }
}
