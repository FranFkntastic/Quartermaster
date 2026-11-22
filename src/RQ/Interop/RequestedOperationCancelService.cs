using System.Text.Json;
using RQ.Domain;
using RQ.Operations;

namespace RQ.Interop;

/// <summary>
/// Quartermaster.v1.CancelRequestedOperation (capability requestedOperationCancel.v1): the requester of an
/// automatic operation (SubmitShortages, SubmitElementalDeposit, SubmitDeliveredStow) withdraws it by request id.
/// An operation that has not started is cancelled; a running one finishes the retainer it is on and stops.
/// A request still queued for persistence is cancelled right after it persists (status cancel_queued).
/// </summary>
public sealed class RequestedOperationCancelService
{
    public const string Capability = "requestedOperationCancel.v1";
    public const string RequestSchema = "gooseworks-quartermaster-cancel-request/v1";
    public const string AckSchema = "gooseworks-quartermaster-cancel-request-ack/v1";
    public const string Channel = "Quartermaster.v1.CancelRequestedOperation";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string providerInstanceId;
    private readonly OperationJournal journal;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly Action<string> warn;

    public RequestedOperationCancelService(string providerInstanceId, OperationJournal journal, IFrameworkWorkQueue workQueue, Action<string> warn)
    {
        this.providerInstanceId = providerInstanceId;
        this.journal = journal;
        this.workQueue = workQueue;
        this.warn = warn;
    }

    public string Cancel(string requestJson)
    {
        CancelRequest? request;
        try { request = JsonSerializer.Deserialize<CancelRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Serialize(Ack(string.Empty, "rejected", exception.Message, errorCode: "invalid_json")); }
        if (request is null)
            return Serialize(Ack(string.Empty, "rejected", "Request body is empty.", errorCode: "invalid_json"));
        if (request.Schema != RequestSchema)
            return Serialize(Ack(request.RequestId, "rejected", $"Expected schema '{RequestSchema}'.", errorCode: "unsupported_schema"));
        if (string.IsNullOrWhiteSpace(request.ProviderInstanceId) || request.ProviderInstanceId != providerInstanceId)
            return Serialize(Ack(request.RequestId, "rejected", "providerInstanceId does not match this Quartermaster provider instance.", errorCode: "provider_mismatch"));
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Serialize(Ack(string.Empty, "rejected", "requestId is required.", errorCode: "missing_request_id"));

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled by its requester." : $"Cancelled by its requester: {request.Reason.Trim()}";
        var result = journal.CancelRequested(request.RequestId, reason);
        if (result is null)
        {
            // Possibly still queued for persistence on the framework thread; that queue is FIFO, so this
            // runs after the persisting step and cancels whatever it created.
            var requestId = request.RequestId;
            workQueue.Enqueue(() =>
            {
                if (journal.CancelRequested(requestId, reason) is { } deferred)
                    warn($"[Quartermaster] CancelledByRequester: request {requestId} ({deferred.Operation.OperationId}) {deferred.Outcome} after it persisted.");
            });
            return Serialize(Ack(request.RequestId, "cancel_queued", "No persisted operation yet; any queued request is cancelled once it persists."));
        }
        var (outcome, operation) = result.Value;
        if (outcome != "already_terminal")
            warn($"[Quartermaster] CancelledByRequester: request {request.RequestId} ({operation.OperationId}, {operation.Kind}) {outcome}. {reason}");
        return Serialize(Ack(request.RequestId, outcome, outcome switch
        {
            "cancelled" => "The operation had not started and was cancelled.",
            "stopping" => "The operation is running; it stops after the current retainer.",
            _ => $"The operation already ended ({operation.Status}).",
        }, operation.OperationId, operation.Status));
    }

    private CancelAcknowledgement Ack(string requestId, string status, string message, string? operationId = null, string? operationStatus = null, string? errorCode = null) => new()
    {
        Schema = AckSchema,
        ProviderInstanceId = providerInstanceId,
        RequestId = requestId ?? string.Empty,
        OperationId = operationId,
        Status = status,
        OperationStatus = operationStatus,
        ErrorCode = errorCode,
        Message = message,
    };

    private static string Serialize(CancelAcknowledgement ack) => JsonSerializer.Serialize(ack, JsonOptions);

    public sealed record CancelRequest
    {
        public string Schema { get; init; } = string.Empty;
        public string ProviderInstanceId { get; init; } = string.Empty;
        public string RequestId { get; init; } = string.Empty;
        public string? Reason { get; init; }
    }

    /// <summary>Status: cancelled, stopping, already_terminal, cancel_queued or rejected (ErrorCode names why).</summary>
    public sealed record CancelAcknowledgement
    {
        public string Schema { get; init; } = AckSchema;
        public string ProviderInstanceId { get; init; } = string.Empty;
        public string RequestId { get; init; } = string.Empty;
        public string? OperationId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? OperationStatus { get; init; }
        public string? ErrorCode { get; init; }
        public string Message { get; init; } = string.Empty;
    }
}
