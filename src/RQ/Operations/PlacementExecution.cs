using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

public sealed partial class TransferCoordinator
{
    private CancellationTokenSource? placementCancellation;
    private sealed record PlacementLeg(TargetPlanItem Rule, Func<bool> Authorized, int Baseline = 0, ulong? Source = null,
        int? TransitBagFloor = null)
    {
        public void Check()
        {
            if (!Authorized()) throw new PlacementStop("PlacementAuthorityChanged", "Plan revision, owner, cancellation or lease changed.");
        }
    }

    private static bool Matches(ItemQualityPolicy quality, bool hq) =>
        quality == ItemQualityPolicy.Any || hq == (quality == ItemQualityPolicy.HqOnly);

    internal Task<TransferExecutionResult> ExecutePlacementAsync(OwnerScope owner, StowagePlan plan,
        IReadOnlyList<TargetPlanItem> rules, Func<bool> revisionCurrent, Func<uint, int> stackSize) =>
        WithAutoRetainerCoordinationAsync(async () =>
        {
            if (!automation.TryAcquire("transfer plan placement", out var lease) || lease is null)
                return new(false, automation.AcquisitionBlockReason ?? "Automation unavailable.");
            using (lease)
            using (var cancel = new CancellationTokenSource())
            {
                lock (activeGate) placementCancellation = cancel;
                try { return await RunPlacementAsync(owner, plan, rules, stackSize, lease,
                    () => !cancel.IsCancellationRequested && automation.Owns(lease) && SamePlacementOwner(owner) && revisionCurrent(),
                    cancel.Token).ConfigureAwait(false); }
                finally { lock (activeGate) placementCancellation = null; }
            }
        }, CancellationToken.None);

    private bool SamePlacementOwner(OwnerScope owner) =>
        currentOwner() is { HasStableIdentity: true } current && owner.HasStableIdentity && owner.Matches(current);

    private async Task<TransferExecutionResult> RunPlacementAsync(OwnerScope owner, StowagePlan plan,
        IReadOnlyList<TargetPlanItem> rules, Func<uint, int> sizeOf, IDisposable lease, Func<bool> authorized, CancellationToken token)
    {
        var parent = journal.CreatePlacement(owner, plan, rules).OperationId;
        var phase = "retrieval";
        var detail = "";
        var problems = new List<(string Code, string Message)>();
        var delivered = new Dictionary<string, int>();
        var armed = new HashSet<ulong>();
        long verifiedUnits = 0;
        // Units a move has landed in the bags and not yet delivered; named in any failure message.
        var inTransit = "";
        // Once units are in the bags, finishing the pair needs only the lease and the owner:
        // a cancel or plan edit stops the next batch, never strands this one.
        bool TransitAuthorized() => automation.Owns(lease) && owner.Matches(currentOwner());
        string Deliveries() => string.Join("; ", delivered.Select(p => $"{p.Key}: {p.Value} delivered"));
        void Arm(ulong id)
        {
            journal.ArmCacheInvalidation(parent, id, owner);
            armed.Add(id);
        }
        void Settle(bool uncertain)
        {
            foreach (var id in armed)
                if (!uncertain || cache.InvalidateStock(id).Persisted)
                    journal.ResolveCacheInvalidation(parent, id);
            armed.Clear();
        }
        journal.Transition(parent, OperationStatuses.Running, "PlacementStarted", "Retrieve, stow, then move between retainers.");
        void Note(string code, string message) => journal.RecordWarning(parent, code, message);
        void Limit(string code, string message) { problems.Add((code, message)); Note(code, message); }
        IReadOnlyDictionary<ulong, CachedRetainer> Cache() => cache.Snapshot().Where(p => p.Value.Owner.Matches(owner)).ToDictionary();
        async Task<(int Units, int Slots)> Read(TargetPlanItem rule, ItemQualityPolicy quality, bool transit = false)
        {
            var readToken = transit ? CancellationToken.None : token;
            var bags = await driver.ReadPlacementBagsAsync(readToken).ConfigureAwait(false);
            var stacks = ElementalCurrencyCatalog.IsElementalCurrency(rule.ItemId)
                ? await driver.ScanPlayerCrystalsAsync(new HashSet<uint> { rule.ItemId }, readToken).ConfigureAwait(false) : bags.Stacks;
            return (stacks.Where(s => s.ItemId == rule.ItemId && Matches(quality, s.IsHighQuality)).Sum(s => s.Quantity), bags.FreeSlots);
        }
        async Task<OperationRecord> Retrieve(TargetPlanItem rule, ItemQualityPolicy quality, int baseline, int count, ulong? source)
        {
            var leg = new PlacementLeg(rule, authorized, baseline, source);
            leg.Check();
            var row = StowagePlanCatalog.CopyRule(rule, plan.Id, newIdentity: false);
            row.Quality = quality;
            row.TargetQuantity = checked(baseline + count);
            var id = journal.CreateTransferRetrieval(owner, plan, [row]).OperationId;
            Note("PlacementRetrievalLeg", $"{detail}; retrieval {id}");
            var result = await ExecuteRetrievalUnderLeaseAsync(id, lease, source.HasValue ? CancellationToken.None : token, leg).ConfigureAwait(false);
            if (!result.Started) throw new InvalidOperationException(result.Message);
            var record = journal.Get(id)!;
            verifiedUnits += record.Lines.Sum(l => l.TransferredQuantity);
            return record;
        }
        async Task<OperationRecord> Deposit(TargetPlanItem rule, bool hq, int count, ulong destination, int? transitFloor = null)
        {
            var leg = new PlacementLeg(rule, transitFloor.HasValue ? TransitAuthorized : authorized, TransitBagFloor: transitFloor);
            leg.Check();
            var request = new StowageDepositRequest(plan.Id, rule.Id, rule.ItemId, rule.ItemName, hq, count,
                rule.Routing, destination, sizeOf(rule.ItemId));
            var stock = Cache();
            if (transitFloor.HasValue && (stock.GetValueOrDefault(destination) is not { Owner.HasStableIdentity: true, IsCurrentlyAssigned: not false, IsUiAccessible: not false } target ||
                !StockCoverage.CoversItem(target, rule.ItemId)))
                throw new PlacementStop("PlacementDestinationUnverified", $"{detail}; destination stock or access is no longer verified.");
            var batch = StowageRouter.BuildBatch([request], stock, owner, sizeOf, utcNow());
            if (batch.PlannedQuantity == 0) throw new PlacementStop("DestinationFull", $"{detail}; no observed room at {destination} for {count}.");
            var id = journal.CreateTransferDeposit(owner, plan, batch).OperationId;
            Note("PlacementDepositLeg", $"{detail}; deposit {id}");
            var result = await ExecuteDepositUnderLeaseAsync(id, lease, transitFloor.HasValue ? CancellationToken.None : token, leg).ConfigureAwait(false);
            if (!result.Started) throw new InvalidOperationException(result.Message);
            var record = journal.Get(id)!;
            verifiedUnits += record.Lines.Sum(l => l.TransferredQuantity);
            return record;
        }
        void CheckOutcome(OperationRecord record, string code)
        {
            if (record.Status == OperationStatuses.Indeterminate) throw new InvalidOperationException(record.Message);
            if (record.Status != OperationStatuses.Succeeded) throw new PlacementStop(code, $"{detail}; {record.Message}");
        }
        try
        {
            foreach (var rule in rules)
            {
                detail = rule.ItemName;
                var (held, _) = await Read(rule, rule.Quality).ConfigureAwait(false);
                var need = rule.TargetQuantity - held;
                if (need <= 0) continue;
                detail = $"{rule.ItemName} × {need} to player";
                var retrievedToPlayer = await Retrieve(rule, rule.Quality, held, need, null).ConfigureAwait(false);
                if (retrievedToPlayer.Status == OperationStatuses.Indeterminate) throw new InvalidOperationException(retrievedToPlayer.Message);
                if (retrievedToPlayer.Status != OperationStatuses.Succeeded)
                    Limit("PlayerRetrievalIncomplete", $"{detail}; {retrievedToPlayer.Message}");
            }
            phase = "stow";
            foreach (var rule in rules)
            {
                detail = rule.ItemName;
                var (held, _) = await Read(rule, rule.Quality).ConfigureAwait(false);
                var surplus = Math.Max(0, held - rule.TargetQuantity);
                foreach (var hq in new[] { false, true })
                {
                    if (surplus == 0 || !Matches(rule.Quality, hq)) continue;
                    var quality = hq ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
                    var amount = Math.Min(surplus, (await Read(rule, quality).ConfigureAwait(false)).Units);
                    var request = new StowageDepositRequest(plan.Id, rule.Id, rule.ItemId, rule.ItemName, hq, amount,
                        rule.Routing);
                    var route = StowageRouter.Route(request, Cache(), owner, sizeOf(rule.ItemId));
                    foreach (var a in route.Allocations)
                    {
                        detail = $"{rule.ItemName} × {a.Quantity} to {a.RetainerName}";
                        var left = a.Quantity;
                        while (left > 0)
                        {
                            var capacity = StowageRouter.Capacity(Cache()[a.RetainerId], rule.ItemId, hq, sizeOf(rule.ItemId));
                            var count = Math.Min(left, capacity.PartialStackCapacity > 0 ? capacity.PartialStackCapacity : capacity.TotalCapacity);
                            if (count <= 0) throw new PlacementStop("PlayerStowFull", $"{detail}; {left} units have no room.");
                            var deposit = await Deposit(rule, hq, count, a.RetainerId).ConfigureAwait(false);
                            CheckOutcome(deposit, "PlayerStowIncomplete");
                            var moved = deposit.Lines.Sum(l => l.TransferredQuantity);
                            if (moved != count) throw new PlacementStop("PlayerStowShort", $"{detail}; accepted {moved} of {count}.");
                            left -= moved;
                        }
                    }
                    surplus -= amount - route.RemainingQuantity;
                    if (route.RemainingQuantity > 0 && (rule.Routing.PreferredRetainerIds.Count > 0 || rule.Routing.AnySpace))
                        Limit("PlayerStowCapacityLimited", $"{rule.ItemName} × {route.RemainingQuantity} remain in bags.");
                }
            }
            phase = "moves";
            foreach (var rule in rules.Where(r => r.Routing.Consolidate && r.Routing.PreferredRetainerIds.Count > 0))
            {
                var size = sizeOf(rule.ItemId);
                detail = rule.ItemName;
                var initial = PlacementMovePlanner.FromCache(rule, Cache().Values, size);
                foreach (var move in initial.Moves)
                {
                    var remaining = move.Quantity;
                    var quality = move.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
                    while (remaining > 0)
                    {
                        var now = Cache();
                        detail = $"{rule.ItemName} ({quality}) × {remaining}: {now.GetValueOrDefault(move.SourceRetainerId)?.RetainerName} [{move.SourceRetainerId}] → {now.GetValueOrDefault(move.DestinationRetainerId)?.RetainerName} [{move.DestinationRetainerId}]";
                        var fresh = PlacementMovePlanner.FromCache(rule, now.Values, size).Moves.FirstOrDefault(m =>
                            m.SourceRetainerId == move.SourceRetainerId && m.DestinationRetainerId == move.DestinationRetainerId && m.IsHighQuality == move.IsHighQuality);
                        if (fresh is null) { Limit("PlacementRouteChanged", detail); break; }
                        var (before, slots) = await Read(rule, quality).ConfigureAwait(false);
                        var crystal = ElementalCurrencyCatalog.IsElementalCurrency(rule.ItemId);
                        var count = crystal
                            ? Math.Min(Math.Min(remaining, fresh.Quantity), Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - before))
                            : (int)Math.Min(Math.Min(remaining, fresh.Quantity), (long)slots * size);
                        var partial = StowageRouter.Capacity(now[move.DestinationRetainerId], rule.ItemId, move.IsHighQuality, size).PartialStackCapacity;
                        if (partial > 0) count = Math.Min(count, partial);
                        if (count == 0 && crystal) throw new PlacementStop("PlacementCrystalPouchFull", $"{detail}; player crystal pouch holds {before:N0} and has no room.");
                        if (count == 0) throw new PlacementStop("PlacementBagsFull", $"{detail}; {slots} free bag slots.");
                        Arm(move.SourceRetainerId);
                        Arm(move.DestinationRetainerId);
                        inTransit = $" Check your bags for up to {count} × {rule.ItemName} ({quality}); arrival is unverified.";
                        var retrieved = await Retrieve(rule, quality, before, count, move.SourceRetainerId).ConfigureAwait(false);
                        var arrived = (await Read(rule, quality, transit: true).ConfigureAwait(false)).Units - before;
                        if (arrived == 0 && retrieved.Status != OperationStatuses.Indeterminate) inTransit = "";
                        if (arrived > 0)
                            inTransit = $" {arrived} × {rule.ItemName} ({quality}) from {now.GetValueOrDefault(move.SourceRetainerId)?.RetainerName} are in your bags.";
                        if (retrieved.Status == OperationStatuses.Indeterminate)
                            throw new PlacementUncertain("PlacementRetrievalIndeterminate", retrieved.Message);
                        if (arrived != retrieved.Lines.Sum(l => l.TransferredQuantity) || arrived > count || arrived < 0)
                            throw new PlacementUncertain("PlacementArrivalMismatch", $"{detail}; arrived {arrived}, receipt {retrieved.Lines.Sum(l => l.TransferredQuantity)}.");
                        if (arrived > 0)
                        {
                            var deposited = await Deposit(rule, move.IsHighQuality, arrived, move.DestinationRetainerId, before).ConfigureAwait(false);
                            inTransit = $" Check your bags for up to {arrived} × {rule.ItemName} ({quality}); delivery is unverified.";
                            var left = (await Read(rule, quality, transit: true).ConfigureAwait(false)).Units - before;
                            if (left == 0) inTransit = "";
                            else if (left > 0) inTransit = $" {left} × {rule.ItemName} ({quality}) from {now[move.SourceRetainerId].RetainerName} are in your bags.";
                            if (deposited.Status == OperationStatuses.Indeterminate)
                                throw new PlacementUncertain("PlacementDepositIndeterminate", deposited.Message);
                            if (left != arrived - deposited.Lines.Sum(l => l.TransferredQuantity))
                                throw new PlacementUncertain("PlacementDepositMismatch", $"{detail}; bag delta {left}. {deposited.Message}");
                            if (left != 0) throw new PlacementStop("PlacementLeftInTransit", $"{detail}; {left} remain in bags. {deposited.Message}");
                            CheckOutcome(deposited, "PlacementDepositIncomplete");
                            var route = $"{rule.ItemName} ({quality}), {now[move.SourceRetainerId].RetainerName} → {now[move.DestinationRetainerId].RetainerName}";
                            delivered[route] = checked(delivered.GetValueOrDefault(route) + arrived);
                            Note("PlacementMoved", $"{route}: {arrived} delivered.");
                        }
                        if (arrived != count || retrieved.Status != OperationStatuses.Succeeded)
                            throw new PlacementStop("PlacementSourceShort", $"{detail}; requested {count}, retrieved {arrived}; {slots} free slots. {retrieved.Message}");
                        Settle(false);
                        remaining -= arrived;
                    }
                }
                foreach (var limit in PlacementMovePlanner.FromCache(rule, Cache().Values, size).Limits)
                    Limit(limit.Code, limit.Message);
            }
            var summary = ((problems.Count == 0 ? "Transfer plan placement completed." : string.Join(" ", problems.Select(p => $"{p.Code}: {p.Message}"))) + " " + Deliveries()).TrimEnd();
            journal.Transition(parent, problems.Count == 0 ? OperationStatuses.Succeeded : verifiedUnits > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed,
                problems.Count == 0 ? "PlacementComplete" : problems.Count == 1 ? problems[0].Code : "PlacementLimited", summary);
            return new(true, summary);
        }
        catch (OperationCanceledException) when (inTransit.Length == 0)
        {
            Settle(false);
            var message = $"{phase}: {detail}; cancelled between steps with nothing in transit. {Deliveries()}".TrimEnd();
            journal.Transition(parent, verifiedUnits > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed, "PlacementCancelled", message);
            return new(true, message);
        }
        catch (Exception ex)
        {
            Settle(ex is not PlacementStop);
            var message = $"{phase}: {detail}; {ex.Message}{inTransit} Remaining steps cancelled. {Deliveries()}".TrimEnd();
            journal.Transition(parent, ex is PlacementStop ? (verifiedUnits > 0 ? OperationStatuses.PartiallySucceeded : OperationStatuses.Failed) : OperationStatuses.Indeterminate,
                ex is PlacementStop stop ? stop.Code : ex is PlacementUncertain uncertain ? uncertain.Code : "PlacementIndeterminate", message);
            return new(true, message);
        }
        finally
        {
            // Legs leave the retainer list open so the next one does not race a bell reopen; close it once here.
            try { await driver.CloseRetainerListAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private sealed class PlacementUncertain(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class PlacementStop(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
