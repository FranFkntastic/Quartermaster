using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using RQ.Domain;

namespace RQ.Inventory;

public enum CaptureOutcome { Persisted, Incomplete, IdentityMismatch, OwnerMismatch, InvalidSession, PersistenceFailed, Failed }
public sealed record CaptureReceipt(long Revision, ulong RetainerId, CaptureOutcome Outcome, string Message, DateTime OccurredAtUtc);
public sealed record CaptureSession(ulong RetainerId, string RetainerName, OwnerScope Owner);
public sealed record CaptureWaitSnapshot(CaptureSession? Session, long Checkpoint);

public sealed class RetainerCaptureService : IDisposable
{
    private const string LargeAddon = "InventoryRetainerLarge";
    private const string SmallAddon = "InventoryRetainer";
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly InventoryScanner scanner;
    private readonly RetainerCacheRepository cache;
    private readonly Func<OwnerScope> currentOwner;
    private CaptureSession? session;
    private long receiptRevision;
    private readonly List<CaptureReceipt> receipts = [];
    private bool registered;
    private DateTime nextPassiveListingScanAt;
    private ulong passiveRetainerId;
    private string? passiveListingFingerprint;
    private ulong candidateRetainerId;
    private string? candidateListingFingerprint;
    private DateTime candidateListingObservedAt;

    public RetainerCaptureService(IAddonLifecycle lifecycle, IPluginLog log, InventoryScanner scanner, RetainerCacheRepository cache, Func<OwnerScope> currentOwner)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        this.scanner = scanner;
        this.cache = cache;
        this.currentOwner = currentOwner;
    }

    public void Register()
    {
        if (registered)
            return;
        registered = true;
        try
        {
            lifecycle.RegisterListener(AddonEvent.PostSetup, LargeAddon, Opened);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, LargeAddon, Closing);
            lifecycle.RegisterListener(AddonEvent.PostSetup, SmallAddon, Opened);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, SmallAddon, Closing);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event Action<CaptureReceipt>? CaptureCompleted;
    public CaptureSession? ActiveSession => session;
    public long Checkpoint => receiptRevision;
    public IReadOnlyList<CaptureReceipt> ReceiptsAfter(long checkpoint) => receipts.Where(receipt => receipt.Revision > checkpoint).ToArray();
    public CaptureWaitSnapshot GetWaitSnapshot() => new(session, receiptRevision);

    public void TickPassive()
    {
        var now = DateTime.UtcNow;
        if (now < nextPassiveListingScanAt)
            return;
        nextPassiveListingScanAt = now.AddMilliseconds(250);

        try
        {
            var identity = ReadActiveRetainer();
            if (identity is null)
            {
                passiveRetainerId = 0;
                passiveListingFingerprint = null;
                candidateRetainerId = 0;
                candidateListingFingerprint = null;
                return;
            }

            var owner = currentOwner();
            if (!owner.HasStableIdentity)
                return;
            var listings = scanner.CaptureRetainerListings();
            if (listings is null)
                return;
            var fingerprint = ListingFingerprint(listings);
            if (passiveRetainerId == identity.Value.Id &&
                string.Equals(passiveListingFingerprint, fingerprint, StringComparison.Ordinal))
                return;
            if (candidateRetainerId != identity.Value.Id ||
                !string.Equals(candidateListingFingerprint, fingerprint, StringComparison.Ordinal))
            {
                candidateRetainerId = identity.Value.Id;
                candidateListingFingerprint = fingerprint;
                candidateListingObservedAt = now;
                return;
            }
            if (now - candidateListingObservedAt < TimeSpan.FromMilliseconds(500))
                return;

            cache.ReplaceListings(new RetainerListingsObservation(
                identity.Value.Id,
                identity.Value.Name,
                owner,
                now,
                listings));
            passiveRetainerId = identity.Value.Id;
            passiveListingFingerprint = fingerprint;
            candidateRetainerId = 0;
            candidateListingFingerprint = null;
        }
        catch (Exception exception)
        {
            log.Error(exception, "Quartermaster failed to passively capture retainer listings.");
        }
    }

    internal static string ListingFingerprint(IReadOnlyList<CachedMarketListing> listings) =>
        string.Join("|", listings
            .OrderBy(listing => listing.SlotIndex)
            .Select(listing =>
                $"{listing.SlotIndex}:{listing.ItemId}:{listing.Quantity}:{listing.IsHq}:{listing.UnitPrice?.ToString() ?? "?"}"));

    private void Opened(AddonEvent _, AddonArgs __)
    {
        try
        {
            var identity = ReadActiveRetainer();
            session = identity is null ? null : new(identity.Value.Id, identity.Value.Name, currentOwner());
            if (session is null)
                Publish(0, CaptureOutcome.InvalidSession, "Retainer inventory opened without a stable active retainer identity.");
        }
        catch (Exception exception)
        {
            session = null;
            log.Error(exception, "Quartermaster failed to establish retainer capture session.");
        }
    }

    private void Closing(AddonEvent _, AddonArgs __)
    {
        var active = session;
        try
        {
            if (active is null)
            {
                Publish(0, CaptureOutcome.InvalidSession, "Retainer inventory closed without a stable open session.");
                return;
            }
            var closeIdentity = ReadActiveRetainer();
            if (closeIdentity is null || closeIdentity.Value.Id != active.RetainerId)
            {
                Publish(active.RetainerId, CaptureOutcome.IdentityMismatch, "Active retainer identity changed before capture.");
                return;
            }
            if (!active.Owner.Matches(currentOwner()))
            {
                Publish(active.RetainerId, CaptureOutcome.OwnerMismatch, "Owner scope changed while retainer inventory was open.");
                return;
            }
            var capture = scanner.CaptureRetainer();
            var missing = InventoryScanner.RequiredRetainerContainers.Where(container => !capture.LoadedContainers.Contains(container)).ToArray();
            if (missing.Length > 0)
            {
                Publish(active.RetainerId, CaptureOutcome.Incomplete, $"Required retainer pages were not loaded: {string.Join(", ", missing)}.");
                return;
            }
            var previous = cache.Snapshot().GetValueOrDefault(active.RetainerId);
            var observedAtUtc = DateTime.UtcNow;
            var bags = capture.Bags.ToList();
            foreach (var bag in bags)
                bag.ObservedAtUtc = observedAtUtc;
            if (!capture.LoadedContainers.Contains(InventoryType.RetainerCrystals) &&
                previous?.Bags.FirstOrDefault(bag => bag.BagName == InventoryType.RetainerCrystals.ToString()) is { } previousCrystals)
            {
                bags.Add(previousCrystals);
            }
            cache.Upsert(new CachedRetainer
            {
                RetainerId = active.RetainerId,
                RetainerName = active.RetainerName,
                Owner = active.Owner,
                ObservedAtUtc = observedAtUtc,
                Gil = capture.Gil ?? previous?.Gil ?? 0,
                GilObservedAtUtc = capture.Gil.HasValue ? observedAtUtc : previous?.GilObservedAtUtc,
                ListingsObservedAtUtc = capture.Listings is not null ? observedAtUtc : previous?.ListingsObservedAtUtc,
                RequestedSources = InventoryScanner.RetainerContainers
                    .Concat([InventoryType.RetainerGil, InventoryType.RetainerMarket])
                    .Select(container => container.ToString())
                    .ToList(),
                ObservedSources = capture.LoadedContainers.Select(container => container.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(source => source, StringComparer.Ordinal)
                    .ToList(),
                Bags = bags,
                Listings = capture.Listings?.ToList() ?? previous?.Listings ?? [],
            });
            Publish(active.RetainerId, CaptureOutcome.Persisted, $"Captured {active.RetainerName}.");
        }
        catch (Exception exception)
        {
            log.Error(exception, "Quartermaster failed to persist retainer capture.");
            Publish(active?.RetainerId ?? 0, CaptureOutcome.PersistenceFailed, exception.Message);
        }
        finally
        {
            session = null;
        }
    }

    private void Publish(ulong retainerId, CaptureOutcome outcome, string message)
    {
        var receipt = new CaptureReceipt(++receiptRevision, retainerId, outcome, message, DateTime.UtcNow);
        receipts.Add(receipt);
        if (receipts.Count > 64)
            receipts.RemoveAt(0);
        PublishSubscribersSafely(
            CaptureCompleted,
            receipt,
            exception => log.Error(exception, "Quartermaster retainer capture subscriber failed."));
    }

    internal static void PublishSubscribersSafely<T>(
        Action<T>? subscribers,
        T value,
        Action<Exception> logException)
    {
        if (subscribers is null)
            return;

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception exception)
            {
                try
                {
                    logException(exception);
                }
                catch
                {
                    // Addon lifecycle callbacks must contain both subscriber and diagnostic failures.
                }
            }
        }
    }

    private static unsafe (ulong Id, string Name)? ReadActiveRetainer()
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return null;
        var retainer = manager->GetActiveRetainer();
        if (retainer == null || retainer->RetainerId == 0)
            return null;
        fixed (byte* name = retainer->Name)
            return (retainer->RetainerId, Marshal.PtrToStringUTF8((nint)name, 32)?.Split('\0')[0] ?? string.Empty);
    }

    public void Dispose()
    {
        if (!registered)
            return;
        lifecycle.UnregisterListener(AddonEvent.PostSetup, LargeAddon, Opened);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, LargeAddon, Closing);
        lifecycle.UnregisterListener(AddonEvent.PostSetup, SmallAddon, Opened);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, SmallAddon, Closing);
        registered = false;
    }
}
