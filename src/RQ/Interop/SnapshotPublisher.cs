using System.Text.Json;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.Interop;

public sealed class SnapshotPublisher
{
    public const string AutomaticRetrievalCapability = "automaticRetrieval";
    public const string TransferPlansCapability = "transferPlans.v1";
    public const string StowagePlansCapability = "stowagePlans.v1";
    public const string RestockPlansCapability = "restockPlans.v1";
    public const string AutomaticElementalDepositCapability = "automaticElementalDeposit";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string providerInstanceId;
    private readonly StateRepository state;
    private readonly Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache;
    private readonly object snapshotGate = new();
    private long revision;
    private int snapshotDirty;
    private string snapshotJson = "{}";
    private IReadOnlyDictionary<string, string> operationJson = new Dictionary<string, string>();
    private OwnerScope currentOwner = new();
    private SnapshotInputs? latestInputs;

    public SnapshotPublisher(string providerInstanceId, StateRepository state, Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache)
    {
        this.providerInstanceId = providerInstanceId;
        this.state = state;
        this.cache = cache;
    }

    public long Revision => Interlocked.Read(ref revision);
    public string GetSnapshot()
    {
        EnsureSnapshotCurrent();
        return Volatile.Read(ref snapshotJson);
    }
    public string GetOperation(string operationId) => Volatile.Read(ref operationJson).TryGetValue(operationId, out var json)
        ? json
        : JsonSerializer.Serialize(new
        {
            schema = "gooseworks-quartermaster-operation/v1",
            providerInstanceId,
            operationId,
            owner = Volatile.Read(ref currentOwner),
            status = "not_found",
            revision = Revision,
        }, JsonOptions);

    public void Refresh(OwnerScope owner, PlayerStorageCapture playerStorage)
    {
        Refresh(owner, playerStorage, state.FullSnapshot(), cache());
    }

    public void Refresh(QuartermasterRuntimeSnapshot runtime, bool rebuildSnapshot = true)
    {
        // The repository remains authoritative for operations that may have
        // advanced while expensive stock reconciliation was intentionally held.
        Refresh(runtime.Owner, runtime.PlayerStorage, state.FullSnapshot(), runtime.Retainers, runtime.Stowage, rebuildSnapshot);
    }

    public void RefreshOperations(OwnerScope owner, IReadOnlyCollection<string> operationIds)
    {
        Volatile.Write(ref currentOwner, owner);
        Interlocked.Increment(ref revision);
        if (operationIds.Count > 0)
        {
            var updated = new Dictionary<string, string>(Volatile.Read(ref operationJson), StringComparer.Ordinal);
            state.Read(document =>
            {
                foreach (var operationId in operationIds)
                {
                    var operation = document.Operations.FirstOrDefault(candidate =>
                        candidate.OperationId == operationId && candidate.Owner.Matches(owner));
                    if (operation is null)
                    {
                        updated.Remove(operationId);
                        continue;
                    }

                    updated[operationId] = JsonSerializer.Serialize(OperationEnvelope(
                        operation,
                        document.Receipts
                            .Where(receipt => receipt.OperationId == operationId)
                            .OrderBy(receipt => receipt.Revision)), JsonOptions);
                }
                return true;
            });
            Volatile.Write(ref operationJson, updated);
        }
        Interlocked.Exchange(ref snapshotDirty, 1);
    }

    private void Refresh(
        OwnerScope owner,
        PlayerStorageCapture playerStorage,
        QuartermasterState stateSnapshot,
        IReadOnlyDictionary<ulong, CachedRetainer> retainersById,
        IReadOnlyList<StowageEvaluation>? stowage = null,
        bool rebuildSnapshot = true)
    {
        Volatile.Write(ref currentOwner, owner);
        Volatile.Write(ref latestInputs, new SnapshotInputs(owner, playerStorage, retainersById, stowage ?? []));
        var nextRevision = Interlocked.Increment(ref revision);
        Volatile.Write(ref operationJson, BuildOperationJson(stateSnapshot, owner));
        if (rebuildSnapshot)
        {
            lock (snapshotGate)
            {
                WriteSnapshot(owner, playerStorage, stateSnapshot, retainersById, stowage ?? [], nextRevision);
                Interlocked.Exchange(ref snapshotDirty, 0);
            }
        }
        else
            Interlocked.Exchange(ref snapshotDirty, 1);
    }

    private void EnsureSnapshotCurrent()
    {
        if (Volatile.Read(ref snapshotDirty) == 0)
            return;

        lock (snapshotGate)
        {
            if (Interlocked.Exchange(ref snapshotDirty, 0) == 0)
                return;
            if (Volatile.Read(ref latestInputs) is not { } inputs)
            {
                Interlocked.Exchange(ref snapshotDirty, 1);
                return;
            }
            WriteSnapshot(
                inputs.Owner,
                inputs.PlayerStorage,
                state.FullSnapshot(),
                inputs.Retainers,
                inputs.Stowage,
                Revision);
        }
    }

    private void WriteSnapshot(
        OwnerScope owner,
        PlayerStorageCapture playerStorage,
        QuartermasterState stateSnapshot,
        IReadOnlyDictionary<ulong, CachedRetainer> retainersById,
        IReadOnlyList<StowageEvaluation> stowage,
        long snapshotRevision)
    {
        var retainers = retainersById.Values
            .Where(retainer => owner.Matches(retainer.Owner))
            .OrderBy(retainer => retainer.RetainerName)
            .Select(RetainerContract)
            .ToArray();
        var current = stateSnapshot.Operations
            .Where(operation => operation.Owner.Matches(owner))
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .FirstOrDefault();
        var latestListingCapture = stateSnapshot.LatestRetainerListingCapture is { } capture && owner.Matches(capture.Owner)
            ? capture
            : null;
        var snapshot = new
        {
            schema = "gooseworks-quartermaster-snapshot/v1",
            providerInstanceId,
            owner,
            revision = snapshotRevision,
            generatedAtUtc = DateTime.UtcNow,
            playerBags = playerStorage.Bags,
            playerStorage = new { requestedSources = playerStorage.RequestedSources, observedSources = playerStorage.ObservedSources },
            retainers,
            latestRetainerListingCapture = latestListingCapture,
            planItems = stateSnapshot.PlanItems,
            transferPlans = TransferPlanContract(stateSnapshot, owner, stowage),
            stowagePlans = StowageContract(stateSnapshot, owner, stowage),
            restockPlans = RestockContract(stateSnapshot, owner),
            currentOperation = current is null ? null : OperationContract(current),
        };
        Volatile.Write(ref snapshotJson, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private IReadOnlyDictionary<string, string> BuildOperationJson(QuartermasterState stateSnapshot, OwnerScope owner) =>
        stateSnapshot.Operations
            .Where(operation => operation.Owner.Matches(owner))
            .ToDictionary(
                operation => operation.OperationId,
                operation => JsonSerializer.Serialize(OperationEnvelope(
                    operation,
                    stateSnapshot.Receipts
                        .Where(receipt => receipt.OperationId == operation.OperationId)
                        .OrderBy(receipt => receipt.Revision)), JsonOptions),
                StringComparer.Ordinal);

    public void Refresh(OwnerScope owner, IReadOnlyList<InventoryBag> playerBags) => Refresh(
        owner,
        new PlayerStorageCapture(
            playerBags,
            playerBags.Select(bag => bag.BagName).ToArray(),
            playerBags.Select(bag => bag.BagName).ToArray()));

    public string GetCapabilities() => JsonSerializer.Serialize(new
    {
        schema = "gooseworks-quartermaster-capabilities/v1",
        providerInstanceId,
        revision = Revision,
        channels = new[] { IpcChannels.GetCapabilities, IpcChannels.GetSnapshot, IpcChannels.SubmitShortages, IpcChannels.SubmitElementalDeposit, IpcChannels.GetOperation, IpcChannels.Changed },
        capabilities = new[] { AutomaticRetrievalCapability, AutomaticElementalDepositCapability, TransferPlansCapability, StowagePlansCapability, RestockPlansCapability },
        requestSchemas = new[] { ShortageSubmissionService.RequestSchema, ElementalDepositSubmissionService.RequestSchema },
        statusVocabulary = new[] { OperationStatuses.Queued, OperationStatuses.Accepted, OperationStatuses.Running, OperationStatuses.Succeeded, OperationStatuses.PartiallySucceeded, OperationStatuses.Indeterminate, OperationStatuses.Failed, OperationStatuses.Cancelled, OperationStatuses.Rejected },
        executionPolicy = "request_selected",
        automaticExecutionField = "executeImmediately",
        reviewSurfaces = new[]
        {
            new { id = "transfer", label = "Stock and Transfer Plans", command = "/rq", target = "transfer" },
            new { id = "transfer-review", label = "Transfer Plan review", command = "/rq", target = "transfer-review" },
            new { id = "item-groups", label = "Item Groups", command = "/rq", target = "item-groups" },
            new { id = "listings", label = "Retainer listings", command = "/rq", target = "listings" },
            new { id = "activity", label = "Operations and receipts", command = "/rq", target = "activity" },
        },
    }, JsonOptions);

    public string CreateChanged(string kind, string? operationId, OwnerScope owner) => JsonSerializer.Serialize(new ChangedNotification(
        "gooseworks-quartermaster-changed/v1",
        providerInstanceId,
        $"{owner.LocalContentId}:{owner.HomeWorldId}",
        Revision,
        kind,
        operationId), JsonOptions);

    private static object OperationContract(OperationRecord operation) => new
    {
        operation.OperationId,
        operation.RequestId,
        operation.Kind,
        operation.ExecuteImmediately,
        operation.Status,
        operation.Revision,
        operation.CreatedAtUtc,
        operation.UpdatedAtUtc,
        operation.Message,
        operation.SourcePlanId,
        operation.SourcePlanRevision,
        operation.SourcePlanName,
        operation.Lines,
        operation.DepositCandidates,
    };

    private static object RestockContract(QuartermasterState state, OwnerScope owner) => new
    {
        schema = "gooseworks-quartermaster-restock-plans/v1",
        plans = RestockPlanCatalog.OwnerPlans(state, owner).Select(plan => new
        {
            plan.Id,
            plan.Revision,
            plan.Owner,
            plan.Name,
            plan.Enabled,
            lines = plan.Items.Select(item => new
            {
                item.Id,
                item.ItemId,
                item.ItemName,
                desiredPlayerQuantity = item.TargetQuantity,
                quality = item.Quality.ToString(),
                item.Enabled,
            }),
        }),
    };

    private static object StowageContract(
        QuartermasterState state,
        OwnerScope owner,
        IReadOnlyList<StowageEvaluation> evaluations) =>
        TransferPlanContract(state, owner, evaluations, "gooseworks-quartermaster-stowage-plans/v1");

    private static object TransferPlanContract(
        QuartermasterState state,
        OwnerScope owner,
        IReadOnlyList<StowageEvaluation> evaluations) =>
        TransferPlanContract(state, owner, evaluations, "gooseworks-quartermaster-transfer-plans/v1");

    private static object TransferPlanContract(
        QuartermasterState state,
        OwnerScope owner,
        IReadOnlyList<StowageEvaluation> evaluations,
        string schema)
    {
        var evaluated = evaluations
            .SelectMany(plan => plan.Lines)
            .ToDictionary(line => line.RuleId);
        return new
        {
            schema,
            plans = state.StowagePlans
                .Where(plan => plan.Owner.Matches(owner))
                .OrderBy(plan => plan.Priority)
                .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
                .Select(plan => new
                {
                    plan.Id,
                    plan.Revision,
                    plan.Owner,
                    plan.Name,
                    plan.Enabled,
                    plan.Priority,
                    rules = state.PlanItems
                        .Where(rule => rule.StowagePlanId == plan.Id)
                        .Select(rule =>
                        {
                            evaluated.TryGetValue(rule.Id, out var line);
                            return new
                            {
                                rule.Id,
                                rule.ItemId,
                                rule.ItemName,
                                desiredPlayerQuantity = rule.TargetQuantity,
                                quality = rule.Quality.ToString(),
                                routing = new
                                {
                                    mode = (rule.Routing?.Mode ?? StowageRoutingMode.ConsolidateFirst).ToString(),
                                    preferredRetainerIds = rule.Routing?.PreferredRetainerIds ?? [],
                                    overflow = (rule.Routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer).ToString(),
                                },
                                rule.Enabled,
                                evaluated = line is null ? null : new
                                {
                                    action = line.Action.ToString().ToLowerInvariant(),
                                    quantity = line.Action == StowageAction.Retrieve
                                        ? line.RetrieveQuantity
                                        : line.DepositQuantity,
                                    line.PlayerQuantity,
                                    line.DesiredPlayerQuantity,
                                },
                            };
                        }),
                }),
        };
    }

    private object OperationEnvelope(OperationRecord operation, IEnumerable<OperationReceipt> receipts) => new
    {
        schema = "gooseworks-quartermaster-operation/v1",
        providerInstanceId,
        operation.OperationId,
        operation.RequestId,
        operation.Kind,
        operation.ExecuteImmediately,
        owner = operation.Owner,
        operation.Status,
        operation.Revision,
        operation.CreatedAtUtc,
        operation.UpdatedAtUtc,
        completedAtUtc = OperationStatuses.IsTerminal(operation.Status) ? operation.UpdatedAtUtc : (DateTime?)null,
        operation.Message,
        operation.SourcePlanId,
        operation.SourcePlanRevision,
        operation.SourcePlanName,
        operation.Lines,
        operation.DepositCandidates,
        receipts,
    };

    private static object RetainerContract(CachedRetainer retainer) => new
    {
        retainer.RetainerId,
        retainer.RetainerName,
        retainer.ObservedAtUtc,
        retainer.Gil,
        retainer.GilObservedAtUtc,
        retainer.ListingsObservedAtUtc,
        retainer.RequestedSources,
        retainer.ObservedSources,
        retainer.Bags,
        listings = retainer.Listings.Select(listing => new
        {
            listing.ItemId,
            listing.ItemName,
            listing.ItemType,
            listing.Quantity,
            listing.IsHq,
            listing.Condition,
            listing.ConditionPercent,
            listing.ContainerKey,
            listing.SlotIndex,
            listing.UnitPrice,
            listedAt = listing.ListedAtUtc,
        }),
    };

    private sealed record SnapshotInputs(
        OwnerScope Owner,
        PlayerStorageCapture PlayerStorage,
        IReadOnlyDictionary<ulong, CachedRetainer> Retainers,
        IReadOnlyList<StowageEvaluation> Stowage);
}
