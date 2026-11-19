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

    private sealed class PendingMove(TargetPlanItem rule, PlacementMove move, int size)
    {
        public TargetPlanItem Rule => rule;
        public PlacementMove Move => move;
        public int Size => size;
        public int Remaining { get; set; } = move.Quantity;
        public (uint ItemId, bool Hq) Key => (rule.ItemId, move.IsHighQuality);
        public ItemQualityPolicy Quality => move.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly;
        public StowageDepositRequest Request(Guid planId, int quantity) =>
            new(planId, rule.Id, rule.ItemId, rule.ItemName, move.IsHighQuality, quantity, rule.Routing, move.DestinationRetainerId, size);
        public string Label => $"{rule.ItemName} ({Quality})";
    }

    private sealed record TransitLine(PendingMove Work, int Before, int Count);

    private async Task<(Dictionary<(uint, bool), int> Units, int FreeSlots)> ReadMoveBagsAsync(
        IEnumerable<PendingMove> moves, CancellationToken token)
    {
        var bags = await driver.ReadPlacementBagsAsync(token).ConfigureAwait(false);
        var crystals = moves.Select(m => m.Rule.ItemId).Where(ElementalCurrencyCatalog.IsElementalCurrency).ToHashSet();
        var stacks = bags.Stacks.Where(s => !ElementalCurrencyCatalog.IsElementalCurrency(s.ItemId));
        if (crystals.Count > 0)
            stacks = stacks.Concat(await driver.ScanPlayerCrystalsAsync(crystals, token).ConfigureAwait(false));
        return (stacks.GroupBy(s => (s.ItemId, s.IsHighQuality)).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity)), bags.FreeSlots);
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
        async Task<OperationRecord> Deposit(IReadOnlyList<(TargetPlanItem Rule, StowageDepositRequest Request, int Floor)> lines, ulong destination, bool transit = false)
        {
            var leg = new PlacementLeg(transit ? TransitAuthorized : authorized,
                DepositFloors: lines.ToDictionary(l => (l.Rule.ItemId, l.Request.IsHighQuality),
                    l => (transit ? (l.Request.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly) : l.Rule.Quality,
                        l.Floor)));
            leg.Check();
            var stock = Cache();
            if (transit && (stock.GetValueOrDefault(destination) is not { Owner.HasStableIdentity: true, IsCurrentlyAssigned: not false, IsUiAccessible: not false } target ||
                lines.Any(l => !StockCoverage.CoversItem(target, l.Rule.ItemId))))
                throw new PlacementStop("PlacementDestinationUnverified", $"{detail}; destination stock or access is no longer verified.");
            var batch = StowageRouter.BuildBatch(lines.Select(l => l.Request), stock, owner, sizeOf, utcNow());
            if (batch.PlannedQuantity == 0)
            {
                if (!transit) throw StowShort(lines[0].Request, detail, 0);
                throw new PlacementStop("DestinationFull", $"{detail}; no observed room at {destination} for {batch.RequestedQuantity}.");
            }
            var id = journal.CreateTransferDeposit(owner, plan, batch).OperationId;
            Note("PlacementDepositLeg", $"{detail}; deposit {id}");
            var result = await ExecuteDepositUnderLeaseAsync(id, lease, transit ? CancellationToken.None : token, leg).ConfigureAwait(false);
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
                var deposit = await Deposit(group.Select(d => (d.Rule, d.Request, d.Rule.TargetQuantity)).ToArray(), retainerId).ConfigureAwait(false);
                if (deposit.Status == OperationStatuses.Indeterminate) throw new InvalidOperationException(deposit.Message);
                foreach (var (_, request, _) in group)
                {
                    var moved = deposit.Lines.SingleOrDefault(l => l.ItemId == request.ItemId && l.IsHighQuality == request.IsHighQuality)?.TransferredQuantity ?? 0;
                    if (moved != request.Quantity) throw StowShort(request, detail, moved, deposit.Message);
                }
                CheckOutcome(deposit, "PlayerStowIncomplete");
            }
            phase = "moves";
            var consolidating = rules.Where(r => r.Routing.Consolidate && r.Routing.PreferredRetainerIds.Count > 0).ToArray();
            // Discover each rule's routes at its turn: earlier trips can free a destination
            // that had no capacity at the start of the phase. Pull later rules onto each
            // trip opportunistically; their own turns then see only outstanding stock.
            IEnumerable<(ulong Source, ulong Destination, List<PendingMove> Queue)> MoveQueues()
            {
                for (var i = 0; i < consolidating.Length; i++)
                {
                    var rule = consolidating[i];
                    var size = sizeOf(rule.ItemId);
                    var initial = PlacementMovePlanner.FromCache(rule, Cache().Values, size);
                    foreach (var group in initial.Moves.GroupBy(m => (m.SourceRetainerId, m.DestinationRetainerId)))
                    {
                        var (source, destination) = group.Key;
                        var queue = group.Select(m => new PendingMove(rule, m, size)).ToList();
                        var stock = Cache();
                        foreach (var other in consolidating.Skip(i + 1).Where(r => r.Routing.PreferredRetainerIds[0] == destination))
                        {
                            var otherSize = sizeOf(other.ItemId);
                            queue.AddRange(PlacementMovePlanner.FromCache(other, stock.Values, otherSize).Moves
                                .Where(m => m.SourceRetainerId == source && m.DestinationRetainerId == destination)
                                .Select(m => new PendingMove(other, m, otherSize)));
                        }
                        yield return (source, destination, queue);
                    }
                }
            }
            foreach (var (source, destination, queue) in MoveQueues())
            {
                while (queue.Count > 0)
                {
                    var now = Cache();
                    var route = $"{now.GetValueOrDefault(source)?.RetainerName} [{source}] → {now.GetValueOrDefault(destination)?.RetainerName} [{destination}]";
                    detail = route;
                    var (before, slots) = await ReadMoveBagsAsync(queue, token).ConfigureAwait(false);
                    var free = slots;
                    var trip = new List<TransitLine>();
                    var seen = new HashSet<(uint, bool)>();
                    CachedRetainer? target = null;
                    foreach (var w in queue)
                    {
                        if (!seen.Add(w.Key)) continue;
                        var fresh = PlacementMovePlanner.FromCache(w.Rule, now.Values, w.Size).Moves.FirstOrDefault(m =>
                            m.SourceRetainerId == source && m.DestinationRetainerId == destination && m.IsHighQuality == w.Key.Hq);
                        if (fresh is null)
                        {
                            Limit("PlacementRouteChanged", $"{w.Label} × {w.Remaining}: {route}");
                            w.Remaining = 0;
                            continue;
                        }
                        target ??= StowageRouter.Copy(now[destination]);
                        var capacity = StowageRouter.Capacity(target, w.Key.ItemId, w.Key.Hq, w.Size);
                        var count = Math.Min(Math.Min(w.Remaining, fresh.Quantity), capacity.TotalCapacity);
                        var held = before.GetValueOrDefault(w.Key);
                        if (ElementalCurrencyCatalog.IsElementalCurrency(w.Key.ItemId))
                        {
                            count = Math.Min(count, Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - held));
                            if (capacity.PartialStackCapacity > 0) count = Math.Min(count, capacity.PartialStackCapacity);
                        }
                        else
                        {
                            count = (int)Math.Min(count, (long)free * w.Size);
                            if (count > 0) free -= (int)(((long)count + w.Size - 1) / w.Size);
                        }
                        if (count <= 0) continue;
                        trip.Add(new(w, held, count));
                        StowageRouter.ApplyProjectedDeposit(target, w.Request(plan.Id, count), count, w.Size);
                    }
                    var removed = queue.RemoveAll(w => w.Remaining == 0);
                    if (trip.Count == 0)
                    {
                        if (removed > 0) continue; // Reconsider any duplicates hidden by a retired move.
                        var blocked = queue[0];
                        if (ElementalCurrencyCatalog.IsElementalCurrency(blocked.Key.ItemId))
                            throw new PlacementStop("PlacementCrystalPouchFull", $"{blocked.Label}: player pouch holds {before.GetValueOrDefault(blocked.Key):N0} and has no room.");
                        throw new PlacementStop("PlacementBagsFull", $"{blocked.Label}: {slots} free bag slots.");
                    }

                    string Manifest(Func<TransitLine, int> quantity) => string.Join("; ", trip.Where(t => quantity(t) > 0)
                        .Select(t => $"{quantity(t)} × {t.Work.Label}"));
                    var requested = Manifest(t => t.Count);
                    detail = $"{requested}: {route}";
                    Arm(source);
                    Arm(destination);
                    inTransit = $" Check your bags for up to {requested}; arrival is unverified.";
                    var retrieved = await Retrieve(trip.Select(t =>
                    {
                        var row = StowagePlanCatalog.CopyRule(t.Work.Rule, plan.Id, newIdentity: false);
                        row.Quality = t.Work.Quality;
                        row.TargetQuantity = checked(t.Before + t.Count);
                        return (row, t.Before);
                    }).ToArray(), source).ConfigureAwait(false);
                    var (afterPickup, _) = await ReadMoveBagsAsync(trip.Select(t => t.Work), CancellationToken.None).ConfigureAwait(false);
                    int Arrived(TransitLine t) => afterPickup.GetValueOrDefault(t.Work.Key) - t.Before;
                    var cargo = Manifest(Arrived);
                    inTransit = cargo.Length == 0 ? "" : $" In your bags from {route}: {cargo}.";
                    if (retrieved.Status == OperationStatuses.Indeterminate)
                        throw new PlacementUncertain("PlacementRetrievalIndeterminate", retrieved.Message);
                    foreach (var t in trip)
                    {
                        var receipt = retrieved.Lines.Single(l => l.ItemId == t.Work.Key.ItemId && l.Quality == t.Work.Quality).TransferredQuantity;
                        if (Arrived(t) != receipt || Arrived(t) < 0 || Arrived(t) > t.Count)
                            throw new PlacementUncertain("PlacementArrivalMismatch", $"{t.Work.Label}: arrived {Arrived(t)}, receipt {receipt}.");
                    }

                    var loaded = trip.Where(t => Arrived(t) > 0).ToArray();
                    if (loaded.Length > 0)
                    {
                        inTransit = $" Check your bags for up to {cargo}; delivery is unverified.";
                        var deposited = await Deposit(loaded.Select(t => (t.Work.Rule, t.Work.Request(plan.Id, Arrived(t)), t.Before)).ToArray(),
                            destination, transit: true).ConfigureAwait(false);
                        var (afterDrop, _) = await ReadMoveBagsAsync(loaded.Select(t => t.Work), CancellationToken.None).ConfigureAwait(false);
                        int Left(TransitLine t) => afterDrop.GetValueOrDefault(t.Work.Key) - t.Before;
                        var residual = string.Join("; ", loaded.Where(t => Left(t) > 0).Select(t => $"{Left(t)} × {t.Work.Label}"));
                        inTransit = residual.Length == 0 ? "" : $" In your bags: {residual}.";
                        if (deposited.Status == OperationStatuses.Indeterminate)
                            throw new PlacementUncertain("PlacementDepositIndeterminate", deposited.Message);
                        foreach (var t in loaded)
                        {
                            var receipt = deposited.Lines.SingleOrDefault(l => l.ItemId == t.Work.Key.ItemId && l.IsHighQuality == t.Work.Key.Hq)?.TransferredQuantity ?? 0;
                            if (Left(t) != Arrived(t) - receipt || Left(t) < 0)
                                throw new PlacementUncertain("PlacementDepositMismatch", $"{t.Work.Label}: bag delta {Left(t)}, arrived {Arrived(t)}, deposit receipt {receipt}.");
                            if (receipt == 0) continue;
                            var key = $"{t.Work.Label}, {route}";
                            delivered[key] = checked(delivered.GetValueOrDefault(key) + receipt);
                            Note("PlacementMoved", $"{key}: {receipt} delivered.");
                            t.Work.Remaining -= receipt;
                        }
                        if (residual.Length > 0) throw new PlacementStop("PlacementLeftInTransit", deposited.Message);
                        CheckOutcome(deposited, "PlacementDepositIncomplete");
                    }
                    Settle(false);
                    if (loaded.Length != trip.Count || trip.Any(t => Arrived(t) != t.Count) || retrieved.Status != OperationStatuses.Succeeded)
                        throw new PlacementStop("PlacementSourceShort", $"Requested {requested}; retrieved {(cargo.Length == 0 ? "nothing" : cargo)}. {retrieved.Message}");
                    queue.RemoveAll(w => w.Remaining == 0);
                }
            }
            var finalStock = Cache();
            foreach (var rule in consolidating)
                foreach (var limit in PlacementMovePlanner.FromCache(rule, finalStock.Values, sizeOf(rule.ItemId)).Limits.Distinct())
                    Limit(limit.Code, limit.Message);
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
