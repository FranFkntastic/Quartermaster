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
    AutomationLease automation, Func<OwnerScope> currentOwner, Func<uint, string> itemName) : IMarketRunDriver
{
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private readonly MarketRunDeadline deadline = new(TimeSpan.FromSeconds(15));
    private ulong retainerId;
    private string retainerName = string.Empty;
    private OwnerScope owner = new();
    public string ItemName(uint id) => itemName(id);

    public async Task RequireRetainerListAsync(CancellationToken token)
    {
        owner = currentOwner();
        if (!owner.HasStableIdentity) throw new InvalidOperationException("Stable character identity required.");
        await transferDriver.RequireRetainerListAsync(token).ConfigureAwait(false);
    }
    public async Task OpenAsync(ulong id, string name, CancellationToken token)
    {
        Require(await session.OpenRetainerAsync(new(id, name), token).ConfigureAwait(false));
        retainerId = id; retainerName = name;
        Require(await session.OpenSellingListAsync(token).ConfigureAwait(false));
    }
    public async Task<IReadOnlyList<RetainerMarketListingTarget>> ListingsAsync(CancellationToken token)
    {
        var scan = await session.ScanMarketListingsAsync(token).ConfigureAwait(false);
        if (!scan.Success) throw new InvalidOperationException($"{scan.Code}: {scan.Message}");
        cache.ReplaceListings(new(retainerId, retainerName, owner, DateTime.UtcNow, scan.Listings.Select(l => new CachedMarketListing
        {
            ItemId = l.ItemId, ItemName = itemName(l.ItemId), Quantity = (uint)l.Quantity,
            IsHq = l.IsHq, UnitPrice = l.UnitPrice, SlotIndex = l.SlotIndex, ContainerKey = InventoryType.RetainerMarket.ToString(),
        }).ToArray()));
        return scan.Listings;
    }
    public async Task<MarketPlayerInventory> PlayerAsync(CancellationToken token)
    {
        var stacks = await session.ScanPlayerInventoryAsync(null, token).ConfigureAwait(false);
        var names = Bags.Select(b => b.ToString()).ToArray();
        playerCache.Observe(owner, new(Bags.Select(b => new InventoryBag
        {
            BagName = b.ToString(), Items = stacks.Where(s => s.Container == b).Select(s => new InventoryItem
            { ItemId = s.ItemId, ItemName = itemName(s.ItemId), Quantity = (uint)s.Quantity, IsHq = s.IsHighQuality,
                ContainerKey = b.ToString(), SlotIndex = s.SlotIndex }).ToList(),
        }).ToArray(), names, names), DateTime.UtcNow);
        return new(stacks, Math.Max(0, 140 - stacks.Select(s => (s.Container, s.SlotIndex)).Distinct().Count()));
    }
    public async Task<MarketMutationOutcome> RepriceAsync(RetainerMarketListingTarget listing, uint price, CancellationToken token) =>
        (await session.UpdateSellingListingPriceAsync(listing, price, token).ConfigureAwait(false)).Success
            ? MarketMutationOutcome.Committed : MarketMutationOutcome.Indeterminate;
    public async Task<MarketMutationOutcome> PullAsync(RetainerMarketListingTarget listing, CancellationToken token) =>
        (MarketMutationOutcome)(int)(await session.RemoveMarketListingToPlayerInventoryAsync(listing, token).ConfigureAwait(false)).Outcome;
    public async Task<MarketMutationOutcome> PostAsync(DalamudInventoryStack source, int quantity, uint price, CancellationToken token) =>
        (MarketMutationOutcome)(int)(await session.PostMarketListingAsync(source, quantity, price, token).ConfigureAwait(false)).Outcome;
    public async Task ReturnAsync(CancellationToken token) => Require(await session.ReturnToRetainerListAsync(token).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>> GatherAsync(IReadOnlyList<MarketGatherTarget> targets, IDisposable lease, Action check, CancellationToken token)
    {
        var reports = new List<string>();
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
            var guarded = new GatherDriver(transferDriver, this, check, target, deadline);
            var operation = journal.CreateManual(owner, [new TargetPlanItem { ItemId = target.ItemId, ItemName = target.ItemName,
                Quality = target.Quality, TargetQuantity = target.Units }]);
            var coordinator = new TransferCoordinator(journal, guarded, cache, currentOwner,
                () => new Dictionary<uint, int> { [target.ItemId] = held }, automation);
            await coordinator.ExecuteRetrievalUnderLeaseAsync(operation.OperationId, lease, token).ConfigureAwait(false);
            var outcome = journal.Get(operation.OperationId);
            if (guarded.BlockedByInventory) reports.Add($"{target.ItemName}: gather blocked by inventory space; {target.Units - held - (outcome?.Lines.Sum(l => l.TransferredQuantity) ?? 0)} units");
            else if (outcome?.Status != OperationStatuses.Succeeded) reports.Add($"{target.ItemName}: gather {outcome?.Message}");
            if (journal.Get(operation.OperationId)?.Status == OperationStatuses.Indeterminate)
                throw new InvalidOperationException($"Gather needs a check: {target.ItemName}.");
            await deadline.RunAsync(PlayerAsync).ConfigureAwait(false);
        }
        return reports;
    }
    private static void Require(RetainerAutomationResult result)
    { if (!result.Success) throw new InvalidOperationException($"{result.Code}: {result.Message}"); }

    private void ObserveGather(ulong id, MarketGatherTarget target, IReadOnlyList<DalamudInventoryStack> stacks) =>
        cache.ReplaceObservedVariant(new(id, target.ItemId, target.ItemName, target.Quality == ItemQualityPolicy.HqOnly,
            DateTime.UtcNow, stacks.Where(s => s.ItemId == target.ItemId && s.IsHighQuality == (target.Quality == ItemQualityPolicy.HqOnly)).ToArray()));

    private sealed class GatherDriver(RetainerLiveDriver inner, MarketRunLiveDriver market, Action check,
        MarketGatherTarget target, IMarketRunDeadline deadline) : IRetainerTransferDriver
    {
        public bool BlockedByInventory { get; private set; }
        private ulong currentRetainer;
        private Task<bool> Step(Func<CancellationToken, Task> action) => deadline.RunAsync(async t => { await action(t).ConfigureAwait(false); return true; });
        public Task RequireRetainerListAsync(CancellationToken token) { check(); return Step(inner.RequireRetainerListAsync); }
        public async Task OpenRetainerAsync(RetainerRouteCandidate candidate, CancellationToken token)
        {
            check();
            await Step(t => inner.OpenRetainerAsync(candidate, t)).ConfigureAwait(false);
            currentRetainer = candidate.RetainerId;
        }
        public Task OpenInventoryAsync(CancellationToken token) { check(); return Step(inner.OpenInventoryAsync); }
        public async Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> ids, CancellationToken token)
        {
            var stacks = await deadline.RunAsync(t => inner.ScanRetainerAsync(ids, t)).ConfigureAwait(false);
            market.ObserveGather(currentRetainer, target, stacks);
            return stacks;
        }
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> ids, CancellationToken token) => deadline.RunAsync(t => inner.ScanPlayerInventoryAsync(ids, t));
        public Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => RetrieveAsync(stack, quantity, 0, token);
        public async Task<RetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, int before, CancellationToken token)
        {
            check();
            var player = await deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false);
            var held = player.Stacks.Where(s => s.ItemId == target.ItemId && s.IsHighQuality == stack.IsHighQuality).Sum(s => s.Quantity);
            quantity = Math.Min(quantity, Math.Max(0, target.Units - held));
            if (quantity == 0) return new(true, 0, "Satisfied", "Live player stock already satisfies gather.");
            // Conservatively reserve one empty slot for each source stack; do not
            // assume stack merge capacity from an item-id-only inventory scan.
            if (player.FreeSlots == 0) { BlockedByInventory = true; return new(false, 0, "InventoryFull", "Gather blocked by inventory space."); }
            var live = await ScanRetainerAsync(new HashSet<uint> { stack.ItemId }, token).ConfigureAwait(false);
            if (!live.Contains(stack)) return new(false, 0, "SourceSlotChanged", "Gather source changed.");
            check();
            var result = await deadline.RunAsync(t => inner.RetrieveAsync(stack, quantity, before, t)).ConfigureAwait(false);
            await deadline.RunAsync(market.PlayerAsync).ConfigureAwait(false);
            return result;
        }
        public Task CloseRetainerAsync(CancellationToken token) => Step(inner.CloseRetainerAsync);
        // Keep the list open for the listing pass under the same lease.
        public Task CloseRetainerListAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> ids, CancellationToken token) => throw new NotSupportedException();
        public Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken token) => throw new NotSupportedException();
        public void CancelActive() { }
    }
}
