using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Observations.V1;
using RQ.Domain;
using RQ.Inventory;

namespace RQ.Automation;

public sealed record ListingOpenRequest(
    ulong RetainerId,
    string RetainerName,
    int? SlotIndex,
    uint ItemId,
    string ItemName,
    int Quantity,
    bool IsHq,
    uint? UnitPrice);

public sealed record ListingOpenResult(bool Started, bool Success, string Message);
public sealed record RetainerListingsOpenRequest(ulong RetainerId, string RetainerName);
public sealed record ListingRefreshTiming(
    ulong RetainerId,
    DateTime ActionStartedAtUtc,
    DateTime EvidenceObservedAtUtc,
    DateTime AppliedAtUtc,
    DateTime CompletedAtUtc,
    double ObservedToAppliedMilliseconds,
    double ActionToAppliedMilliseconds);

/// <summary>
/// Owns the product workflow for navigating from a cached Quartermaster listing to
/// its verified live retainer surface. Franthropy owns the individual game actions.
/// </summary>
public sealed class ListingNavigationCoordinator : IDisposable
{
    private static readonly TimeSpan AutoRetainerWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListingEvidenceWait = TimeSpan.FromSeconds(6);
    private readonly IRetainerAutomationSession session;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly AutoRetainerSuppression autoRetainerSuppression;
    private readonly AutomationLease automation;
    private readonly RetainerCacheRepository? cache;
    private readonly ObservationCaptureSessionRegistry? captureSessions;
    private readonly Func<OwnerScope>? currentOwner;
    private readonly CancellationTokenSource lifetime = new();
    private int running;
    private bool disposed;

    internal ListingNavigationCoordinator(
        IRetainerAutomationSession session,
        IAutoRetainerIpc autoRetainer,
        AutomationLease automation,
        AutoRetainerSuppression? autoRetainerSuppression = null)
    {
        this.session = session;
        this.autoRetainer = autoRetainer;
        this.automation = automation;
        this.autoRetainerSuppression = autoRetainerSuppression ?? new AutoRetainerSuppression(autoRetainer);
    }

    public ListingNavigationCoordinator(
        IRetainerAutomationSession session,
        IAutoRetainerIpc autoRetainer,
        AutomationLease automation,
        RetainerCacheRepository cache,
        ObservationCaptureSessionRegistry captureSessions,
        Func<OwnerScope> currentOwner,
        AutoRetainerSuppression? autoRetainerSuppression = null)
        : this(session, autoRetainer, automation, autoRetainerSuppression)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.captureSessions = captureSessions ?? throw new ArgumentNullException(nameof(captureSessions));
        this.currentOwner = currentOwner ?? throw new ArgumentNullException(nameof(currentOwner));
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public string Status { get; private set; } = string.Empty;
    public ListingRefreshTiming? LastRefreshTiming { get; private set; }

    public async Task<ListingOpenResult> OpenAsync(
        ListingOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.RetainerId == 0 || string.IsNullOrWhiteSpace(request.RetainerName) ||
            request.ItemId == 0 || request.Quantity <= 0)
            return Complete(false, false, "This listing does not have a stable physical identity.");
        if (!automation.TryAcquire("listing navigation", out var lease))
            return Complete(false, false, $"Automation is busy with {automation.Holder}.");

        using (lease)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return Complete(false, false, "Another listing is already opening.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            var token = linked.Token;
            AutoRetainerSuppression.Scope? suppressionScope = null;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the listing was not opened.");

                if (autoRetainer.IsAvailable)
                    suppressionScope = autoRetainerSuppression.Acquire();

                Status = $"Opening {request.RetainerName}…";
                using var evidence = BeginListingEvidence(request.RetainerId, out var evidenceFailure);
                if (evidenceFailure is not null)
                    return Complete(false, false, evidenceFailure);

                var list = await session.EnsureRetainerListAsync(token).ConfigureAwait(false);
                if (!list.Success)
                    return Complete(true, false, Format(list));

                var retainer = await session.OpenRetainerAsync(
                    new(request.RetainerId, request.RetainerName),
                    token).ConfigureAwait(false);
                if (!retainer.Success)
                    return Complete(true, false, Format(retainer));

                var listingActionStartedAtUtc = DateTime.UtcNow;
                evidence?.BeginAction(listingActionStartedAtUtc);
                var listing = await session.OpenSellingListingAsync(
                    new(
                        request.SlotIndex ?? -1,
                        request.ItemId,
                        request.Quantity,
                        request.IsHq,
                        request.UnitPrice),
                    token).ConfigureAwait(false);
                if (listing.Success)
                {
                    if (evidence is not null)
                    {
                        var accepted = await evidence.WaitAsync(ListingEvidenceWait, token).ConfigureAwait(false);
                        if (accepted is null)
                            return Complete(true, false, $"Opened {request.ItemName} on {request.RetainerName}, but fresh listing evidence did not arrive.");
                        RecordTiming(request.RetainerId, listingActionStartedAtUtc, accepted);
                    }
                    return Complete(true, true, $"Opened {request.ItemName} on {request.RetainerName} with fresh listings.");
                }

                var failure = Format(listing);
                var recovery = await session.ReturnToRetainerListAsync(token).ConfigureAwait(false);
                return recovery.Success
                    ? Complete(true, false, failure)
                    : Complete(true, false, $"{failure} Recovery also failed: {Format(recovery)}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Complete(true, false, "Listing navigation was cancelled.");
            }
            catch (Exception exception)
            {
                return Complete(true, false, $"Listing navigation failed: {exception.Message}");
            }
            finally
            {
                if (suppressionScope is not null)
                {
                    suppressionScope.Dispose();
                    if (suppressionScope.RestoreFailure is { } restoreFailure)
                        Status = $"{Status} AutoRetainer suppression could not be restored: {restoreFailure}";
                }
                Interlocked.Exchange(ref running, 0);
            }
        }
    }

    public async Task<ListingOpenResult> OpenRetainerListingsAsync(
        RetainerListingsOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.RetainerId == 0 || string.IsNullOrWhiteSpace(request.RetainerName))
            return Complete(false, false, "This retainer does not have a stable identity.");
        if (!automation.TryAcquire("listing navigation", out var lease))
            return Complete(false, false, $"Automation is busy with {automation.Holder}.");

        using (lease)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return Complete(false, false, "Another listing surface is already opening.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            var token = linked.Token;
            AutoRetainerSuppression.Scope? suppressionScope = null;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the listings were not opened.");

                if (autoRetainer.IsAvailable)
                    suppressionScope = autoRetainerSuppression.Acquire();

                using var evidence = BeginListingEvidence(request.RetainerId, out var evidenceFailure);
                if (evidenceFailure is not null)
                    return Complete(false, false, evidenceFailure);

                Status = $"Opening {request.RetainerName}'s listings…";
                var list = await session.EnsureRetainerListAsync(token).ConfigureAwait(false);
                if (!list.Success)
                    return Complete(true, false, Format(list));

                var retainer = await session.OpenRetainerAsync(
                    new(request.RetainerId, request.RetainerName),
                    token).ConfigureAwait(false);
                if (!retainer.Success)
                    return Complete(true, false, Format(retainer));

                var listingActionStartedAtUtc = DateTime.UtcNow;
                evidence?.BeginAction(listingActionStartedAtUtc);
                var listings = await session.OpenSellingListAsync(token).ConfigureAwait(false);
                if (listings.Success)
                {
                    if (evidence is not null)
                    {
                        var accepted = await evidence.WaitAsync(ListingEvidenceWait, token).ConfigureAwait(false);
                        if (accepted is null)
                            return Complete(true, false, $"Opened {request.RetainerName}'s listings, but fresh listing evidence did not arrive.");
                        RecordTiming(request.RetainerId, listingActionStartedAtUtc, accepted);
                    }
                    return Complete(true, true, $"Opened {request.RetainerName}'s fresh listings.");
                }

                var failure = Format(listings);
                var recovery = await session.ReturnToRetainerListAsync(token).ConfigureAwait(false);
                return recovery.Success
                    ? Complete(true, false, failure)
                    : Complete(true, false, $"{failure} Recovery also failed: {Format(recovery)}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Complete(true, false, "Listing navigation was cancelled.");
            }
            catch (Exception exception)
            {
                return Complete(true, false, $"Listing navigation failed: {exception.Message}");
            }
            finally
            {
                if (suppressionScope is not null)
                {
                    suppressionScope.Dispose();
                    if (suppressionScope.RestoreFailure is { } restoreFailure)
                        Status = $"{Status} AutoRetainer suppression could not be restored: {restoreFailure}";
                }
                Interlocked.Exchange(ref running, 0);
            }
        }
    }

    public async Task<ListingOpenResult> ReturnToRetainerListAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!automation.TryAcquire("listing navigation", out var lease))
            return Complete(false, false, $"Automation is busy with {automation.Holder}.");

        using (lease)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return Complete(false, false, "Another listing surface is already opening.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            var token = linked.Token;
            AutoRetainerSuppression.Scope? suppressionScope = null;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the retainer list was not restored.");

                if (autoRetainer.IsAvailable)
                    suppressionScope = autoRetainerSuppression.Acquire();

                Status = "Returning to the retainer list…";
                var result = await session.ReturnToRetainerListAsync(token).ConfigureAwait(false);
                return result.Success
                    ? Complete(true, true, "Returned to the retainer list.")
                    : Complete(true, false, Format(result));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Complete(true, false, "Listing navigation was cancelled.");
            }
            catch (Exception exception)
            {
                return Complete(true, false, $"Listing recovery failed: {exception.Message}");
            }
            finally
            {
                if (suppressionScope is not null)
                {
                    suppressionScope.Dispose();
                    if (suppressionScope.RestoreFailure is { } restoreFailure)
                        Status = $"{Status} AutoRetainer suppression could not be restored: {restoreFailure}";
                }
                Interlocked.Exchange(ref running, 0);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task<bool> WaitForAutoRetainerAsync(CancellationToken cancellationToken)
    {
        if (!autoRetainer.IsAvailable)
            return true;

        var deadline = DateTime.UtcNow + AutoRetainerWait;
        while (autoRetainer.IsBusy && DateTime.UtcNow < deadline)
        {
            Status = "Waiting for AutoRetainer to finish…";
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return !autoRetainer.IsBusy;
    }

    private ListingEvidenceBoundary? BeginListingEvidence(ulong retainerId, out string? failure)
    {
        failure = null;
        if (cache is null || captureSessions is null || currentOwner is null)
            return null;
        var owner = currentOwner();
        if (!owner.HasStableIdentity)
        {
            failure = "A stable character identity is required to refresh retainer listings.";
            return null;
        }
        try
        {
            var session = captureSessions.Begin(
                new ObservationOwner(owner.LocalContentId!.Value, owner.HomeWorldId!.Value),
                retainerId);
            return new ListingEvidenceBoundary(cache, owner, retainerId, session);
        }
        catch (Exception exception)
        {
            failure = $"Fresh listing capture could not start: {exception.Message}";
            return null;
        }
    }

    private void RecordTiming(ulong retainerId, DateTime actionStartedAtUtc, ListingEvidenceAcceptance accepted)
    {
        var completedAtUtc = DateTime.UtcNow;
        LastRefreshTiming = new(
            retainerId,
            actionStartedAtUtc,
            accepted.Receipt.ObservedAtUtc,
            accepted.AppliedAtUtc,
            completedAtUtc,
            Math.Max(0, (accepted.AppliedAtUtc - accepted.Receipt.ObservedAtUtc).TotalMilliseconds),
            Math.Max(0, (accepted.AppliedAtUtc - actionStartedAtUtc).TotalMilliseconds));
    }

    private sealed record ListingEvidenceAcceptance(RetainerEvidenceReceipt Receipt, DateTime AppliedAtUtc);

    private sealed class ListingEvidenceBoundary : IDisposable
    {
        private readonly RetainerCacheRepository cache;
        private readonly OwnerScope owner;
        private readonly ulong retainerId;
        private readonly ObservationCaptureSession session;
        private readonly object gate = new();
        private readonly TaskCompletionSource<ListingEvidenceAcceptance> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTime? actionStartedAtUtc;
        private long actionCheckpoint;

        public ListingEvidenceBoundary(
            RetainerCacheRepository cache,
            OwnerScope owner,
            ulong retainerId,
            ObservationCaptureSession session)
        {
            this.cache = cache;
            this.owner = owner with { };
            this.retainerId = retainerId;
            this.session = session;
            cache.EvidenceAccepted += OnEvidenceAccepted;
        }

        public void BeginAction(DateTime startedAtUtc)
        {
            lock (gate)
            {
                actionCheckpoint = cache.Revision;
                actionStartedAtUtc = startedAtUtc;
            }
        }

        public async Task<ListingEvidenceAcceptance?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                return await accepted.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        private void OnEvidenceAccepted(RetainerEvidenceReceipt receipt)
        {
            lock (gate)
            {
                if (actionStartedAtUtc is { } actionStarted &&
                    receipt.RetainerId == retainerId &&
                    receipt.Owner.Matches(owner) &&
                    receipt.Revision > actionCheckpoint &&
                    receipt.ObservedAtUtc >= actionStarted &&
                    receipt.Domains.HasFlag(RetainerEvidenceDomain.Listings) &&
                    string.Equals(receipt.EvidenceSessionId, session.SessionId, StringComparison.Ordinal))
                {
                    accepted.TrySetResult(new ListingEvidenceAcceptance(receipt, DateTime.UtcNow));
                }
            }
        }

        public void Dispose()
        {
            cache.EvidenceAccepted -= OnEvidenceAccepted;
            session.Dispose();
        }
    }

    private ListingOpenResult Complete(bool started, bool success, string message)
    {
        Status = message;
        return new(started, success, message);
    }

    private static string Format(RetainerAutomationResult result) => $"{result.Code}: {result.Message}";
}
