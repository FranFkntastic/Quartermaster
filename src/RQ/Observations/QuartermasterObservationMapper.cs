using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Observations.V1;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Observations;

internal static class QuartermasterObservationMapper
{
    public static ObservationEnvelope PlayerInventory(
        OwnerScope owner,
        PlayerStorageCapture capture,
        long sourceRevision,
        DateTime observedAtUtc,
        ObservationProvenance provenance)
    {
        var sharedOwner = ToOwner(owner);
        var requestedSources = capture.RequestedSources.Distinct(StringComparer.Ordinal).ToArray();
        var observedSources = capture.ObservedSources.Distinct(StringComparer.Ordinal).ToArray();
        var requested = ParseContainers(requestedSources);
        var observed = ParseContainers(observedSources);
        var rows = capture.Bags
            .SelectMany(bag => bag.Items.Select(item => ToInventoryRow(bag.BagName, item)))
            .ToArray();
        var complete = requested.Count == requestedSources.Length &&
                       observed.Count == observedSources.Length &&
                       requested.Count > 0 &&
                       requested.All(observed.Contains) &&
                       rows.All(row => row is not null);
        var evidence = ObservationEvidence.CompleteAvailable with
        {
            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial,
        };
        var payload = new InventoryObservationPayload(
            requested.Order().ToArray(),
            observed.Order().ToArray(),
            rows.OfType<InventoryItemObservation>().ToArray());
        return Envelope(
            new ObservationScope(sharedOwner, ObservationSubject.Character(sharedOwner), ObservationContainerKind.PlayerInventory),
            sourceRevision,
            observedAtUtc,
            provenance,
            evidence,
            ObservationPayload.Create(ObservationPayloadContracts.PlayerInventory, 1, payload));
    }

    public static ObservationEnvelope RetainerInventory(
        CachedRetainer retainer,
        long sourceRevision,
        ObservationProvenance provenance)
    {
        var owner = ToOwner(retainer.Owner);
        var market = InventoryType.RetainerMarket.ToString();
        var gil = InventoryType.RetainerGil.ToString();
        var requestedSources = retainer.RequestedSources.Where(source => source != market && source != gil).Distinct(StringComparer.Ordinal).ToArray();
        var observedSources = retainer.ObservedSources.Where(source => source != market && source != gil).Distinct(StringComparer.Ordinal).ToArray();
        var requested = ParseContainers(requestedSources);
        var observed = ParseContainers(observedSources);
        var rows = retainer.Bags
            .Where(bag => bag.BagName != market && bag.BagName != gil)
            .SelectMany(bag => bag.Items.Select(item => ToInventoryRow(bag.BagName, item)))
            .ToArray();
        var complete = requested.Count == requestedSources.Length &&
                       observed.Count == observedSources.Length &&
                       requested.Count > 0 &&
                       requested.All(observed.Contains) &&
                       rows.All(row => row is not null);
        var evidence = ObservationEvidence.CompleteAvailable with
        {
            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial,
        };
        var payload = new InventoryObservationPayload(
            requested.Order().ToArray(),
            observed.Order().ToArray(),
            rows.OfType<InventoryItemObservation>().ToArray());
        return Envelope(
            new ObservationScope(owner, ObservationSubject.Retainer(retainer.RetainerId, owner), ObservationContainerKind.RetainerInventory),
            sourceRevision,
            retainer.ObservedAtUtc,
            provenance,
            evidence,
            ObservationPayload.Create(ObservationPayloadContracts.RetainerInventory, 1, payload));
    }

    public static ObservationEnvelope RetainerListings(
        CachedRetainer retainer,
        DateTime observedAtUtc,
        long sourceRevision,
        ObservationProvenance provenance)
    {
        var owner = ToOwner(retainer.Owner);
        var rows = retainer.Listings.Select(ToListingRow).ToArray();
        var complete = rows.All(row => row is not null);
        var evidence = ObservationEvidence.CompleteAvailable with
        {
            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial,
        };
        return Envelope(
            new ObservationScope(owner, ObservationSubject.Retainer(retainer.RetainerId, owner), ObservationContainerKind.RetainerMarketListings),
            sourceRevision,
            observedAtUtc,
            provenance,
            evidence,
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerMarketListings,
                1,
                new RetainerMarketListingsPayload(rows.OfType<RetainerMarketListingObservation>().ToArray())));
    }

    private static ObservationEnvelope Envelope(
        ObservationScope scope,
        long sourceRevision,
        DateTime observedAtUtc,
        ObservationProvenance provenance,
        ObservationEvidence evidence,
        ObservationPayload payload) =>
        new(
            scope,
            new ObservationCapture(
                sourceRevision,
                ToUtc(observedAtUtc),
                provenance,
                evidence),
            payload);

    private static ObservationOwner ToOwner(OwnerScope owner)
    {
        if (owner.LocalContentId is not > 0 || owner.HomeWorldId is not > 0)
            throw new InvalidOperationException("Quartermaster cannot shadow an observation without exact numeric owner identity.");
        return new ObservationOwner(owner.LocalContentId.Value, owner.HomeWorldId.Value);
    }

    private static HashSet<int> ParseContainers(IEnumerable<string> sources) =>
        sources
            .Select(source => Enum.TryParse<InventoryType>(source, out var type) ? (int?)type : null)
            .OfType<int>()
            .ToHashSet();

    private static InventoryItemObservation? ToInventoryRow(string bagName, RQ.Domain.InventoryItem item)
    {
        if (item.ItemId == 0 || item.Quantity == 0 || item.SlotIndex is not >= 0 ||
            !Enum.TryParse<InventoryType>(item.ContainerKey ?? bagName, out var container))
            return null;
        return new InventoryItemObservation(
            (int)container,
            item.SlotIndex.Value,
            item.ItemId,
            checked((int)item.Quantity),
            item.IsHq);
    }

    private static InventoryItemObservation? ToInventoryRow(string bagName, CachedItem item)
    {
        if (item.ItemId == 0 || item.Quantity == 0 || item.SlotIndex is not >= 0 ||
            !Enum.TryParse<InventoryType>(item.ContainerKey ?? bagName, out var container))
            return null;
        return new InventoryItemObservation(
            (int)container,
            item.SlotIndex.Value,
            item.ItemId,
            checked((int)item.Quantity),
            item.IsHq);
    }

    private static RetainerMarketListingObservation? ToListingRow(CachedMarketListing listing)
    {
        if (listing.ItemId == 0 || listing.Quantity == 0 || listing.SlotIndex is not >= 0 || listing.UnitPrice is null)
            return null;
        return new RetainerMarketListingObservation(
            listing.SlotIndex.Value,
            listing.ItemId,
            checked((int)listing.Quantity),
            checked((int)listing.UnitPrice.Value),
            listing.IsHq);
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
