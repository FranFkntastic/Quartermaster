using RQ.Domain;
using RQ.Persistence;
using RQ.Planning;

namespace RQ.Operations;

public sealed class OperationJournal
{
    private readonly StateRepository repository;
    private readonly Func<DateTime> utcNow;

    public OperationJournal(StateRepository repository, Func<DateTime>? utcNow = null)
    {
        this.repository = repository;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public event Action<OperationRecord>? OperationChanged;

    public OperationRecord? Get(string operationId) => repository.Read(state =>
        state.Operations.FirstOrDefault(operation => operation.OperationId == operationId) is { } operation
            ? Copy(operation)
            : null);

    public OperationRecord? Current(OwnerScope owner) => repository.Read(state => state.Operations
        .Where(operation => operation.Owner.Matches(owner))
        .OrderByDescending(operation => operation.UpdatedAtUtc)
        .Select(Copy)
        .FirstOrDefault());

    public OperationRecord? NextAutomaticRetrieval(OwnerScope owner)
        => NextAutomaticOperation(owner, OperationKinds.Retrieval);

    public OperationRecord? NextAutomaticOperation(OwnerScope owner)
        => NextAutomaticOperation(owner, kind: null);

    private OperationRecord? NextAutomaticOperation(OwnerScope owner, string? kind)
    {
        if (!owner.HasStableIdentity)
            return null;
        return repository.Read(state => state.Operations
            .Where(operation => (kind == null || operation.Kind == kind) &&
                                operation.ExecuteImmediately &&
                                operation.Status == OperationStatuses.Accepted &&
                                operation.Owner.HasStableIdentity &&
                                operation.Owner.Matches(owner))
            .OrderBy(operation => operation.CreatedAtUtc)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .Select(Copy)
            .FirstOrDefault());
    }

    public IReadOnlyList<OperationRecord> ReconcileInterruptedOperations()
    {
        if (!repository.Read(state => state.Operations.Any(operation => operation.Status == OperationStatuses.Running)))
            return [];
        var reconciled = new List<OperationRecord>();
        repository.Mutate(state =>
        {
            foreach (var operation in state.Operations.Where(operation => operation.Status == OperationStatuses.Running))
            {
                operation.Status = OperationStatuses.Indeterminate;
                operation.Revision = checked(operation.Revision + 1);
                operation.UpdatedAtUtc = utcNow();
                operation.Message = "Quartermaster reloaded before live transfer completion could be verified; involved cache evidence was invalidated.";
                AddReceipt(state, operation, "InterruptedByReload", operation.Message);
                reconciled.Add(Copy(operation));
            }
        });
        foreach (var operation in reconciled)
            OperationChanged?.Invoke(operation);
        return reconciled;
    }

    public OperationRecord CreateManual(OwnerScope owner, IReadOnlyList<TargetPlanItem> plan, string kind = OperationKinds.Retrieval)
        => CreateManual(owner, plan, kind, null, null, null);

    public OperationRecord CreateRestock(OwnerScope owner, RestockPlan plan) =>
        CreateManual(
            owner,
            RestockPlanCatalog.ToExecutionRows(plan),
            OperationKinds.Retrieval,
            plan.Id,
            plan.Revision,
            plan.Name);

    public OperationRecord CreateTransferRetrieval(
        OwnerScope owner,
        StowagePlan plan,
        IReadOnlyList<TargetPlanItem> rules) =>
        CreateManual(
            owner,
            rules,
            OperationKinds.Retrieval,
            plan.Id,
            plan.Revision,
            plan.Name);

    private OperationRecord CreateManual(
        OwnerScope owner,
        IReadOnlyList<TargetPlanItem> plan,
        string kind,
        Guid? sourcePlanId,
        long? sourcePlanRevision,
        string? sourcePlanName)
    {
        if (kind is not (OperationKinds.Retrieval or OperationKinds.Deposit))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var id = $"rq-{Guid.NewGuid():N}";
        var now = utcNow();
        var enabledPlan = plan.Where(item => item.Enabled).ToArray();
        var operation = new OperationRecord
        {
            OperationId = id,
            RequestId = id,
            Kind = kind,
            Owner = owner,
            Status = OperationStatuses.Accepted,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Message = "Plan is ready for explicit execution.",
            SourcePlanId = sourcePlanId,
            SourcePlanRevision = sourcePlanRevision,
            SourcePlanName = sourcePlanName,
            SourcePlanItems = enabledPlan.Select(Copy).ToList(),
            Lines = enabledPlan
                .GroupBy(item => (item.ItemId, item.Quality))
                .Select(group => new OperationLine
                {
                    SourcePlanId = group.Select(item => item.StowagePlanId).Distinct().Count() == 1
                        ? group.First().StowagePlanId
                        : null,
                    SourceRuleId = group.Count() == 1 ? group.First().Id : null,
                    ItemId = group.Key.ItemId,
                    ItemName = group.Select(item => item.ItemName).First(name => !string.IsNullOrWhiteSpace(name)),
                    Quality = group.Key.Quality,
                    TargetQuantity = group.Sum(item => item.TargetQuantity),
                })
                .ToList(),
        };
        repository.Mutate(state =>
        {
            state.Operations.Add(operation);
            AddReceipt(state, operation, "PlanAccepted", operation.Message);
        });
        var copy = Copy(operation);
        OperationChanged?.Invoke(copy);
        return copy;
    }

    public OperationRecord CreateDeposit(
        OwnerScope owner,
        ElementalDepositPlan plan,
        string? requestId = null,
        string? operationId = null,
        bool executeImmediately = false,
        string? canonicalHash = null)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Deposit operations require stable owner identity.");
        var now = utcNow();
        var operation = new OperationRecord
        {
            OperationId = operationId ?? $"rq-{Guid.NewGuid():N}",
            RequestId = requestId ?? string.Empty,
            Kind = OperationKinds.Deposit,
            ExecuteImmediately = executeImmediately,
            Owner = owner,
            Status = OperationStatuses.Accepted,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Message = executeImmediately
                ? "Exact elemental deposit authorization is ready for automatic execution."
                : "Exact elemental deposit authorization is ready for explicit execution.",
            Lines = plan.Lines.Where(line => line.PlannedQuantity > 0).Select(line => new OperationLine
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                TargetQuantity = line.PlannedQuantity,
                ShortageQuantity = line.PlannedQuantity,
            }).ToList(),
            DepositCandidates = plan.Candidates.Select(candidate => new DepositCandidateAuthorization
            {
                RetainerId = candidate.RetainerId,
                RetainerName = candidate.RetainerName,
                ObservedAtUtc = candidate.ObservedAtUtc,
                CapacityByItem = candidate.CapacityByItem.ToDictionary(entry => entry.Key, entry => entry.Value),
            }).ToList(),
        };
        if (string.IsNullOrWhiteSpace(operation.RequestId))
            operation.RequestId = operation.OperationId;
        if (operation.Lines.Count == 0 || operation.DepositCandidates.Count == 0)
            throw new InvalidOperationException("Deposit plan has no executable reviewed authorization.");
        repository.Mutate(state =>
        {
            if (state.Operations.Any(candidate => candidate.OperationId == operation.OperationId))
                throw new InvalidOperationException($"Operation ID '{operation.OperationId}' is already in use.");
            if (!string.IsNullOrWhiteSpace(canonicalHash))
            {
                if (state.Requests.Any(candidate => candidate.RequestId == operation.RequestId))
                    throw new InvalidOperationException($"Request ID '{operation.RequestId}' is already in use.");
                state.Requests.Add(new SubmittedRequestRecord
                {
                    RequestId = operation.RequestId,
                    OperationId = operation.OperationId,
                    CanonicalHash = canonicalHash,
                    AcceptedAtUtc = now,
                });
            }
            state.Operations.Add(operation);
            AddReceipt(state, operation, "DepositAuthorizationPersisted", operation.Message);
        });
        var copy = Copy(operation);
        OperationChanged?.Invoke(copy);
        return copy;
    }

    public OperationRecord CreateDeposit(
        OwnerScope owner,
        StowageDepositBatch batch,
        string kind = OperationKinds.QuickDeposit) =>
        CreateDeposit(owner, batch, kind, null);

    public OperationRecord CreateTransferDeposit(
        OwnerScope owner,
        StowagePlan plan,
        StowageDepositBatch batch) =>
        CreateDeposit(owner, batch, OperationKinds.StowageSurplus, plan);

    private OperationRecord CreateDeposit(
        OwnerScope owner,
        StowageDepositBatch batch,
        string kind,
        StowagePlan? sourcePlan)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Deposit operations require stable owner identity.");
        if (kind is not (OperationKinds.QuickDeposit or OperationKinds.StowageSurplus))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var now = utcNow();
        var routes = batch.Routes.Where(route => route.RoutedQuantity > 0).ToArray();
        var operation = new OperationRecord
        {
            OperationId = $"rq-{Guid.NewGuid():N}",
            Kind = kind,
            Owner = owner with { },
            Status = OperationStatuses.Accepted,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Message = "Reviewed stowage authorization is ready for explicit execution.",
            SourcePlanId = sourcePlan?.Id,
            SourcePlanRevision = sourcePlan?.Revision,
            SourcePlanName = sourcePlan?.Name,
            Lines = routes
                .GroupBy(route => (route.Request.ItemId, route.Request.IsHighQuality))
                .Select(group => new OperationLine
                {
                    SourcePlanId = group.Select(route => route.Request.SourcePlanId).Distinct().Count() == 1
                        ? group.First().Request.SourcePlanId
                        : null,
                    SourceRuleId = group.Select(route => route.Request.SourceRuleId).Distinct().Count() == 1
                        ? group.First().Request.SourceRuleId
                        : null,
                    ItemId = group.Key.ItemId,
                    ItemName = group.Select(route => route.Request.ItemName).First(name => !string.IsNullOrWhiteSpace(name)),
                    IsHighQuality = group.Key.IsHighQuality,
                    Quality = group.Key.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly,
                    TargetQuantity = group.Sum(route => route.RoutedQuantity),
                    ShortageQuantity = group.Sum(route => route.RoutedQuantity),
                })
                .ToList(),
            DepositCandidates = routes
                .SelectMany(route => route.Allocations.Select(allocation => new
                {
                    Route = route,
                    Allocation = allocation,
                }))
                .GroupBy(entry => entry.Allocation.RetainerId)
                .Select(group => new DepositCandidateAuthorization
                {
                    RetainerId = group.Key,
                    RetainerName = group.First().Allocation.RetainerName,
                    ObservedAtUtc = group.First().Allocation.ObservedAtUtc,
                    CapacityByVariant = group
                        .GroupBy(entry => VariantKey(entry.Route.Request.ItemId, entry.Route.Request.IsHighQuality))
                        .ToDictionary(
                            entries => entries.Key,
                            entries => entries.Sum(entry => entry.Allocation.Quantity)),
                })
                .ToList(),
        };
        operation.RequestId = operation.OperationId;
        if (operation.Lines.Count == 0 || operation.DepositCandidates.Count == 0)
            throw new InvalidOperationException("Stowage batch has no executable reviewed authorization.");

        repository.Mutate(state =>
        {
            state.Operations.Add(operation);
            AddReceipt(state, operation, "StowageAuthorizationPersisted", operation.Message);
        });
        var copy = Copy(operation);
        OperationChanged?.Invoke(copy);
        return copy;
    }

    public void ArmCacheInvalidation(string operationId, ulong retainerId, OwnerScope owner) => repository.Mutate(state =>
    {
        if (!state.PendingCacheInvalidations.Any(entry => entry.OperationId == operationId && entry.RetainerId == retainerId))
            state.PendingCacheInvalidations.Add(new PendingCacheInvalidation { OperationId = operationId, RetainerId = retainerId, Owner = owner with { } });
    });

    public void ResolveCacheInvalidation(string operationId, ulong retainerId) => repository.Mutate(state =>
        state.PendingCacheInvalidations.RemoveAll(entry => entry.OperationId == operationId && entry.RetainerId == retainerId));

    public IReadOnlyList<PendingCacheInvalidation> PendingCacheInvalidations() => repository.Read(state =>
        state.PendingCacheInvalidations.Select(entry => new PendingCacheInvalidation
        {
            OperationId = entry.OperationId,
            RetainerId = entry.RetainerId,
            Owner = entry.Owner with { },
        }).ToArray());

    public OperationRecord Transition(string operationId, string status, string code, string message)
    {
        OperationRecord changed = null!;
        repository.Mutate(state =>
        {
            var operation = state.Operations.SingleOrDefault(candidate => candidate.OperationId == operationId)
                ?? throw new KeyNotFoundException($"Operation '{operationId}' was not found.");
            ValidateTransition(operation.Status, status);
            operation.Status = status;
            operation.Revision = checked(operation.Revision + 1);
            operation.UpdatedAtUtc = utcNow();
            operation.Message = message;
            AddReceipt(state, operation, code, message);
            changed = Copy(operation);
        });
        OperationChanged?.Invoke(changed);
        return changed;
    }

    public void RecordTransfer(string operationId, uint itemId, ulong retainerId, int quantity, string code, string message)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        OperationRecord changed = null!;
        repository.Mutate(state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == operationId);
            if (operation.Status != OperationStatuses.Running)
                throw new InvalidOperationException("Transfers can only be recorded for a running operation.");
            var line = operation.Lines.Single(candidate => candidate.ItemId == itemId);
            if (line.TransferredQuantity > line.TargetQuantity - quantity)
                throw new InvalidOperationException("Verified transfer exceeds persisted operation authorization.");
            line.TransferredQuantity = checked(line.TransferredQuantity + quantity);
            operation.Revision = checked(operation.Revision + 1);
            operation.UpdatedAtUtc = utcNow();
            state.Receipts.Add(new OperationReceipt
            {
                OperationId = operationId,
                Revision = operation.Revision,
                OccurredAtUtc = operation.UpdatedAtUtc,
                Status = operation.Status,
                Code = code,
                Message = message,
                ItemId = itemId,
                RetainerId = retainerId,
                Quantity = quantity,
            });
            changed = Copy(operation);
        });
        OperationChanged?.Invoke(changed);
    }

    public void RecordDepositTransfer(
        string operationId,
        uint itemId,
        bool isHighQuality,
        ulong retainerId,
        int quantity,
        string code,
        string message)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        OperationRecord changed = null!;
        repository.Mutate(state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == operationId);
            if (operation.Status != OperationStatuses.Running)
                throw new InvalidOperationException("Transfers can only be recorded for a running operation.");
            var line = operation.Lines.Single(candidate =>
                candidate.ItemId == itemId && candidate.IsHighQuality == isHighQuality);
            if (line.TransferredQuantity > line.TargetQuantity - quantity)
                throw new InvalidOperationException("Verified transfer exceeds persisted operation authorization.");
            line.TransferredQuantity = checked(line.TransferredQuantity + quantity);
            operation.Revision = checked(operation.Revision + 1);
            operation.UpdatedAtUtc = utcNow();
            state.Receipts.Add(new OperationReceipt
            {
                OperationId = operationId,
                Revision = operation.Revision,
                OccurredAtUtc = operation.UpdatedAtUtc,
                Status = operation.Status,
                Code = code,
                Message = message,
                ItemId = itemId,
                RetainerId = retainerId,
                Quantity = quantity,
            });
            changed = Copy(operation);
        });
        OperationChanged?.Invoke(changed);
    }

    public void RecordRetrievalTransfer(
        string operationId,
        uint itemId,
        bool isHighQuality,
        ulong retainerId,
        int quantity,
        string code,
        string message)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        OperationRecord changed = null!;
        repository.Mutate(state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == operationId);
            if (operation.Status != OperationStatuses.Running)
                throw new InvalidOperationException("Transfers can only be recorded for a running operation.");
            var line = operation.Lines
                .Where(candidate => candidate.ItemId == itemId && QualityMatches(candidate.Quality, isHighQuality))
                .OrderBy(candidate => candidate.Quality == ItemQualityPolicy.Any ? 1 : 0)
                .FirstOrDefault(candidate => candidate.TransferredQuantity < candidate.TargetQuantity)
                ?? throw new InvalidOperationException("No persisted operation line authorizes this item quality.");
            if (line.TransferredQuantity > line.TargetQuantity - quantity)
                throw new InvalidOperationException("Verified transfer exceeds persisted operation authorization.");
            line.TransferredQuantity = checked(line.TransferredQuantity + quantity);
            operation.Revision = checked(operation.Revision + 1);
            operation.UpdatedAtUtc = utcNow();
            state.Receipts.Add(new OperationReceipt
            {
                OperationId = operationId,
                Revision = operation.Revision,
                OccurredAtUtc = operation.UpdatedAtUtc,
                Status = operation.Status,
                Code = code,
                Message = message,
                ItemId = itemId,
                RetainerId = retainerId,
                Quantity = quantity,
            });
            changed = Copy(operation);
        });
        OperationChanged?.Invoke(changed);
    }

    public void RecordWarning(string operationId, string code, string message)
    {
        OperationRecord changed = null!;
        repository.Mutate(state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == operationId);
            if (OperationStatuses.IsTerminal(operation.Status))
                throw new InvalidOperationException("Warnings cannot be appended to a terminal operation.");
            operation.Revision = checked(operation.Revision + 1);
            operation.UpdatedAtUtc = utcNow();
            state.Receipts.Add(new OperationReceipt
            {
                OperationId = operationId,
                Revision = operation.Revision,
                OccurredAtUtc = operation.UpdatedAtUtc,
                Status = operation.Status,
                Code = code,
                Message = message,
            });
            changed = Copy(operation);
        });
        OperationChanged?.Invoke(changed);
    }

    private static void ValidateTransition(string current, string next)
    {
        if (current == next)
            throw new InvalidOperationException($"Operation is already '{next}'.");
        if (OperationStatuses.IsTerminal(current))
            throw new InvalidOperationException($"Terminal operation '{current}' cannot transition to '{next}'.");
        if (current == OperationStatuses.Accepted && next != OperationStatuses.Running && next != OperationStatuses.Cancelled && next != OperationStatuses.Failed)
            throw new InvalidOperationException($"Invalid operation transition '{current}' -> '{next}'.");
        if (current == OperationStatuses.Running && next is not (OperationStatuses.Succeeded or OperationStatuses.PartiallySucceeded or OperationStatuses.Indeterminate or OperationStatuses.Failed or OperationStatuses.Cancelled))
            throw new InvalidOperationException($"Invalid operation transition '{current}' -> '{next}'.");
    }

    private static void AddReceipt(QuartermasterState state, OperationRecord operation, string code, string message) => state.Receipts.Add(new OperationReceipt
    {
        OperationId = operation.OperationId,
        Revision = operation.Revision,
        OccurredAtUtc = operation.UpdatedAtUtc,
        Status = operation.Status,
        Code = code,
        Message = message,
    });

    private static OperationRecord Copy(OperationRecord operation) => new()
    {
        OperationId = operation.OperationId,
        RequestId = operation.RequestId,
        Kind = operation.Kind,
        ExecuteImmediately = operation.ExecuteImmediately,
        Owner = operation.Owner with { },
        Status = operation.Status,
        Revision = operation.Revision,
        CreatedAtUtc = operation.CreatedAtUtc,
        UpdatedAtUtc = operation.UpdatedAtUtc,
        Message = operation.Message,
        SourcePlanId = operation.SourcePlanId,
        SourcePlanRevision = operation.SourcePlanRevision,
        SourcePlanName = operation.SourcePlanName,
        SourcePlanItems = operation.SourcePlanItems.Select(Copy).ToList(),
        DepositCandidates = operation.DepositCandidates.Select(candidate => new DepositCandidateAuthorization
        {
            RetainerId = candidate.RetainerId,
            RetainerName = candidate.RetainerName,
            ObservedAtUtc = candidate.ObservedAtUtc,
            CapacityByItem = candidate.CapacityByItem.ToDictionary(entry => entry.Key, entry => entry.Value),
            CapacityByVariant = candidate.CapacityByVariant.ToDictionary(entry => entry.Key, entry => entry.Value),
        }).ToList(),
        Lines = operation.Lines.Select(line => new OperationLine
        {
            SourcePlanId = line.SourcePlanId,
            SourceRuleId = line.SourceRuleId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            IsHighQuality = line.IsHighQuality,
            Quality = line.Quality,
            TargetQuantity = line.TargetQuantity,
            ShortageQuantity = line.ShortageQuantity,
            TransferredQuantity = line.TransferredQuantity,
        }).ToList(),
    };

    private static TargetPlanItem Copy(TargetPlanItem item) => new()
    {
        Id = item.Id,
        StowagePlanId = item.StowagePlanId,
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        TargetQuantity = item.TargetQuantity,
        Quality = item.Quality,
        Routing = new StowageRoutingPolicy
        {
            Mode = item.Routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
            Overflow = item.Routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
            PreferredRetainerIds = item.Routing?.PreferredRetainerIds.ToList() ?? [],
        },
        Notes = item.Notes,
        Enabled = item.Enabled,
    };

    public static string VariantKey(uint itemId, bool isHighQuality) =>
        $"{itemId}:{(isHighQuality ? "hq" : "nq")}";

    private static bool QualityMatches(ItemQualityPolicy quality, bool isHighQuality) => quality switch
    {
        ItemQualityPolicy.NqOnly => !isHighQuality,
        ItemQualityPolicy.HqOnly => isHighQuality,
        _ => true,
    };
}
