using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Interop;

public sealed class ElementalDepositSubmissionService
{
    public const string RequestSchema = "gooseworks-quartermaster-elemental-deposit/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly string providerInstanceId;
    private readonly StateRepository repository;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly OperationJournal journal;
    private readonly Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<DateTime> utcNow;
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransientOperation> transientOperations = new(StringComparer.Ordinal);

    public event Action<string>? OperationChanged;

    public ElementalDepositSubmissionService(
        string providerInstanceId,
        StateRepository repository,
        IFrameworkWorkQueue workQueue,
        OperationJournal journal,
        Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache,
        Func<OwnerScope> currentOwner,
        Func<DateTime>? utcNow = null)
    {
        this.providerInstanceId = providerInstanceId;
        this.repository = repository;
        this.workQueue = workQueue;
        this.journal = journal;
        this.cache = cache;
        this.currentOwner = currentOwner;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Submit(string requestJson)
    {
        ElementalDepositRequest? request;
        try { request = JsonSerializer.Deserialize<ElementalDepositRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Serialize(Reject(string.Empty, string.Empty, "invalid_json", exception.Message)); }
        if (request is null)
            return Serialize(Reject(string.Empty, string.Empty, "invalid_json", "Request body is empty."));

        var owner = currentOwner();
        var error = Validate(request, owner, providerInstanceId);
        if (error is not null)
            return Serialize(Reject(request.RequestId, request.OperationId, error.Value.Code, error.Value.Message));
        var canonicalHash = CanonicalHash(request);
        lock (gate)
        {
            var persisted = repository.Read(state => state.Requests.FirstOrDefault(record => record.RequestId == request.RequestId));
            if (persisted is not null)
            {
                if (persisted.CanonicalHash != canonicalHash)
                    return Serialize(Reject(request.RequestId, request.OperationId, "request_conflict", "Request ID was already used with a different canonical payload."));
                var operation = repository.Read(state => state.Operations.Single(record => record.OperationId == persisted.OperationId));
                return Serialize(Acknowledge(operation.RequestId, operation.OperationId, operation.Status, operation.Revision, "Idempotent replay returned the existing deposit operation."));
            }
            if (pending.TryGetValue(request.RequestId, out var queued))
            {
                return queued.CanonicalHash == canonicalHash
                    ? Serialize(Acknowledge(request.RequestId, queued.OperationId, OperationStatuses.Queued, 0, "Idempotent replay returned the queued deposit operation."))
                    : Serialize(Reject(request.RequestId, request.OperationId, "request_conflict", "Request ID is queued with a different canonical payload."));
            }
            var plan = BuildPlan(request, owner);
            if (plan.Lines.Any(line => line.PlannedQuantity != line.PlayerQuantity))
                return Serialize(Reject(request.RequestId, request.OperationId, "insufficient_retainer_capacity", "Known owner-scoped retainer capacity cannot accept the complete requested deposit."));
            if (repository.Read(state => state.Operations.Any(operation => operation.OperationId == request.OperationId)) ||
                pending.Values.Any(candidate => candidate.OperationId == request.OperationId))
                return Serialize(Reject(request.RequestId, request.OperationId, "operation_conflict", "Operation ID is already in use."));

            transientOperations.Remove(request.OperationId);
            pending.Add(request.RequestId, new(canonicalHash, request.RequestId, request.OperationId, request.SubmittedAtUtc, request.ExecuteImmediately, request.Owner));
            workQueue.Enqueue(() => Apply(request, canonicalHash));
        }
        return Serialize(Acknowledge(request.RequestId, request.OperationId, OperationStatuses.Queued, 0,
            request.ExecuteImmediately
                ? "Exact elemental deposit queued for persistence and automatic execution."
                : "Exact elemental deposit queued for persistence and review."));
    }

    public string? GetPendingOperation(string operationId)
    {
        lock (gate)
        {
            if (pending.Values.FirstOrDefault(candidate => candidate.OperationId == operationId) is { } queued)
                return PendingJson(queued, OperationStatuses.Queued, queued.SubmittedAtUtc, null, "Deposit request is queued for framework-thread persistence.");
            if (transientOperations.TryGetValue(operationId, out var failed))
                return PendingJson(failed, OperationStatuses.Failed, failed.SubmittedAtUtc, failed.FailedAtUtc, failed.Message);
            return null;
        }
    }

    private void Apply(ElementalDepositRequest request, string canonicalHash)
    {
        try
        {
            var owner = currentOwner();
            var error = Validate(request, owner, providerInstanceId);
            if (error is not null)
                throw new InvalidOperationException(error.Value.Message);
            var plan = BuildPlan(request, owner);
            if (plan.Lines.Any(line => line.PlannedQuantity != line.PlayerQuantity))
                throw new InvalidOperationException("Known owner-scoped retainer capacity no longer covers the complete requested deposit.");
            journal.CreateDeposit(owner, plan, request.RequestId, request.OperationId, request.ExecuteImmediately, canonicalHash);
            lock (gate)
            {
                pending.Remove(request.RequestId);
                transientOperations.Remove(request.OperationId);
            }
            OperationChanged?.Invoke(request.OperationId);
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                pending.Remove(request.RequestId);
                transientOperations[request.OperationId] = new(
                    canonicalHash,
                    request.RequestId,
                    request.OperationId,
                    request.SubmittedAtUtc,
                    request.ExecuteImmediately,
                    request.Owner,
                    DateTime.SpecifyKind(utcNow(), DateTimeKind.Utc),
                    $"Quartermaster could not persist the accepted deposit request: {exception.Message}");
            }
            OperationChanged?.Invoke(request.OperationId);
        }
    }

    private ElementalDepositPlan BuildPlan(ElementalDepositRequest request, OwnerScope owner)
    {
        var names = request.Items.ToDictionary(item => item.ItemId, item => item.ItemName.Trim());
        return ElementalDepositPlanner.Build(
            request.Items.ToDictionary(item => item.ItemId, item => item.MaximumQuantity),
            cache(),
            owner,
            itemId => names.GetValueOrDefault(itemId),
            utcNow());
    }

    private static (string Code, string Message)? Validate(ElementalDepositRequest request, OwnerScope owner, string providerInstanceId)
    {
        if (request.Schema != RequestSchema)
            return ("unsupported_schema", $"Expected schema '{RequestSchema}'.");
        if (string.IsNullOrWhiteSpace(request.ProviderInstanceId) || request.ProviderInstanceId != providerInstanceId)
            return ("provider_mismatch", "providerInstanceId does not match this Quartermaster provider instance.");
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return ("missing_request_id", "requestId is required.");
        if (string.IsNullOrWhiteSpace(request.OperationId))
            return ("missing_operation_id", "operationId is required.");
        if (request.SubmittedAtUtc == default)
            return ("missing_submitted_at", "submittedAtUtc is required.");
        if (!owner.HasStableIdentity)
            return ("missing_owner_scope", "Current character owner scope is unavailable.");
        if (request.Owner.LocalContentId == 0 || request.Owner.HomeWorldId == 0)
            return ("missing_owner_scope", "owner.localContentId and owner.homeWorldId are required.");
        if (request.Owner.LocalContentId != owner.LocalContentId || request.Owner.HomeWorldId != owner.HomeWorldId)
            return ("owner_mismatch", "Request owner does not match the current local character and home world.");
        if (request.Items.Count == 0)
            return ("empty_items", "At least one shard is required.");
        if (request.Items.GroupBy(item => item.ItemId).Any(group => group.Count() > 1))
            return ("duplicate_item", "Duplicate item IDs are not allowed.");
        foreach (var item in request.Items)
        {
            if (!ElementalCurrencyCatalog.ShardItemIds.Contains(item.ItemId) || string.IsNullOrWhiteSpace(item.ItemName))
                return ("invalid_item", "V1 elemental deposits accept the six shard currencies only.");
            if (item.MaximumQuantity <= 0)
                return ("invalid_quantity", "maximumQuantity must be positive.");
        }
        return null;
    }

    private static string CanonicalHash(ElementalDepositRequest request)
    {
        var canonical = new StringBuilder()
            .Append(request.Schema).Append('|')
            .Append(request.RequestId.Trim()).Append('|')
            .Append(request.OperationId.Trim()).Append('|')
            .Append(request.SubmittedAtUtc.ToUniversalTime().ToString("O")).Append('|')
            .Append(request.Owner.LocalContentId).Append('|')
            .Append(request.Owner.HomeWorldId).Append('|')
            .Append(request.ExecuteImmediately ? '1' : '0');
        foreach (var item in request.Items.OrderBy(item => item.ItemId))
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.MaximumQuantity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private string PendingJson(PendingRequest operation, string status, DateTimeOffset created, DateTime? completed, string message) =>
        JsonSerializer.Serialize(new
        {
            schema = "gooseworks-quartermaster-operation/v1",
            providerInstanceId,
            operationId = operation.OperationId,
            requestId = operation.RequestId,
            kind = OperationKinds.Deposit,
            executeImmediately = operation.ExecuteImmediately,
            owner = operation.Owner,
            status,
            revision = status == OperationStatuses.Queued ? 0 : 1,
            createdAtUtc = created,
            updatedAtUtc = completed ?? created.UtcDateTime,
            completedAtUtc = completed,
            message,
        }, JsonOptions);

    private SubmissionAcknowledgement Reject(string requestId, string operationId, string code, string message) => new()
    {
        ProviderInstanceId = providerInstanceId,
        RequestId = requestId,
        OperationId = operationId,
        Status = OperationStatuses.Rejected,
        Revision = repository.Read(state => state.Revision),
        ErrorCode = code,
        Message = message,
    };

    private SubmissionAcknowledgement Acknowledge(string requestId, string operationId, string status, long revision, string message) => new()
    {
        ProviderInstanceId = providerInstanceId,
        RequestId = requestId,
        OperationId = operationId,
        Status = status,
        Revision = revision,
        Message = message,
    };

    private static string Serialize(SubmissionAcknowledgement acknowledgement) => JsonSerializer.Serialize(acknowledgement, JsonOptions);
    private record PendingRequest(string CanonicalHash, string RequestId, string OperationId, DateTimeOffset SubmittedAtUtc, bool ExecuteImmediately, RequestOwner Owner);
    private sealed record TransientOperation(string CanonicalHash, string RequestId, string OperationId, DateTimeOffset SubmittedAtUtc, bool ExecuteImmediately, RequestOwner Owner, DateTime FailedAtUtc, string Message)
        : PendingRequest(CanonicalHash, RequestId, OperationId, SubmittedAtUtc, ExecuteImmediately, Owner);
}
