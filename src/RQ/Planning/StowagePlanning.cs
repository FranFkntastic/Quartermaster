using Franthropy.FFXIV.Filtering;
using Franthropy.Dalamud.Automation.Retainers;
using RQ.Domain;
using RQ.Inventory;
using RQ.Persistence;

namespace RQ.Planning;

public enum StowageAction
{
    None,
    Retrieve,
    Deposit,
    Move,
}

public sealed record StowageEvaluationLine(
    Guid PlanId,
    Guid RuleId,
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int PlayerQuantity,
    int DesiredPlayerQuantity,
    int RetrieveQuantity,
    int DepositQuantity,
    StowageAction Action,
    StowageRoutingPolicy Routing,
    int SaddlebagQuantity = 0)
{
    public IReadOnlyList<PlacementMove> Moves { get; init; } = [];
    public int MoveQuantity => Moves.Sum(m => m.Quantity);
    public IReadOnlyList<PlacementLimit> MoveLimits { get; init; } = [];
    public string? MissingPlacementEvidence { get; init; }
}

public sealed record StowageEvaluation(
    Guid PlanId,
    string PlanName,
    long PlanRevision,
    IReadOnlyList<StowageEvaluationLine> Lines)
{
    public int RetrieveQuantity => Lines.Sum(line => line.RetrieveQuantity);
    public int DepositQuantity => Lines.Sum(line => line.DepositQuantity);
}

public static class StowagePlanMigration
{
    public const string MigrationId = "target-plan-to-stowage-v1";

    public static bool EnsureOwnerPlan(StateRepository repository, OwnerScope owner, Func<DateTime>? utcNow = null)
    {
        if (!owner.HasStableIdentity)
            return false;
        if (!repository.ReadConfiguration(state => NeedsMigration(state, owner)))
            return false;

        repository.Mutate(state =>
        {
            if (!NeedsMigration(state, owner))
                return;
            var plan = state.StowagePlans.FirstOrDefault(candidate =>
                !candidate.IsEphemeral && candidate.Kind == StowagePlanKind.Transfer && candidate.Owner.Matches(owner));
            if (plan is null)
            {
                plan = new StowagePlan
                {
                    Owner = owner with { },
                    Name = "General",
                    Priority = 0,
                    Revision = 1,
                };
                state.StowagePlans.Add(plan);
            }

            foreach (var rule in state.PlanItems.Where(rule => rule.StowagePlanId == Guid.Empty))
            {
                rule.StowagePlanId = plan.Id;
                rule.Routing ??= new StowageRoutingPolicy { AnySpace = true };
            }

            if (state.StowageMigrations.All(record =>
                    record.MigrationId != MigrationId || !record.Owner.Matches(owner)))
            {
                state.StowageMigrations.Add(new StowageMigrationRecord
                {
                    MigrationId = MigrationId,
                    PlanId = plan.Id,
                    Owner = owner with { },
                    RuleCount = state.PlanItems.Count(rule => rule.StowagePlanId == plan.Id),
                    CompletedAtUtc = (utcNow ?? (() => DateTime.UtcNow))(),
                });
            }

            state.Schema = "gooseworks-quartermaster-state/v5";
        });
        return true;
    }

    public static bool NeedsMigration(QuartermasterState state, OwnerScope owner) =>
        owner.HasStableIdentity &&
        (state.StowagePlans.All(plan => plan.IsEphemeral || plan.Kind != StowagePlanKind.Transfer || !plan.Owner.Matches(owner)) ||
         state.PlanItems.Any(rule => rule.StowagePlanId == Guid.Empty) ||
         state.StowageMigrations.All(record =>
             record.MigrationId != MigrationId || !record.Owner.Matches(owner)));

    public static StowagePlan? OwnerPlan(QuartermasterState state, OwnerScope owner) =>
        state.StowagePlans
            .Where(plan => !plan.IsEphemeral && plan.Kind == StowagePlanKind.Transfer && plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .FirstOrDefault();

    public static IReadOnlyList<TargetPlanItem> OwnerRules(
        QuartermasterState state,
        OwnerScope owner,
        bool enabledPlansOnly = true)
    {
        var planIds = state.StowagePlans
            .Where(plan => !plan.IsEphemeral && plan.Kind == StowagePlanKind.Transfer && plan.Owner.Matches(owner) && (!enabledPlansOnly || plan.Enabled))
            .Select(plan => plan.Id)
            .ToHashSet();
        return state.PlanItems.Where(rule => planIds.Contains(rule.StowagePlanId)).ToArray();
    }

    /// <summary>
    /// Folds courier lines written one per route into one line per item and plan. A line that kept N
    /// in its source becomes that source with N typed. Every line's destination container (its single
    /// retainer, else its rank 1, else bags) is added as a destination that takes what is left, so a
    /// line that sent N moves nothing until a number is typed. Ranked fallbacks and Any space are
    /// dropped. Quality is kept when the lines agree, otherwise Any. Kept amounts carry over only when
    /// the lines shared one destination, quality and enabled state; lines that differed fold into a
    /// line with their destinations and no numbers. Returns whether anything changed; running it again
    /// changes nothing.
    /// </summary>
    public static bool CollapseCourierLines(QuartermasterState state)
    {
        var plans = new HashSet<Guid>();
        foreach (var group in state.PlanItems.Where(rule => rule.Courier is not null)
                     .GroupBy(rule => (rule.StowagePlanId, rule.ItemId))
                     .Where(group => group.Count() > 1 || group.Any(rule => rule.Courier!.IsLegacy)).ToArray())
        {
            var lines = group.ToList();
            var owner = state.StowagePlans.FirstOrDefault(plan => plan.Id == group.Key.StowagePlanId)?.Owner;
            var survivor = lines.FirstOrDefault(rule => !rule.Courier!.IsLegacy) ?? lines[0];
            // Lines that list sources and destinations keep them, in line order; a place keeps its first role.
            var explicitLines = lines.Where(rule => !rule.Courier!.IsLegacy).Select(rule => rule.Courier!).ToList();
            var sources = new List<Courier.CourierPlace>();
            var destinations = new List<Courier.CourierPlace>();
            bool Listed(OwnerScope who, ulong? retainer) =>
                sources.Any(known => known.Is(who, retainer)) || destinations.Any(known => known.Is(who, retainer));
            foreach (var line in explicitLines)
            {
                foreach (var place in line.Sources.Where(place => !Listed(place.Character, place.Retainer)))
                    sources.Add(place);
                foreach (var place in line.Destinations.Where(place => !Listed(place.Character, place.Retainer)))
                    destinations.Add(place);
            }
            var places = new List<Courier.LegacyCourierPlace>();
            void Add(Courier.LegacyCourierPlace place)
            {
                if (!places.Any(known => known.Is(place.Character, place.Retainer)) && !Listed(place.Character, place.Retainer))
                    places.Add(place);
            }
            foreach (var line in explicitLines.SelectMany(line => line.LegacyPlaces))
                Add(line);
            var legacy = lines.Where(rule => rule.Courier!.IsLegacy).ToList();
            static ulong? To(TargetPlanItem line) =>
                line.Courier!.RetainerId ?? (line.Routing.PreferredRetainerIds.Count > 0 ? line.Routing.PreferredRetainerIds[0] : null);
            foreach (var line in legacy)
                Add(new(line.Courier!.Character! with { }, To(line), Destination: true));
            // Kept amounts carry over only when the folded line would move what the old lines moved: one
            // destination for everything, one quality, one enabled state, and no kept source that is also
            // that destination. Otherwise the destinations alone carry over and the line moves nothing
            // until numbers are typed, rather than sending one line's surplus to another line's destination.
            var faithful = places.Count(place => place.Destination) + destinations.Count == 1 &&
                lines.Select(rule => rule.Quality).Distinct().Count() == 1 &&
                lines.Select(rule => rule.Enabled).Distinct().Count() == 1;
            if (faithful)
                foreach (var line in legacy.Where(rule => rule.Courier!.KeepInSource && rule.TargetQuantity >= 0))
                    if ((line.Courier!.SourceCharacter ?? owner) is { } source && !places.Any(known => known.Is(source, line.Courier.SourceRetainerId)) &&
                        !Listed(source, line.Courier.SourceRetainerId))
                        places.Insert(0, new(source with { }, line.Courier.SourceRetainerId, line.TargetQuantity));
            // Places in the older shape are converted once stock is known (CourierDistribution.Resolve).
            survivor.Courier = new Courier.CourierLine
            {
                Sources = [.. sources],
                Destinations = [.. destinations],
                SuggestionDismissed = explicitLines.Any(line => line.SuggestionDismissed),
                Places = places.Count > 0 ? [.. places] : default,
            };
            survivor.Quality = lines.Select(rule => rule.Quality).Distinct().Count() == 1 ? survivor.Quality : ItemQualityPolicy.Any;
            survivor.Enabled = lines.All(rule => rule.Enabled);
            if (string.IsNullOrWhiteSpace(survivor.Notes))
                survivor.Notes = lines.Select(rule => rule.Notes).FirstOrDefault(notes => !string.IsNullOrWhiteSpace(notes)) ?? survivor.Notes;
            survivor.TargetQuantity = 0;
            survivor.Routing = new();
            state.PlanItems.RemoveAll(rule => rule != survivor && lines.Contains(rule));
            plans.Add(group.Key.StowagePlanId);
        }
        foreach (var plan in state.StowagePlans.Where(plan => plans.Contains(plan.Id)))
            plan.Revision = checked(plan.Revision + 1);
        if (plans.Count > 0)
            StowagePlanCatalog.ClearStaleRecovery(state);
        return plans.Count > 0;
    }

    /// <summary>
    /// Separates courier lines from Transfer plans written before plans had a kind, losslessly:
    /// an unsaved plan's courier lines move to its owner's unsaved Courier plan; a saved plan holding
    /// only courier lines becomes a Courier plan; a saved plan holding both keeps its ordinary lines
    /// and its courier lines move to a new saved sibling "{Name} (Courier)". Line identities, routes,
    /// quantities and states are kept. Returns whether anything changed; running it again changes nothing.
    /// </summary>
    public static bool SeparateCourierLines(QuartermasterState state)
    {
        var changed = false;
        foreach (var plan in state.StowagePlans.Where(plan => plan.Kind == StowagePlanKind.Transfer).ToArray())
        {
            var courierLines = state.PlanItems.Where(rule => rule.StowagePlanId == plan.Id && rule.Courier is not null).ToArray();
            if (courierLines.Length == 0)
                continue;
            var hasOrdinaryLines = state.PlanItems.Any(rule => rule.StowagePlanId == plan.Id && rule.Courier is null);
            StowagePlan target;
            if (plan.IsEphemeral)
            {
                target = StowagePlanCatalog.FindScratch(state, plan.Owner, StowagePlanKind.Courier) ?? new StowagePlan
                {
                    Owner = plan.Owner with { },
                    Name = StowagePlanCatalog.ScratchName(StowagePlanKind.Courier),
                    IsEphemeral = true,
                    Enabled = false,
                    Kind = StowagePlanKind.Courier,
                    LastAppliedCourierRun = plan.LastAppliedCourierRun,
                };
                if (!state.StowagePlans.Contains(target))
                    state.StowagePlans.Add(target);
            }
            else if (!hasOrdinaryLines)
            {
                // Courier lines never took part in the enabled plan's targets; a Courier plan is never enabled.
                plan.Kind = StowagePlanKind.Courier;
                plan.Enabled = false;
                plan.Revision = checked(plan.Revision + 1);
                state.TransferPlanListingLinks.RemoveAll(link => link.StowagePlanId == plan.Id);
                changed = true;
                continue;
            }
            else
            {
                target = new StowagePlan
                {
                    Owner = plan.Owner with { },
                    Name = StowagePlanCatalog.UniqueName(state, plan.Owner, $"{plan.Name} (Courier)"),
                    Enabled = false,
                    Priority = plan.Priority,
                    Kind = StowagePlanKind.Courier,
                    LastAppliedCourierRun = plan.LastAppliedCourierRun,
                };
                state.StowagePlans.Add(target);
            }
            // Lines of one item in the target become one line in CollapseCourierLines, which runs next.
            foreach (var line in courierLines)
                line.StowagePlanId = target.Id;
            plan.Revision = checked(plan.Revision + 1);
            target.Revision = checked(target.Revision + 1);
            changed = true;
        }
        if (changed)
            StowagePlanCatalog.ClearStaleRecovery(state);
        return changed;
    }
}

public static class TransferPlanMigration
{
    public const string MigrationId = "restock-plans-to-transfer-plans-v1";

    public static bool EnsureOwnerPlans(StateRepository repository, OwnerScope owner, Func<DateTime>? utcNow = null)
    {
        if (!owner.HasStableIdentity || !repository.ReadConfiguration(state => NeedsMigration(state, owner)))
            return false;

        repository.Mutate(state =>
        {
            var migratedSourceIds = state.TransferPlanMigrations
                .Where(record => record.MigrationId == MigrationId && record.Owner.Matches(owner))
                .Select(record => record.SourceRestockPlanId)
                .ToHashSet();
            var sources = state.RestockPlans
                .Where(plan => plan.Owner.Matches(owner) && !migratedSourceIds.Contains(plan.Id))
                .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.Id)
                .ToArray();
            var emptyPlaceholder = state.StowagePlans.FirstOrDefault(plan =>
                !plan.IsEphemeral && plan.Kind == StowagePlanKind.Transfer && plan.Owner.Matches(owner) &&
                plan.Enabled &&
                state.PlanItems.All(rule => rule.StowagePlanId != plan.Id));
            if (sources.Any(source => source.Enabled) && emptyPlaceholder is not null)
            {
                emptyPlaceholder.Enabled = false;
                emptyPlaceholder.Revision = checked(emptyPlaceholder.Revision + 1);
            }
            foreach (var source in sources)
            {
                var target = new StowagePlan
                {
                    Owner = owner with { },
                    Name = StowagePlanCatalog.UniqueName(state, owner, source.Name),
                    Enabled = source.Enabled &&
                              state.StowagePlans.All(plan => !plan.Owner.Matches(owner) || !plan.Enabled),
                    Priority = StowagePlanCatalog.OwnerPlans(state, owner).Count,
                    Revision = 1,
                };
                state.StowagePlans.Add(target);
                var rules = source.Items
                    .Where(item => item.ItemId > 0)
                    .GroupBy(item => (item.ItemId, item.Quality))
                    .Select(group =>
                    {
                        var item = group.First();
                        return new TargetPlanItem
                        {
                            StowagePlanId = target.Id,
                            ItemId = item.ItemId,
                            ItemName = item.ItemName,
                            TargetQuantity = Math.Max(0, item.TargetQuantity),
                            Quality = item.Quality,
                            Notes = item.Notes,
                            Enabled = item.Enabled,
                        };
                    })
                    .ToArray();
                state.PlanItems.AddRange(rules);
                state.TransferPlanMigrations.Add(new TransferPlanMigrationRecord
                {
                    MigrationId = MigrationId,
                    SourceRestockPlanId = source.Id,
                    TransferPlanId = target.Id,
                    Owner = owner with { },
                    RuleCount = rules.Length,
                    CompletedAtUtc = (utcNow ?? (() => DateTime.UtcNow))(),
                });
            }
            state.Schema = "gooseworks-quartermaster-state/v5";
        });
        return true;
    }

    public static bool NeedsMigration(QuartermasterState state, OwnerScope owner)
    {
        if (!owner.HasStableIdentity)
            return false;
        var migratedSourceIds = state.TransferPlanMigrations
            .Where(record => record.MigrationId == MigrationId && record.Owner.Matches(owner))
            .Select(record => record.SourceRestockPlanId)
            .ToHashSet();
        return state.Schema != "gooseworks-quartermaster-state/v5" ||
               state.RestockPlans.Any(plan => plan.Owner.Matches(owner) && !migratedSourceIds.Contains(plan.Id));
    }
}

public static class StowageEvaluator
{
    public static IReadOnlyList<StowageEvaluation> Build(
        QuartermasterState state,
        BrowserProjection stock,
        OwnerScope owner)
    {
        var groups = stock.Items.ToDictionary(group => group.ItemId);
        return state.StowagePlans
            .Where(plan => !plan.IsEphemeral && plan.Enabled && plan.Owner.Matches(owner))
            .OrderBy(plan => plan.Priority)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id)
            .Select(plan =>
            {
                var lines = ListingPlanEvaluator.ComposeRules(state, stock, owner, plan.Id)
                    .Where(rule => rule.Enabled && rule.ItemId > 0)
                    .Select(rule => BuildLine(plan, rule, groups.GetValueOrDefault(rule.ItemId)))
                    .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(line => line.ItemId)
                    .ToArray();
                return new StowageEvaluation(plan.Id, plan.Name, plan.Revision, Project(lines, stock, owner));
            })
            .ToArray();
    }

    public static StowageEvaluation? BuildPlan(
        QuartermasterState state,
        BrowserProjection stock,
        OwnerScope owner,
        Guid planId)
    {
        var plan = state.StowagePlans.FirstOrDefault(candidate =>
            candidate.Id == planId && candidate.Owner.Matches(owner));
        if (plan is null)
            return null;
        var groups = stock.Items.ToDictionary(group => group.ItemId);
        var lines = ListingPlanEvaluator.ComposeRules(state, stock, owner, plan.Id)
            .Where(rule => rule.Enabled && rule.ItemId > 0)
            .Select(rule => BuildLine(plan, rule, groups.GetValueOrDefault(rule.ItemId)))
            .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.ItemId)
            .ToArray();
        return new StowageEvaluation(plan.Id, plan.Name, plan.Revision, Project(lines, stock, owner));
    }

    private static StowageEvaluationLine[] Project(StowageEvaluationLine[] lines, BrowserProjection stock, OwnerScope owner)
    {
        if (!lines.Any(l => l.Routing.Consolidate && l.Routing.PreferredRetainerIds.Count > 0)) return lines;
        var projected = stock.RetainerStock.Values.Where(r => r.Owner.Matches(owner))
            .ToDictionary(r => r.RetainerId, StowageRouter.Copy);
        var groups = stock.Items.ToDictionary(g => g.ItemId);
        int Size(uint id) => groups.GetValueOrDefault(id)?.Definition?.MaxStackSize is > 0 and var size ? checked((int)size) : 0;
        foreach (var line in lines.Where(l => l.RetrieveQuantity > l.SaddlebagQuantity))
        {
            var rule = new TargetPlanItem { Id = line.RuleId, ItemId = line.ItemId, ItemName = line.ItemName,
                Routing = line.Routing, Quality = line.Quality, TargetQuantity = line.DesiredPlayerQuantity };
            var retrieval = RestockPlanner.Build([rule], new Dictionary<uint, int>(), projected, owner, DateTime.UtcNow, stock).Lines.Single();
            var remaining = line.RetrieveQuantity - line.SaddlebagQuantity;
            foreach (var candidate in retrieval.Candidates)
            {
                if (remaining == 0) break;
                var count = Math.Min(remaining, candidate.CachedQuantity);
                Withdraw(projected[candidate.RetainerId], line.ItemId, line.Quality, count);
                remaining -= count;
            }
        }
        foreach (var line in lines)
        {
            var remaining = line.DepositQuantity;
            if (Size(line.ItemId) == 0) continue;
            foreach (var hq in new[] { false, true })
            {
                if (line.Quality != ItemQualityPolicy.Any && hq != (line.Quality == ItemQualityPolicy.HqOnly)) continue;
                var available = groups.GetValueOrDefault(line.ItemId)?.Stacks.Where(s => s.ScopeKind == BrowserScopeKind.Player &&
                    (s.Quality == FfxivItemQuality.HQ) == hq).Sum(s => s.Quantity) ?? 0;
                var quantity = Math.Min(remaining, available);
                if (quantity == 0) continue;
                var request = new StowageDepositRequest(line.PlanId, line.RuleId, line.ItemId, line.ItemName, hq, quantity,
                    line.Routing);
                foreach (var allocation in StowageRouter.Route(request, projected, owner, Size(line.ItemId)).Allocations)
                    StowageRouter.ApplyProjectedDeposit(projected[allocation.RetainerId], request, allocation.Quantity, Size(line.ItemId));
                remaining -= quantity;
            }
        }
        return lines.Select(line =>
        {
            if (!line.Routing.Consolidate || line.Routing.PreferredRetainerIds.Count == 0) return line;
            var rule = new TargetPlanItem { Id = line.RuleId, ItemId = line.ItemId, ItemName = line.ItemName,
                Quality = line.Quality, Routing = line.Routing };
            var size = Size(rule.ItemId);
            // No stock group means no observed units to move; stack size cannot affect that plan.
            if (size == 0 && groups.ContainsKey(rule.ItemId))
                return line with { MoveLimits = [new("PlacementStackSizeUnknown", 0, rule.ItemId, 0, $"Stack size unknown for {rule.ItemName}.")],
                    Action = line.Action == StowageAction.None ? StowageAction.Move : line.Action };
            var plan = PlacementMovePlanner.FromCache(rule, projected.Values, Math.Max(1, size));
            foreach (var move in plan.Moves)
            {
                Withdraw(projected[move.SourceRetainerId], rule.ItemId, move.IsHighQuality ? ItemQualityPolicy.HqOnly : ItemQualityPolicy.NqOnly, move.Quantity);
                StowageRouter.ApplyProjectedDeposit(projected[move.DestinationRetainerId],
                    new(line.PlanId, rule.Id, rule.ItemId, rule.ItemName, move.IsHighQuality, move.Quantity, rule.Routing), move.Quantity, size);
            }
            var missing = plan.Limits.FirstOrDefault(l => l.Code == "PlacementEvidenceUnavailable")?.Message;
            var pending = plan.MoveQuantity > 0 || plan.Limits.Count > 0 || missing is not null;
            return line with { Moves = plan.Moves, MoveLimits = plan.Limits, MissingPlacementEvidence = missing,
                Action = pending && line.Action == StowageAction.None ? StowageAction.Move : line.Action };
        }).ToArray();
    }

    private static void Withdraw(CachedRetainer retainer, uint itemId, ItemQualityPolicy quality, int count)
    {
        foreach (var item in retainer.Bags.Where(b => b.BagName.StartsWith("RetainerPage", StringComparison.Ordinal) ||
                     b.BagName == "RetainerCrystals").SelectMany(b => b.Items)
                     .Where(i => i.ItemId == itemId && (quality == ItemQualityPolicy.Any || i.IsHq == (quality == ItemQualityPolicy.HqOnly))))
        {
            var taken = Math.Min(count, checked((int)item.Quantity));
            item.Quantity -= (uint)taken;
            count -= taken;
            if (count == 0) break;
        }
        foreach (var bag in retainer.Bags) bag.Items.RemoveAll(i => i.ItemId == itemId && i.Quantity == 0);
    }

    private static StowageEvaluationLine BuildLine(
        StowagePlan plan,
        TargetPlanItem rule,
        StockGroup? stock)
    {
        var player = PlayerQuantity(rule, stock);
        var target = Math.Max(0, rule.TargetQuantity);
        var retrieve = Math.Max(0, target - player);
        var deposit = Math.Max(0, player - target);
        return new(
            plan.Id,
            rule.Id,
            rule.ItemId,
            rule.ItemName,
            rule.Quality,
            player,
            target,
            retrieve,
            deposit,
            retrieve > 0 ? StowageAction.Retrieve : deposit > 0 ? StowageAction.Deposit : StowageAction.None,
            Copy(rule.Routing),
            SaddlebagQuantity(rule, stock));
    }

    public static int PlayerQuantity(TargetPlanItem rule, StockGroup? stock) =>
        stock?.Stacks
            .Where(stack => stack.ScopeKind == BrowserScopeKind.Player && Matches(rule.Quality, stack.Quality))
            .Sum(stack => stack.Quantity) ?? 0;

    public static int SaddlebagQuantity(TargetPlanItem rule, StockGroup? stock) =>
        stock?.Stacks
            .Where(stack => stack.ScopeKind == BrowserScopeKind.Saddlebag && Matches(rule.Quality, stack.Quality))
            .Sum(stack => stack.Quantity) ?? 0;

    private static bool Matches(ItemQualityPolicy policy, FfxivItemQuality quality) => policy switch
    {
        ItemQualityPolicy.NqOnly => quality == FfxivItemQuality.NQ,
        ItemQualityPolicy.HqOnly => quality == FfxivItemQuality.HQ,
        _ => true,
    };

    private static StowageRoutingPolicy Copy(StowageRoutingPolicy? routing) => StowageRoutingPolicy.CopyOf(routing);
}

public sealed record StowageDepositRequest(
    Guid? SourcePlanId,
    Guid? SourceRuleId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity,
    StowageRoutingPolicy Routing,
    ulong? DestinationOverride = null,
    int MaxStackSize = 999);

public sealed record RetainerStowageCapacity(
    ulong RetainerId,
    string RetainerName,
    DateTime ObservedAtUtc,
    int PartialStackCapacity,
    int EmptySlotCapacity)
{
    public int TotalCapacity => checked(PartialStackCapacity + EmptySlotCapacity);
}

public sealed record StowageAllocation(
    ulong RetainerId,
    string RetainerName,
    int Quantity,
    int Capacity,
    DateTime ObservedAtUtc);

public sealed record StowageRoute(
    StowageDepositRequest Request,
    IReadOnlyList<StowageAllocation> Allocations,
    int RoutedQuantity,
    int RemainingQuantity)
{
    public IReadOnlyList<RetainerStowageCapacity> Candidates { get; init; } = [];
}

public sealed record StowageDepositBatch(
    DateTime BuiltAtUtc,
    IReadOnlyList<StowageRoute> Routes)
{
    public bool HasPlacementWork { get; init; }
    public int RequestedQuantity => Routes.Sum(route => route.Request.Quantity);
    public int PlannedQuantity => Routes.Sum(route => route.RoutedQuantity);
    public int RemainingQuantity => Routes.Sum(route => route.RemainingQuantity);
}

public static class StowageRouter
{
    private static readonly string[] RetainerPageNames =
    [
        "RetainerPage1",
        "RetainerPage2",
        "RetainerPage3",
        "RetainerPage4",
        "RetainerPage5",
        "RetainerPage6",
        "RetainerPage7",
    ];

    public static StowageRoute Route(
        StowageDepositRequest request,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        int maxStackSize)
    {
        if (request.Quantity <= 0 || maxStackSize <= 0)
            return new(request, [], 0, Math.Max(0, request.Quantity));

        var capacities = cache.Values
            .Where(retainer => retainer.Owner.Matches(owner))
            .Where(retainer => retainer.IsCurrentlyAssigned is not false)
            .Where(retainer => retainer.IsUiAccessible is not false)
            .Where(retainer => request.DestinationOverride is null ||
                               retainer.RetainerId == request.DestinationOverride)
            .Select(retainer => Capacity(retainer, request.ItemId, request.IsHighQuality, maxStackSize))
            .Where(candidate => candidate.TotalCapacity > 0)
            .ToArray();

        var ordered = new List<RetainerStowageCapacity>();
        if (request.DestinationOverride is not null)
        {
            ordered.AddRange(capacities
                .OrderBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RetainerId));
        }
        else
        {
            // Listed retainers strictly in list order; never an unlisted one unless AnySpace.
            var byId = new Dictionary<ulong, RetainerStowageCapacity>();
            foreach (var candidate in capacities)
                byId.TryAdd(candidate.RetainerId, candidate);
            var listed = request.Routing.PreferredRetainerIds.Distinct().ToArray();
            foreach (var retainerId in listed)
            {
                if (byId.TryGetValue(retainerId, out var candidate))
                    ordered.Add(candidate);
            }

            if (request.Routing.AnySpace)
            {
                var listedIds = listed.ToHashSet();
                ordered.AddRange(capacities
                    .Where(candidate => !listedIds.Contains(candidate.RetainerId))
                    .OrderByDescending(candidate => candidate.PartialStackCapacity > 0)
                    .ThenByDescending(candidate => candidate.PartialStackCapacity)
                    .ThenByDescending(candidate => candidate.TotalCapacity)
                    .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.RetainerId));
            }
        }

        var remaining = request.Quantity;
        var allocations = new List<StowageAllocation>();
        foreach (var candidate in ordered)
        {
            var quantity = Math.Min(remaining, candidate.TotalCapacity);
            if (quantity <= 0)
                continue;
            allocations.Add(new(
                candidate.RetainerId,
                candidate.RetainerName,
                quantity,
                candidate.TotalCapacity,
                candidate.ObservedAtUtc));
            remaining -= quantity;
            if (remaining == 0)
                break;
        }

        return new(request, allocations, request.Quantity - remaining, remaining)
        {
            Candidates = ordered.ToArray(),
        };
    }

    public static StowageDepositBatch BuildBatch(
        IEnumerable<StowageDepositRequest> requests,
        IReadOnlyDictionary<ulong, CachedRetainer> cache,
        OwnerScope owner,
        Func<uint, int> maxStackSize,
        DateTime nowUtc)
    {
        var projectedCache = cache.ToDictionary(entry => entry.Key, entry => Copy(entry.Value));
        var routes = new List<StowageRoute>();
        foreach (var request in requests.Where(request => request.Quantity > 0))
        {
            var resolvedMaxStackSize = Math.Max(1, maxStackSize(request.ItemId));
            var resolvedRequest = request with { MaxStackSize = resolvedMaxStackSize };
            var planned = Route(resolvedRequest, projectedCache, owner, resolvedMaxStackSize);
            var eligible = Route(resolvedRequest, cache, owner, resolvedMaxStackSize);
            routes.Add(planned with { Candidates = eligible.Candidates });
            foreach (var allocation in planned.Allocations)
            {
                if (projectedCache.TryGetValue(allocation.RetainerId, out var retainer))
                    ApplyProjectedDeposit(retainer, resolvedRequest, allocation.Quantity, resolvedMaxStackSize);
            }
        }
        return new(nowUtc, routes);
    }

    internal static int Held(CachedRetainer retainer, uint itemId, ItemQualityPolicy quality) =>
        retainer.Bags.Where(b => RetainerPageNames.Contains(b.BagName) || b.BagName == "RetainerCrystals")
            .SelectMany(b => b.Items).Where(i => i.ItemId == itemId && (quality == ItemQualityPolicy.Any ||
                i.IsHq == (quality == ItemQualityPolicy.HqOnly))).Sum(i => checked((int)i.Quantity));

    internal static void ApplyProjectedDeposit(
        CachedRetainer retainer,
        StowageDepositRequest request,
        int quantity,
        int maxStackSize)
    {
        var remaining = Math.Max(0, quantity);
        if (remaining == 0)
            return;
        if (ElementalCurrencyCatalog.IsElementalCurrency(request.ItemId))
        {
            var bag = retainer.Bags.FirstOrDefault(candidate => candidate.BagName == "RetainerCrystals");
            if (bag is null)
            {
                bag = new CachedBag { BagName = "RetainerCrystals", Location = "RetainerCrystals" };
                retainer.Bags.Add(bag);
            }
            var item = bag.Items.FirstOrDefault(candidate => candidate.ItemId == request.ItemId);
            if (item is null)
            {
                item = new CachedItem { ItemId = request.ItemId, ItemName = request.ItemName };
                bag.Items.Add(item);
            }
            item.Quantity = checked(item.Quantity + (uint)remaining);
            return;
        }

        var pages = retainer.Bags.Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal)).ToList();
        foreach (var item in pages
                     .SelectMany(page => page.Items)
                     .Where(item => item.ItemId == request.ItemId && item.IsHq == request.IsHighQuality && item.Quantity < maxStackSize))
        {
            var moved = Math.Min(remaining, maxStackSize - checked((int)item.Quantity));
            item.Quantity = checked(item.Quantity + (uint)moved);
            remaining -= moved;
            if (remaining == 0)
                return;
        }

        var targetPage = pages.FirstOrDefault();
        if (targetPage is null)
        {
            targetPage = new CachedBag { BagName = RetainerPageNames[0], Location = RetainerPageNames[0] };
            retainer.Bags.Add(targetPage);
        }
        while (remaining > 0)
        {
            var moved = Math.Min(remaining, maxStackSize);
            targetPage.Items.Add(new CachedItem
            {
                ItemId = request.ItemId,
                ItemName = request.ItemName,
                Quantity = checked((uint)moved),
                IsHq = request.IsHighQuality,
            });
            remaining -= moved;
        }
    }

    internal static CachedRetainer Copy(CachedRetainer source) => new()
    {
        RetainerId = source.RetainerId,
        RetainerName = source.RetainerName,
        Owner = source.Owner with { },
        IsCurrentlyAssigned = source.IsCurrentlyAssigned,
        DisplayOrder = source.DisplayOrder,
        IsUiAccessible = source.IsUiAccessible,
        UiAccessibilityObservedAtUtc = source.UiAccessibilityObservedAtUtc,
        IsGameAvailable = source.IsGameAvailable,
        ClassJobId = source.ClassJobId,
        Level = source.Level,
        MarketItemCount = source.MarketItemCount,
        RosterObservedAtUtc = source.RosterObservedAtUtc,
        ObservedAtUtc = source.ObservedAtUtc,
        Gil = source.Gil,
        GilObservedAtUtc = source.GilObservedAtUtc,
        ListingsObservedAtUtc = source.ListingsObservedAtUtc,
        RequestedSources = [.. source.RequestedSources],
        ObservedSources = [.. source.ObservedSources],
        Bags = source.Bags.Select(Copy).ToList(),
        Listings = [.. source.Listings],
    };

    private static CachedBag Copy(CachedBag source) => new()
    {
        BagName = source.BagName,
        Location = source.Location,
        ObservedAtUtc = source.ObservedAtUtc,
        Items = source.Items.Select(item => new CachedItem
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = item.ItemType,
            Quantity = item.Quantity,
            IsHq = item.IsHq,
            Condition = item.Condition,
            ConditionPercent = item.ConditionPercent,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            Equipped = item.Equipped,
        }).ToList(),
    };

    /// <summary>Empty retainer inventory slots, or null while any inventory page is unobserved.</summary>
    public static int? FreeInventorySlots(CachedRetainer retainer)
    {
        if (RetainerPageNames.Any(page => !retainer.ObservedSources.Contains(page, StringComparer.Ordinal)))
            return null;
        var used = retainer.Bags
            .Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
            .SelectMany(bag => bag.Items)
            .Count(item => item.ItemId > 0 && item.Quantity > 0);
        return Math.Max(0, 175 - used);
    }

    public static RetainerStowageCapacity Capacity(
        CachedRetainer retainer,
        uint itemId,
        bool isHighQuality,
        int maxStackSize)
    {
        if (ElementalCurrencyCatalog.IsElementalCurrency(itemId))
        {
            if (!retainer.ObservedSources.Contains("RetainerCrystals", StringComparer.Ordinal))
                return new(retainer.RetainerId, retainer.RetainerName, retainer.ObservedAtUtc, 0, 0);
            var current = retainer.Bags
                .Where(bag => bag.BagName == "RetainerCrystals")
                .SelectMany(bag => bag.Items)
                .Where(item => item.ItemId == itemId)
                .Sum(item => checked((int)item.Quantity));
            return new(
                retainer.RetainerId,
                retainer.RetainerName,
                retainer.ObservedAtUtc,
                Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - current),
                0);
        }

        if (RetainerPageNames.Any(page => !retainer.ObservedSources.Contains(page, StringComparer.Ordinal)))
            return new(retainer.RetainerId, retainer.RetainerName, retainer.ObservedAtUtc, 0, 0);

        var items = retainer.Bags
            .Where(bag => RetainerPageNames.Contains(bag.BagName, StringComparer.Ordinal))
            .SelectMany(bag => bag.Items)
            .Where(item => item.ItemId > 0 && item.Quantity > 0)
            .ToArray();
        var partial = items
            .Where(item => item.ItemId == itemId && item.IsHq == isHighQuality)
            .Sum(item => Math.Max(0, maxStackSize - checked((int)item.Quantity)));
        var emptySlots = Math.Max(0, 175 - items.Length);
        return new(
            retainer.RetainerId,
            retainer.RetainerName,
            retainer.ObservedAtUtc,
            partial,
            checked(emptySlots * maxStackSize));
    }
}
