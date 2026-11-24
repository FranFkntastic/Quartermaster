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
    public const string ClearCourierRun = "Quartermaster.v1.ClearCourierRun";
    public const string PairCourierOwner = "Quartermaster.v1.PairCourierOwner";
    public const string GetStockSources = "Quartermaster.v1.GetStockSources";
    public const string GetReserveItems = "Quartermaster.v1.GetReserveItems";
    public const string OpenPlacement = "Quartermaster.v1.OpenPlacement";
    public const string SetReserveLevel = "Quartermaster.v1.SetReserveLevel";
    public const string PlanArrival = "Quartermaster.v1.PlanArrival";
}

/// <summary>
/// <c>gooseworks-quartermaster-arrival-plan-request/v1</c>: where Quantity units of each item would go if they arrived in
/// Owner's bags now. Owner is optional (the logged-in character when absent).
/// </summary>
public sealed record ArrivalPlanRequest
{
    public string Schema { get; init; } = string.Empty;
    public RequestOwner? Owner { get; init; }
    public IReadOnlyList<ArrivalPlanRequestItem>? Items { get; init; }
}

public sealed record ArrivalPlanRequestItem
{
    public uint ItemId { get; init; }
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
}

/// <summary><c>gooseworks-quartermaster-stock-sources-request/v1</c>: which items to count per source.</summary>
public sealed record StockSourcesRequest
{
    public string Schema { get; init; } = string.Empty;
    public IReadOnlyList<uint>? ItemIds { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-open-placement/v1</c>: opens Quartermaster's placement editor for the current
/// character with one row per item. Quality is <c>NqOnly</c> or <c>HqOnly</c>.
/// </summary>
public sealed record OpenPlacementRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public RequestOwner? Owner { get; init; }
    public IReadOnlyList<OpenPlacementItem>? Items { get; init; }
}

public sealed record OpenPlacementItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
}

/// <summary><c>gooseworks-quartermaster-reserve-level/v1</c>: how many spare units of an item to keep.</summary>
public sealed record ReserveLevelRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public RequestOwner? Owner { get; init; }
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
    public int ReserveUnits { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-reserve-edit-ack/v1</c>. Status: queued (applied on the next framework
/// tick, then announced through Changed) or rejected (ErrorCode names why).
/// </summary>
public sealed record ReserveEditAcknowledgement
{
    public string Schema { get; init; } = "gooseworks-quartermaster-reserve-edit-ack/v1";
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>A roster character named by an external courier request.</summary>
public sealed record CourierTransferCharacter
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;
}

/// <summary>
/// RetainerId (optional, capability <c>courierTransferToRetainer.v1</c>): a retainer of the destination to deposit these
/// units into after the hand-in; absent, they stay in the destination's bags. One item may appear several times with
/// different retainers. Refusals: RetainerUnreachableAtPlaza, DestinationBagsUnobserved, DestinationBagsFull,
/// DestinationRetainerUnknown, DestinationRetainerFull.
/// </summary>
public sealed record CourierTransferRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? RetainerId { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-courier-transfer/v1</c>: move carried gil and bag-held items from one roster
/// character to another through the carrier. Source may also be the carrier itself (as GetCourierStatus names it):
/// it then delivers stock it already carries in one leg. RequestId is the idempotency key across every run this
/// client has journaled; reusing it with a different payload is refused as RequestConflict. A run that provably moved
/// nothing (refused, or stopped before any hand-in) may run again under its RequestId; one that may have delivered part
/// is refused as CourierRunCleared once cleared.
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

/// <summary>
/// Stop, resume or clear a courier run. For stop and resume an empty RunId means the current run; clear requires
/// the RunId of the run to forget. RequestId is optional and only echoed in the acknowledgement and the log.
/// </summary>
public sealed record CourierRunRequest
{
    public string? RunId { get; init; }
    public string? RequestId { get; init; }
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

/// <summary>
/// SourceRetainerIds (optional, capability <c>shortageSourceRetainers.v1</c>): the only retainers of the owner this
/// item may be retrieved from. SourceSaddlebag (only with SourceRetainerIds) also allows the owner's saddlebag.
/// Absent, retrieval may draw from any owner retainer and the saddlebag as before. A restricted item that the allowed
/// sources cannot cover is retrieved as far as they reach and the rest is reported, never topped up elsewhere.
/// </summary>
public sealed record ShortageRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int TargetQuantity { get; init; }
    public int ShortageQuantity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ulong>? SourceRetainerIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SourceSaddlebag { get; init; }
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
    /// <summary>The item a rejection is about, when it is about one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ItemId { get; init; }
    /// <summary>The retainer a rejection is about, when it is about one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? RetainerId { get; init; }
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

// ---- Delivered stow (capability deliveredStow.v1) ------------------------------------------------
// Kept as its own block so it merges independently of other contract additions.

public static class DeliveredStowIpcChannels
{
    public const string SubmitDeliveredStow = "Quartermaster.v1.SubmitDeliveredStow";
    public const string GetDeliveredStow = "Quartermaster.v1.GetDeliveredStow";
}

/// <summary>
/// Quantity: units this delivery put into the owner's bags (never units sent anywhere else). SentDirect (optional):
/// units of the item the caller delivered straight to its homes on another character; Quartermaster never forwards
/// them and reports them on the line, so Quantity may be 0 when all of it went there.
/// </summary>
public sealed record DeliveredStowRequestItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SentDirect { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-delivered-stow/v1</c>: stow goods a caller just delivered into the logged-in
/// owner's bags into each item's home retainers (the owner's Listing Plan reserve "Goes to"). Quantities are
/// upper bounds, capped by the owner's carried bags; items without a home stay in the bags. RequestId is the
/// idempotency key. AllowForward (optional, capability deliveredStowForward.v1): carry what the owner's own homes
/// leave on to the item's homes on another character with the courier; without it such items stay in the bags
/// (NoHome) exactly as before forwarding existed.
/// </summary>
public sealed record DeliveredStowRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public RequestOwner? Owner { get; init; }
    public IReadOnlyList<DeliveredStowRequestItem>? Items { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowForward { get; init; }
}

/// <summary>
/// One requested item variant. Code: Planned, NoHome, NoRoom, NotCarried, Stowed, PartlyStowed, NotStowed,
/// HomedElsewhere (no home among the owner's retainers, homes on another character: see Forward), or SentDirect
/// (nothing delivered to the owner; the caller sent it all straight to the item's homes on another character).
/// Carried (acknowledgement only) is the requested quantity found in the bags; Planned is what fits the
/// listed home retainers, so Planned &lt; Carried means part had no room and Carried &lt; Requested means
/// part was not carried. Code, Planned and Stowed describe the owner's own retainers only.
/// </summary>
public sealed record DeliveredStowLine
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Requested { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Carried { get; init; }
    public int Planned { get; init; }
    public int Stowed { get; init; }
    public string Code { get; init; } = string.Empty;
    public IReadOnlyList<string> Destinations { get; init; } = [];
    /// <summary>Present only for an item with homes on another character (capability <c>deliveredStowForward.v1</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeliveredStowForwardLine? Forward { get; init; }
}

/// <summary>A character of this account, by its stable identity.</summary>
public sealed record DeliveredStowCharacter
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;
}

public sealed record DeliveredStowHome
{
    public ulong RetainerId { get; init; }
    /// <summary>Null when that retainer has not been observed under the character.</summary>
    public string? RetainerName { get; init; }
}

/// <summary>
/// The part of a line bound for another character's retainers, carried by the courier after the owner's own stow.
/// Wanted: units the local homes could not take (capped by what was carried). Planned: units the courier run carries
/// (set once it starts). Delivered: units deposited so far (null until known). Code: Pending, Running, Delivered,
/// PartlyDelivered, NotDelivered, NoRoom, NothingLeft, SentDirect (the caller already delivered the remote share), or a refusal (CourierNotLoaded, CourierBusy,
/// CourierRunUnresolved, CarrierNotConfigured, CarrierUnreachable, CarrierOptInOff, CarrierSourceUnsupported,
/// OwnerNotPaired, OwnerBagsUnobserved, OwnerChanged, OutsideClientRoster, InvalidParticipants, RegionUnreachable,
/// MeetingBellMissing, ItemNotTradeable, ItemMetadataUnknown, UniqueConflict, BagSlotLimit, SourceShort,
/// CarrierStockShort, InvalidManifest, PlanningFailed, RetainerUnreachableAtPlaza, DestinationBagsUnobserved,
/// DestinationBagsFull, DestinationRetainerUnknown, DestinationRetainerFull, AutomationBusy, LocalDepositMissing, LocalDepositUnverified,
/// LocalDepositCancelled, ForwardFailed, RunNotStarted, RunRefused, RunFailed, RunInterrupted, RunIndeterminate). Message says why in plain words.
/// Goods that are not forwarded stay in the owner's bags.
/// </summary>
public sealed record DeliveredStowForwardLine
{
    public DeliveredStowCharacter Character { get; init; } = new();
    public IReadOnlyList<DeliveredStowHome> Homes { get; init; } = [];
    public int Wanted { get; init; }
    public int Planned { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Delivered { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// The request's courier forward as a whole. State: pending (waiting for the owner's own stow to settle), running,
/// or settled. RunId names the courier run once one started; Code and Message name the outcome or the refusal.
/// </summary>
public sealed record DeliveredStowForwardSummary
{
    public string State { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Acknowledgement (<c>gooseworks-quartermaster-delivered-stow-ack/v1</c>) and status
/// (<c>gooseworks-quartermaster-delivered-stow-status/v1</c>). Status: queued, accepted, running, succeeded,
/// partially_succeeded, indeterminate, failed, cancelled, nothing_to_stow, rejected (ErrorCode names why) or not_found.
/// With a courier forward, Status stays non-terminal until the forward settles and then covers both parts;
/// DepositStatus is then the owner's own stow operation alone (absent when there is none) and Forward the courier part.
/// </summary>
public sealed record DeliveredStowStatus
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string? OperationId { get; init; }
    public string Status { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<DeliveredStowLine> Lines { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DepositStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeliveredStowForwardSummary? Forward { get; init; }
}

// ---- Placement edit (capability placementEdit.v1) ------------------------------------------------
// Kept as its own block so it merges independently of other contract additions.

public static class PlacementIpcChannels
{
    public const string GetPlacement = "Quartermaster.v1.GetPlacement";
    public const string EditPlacement = "Quartermaster.v1.EditPlacement";
}

/// <summary>
/// <c>gooseworks-quartermaster-placement-request/v1</c>: what the placement matrix shows. Items absent: every
/// enabled reserve item of the logged-in owner. Items present: exactly those (an item without a reserve rule reads
/// with no homes). Quality is <c>NqOnly</c> or <c>HqOnly</c>.
/// </summary>
public sealed record PlacementRequest
{
    public string Schema { get; init; } = string.Empty;
    public IReadOnlyList<PlacementItemKey>? Items { get; init; }
}

public sealed record PlacementItemKey
{
    public uint ItemId { get; init; }
    public string Quality { get; init; } = string.Empty;
}

/// <summary>A character by its stable identity.</summary>
public sealed record PlacementCharacterRef
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
}

/// <summary>One destination on the edited character: a retainer, or Any space (the owner only). Never both.</summary>
public sealed record PlacementDestination
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? RetainerId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AnySpace { get; init; }
}

/// <summary>
/// <c>gooseworks-quartermaster-placement-edit/v1</c>: one placement editor action on these items, as the Placement
/// window's toolbar applies it to selected rows. Operation: makeFirst, addFallback, remove, toggle (each needs
/// Destination), useCurrentHolders or clearDestinations (no Destination). Character: whose destinations the action
/// edits (the owner when absent); another character edits the item's remote homes. AllOrNothing: apply nothing when
/// any item is refused.
/// </summary>
public sealed record PlacementEditRequest
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public RequestOwner? Owner { get; init; }
    public IReadOnlyList<OpenPlacementItem>? Items { get; init; }
    public string Operation { get; init; } = string.Empty;
    public PlacementCharacterRef? Character { get; init; }
    public PlacementDestination? Destination { get; init; }
    public bool AllOrNothing { get; init; }
}
