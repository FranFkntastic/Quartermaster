using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using RQ.Planning;
using InventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;

namespace RQ.Automation;

/// <summary>Thin adapter: only the pinned Franthropy session performs game actions.</summary>
internal sealed class MarketRunLiveDriver(
    IRetainerAutomationSession session, RetainerLiveDriver transferDriver,
    OperationJournal journal, RetainerCacheRepository cache, PlayerInventoryCacheRepository playerCache,
    AutomationLease automation, Func<OwnerScope> currentOwner, Func<uint, string> itemName,
    Action<string>? warning = null, ISaddlebagRetrievalDriver? saddlebag = null,
    Func<IReadOnlyDictionary<uint, int>>? cachedSaddlebag = null, Action<string>? information = null,
    Func<uint, int>? maxStackSize = null) : IMarketRunDriver
{
    private readonly Action<string>? logWarning = warning;
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private readonly MarketRunDeadline deadline = new(TimeSpan.FromSeconds(15));
    private readonly MarketRunDeadline routeDeadline = new(MarketRunDeadline.RetainerListTimeout);
    private ulong retainerId;
    private string retainerName = string.Empty;
    private OwnerScope owner = new();
    private IDisposable? gatherLease;
    private Func<bool> runAuthority = () => false;
    public int GatherInventoryBlockedUnits { get; private set; }
    private bool OwnsGatherLease() => gatherLease is not null && automation.Owns(gatherLease);
    private int StackSize(uint id) => Math.Max(1, (maxStackSize ?? throw new InvalidOperationException("Item stack-size authority unavailable."))(id));
    private int RelocationCapacity(StowageDepositRequest request) =>
        StowageRouter.Route(request, cache.Snapshot(), owner, StackSize(request.ItemId)).RoutedQuantity;
    private bool HasSessionAuthority() => runAuthority() && OwnsGatherLease() &&
        owner.HasStableIdentity && currentOwner() is { HasStableIdentity: true } current && owner.Matches(current);
    private void RequireSessionAuthority()
    {
        if (!HasSessionAuthority()) throw new InvalidOperationException("Market run session authority lost.");
    }
    public string ItemName(uint id) => itemName(id);
    public void BeginRun(IDisposable lease, Func<bool> hasAuthority)
    {
        gatherLease = lease;
        runAuthority = hasAuthority;
        owner = currentOwner();
        RequireSessionAuthority();
    }

    public async Task RequireRetainerListAsync(CancellationToken token)
    {
        RequireSessionAuthority();
        await transferDriver.RequireRetainerListAsync(token).ConfigureAwait(false);
    }
    /// <summary>
    /// Judges each listing retainer by ID against the manager-owned roster read the refresh path
    /// already uses (no UI, nothing moves), plus the cache's retainer-list accessibility. When the
    /// live read is not ready it falls back to the cached roster; with neither, every target fails.
    /// </summary>
    public async Task<IReadOnlyList<MarketRetainerUnavailable>> UnavailableListingRetainersAsync(IReadOnlyCollection<ulong> retainerIds, CancellationToken token)
    {
        RequireSessionAuthority();
        var roster = await session.ScanRetainerRosterAsync(token).ConfigureAwait(false);
        RequireSessionAuthority();
        var live = roster is { Success: true, IsComplete: true } ? roster.Retainers.ToDictionary(r => r.RetainerId) : null;
        var cached = cache.Snapshot().Values.Where(r => r.Owner.Matches(owner)).ToDictionary(r => r.RetainerId);
        var rosterObservedAt = cached.Values.Max(r => r.RosterObservedAtUtc);
        var basis = live is not null ? "live roster" : $"cached roster from {rosterObservedAt:u} (live read {roster.Code}: {roster.Message})";
        var unavailable = new List<MarketRetainerUnavailable>();
        foreach (var id in retainerIds)
        {
            var entry = cached.GetValueOrDefault(id);
            var rosterName = live?.GetValueOrDefault(id)?.RetainerName ?? entry?.RetainerName;
            var named = string.IsNullOrWhiteSpace(rosterName) ? string.Empty : $"; roster name {rosterName}";
            (string Code, string Detail)? failure =
                live is null && rosterObservedAt is null ? ("RosterUnavailable", $"the live roster read failed ({roster.Code}: {roster.Message}) and no roster is cached for this character") :
                live is not null ? !live.ContainsKey(id) ? ("NotInRoster", $"not in this character's assigned retainer roster ({basis})") : null :
                entry is null ? ("NotInRoster", $"not in this character's assigned retainer roster ({basis})") :
                entry.IsCurrentlyAssigned == false ? ("NotAssigned", $"no longer assigned to this character ({basis})") : null;
            // Membership plus a recorded retainer-list refusal only: raw availability metadata cannot
            // prove an assigned retainer inaccessible (same rule as RetainerRefreshCoordinator).
            failure ??= entry?.IsUiAccessible == false
                ? ("NotInRetainerList", $"last seen missing from the retainer list at {entry.UiAccessibilityObservedAtUtc:u}{named}")
                : null;
            if (failure is { } f) unavailable.Add(new(id, f.Code, f.Detail));
        }
        return unavailable;
    }

    public async Task OpenAsync(ulong id, string name, CancellationToken token)
    {
        RequireSessionAuthority();
        Require(await session.OpenRetainerAsync(new(id, name), token).ConfigureAwait(false));
        RequireSessionAuthority();
        retainerId = id; retainerName = name;
        Require(await session.OpenSellingListAsync(token).ConfigureAwait(false));
    }
    public async Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken token)
    {
        RequireSessionAuthority();
        var scan = await session.ScanMarketListingsAsync(token).ConfigureAwait(false);
        RequireSessionAuthority();
        if (!scan.Success) throw new InvalidOperationException($"{scan.Code}: {scan.Message}");
        cache.ReplaceListings(new(retainerId, retainerName, owner, DateTime.UtcNow, scan.Listings.Select(l => new CachedMarketListing
        {
            ItemId = l.ItemId,
            ItemName = itemName(l.ItemId),
            Quantity = (uint)l.Quantity,
            IsHq = l.IsHq,
            UnitPrice = l.UnitPrice,
            SlotIndex = l.SlotIndex,
            ContainerKey = InventoryType.RetainerMarket.ToString(),
        }).ToArray()));
        return scan.Listings;
    }
    public Task<MarketPlayerInventory> PlayerAsync(CancellationToken token) => ReadPlayerAsync(token, true);
    private async Task<MarketPlayerInventory> ReadPlayerAsync(CancellationToken token, bool complete)
    {
        RequireSessionAuthority();
        var stacks = await session.ScanPlayerInventoryAsync(null, token).ConfigureAwait(false);
        RequireSessionAuthority();
        var names = Bags.Select(b => b.ToString()).ToArray();
        playerCache.Observe(owner, new(Bags.Select(b => new InventoryBag
        {
            BagName = b.ToString(),
            Items = stacks.Where(s => s.Container == b).Select(s => new InventoryItem
            {
                ItemId = s.ItemId,
                ItemName = itemName(s.ItemId),
                Quantity = (uint)s.Quantity,
                IsHq = s.IsHighQuality,
                ContainerKey = b.ToString(),
                SlotIndex = s.SlotIndex
            }).ToList(),
        }).ToArray(), names, names), DateTime.UtcNow, coverageUncertain: !complete);
        return new(stacks, Math.Max(0, 140 - stacks.Select(s => (s.Container, s.SlotIndex)).Distinct().Count()));
    }
    public async Task<MarketMutationResult> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken token)
    {
        RetainerAutomationResult result;
        try
        {
            RequireSessionAuthority();
            result = await session.UpdateSellingListingPriceAsync(listing, price, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not RetainerMarketMutationIndeterminateException and not OperationCanceledException)
        {
            // Franthropy catches every exception once the price request may have been sent and
            // returns RetainerMarketPriceUpdateIndeterminate, so an exception escaping it was raised
            // before dispatch. Cancellation keeps its own classification in the executor.
            return new(MarketMutationOutcome.FailedBeforeSend, "RepriceFaultedBeforeSend", e.Message);
        }
        return new(ClassifyReprice(result), result.Code, result.Message);
    }

    /// <summary>
    /// FailedBeforeSend codes are returned before the Adjust Price callback fires (policy checks,
    /// list, row selection, recovery) or, for NativeCapabilityUnavailable, before the editor's
    /// confirm click. RetainerRepriceRecoveryExhausted means pre-send recovery ran out of attempts
    /// or wall-clock budget; RetainerRepriceEditorAbandonedUnsent means the editor was closed
    /// unconfirmed and the listing was proven unchanged. Fail closed:
    /// RetainerSellingListingSelectionIndeterminate (nothing-sent could not be proven),
    /// RetainerMarketPriceUpdateIndeterminate (after the confirm click),
    /// UnexpectedRetainerMarketConfirmation (returned both before and after the click) and any
    /// unknown code stay Indeterminate.
    /// </summary>
    internal static MarketMutationOutcome ClassifyReprice(RetainerAutomationResult result) => result.Success
        ? MarketMutationOutcome.Committed : result.Code is "InvalidObservedMarketUnitPrice" or "InvalidMarketUnitPrice" or
            "MarketUnitPriceUnchanged" or "InvalidMarketListing" or "RetainerSellingListingTimeout" or "RetainerSellingListingSelectionFailed" or
            "RetainerRepriceRecoveryExhausted" or "RetainerRepriceEditorAbandonedUnsent" or "RetainerSellingListTimeout" or "RetainerIdentityMismatch" or
            "RetainerMarketUnavailable" or "RetainerMarketListingChanged" or "RetainerMenuUnavailable" or "RetainerCommandUnavailable" or
            "NativeCapabilityUnavailable"
            ? MarketMutationOutcome.FailedBeforeSend : MarketMutationOutcome.Indeterminate;
    public async Task<MarketMutationResult> PullAsync(RetainerMarketListingTarget listing, CancellationToken token)
    {
        RequireSessionAuthority();
        var result = await session.RemoveMarketListingToPlayerInventoryAsync(listing, token).ConfigureAwait(false);
        return new((MarketMutationOutcome)(int)result.Outcome, result.Code, result.Message);
    }
    public async Task<MarketMutationResult> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken token)
    {
        RequireSessionAuthority();
        var result = await session.PostMarketListingAsync(source, quantity, price, token).ConfigureAwait(false);
        return new((MarketMutationOutcome)(int)result.Outcome, result.Code, result.Message);
    }
    public async Task ReturnAsync(CancellationToken token)
    { RequireSessionAuthority(); Require(await session.ReturnToRetainerListAsync(token).ConfigureAwait(false)); }
    public async Task CloseListAsync(CancellationToken token)
    { RequireSessionAuthority(); Require(await session.CloseRetainerListAsync(token).ConfigureAwait(false)); }

    public async Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken token, Action<MarketMutationReceipt>? receiptChanged = null)
    {
        var reports = new List<string>();
        gatherLease = lease;
        runAuthority = hasAuthority;
        GatherInventoryBlockedUnits = 0;
        owner = currentOwner();
        if (!owner.HasStableIdentity) throw new InvalidOperationException("Stable character identity required.");
        // Exact-quality operations are separate because the transfer planner's player
        // count delegate is item keyed. Each delegate below counts only that variant.
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            check();
            var player = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
            var held = player.Stacks.Where(s => s.ItemId == target.ItemId && s.IsHighQuality == (target.Quality == ItemQualityPolicy.HqOnly)).Sum(s => s.Quantity);
            if (held >= target.Units) continue;
            var guarded = new GatherDriver(transferDriver, this, check, target, deadline, routeDeadline, receiptChanged);
            var operation = journal.CreateManual(owner, [new TargetPlanItem { ItemId = target.ItemId, ItemName = target.ItemName,
                Quality = target.Quality, TargetQuantity = target.Units }]);
            var coordinator = new TransferCoordinator(journal, guarded, cache, currentOwner,
                () => new Dictionary<uint, int> { [target.ItemId] = held }, automation, warning: logWarning,
                saddlebagDriver: target.AllowSaddlebag ? saddlebag : null, cachedSaddlebag: cachedSaddlebag, information: information,
                // The same mutation authority GatherDriver.RetrieveAsync enforces: run cancel and
                // plan or owner change refuse every saddlebag dispatch from that point on. It is a
                // predicate, not a token, so an already-dispatched move is still observed to its end.
                callerAuthority: () => { try { check(); return HasSessionAuthority(); } catch { return false; } },
                saddlebagAttempt: attempt => receiptChanged?.Invoke(SaddlebagReceipt(attempt)));
            check();
            await coordinator.ExecuteRetrievalUnderLeaseAsync(operation.OperationId, lease, token).ConfigureAwait(false);
            var outcome = journal.Get(operation.OperationId);
            if (outcome?.Status == OperationStatuses.Indeterminate)
            {
                // Refresh the player projection, not the ambiguous movement receipt.
                // A failed read must not hide the original needs-check or trigger a resend.
                if (!MarketRunDeadline.HasUnsettled && HasSessionAuthority())
                {
                    try { await deadline.RunAsync(t => ReadPlayerAsync(t, false)).ConfigureAwait(false); }
                    catch (Exception exception) { journal.RecordWarning(operation.OperationId, "GatherPlayerObservationRecoveryFailed", exception.Message); }
                }
                throw new MarketGatherIndeterminateException(guarded.CurrentRetainer, guarded.CurrentRetainerName, $"Gather needs a check: {target.ItemName}. {outcome.Message}");
            }
            if (MarketRunDeadline.HasUnsettled) throw new TimeoutException("Gather driver has not settled.");
            check();
            if (guarded.BlockedByInventory)
            {
                GatherInventoryBlockedUnits += guarded.InventoryBlockedUnits;
                reports.Add($"{target.ItemName}: gather blocked by inventory space; {guarded.InventoryBlockedUnits} units not gathered");
                var remainingPlayer = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
                var unattempted = targets.Skip(targetIndex + 1).ToArray();
                GatherInventoryBlockedUnits += unattempted.Sum(next => Math.Max(0, next.Units - remainingPlayer.Stacks
                    .Where(stack => stack.ItemId == next.ItemId && stack.IsHighQuality == (next.Quality == ItemQualityPolicy.HqOnly))
                    .Sum(stack => stack.Quantity)));
                logWarning?.Invoke($"Gather blocked by inventory space; {unattempted.Length} targets left unattempted, {GatherInventoryBlockedUnits} units not gathered.");
                break;
            }
            else if (guarded.SourceChanged) reports.Add($"{target.ItemName}: source changed; {Math.Max(0, target.Units - held - (outcome?.Lines.Sum(l => l.TransferredQuantity) ?? 0))} units not gathered");
            else if (outcome?.Status != OperationStatuses.Succeeded) reports.Add($"{target.ItemName}: gather {outcome?.Message}");
            await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
        }
        return reports;
    }
    internal const string SaddlebagKind = "SaddlebagRetrieval";

    public async Task<IReadOnlyList<string>> StowAsync(Guid planId, IReadOnlyList<ItemPlacementRule> planPlacements,
        IDisposable lease, Action check, Func<bool> hasAuthority, CancellationToken token,
        Action<MarketMutationReceipt> receiptChanged, Action<string> reportChanged,
        bool reorganizeRetainers)
    {
        gatherLease = lease;
        runAuthority = hasAuthority;
        owner = currentOwner();
        check();
        var player = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
        var requests = MarketRunPlanner.StowRequests(planId, planPlacements,
            player.Stacks.Select(s => (s.ItemId, s.IsHighQuality, s.Quantity)), owner);
        var reports = (await DepositRequestsAsync(requests, lease, check, token, receiptChanged, reportChanged, false).ConfigureAwait(false)).ToList();
        if (reorganizeRetainers)
            reports.AddRange(await RelocateAsync(planId, planPlacements, lease, check, token, receiptChanged, reportChanged).ConfigureAwait(false));
        return reports;
    }

    private async Task<IReadOnlyList<string>> DepositRequestsAsync(IReadOnlyList<StowageDepositRequest> requests,
        IDisposable lease, Action check, CancellationToken token, Action<MarketMutationReceipt> receiptChanged,
        Action<string> reportChanged, bool relocation)
    {
        var prefix = relocation ? "MarketRunRelocate" : "MarketRunStow";
        var reports = new List<string>();
        foreach (var request in requests)
        {
            check();
            var destinations = string.Join(", ", request.Routing.PreferredRetainerIds.Select(id =>
                cache.Snapshot().GetValueOrDefault(id)?.RetainerName ?? $"Retainer {id}"));
            // Plan one variant at a time so the preceding deposit's observations feed capacity math. One route per home
            // with exactly its planned units, so a capped home is never filled past its cap.
            var batch = PlacementRouter.LocalBatch([request], cache.Snapshot(), owner,
                maxStackSize ?? throw new InvalidOperationException("Item stack-size authority unavailable."), DateTime.UtcNow);
            var guarded = new StowDriver(transferDriver, this, check, request, receiptChanged, relocation);
            if (batch.PlannedQuantity > 0)
            {
                var operation = journal.CreateDeposit(owner, batch, OperationKinds.StowageSurplus);
                var coordinator = new TransferCoordinator(journal, guarded, cache, currentOwner,
                    () => new Dictionary<uint, int>(), automation, warning: logWarning);
                await coordinator.ExecuteDepositUnderLeaseAsync(operation.OperationId, lease, token).ConfigureAwait(false);
                var outcome = journal.Get(operation.OperationId)!;
                if (outcome.Status == OperationStatuses.Indeterminate)
                {
                    guarded.MarkIndeterminate(outcome.Message);
                    throw new MarketGatherIndeterminateException(guarded.CurrentRetainer, guarded.CurrentRetainerName,
                        $"{prefix}Indeterminate: {request.ItemName} × {request.Quantity} to {destinations}: {outcome.Message}");
                }
                check();
                var moved = outcome.Lines.Sum(l => l.TransferredQuantity);
                var left = request.Quantity - moved;
                if (left > 0)
                {
                    // Only a capacity refusal or a normally settled route proves no room. Other failures
                    // (authority, source change, opening the session) must not masquerade as full storage.
                    var noRoom = outcome.Status is OperationStatuses.Succeeded or OperationStatuses.PartiallySucceeded ||
                        outcome.Message.StartsWith("Stowed ", StringComparison.Ordinal);
                    // A settled route that left a dropped deposit in bags says so instead of claiming no room.
                    var zeroMovement = outcome.Message.Contains(TransferCoordinator.DepositZeroMovementCode, StringComparison.Ordinal);
                    Report(!noRoom ? prefix + "SkippedExecutionFailed" : zeroMovement ? prefix + "LeftInBagsZeroMovement" : prefix + "LeftInBagsNoRoom",
                        left, outcome.Message);
                    if (!noRoom) throw new InvalidOperationException(outcome.Message);
                }
                await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
            }
            else Report(prefix + "LeftInBagsNoRoom", request.Quantity, "No observed capacity at the configured destinations.");

            void Report(string code, int quantity, string reason)
            {
                var message = $"{code}: {request.ItemName} × {quantity} to {destinations}: {reason}";
                reports.Add(message);
                reportChanged(message);
                logWarning?.Invoke(message);
            }
        }
        return reports;
    }

    private async Task<IReadOnlyList<string>> RelocateAsync(Guid planId, IReadOnlyList<ItemPlacementRule> planPlacements,
        IDisposable lease, Action check, CancellationToken token, Action<MarketMutationReceipt> receiptChanged, Action<string> reportChanged)
    {
        var reports = new List<string>();
        foreach (var request in MarketRunPlanner.RelocationRequests(planId, planPlacements, cache.Snapshot(), owner))
        {
            // Finish each batch's deposit before retrieving more; never retry a refused or ambiguous move.
            var sources = cache.Snapshot().Values.Where(r => r.Owner.Matches(owner) &&
                r.IsCurrentlyAssigned is not false && r.IsUiAccessible is not false &&
                !request.Routing.PreferredRetainerIds.Contains(r.RetainerId)).Select(r => r.RetainerId).ToArray();
            var stopItem = false;
            foreach (var source in sources)
            {
                if (stopItem) break;
                while (true)
                {
                    check();
                    if (!cache.Snapshot().TryGetValue(source, out var sourceCache) ||
                        !sourceCache.Bags.Where(b => b.BagName.StartsWith("RetainerPage", StringComparison.Ordinal))
                            .SelectMany(b => b.Items).Any(i => i.ItemId == request.ItemId && i.IsHq == request.IsHighQuality && i.Quantity > 0)) break;
                    var player = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
                    var stackSize = Math.Max(1, (maxStackSize ?? throw new InvalidOperationException("Item stack-size authority unavailable."))(request.ItemId));
                    var capacity = PlacementRouter.LocalBatch([request], cache.Snapshot(), owner, _ => stackSize, DateTime.UtcNow).PlannedQuantity;
                    // Empty slots are a conservative bound: do not assume a partial bag stack can merge.
                    var batchUnits = (int)Math.Min(capacity, Math.Min(int.MaxValue, (long)player.FreeSlots * stackSize));
                    if (batchUnits <= 0)
                    {
                        var message = $"MarketRunRelocateSkippedNoRoom: {request.ItemName}: no observed destination capacity or free bag slots.";
                        reports.Add(message); reportChanged(message); logWarning?.Invoke(message);
                        stopItem = true;
                        break;
                    }
                    var held = player.Stacks.Where(s => s.ItemId == request.ItemId && s.IsHighQuality == request.IsHighQuality).Sum(s => s.Quantity);
                    var target = new MarketGatherTarget(request.ItemId, request.ItemName,
                        request.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly, checked(held + batchUnits), false);
                    MarketMutationReceipt? lastReceipt = null;
                    void Receipt(MarketMutationReceipt receipt)
                    {
                        lastReceipt = receipt;
                        receipt.Intent = receipt.Intent with { Kind = "RelocationRetrieval" };
                        receipt.Code = receipt.Outcome == MarketMutationOutcome.Committed ? "MarketRunRelocateRetrieved" :
                            receipt.Outcome is null or MarketMutationOutcome.Indeterminate ? "MarketRunRelocateIndeterminate" : "MarketRunRelocateSkippedExecutionFailed";
                        receiptChanged(receipt);
                    }
                    var guarded = new GatherDriver(transferDriver, this, check, target, deadline, routeDeadline, Receipt, request, source);
                    var operation = journal.CreateManual(owner, [new TargetPlanItem { ItemId = target.ItemId,
                    ItemName = target.ItemName, Quality = target.Quality, TargetQuantity = target.Units }]);
                    var coordinator = new TransferCoordinator(journal, guarded, cache, currentOwner,
                        () => new Dictionary<uint, int> { [request.ItemId] = held }, automation, warning: logWarning,
                        retrievalSource: id => id == source);
                    await coordinator.ExecuteRetrievalUnderLeaseAsync(operation.OperationId, lease, token).ConfigureAwait(false);
                    var outcome = journal.Get(operation.OperationId)!;
                    if (outcome.Status == OperationStatuses.Indeterminate)
                        throw Indeterminate(outcome.Message);
                    check();
                    var moved = outcome.Lines.Sum(l => l.TransferredQuantity);
                    if (moved <= 0)
                    {
                        if (outcome.Status != OperationStatuses.Succeeded)
                        {
                            var message = $"MarketRunRelocateSkippedExecutionFailed: {request.ItemName}: {outcome.Message}";
                            reports.Add(message); reportChanged(message); logWarning?.Invoke(message);
                        }
                        break;
                    }
                    var after = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
                    var arrived = Math.Max(0, after.Stacks.Where(s => s.ItemId == request.ItemId && s.IsHighQuality == request.IsHighQuality).Sum(s => s.Quantity) - held);
                    if (arrived != moved)
                        throw Indeterminate("Retrieved stock does not match live bag arrival.");
                    reports.AddRange(await DepositRequestsAsync([request with { Quantity = moved }], lease, check, token,
                        receiptChanged, report => { reportChanged(report); }, true).ConfigureAwait(false));
                    var final = await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
                    if (final.Stacks.Where(s => s.ItemId == request.ItemId && s.IsHighQuality == request.IsHighQuality).Sum(s => s.Quantity) > held)
                    {
                        stopItem = true;
                        break; // Settled no-room: leave the reported remainder, never retrieve another batch of this item.
                    }
                    // A bag-limited batch ends PartiallySucceeded; its verified deposit freed the bags, so take the next batch.
                    // Each pass moved and stowed > 0 units, so this terminates with the source.
                    if (outcome.Status is not (OperationStatuses.Succeeded or OperationStatuses.PartiallySucceeded)) break;

                    MarketGatherIndeterminateException Indeterminate(string reason)
                    {
                        var receipt = lastReceipt ?? new MarketMutationReceipt
                        {
                            Intent = new("RelocationRetrieval", guarded.CurrentRetainer, request.ItemName, null, null, batchUnits, 0),
                            RetainerName = guarded.CurrentRetainerName,
                        };
                        receipt.Outcome = MarketMutationOutcome.Indeterminate;
                        receipt.Code = "MarketRunRelocateIndeterminate"; receipt.Message = reason;
                        receiptChanged(receipt);
                        logWarning?.Invoke($"{receipt.Code}: {request.ItemName}: {reason}");
                        return new(guarded.CurrentRetainer, guarded.CurrentRetainerName, $"{receipt.Code}: {request.ItemName}: {reason}");
                    }
                }
            }
        }
        return reports;
    }

    private sealed class StowDriver(RetainerLiveDriver inner, MarketRunLiveDriver market, Action check,
        StowageDepositRequest request, Action<MarketMutationReceipt> receiptChanged, bool relocation = false) : IRetainerTransferDriver
    {
        private string Prefix => relocation ? "MarketRunRelocate" : "MarketRunStow";
        public ulong CurrentRetainer { get; private set; }
        public string CurrentRetainerName { get; private set; } = string.Empty;
        private MarketMutationReceipt? lastReceipt;
        private void Guard() { check(); market.RequireSessionAuthority(); }
        private Task Step(Func<CancellationToken, Task> action)
        {
            Guard();
            return market.deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
        }
        public Task RequireRetainerListAsync(CancellationToken token)
        {
            Guard();
            return market.routeDeadline.RunAsync(async t => { await inner.RequireRetainerListAsync(t).ConfigureAwait(false); return true; });
        }
        public async Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken token)
        {
            Guard();
            if (!request.Routing.AnySpace && !request.Routing.PreferredRetainerIds.Contains(candidate.RetainerId))
                throw new InvalidOperationException("Stowage destination is not authorized by the reserve.");
            CurrentRetainer = candidate.RetainerId;
            CurrentRetainerName = candidate.RetainerName;
            await Step(t => inner.OpenRetainerAsync(candidate, t)).ConfigureAwait(false);
        }
        public Task OpenInventoryAsync(CancellationToken token) => Step(inner.OpenInventoryAsync);
        public async Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> ids, CancellationToken token)
        {
            market.RequireSessionAuthority();
            var stacks = await market.deadline.RunAsync(t => inner.ScanRetainerAsync(ids, t)).ConfigureAwait(false);
            market.RequireSessionAuthority();
            market.ObserveStow(CurrentRetainer, request, stacks);
            return stacks;
        }
        public async Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> ids, CancellationToken token)
        {
            Guard();
            return (await market.deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false)).Stacks.Where(s => ids.Contains(s.ItemId)).ToArray();
        }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DalamudInventoryStack>>([]);
        public async Task<RetainerDepositResult> DepositAsync(DalamudInventoryStack stack, int quantity, CancellationToken token)
        {
            Guard();
            if (stack.ItemId != request.ItemId || stack.IsHighQuality != request.IsHighQuality)
                throw new InvalidOperationException("Stowage source variant is not authorized.");
            var live = await market.deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false);
            Guard();
            if (!live.Stacks.Contains(stack)) throw new InvalidOperationException("Stowage source stack changed before dispatch.");
            var receipt = new MarketMutationReceipt
            {
                Intent = new(relocation ? "RelocationDeposit" : "Stowage", CurrentRetainer, request.ItemName, null, stack, quantity, 0),
                RetainerName = CurrentRetainerName,
            };
            receiptChanged(receipt); // Persist intent before dispatch; never resend an uncertain move.
            lastReceipt = receipt;
            try
            {
                Guard();
                var result = await market.deadline.RunAsync(t => inner.DepositAsync(stack, quantity, t)).ConfigureAwait(false);
                // A dropped dispatch shows no movement through the verifier's window and late tail. With the source
                // stack still intact this attempt moved nothing; the coordinator retries or settles it and marks the
                // run uncertain itself if a later read disagrees.
                var zeroMovement = !result.Success && result.Transferred == 0 && result.Code == "VerificationWindowExpired" &&
                    await SourceIntactAsync(stack).ConfigureAwait(false);
                receipt.Outcome = result.Success && result.Transferred > 0 ? MarketMutationOutcome.Committed :
                    result.Code == "NoCapacity" || zeroMovement ? MarketMutationOutcome.Unchanged : MarketMutationOutcome.Indeterminate;
                receipt.Code = receipt.Outcome == MarketMutationOutcome.Committed ? (relocation ? "MarketRunRelocated" : "MarketRunStowed") :
                    zeroMovement ? Prefix + "ZeroMovement" :
                    receipt.Outcome == MarketMutationOutcome.Unchanged ? Prefix + "LeftInBagsNoRoom" : Prefix + "Indeterminate";
                receipt.Message = $"{result.Transferred} units stowed at {CurrentRetainerName}; {result.Code}: {result.Message}";
                return result;
            }
            catch (Exception e)
            {
                receipt.Outcome = MarketMutationOutcome.Indeterminate;
                receipt.Code = Prefix + "Indeterminate"; receipt.Message = e.Message;
                throw;
            }
            finally
            {
                if (receipt.Outcome != MarketMutationOutcome.Committed)
                    market.logWarning?.Invoke($"{receipt.Code}: {request.ItemName} × {quantity} to {CurrentRetainerName}: {receipt.Message}");
                receiptChanged(receipt);
            }
        }
        private async Task<bool> SourceIntactAsync(DalamudInventoryStack stack)
        {
            try { return (await market.deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false)).Stacks.Contains(stack); }
            catch (Exception e) when (e is not OperationCanceledException) { return false; }
        }
        public void MarkIndeterminate(string reason)
        {
            var receipt = lastReceipt ?? new MarketMutationReceipt
            {
                Intent = new(relocation ? "RelocationDeposit" : "Stowage", CurrentRetainer, request.ItemName, null, null, request.Quantity, 0),
                RetainerName = CurrentRetainerName,
            };
            receipt.Outcome = MarketMutationOutcome.Indeterminate;
            receipt.Code = Prefix + "Indeterminate"; receipt.Message = reason;
            receiptChanged(receipt);
            market.logWarning?.Invoke($"{receipt.Code}: {request.ItemName} × {receipt.Intent.Quantity} to {CurrentRetainerName}: {reason}");
        }
        public Task CloseRetainerAsync(CancellationToken token) => market.HasSessionAuthority() && !MarketRunDeadline.HasUnsettled
            ? market.deadline.RunAsync(async t => { await inner.CloseRetainerAsync(t).ConfigureAwait(false); return true; }) : Task.CompletedTask;
        public Task CloseRetainerListAsync(CancellationToken token) => Task.CompletedTask;
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => throw new NotSupportedException();
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => throw new NotSupportedException();
        public void CancelActive() { }
    }

    /// <summary>
    /// One terminal saddlebag attempt as a run receipt, so the run's no-repeat rules see it.
    /// A move that may have happened without a proven arrival is Indeterminate.
    /// </summary>
    private static MarketMutationReceipt SaddlebagReceipt(SaddlebagAttemptReport attempt) => new()
    {
        Intent = new(SaddlebagKind, 0, attempt.ItemName, null, attempt.Source, attempt.Quantity, 0),
        RetainerName = "Saddlebag",
        Outcome = attempt.Outcome == InventoryMoveOutcome.Retrieved ? MarketMutationOutcome.Committed
            : attempt.MayHaveMoved ? MarketMutationOutcome.Indeterminate
            : attempt.Outcome == InventoryMoveOutcome.NotEnqueued ? MarketMutationOutcome.Unchanged
            : MarketMutationOutcome.FailedBeforeSend,
        Code = attempt.Code,
        Message = attempt.Message,
    };

    private static void Require(RetainerAutomationResult result)
    { if (!result.Success) throw new InvalidOperationException($"{result.Code}: {result.Message}"); }

    private void ObserveGather(ulong id, MarketGatherTarget target, IReadOnlyList<DalamudInventoryStack> stacks) =>
        cache.ReplaceObservedVariant(new(id, target.ItemId, target.ItemName, target.Quality == ItemQualityPolicy.HqOnly,
            DateTime.UtcNow, stacks.Where(s => s.ItemId == target.ItemId && s.IsHighQuality == (target.Quality == ItemQualityPolicy.HqOnly)).ToArray()));

    private void ObserveStow(ulong id, StowageDepositRequest request, IReadOnlyList<DalamudInventoryStack> stacks) =>
        cache.ReplaceObservedVariant(new(id, request.ItemId, request.ItemName, request.IsHighQuality,
            DateTime.UtcNow, stacks.Where(s => s.ItemId == request.ItemId && s.IsHighQuality == request.IsHighQuality).ToArray()));

    private sealed class GatherDriver(RetainerLiveDriver inner, MarketRunLiveDriver market, Action check,
        MarketGatherTarget target, IMarketRunDeadline deadline, IMarketRunDeadline routeDeadline,
        Action<MarketMutationReceipt>? receiptChanged, StowageDepositRequest? relocation = null, ulong? sourceId = null) : IRetainerTransferDriver
    {
        public bool BlockedByInventory { get; private set; }
        public int InventoryBlockedUnits { get; private set; }
        public bool SourceChanged { get; private set; }
        private ulong currentRetainer;
        public ulong CurrentRetainer => currentRetainer;
        public string CurrentRetainerName { get; private set; } = string.Empty;
        private Task<bool> Step(Func<CancellationToken, Task> action) => deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
        public Task RequireRetainerListAsync(CancellationToken token)
        {
            check();
            return routeDeadline.RunAsync(async t => { await inner.RequireRetainerListAsync(t).ConfigureAwait(false); return true; });
        }
        public async Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken token)
        {
            check();
            if (relocation is not null && (candidate.RetainerId != sourceId || relocation.Routing.PreferredRetainerIds.Contains(candidate.RetainerId)))
                throw new InvalidOperationException("Relocation source is not authorized.");
            await Step(t => inner.OpenRetainerAsync(candidate, t)).ConfigureAwait(false);
            currentRetainer = candidate.RetainerId;
            CurrentRetainerName = candidate.RetainerName;
        }
        public Task OpenInventoryAsync(CancellationToken token) { check(); return Step(inner.OpenInventoryAsync); }
        public async Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> ids, CancellationToken token)
        {
            market.RequireSessionAuthority();
            var stacks = await deadline.RunAsync(t => inner.ScanRetainerAsync(ids, t)).ConfigureAwait(false);
            market.RequireSessionAuthority();
            market.ObserveGather(currentRetainer, target, stacks);
            return stacks;
        }
        public async Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> ids, CancellationToken token)
        {
            market.RequireSessionAuthority();
            var stacks = await deadline.RunAsync(t => inner.ScanPlayerInventoryAsync(ids, t)).ConfigureAwait(false);
            market.RequireSessionAuthority();
            return stacks;
        }
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => RetrieveAsync(stack, quantity, 0, token);
        public async Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, int before, CancellationToken token)
        {
            var dispatched = false;
            try
            {
                check();
                var player = await deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false);
                var held = player.Stacks.Where(s => s.ItemId == target.ItemId && s.IsHighQuality == stack.IsHighQuality).Sum(s => s.Quantity);
                quantity = Math.Min(quantity, Math.Max(0, target.Units - held));
                if (relocation is not null)
                {
                    if (stack.ItemId != relocation.ItemId || stack.IsHighQuality != relocation.IsHighQuality ||
                        !stack.Container.ToString().StartsWith("RetainerPage", StringComparison.Ordinal))
                        return new(false, 0, "SourceNotAuthorized", "Relocation requires the configured variant in retainer inventory.");
                    var size = market.StackSize(stack.ItemId);
                    quantity = (int)Math.Min(quantity, Math.Min((long)player.FreeSlots * size,
                        market.RelocationCapacity(relocation)));
                }
                if (quantity == 0) return new(true, 0, "Satisfied", "Live player stock already satisfies gather.");
                // Conservatively reserve an empty slot; an item-only scan does not prove merge capacity.
                if (player.FreeSlots == 0)
                {
                    BlockedByInventory = true;
                    InventoryBlockedUnits = Math.Max(0, target.Units - held);
                    return new(false, 0, "InventoryFull", "Gather blocked by inventory space.");
                }
                var live = await ScanRetainerAsync(new HashSet<uint> { stack.ItemId }, token).ConfigureAwait(false);
                if (!live.Contains(stack)) { SourceChanged = true; return new(false, 0, "SourceSlotChanged", "Gather source changed."); }
                check();
                if (!market.OwnsGatherLease()) return new(false, 0, "LeaseLost", "Gather lease lost.");
                var receipt = new MarketMutationReceipt
                { Intent = new("Retrieval", currentRetainer, target.ItemName, null, stack, quantity, 0), RetainerName = CurrentRetainerName };
                receiptChanged?.Invoke(receipt); // Durable intent before dispatch, just like listing mutations.
                dispatched = true;
                try
                {
                    var result = await deadline.RunAsync(t => inner.RetrieveAsync(stack, quantity, before, t)).ConfigureAwait(false);
                    if (result.Code == "InventoryFull" && !result.MovementMayHaveOccurred)
                    {
                        BlockedByInventory = true;
                        InventoryBlockedUnits = Math.Max(0, target.Units - held);
                    }
                    receipt.Outcome = result.Success && result.Transferred > 0 ? MarketMutationOutcome.Committed :
                        result.MovementMayHaveOccurred ? MarketMutationOutcome.Indeterminate : MarketMutationOutcome.FailedBeforeSend;
                    receipt.Code = result.Code; receipt.Message = result.Message;
                    return result;
                }
                catch (Exception e)
                {
                    receipt.Outcome = MarketMutationOutcome.Indeterminate;
                    receipt.Code = "RetrievalFaulted"; receipt.Message = e.Message;
                    throw;
                }
                finally
                {
                    if (receipt.Outcome != MarketMutationOutcome.Committed && !BlockedByInventory)
                        market.logWarning?.Invoke($"Retrieval {target.ItemName} at {CurrentRetainerName}: {receipt.Code}: {receipt.Message}");
                    receiptChanged?.Invoke(receipt);
                }
            }
            catch (OperationCanceledException) when (!dispatched)
            { return new(false, 0, "GatherCancelled", "Gather cancelled before dispatch."); }
            catch (Exception e) when (!dispatched)
            { return new(false, 0, "GatherStoppedBeforeSend", e.Message); }
        }
        public Task CloseRetainerAsync(CancellationToken token) => market.HasSessionAuthority() && !MarketRunDeadline.HasUnsettled
            ? Step(inner.CloseRetainerAsync) : Task.CompletedTask;
        // Keep the list open for the listing pass under the same lease.
        public Task CloseRetainerListAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> ids, CancellationToken token) => throw new NotSupportedException();
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => throw new NotSupportedException();
        public void CancelActive() { }
    }
}
