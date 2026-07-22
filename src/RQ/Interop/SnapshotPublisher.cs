using System.Text.Json;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Interop;

public sealed class SnapshotPublisher
{
    public const string AutomaticRetrievalCapability = "automaticRetrieval";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string providerInstanceId;
    private readonly StateRepository state;
    private readonly Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache;
    private long revision;
    private string snapshotJson = "{}";
    private IReadOnlyDictionary<string, string> operationJson = new Dictionary<string, string>();
    private OwnerScope currentOwner = new();

    public SnapshotPublisher(string providerInstanceId, StateRepository state, Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache)
    {
        this.providerInstanceId = providerInstanceId;
        this.state = state;
        this.cache = cache;
    }

    public long Revision => Interlocked.Read(ref revision);
    public string GetSnapshot() => Volatile.Read(ref snapshotJson);
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
        Volatile.Write(ref currentOwner, owner);
        var stateSnapshot = state.Snapshot();
        var nextRevision = Interlocked.Increment(ref revision);
        var retainers = cache().Values
            .Where(retainer => owner.Matches(retainer.Owner))
            .OrderBy(retainer => retainer.RetainerName)
            .Select(RetainerContract)
            .ToArray();
        var scopedOperations = stateSnapshot.Operations.Where(operation => operation.Owner.Matches(owner)).ToArray();
        var current = scopedOperations.OrderByDescending(operation => operation.UpdatedAtUtc).FirstOrDefault();
        var snapshot = new
        {
            schema = "gooseworks-quartermaster-snapshot/v1",
            providerInstanceId,
            owner,
            revision = nextRevision,
            generatedAtUtc = DateTime.UtcNow,
            playerBags = playerStorage.Bags,
            playerStorage = new { requestedSources = playerStorage.RequestedSources, observedSources = playerStorage.ObservedSources },
            retainers,
            planItems = stateSnapshot.PlanItems,
            currentOperation = current is null ? null : OperationContract(current),
        };
        var operations = scopedOperations.ToDictionary(
            operation => operation.OperationId,
            operation => JsonSerializer.Serialize(OperationEnvelope(
                operation,
                stateSnapshot.Receipts.Where(receipt => receipt.OperationId == operation.OperationId).OrderBy(receipt => receipt.Revision)), JsonOptions),
            StringComparer.Ordinal);
        Volatile.Write(ref operationJson, operations);
        Volatile.Write(ref snapshotJson, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

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
        channels = new[] { IpcChannels.GetCapabilities, IpcChannels.GetSnapshot, IpcChannels.SubmitShortages, IpcChannels.GetOperation, IpcChannels.Changed },
        capabilities = new[] { AutomaticRetrievalCapability },
        requestSchemas = new[] { ShortageSubmissionService.RequestSchema },
        statusVocabulary = new[] { OperationStatuses.Queued, OperationStatuses.Accepted, OperationStatuses.Running, OperationStatuses.Succeeded, OperationStatuses.PartiallySucceeded, OperationStatuses.Indeterminate, OperationStatuses.Failed, OperationStatuses.Cancelled, OperationStatuses.Rejected },
        executionPolicy = "request_selected",
        automaticExecutionField = "executeImmediately",
        reviewSurfaces = new[]
        {
            new { id = "stock-and-plan", label = "Stock and retrieval plan", command = "/rq", target = "stock" },
            new { id = "listings", label = "Retainer listings", command = "/rq", target = "listings" },
            new { id = "operation", label = "Current operation", command = "/rq", target = "operation" },
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
        operation.Lines,
        operation.DepositCandidates,
    };

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
}
