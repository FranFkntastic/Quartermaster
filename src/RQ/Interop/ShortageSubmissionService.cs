using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RQ.Domain;
using RQ.Persistence;

namespace RQ.Interop;

public sealed class ShortageSubmissionService
{
    public const string RequestSchema = "gooseworks-quartermaster-shortages/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly string providerInstanceId;
    private readonly StateRepository repository;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<DateTime> utcNow;
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransientOperation> transientOperations = new(StringComparer.Ordinal);

    public event Action<string>? OperationChanged;

    public ShortageSubmissionService(
        string providerInstanceId,
        StateRepository repository,
        IFrameworkWorkQueue workQueue,
        Func<OwnerScope> currentOwner,
        Func<DateTime>? utcNow = null)
    {
        this.providerInstanceId = providerInstanceId;
        this.repository = repository;
        this.workQueue = workQueue;
        this.currentOwner = currentOwner;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Submit(string requestJson)
    {
        ShortageRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ShortageRequest>(requestJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Serialize(Reject(string.Empty, string.Empty, "invalid_json", exception.Message));
        }
        if (request is null)
            return Serialize(Reject(string.Empty, string.Empty, "invalid_json", "Request body is empty."));

        var error = Validate(request, currentOwner(), providerInstanceId);
        if (error is not null)
            return Serialize(Reject(request.RequestId, request.OperationId, error.Value.Code, error.Value.Message));

        var canonicalHash = CanonicalHash(request);
        lock (gate)
        {
            var persisted = repository.Read(state => state.Requests.FirstOrDefault(record => record.RequestId == request.RequestId));
            if (persisted is not null)
            {
                var matchesLegacyReviewRequest = !request.ExecuteImmediately && persisted.CanonicalHash == CanonicalHash(request, includeExecutionIntent: false);
                if (persisted.CanonicalHash != canonicalHash && !matchesLegacyReviewRequest)
                    return Serialize(Reject(request.RequestId, request.OperationId, "request_conflict", "Request ID was already used with a different canonical payload."));
                var operation = repository.Read(state => state.Operations.Single(record => record.OperationId == persisted.OperationId));
                return Serialize(Acknowledge(operation.RequestId, operation.OperationId, OperationStatuses.Accepted, operation.Revision, $"Idempotent replay returned existing {(operation.ExecuteImmediately ? "automatic" : "review-required")} operation in '{operation.Status}' state."));
            }
            if (pending.TryGetValue(request.RequestId, out var pendingRequest))
            {
                return pendingRequest.CanonicalHash == canonicalHash
                    ? Serialize(Acknowledge(request.RequestId, pendingRequest.OperationId, OperationStatuses.Queued, 0, $"Idempotent replay returned the queued {(pendingRequest.ExecuteImmediately ? "automatic" : "review-required")} operation."))
                    : Serialize(Reject(request.RequestId, request.OperationId, "request_conflict", "Request ID is queued with a different canonical payload."));
            }
            var operationCollision = repository.Read(state => state.Operations.Any(operation => operation.OperationId == request.OperationId));
            if (operationCollision || pending.Values.Any(candidate => candidate.OperationId == request.OperationId))
                return Serialize(Reject(request.RequestId, request.OperationId, "operation_conflict", "Operation ID is already in use."));

            transientOperations.Remove(request.OperationId);
            pending.Add(request.RequestId, new(canonicalHash, request.RequestId, request.OperationId, request.SubmittedAtUtc, request.ExecuteImmediately, request.Owner));
            workQueue.Enqueue(() => Apply(request, canonicalHash));
        }
        return Serialize(Acknowledge(
            request.RequestId,
            request.OperationId,
            OperationStatuses.Queued,
            0,
            request.ExecuteImmediately
                ? "Request queued for framework-thread persistence and automatic execution."
                : "Request queued for framework-thread persistence and review."));
    }

    public string? GetPendingOperation(string operationId)
    {
        lock (gate)
        {
            if (pending.Values.FirstOrDefault(candidate => candidate.OperationId == operationId) is { } queued)
            {
                return JsonSerializer.Serialize(new
                {
                    schema = "gooseworks-quartermaster-operation/v1",
                    providerInstanceId,
                    operationId = queued.OperationId,
                    requestId = queued.RequestId,
                    kind = OperationKinds.Retrieval,
                    executeImmediately = queued.ExecuteImmediately,
                    owner = queued.Owner,
                    status = OperationStatuses.Queued,
                    revision = 0,
                    createdAtUtc = queued.SubmittedAtUtc,
                    updatedAtUtc = queued.SubmittedAtUtc,
                    message = queued.ExecuteImmediately
                        ? "Request is queued for framework-thread persistence and automatic execution."
                        : "Request is queued for framework-thread persistence and review.",
                }, JsonOptions);
            }
            if (transientOperations.TryGetValue(operationId, out var failed))
            {
                return JsonSerializer.Serialize(new
                {
                    schema = "gooseworks-quartermaster-operation/v1",
                    providerInstanceId,
                    operationId = failed.OperationId,
                    requestId = failed.RequestId,
                    kind = OperationKinds.Retrieval,
                    executeImmediately = failed.ExecuteImmediately,
                    owner = failed.Owner,
                    status = OperationStatuses.Failed,
                    revision = 1,
                    createdAtUtc = failed.SubmittedAtUtc,
                    updatedAtUtc = failed.FailedAtUtc,
                    completedAtUtc = failed.FailedAtUtc,
                    message = failed.Message,
                }, JsonOptions);
            }
            return null;
        }
    }

    private void Apply(ShortageRequest request, string canonicalHash)
    {
        try
        {
            repository.Mutate(state =>
            {
                if (state.Requests.Any(record => record.RequestId == request.RequestId))
                    return;
                var now = utcNow();
                foreach (var source in request.Items)
                {
                    var existing = state.PlanItems.FirstOrDefault(item => item.ItemId == source.ItemId);
                    if (existing is null)
                    {
                        state.PlanItems.Add(new TargetPlanItem
                        {
                            ItemId = source.ItemId,
                            ItemName = source.ItemName.Trim(),
                            TargetQuantity = source.TargetQuantity,
                            Enabled = true,
                        });
                    }
                    else
                    {
                        existing.ItemName = source.ItemName.Trim();
                        existing.TargetQuantity = source.TargetQuantity;
                    }
                }
                state.Requests.Add(new SubmittedRequestRecord
                {
                    RequestId = request.RequestId,
                    OperationId = request.OperationId,
                    CanonicalHash = canonicalHash,
                    AcceptedAtUtc = now,
                });
                var operation = new OperationRecord
                {
                    RequestId = request.RequestId,
                    OperationId = request.OperationId,
                    Kind = OperationKinds.Retrieval,
                    ExecuteImmediately = request.ExecuteImmediately,
                    Owner = new OwnerScope
                    {
                        LocalContentId = request.Owner.LocalContentId,
                        HomeWorldId = request.Owner.HomeWorldId,
                        CharacterName = request.Owner.CharacterName.Trim(),
                    },
                    Status = OperationStatuses.Accepted,
                    Revision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Message = request.ExecuteImmediately
                        ? "Shortages accepted; automatic execution will start when matching owner and automation are ready."
                        : "Shortages accepted into the plan; review and explicit execution are required.",
                    SourcePlanItems = request.Items.Select(item => Copy(state.PlanItems.First(plan => plan.ItemId == item.ItemId))).ToList(),
                    Lines = request.Items.Select(item => new OperationLine
                    {
                        ItemId = item.ItemId,
                        ItemName = item.ItemName.Trim(),
                        TargetQuantity = item.TargetQuantity,
                        ShortageQuantity = item.ShortageQuantity,
                    }).ToList(),
                };
                state.Operations.Add(operation);
                state.Receipts.Add(new OperationReceipt
                {
                    OperationId = operation.OperationId,
                    Revision = operation.Revision,
                    OccurredAtUtc = now,
                    Status = operation.Status,
                    Code = "ShortagesAccepted",
                    Message = operation.Message,
                });
            });
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
                    request.RequestId,
                    request.OperationId,
                    request.SubmittedAtUtc,
                    request.ExecuteImmediately,
                    request.Owner,
                    DateTime.SpecifyKind(utcNow(), DateTimeKind.Utc),
                    $"Quartermaster could not persist the accepted request: {exception.Message}");
                while (transientOperations.Count > 100)
                    transientOperations.Remove(transientOperations.Keys.First());
            }
            OperationChanged?.Invoke(request.OperationId);
        }
    }

    private static (string Code, string Message)? Validate(ShortageRequest request, OwnerScope owner, string providerInstanceId)
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
            return ("empty_items", "At least one shortage item is required.");
        if (request.Items.GroupBy(item => item.ItemId).Any(group => group.Count() > 1))
            return ("duplicate_item", "Duplicate item IDs are not allowed.");
        foreach (var item in request.Items)
        {
            if (item.ItemId == 0 || string.IsNullOrWhiteSpace(item.ItemName))
                return ("invalid_item", "Each item requires an internal itemId and display itemName.");
            if (item.TargetQuantity <= 0 || item.ShortageQuantity <= 0)
                return ("invalid_quantity", "Target and shortage quantities must be positive.");
            if (item.ShortageQuantity > item.TargetQuantity)
                return ("invalid_quantity", "Shortage quantity cannot exceed target quantity.");
        }
        return null;
    }

    private static string CanonicalHash(ShortageRequest request, bool includeExecutionIntent = true)
    {
        var canonical = new StringBuilder()
            .Append(request.Schema).Append('|')
            .Append(request.RequestId.Trim()).Append('|')
            .Append(request.OperationId.Trim()).Append('|')
            .Append(request.SubmittedAtUtc.ToUniversalTime().ToString("O")).Append('|')
            .Append(request.Owner.LocalContentId).Append('|')
            .Append(request.Owner.HomeWorldId);
        if (includeExecutionIntent)
            canonical.Append('|').Append(request.ExecuteImmediately ? '1' : '0');
        foreach (var item in request.Items.OrderBy(item => item.ItemId))
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.TargetQuantity).Append('|').Append(item.ShortageQuantity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static TargetPlanItem Copy(TargetPlanItem item) => new()
    {
        Id = item.Id,
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        TargetQuantity = item.TargetQuantity,
        Notes = item.Notes,
        Enabled = item.Enabled,
    };

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
    private sealed record PendingRequest(string CanonicalHash, string RequestId, string OperationId, DateTimeOffset SubmittedAtUtc, bool ExecuteImmediately, RequestOwner Owner);
    private sealed record TransientOperation(string RequestId, string OperationId, DateTimeOffset SubmittedAtUtc, bool ExecuteImmediately, RequestOwner Owner, DateTime FailedAtUtc, string Message);
}
