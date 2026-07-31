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
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StowagePlanId { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int TargetQuantity { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
    public StowageRoutingPolicy Routing { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public enum ItemQualityPolicy
{
    Any,
    NqOnly,
    HqOnly,
}

public enum StowageRoutingMode
{
    ConsolidateFirst,
    HomeFirst,
}

public enum StowageOverflowPolicy
{
    AnyOwnerRetainer,
    HoldOnPlayer,
}

public sealed class StowageRoutingPolicy
{
    public StowageRoutingMode Mode { get; set; } = StowageRoutingMode.ConsolidateFirst;
    public List<ulong> PreferredRetainerIds { get; set; } = [];
    public StowageOverflowPolicy Overflow { get; set; } = StowageOverflowPolicy.AnyOwnerRetainer;
}

public sealed class StowagePlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public OwnerScope Owner { get; set; } = new();
    public string Name { get; set; } = "General";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
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
}

public sealed class PendingCacheInvalidation
{
    public string OperationId { get; set; } = string.Empty;
    public ulong RetainerId { get; set; }
    public OwnerScope Owner { get; set; } = new();
}

public sealed class OperationLine
{
    public Guid? SourcePlanId { get; set; }
    public Guid? SourceRuleId { get; set; }
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHighQuality { get; set; }
    public ItemQualityPolicy Quality { get; set; } = ItemQualityPolicy.Any;
    public int TargetQuantity { get; set; }
    public int ShortageQuantity { get; set; }
    public int TransferredQuantity { get; set; }
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
}

public sealed class RetainerListingCaptureReceipt
{
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
    public string Schema { get; set; } = "gooseworks-quartermaster-state/v5";
    public long Revision { get; set; }
    public List<StowagePlan> StowagePlans { get; set; } = [];
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
}
