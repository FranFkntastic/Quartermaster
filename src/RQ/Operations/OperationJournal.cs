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

    public IReadOnlyList<OperationRecord> History(OwnerScope owner, int limit) => repository.Read(state => state.Operations
        .Where(operation => operation.Owner.Matches(owner))
        .OrderByDescending(operation => operation.UpdatedAtUtc)
        .ThenByDescending(operation => operation.CreatedAtUtc)
        .Take(Math.Max(0, limit))
        .Select(Copy)
        .ToArray());

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

    // ---- Requester cancellation and unstarted expiry ------------------------------------------
    // Stop requests for running operations live in memory only: a reload already settles every running
    // operation as indeterminate, and accepted operations are cancelled durably below.
    private readonly object stopGate = new();
    private readonly HashSet<string> stopRequests = new(StringComparer.Ordinal);

    /// <summary>True when the requester asked a running operation to stop after its current retainer.</summary>
    public bool IsStopRequested(string operationId)
    {
        lock (stopGate)
            return stopRequests.Contains(operationId);
    }

    /// <summary>
    /// Cancels the operation an external request id created: an accepted operation is cancelled durably; a
    /// running one finishes the retainer it is on and stops. Null when the request id is unknown.
    /// </summary>
    public (string Outcome, OperationRecord Operation)? CancelRequested(string requestId, string message)
    {
        var operation = repository.Read(state =>
            state.Requests.FirstOrDefault(record => record.RequestId == requestId) is { } request &&
            state.Operations.FirstOrDefault(candidate => candidate.OperationId == request.OperationId) is { } found
                ? Copy(found)
                : null);
        if (operation is null)
            return null;
        if (operation.Status == OperationStatuses.Accepted)
        {
            try
            {
                return ("cancelled", Transition(operation.OperationId, OperationStatuses.Cancelled, "CancelledByRequester", message));
            }
            catch (InvalidOperationException)
            {
                // Started between the read and the transition: fall through to the running case.
                operation = Get(operation.OperationId) ?? operation;
            }
        }
        if (operation.Status == OperationStatuses.Running)
        {
            lock (stopGate)
                stopRequests.Add(operation.OperationId);
            return ("stopping", operation);
        }
        return ("already_terminal", operation);
    }

    /// <summary>
    /// Cancels externally requested delivered-stow operations that have not started within
    /// <paramref name="startWindow"/> of submission, so a stale stow never deposits later (for example on the
    /// owner's next login). Requested retrievals and elemental deposits keep waiting as before; their
    /// requesters withdraw them through the cancel channel. <paramref name="activeOperationId"/> is skipped.
    /// </summary>
    public IReadOnlyList<OperationRecord> ExpireUnstartedRequestedOperations(TimeSpan startWindow, string? activeOperationId)
    {
        var cutoff = utcNow() - startWindow;
        var expired = repository.Read(state => state.Operations
            .Where(operation => operation.ExecuteImmediately &&
                                operation.Kind == OperationKinds.StowageSurplus &&
                                operation.Status == OperationStatuses.Accepted &&
                                operation.OperationId != activeOperationId &&
                                state.Requests.FirstOrDefault(record => record.OperationId == operation.OperationId) is { } request &&
                                (request.AcceptedAtUtc == default ? operation.CreatedAtUtc : request.AcceptedAtUtc) < cutoff)
            .Select(operation => operation.OperationId)
            .ToArray());
        var cancelled = new List<OperationRecord>();
        foreach (var operationId in expired)
        {
            try
            {
                cancelled.Add(Transition(operationId, OperationStatuses.Cancelled, "ExpiredBeforeStart",
                    $"Not started within {startWindow.TotalMinutes:N0} minutes of submission; cancelled instead of running later."));
            }
            catch (InvalidOperationException)
            {
                // Started or settled concurrently; nothing to expire.
            }
        }
        return cancelled;
    }

    public IReadOnlyList<OperationRecord> ReconcileInterruptedOperations()
    {
        if (!repository.Read(state => state.Operations.Any(operation => operation.Status == OperationStatuses.Running)))
            return [];
        var reconciled = new List<OperationRecord>();
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            foreach (var operation in state.Operations.Where(operation => operation.Status == OperationStatuses.Running))
            {
                operation.Status = OperationStatuses.Indeterminate;
                operation.Revision = checked(operation.Revision + 1);
                operation.UpdatedAtUtc = utcNow();
                operation.Message = "Quartermaster reloaded before live transfer completion could be verified; pending mutation evidence was reconciled.";
                if (operation.Kind == OperationKinds.Placement)
                {
                    var last = state.Receipts.LastOrDefault(r => r.OperationId == operation.OperationId);
                    operation.Message = $"Placement interrupted; no move will be replayed. Last recorded step: {last?.Message}";
                }
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

    internal OperationRecord CreatePlacement(OwnerScope owner, StowagePlan plan, IReadOnlyList<TargetPlanItem> rules) =>
        CreateManual(owner, rules, OperationKinds.Placement, plan.Id, plan.Revision, plan.Name);

    private OperationRecord CreateManual(
        OwnerScope owner,
        IReadOnlyList<TargetPlanItem> plan,
        string kind,
        Guid? sourcePlanId,
        long? sourcePlanRevision,
        string? sourcePlanName)
    {
        if (kind is not (OperationKinds.Retrieval or OperationKinds.Deposit or OperationKinds.Placement))
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
        repository.Mutate(StateChangeKind.Operations, state =>
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
        repository.Mutate(StateChangeKind.Operations, state =>
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

    /// <summary>
    /// An externally requested stowage deposit (delivered-stow IPC): the request id is persisted with its
    /// canonical hash in the same mutation as the operation, so a replay can never create a second deposit.
    /// </summary>
    public OperationRecord CreateRequestedDeposit(
        OwnerScope owner,
        StowageDepositBatch batch,
        string requestId,
        string operationId,
        string canonicalHash) =>
        CreateDeposit(owner, batch, OperationKinds.StowageSurplus, null, (requestId, operationId, canonicalHash));

    private OperationRecord CreateDeposit(
        OwnerScope owner,
        StowageDepositBatch batch,
        string kind,
        StowagePlan? sourcePlan,
        (string RequestId, string OperationId, string CanonicalHash)? requested = null)
    {
        if (!owner.HasStableIdentity)
            throw new InvalidOperationException("Deposit operations require stable owner identity.");
        if (kind is not (OperationKinds.QuickDeposit or OperationKinds.StowageSurplus))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var now = utcNow();
        var routes = batch.Routes.Where(route => route.RoutedQuantity > 0).ToArray();
        var indexedRoutes = routes
            .Select((route, index) => new { Route = route, AuthorizationKey = $"route:{index}" })
            .ToArray();
        var operation = new OperationRecord
        {
            OperationId = requested?.OperationId ?? $"rq-{Guid.NewGuid():N}",
            Kind = kind,
            ExecuteImmediately = requested is not null,
            Owner = owner with { },
            Status = OperationStatuses.Accepted,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Message = requested is null
                ? "Reviewed stowage authorization is ready for explicit execution."
                : "Delivered-stow authorization is ready for automatic execution.",
            SourcePlanId = sourcePlan?.Id,
            SourcePlanRevision = sourcePlan?.Revision,
            SourcePlanName = sourcePlan?.Name,
            Lines = indexedRoutes
                .Select(entry => new OperationLine
                {
                    AuthorizationKey = entry.AuthorizationKey,
                    SourcePlanId = entry.Route.Request.SourcePlanId,
                    SourceRuleId = entry.Route.Request.SourceRuleId,
                    ItemId = entry.Route.Request.ItemId,
                    ItemName = entry.Route.Request.ItemName,
                    IsHighQuality = entry.Route.Request.IsHighQuality,
                    Quality = entry.Route.Request.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly,
                    TargetQuantity = entry.Route.RoutedQuantity,
                    ShortageQuantity = entry.Route.RoutedQuantity,
                    MaxStackSize = entry.Route.Request.MaxStackSize,
                    Routing = CopyRouting(entry.Route.Request.Routing),
                })
                .ToList(),
            DepositCandidates = indexedRoutes
                .SelectMany(entry => entry.Route.Candidates.Count != 0
                    ? entry.Route.Candidates.Select((candidate, priority) => new
                    {
                        entry.AuthorizationKey,
                        Candidate = candidate,
                        Priority = priority,
                        AuthorizedCapacity = candidate.TotalCapacity,
                        UsesLiveCapacity = true,
                    })
                    : entry.Route.Allocations.Select((allocation, priority) => new
                    {
                        entry.AuthorizationKey,
                        Candidate = new RetainerStowageCapacity(
                            allocation.RetainerId,
                            allocation.RetainerName,
                            allocation.ObservedAtUtc,
                            allocation.Capacity,
                            0),
                        Priority = priority,
                        AuthorizedCapacity = allocation.Quantity,
                        UsesLiveCapacity = false,
                    }))
                .GroupBy(entry => entry.Candidate.RetainerId)
                .Select(group => new DepositCandidateAuthorization
                {
                    RetainerId = group.Key,
                    RetainerName = group.First().Candidate.RetainerName,
                    ObservedAtUtc = group.First().Candidate.ObservedAtUtc,
                    CapacityByVariant = group
                        .GroupBy(entry => entry.AuthorizationKey)
                        .ToDictionary(
                            entries => entries.Key,
                            entries => entries.Max(entry => entry.AuthorizedCapacity)),
                    PriorityByVariant = group
                        .GroupBy(entry => entry.AuthorizationKey)
                        .ToDictionary(
                            entries => entries.Key,
                            entries => entries.Min(entry => entry.Priority)),
                    UsesLiveCapacity = group.Any(entry => entry.UsesLiveCapacity),
                })
                .ToList(),
        };
        operation.RequestId = requested?.RequestId ?? operation.OperationId;
        if (operation.Lines.Count == 0 || operation.DepositCandidates.Count == 0)
            throw new InvalidOperationException("Stowage batch has no executable reviewed authorization.");

        repository.Mutate(StateChangeKind.Operations, state =>
        {
            if (requested is { } ids)
            {
                if (state.Operations.Any(candidate => candidate.OperationId == operation.OperationId))
                    throw new InvalidOperationException($"Operation ID '{operation.OperationId}' is already in use.");
                if (state.Requests.Any(candidate => candidate.RequestId == ids.RequestId))
                    throw new InvalidOperationException($"Request ID '{ids.RequestId}' is already in use.");
                state.Requests.Add(new SubmittedRequestRecord
                {
                    RequestId = ids.RequestId,
                    OperationId = operation.OperationId,
                    CanonicalHash = ids.CanonicalHash,
                    AcceptedAtUtc = now,
                });
            }
            state.Operations.Add(operation);
            AddReceipt(state, operation, "StowageAuthorizationPersisted", operation.Message);
        });
        var copy = Copy(operation);
        OperationChanged?.Invoke(copy);
        return copy;
    }

    public void ArmCacheInvalidation(string operationId, ulong retainerId, OwnerScope owner) => repository.Mutate(StateChangeKind.Operations, state =>
    {
        if (!state.PendingCacheInvalidations.Any(entry => entry.OperationId == operationId && entry.RetainerId == retainerId))
            state.PendingCacheInvalidations.Add(new PendingCacheInvalidation { OperationId = operationId, RetainerId = retainerId, Owner = owner with { } });
    });

    public void ResolveCacheInvalidation(string operationId, ulong retainerId) => repository.Mutate(StateChangeKind.Operations, state =>
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
        repository.Mutate(StateChangeKind.Operations, state =>
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
        if (OperationStatuses.IsTerminal(status))
            lock (stopGate)
                stopRequests.Remove(operationId);
        OperationChanged?.Invoke(changed);
        return changed;
    }

    public void RecordTransfer(string operationId, uint itemId, ulong retainerId, int quantity, string code, string message)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        OperationRecord changed = null!;
        repository.Mutate(StateChangeKind.Operations, state =>
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
        string message) =>
        RecordDepositTransferCore(operationId, null, itemId, isHighQuality, retainerId, quantity, code, message);

    public void RecordDepositTransfer(
        string operationId,
        string authorizationKey,
        uint itemId,
        bool isHighQuality,
        ulong retainerId,
        int quantity,
        string code,
        string message) =>
        RecordDepositTransferCore(operationId, authorizationKey, itemId, isHighQuality, retainerId, quantity, code, message);

    private void RecordDepositTransferCore(
        string operationId,
        string? authorizationKey,
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
        repository.Mutate(StateChangeKind.Operations, state =>
        {
            var operation = state.Operations.Single(candidate => candidate.OperationId == operationId);
            if (operation.Status != OperationStatuses.Running)
                throw new InvalidOperationException("Transfers can only be recorded for a running operation.");
            var line = operation.Lines.Single(candidate =>
                candidate.ItemId == itemId &&
                candidate.IsHighQuality == isHighQuality &&
                (authorizationKey is null || AuthorizationKey(candidate) == authorizationKey));
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
                AuthorizationKey = authorizationKey,
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
        string message) =>
        RecordRetrievalTransferCore(operationId, itemId, isHighQuality, retainerId, quantity, code, message);

    /// <summary>
    /// Records a verified saddlebag-to-bag retrieval against the same persisted
    /// retrieval authorization as retainer movement. The receipt has no retainer.
    /// </summary>
    public void RecordSaddlebagRetrieval(
        string operationId,
        uint itemId,
        bool isHighQuality,
        int quantity,
        string code,
        string message) =>
        RecordRetrievalTransferCore(operationId, itemId, isHighQuality, null, quantity, code, message);

    private void RecordRetrievalTransferCore(
        string operationId,
        uint itemId,
        bool isHighQuality,
        ulong? retainerId,
        int quantity,
        string code,
        string message)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        OperationRecord changed = null!;
        repository.Mutate(StateChangeKind.Operations, state =>
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
        repository.Mutate(StateChangeKind.Operations, state =>
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
            PriorityByVariant = candidate.PriorityByVariant.ToDictionary(entry => entry.Key, entry => entry.Value),
            UsesLiveCapacity = candidate.UsesLiveCapacity,
        }).ToList(),
        Lines = operation.Lines.Select(line => new OperationLine
        {
            AuthorizationKey = line.AuthorizationKey,
            SourcePlanId = line.SourcePlanId,
            SourceRuleId = line.SourceRuleId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            IsHighQuality = line.IsHighQuality,
            Quality = line.Quality,
            TargetQuantity = line.TargetQuantity,
            ShortageQuantity = line.ShortageQuantity,
            TransferredQuantity = line.TransferredQuantity,
            MaxStackSize = line.MaxStackSize,
            Routing = CopyRouting(line.Routing),
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
        Routing = StowageRoutingPolicy.CopyOf(item.Routing),
        Notes = item.Notes,
        Enabled = item.Enabled,
    };

    private static StowageRoutingPolicy CopyRouting(StowageRoutingPolicy? routing) => StowageRoutingPolicy.CopyOf(routing);

    public static string VariantKey(uint itemId, bool isHighQuality) =>
        $"{itemId}:{(isHighQuality ? "hq" : "nq")}";

    public static string AuthorizationKey(OperationLine line) =>
        string.IsNullOrWhiteSpace(line.AuthorizationKey)
            ? VariantKey(line.ItemId, line.IsHighQuality)
            : line.AuthorizationKey;

    private static bool QualityMatches(ItemQualityPolicy quality, bool isHighQuality) => quality switch
    {
        ItemQualityPolicy.NqOnly => !isHighQuality,
        ItemQualityPolicy.HqOnly => isHighQuality,
        _ => true,
    };
}
