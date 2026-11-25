using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Interop;

/// <summary>
/// Quartermaster.v1.SubmitDeliveredStow / GetDeliveredStow: stow goods another plugin just delivered into the
/// logged-in owner's bags into each item's home retainers. Homes are the owner's Listing Plan reserve "Goes to"
/// lists; items without a home stay in the bags. Execution reuses the stowage deposit path (one requested
/// <see cref="OperationKinds.StowageSurplus"/> operation for the whole batch, so each retainer opens once),
/// picked up by <see cref="AutomaticRetrievalQueue"/>. RequestId is the idempotency key.
/// An item's homes are one ranked list across the characters of this account (<see cref="ItemPlacementRule.Homes"/>),
/// routed by <see cref="PlacementRouter"/>, each home taking what its observed room and its cap allow. When the request
/// sets AllowForward, once the owner's own stow settles what the owner's homes did not take is carried to the other
/// characters' homes by the courier in one run (<see cref="Tick"/>), and the line's Forwards (one per character) say
/// how that went; without it only the owner's homes count and the rest stays in the bags. Only units still in the owner's bags are sent on,
/// never more than the request delivered; units the caller already sent straight to those homes (SentDirect) are
/// never forwarded. The forward is journaled in its own file before the local operation is created and a forward
/// with no local operation is never pruned, so a request id is answered the same across reloads; its courier run is
/// never started twice (an earlier run under the same courier request id is adopted).
/// </summary>
public sealed class DeliveredStowService
{
    public const string Capability = "deliveredStow.v1";
    public const string ForwardCapability = "deliveredStowForward.v1";
    /// <summary>Quartermaster.v1.CancelDeliveredStowForward: the requester withdraws a request's courier forward.</summary>
    public const string ForwardCancelCapability = "deliveredStowForwardCancel.v1";
    public const string ForwardCancelSchema = "gooseworks-quartermaster-delivered-stow-forward-cancel/v1";
    public const string ForwardCancelAckSchema = "gooseworks-quartermaster-delivered-stow-forward-cancel-ack/v1";
    public const string RequestSchema = "gooseworks-quartermaster-delivered-stow/v1";
    public const string AckSchema = "gooseworks-quartermaster-delivered-stow-ack/v1";
    public const string StatusSchema = "gooseworks-quartermaster-delivered-stow-status/v1";
    public const string NothingToStow = "nothing_to_stow";
    public const string NotFound = "not_found";

    public const string LinePlanned = "Planned";
    public const string LineNoHome = "NoHome";
    public const string LineNoRoom = "NoRoom";
    public const string LineNotCarried = "NotCarried";
    public const string LineStowed = "Stowed";
    public const string LinePartlyStowed = "PartlyStowed";
    public const string LineNotStowed = "NotStowed";
    public const string LineHomedElsewhere = "HomedElsewhere";
    public const string LineSentDirect = "SentDirect";

    public const string ForwardPending = "Pending";
    public const string ForwardRunning = "Running";
    public const string ForwardDelivered = "Delivered";
    public const string ForwardPartlyDelivered = "PartlyDelivered";
    public const string ForwardNotDelivered = "NotDelivered";
    public const string ForwardNoRoom = "NoRoom";
    public const string ForwardNothingLeft = "NothingLeft";
    public const string ForwardSentDirect = "SentDirect";
    public const string ForwardNotInRun = "NotInRun";
    public const string ForwardJournalLost = "forward_journal_lost";
    /// <summary>Forward refusal: the requester cancelled the delivered stow (CancelRequestedOperation) before the forward began.</summary>
    public const string LocalDepositCancelled = "LocalDepositCancelled";
    /// <summary>Forward refusal: the requester cancelled the forward itself (CancelDeliveredStowForward).</summary>
    public const string ForwardCancelled = "ForwardCancelled";
    /// <summary>Forward outcome: its courier run was cleared (settled as-is) in Courier before it completed.</summary>
    public const string RunAbandoned = "RunAbandoned";

    private const string ForwardStatePending = "pending";
    private const string ForwardStateRunning = "running";
    private const string ForwardStateSettled = "settled";

    /// <summary>Forward codes that leave goods where they are without anything having gone wrong.</summary>
    private static readonly HashSet<string> NeutralForwardCodes =
        [ForwardPending, ForwardRunning, ForwardNoRoom, ForwardNothingLeft, ForwardSentDirect];

    /// <summary>How long a forward waits for another automation job to let go once the owner's own stow has settled.</summary>
    private static readonly TimeSpan AutomationWait = TimeSpan.FromSeconds(60);
    /// <summary>
    /// How long a forward waits for another courier run to end (or be resumed or cleared), including one that has the
    /// owner logged out, before it refuses with that run's cause.
    /// </summary>
    private static readonly TimeSpan CourierWait = TimeSpan.FromMinutes(30);
    /// <summary>Consecutive failed reads of a started run's progress before its forward settles as unverified.</summary>
    private const int ProgressFailureLimit = 5;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    /// <summary>Kept this long after settling; forwards without a local operation are kept for good (their only record).</summary>
    private static readonly TimeSpan SettledRetention = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly string providerInstanceId;
    private readonly StateRepository repository;
    private readonly IFrameworkWorkQueue workQueue;
    private readonly OperationJournal journal;
    private readonly Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache;
    private readonly Func<OwnerScope> currentOwner;
    private readonly Func<OwnerScope, IReadOnlyList<InventoryBag>> carriedBags;
    private readonly Func<uint, int> maxStackSize;
    private readonly Action<string> warn;
    private readonly Func<DateTime> utcNow;
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeliveredStowStatus> transient = new(StringComparer.Ordinal);
    private readonly IDeliveredStowCourier? courier;
    private readonly AtomicDocumentStore<DeliveredStowForwardDocument>? forwardStore;
    private readonly List<DeliveredStowForwardRecord> forwards = [];
    private readonly Dictionary<string, DateTime> automationWaitSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> courierWaitSince = new(StringComparer.Ordinal);
    // Forwards whose cancel re-stop was already warned about (once per forward; not persisted).
    private readonly HashSet<string> cancelRestopWarned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> progressFailures = new(StringComparer.Ordinal);
    /// <summary>Request ids (and hashes when known) of an unreadable forward journal that was moved aside.</summary>
    private readonly List<DeliveredStowLostRequest> lost = [];
    /// <summary>Set when an unreadable journal could not be moved aside: saving would overwrite it, so nothing is saved.</summary>
    private string? forwardStoreBlocked;
    /// <summary>The forward journal has changes not yet saved (a save failed); every tick tries again.</summary>
    private bool forwardsDirty;
    private int saveFailures;
    private DateTime nextTickAt;

    public event Action<string>? OperationChanged;

    internal DeliveredStowService(
        string providerInstanceId,
        StateRepository repository,
        IFrameworkWorkQueue workQueue,
        OperationJournal journal,
        Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache,
        Func<OwnerScope> currentOwner,
        Func<OwnerScope, IReadOnlyList<InventoryBag>> carriedBags,
        Func<uint, int> maxStackSize,
        Action<string> warn,
        Func<DateTime>? utcNow = null,
        IDeliveredStowCourier? courier = null,
        string? forwardStorePath = null)
    {
        this.providerInstanceId = providerInstanceId;
        this.repository = repository;
        this.workQueue = workQueue;
        this.journal = journal;
        this.cache = cache;
        this.currentOwner = currentOwner;
        this.carriedBags = carriedBags;
        this.maxStackSize = maxStackSize;
        this.warn = warn;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.courier = courier;
        if (forwardStorePath is not null)
        {
            forwardStore = new(forwardStorePath);
            LoadForwards();
        }
    }

    public string Submit(string requestJson)
    {
        DeliveredStowRequest? request;
        try { request = JsonSerializer.Deserialize<DeliveredStowRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Serialize(Reject(string.Empty, "invalid_json", exception.Message)); }
        if (request is null)
            return Serialize(Reject(string.Empty, "invalid_json", "Request body is empty."));

        var owner = currentOwner();
        if (Validate(request, owner) is { } error)
        {
            warn($"[DeliveredStow] Rejected {request.RequestId}: {error.Code} ({error.Message}).");
            return Serialize(Reject(request.RequestId, error.Code, error.Message));
        }
        var hash = CanonicalHash(request);
        lock (gate)
        {
            var persisted = repository.Read(state => state.Requests.FirstOrDefault(record => record.RequestId == request.RequestId));
            if (persisted is not null)
            {
                if (persisted.CanonicalHash != hash)
                    return Serialize(Reject(request.RequestId, "request_conflict", "Request ID was already used with a different canonical payload."));
                return Serialize(StatusOf(request.RequestId) with { Schema = AckSchema, Message = "Idempotent replay returned the existing stow operation." });
            }
            if (forwards.FirstOrDefault(record => record.RequestId == request.RequestId) is { } forwarded)
            {
                if (forwarded.CanonicalHash != hash)
                    return Serialize(Reject(request.RequestId, "request_conflict", "Request ID was already used with a different canonical payload."));
                return Serialize(StatusOf(request.RequestId) with { Schema = AckSchema, Message = "Idempotent replay returned the existing stow request." });
            }
            if (lost.FirstOrDefault(entry => entry.RequestId == request.RequestId) is { } gone)
            {
                // Accepted before, but its forward journal became unreadable: refused rather than planned and sent again.
                if (gone.CanonicalHash is { Length: > 0 } knownHash && knownHash != hash)
                    return Serialize(Reject(request.RequestId, "request_conflict", "Request ID was already used with a different canonical payload."));
                return Serialize(Reject(request.RequestId, ForwardJournalLost, LostMessage(gone)));
            }
            if (pending.TryGetValue(request.RequestId, out var queued))
            {
                return queued.CanonicalHash == hash
                    ? Serialize(queued.Ack with { Message = "Idempotent replay returned the queued stow operation." })
                    : Serialize(Reject(request.RequestId, "request_conflict", "Request ID is queued with a different canonical payload."));
            }
            if (transient.TryGetValue(request.RequestId, out var failed))
                return Serialize(failed with { Schema = AckSchema });

            var (batch, lines) = Plan(request, owner);
            var forwardWanted = lines.Sum(line => ForwardsOf(line).Where(forward => forward.Code == ForwardPending).Sum(forward => forward.Wanted));
            if (batch.PlannedQuantity <= 0 && forwardWanted <= 0)
            {
                // Nothing routable: no operation, nothing persisted. Callers record this outcome themselves.
                return Serialize(new DeliveredStowStatus
                {
                    Schema = AckSchema,
                    ProviderInstanceId = providerInstanceId,
                    RequestId = request.RequestId,
                    Status = NothingToStow,
                    Message = lines.Any(line => line.Code == LineNoRoom || ForwardsOf(line).Any(forward => forward.Code == ForwardNoRoom))
                        ? "No delivered item has room in its home retainers."
                        : "No delivered item has a home retainer.",
                    Lines = lines,
                });
            }

            var operationId = batch.PlannedQuantity > 0 ? $"rq-dstow-{Guid.NewGuid():N}" : null;
            var ack = new DeliveredStowStatus
            {
                Schema = AckSchema,
                ProviderInstanceId = providerInstanceId,
                RequestId = request.RequestId,
                OperationId = operationId,
                Status = OperationStatuses.Queued,
                Message = forwardWanted <= 0 ? "Stow queued for persistence and automatic execution."
                    : operationId is null ? $"Courier forward of {forwardWanted:N0} unit(s) to other characters queued."
                    : $"Stow queued for persistence and automatic execution; {forwardWanted:N0} unit(s) then go to other characters by courier.",
                Lines = lines,
                Forward = forwardWanted > 0
                    ? new() { State = ForwardStatePending, Message = "Waiting for the owner's own stow to settle." }
                    : lines.Any(line => line.Forward is not null)
                        ? new() { State = ForwardStateSettled, Message = NothingToForward }
                        : null,
            };
            pending.Add(request.RequestId, new(hash, operationId, ack));
            workQueue.Enqueue(() => Apply(request, operationId, hash));
            return Serialize(ack);
        }
    }

    public string Get(string requestId)
    {
        lock (gate)
            return Serialize(StatusOf(requestId ?? string.Empty));
    }

    private DeliveredStowStatus StatusOf(string requestId)
    {
        if (pending.TryGetValue(requestId, out var queued))
            return queued.Ack with { Schema = StatusSchema };
        if (transient.TryGetValue(requestId, out var failed))
            return failed;
        var persisted = repository.Read(state => state.Requests.FirstOrDefault(record => record.RequestId == requestId));
        var operation = persisted is null
            ? null
            : repository.Read(state => state.Operations.FirstOrDefault(record => record.OperationId == persisted.OperationId));
        if (forwards.FirstOrDefault(record => record.RequestId == requestId) is { } forward)
            return ForwardStatus(forward, operation);
        if (operation is null && lost.FirstOrDefault(entry => entry.RequestId == requestId) is { } gone)
        {
            return new()
            {
                Schema = StatusSchema,
                ProviderInstanceId = providerInstanceId,
                RequestId = requestId,
                Status = OperationStatuses.Indeterminate,
                ErrorCode = ForwardJournalLost,
                Message = LostMessage(gone),
            };
        }
        if (operation is null)
        {
            return new()
            {
                Schema = StatusSchema,
                ProviderInstanceId = providerInstanceId,
                RequestId = requestId,
                Status = NotFound,
                Message = "No delivered-stow request with this ID.",
            };
        }
        var terminal = OperationStatuses.IsTerminal(operation.Status);
        var lines = operation.Lines
            .GroupBy(line => (line.ItemId, line.IsHighQuality))
            .Select(group =>
            {
                var planned = group.Sum(line => line.TargetQuantity);
                var stowed = group.Sum(line => line.TransferredQuantity);
                return new DeliveredStowLine
                {
                    ItemId = group.Key.ItemId,
                    ItemName = group.First().ItemName,
                    IsHq = group.Key.IsHighQuality,
                    Requested = planned,
                    Planned = planned,
                    Stowed = stowed,
                    Code = !terminal ? LinePlanned
                        : stowed >= planned ? LineStowed
                        : stowed > 0 ? LinePartlyStowed
                        : LineNotStowed,
                    Destinations = Destinations(operation, group),
                };
            })
            .ToArray();
        return new()
        {
            Schema = StatusSchema,
            ProviderInstanceId = providerInstanceId,
            RequestId = requestId,
            OperationId = operation.OperationId,
            Status = operation.Status,
            Message = operation.Message,
            Lines = lines,
        };
    }

    private static string[] Destinations(OperationRecord operation, IEnumerable<OperationLine> lines) =>
        operation.DepositCandidates
            .Where(candidate => lines.Any(line => candidate.CapacityByVariant.ContainsKey(line.AuthorizationKey)))
            .Select(candidate => candidate.RetainerName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void Apply(DeliveredStowRequest request, string? operationId, string hash)
    {
        DeliveredStowForwardRecord? record = null;
        try
        {
            var owner = currentOwner();
            if (Validate(request, owner) is { } error)
                throw new InvalidOperationException($"{error.Code}: {error.Message}");
            var (batch, lines) = Plan(request, owner);
            if (operationId is not null && batch.PlannedQuantity <= 0)
                throw new InvalidOperationException("Delivered items no longer have routable home-retainer room or are no longer carried.");
            // Accepted as forward-only, but the owner's own homes have room now: those units are stowed here (the
            // forward takes only what they leave), so none is left unplanned in the bags.
            if (operationId is null && batch.PlannedQuantity > 0)
                operationId = $"rq-dstow-{Guid.NewGuid():N}";
            if (operationId is null && !lines.Any(line => ForwardsOf(line).Any(forward => forward.Code == ForwardPending)))
                throw new InvalidOperationException("Delivered items no longer have room in their homes on other characters or are no longer carried.");
            if (lines.Any(line => line.Forward is not null))
            {
                // Journaled before the local operation exists, so a forward is never lost behind a stow that ran.
                record = Record(request, hash, operationId, owner, lines);
                lock (gate)
                {
                    forwards.Add(record);
                    SaveForwards();
                }
            }
            if (operationId is not null)
                journal.CreateRequestedDeposit(owner, batch, request.RequestId, operationId, hash);
            lock (gate)
                pending.Remove(request.RequestId);
        }
        catch (Exception exception)
        {
            warn($"[DeliveredStow] Could not persist {request.RequestId} ({operationId ?? "forward only"}): {exception.Message}");
            lock (gate)
            {
                if (record is not null && forwards.Remove(record))
                {
                    try { SaveForwards(); }
                    catch (Exception cleanup) { warn($"[DeliveredStow] Could not withdraw the forward of {request.RequestId}: {cleanup.Message}"); }
                }
                var ack = pending.Remove(request.RequestId, out var queued) ? queued.Ack : null;
                transient[request.RequestId] = new()
                {
                    Schema = StatusSchema,
                    ProviderInstanceId = providerInstanceId,
                    RequestId = request.RequestId,
                    OperationId = operationId,
                    Status = OperationStatuses.Failed,
                    ErrorCode = "persist_failed",
                    Message = $"Quartermaster could not persist the accepted stow request: {exception.Message}",
                    Lines = ack?.Lines ?? [],
                };
            }
        }
        if (operationId is not null)
            OperationChanged?.Invoke(operationId);
    }

    /// <summary>The owner's item homes from the Placements catalog (copied out of the state lock).</summary>
    private IReadOnlyList<ItemPlacementRule> OwnerPlacements(OwnerScope owner) =>
        repository.Read(state => PlacementCatalog.OwnerPlacements(state, owner).Select(PlacementCatalog.Copy).ToArray());

    /// <summary>
    /// Plans a request through <see cref="PlacementRouter"/>: each item's ranked homes across characters, in rank order,
    /// each taking what its observed room and cap allow, against one projection for the whole request. Units the
    /// caller already sent straight to homes on other characters (SentDirect) are placed there first, so they count
    /// against those homes' room and caps. The owner's share is a per-retainer batch with exact targets (a capped
    /// home is never filled past its cap); the rest are the item's forwards, one per other character. Homes on other
    /// characters count only when the caller opted into forwarding or reports units it sent there itself.
    /// </summary>
    private (StowageDepositBatch Batch, DeliveredStowLine[] Lines) Plan(DeliveredStowRequest request, OwnerScope owner)
    {
        var carried = carriedBags(owner)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var rules = OwnerPlacements(owner);
        var retainers = cache();
        var requested = request.Items!
            .Select(item =>
            {
                var reserve = PlacementCatalog.ForArrival(rules, item.ItemId, item.IsHq);
                var all = reserve?.Homes ?? [];
                var remote = all.Where(home => !ReservePlacement.SameCharacter(home.Character, owner) && home.Character.HasStableIdentity).ToArray();
                var considered = !request.AllowForward && item.SentDirect <= 0 ? [] : remote;
                return (Key: (item.ItemId, item.IsHq), item.ItemName, item.Quantity, item.SentDirect, Reserve: reserve,
                    Held: Math.Min(item.Quantity, carried.GetValueOrDefault((item.ItemId, item.IsHq))),
                    // Without forwarding only the owner's homes take carried units.
                    Routed: (IReadOnlyList<PlacementHome>)(request.AllowForward ? all.Where(home => home.Character.HasStableIdentity).ToArray()
                        : all.Where(home => ReservePlacement.SameCharacter(home.Character, owner)).ToArray()),
                    Remote: considered);
            })
            .ToArray();

        var projected = new Dictionary<ulong, CachedRetainer>();
        var direct = requested.Select(item => item.SentDirect > 0 && item.Remote.Length > 0
                ? PlacementRouter.RouteOne(new(item.Key.ItemId, item.ItemName, item.Key.IsHq, item.SentDirect), item.Remote, retainers, projected, maxStackSize)
                : null)
            .ToArray();
        var routes = requested.Select(item =>
                PlacementRouter.RouteOne(new(item.Key.ItemId, item.ItemName, item.Key.IsHq, item.Held), item.Routed, retainers, projected, maxStackSize))
            .ToArray();
        var batch = PlacementRouter.LocalBatch(
            requested.Select((item, index) => (routes[index], (Guid?)null, item.Reserve?.Id,
                item.Reserve is null ? new StowageRoutingPolicy() : PlacementRouter.OwnerRouting(item.Reserve, owner).Routing)),
            retainers, owner, maxStackSize, utcNow());

        var lines = requested.Select((item, index) =>
        {
            var route = routes[index];
            var local = route.Allocations.Where(allocation => ReservePlacement.SameCharacter(allocation.Character, owner)).ToArray();
            var planned = local.Sum(allocation => allocation.Units);
            var sentOn = route.Allocations.Where(allocation => !ReservePlacement.SameCharacter(allocation.Character, owner)).ToArray();
            var leftover = Math.Max(0, item.Held - route.Placed);
            var directUnplaced = direct[index] is { } sent ? sent.Unplaced : item.SentDirect;
            var forwards = item.Remote
                .Select(home => home.Character)
                .DistinctBy(character => (character.LocalContentId ?? 0, character.CharacterName))
                .Select((character, rank) =>
                {
                    var routed = sentOn.Where(allocation => ReservePlacement.SameCharacter(allocation.Character, character)).Sum(allocation => allocation.Units);
                    // Units sent directly count where the router placed them; any it could not place are the first character's.
                    var already = (direct[index]?.Allocations ?? [])
                        .Where(allocation => ReservePlacement.SameCharacter(allocation.Character, character)).Sum(allocation => allocation.Units) +
                        (rank == 0 ? directUnplaced : 0);
                    var homes = Homes(character, item.Remote.Where(home => ReservePlacement.SameCharacter(home.Character, character)).Select(home => home.RetainerId), retainers);
                    var forward = new DeliveredStowForwardLine { Character = Character(character), Homes = homes };
                    var also = already > 0 ? $"; {already:N0} already went there directly" : string.Empty;
                    if (!request.AllowForward || routed <= 0 && leftover <= 0)
                        return already > 0
                            ? forward with { Code = ForwardSentDirect, Message = $"{already:N0} went to {character.CharacterName} directly." }
                            : forward with { Code = ForwardNothingLeft, Message = item.Held > 0 ? "Homes ranked ahead of it take all of it." : "Not carried." };
                    if (routed <= 0)
                        return forward with
                        {
                            Code = ForwardNoRoom,
                            Message = route.UnplacedReason == PlacementRouter.AtCap
                                ? $"{character.CharacterName}'s homes ({HomeList(homes)}) are at their cap; {leftover:N0} stay in the bags{also}."
                                : $"No observed room on {character.CharacterName} ({HomeList(homes)}) for {leftover:N0}; it stays in the bags{also}.",
                        };
                    return forward with
                    {
                        Code = ForwardPending,
                        Wanted = routed,
                        Message = $"{routed:N0} go to {character.CharacterName} after the owner's own stow{also}.",
                    };
                })
                .ToArray();
            var pending = forwards.Any(forward => forward.Code == ForwardPending);
            var hasLocalHome = item.Reserve?.Homes.Any(home => ReservePlacement.SameCharacter(home.Character, owner)) == true;
            var code = item.Quantity == 0 && item.SentDirect > 0 ? LineSentDirect
                : item.Held <= 0 ? LineNotCarried
                : !hasLocalHome ? forwards.Length == 0 || !request.AllowForward ? LineNoHome : LineHomedElsewhere
                : planned <= 0 ? pending ? LineHomedElsewhere : LineNoRoom
                : LinePlanned;
            return new DeliveredStowLine
            {
                ItemId = item.Key.ItemId,
                ItemName = item.ItemName,
                IsHq = item.Key.IsHq,
                Requested = item.Quantity,
                Carried = item.Held,
                Planned = planned,
                Code = code,
                Destinations = local.Select(allocation => allocation.RetainerName).Distinct(StringComparer.Ordinal).ToArray(),
                Forward = forwards.FirstOrDefault(),
                Forwards = forwards.Length == 0 ? null : forwards,
            };
        }).ToArray();
        return (batch, lines);
    }

    private static IEnumerable<DeliveredStowForwardLine> ForwardsOf(DeliveredStowLine line) =>
        line.Forwards ?? (line.Forward is { } forward ? [forward] : []);

    // ---- Requester cancel ---------------------------------------------------------------------

    /// <summary>
    /// The requester cancelled this delivered stow (CancelRequestedOperation, or Cancel in Transfer history): its forward,
    /// if one has not begun, records that durably and never starts, even when the local stow then settles normally.
    /// A forward whose run already started is left to <see cref="CancelForward"/>. Unknown ids are ignored.
    /// Returns true when this call withdrew a forward (it will not start), so the cancel's ack can say so.
    /// </summary>
    public bool RecordRequesterCancel(string requestId, string reason)
    {
        lock (gate)
        {
            if (forwards.FirstOrDefault(record => record.RequestId == requestId) is not { } record ||
                record.State == ForwardStateSettled || record.Run is not null || record.CancelRequestedUtc is not null)
                return false;
            MarkCancel(record, LocalDepositCancelled, reason);
            warn($"[DeliveredStow] {requestId}: {LocalDepositCancelled} recorded ({reason}); its courier forward will not start.");
            return true;
        }
    }

    /// <summary>
    /// Quartermaster.v1.CancelDeliveredStowForward. Status: cancelled (the forward never starts), stopping (its courier
    /// run is told to stop at its next verified checkpoint; RunId names it), already_terminal, cancel_queued (accepted
    /// but not persisted yet; cancelled once it is), not_found, or rejected (ErrorCode names why). Schema and
    /// providerInstanceId are optional, and checked when given; the schema may be this channel's or the generic
    /// requested-operation cancel schema.
    /// </summary>
    public string CancelForward(string requestJson)
    {
        DeliveredStowForwardCancelRequest? request;
        try { request = JsonSerializer.Deserialize<DeliveredStowForwardCancelRequest>(requestJson, JsonOptions); }
        catch (JsonException exception) { return Serialize(CancelAck(string.Empty, "rejected", exception.Message, errorCode: "invalid_json")); }
        if (request is null)
            return Serialize(CancelAck(string.Empty, "rejected", "Request body is empty.", errorCode: "invalid_json"));
        // The generic requested-operation cancel schema is accepted too, for requesters that reuse that envelope.
        if (!string.IsNullOrWhiteSpace(request.Schema) && request.Schema != ForwardCancelSchema && request.Schema != RequestedOperationCancelService.RequestSchema)
            return Serialize(CancelAck(request.RequestId, "rejected", $"Expected schema '{ForwardCancelSchema}' (or '{RequestedOperationCancelService.RequestSchema}').", errorCode: "unsupported_schema"));
        if (!string.IsNullOrWhiteSpace(request.ProviderInstanceId) && request.ProviderInstanceId != providerInstanceId)
            return Serialize(CancelAck(request.RequestId, "rejected", "providerInstanceId does not match this Quartermaster provider instance.", errorCode: "provider_mismatch"));
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Serialize(CancelAck(string.Empty, "rejected", "requestId is required.", errorCode: "missing_request_id"));
        var requestId = request.RequestId;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled by its requester." : $"Cancelled by its requester: {request.Reason.Trim()}";
        lock (gate)
        {
            var ack = CancelForwardLocked(requestId, reason);
            warn($"[DeliveredStow] {requestId}: CancelDeliveredStowForward {ack.Status}{(ack.RunId is null ? "" : $" (run {ack.RunId})")}: {ack.Message} {reason}");
            return Serialize(ack);
        }
    }

    private DeliveredStowForwardCancelAcknowledgement CancelForwardLocked(string requestId, string reason)
    {
        if (forwards.FirstOrDefault(candidate => candidate.RequestId == requestId) is not { } record)
        {
            if (pending.ContainsKey(requestId))
            {
                // Persisted on the framework thread; that queue is FIFO, so this runs once the forward is journaled.
                workQueue.Enqueue(() =>
                {
                    lock (gate)
                    {
                        var deferred = CancelForwardLocked(requestId, reason);
                        warn($"[DeliveredStow] {requestId}: queued CancelDeliveredStowForward {deferred.Status} after it persisted: {deferred.Message}");
                    }
                });
                return CancelAck(requestId, "cancel_queued", "The request is not persisted yet; its forward is cancelled once it is.");
            }
            if (repository.Read(state => state.Requests.Any(candidate => candidate.RequestId == requestId)))
                return CancelAck(requestId, "already_terminal", "This delivered stow has no courier forward.");
            return CancelAck(requestId, "not_found", "No delivered-stow request with this ID.");
        }
        var runId = record.Run?.ToString();
        if (record.State == ForwardStateSettled)
            return CancelAck(requestId, "already_terminal", $"The forward already ended ({record.Code ?? "settled"}: {record.Message}).", runId, record.Code);
        // The run already finished; the next tick settles the forward from its outcome, so there is nothing left to stop.
        if (record.Run is { } bound && RunEnded(bound) is { } ended)
            return CancelAck(requestId, "already_terminal", $"The forward's courier run {bound} already ended ({ended}); the forward settles from its outcome.", runId);
        if (record.CancelRequestedUtc is null)
            MarkCancel(record, ForwardCancelled, reason);
        if (record.Run is { } run)
        {
            // Normal Stop: the run halts at its next verified checkpoint and keeps its custody; nothing is replayed.
            var stopped = courier?.Stop(run) == true;
            return CancelAck(requestId, "stopping", stopped
                ? $"Courier run {run} was told to stop at its next verified checkpoint; once it has stopped, clear it with the courier IPC to settle the forward, or resume it to carry on with the delivery."
                : $"Courier run {run} is not running; clear it with the courier IPC to settle the forward, or resume it to carry on with the delivery.", runId);
        }
        if (courier is not null)
        {
            // A run started under this request whose binding was not saved (Quartermaster reloaded before the next tick):
            // the next tick binds it and keeps telling it to stop, so this cancel reports the run instead of "cancelled".
            var (earlier, loading) = courier.Adopt(CourierRequestId(requestId));
            if (earlier is not null)
            {
                var adoptedId = earlier.Run.ToString();
                if (RunEnded(earlier.Run) is { } adoptedEnd)
                    return CancelAck(requestId, "already_terminal", $"The courier run {earlier.Run} started for this forward already ended ({adoptedEnd}); the forward settles from its outcome.", adoptedId);
                var stopped = courier.Stop(earlier.Run);
                return CancelAck(requestId, "stopping", stopped
                    ? $"Courier run {earlier.Run} (started for this forward before Quartermaster reloaded) was told to stop at its next verified checkpoint; once it has stopped, clear it with the courier IPC to settle the forward, or resume it to carry on with the delivery."
                    : $"Courier run {earlier.Run} (started for this forward before Quartermaster reloaded) is not running; clear it with the courier IPC to settle the forward, or resume it to carry on with the delivery.", adoptedId);
            }
            if (loading)
                return CancelAck(requestId, "cancel_queued",
                    "The cancel is recorded and the forward will not start; the courier's request history is still loading, so whether a run already started for it is not known yet. Send the cancel again for its final status.");
        }
        var localOpen = record.OperationId is { } operationId &&
            repository.Read(state => state.Operations.FirstOrDefault(candidate => candidate.OperationId == operationId)) is { } operation &&
            !OperationStatuses.IsTerminal(operation.Status);
        return CancelAck(requestId, "cancelled", localOpen
            ? "The forward will not start; the owner's own stow is still running (cancel it with Quartermaster.v1.CancelRequestedOperation)."
            : "The forward will not start.");
    }

    /// <summary>
    /// How the run ended (state and code) when it is no longer running or resumable: completed, or closed (cleared or
    /// replaced) with its archive read. Null while it may still run or be resumed, or while that is not known yet.
    /// </summary>
    private string? RunEnded(Guid run)
    {
        if (courier is null)
            return null;
        try
        {
            return courier.Progress(run) is { } progress && (progress.State == "Completed" || !progress.Current && progress.State != "Starting")
                ? $"{progress.State}{(progress.Code is { Length: > 0 } code ? $" {code}" : "")}"
                : null;
        }
        catch (Exception exception)
        {
            warn($"[DeliveredStow] courier run {run}'s state could not be read for a cancel ({exception.Message}); reported as stopping.");
            return null;
        }
    }

    /// <summary>Records the requester's cancel durably on a forward that has not settled. Holding the gate.</summary>
    private void MarkCancel(DeliveredStowForwardRecord record, string code, string reason)
    {
        record.CancelRequestedUtc = utcNow();
        record.CancelCode = code;
        record.CancelReason = reason;
        // Saved now so a reload before the next tick still refuses to start; a failed save is retried by Tick.
        try { SaveForwards(); }
        catch (Exception exception)
        {
            forwardsDirty = true;
            warn($"[DeliveredStow] {record.RequestId}: {code} kept in memory, the forward journal save failed ({exception.Message}); retrying every tick.");
        }
    }

    private DeliveredStowForwardCancelAcknowledgement CancelAck(string requestId, string status, string message, string? runId = null, string? errorCode = null) => new()
    {
        ProviderInstanceId = providerInstanceId,
        RequestId = requestId ?? string.Empty,
        Status = status,
        RunId = runId,
        ErrorCode = errorCode,
        Message = message,
    };

    private static string Serialize(DeliveredStowForwardCancelAcknowledgement ack) => JsonSerializer.Serialize(ack, JsonOptions);

    // ---- Courier forward ----------------------------------------------------------------------

    private static string CourierRequestId(string requestId) => $"delivered-stow:{requestId}";

    /// <summary>
    /// Advances every unsettled forward once a second: starts its courier run once the owner's own stow has settled,
    /// then follows the run to its end. Framework thread.
    /// </summary>
    public void Tick()
    {
        var now = utcNow();
        if (now < nextTickAt)
            return;
        nextTickAt = now + TickInterval;
        var changedOperations = new List<string>();
        lock (gate)
        {
            if (!forwardsDirty && !forwards.Any(record => record.State != ForwardStateSettled))
                return;
            foreach (var record in forwards.Where(record => record.State != ForwardStateSettled).ToArray())
            {
                bool changed;
                try
                {
                    changed = Advance(record, now);
                    progressFailures.Remove(record.RequestId);
                }
                catch (Exception exception)
                {
                    changed = Failed(record, exception, now);
                }
                if (!changed)
                    continue;
                forwardsDirty = true;
                if (record.State == ForwardStateSettled && record.OperationId is { } operationId)
                    changedOperations.Add(operationId);
            }
            if (forwardsDirty)
            {
                // A failed save keeps every change (and a started run's binding) in memory and is tried again each tick.
                try
                {
                    SaveForwards();
                    forwardsDirty = false;
                    if (saveFailures > 0)
                        warn($"[DeliveredStow] Forward journal saved after {saveFailures} failed attempt(s).");
                    saveFailures = 0;
                }
                catch (Exception exception)
                {
                    if (saveFailures++ == 0)
                        warn($"[DeliveredStow] ForwardJournalSaveFailed: could not save the forward journal ({exception.Message}); retrying every {TickInterval.TotalSeconds:N0}s with every change kept in memory.");
                }
            }
        }
        foreach (var operationId in changedOperations)
            OperationChanged?.Invoke(operationId);
    }

    /// <summary>
    /// A forward step threw. One that never started a run cannot start safely now and is refused; a started run's
    /// progress is read again next tick, until <see cref="ProgressFailureLimit"/> reads in a row fail. True when the
    /// record changed.
    /// </summary>
    private bool Failed(DeliveredStowForwardRecord record, Exception exception, DateTime now)
    {
        if (record.Run is not { } run)
        {
            warn($"[DeliveredStow] Forward of {record.RequestId} failed: {exception}");
            return Refuse(record, "ForwardFailed", $"Quartermaster could not forward: {exception.Message}", now);
        }
        var count = progressFailures[record.RequestId] = progressFailures.GetValueOrDefault(record.RequestId) + 1;
        if (count < ProgressFailureLimit)
            return false;
        progressFailures.Remove(record.RequestId);
        var message = $"Quartermaster could not read courier run {run}'s progress {ProgressFailureLimit} times in a row " +
            $"(last: {exception.Message}); where the goods are is unverified, check the run in Courier.";
        foreach (var line in record.Lines.Where(line => line.Code == ForwardRunning))
            SetLine(line, "RunIndeterminate", line.Delivered is > 0 ? $"{line.Delivered:N0} of {line.Planned:N0} deposited as last read. {message}" : message);
        warn($"[DeliveredStow] {record.RequestId}: RunIndeterminate after {ProgressFailureLimit} failed progress reads of run {run}: {exception}");
        Settle(record, "RunIndeterminate", message, now);
        return true;
    }

    /// <summary>One step of a forward; true when its record changed.</summary>
    private bool Advance(DeliveredStowForwardRecord record, DateTime now)
    {
        if (record.Run is { } run)
            return Follow(record, run, now);
        var open = record.Lines.Where(line => line.Code == ForwardPending).ToArray();
        if (open.Length == 0)
        {
            Settle(record, null, "Nothing was left to forward.", now);
            return true;
        }
        if (record.OperationId is { } operationId)
        {
            var operation = repository.Read(state => state.Operations.FirstOrDefault(candidate => candidate.OperationId == operationId));
            if (operation is null)
                return Refuse(record, "LocalDepositMissing", "The owner's own stow operation is missing; nothing was forwarded.", now);
            if (!OperationStatuses.IsTerminal(operation.Status))
                return false;
            if (operation.Status == OperationStatuses.Indeterminate)
                return Refuse(record, "LocalDepositUnverified",
                    "The owner's own stow ended unverified, so where the goods are is unknown; nothing was forwarded.", now);
            if (operation.Status == OperationStatuses.Cancelled)
                return Refuse(record, LocalDepositCancelled, "The owner's own stow was cancelled; nothing was forwarded.", now);
        }
        if (record.CancelRequestedUtc is not null)
        {
            // The requester cancelled: never started now, whatever the local stow did after the cancel. A run already
            // started under this request (its binding unsaved) is followed and told to stop, never left unowned.
            if (courier is not null)
            {
                var (earlier, loading) = courier.Adopt(CourierRequestId(record.RequestId));
                if (loading)
                    return false;
                if (earlier is not null)
                {
                    Bind(record, earlier, open);
                    var stopped = courier.Stop(earlier.Run);
                    warn($"[DeliveredStow] {record.RequestId}: {record.CancelCode} found courier run {earlier.Run} already started; " +
                         (stopped ? "told it to stop at its next verified checkpoint." : "it is not running; clear it in Courier to settle the forward, or resume it to carry on."));
                    return true;
                }
            }
            var code = record.CancelCode ?? LocalDepositCancelled;
            var by = record.CancelReason is { Length: > 0 } why ? $" ({why.TrimEnd('.')})" : "";
            return Refuse(record, code, code == ForwardCancelled
                ? $"The requester cancelled the courier forward{by}; nothing was forwarded."
                : $"The requester cancelled this delivered stow{by}; nothing was forwarded.", now);
        }
        if (courier is null)
            return Refuse(record, "CourierNotLoaded", "The courier is not loaded.", now);

        // A run already started for this request (its binding was never saved, or Quartermaster reloaded) is
        // followed, never started again.
        var (adopted, historyLoading) = courier.Adopt(CourierRequestId(record.RequestId));
        if (historyLoading)
            return false;
        if (adopted is not null)
            return Bind(record, adopted, open);

        var owner = currentOwner();
        var blocked = courier.Blocked();
        if (!owner.Matches(record.Owner))
        {
            // The courier logs characters in and out: a run under way waits; otherwise the owner left for good.
            if (blocked is { Wait: true })
                return WaitForCourier(record, "OwnerChanged",
                    $"{record.Owner.CharacterName} is not logged in while a courier run is under way", now);
            return Refuse(record, "OwnerChanged", $"{record.Owner.CharacterName} is no longer the logged-in character; the goods stay in their bags.", now);
        }
        if (blocked is { Wait: true })
            return WaitForCourier(record, blocked.Code, blocked.Message, now);
        if (blocked is not null)
            return Refuse(record, blocked.Code, blocked.Message, now);
        courierWaitSince.Remove(record.RequestId);
        if (courier.AutomationBlock is { } reason)
        {
            if (!automationWaitSince.TryGetValue(record.RequestId, out var since))
                automationWaitSince[record.RequestId] = since = now;
            if (now - since < AutomationWait)
                return false;
            return Refuse(record, "AutomationBusy",
                $"Automation stayed busy for {AutomationWait.TotalSeconds:N0}s after the owner's own stow ({reason}); nothing was forwarded.", now);
        }
        automationWaitSince.Remove(record.RequestId);

        // Only what is in the bags now is sent on, never more than this request left for the other characters, and
        // re-planned through the router against those characters' homes as observed now (their caps and the units the
        // caller sent there directly included).
        var carried = carriedBags(owner)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var retainers = cache();
        var rules = OwnerPlacements(record.Owner);
        var projected = new Dictionary<ulong, CachedRetainer>();
        var items = open.GroupBy(line => (line.ItemId, line.IsHq)).Select(group => group.ToArray()).ToArray();
        var allocations = new Dictionary<DeliveredStowForwardRecordLine, List<(ulong RetainerId, int Units)>>();
        var noRoom = new Dictionary<DeliveredStowForwardRecordLine, string?>();
        // The units sent directly take their room (and cap) first, for every item.
        var plans = items.Select(lines =>
        {
            var first = lines[0];
            var rule = PlacementCatalog.ForArrival(rules, first.ItemId, first.IsHq);
            var remote = (rule?.Homes ?? []).Where(home => !ReservePlacement.SameCharacter(home.Character, record.Owner)).ToArray();
            var sentDirect = record.Lines.Where(line => line.ItemId == first.ItemId && line.IsHq == first.IsHq).Select(line => line.SentDirect).DefaultIfEmpty(0).Max();
            if (sentDirect > 0 && remote.Length > 0)
                PlacementRouter.RouteOne(new(first.ItemId, first.ItemName, first.IsHq, sentDirect), remote, retainers, projected, maxStackSize);
            // The characters this request still forwards to, by the rule's current ranked homes there; the journaled
            // retainers (uncapped) when the rule no longer names any.
            var homes = remote.Where(home => lines.Any(line => ReservePlacement.SameCharacter(line.Character!, home.Character))).ToList();
            foreach (var line in lines.Where(line => !homes.Any(home => ReservePlacement.SameCharacter(home.Character, line.Character!))))
                homes.AddRange(line.RetainerIds.Distinct().Where(id => id != 0).Select(id => new PlacementHome(line.Character!, id)));
            var units = Math.Min(lines.Sum(line => line.Wanted), carried.GetValueOrDefault((first.ItemId, first.IsHq)));
            return (Lines: lines, Homes: (IReadOnlyList<PlacementHome>)homes, Units: units);
        }).ToArray();
        foreach (var (lines, homes, units) in plans)
        {
            var first = lines[0];
            if (units <= 0)
            {
                foreach (var line in lines)
                    SetLine(line, ForwardNothingLeft, "None of it is left in the owner's bags to forward.");
                continue;
            }
            var route = PlacementRouter.RouteOne(new(first.ItemId, first.ItemName, first.IsHq, units), homes, retainers, projected, maxStackSize);
            foreach (var line in lines)
            {
                var mine = route.Allocations.Where(allocation => ReservePlacement.SameCharacter(allocation.Character, line.Character!))
                    .Select(allocation => (allocation.RetainerId, allocation.Units)).ToList();
                if (mine.Count > 0)
                    allocations[line] = mine;
                else
                    noRoom[line] = route.UnplacedReason;
            }
        }
        foreach (var (line, unplaced) in noRoom)
        {
            var homes = HomeList(Homes(line.Character!, line.RetainerIds, retainers));
            SetLine(line, ForwardNoRoom, unplaced == PlacementRouter.AtCap
                ? $"{line.Character!.CharacterName}'s homes ({homes}) are at their cap; it stays in the bags."
                : unplaced is null
                    ? $"Homes ranked ahead of {line.Character!.CharacterName} take all of it."
                    : $"No observed room on {line.Character!.CharacterName} ({homes}); it stays in the bags.");
        }
        var groups = allocations
            .GroupBy(entry => (entry.Key.Character!.LocalContentId ?? 0, entry.Key.Character.CharacterName))
            .Select(group => (Destination: group.First().Key.Character!,
                Units: (IReadOnlyList<DeliveredStowForwardUnit>)group
                    .Select(entry => new DeliveredStowForwardUnit(entry.Key.Line, entry.Key.ItemId, entry.Key.ItemName, entry.Key.IsHq, entry.Value))
                    .ToArray()))
            .ToList();
        var bound = allocations.Select(entry => (Line: entry.Key, Units: entry.Value.Sum(home => home.Units))).ToList();
        if (groups.Count == 0)
        {
            Settle(record, null, "Nothing was left to forward.", now);
            return true;
        }

        var (started, refusal, refused) = courier.Start(CourierRequestId(record.RequestId), record.CanonicalHash, owner, groups);
        if (refusal is { Wait: true })
        {
            // The bound lines are still pending and are planned again once the courier is free.
            WaitForCourier(record, refusal.Code, refusal.Message, now);
            return true;
        }
        foreach (var (line, units) in bound)
        {
            var key = line.Character!.LocalContentId ?? 0;
            if (refusal is not null || refused.ContainsKey(key) || started is null)
            {
                var why = refusal ?? refused.GetValueOrDefault(key) ?? new("RunNotStarted", "The courier started no run.");
                SetLine(line, why.Code, why.Message);
                warn($"[DeliveredStow] {record.RequestId}: {line.ItemName} not forwarded to {line.Character.CharacterName}: {why.Code} ({why.Message}).");
                continue;
            }
            line.Planned = units;
            SetLine(line, ForwardRunning, $"The courier is carrying {units:N0} to {line.Character.CharacterName}.");
        }
        if (started is not { } runId)
        {
            Settle(record, refusal?.Code, refusal?.Message ?? "Nothing was forwarded.", now);
            return true;
        }
        record.Run = runId;
        record.State = ForwardStateRunning;
        record.Code = null;
        record.Message = $"Courier run carrying {bound.Where(entry => entry.Line.Code == ForwardRunning).Sum(entry => entry.Units):N0} unit(s) to other characters.";
        return true;
    }

    /// <summary>Follows a run started earlier under this request's courier request id instead of starting one. Always true.</summary>
    private bool Bind(DeliveredStowForwardRecord record, DeliveredStowAdoptedRun adopted, IEnumerable<DeliveredStowForwardRecordLine> open)
    {
        foreach (var line in open)
        {
            if (adopted.Planned is null)
            {
                // The run's plan could not be read: what it carries for this line is taken as at most what was wanted.
                line.Planned = line.Wanted;
                SetLine(line, ForwardRunning, $"Following courier run {adopted.Run} started earlier for this request; its plan could not be read.");
            }
            else if (adopted.Planned.TryGetValue(line.Line, out var units) && units > 0)
            {
                line.Planned = units;
                SetLine(line, ForwardRunning, $"The courier is carrying {units:N0} to {line.Character!.CharacterName}.");
            }
            else
            {
                SetLine(line, ForwardNotInRun, $"The courier run started earlier for this request does not carry it; it stays in the bags.");
            }
        }
        warn($"[DeliveredStow] {record.RequestId}: following courier run {adopted.Run} started earlier for this request instead of starting another.");
        record.Run = adopted.Run;
        record.State = ForwardStateRunning;
        record.Code = null;
        record.Message = $"Following courier run {adopted.Run} started earlier for this request.";
        courierWaitSince.Remove(record.RequestId);
        automationWaitSince.Remove(record.RequestId);
        return true;
    }

    /// <summary>
    /// Waits for another courier run, at most <see cref="CourierWait"/> from the first wait, then refuses with its
    /// cause and the time waited. True when the record changed.
    /// </summary>
    private bool WaitForCourier(DeliveredStowForwardRecord record, string code, string message, DateTime now)
    {
        if (!courierWaitSince.TryGetValue(record.RequestId, out var since))
            courierWaitSince[record.RequestId] = since = now;
        if (now - since >= CourierWait)
            return Refuse(record, code,
                $"{message.TrimEnd('.')}. Waited {CourierWait.TotalMinutes:N0} min for it after the owner's own stow; nothing was forwarded.", now);
        var waiting = $"Waiting for the courier: {message.TrimEnd('.')}.";
        if (record.Code == code && record.Message == waiting)
            return false;
        record.Code = code;
        record.Message = waiting;
        return true;
    }

    /// <summary>Follows a started run; true when the record changed.</summary>
    private bool Follow(DeliveredStowForwardRecord record, Guid run, DateTime now)
    {
        if (courier is null)
            return false;
        if (courier.Progress(run) is not { } progress)
            return false;
        var running = record.Lines.Where(line => line.Code == ForwardRunning).ToArray();
        var changed = false;
        foreach (var line in running)
        {
            if (progress.Delivered.TryGetValue(line.Line, out var delivered) && line.Delivered != delivered)
            {
                line.Delivered = delivered;
                changed = true;
            }
        }
        if (record.CancelRequestedUtc is not null && record.CancelStopObservedUtc is null)
        {
            if (progress.State is "Starting" or "Running")
            {
                // The requester cancelled after the run was bound, possibly while it was still launching (a launching run
                // can miss the cancel's own Stop): keep telling it to stop until it is first seen not running. Stop is
                // idempotent and halts at the next verified checkpoint, never replaying custody.
                if (courier.Stop(run) && cancelRestopWarned.Add(record.RequestId))
                    warn($"[DeliveredStow] CancelRestop: {record.RequestId}: courier run {run} is {progress.State} after the requester's " +
                         $"{record.CancelCode ?? ForwardCancelled}; the cancel is stopping it at its next verified checkpoint (warned once per forward).");
            }
            else
            {
                // Seen stopped (Idle, interrupted, failed or ended): the cancel's stop is done. Saved, so a reload does not
                // stop the run again; a later resume is the operator's explicit choice and carries on.
                record.CancelStopObservedUtc = now;
                cancelRestopWarned.Remove(record.RequestId);
                changed = true;
            }
        }
        switch (progress.State)
        {
            case "Starting":
            case "Running":
            case "Idle":
                if (record.Code is not null)
                {
                    // Resumed after a stop: the run is carrying again.
                    record.Code = null;
                    record.Message = $"Courier run carrying {running.Sum(line => line.Planned):N0} unit(s) to other characters.";
                    foreach (var line in running)
                        SetLine(line, ForwardRunning, $"The courier is carrying {line.Planned:N0} to {line.Character!.CharacterName}.");
                    changed = true;
                }
                return changed;
            case "Completed":
                foreach (var line in running)
                {
                    // The run's own tally (its current journal or its archive) says what was deposited.
                    var delivered = line.Delivered ??= 0;
                    SetLine(line,
                        delivered >= line.Planned ? ForwardDelivered : delivered > 0 ? ForwardPartlyDelivered : ForwardNotDelivered,
                        $"{delivered:N0} of {line.Planned:N0} deposited on {line.Character!.CharacterName}.");
                }
                Settle(record, progress.Code is { Length: > 0 } code && code != "Succeeded" ? code : null, "Courier run completed.", now);
                courier.Forget(run, CourierRequestId(record.RequestId));
                return true;
            case "Failed" or "Interrupted" or "Indeterminate" when progress.Current:
            {
                // Still the courier's current run: it can be resumed and may hold goods, so the forward stays open
                // until it completes or is cleared (its archive then says what was deposited).
                var (stall, why) = progress.State switch
                {
                    "Failed" => ("RunFailed", $"The courier run failed ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    "Interrupted" => ("RunInterrupted", $"The courier run was interrupted ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    _ => ("RunIndeterminate", $"The courier run stopped unverified ({progress.Code}: {progress.Detail}); check its custody in Courier, then resume or clear it."),
                };
                if (record.Code == stall && record.Message == why)
                    return changed;
                record.Code = stall;
                record.Message = why;
                foreach (var line in running)
                    SetLine(line, ForwardRunning, line.Delivered is > 0 ? $"{line.Delivered:N0} of {line.Planned:N0} deposited so far. {why}" : why);
                warn($"[DeliveredStow] {record.RequestId}: courier run {run} stopped {progress.State} ({progress.Code}: {progress.Detail}); the forward waits for it to be resumed or cleared.");
                return true;
            }
            default:
                var (outcome, message) = progress.State switch
                {
                    "Refused" => ("RunRefused", $"The courier refused the run ({progress.Code}: {progress.Detail})."),
                    "Failed" => ("RunFailed", $"The courier run failed ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    "Interrupted" => ("RunInterrupted", $"The courier run was interrupted ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    "Indeterminate" => ("RunIndeterminate", $"The courier run ended unverified ({progress.Code}: {progress.Detail}); check its custody in Courier."),
                    "Abandoned" => (RunAbandoned, $"The courier run was cleared in Courier and settled as-is ({progress.Code}: {progress.Detail}); what it had not deposited stays where the run left it."),
                    _ => ("RunNotStarted", $"The courier run never began ({progress.Code}: {progress.Detail})."),
                };
                foreach (var line in running)
                {
                    var delivered = line.Delivered ?? 0;
                    if (delivered >= line.Planned)
                        SetLine(line, ForwardDelivered, $"{delivered:N0} of {line.Planned:N0} deposited on {line.Character!.CharacterName} before the run stopped.");
                    else
                        SetLine(line, outcome, delivered > 0 ? $"{delivered:N0} of {line.Planned:N0} deposited. {message}" : message);
                }
                warn($"[DeliveredStow] {record.RequestId}: courier run {run} {progress.State} ({progress.Code}: {progress.Detail}).");
                Settle(record, outcome, message, now);
                courier.Forget(run, CourierRequestId(record.RequestId));
                return true;
        }
    }

    private static void SetLine(DeliveredStowForwardRecordLine line, string code, string message)
    {
        line.Code = code;
        line.Message = message;
    }

    /// <summary>Settles a forward that cannot run: every line still waiting gets the reason. Always true.</summary>
    private bool Refuse(DeliveredStowForwardRecord record, string code, string message, DateTime now)
    {
        foreach (var line in record.Lines.Where(line => line.Code == ForwardPending))
            SetLine(line, code, message);
        warn($"[DeliveredStow] {record.RequestId} not forwarded: {code} ({message}).");
        Settle(record, code, message, now);
        return true;
    }

    private void Settle(DeliveredStowForwardRecord record, string? code, string message, DateTime now)
    {
        record.State = ForwardStateSettled;
        record.Code = code;
        record.Message = message;
        record.SettledUtc = now;
        automationWaitSince.Remove(record.RequestId);
        courierWaitSince.Remove(record.RequestId);
        progressFailures.Remove(record.RequestId);
    }

    private const string NothingToForward = "Nothing to forward.";

    private static string LostMessage(DeliveredStowLostRequest gone) =>
        $"Request ID was accepted before, but Quartermaster's forward journal became unreadable and was moved aside to " +
        $"{gone.Journal}; it is not planned or sent again. Check where its goods are before resubmitting under a new ID.";

    private DeliveredStowStatus ForwardStatus(DeliveredStowForwardRecord record, OperationRecord? operation)
    {
        var terminal = operation is not null && OperationStatuses.IsTerminal(operation.Status);
        var retainers = cache();
        // One status line per item variant; its record lines are the item's forwards, one per other character.
        var lines = record.Lines.GroupBy(line => (line.ItemId, line.IsHq)).Select(group =>
        {
            var line = group.First();
            var local = operation?.Lines.Where(candidate => candidate.ItemId == line.ItemId && candidate.IsHighQuality == line.IsHq).ToArray() ?? [];
            var planned = local.Sum(candidate => candidate.TargetQuantity);
            var stowed = local.Sum(candidate => candidate.TransferredQuantity);
            var forwards = group.Where(entry => entry.Character is not null).Select(entry => new DeliveredStowForwardLine
            {
                Character = Character(entry.Character!),
                Homes = Homes(entry.Character!, entry.RetainerIds, retainers),
                Wanted = entry.Wanted,
                Planned = entry.Planned,
                Delivered = entry.Delivered,
                Code = entry.Code,
                Message = entry.Message,
            }).ToArray();
            return new DeliveredStowLine
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                IsHq = line.IsHq,
                Requested = line.Requested,
                Planned = planned,
                Stowed = stowed,
                Code = planned <= 0 ? line.LocalCode
                    : !terminal ? LinePlanned
                    : stowed >= planned ? LineStowed
                    : stowed > 0 ? LinePartlyStowed
                    : LineNotStowed,
                Destinations = planned > 0 ? Destinations(operation!, local) : line.LocalDestinations.ToArray(),
                Forward = forwards.FirstOrDefault(),
                Forwards = forwards.Length == 0 ? null : forwards,
            };
        }).ToArray();

        var settled = record.State == ForwardStateSettled;
        var localOpen = operation is not null && !terminal;
        var status = !settled
            ? localOpen ? operation!.Status : OperationStatuses.Running
            : Combine(operation?.Status, ForwardOutcome(record));
        var message = !settled
            ? localOpen ? operation!.Message : record.Message
            : string.Join(" ", new[] { operation?.Message, record.Message }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return new()
        {
            Schema = StatusSchema,
            ProviderInstanceId = providerInstanceId,
            RequestId = record.RequestId,
            OperationId = record.OperationId,
            Status = status,
            Message = message,
            Lines = lines,
            DepositStatus = operation?.Status,
            Forward = new()
            {
                State = record.State,
                RunId = record.Run?.ToString(),
                Code = record.Code,
                Message = record.Message,
            },
        };
    }

    /// <summary>The courier part's outcome, from the lines it tried to move; null when it tried none.</summary>
    private static string? ForwardOutcome(DeliveredStowForwardRecord record)
    {
        var tried = record.Lines.Where(line => line.Character is not null && (line.Planned > 0 || !NeutralForwardCodes.Contains(line.Code))).ToArray();
        if (tried.Length == 0)
            return null;
        // Cancelled by its requester before anything was carried.
        if (record.CancelRequestedUtc is not null && tried.All(line => line.Planned <= 0))
            return OperationStatuses.Cancelled;
        if (tried.Any(line => line.Code == "RunIndeterminate"))
            return OperationStatuses.Indeterminate;
        if (tried.All(line => line.Code == ForwardDelivered))
            return OperationStatuses.Succeeded;
        return tried.Any(line => line.Delivered is > 0) ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed;
    }

    /// <summary>One status for the owner's own stow and the courier part together.</summary>
    private static string Combine(string? local, string? forward)
    {
        if (local is null)
            return forward ?? NothingToStow;
        if (forward is null)
            return local;
        if (local == OperationStatuses.Cancelled)
            return local;
        // The requester cancelled the forward only: the owner's own stow stands as it settled.
        if (forward == OperationStatuses.Cancelled)
            return local;
        if (local == OperationStatuses.Indeterminate || forward == OperationStatuses.Indeterminate)
            return OperationStatuses.Indeterminate;
        static string Normal(string status) => status == OperationStatuses.Rejected ? OperationStatuses.Failed : status;
        var (a, b) = (Normal(local), Normal(forward));
        return a == b && a is OperationStatuses.Succeeded or OperationStatuses.Failed ? a : OperationStatuses.PartiallySucceeded;
    }

    /// <summary>
    /// The journaled forward of an accepted request: one record line per item variant and other character it is
    /// forwarded to (each its own courier line), or one with no character for an item forwarded nowhere. One with
    /// nothing to send (only lines the caller already sent directly) is settled from the start and only keeps those
    /// lines answerable.
    /// </summary>
    private DeliveredStowForwardRecord Record(DeliveredStowRequest request, string hash, string? operationId, OwnerScope owner,
        IReadOnlyList<DeliveredStowLine> lines)
    {
        var now = utcNow();
        var sends = lines.Any(line => ForwardsOf(line).Any(forward => forward.Code == ForwardPending));
        DeliveredStowForwardRecordLine Line(DeliveredStowLine line, DeliveredStowForwardLine? forward) => new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            IsHq = line.IsHq,
            Requested = line.Requested,
            SentDirect = request.Items!.FirstOrDefault(item => item.ItemId == line.ItemId && item.IsHq == line.IsHq)?.SentDirect ?? 0,
            LocalCode = line.Code,
            LocalDestinations = [.. line.Destinations],
            Line = Guid.NewGuid(),
            Character = forward is null ? null : Scope(forward.Character),
            RetainerIds = forward?.Homes.Select(home => home.RetainerId).ToList() ?? [],
            Wanted = forward?.Wanted ?? 0,
            Code = forward?.Code ?? string.Empty,
            Message = forward?.Message ?? string.Empty,
        };
        return new()
        {
            RequestId = request.RequestId,
            CanonicalHash = hash,
            OperationId = operationId,
            PlanId = repository.Read(state => ListingPlanCatalog.DefaultPlan(state, owner))?.Id,
            Owner = owner with { },
            CreatedUtc = now,
            State = sends ? ForwardStatePending : ForwardStateSettled,
            Message = sends ? "Waiting for the owner's own stow to settle." : NothingToForward,
            SettledUtc = sends ? null : now,
            Lines = lines.SelectMany(line => ForwardsOf(line).ToArray() is { Length: > 0 } forwards
                ? forwards.Select(forward => Line(line, forward))
                : [Line(line, null)]).ToList(),
        };
    }

    // ---- Characters and homes -------------------------------------------------------------------------

    private static DeliveredStowCharacter Character(OwnerScope owner) => new()
    {
        LocalContentId = owner.LocalContentId ?? 0,
        HomeWorldId = owner.HomeWorldId ?? 0,
        CharacterName = owner.CharacterName,
        HomeWorldName = owner.HomeWorldName,
    };

    private static OwnerScope Scope(DeliveredStowCharacter character) => new()
    {
        LocalContentId = character.LocalContentId,
        HomeWorldId = character.HomeWorldId,
        CharacterName = character.CharacterName,
        HomeWorldName = character.HomeWorldName,
    };

    private static IReadOnlyList<DeliveredStowHome> Homes(OwnerScope character, IEnumerable<ulong> retainerIds,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers) =>
        retainerIds.Distinct().Select(id => new DeliveredStowHome
        {
            RetainerId = id,
            RetainerName = retainers.TryGetValue(id, out var retainer) && retainer.Owner.Matches(character) ? retainer.RetainerName : null,
        }).ToArray();

    private static string HomeList(IEnumerable<DeliveredStowHome> homes) =>
        string.Join(" → ", homes.Select(home => home.RetainerName ?? $"Retainer {home.RetainerId}"));

    // ---- Forward store ------------------------------------------------------------------------

    private void LoadForwards()
    {
        try
        {
            var document = forwardStore!.Load();
            forwards.AddRange(document.Requests.Where(record => !Expired(record, utcNow())));
            lost.AddRange(document.Lost);
        }
        catch (Exception exception)
        {
            // An unreadable journal is moved aside so it is never overwritten. Its forwards are no longer followed, but
            // every request id still legible in it is remembered and refused, so none is planned and sent again.
            var aside = $"{forwardStore!.Path}.unreadable-{utcNow():yyyyMMddHHmmss}";
            var salvaged = Salvage(forwardStore.Path, aside);
            try { File.Move(forwardStore.Path, aside); }
            catch (Exception move)
            {
                forwardStoreBlocked = $"the unreadable forward journal could not be moved aside ({move.Message})";
                warn($"[DeliveredStow] ForwardJournalUnreadable: {forwardStore.Path} is unreadable ({exception.Message}) and could not be moved aside ({move.Message}); new forwards are refused until it is fixed or removed.");
                return;
            }
            lost.AddRange(salvaged);
            warn($"[DeliveredStow] ForwardJournalUnreadable: {exception.Message}; moved to {aside}. {salvaged.Count} request id(s) recovered from it are refused if submitted again.");
            try { forwardStore.Save(new() { Lost = [.. lost] }); }
            catch (Exception save)
            {
                forwardsDirty = true;
                warn($"[DeliveredStow] Could not record the recovered request ids yet ({save.Message}); retrying on the next tick.");
            }
        }
    }

    private static readonly Regex SalvagePattern = new(
        "\"requestId\"\\s*:\\s*\"(?<id>(?:[^\"\\\\]|\\\\.)*)\"(?:\\s*,\\s*\"canonicalHash\"\\s*:\\s*\"(?<hash>[0-9a-fA-F]*)\")?",
        RegexOptions.CultureInvariant);

    /// <summary>Every request id (with its hash when it follows) still legible in an unreadable forward journal.</summary>
    private List<DeliveredStowLostRequest> Salvage(string path, string aside)
    {
        var found = new List<DeliveredStowLostRequest>();
        try
        {
            foreach (Match match in SalvagePattern.Matches(File.ReadAllText(path)))
            {
                string id;
                try { id = JsonSerializer.Deserialize<string>($"\"{match.Groups["id"].Value}\"") ?? string.Empty; }
                catch (JsonException) { continue; }
                if (id.Length == 0 || found.Any(entry => entry.RequestId == id))
                    continue;
                found.Add(new() { RequestId = id, CanonicalHash = match.Groups["hash"].Success ? match.Groups["hash"].Value : null, Journal = aside, LostUtc = utcNow() });
            }
        }
        catch (Exception exception)
        {
            warn($"[DeliveredStow] No request id could be recovered from the unreadable forward journal: {exception.Message}");
        }
        return found;
    }

    /// <summary>
    /// A settled forward past the retention that has a local operation (whose request record keeps the id answered).
    /// A forward with no local operation is its request's only memory and is kept for good.
    /// </summary>
    private static bool Expired(DeliveredStowForwardRecord record, DateTime now) =>
        record.State == ForwardStateSettled && record.OperationId is not null && record.SettledUtc is { } at && at < now - SettledRetention;

    /// <summary>Writes the forward journal, dropping expired forwards. Under <see cref="gate"/>.</summary>
    private void SaveForwards()
    {
        if (forwardStore is null)
            return;
        if (forwardStoreBlocked is { } why)
            throw new InvalidOperationException($"The forward journal is not saved: {why}.");
        var now = utcNow();
        forwards.RemoveAll(record => Expired(record, now));
        forwardStore.Save(new() { Requests = [.. forwards], Lost = [.. lost] });
    }

    // ---- Validation ---------------------------------------------------------------------------

    private (string Code, string Message)? Validate(DeliveredStowRequest request, OwnerScope owner)
    {
        if (request.Schema != RequestSchema)
            return ("unsupported_schema", $"Expected schema '{RequestSchema}'.");
        if (string.IsNullOrWhiteSpace(request.ProviderInstanceId) || request.ProviderInstanceId != providerInstanceId)
            return ("provider_mismatch", "providerInstanceId does not match this Quartermaster provider instance.");
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return ("missing_request_id", "requestId is required.");
        if (!owner.HasStableIdentity)
            return ("missing_owner_scope", "Current character owner scope is unavailable.");
        if (request.Owner is not { LocalContentId: > 0, HomeWorldId: > 0 } requestOwner)
            return ("missing_owner_scope", "owner.localContentId and owner.homeWorldId are required.");
        if (requestOwner.LocalContentId != owner.LocalContentId || requestOwner.HomeWorldId != owner.HomeWorldId)
            return ("owner_mismatch", "Request owner does not match the logged-in character and home world.");
        if (request.Items is not { Count: > 0 } items)
            return ("empty_items", "At least one item is required.");
        if (items.GroupBy(item => (item.ItemId, item.IsHq)).Any(group => group.Count() > 1))
            return ("duplicate_item", "Each item and quality may appear once.");
        // sentDirect lets an item be reported with nothing delivered to the owner at all.
        if (items.Any(item => item.ItemId == 0 || item.Quantity < 0 || item.SentDirect < 0 || item.Quantity == 0 && item.SentDirect == 0))
            return ("invalid_item", "itemId and a positive quantity are required for every item.");
        return null;
    }

    private static string CanonicalHash(DeliveredStowRequest request)
    {
        var canonical = new StringBuilder()
            .Append(request.Schema).Append('|')
            .Append(request.RequestId.Trim()).Append('|')
            .Append(request.Owner!.LocalContentId).Append('|')
            .Append(request.Owner.HomeWorldId);
        foreach (var item in request.Items!.OrderBy(item => item.ItemId).ThenBy(item => item.IsHq))
        {
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.IsHq ? 'H' : 'N').Append('|').Append(item.Quantity);
            // Absent before sentDirect existed: a request without it hashes exactly as it always did.
            if (item.SentDirect > 0)
                canonical.Append("|d").Append(item.SentDirect);
        }
        // Likewise absent unless the caller opts into forwarding.
        if (request.AllowForward)
            canonical.Append("|f1");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private DeliveredStowStatus Reject(string requestId, string code, string message) => new()
    {
        Schema = AckSchema,
        ProviderInstanceId = providerInstanceId,
        RequestId = requestId ?? string.Empty,
        Status = OperationStatuses.Rejected,
        ErrorCode = code,
        Message = message,
    };

    private static string Serialize(DeliveredStowStatus status) => JsonSerializer.Serialize(status, JsonOptions);

    private sealed record Pending(string CanonicalHash, string? OperationId, DeliveredStowStatus Ack);
}

/// <summary><c>delivered-stow-forwards.json</c>: every delivered-stow request with homes on another character.</summary>
public sealed class DeliveredStowForwardDocument
{
    public List<DeliveredStowForwardRecord> Requests { get; set; } = [];
    /// <summary>Requests of an unreadable journal that was moved aside: refused if submitted again. Never pruned.</summary>
    public List<DeliveredStowLostRequest> Lost { get; set; } = [];
}

/// <summary>A request id recovered from an unreadable forward journal, with its hash when legible, and where that journal went.</summary>
public sealed class DeliveredStowLostRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string? CanonicalHash { get; set; }
    public string Journal { get; set; } = string.Empty;
    public DateTime LostUtc { get; set; }
}

/// <summary>
/// One request's courier forward. State: pending (waiting for the owner's own stow), running (Run started; it is
/// never started again), settled.
/// </summary>
public sealed class DeliveredStowForwardRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string CanonicalHash { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public Guid? PlanId { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public Guid? Run { get; set; }
    public string? Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime? SettledUtc { get; set; }
    /// <summary>
    /// Set when the requester cancelled the delivered stow or its forward; the forward then never starts. CancelCode is
    /// LocalDepositCancelled or ForwardCancelled. Absent (null) in journals written before these fields existed.
    /// </summary>
    public DateTime? CancelRequestedUtc { get; set; }
    public string? CancelCode { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>
    /// When the bound run was first seen not running after the cancel; from then on the cancel no longer stops it, so a
    /// resume carries on. Null until then, and in journals written before this field existed (the next tick observes it).
    /// </summary>
    public DateTime? CancelStopObservedUtc { get; set; }
    public List<DeliveredStowForwardRecordLine> Lines { get; set; } = [];
}

/// <summary>Quartermaster.v1.CancelDeliveredStowForward request; Schema and ProviderInstanceId are optional.</summary>
public sealed record DeliveredStowForwardCancelRequest
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

/// <summary>Status: cancelled, stopping, already_terminal, cancel_queued, not_found or rejected (ErrorCode names why).</summary>
public sealed record DeliveredStowForwardCancelAcknowledgement
{
    public string Schema { get; init; } = DeliveredStowService.ForwardCancelAckSchema;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Every requested line, so the status reads the same with or without the local operation; Character is null for a
/// line forwarded nowhere. An item forwarded to several characters has one line per character (a journal written
/// before that has at most one). Line is the courier line its stacks travel under. SentDirect: units of the item the
/// caller already sent straight to its homes on other characters (they count against those homes' room and caps).
/// </summary>
public sealed class DeliveredStowForwardRecordLine
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public int Requested { get; set; }
    public int SentDirect { get; set; }
    public string LocalCode { get; set; } = string.Empty;
    public List<string> LocalDestinations { get; set; } = [];
    public Guid Line { get; set; }
    public OwnerScope? Character { get; set; }
    public List<ulong> RetainerIds { get; set; } = [];
    public int Wanted { get; set; }
    public int Planned { get; set; }
    public int? Delivered { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
