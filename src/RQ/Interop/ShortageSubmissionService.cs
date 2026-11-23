using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RQ.Domain;
using RQ.Persistence;

namespace RQ.Interop;

public sealed class ShortageSubmissionService
{
    public const string RequestSchema = "gooseworks-quartermaster-shortages/v1";
    /// <summary>Shortage items may name the only retainers (and optionally the saddlebag) retrieval draws them from.</summary>
    public const string SourceRetainersCapability = "shortageSourceRetainers.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly string providerInstanceId;
    private readonly StateRepository repository;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<DateTime> utcNow;
    private readonly Func<IReadOnlyDictionary<ulong, CachedRetainer>>? retainers;
    private readonly Action<string>? warn;
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransientOperation> transientOperations = new(StringComparer.Ordinal);

    public event Action<string>? OperationChanged;

    public ShortageSubmissionService(
        string providerInstanceId,
        StateRepository repository,
        IFrameworkWorkQueue workQueue,
        Func<OwnerScope> currentOwner,
        Func<DateTime>? utcNow = null,
        Func<IReadOnlyDictionary<ulong, CachedRetainer>>? retainers = null,
        Action<string>? warn = null)
    {
        this.retainers = retainers;
        this.warn = warn;
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

        var owner = currentOwner();
        var error = Validate(request, owner, providerInstanceId);
        if (error is not null)
            return Serialize(Reject(request.RequestId, request.OperationId, error.Value.Code, error.Value.Message));
        if (ValidateSourceRestrictions(request, owner) is { } refusal)
        {
            warn?.Invoke($"[Quartermaster] Shortage request {request.RequestId} refused: {refusal.Code}: {refusal.Message}");
            return Serialize(Reject(request.RequestId, request.OperationId, refusal.Code, refusal.Message) with
            {
                ItemId = refusal.ItemId,
                RetainerId = refusal.RetainerId,
            });
        }

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
            var operationPlanItems = request.Items.Select(source => new TargetPlanItem
            {
                ItemId = source.ItemId,
                ItemName = source.ItemName.Trim(),
                TargetQuantity = source.TargetQuantity,
                Enabled = true,
            }).ToList();
            if (!request.ExecuteImmediately)
            {
                // The reviewed plan is configuration authority, while request and
                // execution evidence belong to the operation journal. Persist the
                // small plan document first; a retry is idempotent if journal
                // creation fails or the process stops between the two stores.
                operationPlanItems = repository.Mutate(StateChangeKind.Plans, state =>
                {
                    foreach (var source in request.Items)
                    {
                        var existing = state.PlanItems.FirstOrDefault(item => item.ItemId == source.ItemId);
                        if (existing is null)
                        {
                            existing = new TargetPlanItem
                            {
                                ItemId = source.ItemId,
                                ItemName = source.ItemName.Trim(),
                                TargetQuantity = source.TargetQuantity,
                                Enabled = true,
                            };
                            state.PlanItems.Add(existing);
                        }
                        else
                        {
                            existing.ItemName = source.ItemName.Trim();
                            existing.TargetQuantity = source.TargetQuantity;
                        }
                    }
                    return request.Items
                        .Select(source => Copy(state.PlanItems.First(item => item.ItemId == source.ItemId)))
                        .ToList();
                });
            }

            repository.Mutate(StateChangeKind.Operations, state =>
            {
                if (state.Requests.Any(record => record.RequestId == request.RequestId))
                    return;
                var now = utcNow();
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
                        ? "Ephemeral shortages accepted; automatic execution will start when matching owner and automation are ready."
                        : "Shortages accepted into the plan; review and explicit execution are required.",
                    SourcePlanItems = operationPlanItems.Select(Copy).ToList(),
                    Lines = request.Items.Select(item => new OperationLine
                    {
                        ItemId = item.ItemId,
                        ItemName = item.ItemName.Trim(),
                        TargetQuantity = item.TargetQuantity,
                        ShortageQuantity = item.ShortageQuantity,
                        SourceRetainerIds = item.SourceRetainerIds?.Distinct().ToList(),
                        SourceSaddlebag = item.SourceRetainerIds is not null && item.SourceSaddlebag == true,
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

    /// <summary>
    /// Source restrictions name current retainers of the owner, apply only to automatic execution (a reviewed request
    /// becomes the user's own plan, which carries no restriction), and always leave at least one source.
    /// </summary>
    private (string Code, string Message, uint? ItemId, ulong? RetainerId)? ValidateSourceRestrictions(ShortageRequest request, OwnerScope owner)
    {
        var restricted = request.Items.Where(item => item.SourceRetainerIds is not null || item.SourceSaddlebag is not null).ToArray();
        if (restricted.Length == 0)
            return null;
        foreach (var item in restricted)
        {
            if (item.SourceRetainerIds is null)
                return ("invalid_source_restriction", $"Item {item.ItemId}: sourceSaddlebag needs sourceRetainerIds.", item.ItemId, null);
            if (item.SourceRetainerIds.Count == 0 && item.SourceSaddlebag != true)
                return ("invalid_source_restriction", $"Item {item.ItemId}: sourceRetainerIds is empty and the saddlebag is not allowed, so nothing could supply it.", item.ItemId, null);
            if (item.SourceRetainerIds.Contains(0UL))
                return ("invalid_source_restriction", $"Item {item.ItemId}: retainer id 0 is not a retainer.", item.ItemId, 0UL);
        }
        if (!request.ExecuteImmediately)
            return ("source_restriction_requires_immediate", "Source restrictions apply only to executeImmediately requests; a reviewed request becomes the user's own plan.", restricted[0].ItemId, null);
        if (retainers is null)
            return ("source_restriction_unavailable", "This Quartermaster build cannot check source retainers.", restricted[0].ItemId, null);
        var roster = retainers();
        foreach (var item in restricted)
        foreach (var id in item.SourceRetainerIds!)
        {
            if (!roster.TryGetValue(id, out var retainer) || !retainer.Owner.Matches(owner))
                return ("unknown_source_retainer", $"Item {item.ItemId}: retainer {id} is not a known retainer of {owner.CharacterName}.", item.ItemId, id);
            if (retainer.IsCurrentlyAssigned is false)
                return ("unknown_source_retainer", $"Item {item.ItemId}: retainer {id} ({retainer.RetainerName}) is no longer assigned to {owner.CharacterName}.", item.ItemId, id);
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
        {
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.TargetQuantity).Append('|').Append(item.ShortageQuantity);
            // Appended only when present so unrestricted requests keep their earlier hashes.
            if (item.SourceRetainerIds is { } sources)
                canonical.Append("|src:").Append(string.Join(',', sources.Distinct().Order()))
                    .Append("|sb:").Append(item.SourceSaddlebag == true ? '1' : '0');
        }
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
