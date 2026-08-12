using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using RQ.Domain;

namespace RQ.Planning;

public enum TransferVendorProcurementState
{
    Ready,
    ExactQualityUnsupported,
    OfferNotCataloged,
    VendorUnavailable,
}

public sealed record TransferVendorCandidate(
    GilVendorOffer Offer,
    GilVendorAccessAssessment Access);

public sealed record TransferVendorProcurementLine(
    Guid RuleId,
    uint ItemId,
    string ItemName,
    ItemQualityPolicy Quality,
    int TargetTotalQuantity,
    int ApprovedQuantity,
    TransferVendorProcurementState State,
    string Message,
    IReadOnlyList<TransferVendorCandidate> Candidates,
    TransferVendorCandidate? SelectedCandidate)
{
    public ulong MaximumGil => SelectedCandidate is null
        ? 0
        : checked((ulong)ApprovedQuantity * SelectedCandidate.Offer.UnitPriceGil);

    public bool IsReady => State == TransferVendorProcurementState.Ready &&
                           SelectedCandidate is not null &&
                           ApprovedQuantity > 0;
}

public sealed record TransferVendorStop(
    uint NpcId,
    uint ShopId,
    uint TerritoryId,
    string NpcName,
    IReadOnlyList<TransferVendorProcurementLine> Lines);

public sealed record TransferVendorProcurementReview(
    OwnerScope Owner,
    Guid PlanId,
    string PlanName,
    long PlanRevision,
    long RuntimeRevision,
    string ContextSignature,
    IReadOnlyList<TransferVendorProcurementLine> Lines,
    IReadOnlyList<TransferVendorStop> Stops)
{
    public IReadOnlyList<TransferVendorProcurementLine> ReadyLines => Lines.Where(line => line.IsReady).ToArray();
    public int ApprovedQuantity => ReadyLines.Sum(line => line.ApprovedQuantity);
    public ulong MaximumGil => ReadyLines.Aggregate(0UL, (sum, line) => checked(sum + line.MaximumGil));
    public bool CanStart => ApprovedQuantity > 0 && Stops.Count > 0;

    public GilVendorBuyPlan ToBuyPlan() => new()
    {
        MaximumApprovedGil = MaximumGil,
        Lines = ReadyLines.Select(line => new GilVendorBuyLineSnapshot
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ApprovedQuantity = line.ApprovedQuantity,
            TargetTotalQuantity = line.TargetTotalQuantity,
            UnitPriceGil = line.SelectedCandidate!.Offer.UnitPriceGil,
            ApprovedGilCeiling = line.MaximumGil,
            Offer = GilVendorBuyOfferSnapshot.From(line.SelectedCandidate.Offer),
            AlternativeOffers = line.Candidates
                .Where(candidate => candidate.Access.IsEligible && candidate != line.SelectedCandidate)
                .Select(candidate => GilVendorBuyOfferSnapshot.From(candidate.Offer))
                .ToList(),
        }).ToArray(),
        Stops = Stops.Select(stop => new GilVendorBuyStopSnapshot
        {
            NpcId = stop.NpcId,
            ShopId = stop.ShopId,
            TerritoryId = stop.TerritoryId,
            NpcName = stop.NpcName,
            ItemIds = stop.Lines.Select(line => line.ItemId).Distinct().ToList(),
        }).ToArray(),
    };
}

public sealed class TransferVendorProcurementPlanner
{
    private readonly GilVendorCatalog catalog;
    private readonly Func<GilVendorOffer, GilVendorAccessAssessment> assessAccess;

    public TransferVendorProcurementPlanner(
        GilVendorCatalog catalog,
        Func<GilVendorOffer, GilVendorAccessAssessment> assessAccess)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.assessAccess = assessAccess ?? throw new ArgumentNullException(nameof(assessAccess));
    }

    public TransferVendorProcurementReview Build(
        OwnerScope owner,
        StowagePlan plan,
        long runtimeRevision,
        IReadOnlyList<TargetPlanItem> effectiveRules,
        RetrievalPlan retrieval)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(effectiveRules);
        ArgumentNullException.ThrowIfNull(retrieval);
        if (!owner.HasStableIdentity || !plan.Owner.Matches(owner))
            throw new InvalidOperationException("Vendor procurement requires the current Transfer Plan owner.");

        var retrievalByRule = retrieval.Lines.ToDictionary(line => line.PlanItemId);
        var lines = effectiveRules
            .Where(rule => rule.Enabled && rule.AllowVendorPurchase)
            .Where(rule => retrievalByRule.GetValueOrDefault(rule.Id)?.MissingQuantity > 0)
            .Select(rule => BuildLine(rule, retrievalByRule[rule.Id]))
            .OrderByDescending(line => line.IsReady)
            .ThenBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.ItemId)
            .ToArray();
        var stops = lines
            .Where(line => line.IsReady)
            .GroupBy(line => new
            {
                line.SelectedCandidate!.Offer.NpcId,
                line.SelectedCandidate.Offer.ShopId,
                line.SelectedCandidate.Offer.TerritoryId,
                line.SelectedCandidate.Offer.NpcName,
            })
            .Select(group => new TransferVendorStop(
                group.Key.NpcId,
                group.Key.ShopId,
                group.Key.TerritoryId,
                group.Key.NpcName,
                group.OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(stop => stop.Lines.Count)
            .ThenBy(stop => stop.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stop => stop.NpcId)
            .ToArray();
        return new(
            owner with { },
            plan.Id,
            plan.Name,
            plan.Revision,
            runtimeRevision,
            BuildContextSignature(owner, plan, effectiveRules),
            lines,
            stops);
    }

    public static string BuildContextSignature(
        OwnerScope owner,
        StowagePlan plan,
        IEnumerable<TargetPlanItem> effectiveRules)
    {
        var canonical = string.Join(
            "|",
            effectiveRules
                .Where(rule => rule.Enabled && rule.AllowVendorPurchase)
                .OrderBy(rule => rule.Id)
                .Select(rule => $"{rule.Id:N}:{rule.ItemId}:{rule.Quality}:{rule.TargetQuantity}"));
        var value = $"{owner.LocalContentId}:{owner.HomeWorldId}:{plan.Id:N}:{plan.Revision}:{canonical}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private TransferVendorProcurementLine BuildLine(TargetPlanItem rule, PlanLine retrieval)
    {
        if (rule.Quality != ItemQualityPolicy.Any)
        {
            return new(
                rule.Id,
                rule.ItemId,
                rule.ItemName,
                rule.Quality,
                retrieval.TargetQuantity,
                retrieval.MissingQuantity,
                TransferVendorProcurementState.ExactQualityUnsupported,
                "Vendor purchasing currently requires Any quality so live target reconciliation cannot count the wrong quality.",
                [],
                null);
        }

        var candidates = catalog.FindOffers(rule.ItemId)
            .Select(offer => new TransferVendorCandidate(offer, assessAccess(offer)))
            .OrderByDescending(candidate => candidate.Access.State == GilVendorAccessState.Verified)
            .ThenByDescending(candidate => candidate.Access.State == GilVendorAccessState.Probeable)
            .ThenBy(candidate => candidate.Offer.UnitPriceGil)
            .ThenBy(candidate => candidate.Offer.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Offer.NpcId)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new(
                rule.Id,
                rule.ItemId,
                rule.ItemName,
                rule.Quality,
                retrieval.TargetQuantity,
                retrieval.MissingQuantity,
                TransferVendorProcurementState.OfferNotCataloged,
                "No executable ordinary-gil vendor offer is cataloged for this item.",
                [],
                null);
        }

        var selected = candidates.FirstOrDefault(candidate => candidate.Access.IsEligible);
        return selected is null
            ? new(
                rule.Id,
                rule.ItemId,
                rule.ItemName,
                rule.Quality,
                retrieval.TargetQuantity,
                retrieval.MissingQuantity,
                TransferVendorProcurementState.VendorUnavailable,
                candidates[0].Access.Message,
                candidates,
                null)
            : new(
                rule.Id,
                rule.ItemId,
                rule.ItemName,
                rule.Quality,
                retrieval.TargetQuantity,
                retrieval.MissingQuantity,
                TransferVendorProcurementState.Ready,
                $"Buy from {selected.Offer.NpcName} for {selected.Offer.UnitPriceGil:N0} gil each.",
                candidates,
                selected);
    }
}
