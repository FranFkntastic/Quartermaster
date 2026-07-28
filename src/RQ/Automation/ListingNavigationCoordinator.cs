using Franthropy.Dalamud.Automation.Retainers;

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

/// <summary>
/// Owns the product workflow for navigating from a cached Quartermaster listing to
/// its verified live retainer surface. Franthropy owns the individual game actions.
/// </summary>
public sealed class ListingNavigationCoordinator : IDisposable
{
    private static readonly TimeSpan AutoRetainerWait = TimeSpan.FromSeconds(30);
    private readonly IRetainerAutomationSession session;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly AutomationLease automation;
    private readonly CancellationTokenSource lifetime = new();
    private int running;
    private bool disposed;

    public ListingNavigationCoordinator(
        IRetainerAutomationSession session,
        IAutoRetainerIpc autoRetainer,
        AutomationLease automation)
    {
        this.session = session;
        this.autoRetainer = autoRetainer;
        this.automation = automation;
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public string Status { get; private set; } = string.Empty;

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
            var restoreSuppression = false;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the listing was not opened.");

                if (autoRetainer.IsAvailable && !autoRetainer.IsSuppressed)
                {
                    autoRetainer.SetSuppressed(true);
                    restoreSuppression = true;
                }

                Status = $"Opening {request.RetainerName}…";
                var list = await session.EnsureRetainerListAsync(token).ConfigureAwait(false);
                if (!list.Success)
                    return Complete(true, false, Format(list));

                var retainer = await session.OpenRetainerAsync(
                    new(request.RetainerId, request.RetainerName),
                    token).ConfigureAwait(false);
                if (!retainer.Success)
                    return Complete(true, false, Format(retainer));

                var listing = await session.OpenSellingListingAsync(
                    new(
                        request.SlotIndex ?? -1,
                        request.ItemId,
                        request.Quantity,
                        request.IsHq,
                        request.UnitPrice),
                    token).ConfigureAwait(false);
                if (listing.Success)
                    return Complete(true, true, $"Opened {request.ItemName} on {request.RetainerName}.");

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
                if (restoreSuppression)
                {
                    try { autoRetainer.SetSuppressed(false); }
                    catch (Exception exception) { Status = $"The listing opened, but AutoRetainer suppression could not be restored: {exception.Message}"; }
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
            var restoreSuppression = false;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the listings were not opened.");

                if (autoRetainer.IsAvailable && !autoRetainer.IsSuppressed)
                {
                    autoRetainer.SetSuppressed(true);
                    restoreSuppression = true;
                }

                Status = $"Opening {request.RetainerName}'s listings…";
                var list = await session.EnsureRetainerListAsync(token).ConfigureAwait(false);
                if (!list.Success)
                    return Complete(true, false, Format(list));

                var retainer = await session.OpenRetainerAsync(
                    new(request.RetainerId, request.RetainerName),
                    token).ConfigureAwait(false);
                if (!retainer.Success)
                    return Complete(true, false, Format(retainer));

                var listings = await session.OpenSellingListAsync(token).ConfigureAwait(false);
                if (listings.Success)
                    return Complete(true, true, $"Opened {request.RetainerName}'s listings.");

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
                if (restoreSuppression)
                {
                    try { autoRetainer.SetSuppressed(false); }
                    catch (Exception exception) { Status = $"The listings opened, but AutoRetainer suppression could not be restored: {exception.Message}"; }
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
            var restoreSuppression = false;
            try
            {
                if (!await WaitForAutoRetainerAsync(token).ConfigureAwait(false))
                    return Complete(false, false, "AutoRetainer remained busy; the retainer list was not restored.");

                if (autoRetainer.IsAvailable && !autoRetainer.IsSuppressed)
                {
                    autoRetainer.SetSuppressed(true);
                    restoreSuppression = true;
                }

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
                if (restoreSuppression)
                {
                    try { autoRetainer.SetSuppressed(false); }
                    catch (Exception exception) { Status = $"The retainer list was restored, but AutoRetainer suppression could not be restored: {exception.Message}"; }
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

    private ListingOpenResult Complete(bool started, bool success, string message)
    {
        Status = message;
        return new(started, success, message);
    }

    private static string Format(RetainerAutomationResult result) => $"{result.Code}: {result.Message}";
}
