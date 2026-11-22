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
/// </summary>
public sealed class DeliveredStowService
{
    public const string Capability = "deliveredStow.v1";
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

    public event Action<string>? OperationChanged;

    public DeliveredStowService(
        string providerInstanceId,
        StateRepository repository,
        IFrameworkWorkQueue workQueue,
        OperationJournal journal,
        Func<IReadOnlyDictionary<ulong, CachedRetainer>> cache,
        Func<OwnerScope> currentOwner,
        Func<OwnerScope, IReadOnlyList<InventoryBag>> carriedBags,
        Func<uint, int> maxStackSize,
        Action<string> warn,
        Func<DateTime>? utcNow = null)
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
            if (pending.TryGetValue(request.RequestId, out var queued))
            {
                return queued.CanonicalHash == hash
                    ? Serialize(queued.Ack with { Message = "Idempotent replay returned the queued stow operation." })
                    : Serialize(Reject(request.RequestId, "request_conflict", "Request ID is queued with a different canonical payload."));
            }
            if (transient.TryGetValue(request.RequestId, out var failed))
                return Serialize(failed with { Schema = AckSchema });

            var (batch, lines) = Plan(request, owner);
            if (batch.PlannedQuantity <= 0)
            {
                // Nothing routable: no operation, nothing persisted. Callers record this outcome themselves.
                return Serialize(new DeliveredStowStatus
                {
                    Schema = AckSchema,
                    ProviderInstanceId = providerInstanceId,
                    RequestId = request.RequestId,
                    Status = NothingToStow,
                    Message = lines.Any(line => line.Code == LineNoRoom)
                        ? "No delivered item has room in its home retainers."
                        : "No delivered item has a home retainer.",
                    Lines = lines,
                });
            }

            var operationId = $"rq-dstow-{Guid.NewGuid():N}";
            var ack = new DeliveredStowStatus
            {
                Schema = AckSchema,
                ProviderInstanceId = providerInstanceId,
                RequestId = request.RequestId,
                OperationId = operationId,
                Status = OperationStatuses.Queued,
                Message = "Stow queued for persistence and automatic execution.",
                Lines = lines,
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
                    Destinations = operation.DepositCandidates
                        .Where(candidate => group.Any(line => candidate.CapacityByVariant.ContainsKey(line.AuthorizationKey)))
                        .Select(candidate => candidate.RetainerName)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
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

    private void Apply(DeliveredStowRequest request, string operationId, string hash)
    {
        try
        {
            var owner = currentOwner();
            if (Validate(request, owner) is { } error)
                throw new InvalidOperationException($"{error.Code}: {error.Message}");
            var (batch, _) = Plan(request, owner);
            if (batch.PlannedQuantity <= 0)
                throw new InvalidOperationException("Delivered items no longer have routable home-retainer room or are no longer carried.");
            journal.CreateRequestedDeposit(owner, batch, request.RequestId, operationId, hash);
            lock (gate)
                pending.Remove(request.RequestId);
        }
        catch (Exception exception)
        {
            warn($"[DeliveredStow] Could not persist {request.RequestId} ({operationId}): {exception.Message}");
            lock (gate)
            {
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
        OperationChanged?.Invoke(operationId);
    }

    private (StowageDepositBatch Batch, IReadOnlyList<DeliveredStowLine> Lines) Plan(DeliveredStowRequest request, OwnerScope owner)
    {
        var carried = carriedBags(owner)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
        var requested = request.Items!
            .Select(item => (Key: (item.ItemId, item.IsHq), item.ItemName, item.Quantity))
            .ToArray();
        var stowable = requested
            .Select(item => (item.Key.ItemId, item.Key.IsHq, Quantity: Math.Min(item.Quantity, carried.GetValueOrDefault(item.Key))))
            .Where(item => item.Quantity > 0)
            .ToArray();

        var plan = repository.Read(state => ListingPlanCatalog.OwnerPlan(state, owner));
        var stowRequests = plan is null
            ? []
            : MarketRunPlanner.StowRequests(plan.Id, plan.Reserves, stowable);
        var batch = StowageRouter.BuildBatch(stowRequests, cache(), owner, maxStackSize, utcNow());

        var lines = requested.Select(item =>
        {
            var routes = batch.Routes
                .Where(route => route.Request.ItemId == item.Key.ItemId && route.Request.IsHighQuality == item.Key.IsHq)
                .ToArray();
            var planned = routes.Sum(route => route.RoutedQuantity);
            var code = carried.GetValueOrDefault(item.Key) <= 0 ? LineNotCarried
                : routes.Length == 0 ? LineNoHome
                : planned <= 0 ? LineNoRoom
                : LinePlanned;
            return new DeliveredStowLine
            {
                ItemId = item.Key.ItemId,
                ItemName = item.ItemName,
                IsHq = item.Key.IsHq,
                Requested = item.Quantity,
                Planned = planned,
                Code = code,
                Destinations = routes.SelectMany(route => route.Allocations).Select(allocation => allocation.RetainerName).Distinct(StringComparer.Ordinal).ToArray(),
            };
        }).ToArray();
        return (batch, lines);
    }

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
        if (items.Any(item => item.ItemId == 0 || item.Quantity <= 0))
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
            canonical.Append('|').Append(item.ItemId).Append('|').Append(item.IsHq ? 'H' : 'N').Append('|').Append(item.Quantity);
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

    private sealed record Pending(string CanonicalHash, string OperationId, DeliveredStowStatus Ack);
}
