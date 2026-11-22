using System.Text.Json.Serialization;
using RQ.Domain;

namespace RQ.Interop;

public static class IpcChannels
{
    public const string GetCapabilities = "Quartermaster.v1.GetCapabilities";
    public const string GetSnapshot = "Quartermaster.v1.GetSnapshot";
    public const string SubmitShortages = "Quartermaster.v1.SubmitShortages";
    public const string SubmitElementalDeposit = "Quartermaster.v1.SubmitElementalDeposit";
    public const string GetOperation = "Quartermaster.v1.GetOperation";
    public const string Changed = "Quartermaster.v1.Changed";
    public const string GetCourierStatus = "Quartermaster.v1.GetCourierStatus";
    public const string SubmitCourierTransfer = "Quartermaster.v1.SubmitCourierTransfer";
    public const string StopCourierRun = "Quartermaster.v1.StopCourierRun";
    public const string ResumeCourierRun = "Quartermaster.v1.ResumeCourierRun";
    public const string PairCourierOwner = "Quartermaster.v1.PairCourierOwner";
}

/// <summary>A roster character named by an external courier request.</summary>
public sealed record CourierTransferCharacter
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;
}

public sealed record CourierTransferRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-courier-transfer/v1</c>: move carried gil and bag-held items from one roster
/// character to another through the carrier. RequestId is the idempotency key.
/// </summary>
public sealed record CourierTransferRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public CourierTransferCharacter? Source { get; init; }
    public CourierTransferCharacter? Destination { get; init; }
    public int GilAmount { get; init; }
    public IReadOnlyList<CourierTransferRequestItem>? Items { get; init; }
    public bool ReturnOwner { get; init; }
    public bool ReturnDestinationHome { get; init; }
    public bool ReturnCarrier { get; init; }
}

/// <summary>Stop or resume a courier run; an empty RunId means the current run.</summary>
public sealed record CourierRunRequest
{
    public string? RunId { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-courier-transfer-ack/v1</c>. Status: accepted, already_running,
/// already_completed, stopping, idle or rejected (ErrorCode then names why).
/// </summary>
public sealed record CourierTransferAcknowledgement
{
    public string Schema { get; init; } = "gooseworks-quartermaster-courier-transfer-ack/v1";
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record ElementalDepositRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset SubmittedAtUtc { get; init; }
    public bool ExecuteImmediately { get; init; }
    public required RequestOwner Owner { get; init; }
    public IReadOnlyList<ElementalDepositRequestItem> Items { get; init; } = [];
}

public sealed record ElementalDepositRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int MaximumQuantity { get; init; }
}

public sealed record ShortageRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset SubmittedAtUtc { get; init; }
    public bool ExecuteImmediately { get; init; }
    public required RequestOwner Owner { get; init; }
    public IReadOnlyList<ShortageRequestItem> Items { get; init; } = [];
}

public sealed record RequestOwner
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
}

public sealed record ShortageRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int TargetQuantity { get; init; }
    public int ShortageQuantity { get; init; }
}

public sealed record SubmissionAcknowledgement
{
    public string Schema { get; init; } = "gooseworks-quartermaster-shortages-acknowledgement/v1";
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long Revision { get; init; }
    public string Message { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
}

public sealed record ChangedNotification(
    string Schema,
    string ProviderInstanceId,
    string OwnerScopeKey,
    long Revision,
    string Kind,
    string? OperationId);

public interface IFrameworkWorkQueue
{
    void Enqueue(Action action);
}

public sealed class FrameworkWorkQueue : IFrameworkWorkQueue
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
    public void Enqueue(Action action) => queue.Enqueue(action);
    public int Drain(int maximum = 32)
    {
        var count = 0;
        while (count < maximum && queue.TryDequeue(out var action))
        {
            action();
            count++;
        }
        return count;
    }
}
