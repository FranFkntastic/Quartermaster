using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Planning;

namespace RQ.Operations;

public sealed partial class TransferCoordinator
{
    private CancellationTokenSource? placementCancellation;
    private sealed record PlacementLeg(Func<bool> Authorized,
        IReadOnlyDictionary<(uint, ItemQualityPolicy), int>? RetrievalBaselines = null, ulong? Source = null,
        IReadOnlyDictionary<(uint, bool), (ItemQualityPolicy Quality, int Quantity)>? DepositFloors = null,
        bool KeepListOpen = true)
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
        async Task<OperationRecord> Retrieve(IReadOnlyList<(TargetPlanItem Rule, int Held)> needs, ulong? source)
        {
            var leg = new PlacementLeg(authorized, needs.ToDictionary(n => (n.Rule.ItemId, n.Rule.Quality), n => n.Held), source);
            leg.Check();
            var id = journal.CreateTransferRetrieval(owner, plan, needs.Select(n => n.Rule).ToArray()).OperationId;
            Note("PlacementRetrievalLeg", $"{detail}; retrieval {id}");
            var result = await ExecuteRetrievalUnderLeaseAsync(id, lease, source.HasValue ? CancellationToken.None : token, leg).ConfigureAwait(false);
            if (!result.Started) throw new InvalidOperationException(result.Message);
            var record = journal.Get(id)!;
            verifiedUnits += record.Lines.Sum(l => l.TransferredQuantity);
            return record;
        }
        async Task<OperationRecord> Deposit(IReadOnlyList<(TargetPlanItem Rule, StowageDepositRequest Request)> lines, ulong destination, int? transitFloor = null)
        {
            var leg = new PlacementLeg(transitFloor.HasValue ? TransitAuthorized : authorized,
                DepositFloors: lines.ToDictionary(l => (l.Rule.ItemId, l.Request.IsHighQuality),
                    l => (transitFloor.HasValue ? (l.Request.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly) : l.Rule.Quality,
                        transitFloor ?? l.Rule.TargetQuantity)));
            leg.Check();
            var stock = Cache();
            if (transitFloor.HasValue && (stock.GetValueOrDefault(destination) is not { Owner.HasStableIdentity: true, IsCurrentlyAssigned: not false, IsUiAccessible: not false } target ||
                !StockCoverage.CoversItem(target, lines[0].Rule.ItemId)))
                throw new PlacementStop("PlacementDestinationUnverified", $"{detail}; destination stock or access is no longer verified.");
            var batch = StowageRouter.BuildBatch(lines.Select(l => l.Request), stock, owner, sizeOf, utcNow());
            if (batch.PlannedQuantity == 0)
            {
                if (!transitFloor.HasValue) throw StowShort(lines[0].Request, detail, 0);
                throw new PlacementStop("DestinationFull", $"{detail}; no observed room at {destination} for {batch.RequestedQuantity}.");
            }
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
            var needs = new List<(TargetPlanItem Rule, int Held)>();
            foreach (var rule in rules)
            {
                detail = rule.ItemName;
                var (held, _) = await Read(rule, rule.Quality).ConfigureAwait(false);
                var need = rule.TargetQuantity - held;
                if (need <= 0) continue;
                needs.Add((rule, held));
            }
            if (needs.Count > 0)
            {
                detail = "retrieving player shortfalls";
                var retrievedToPlayer = await Retrieve(needs, null).ConfigureAwait(false);
                if (retrievedToPlayer.Status == OperationStatuses.Indeterminate) throw new InvalidOperationException(retrievedToPlayer.Message);
                if (retrievedToPlayer.Status != OperationStatuses.Succeeded)
                {
                    var incomplete = false;
                    foreach (var line in retrievedToPlayer.Lines)
                    {
                        var held = needs.First(n => n.Rule.ItemId == line.ItemId && n.Rule.Quality == line.Quality).Held;
                        if (line.TransferredQuantity < line.TargetQuantity - held)
                        {
                            incomplete = true;
                            Limit("PlayerRetrievalIncomplete", $"{line.ItemName} × {line.TargetQuantity - held} to player; {retrievedToPlayer.Message}");
                        }
                    }
                    if (!incomplete) Limit("PlayerRetrievalIncomplete", retrievedToPlayer.Message);
                }
            }
            phase = "stow";
            var projected = Cache().ToDictionary(p => p.Key, p => StowageRouter.Copy(p.Value));
            var deposits = new List<(TargetPlanItem Rule, StowageDepositRequest Request, StowageAllocation Destination)>();
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
                    var size = sizeOf(rule.ItemId);
                    var route = StowageRouter.Route(request, projected, owner, size);
                    foreach (var a in route.Allocations)
                    {
                        deposits.Add((rule, request with { Quantity = a.Quantity, DestinationOverride = a.RetainerId }, a));
                        StowageRouter.ApplyProjectedDeposit(projected[a.RetainerId], request, a.Quantity, size);
                    }
                    surplus -= amount - route.RemainingQuantity;
                    if (route.RemainingQuantity > 0 && (rule.Routing.PreferredRetainerIds.Count > 0 || rule.Routing.AnySpace))
                        Limit("PlayerStowCapacityLimited", $"{rule.ItemName} × {route.RemainingQuantity} remain in bags.");
                }
            }
            // One visit per destination. Receipts and bag floors are keyed by item + NQ/HQ, so two rules
            // landing in the same variant (e.g. an Any rule and an NQ-only rule for one item) take
            // separate visits rather than sharing a batch.
            var batches = deposits.GroupBy(d => d.Destination.RetainerId).SelectMany(group => group
                .GroupBy(d => (d.Rule.ItemId, d.Request.IsHighQuality))
                .SelectMany(variant => variant.Select((d, index) => (Index: index, Deposit: d)))
                .GroupBy(entry => entry.Index, entry => entry.Deposit));
            foreach (var group in batches)
            {
                var retainerId = group.First().Destination.RetainerId;
                detail = group.First().Destination.RetainerName;
                var deposit = await Deposit(group.Select(d => (d.Rule, d.Request)).ToArray(), retainerId).ConfigureAwait(false);
                if (deposit.Status == OperationStatuses.Indeterminate) throw new InvalidOperationException(deposit.Message);
                foreach (var (_, request, _) in group)
                {
                    var moved = deposit.Lines.SingleOrDefault(l => l.ItemId == request.ItemId && l.IsHighQuality == request.IsHighQuality)?.TransferredQuantity ?? 0;
                    if (moved != request.Quantity) throw StowShort(request, detail, moved, deposit.Message);
                }
                CheckOutcome(deposit, "PlayerStowIncomplete");
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
                        var row = StowagePlanCatalog.CopyRule(rule, plan.Id, newIdentity: false);
                        row.Quality = quality;
                        row.TargetQuantity = checked(before + count);
                        var retrieved = await Retrieve([(row, before)], move.SourceRetainerId).ConfigureAwait(false);
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
                            var request = new StowageDepositRequest(plan.Id, rule.Id, rule.ItemId, rule.ItemName,
                                move.IsHighQuality, arrived, rule.Routing, move.DestinationRetainerId, size);
                            var deposited = await Deposit([(rule, request)], move.DestinationRetainerId, before).ConfigureAwait(false);
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

    private static PlacementStop StowShort(StowageDepositRequest request, string destination, int moved, string reason = "") =>
        new("PlayerStowShort", $"{request.ItemName} ({(request.IsHighQuality ? "HQ" : "NQ")}) to {destination}; accepted {moved} of {request.Quantity}. {reason}".TrimEnd());

    private sealed class PlacementUncertain(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class PlacementStop(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
