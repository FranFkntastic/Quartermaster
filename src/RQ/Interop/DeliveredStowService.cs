using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// An item may also have homes on another character of this account (<see cref="ListingReserveRule.RemoteHomes"/>):
/// once the owner's own stow settles, what its local homes did not take is carried there by the courier in one run
/// (<see cref="Tick"/>), and the line's Forward says how that went. Only units still in the owner's bags are sent on,
/// never more than the request delivered; units the caller already sent straight to those homes (SentDirect) are
/// never forwarded. The forward is journaled in its own file before the local operation is created, so a request id
/// is answered the same across reloads, and its courier run never starts twice.
/// </summary>
public sealed class DeliveredStowService
{
    public const string Capability = "deliveredStow.v1";
    public const string ForwardCapability = "deliveredStowForward.v1";
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

    private const string ForwardStatePending = "pending";
    private const string ForwardStateRunning = "running";
    private const string ForwardStateSettled = "settled";

    /// <summary>Forward codes that leave goods where they are without anything having gone wrong.</summary>
    private static readonly HashSet<string> NeutralForwardCodes =
        [ForwardPending, ForwardRunning, ForwardNoRoom, ForwardNothingLeft, ForwardSentDirect];

    /// <summary>How long a forward waits for another automation job to let go once the owner's own stow has settled.</summary>
    private static readonly TimeSpan AutomationWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
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
    private readonly DeliveredStowCourier? courier;
    private readonly AtomicDocumentStore<DeliveredStowForwardDocument>? forwardStore;
    private readonly List<DeliveredStowForwardRecord> forwards = [];
    private readonly Dictionary<string, DateTime> automationWaitSince = new(StringComparer.Ordinal);
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
        DeliveredStowCourier? courier = null,
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
            if (pending.TryGetValue(request.RequestId, out var queued))
            {
                return queued.CanonicalHash == hash
                    ? Serialize(queued.Ack with { Message = "Idempotent replay returned the queued stow operation." })
                    : Serialize(Reject(request.RequestId, "request_conflict", "Request ID is queued with a different canonical payload."));
            }
            if (transient.TryGetValue(request.RequestId, out var failed))
                return Serialize(failed with { Schema = AckSchema });

            var (batch, lines) = Plan(request, owner);
            var forwardWanted = lines.Sum(line => line.Forward is { Code: ForwardPending } forward ? forward.Wanted : 0);
            if (batch.PlannedQuantity <= 0 && forwardWanted <= 0)
            {
                // Nothing routable: no operation, nothing persisted. Callers record this outcome themselves.
                return Serialize(new DeliveredStowStatus
                {
                    Schema = AckSchema,
                    ProviderInstanceId = providerInstanceId,
                    RequestId = request.RequestId,
                    Status = NothingToStow,
                    Message = lines.Any(line => line.Code == LineNoRoom || line.Forward?.Code == ForwardNoRoom)
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
                Forward = lines.Any(line => line.Forward is not null)
                    ? new() { State = ForwardStatePending, Message = "Waiting for the owner's own stow to settle." }
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
            if (operationId is null && !lines.Any(line => line.Forward is { Code: ForwardPending }))
                throw new InvalidOperationException("Delivered items no longer have room in their homes on other characters or are no longer carried.");
            if (lines.Any(line => line.Forward is not null))
            {
                // Journaled before the local operation exists, so a forward is never lost behind a stow that ran.
                record = Record(request.RequestId, hash, operationId, owner, lines);
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

    private (StowageDepositBatch Batch, DeliveredStowLine[] Lines) Plan(DeliveredStowRequest request, OwnerScope owner)
    {
        var carried = carriedBags(owner)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var requested = request.Items!
            .Select(item => (Key: (item.ItemId, item.IsHq), item.ItemName, item.Quantity, item.SentDirect))
            .ToArray();
        var stowable = requested
            .Select(item => (item.Key.ItemId, item.Key.IsHq, Quantity: Math.Min(item.Quantity, carried.GetValueOrDefault(item.Key))))
            .Where(item => item.Quantity > 0)
            .ToArray();

        var plan = repository.Read(state => ListingPlanCatalog.OwnerPlan(state, owner));
        var stowRequests = plan is null
            ? []
            : MarketRunPlanner.StowRequests(plan.Id, plan.Reserves, stowable);
        var retainers = cache();
        var batch = StowageRouter.BuildBatch(stowRequests, retainers, owner, maxStackSize, utcNow());

        var lines = requested.Select(item =>
        {
            var routes = batch.Routes
                .Where(route => route.Request.ItemId == item.Key.ItemId && route.Request.IsHighQuality == item.Key.IsHq)
                .ToArray();
            var planned = routes.Sum(route => route.RoutedQuantity);
            var held = Math.Min(item.Quantity, carried.GetValueOrDefault(item.Key));
            var remote = plan is null ? null : RemoteHomesOf(plan, item.Key.ItemId, item.Key.IsHq);
            var code = item.Quantity == 0 && item.SentDirect > 0 ? LineSentDirect
                : held <= 0 ? LineNotCarried
                : routes.Length == 0 ? remote is null ? LineNoHome : LineHomedElsewhere
                : planned <= 0 ? LineNoRoom
                : LinePlanned;
            return new DeliveredStowLine
            {
                ItemId = item.Key.ItemId,
                ItemName = item.ItemName,
                IsHq = item.Key.IsHq,
                Requested = item.Quantity,
                Carried = held,
                Planned = planned,
                Code = code,
                Destinations = routes.SelectMany(route => route.Allocations).Select(allocation => allocation.RetainerName).Distinct(StringComparer.Ordinal).ToArray(),
                Forward = remote is null ? null : new DeliveredStowForwardLine
                {
                    Character = Character(remote.Character),
                    Homes = Homes(remote.Character, remote.RetainerIds, retainers),
                    Wanted = Math.Max(0, held - planned),
                },
            };
        }).ToArray();

        // What the local homes leave goes to the remote homes, routed against that character's observed room.
        var sentDirect = requested.ToDictionary(item => item.Key, item => item.SentDirect);
        foreach (var group in lines.Select((line, index) => (Line: line, Index: index))
                     .Where(entry => entry.Line.Forward is not null)
                     .GroupBy(entry => (entry.Line.Forward!.Character.LocalContentId, entry.Line.Forward.Character.HomeWorldId)))
        {
            var character = Scope(group.First().Line.Forward!.Character);
            var remoteBatch = RemoteBatch(plan?.Id, group
                .Where(entry => entry.Line.Forward!.Wanted > 0)
                .Select(entry => (entry.Line.ItemId, entry.Line.ItemName, entry.Line.IsHq, entry.Line.Forward!.Wanted,
                    (IReadOnlyList<ulong>)entry.Line.Forward.Homes.Select(home => home.RetainerId).ToArray())), character, retainers);
            foreach (var (line, index) in group)
            {
                var forward = line.Forward!;
                var direct = sentDirect.GetValueOrDefault((line.ItemId, line.IsHq));
                var routed = remoteBatch.Routes.Where(route => route.Request.ItemId == line.ItemId && route.Request.IsHighQuality == line.IsHq)
                    .Sum(route => route.RoutedQuantity);
                var homes = HomeList(forward.Homes);
                var already = direct > 0 ? $"; {direct:N0} already went there directly" : string.Empty;
                lines[index] = line with
                {
                    Forward = forward.Wanted <= 0
                        ? direct > 0
                            ? forward with { Code = ForwardSentDirect, Message = $"{direct:N0} went to {forward.Character.CharacterName} directly." }
                            : forward with { Code = ForwardNothingLeft, Message = line.Carried is > 0 ? "The owner's own homes take all of it." : "Not carried." }
                        : routed <= 0
                            ? forward with { Code = ForwardNoRoom, Message = $"No observed room on {forward.Character.CharacterName} ({homes}) for {forward.Wanted:N0}; it stays in the bags{already}." }
                            : forward with
                            {
                                Code = ForwardPending,
                                Message = routed < forward.Wanted
                                    ? $"{routed:N0} go to {forward.Character.CharacterName} after the owner's own stow; {forward.Wanted - routed:N0} have no observed room there and stay in the bags{already}."
                                    : $"{routed:N0} go to {forward.Character.CharacterName} after the owner's own stow{already}.",
                                Wanted = routed,
                            },
                };
            }
        }
        return (batch, lines);
    }

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
            if (!forwards.Any(record => record.State != ForwardStateSettled))
                return;
            foreach (var record in forwards.Where(record => record.State != ForwardStateSettled).ToArray())
            {
                try
                {
                    if (!Advance(record, now))
                        continue;
                    if (record.State == ForwardStateSettled && record.OperationId is { } operationId)
                        changedOperations.Add(operationId);
                    SaveForwards();
                }
                catch (Exception exception)
                {
                    warn($"[DeliveredStow] Forward of {record.RequestId} failed: {exception}");
                    // A run that started is followed again next tick; one that never started cannot start safely now.
                    if (record.Run is null)
                    {
                        Refuse(record, "ForwardFailed", $"Quartermaster could not forward: {exception.Message}", now);
                        try { SaveForwards(); }
                        catch (Exception save) { warn($"[DeliveredStow] Could not save the forward of {record.RequestId}: {save.Message}"); }
                    }
                }
            }
        }
        foreach (var operationId in changedOperations)
            OperationChanged?.Invoke(operationId);
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
                return Refuse(record, "LocalDepositCancelled", "The owner's own stow was cancelled; nothing was forwarded.", now);
        }
        if (courier is null)
            return Refuse(record, "CourierNotLoaded", "The courier is not loaded.", now);
        var owner = currentOwner();
        if (!owner.Matches(record.Owner))
            return Refuse(record, "OwnerChanged", $"{record.Owner.CharacterName} is no longer the logged-in character; the goods stay in their bags.", now);
        if (courier.Blocked() is { } blocked)
            return Refuse(record, blocked.Code, blocked.Message, now);
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

        // Only what is in the bags now is sent on, and never more than this request left for the remote homes.
        var carried = carriedBags(owner)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var retainers = cache();
        var groups = new List<(OwnerScope Destination, IReadOnlyList<DeliveredStowForwardUnit> Units)>();
        var bound = new List<(DeliveredStowForwardRecordLine Line, int Units)>();
        foreach (var group in open.GroupBy(line => (line.Character!.LocalContentId, line.Character.HomeWorldId)))
        {
            var character = group.First().Character!;
            var wanted = group.Select(line => (Line: line, Units: Math.Min(line.Wanted, carried.GetValueOrDefault((line.ItemId, line.IsHq))))).ToArray();
            foreach (var (line, _) in wanted.Where(entry => entry.Units <= 0))
                SetLine(line, ForwardNothingLeft, "None of it is left in the owner's bags to forward.");
            var batch = RemoteBatch(record.PlanId, wanted.Where(entry => entry.Units > 0)
                .Select(entry => (entry.Line.ItemId, entry.Line.ItemName, entry.Line.IsHq, entry.Units, (IReadOnlyList<ulong>)entry.Line.RetainerIds)), character, retainers);
            var units = new List<DeliveredStowForwardUnit>();
            foreach (var (line, _) in wanted.Where(entry => entry.Units > 0))
            {
                var homes = batch.Routes
                    .Where(route => route.Request.ItemId == line.ItemId && route.Request.IsHighQuality == line.IsHq)
                    .SelectMany(route => route.Allocations)
                    .Where(allocation => allocation.Quantity > 0)
                    .Select(allocation => (allocation.RetainerId, allocation.Quantity))
                    .ToArray();
                if (homes.Length == 0)
                {
                    SetLine(line, ForwardNoRoom, $"No observed room on {character.CharacterName} ({HomeList(Homes(character, line.RetainerIds, retainers))}); it stays in the bags.");
                    continue;
                }
                units.Add(new(line.Line, line.ItemId, line.ItemName, line.IsHq, homes));
                bound.Add((line, homes.Sum(home => home.Quantity)));
            }
            if (units.Count > 0)
                groups.Add((character, units));
        }
        if (groups.Count == 0)
        {
            Settle(record, null, "Nothing was left to forward.", now);
            return true;
        }

        var (started, refusal, refused) = courier.Start(CourierRequestId(record.RequestId), record.CanonicalHash, owner, groups);
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
        record.Message = $"Courier run carrying {bound.Where(entry => entry.Line.Code == ForwardRunning).Sum(entry => entry.Units):N0} unit(s) to other characters.";
        return true;
    }

    /// <summary>Follows a started run; true when the record changed.</summary>
    private bool Follow(DeliveredStowForwardRecord record, Guid run, DateTime now)
    {
        if (courier is null)
            return false;
        if (courier.Progress(run, CourierRequestId(record.RequestId)) is not { } progress)
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
        switch (progress.State)
        {
            case "Starting":
            case "Running":
            case "Idle":
                return changed;
            case "Completed":
                foreach (var line in running)
                {
                    // A completed run deposited every stack it carried; the journal's tally is used while it still has it.
                    var delivered = line.Delivered ??= line.Planned;
                    SetLine(line,
                        delivered >= line.Planned ? ForwardDelivered : delivered > 0 ? ForwardPartlyDelivered : ForwardNotDelivered,
                        $"{delivered:N0} of {line.Planned:N0} deposited on {line.Character!.CharacterName}.");
                }
                Settle(record, progress.Code is { Length: > 0 } code && code != "Succeeded" ? code : null, "Courier run completed.", now);
                return true;
            default:
                var (outcome, message) = progress.State switch
                {
                    "Refused" => ("RunRefused", $"The courier refused the run ({progress.Code}: {progress.Detail})."),
                    "Failed" => ("RunFailed", $"The courier run failed ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    "Interrupted" => ("RunInterrupted", $"The courier run was interrupted ({progress.Code}: {progress.Detail}); resume or clear it in Courier."),
                    "Indeterminate" => ("RunIndeterminate", $"The courier run ended unverified ({progress.Code}: {progress.Detail}); check its custody in Courier."),
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
    }

    private DeliveredStowStatus ForwardStatus(DeliveredStowForwardRecord record, OperationRecord? operation)
    {
        var terminal = operation is not null && OperationStatuses.IsTerminal(operation.Status);
        var retainers = cache();
        var lines = record.Lines.Select(line =>
        {
            var local = operation?.Lines.Where(candidate => candidate.ItemId == line.ItemId && candidate.IsHighQuality == line.IsHq).ToArray() ?? [];
            var planned = local.Sum(candidate => candidate.TargetQuantity);
            var stowed = local.Sum(candidate => candidate.TransferredQuantity);
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
                Forward = line.Character is not { } character ? null : new DeliveredStowForwardLine
                {
                    Character = Character(character),
                    Homes = Homes(character, line.RetainerIds, retainers),
                    Wanted = line.Wanted,
                    Planned = line.Planned,
                    Delivered = line.Delivered,
                    Code = line.Code,
                    Message = line.Message,
                },
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
        if (local == OperationStatuses.Indeterminate || forward == OperationStatuses.Indeterminate)
            return OperationStatuses.Indeterminate;
        static string Normal(string status) => status == OperationStatuses.Rejected ? OperationStatuses.Failed : status;
        var (a, b) = (Normal(local), Normal(forward));
        return a == b && a is OperationStatuses.Succeeded or OperationStatuses.Failed ? a : OperationStatuses.PartiallySucceeded;
    }

    private DeliveredStowForwardRecord Record(string requestId, string hash, string? operationId, OwnerScope owner, IEnumerable<DeliveredStowLine> lines) => new()
    {
        RequestId = requestId,
        CanonicalHash = hash,
        OperationId = operationId,
        PlanId = repository.Read(state => ListingPlanCatalog.OwnerPlan(state, owner))?.Id,
        Owner = owner with { },
        CreatedUtc = utcNow(),
        State = ForwardStatePending,
        Message = "Waiting for the owner's own stow to settle.",
        Lines = lines.Select(line => new DeliveredStowForwardRecordLine
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            IsHq = line.IsHq,
            Requested = line.Requested,
            LocalCode = line.Code,
            LocalDestinations = [.. line.Destinations],
            Line = Guid.NewGuid(),
            Character = line.Forward is { } forward ? Scope(forward.Character) : null,
            RetainerIds = line.Forward?.Homes.Select(home => home.RetainerId).ToList() ?? [],
            Wanted = line.Forward?.Wanted ?? 0,
            Code = line.Forward?.Code ?? string.Empty,
            Message = line.Forward?.Message ?? string.Empty,
        }).ToList(),
    };

    // ---- Remote homes -------------------------------------------------------------------------

    /// <summary>The remote homes of the reserve that covers this item variant, if it has any.</summary>
    private static ReserveRemoteHomes? RemoteHomesOf(ListingPlan plan, uint itemId, bool hq) =>
        plan.Reserves.FirstOrDefault(reserve => reserve.Enabled && reserve.ItemId == itemId &&
                (reserve.Quality == ItemQualityPolicy.Any || reserve.Quality == ItemQualityPolicy.HqOnly == hq) &&
                reserve.RemoteHomes is { RetainerIds.Count: > 0, Character.HasStableIdentity: true })
            ?.RemoteHomes;

    /// <summary>Routes units into another character's ranked retainers by their observed room, exactly as local homes are.</summary>
    private StowageDepositBatch RemoteBatch(Guid? planId,
        IEnumerable<(uint ItemId, string ItemName, bool Hq, int Units, IReadOnlyList<ulong> RetainerIds)> items,
        OwnerScope character, IReadOnlyDictionary<ulong, CachedRetainer> retainers) =>
        StowageRouter.BuildBatch(
            items.Select(item => new StowageDepositRequest(planId, null, item.ItemId, item.ItemName, item.Hq, item.Units,
                new StowageRoutingPolicy { PreferredRetainerIds = [.. item.RetainerIds] })),
            retainers, character, maxStackSize, utcNow());

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
            var cutoff = utcNow() - SettledRetention;
            forwards.AddRange(forwardStore!.Load().Requests
                .Where(record => record.State != ForwardStateSettled || record.SettledUtc is not { } at || at >= cutoff));
        }
        catch (Exception exception)
        {
            // An unreadable journal is moved aside so it is never overwritten; its forwards are no longer followed.
            var aside = $"{forwardStore!.Path}.unreadable-{utcNow():yyyyMMddHHmmss}";
            warn($"[DeliveredStow] Forward journal unreadable ({exception.Message}); moved to {aside}.");
            try { File.Move(forwardStore.Path, aside); }
            catch (Exception move) { warn($"[DeliveredStow] Could not move the unreadable forward journal: {move.Message}"); }
        }
    }

    /// <summary>Writes the forward journal, dropping forwards settled longer ago than the retention. Under <see cref="gate"/>.</summary>
    private void SaveForwards()
    {
        if (forwardStore is null)
            return;
        var cutoff = utcNow() - SettledRetention;
        forwards.RemoveAll(record => record.State == ForwardStateSettled && record.SettledUtc is { } at && at < cutoff);
        forwardStore.Save(new() { Requests = [.. forwards] });
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
    public List<DeliveredStowForwardRecordLine> Lines { get; set; } = [];
}

/// <summary>
/// Every requested line, so the status reads the same with or without the local operation; Character is null for a
/// line with no remote homes. Line is the courier line its stacks travel under.
/// </summary>
public sealed class DeliveredStowForwardRecordLine
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public int Requested { get; set; }
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
