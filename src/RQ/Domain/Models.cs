using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RQ.Domain;

public sealed record OwnerScope
{
    public ulong? LocalContentId { get; init; }
    public uint? HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;

    [JsonIgnore]
    public bool HasStableIdentity => LocalContentId is > 0 && HomeWorldId is > 0;

    public bool Matches(OwnerScope? other)
    {
        if (other is null)
            return false;
        if (LocalContentId is > 0 && HomeWorldId is > 0 && other.LocalContentId is > 0 && other.HomeWorldId is > 0)
            return LocalContentId == other.LocalContentId && HomeWorldId == other.HomeWorldId;
        return !string.IsNullOrWhiteSpace(CharacterName) && !string.IsNullOrWhiteSpace(HomeWorldName) &&
               string.Equals(CharacterName, other.CharacterName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(HomeWorldName, other.HomeWorldName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public OwnerScope Owner { get; set; } = new();
    public bool? IsCurrentlyAssigned { get; set; }
    public int? DisplayOrder { get; set; }
    public bool? IsUiAccessible { get; set; }
    public DateTime? UiAccessibilityObservedAtUtc { get; set; }
    public bool? IsGameAvailable { get; set; }
    public byte? ClassJobId { get; set; }
    public byte? Level { get; set; }
    public byte? MarketItemCount { get; set; }
    public DateTime? RosterObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public ulong Gil { get; set; }
    public DateTime? GilObservedAtUtc { get; set; }
    public DateTime? ListingsObservedAtUtc { get; set; }
    public List<string> RequestedSources { get; set; } = [];
    public List<string> ObservedSources { get; set; } = [];
    public List<CachedBag> Bags { get; set; } = [];
    public List<CachedMarketListing> Listings { get; set; } = [];
}

public sealed class CachedBag
{
    public string BagName { get; set; } = string.Empty;
    public string? Location { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public List<CachedItem> Items { get; set; } = [];
}

public sealed class CachedItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHq { get; set; }
    public float Condition { get; set; }
    public float? ConditionPercent { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public bool? Equipped { get; set; }
}

public sealed class CachedMarketListing
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHq { get; set; }
    public float Condition { get; set; }
    public float? ConditionPercent { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public uint? UnitPrice { get; set; }
    public DateTime? ListedAtUtc { get; set; }
}

public sealed class InventoryBag
{
    public string BagName { get; set; } = string.Empty;
    public string? Location { get; set; }
    public List<InventoryItem> Items { get; set; } = [];
}

public sealed class InventoryItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint Quantity { get; set; }
    public bool IsHq { get; set; }
    public string? ItemType { get; set; }
    public float Condition { get; set; }
    public float? ConditionPercent { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public bool? Equipped { get; set; }
}

public sealed class CachedPlayerInventory
{
    public OwnerScope Owner { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; }
    public List<string> RequestedSources { get; set; } = [];
    public List<string> ObservedSources { get; set; } = [];
    public List<CachedPlayerBag> Bags { get; set; } = [];
}

public sealed class CachedPlayerBag
{
    public string BagName { get; set; } = string.Empty;
    public string? Location { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public List<InventoryItem> Items { get; set; } = [];
}

public sealed class TargetPlanItem
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Courier.CourierLine? Courier { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StowagePlanId { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int TargetQuantity { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
    public StowageRoutingPolicy Routing { get; set; } = new();
    public bool AllowVendorPurchase { get; set; }
    /// <summary>Preferred vendor NPC for purchases; null means cheapest reachable.</summary>
    public uint? PreferredVendorNpcId { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public enum ItemQualityPolicy
{
    Any,
    NqOnly,
    HqOnly,
}

/// <summary>
/// Where surplus of one item is deposited: the listed retainers in order, then, only when
/// <see cref="AnySpace"/> is set, any other owner retainer with room. Anything left stays
/// in the player's bags. An empty list without <see cref="AnySpace"/> deposits nothing.
/// </summary>
public sealed class StowageRoutingPolicy : IJsonOnDeserialized
{
    private bool anySpace;
    private bool anySpaceRead;
    private bool legacyShapeRead;
    private bool? legacyOverflowAnywhere;

    public List<ulong> PreferredRetainerIds { get; set; } = [];

    public bool Consolidate { get; set; }

    public bool AnySpace
    {
        get => anySpace;
        set
        {
            anySpace = value;
            anySpaceRead = true;
        }
    }

    /// <summary>Pre-matrix placement mode. Read once while loading old data; never written.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement? LegacyMode
    {
        get => null;
        set => legacyShapeRead |= value is not null;
    }

    /// <summary>Pre-matrix overflow policy (0/"AnyOwnerRetainer", 1/"HoldOnPlayer"). Read once while loading old data; never written.</summary>
    [JsonPropertyName("overflow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement? LegacyOverflow
    {
        get => null;
        set
        {
            if (value is not { } element)
                return;
            legacyShapeRead = true;
            legacyOverflowAnywhere = element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt32(out var number) => number != 1,
                JsonValueKind.String => !string.Equals(element.GetString(), "HoldOnPlayer", StringComparison.OrdinalIgnoreCase),
                _ => null,
            };
        }
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Old shapes carried the fallback in Overflow, whose default was AnyOwnerRetainer.
        if (legacyShapeRead && !anySpaceRead)
            anySpace = legacyOverflowAnywhere ?? true;
        PreferredRetainerIds ??= [];
        legacyShapeRead = false;
        legacyOverflowAnywhere = null;
    }

    public StowageRoutingPolicy Copy() => new()
    {
        PreferredRetainerIds = PreferredRetainerIds.Distinct().ToList(),
        AnySpace = AnySpace,
        Consolidate = Consolidate,
    };

    public bool SameAs(StowageRoutingPolicy? other) =>
        other is not null && Consolidate == other.Consolidate && AnySpace == other.AnySpace && PreferredRetainerIds.SequenceEqual(other.PreferredRetainerIds);

    public static StowageRoutingPolicy CopyOf(StowageRoutingPolicy? source) => source?.Copy() ?? new();
}

/// <summary>
/// What a plan's lines mean. A Transfer plan sets same-character targets (keep in bags, stow to
/// retainers); a Courier plan sends explicit quantities between characters. A plan never mixes them.
/// </summary>
public enum StowagePlanKind
{
    Transfer = 0,
    Courier = 1,
}

public sealed class StowagePlan
{
    public bool IsEphemeral { get; set; }
    /// <summary>Absent in documents written before courier plans, which deserialize as Transfer.</summary>
    public StowagePlanKind Kind { get; set; } = StowagePlanKind.Transfer;
    public Guid? LastAppliedCourierRun { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public OwnerScope Owner { get; set; } = new();
    public string Name { get; set; } = "General";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
}

public sealed class ListingPlan : IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public OwnerScope Owner { get; set; } = new();
    public List<ListingPlanAssignment> Assignments { get; set; } = [];
    public List<ListingReserveRule> Reserves { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Rules written before ranked cross-character homes are read once into <see cref="ListingReserveRule.Homes"/>.</summary>
    void IJsonOnDeserialized.OnDeserialized() => MigrateLegacyHomes();

    /// <summary>Moves every rule's legacy Location/RemoteHomes into Homes. Idempotent; true when any rule changed.</summary>
    public bool MigrateLegacyHomes()
    {
        Reserves ??= [];
        var changed = false;
        foreach (var reserve in Reserves)
            changed |= reserve.MigrateLegacyHomes(Owner);
        return changed;
    }
}

/// <summary>One ranked deposition home retainer on any character of the service account, with an optional stock cap.</summary>
public sealed record PlacementHome(
    OwnerScope Character,
    ulong RetainerId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxUnits = null)
{
    /// <summary>The same home: the same retainer on the same character (by content id when both carry one).</summary>
    public bool SameHome(OwnerScope character, ulong retainerId) => RetainerId == retainerId && SameCharacter(Character, character);

    /// <summary>
    /// The same character for placement: by content id when both carry one (a world transfer keeps it, so homes stored
    /// under the old world stay with the character), else by name and world.
    /// </summary>
    public static bool SameCharacter(OwnerScope left, OwnerScope right) =>
        left.LocalContentId is > 0 && right.LocalContentId is > 0
            ? left.LocalContentId == right.LocalContentId
            : left.Matches(right);

    public PlacementHome Copy() => new(Character with { }, RetainerId, MaxUnits);
}

/// <summary>Spare stock of one exact-quality item the market run keeps on hand beyond its listings.</summary>
public sealed class ListingReserveRule
{
    public const int MaxReserveUnits = 999_999;

    public Guid Id { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.NqOnly;
    public int ReserveUnits { get; set; }
    /// <summary>
    /// The item's deposition homes: one ranked chain across every character of the service account (rank = index + 1).
    /// Each home is an explicit retainer with an optional stock cap; arriving units fill rank 1 up to its cap and room,
    /// then spill down the chain. What no home takes stays in the owner's bags. There is no "any space" home.
    /// </summary>
    public List<PlacementHome> Homes { get; set; } = [];
    /// <summary>Owner-side stowage consolidation, carried from the legacy location.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Consolidate { get; set; }
    /// <summary>Legacy ranked owner homes (and Any space). Read once on load into <see cref="Homes"/>; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public StowageRoutingPolicy? Location { get; set; }
    /// <summary>Legacy ranked homes on one other character. Read once on load into <see cref="Homes"/>; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReserveRemoteHomes? RemoteHomes { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the rule names any home, here or on another character.</summary>
    [JsonIgnore]
    public bool HasAnyHome => Homes.Count > 0;

    /// <summary>
    /// Reads legacy <see cref="Location"/> (the owner's ranked retainers) then <see cref="RemoteHomes"/> into
    /// <see cref="Homes"/> uncapped, dropping Any space and duplicates, then clears both so only Homes is written.
    /// Homes already present win: legacy fields are then only cleared. Idempotent; true when anything changed.
    /// </summary>
    public bool MigrateLegacyHomes(OwnerScope owner)
    {
        Homes ??= [];
        if (Location is null && RemoteHomes is null)
            return false;
        if (Homes.Count == 0)
        {
            void Add(OwnerScope character, ulong retainerId)
            {
                if (retainerId == 0 || Homes.Any(home => home.SameHome(character, retainerId)))
                    return;
                Homes.Add(new(character with { }, retainerId));
            }
            foreach (var id in Location?.PreferredRetainerIds ?? [])
                Add(owner, id);
            if (RemoteHomes is { } remote)
                foreach (var id in remote.RetainerIds ?? [])
                    Add(remote.Character ?? new OwnerScope(), id);
        }
        Consolidate |= Location?.Consolidate == true;
        Location = null;
        RemoteHomes = null;
        return true;
    }
}

/// <summary>Legacy (read-once) ranked home retainers on another character than the plan owner.</summary>
public sealed class ReserveRemoteHomes
{
    public OwnerScope Character { get; set; } = new();
    public List<ulong> RetainerIds { get; set; } = [];

    public ReserveRemoteHomes Copy() => new() { Character = Character with { }, RetainerIds = RetainerIds.Distinct().ToList() };

    public static ReserveRemoteHomes? CopyOf(ReserveRemoteHomes? source) =>
        source is { RetainerIds.Count: > 0 } ? source.Copy() : null;

    public static bool Same(ReserveRemoteHomes? left, ReserveRemoteHomes? right)
    {
        var a = left is { RetainerIds.Count: > 0 } ? left : null;
        var b = right is { RetainerIds.Count: > 0 } ? right : null;
        if (a is null || b is null)
            return a is null && b is null;
        return a.Character.Matches(b.Character) && a.RetainerIds.SequenceEqual(b.RetainerIds);
    }
}

public sealed class ListingPlanAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.NqOnly;
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public int ListingCount { get; set; } = 1;
    public int QuantityPerListing { get; set; } = 1;
    public int UnitPrice { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public int DesiredUnits => ListingCount <= 0 || QuantityPerListing <= 0
        ? 0
        : ListingCount > int.MaxValue / QuantityPerListing
            ? int.MaxValue
            : ListingCount * QuantityPerListing;
}

public sealed class TransferPlanListingLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StowagePlanId { get; set; }
    public Guid ListingPlanId { get; set; }
    public uint ItemId { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.NqOnly;
}

public sealed class StowageMigrationRecord
{
    public string MigrationId { get; set; } = "target-plan-to-stowage-v1";
    public Guid PlanId { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public int RuleCount { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class TransferPlanMigrationRecord
{
    public string MigrationId { get; set; } = "restock-plans-to-transfer-plans-v1";
    public Guid SourceRestockPlanId { get; set; }
    public Guid TransferPlanId { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public int RuleCount { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class RestockPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public OwnerScope Owner { get; set; } = new();
    public string Name { get; set; } = "Restock plan";
    public bool Enabled { get; set; } = true;
    public List<RestockPlanItem> Items { get; set; } = [];
}

public sealed class RestockPlanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int TargetQuantity { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
    public string Notes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class ItemGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public string Name { get; set; } = "Item group";
    public List<ItemGroupItem> Items { get; set; } = [];
}

public sealed class ItemGroupItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
}

public static class OperationStatuses
{
    public const string Queued = "queued";
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string PartiallySucceeded = "partially_succeeded";
    public const string Indeterminate = "indeterminate";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";

    public static bool IsTerminal(string status) => status is Succeeded or PartiallySucceeded or Indeterminate or Failed or Cancelled or Rejected;
}

public static class OperationKinds
{
    public const string Placement = "PlacementMove";
    public const string Retrieval = "retrieval";
    public const string Deposit = "deposit";
    public const string QuickDeposit = "quick_deposit";
    public const string StowageSurplus = "stowage_surplus";
}

public sealed class SubmittedRequestRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string CanonicalHash { get; set; } = string.Empty;
    public DateTime AcceptedAtUtc { get; set; }
}

public sealed class OperationRecord
{
    public string OperationId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string Kind { get; set; } = OperationKinds.Retrieval;
    public bool ExecuteImmediately { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public string Status { get; set; } = OperationStatuses.Accepted;
    public long Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string Message { get; set; } = string.Empty;
    public Guid? SourcePlanId { get; set; }
    public long? SourcePlanRevision { get; set; }
    public string? SourcePlanName { get; set; }
    public List<TargetPlanItem> SourcePlanItems { get; set; } = [];
    public List<OperationLine> Lines { get; set; } = [];
    public List<DepositCandidateAuthorization> DepositCandidates { get; set; } = [];
}

public sealed class DepositCandidateAuthorization
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public Dictionary<uint, int> CapacityByItem { get; set; } = [];
    public Dictionary<string, int> CapacityByVariant { get; set; } = [];
    public Dictionary<string, int> PriorityByVariant { get; set; } = [];
    public bool UsesLiveCapacity { get; set; }
}

public sealed class PendingCacheInvalidation
{
    public string OperationId { get; set; } = string.Empty;
    public ulong RetainerId { get; set; }
    public OwnerScope Owner { get; set; } = new();
}

public sealed class OperationLine
{
    public string AuthorizationKey { get; set; } = string.Empty;
    public Guid? SourcePlanId { get; set; }
    public Guid? SourceRuleId { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHighQuality { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
    public int TargetQuantity { get; set; }
    public int ShortageQuantity { get; set; }
    public int TransferredQuantity { get; set; }
    public int MaxStackSize { get; set; } = 999;
    public StowageRoutingPolicy Routing { get; set; } = new();
    /// <summary>
    /// Retrieval may draw this line only from these owner retainers (and the saddlebag only when
    /// <see cref="SourceSaddlebag"/>). Null: any owner retainer and the saddlebag.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ulong>? SourceRetainerIds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SourceSaddlebag { get; set; }

    [JsonIgnore]
    public bool IsSourceRestricted => SourceRetainerIds is not null;
}

public sealed class OperationReceipt
{
    public string OperationId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public uint? ItemId { get; set; }
    public ulong? RetainerId { get; set; }
    public int? Quantity { get; set; }
    public string? AuthorizationKey { get; set; }
}

public sealed class RetainerListingCaptureReceipt
{
    public const string ChangedListingsV1 = "changed-listings/v1";

    public string Semantics { get; set; } = ChangedListingsV1;
    public bool ComparisonAvailable { get; set; }
    public string CaptureId { get; set; } = string.Empty;
    public ulong RetainerId { get; set; }
    public OwnerScope Owner { get; set; } = new();
    public DateTime CapturedAtUtc { get; set; }
    public List<RetainerListingCaptureItem> Items { get; set; } = [];
}

public sealed class RetainerListingCaptureItem
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
}

public sealed class QuartermasterState
{
    public Courier.CourierSettings Courier { get; set; } = new();
    public string Schema { get; set; } = "gooseworks-quartermaster-state/v5";
    public long Revision { get; set; }
    public List<StowagePlan> StowagePlans { get; set; } = [];
    public List<ListingPlan> ListingPlans { get; set; } = [];
    public List<TransferPlanListingLink> TransferPlanListingLinks { get; set; } = [];
    public List<StowageMigrationRecord> StowageMigrations { get; set; } = [];
    public List<TransferPlanMigrationRecord> TransferPlanMigrations { get; set; } = [];
    public List<TargetPlanItem> PlanItems { get; set; } = [];
    public List<RestockPlan> RestockPlans { get; set; } = [];
    public List<ItemGroup> ItemGroups { get; set; } = [];
    public List<SubmittedRequestRecord> Requests { get; set; } = [];
    public List<OperationRecord> Operations { get; set; } = [];
    public List<OperationReceipt> Receipts { get; set; } = [];
    public List<PendingCacheInvalidation> PendingCacheInvalidations { get; set; } = [];
    public RetainerListingCaptureReceipt? LatestRetainerListingCapture { get; set; }
    public RetainerRefreshRecoveryState? RetainerRefreshRecovery { get; set; }
    public TransferPlanRecoveryState? TransferPlanRecovery { get; set; }
    public OperationJournalMigrationRecord? OperationJournalMigration { get; set; }
}

public sealed class TransferPlanRecoveryState
{
    public OwnerScope Owner { get; set; } = new();
    public Guid PlanId { get; set; }
    public long PlanRevision { get; set; }
    public string RefreshRunId { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public string FailureMessage { get; set; } = string.Empty;
}

public sealed class RetainerRefreshRecoveryState
{
    public string RunId { get; set; } = string.Empty;
    public OwnerScope Owner { get; set; } = new();
    public List<ulong> PendingRetainerIds { get; set; } = [];
    public DateTime FailedAtUtc { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class OperationJournalMigrationRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string JournalId { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int OperationCount { get; set; }
    public int ReceiptCount { get; set; }
    public int PendingInvalidationCount { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}
