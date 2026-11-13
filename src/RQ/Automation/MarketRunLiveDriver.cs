using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Inventory;
using RQ.Operations;
using InventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;

namespace RQ.Automation;

/// <summary>Thin adapter: only the pinned Franthropy session performs game actions.</summary>
internal sealed class MarketRunLiveDriver(
    IRetainerAutomationSession session, RetainerLiveDriver transferDriver,
    OperationJournal journal, RetainerCacheRepository cache, PlayerInventoryCacheRepository playerCache,
    AutomationLease automation, Func<OwnerScope> currentOwner, Func<uint, string> itemName,
    Action<string>? warning = null, ISaddlebagRetrievalDriver? saddlebag = null,
    Func<IReadOnlyDictionary<uint, int>>? cachedSaddlebag = null, Action<string>? information = null) : IMarketRunDriver
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
    private bool HasSessionAuthority() => runAuthority() && OwnsGatherLease() &&
        owner.HasStableIdentity && currentOwner() is { HasStableIdentity: true } current && owner.Matches(current);
    private void RequireSessionAuthority()
    {
        if (!HasSessionAuthority()) throw new InvalidOperationException("Market run session authority lost.");
    }
    public string ItemName(uint id) => itemName(id);

    public async Task RequireRetainerListAsync(CancellationToken token)
    {
        RequireSessionAuthority();
        await transferDriver.RequireRetainerListAsync(token).ConfigureAwait(false);
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
            ItemId = l.ItemId, ItemName = itemName(l.ItemId), Quantity = (uint)l.Quantity,
            IsHq = l.IsHq, UnitPrice = l.UnitPrice, SlotIndex = l.SlotIndex, ContainerKey = InventoryType.RetainerMarket.ToString(),
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
            BagName = b.ToString(), Items = stacks.Where(s => s.Container == b).Select(s => new InventoryItem
            { ItemId = s.ItemId, ItemName = itemName(s.ItemId), Quantity = (uint)s.Quantity, IsHq = s.IsHighQuality,
                ContainerKey = b.ToString(), SlotIndex = s.SlotIndex }).ToList(),
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
        foreach (var target in targets)
        {
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
            }
            else if (guarded.SourceChanged) reports.Add($"{target.ItemName}: source changed; {Math.Max(0, target.Units - held - (outcome?.Lines.Sum(l => l.TransferredQuantity) ?? 0))} units not gathered");
            else if (outcome?.Status != OperationStatuses.Succeeded) reports.Add($"{target.ItemName}: gather {outcome?.Message}");
            await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
        }
        return reports;
    }
    internal const string SaddlebagKind = "SaddlebagRetrieval";

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

    private sealed class GatherDriver(RetainerLiveDriver inner, MarketRunLiveDriver market, Action check,
        MarketGatherTarget target, IMarketRunDeadline deadline, IMarketRunDeadline routeDeadline,
        Action<MarketMutationReceipt>? receiptChanged) : IRetainerTransferDriver
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
                    if (receipt.Outcome != MarketMutationOutcome.Committed)
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
