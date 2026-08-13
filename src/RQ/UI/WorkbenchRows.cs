using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using RQ.Automation;
using RQ.Domain;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

internal sealed record StockWorkbenchRow(
    StockGroup Item,
    int AccessibleRetainerQuantity,
    TargetPlanItem? Rule,
    StowageEvaluationLine? Line,
    IReadOnlyList<ListingPlanItemEvaluation> ListingDemand);

internal sealed record StockWorkbenchProjection(
    long RuntimeRevision,
    Guid? PlanId,
    IReadOnlyList<StockGroup> QueryItems,
    IReadOnlyList<StockWorkbenchRow> Rows);

internal sealed record TransferWorkbenchProjection(
    long RuntimeRevision,
    Guid PlanId,
    IReadOnlyList<TargetPlanItem> Rules,
    StowageEvaluation? Stowage,
    RetrievalPlan Retrieval,
    TransferVendorProcurementReview Vendor,
    StowageDepositBatch Deposit,
    int Movements,
    bool HasMovement,
    bool HasUnknownListingDemand,
    IReadOnlyList<TransferWorkbenchRow> Rows);

internal sealed record PendingTransferPlanRecovery(Guid PlanId, string RefreshRunId);

internal sealed record RestockPlanRow(
    RestockPlanItem Item,
    PlanLine? Line,
    Guid PlanId,
    OwnerScope Owner);

internal sealed record TransferWorkbenchRow(
    TargetPlanItem Rule,
    StowageEvaluationLine? Line,
    PlanLine? RetrievalLine,
    TransferVendorProcurementLine? VendorLine,
    int RoutedDepositQuantity,
    int PlayerQuantity,
    int AccessibleStorageQuantity,
    int Difference,
    FieldEvidence<int> ListingContribution,
    TransferPlanListingLink? ListingLink,
    OwnerScope Owner,
    Guid PlanId,
    QuartermasterRuntimeSnapshot Runtime);

internal sealed record StowageDraftRow(
    TargetPlanItem Rule,
    QuartermasterRuntimeSnapshot Runtime);

internal sealed record TransferReviewRow(
    TargetPlanItem Rule,
    StowageEvaluationLine? Line,
    int PlayerQuantity,
    int Difference,
    FieldEvidence<int> ListingContribution,
    QuartermasterRuntimeSnapshot Runtime);

internal sealed record TransferReviewRequest(Guid PlanId, string PlanName);

internal sealed record ListingGroupView(
    ListingItemKey Key,
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int DesiredUnits,
    FieldEvidence<int> ListedUnits,
    FieldEvidence<int> NeedUnits,
    int PlayerUnits,
    FieldEvidence<int> RetainerUnits,
    FieldEvidence<int> ImmediatelyListableUnits,
    FieldEvidence<int> MovementNeedUnits,
    FieldEvidence<int> OtherRetainerUnits,
    FieldEvidence<int> RetrievableUnits,
    FieldEvidence<int> MissingUnits,
    ListingCoverageState Coverage,
    IReadOnlyList<ListingAssignmentEvaluation> Assignments,
    IReadOnlyList<ListingRow> Listings,
    IReadOnlyList<ListingRow> UnmanagedListings);

internal sealed record PhysicalListingGroupView(
    ulong RetainerId,
    string RetainerName,
    int Quantity,
    FfxivItemQuality Quality,
    FieldEvidence<decimal> UnitPrice,
    IReadOnlyList<ListingRow> Listings);

internal sealed record ItemChoice(uint ItemId, string Name, string Label);
