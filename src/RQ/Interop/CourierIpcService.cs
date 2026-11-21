using System.Collections.Immutable;
using System.Text.Json;
using RQ.Courier;
using RQ.Domain;
using RQ.Inventory;
using RQ.UI;

namespace RQ.Interop;

/// <summary>
/// The versioned courier IPC (<c>Quartermaster.v1.*Courier*</c>): status, direct carried transfers between
/// roster characters (gil and bag-held items, across regions through the bridge), stop and resume.
/// Runs start in the background through <see cref="CourierSession"/>, exactly as the UI starts them.
/// </summary>
public sealed class CourierIpcService
{
    public const string StatusSchema = "gooseworks-quartermaster-courier-status/v1";
    public const string TransferSchema = "gooseworks-quartermaster-courier-transfer/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string providerInstanceId;
    private readonly CourierSession session;
    private readonly Func<CourierSettings> settings;
    private readonly Func<InventoryView?> localBags;
    private readonly Func<MeetingBell?> bridge;
    private readonly Func<uint, uint> worldRegion;
    private readonly Func<uint, string> worldName;
    private readonly Func<uint, ItemMetadata> metadata;
    private readonly Action<string> warn;
    private readonly object gate = new();
    private (string RequestId, Guid Run)? launched;

    /// <param name="localBags">The logged-in character's latest bags, observed on the framework thread.</param>
    public CourierIpcService(string providerInstanceId, CourierSession session, Func<CourierSettings> settings,
        Func<InventoryView?> localBags, Func<MeetingBell?> bridge, Func<uint, uint> worldRegion, Func<uint, string> worldName,
        Func<uint, ItemMetadata> metadata, Action<string> warn)
    {
        this.providerInstanceId = providerInstanceId;
        this.session = session;
        this.settings = settings;
        this.localBags = localBags;
        this.bridge = bridge;
        this.worldRegion = worldRegion;
        this.worldName = worldName;
        this.metadata = metadata;
        this.warn = warn;
    }

    internal static string RegionName(uint region) => region switch
    {
        1 => "Japan",
        2 => "North America",
        3 => "Europe",
        4 => "Oceania",
        _ => "",
    };

    private string HomeWorldName(OwnerScope who) =>
        !string.IsNullOrWhiteSpace(who.HomeWorldName) ? who.HomeWorldName : worldName(who.HomeWorldId ?? 0);

    private static MeetingBell? HomeBell(CourierSettings cfg, OwnerScope who) =>
        cfg.Bells.GetValueOrDefault(who.LocalContentId ?? 0) is { } bell && bell.World == who.HomeWorldId ? bell : null;

    private CourierPeer? CarrierPeer(CourierSettings cfg)
    {
        if (cfg.Carrier is not { HasStableIdentity: true } carrier)
            return null;
        var matches = session.Link.Peers.Where(p => CourierPlanner.Exact(p.Character, carrier)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    // ---- GetCourierStatus ---------------------------------------------------------------------

    public string GetStatus()
    {
        try
        {
            var cfg = settings();
            var bags = localBags();
            object? loggedIn = bags is { Character.HasStableIdentity: true } live ? new
            {
                localContentId = live.Character.LocalContentId,
                homeWorldId = live.Character.HomeWorldId,
                characterName = live.Character.CharacterName,
                homeWorldName = HomeWorldName(live.Character),
                regionId = worldRegion(live.Character.HomeWorldId ?? 0),
                regionName = RegionName(worldRegion(live.Character.HomeWorldId ?? 0)),
                carriedGil = live.Gil,
                freeBagSlots = live.FreeSlots,
                hasMeetingBell = HomeBell(cfg, live.Character) != null,
                meetingBellLabel = HomeBell(cfg, live.Character)?.Label,
            } : null;
            var carrierScope = cfg.Carrier is { HasStableIdentity: true } c ? c : null;
            var carrierBags = CarrierPeer(cfg)?.Bags;
            var carrier = new
            {
                configured = carrierScope != null,
                online = carrierBags != null,
                localContentId = carrierScope?.LocalContentId,
                homeWorldId = carrierScope?.HomeWorldId,
                characterName = carrierScope?.CharacterName,
                homeWorldName = carrierScope == null ? null : HomeWorldName(carrierScope),
                regionId = carrierScope == null ? (uint?)null : worldRegion(carrierScope.HomeWorldId ?? 0),
                regionName = carrierScope == null ? null : RegionName(worldRegion(carrierScope.HomeWorldId ?? 0)),
                carriedGil = carrierBags?.Gil,
                freeBagSlots = carrierBags?.FreeSlots,
            };
            var bridgeBell = bridge();
            object? bridgeView = bridgeBell == null ? null : new
            {
                worldId = bridgeBell.World,
                worldName = worldName(bridgeBell.World),
                territoryId = bridgeBell.Territory,
                label = bridgeBell.Label,
            };
            var roster = cfg.LocalRoster.Where(o => o.HasStableIdentity).Select(o => new
            {
                localContentId = o.LocalContentId,
                homeWorldId = o.HomeWorldId,
                characterName = o.CharacterName,
                homeWorldName = HomeWorldName(o),
                regionId = worldRegion(o.HomeWorldId ?? 0),
                regionName = RegionName(worldRegion(o.HomeWorldId ?? 0)),
                hasMeetingBell = HomeBell(cfg, o) != null,
                meetingBellLabel = HomeBell(cfg, o)?.Label,
                isCarrier = CourierPlanner.Exact(o, cfg.Carrier),
            }).ToArray();
            object? run = null;
            if (session.Conductor is { } conductor)
            {
                var journal = conductor.Journal();
                var snapshot = conductor.Snapshot();
                run = new
                {
                    runId = journal.Run == Guid.Empty ? null : journal.Run.ToString(),
                    requestId = journal.RequestId,
                    state = snapshot.State,
                    ownerPhase = snapshot.OwnerPhase,
                    carrierPhase = snapshot.CarrierPhase,
                    trip = snapshot.Trip,
                    trips = snapshot.Trips,
                    code = snapshot.Code,
                    detail = snapshot.Detail,
                    active = snapshot.State == "Running" || session.IsStarting,
                    canResume = snapshot.CanResume,
                    canClear = snapshot.CanClear,
                    custodyUnits = journal.Custody.Where(s => !s.IsGil).Sum(s => (long)s.Units),
                    custodyGil = journal.Custody.Where(s => s.IsGil).Sum(s => (long)s.Units),
                };
            }
            return JsonSerializer.Serialize(new
            {
                schema = StatusSchema,
                providerInstanceId,
                loggedIn,
                carrier,
                bridge = bridgeView,
                roster,
                run,
            }, JsonOptions);
        }
        catch (Exception e)
        {
            warn("CourierIpcStatus: " + e);
            return JsonSerializer.Serialize(new { schema = StatusSchema, providerInstanceId, error = "StatusUnavailable" }, JsonOptions);
        }
    }

    // ---- SubmitCourierTransfer ----------------------------------------------------------------

    private string Ack(string requestId, Guid? run, string status, string? code, string message)
    {
        if (code != null)
            warn($"CourierIpcTransfer {requestId}: {status} {code}: {message}");
        return JsonSerializer.Serialize(new CourierTransferAcknowledgement
        {
            ProviderInstanceId = providerInstanceId,
            RequestId = requestId,
            RunId = run is { } id && id != Guid.Empty ? id.ToString() : null,
            Status = status,
            ErrorCode = code,
            Message = message,
        }, JsonOptions);
    }

    public string Submit(string json)
    {
        CourierTransferRequest? request;
        try { request = JsonSerializer.Deserialize<CourierTransferRequest>(json, JsonOptions); }
        catch (JsonException e) { return Ack("", null, "rejected", "InvalidRequest", e.Message); }
        if (request == null)
            return Ack("", null, "rejected", "InvalidRequest", "Empty request.");
        var requestId = request.RequestId?.Trim() ?? "";
        if (request.Schema != TransferSchema)
            return Ack(requestId, null, "rejected", "UnsupportedSchema", $"Expected {TransferSchema}.");
        if (requestId.Length == 0)
            return Ack("", null, "rejected", "RequestIdRequired", "Every transfer needs a requestId.");
        try
        {
            lock (gate)
                return SubmitLocked(request, requestId);
        }
        catch (Exception e)
        {
            warn($"CourierIpcTransfer {requestId}: {e}");
            return Ack(requestId, null, "rejected", "SubmissionFailed", e.Message);
        }
    }

    private string SubmitLocked(CourierTransferRequest request, string requestId)
    {
        if (session.Conductor is not { } conductor)
            return Ack(requestId, null, "rejected", "CourierNotLoaded", "The courier is not loaded.");
        var journal = conductor.Journal();
        // Idempotency: a request already journaled (or still starting) never starts a second run.
        if (journal.Run != Guid.Empty && journal.RequestId == requestId)
            return journal.State switch
            {
                "Running" => Ack(requestId, journal.Run, "already_running", null, "This request's run is in progress."),
                "Completed" => Ack(requestId, journal.Run, "already_completed", null, "This request's run completed."),
                "Refused" => Ack(requestId, journal.Run, "rejected", journal.Code, journal.Detail),
                _ when session.IsStarting => Ack(requestId, journal.Run, "already_running", null, "This request's run is starting."),
                _ => Ack(requestId, journal.Run, "rejected", "CourierRunUnresolved", $"This request's run is {journal.State} ({journal.Code}); resume or clear it."),
            };
        if (launched is { } pending && pending.RequestId == requestId && session.IsStarting)
            return Ack(requestId, pending.Run, "already_running", null, "This request's run is starting.");
        if (session.IsActive)
            return Ack(requestId, journal.Run, "rejected", "CourierAlreadyRunning", "Another courier run is active.");
        if (journal.State is "Failed" or "Interrupted" or "Indeterminate")
            return Ack(requestId, journal.Run, "rejected", "CourierRunUnresolved", $"The previous run is {journal.State} ({journal.Code}); resume or clear it first.");

        var cfg = settings();
        OwnerScope? Roster(CourierTransferCharacter? who)
        {
            if (who == null)
                return null;
            var named = new OwnerScope { LocalContentId = who.LocalContentId, HomeWorldId = who.HomeWorldId, CharacterName = who.CharacterName, HomeWorldName = who.HomeWorldName };
            return cfg.LocalRoster.FirstOrDefault(o => CourierPlanner.Exact(o, named));
        }
        var source = Roster(request.Source);
        var destination = Roster(request.Destination);
        if (source == null || destination == null)
            return Ack(requestId, null, "rejected", "OutsideClientRoster", "Source and destination must both be characters of this client's service account.");
        var order = new CourierTransferOrder(source, destination, request.GilAmount,
            [.. (request.Items ?? []).Select(i => new CourierTransferItem(i.ItemId, i.ItemName, i.IsHq, i.Quantity))],
            request.ReturnDestinationHome, requestId);
        var (plan, refusal) = CourierTransferPlanner.Build(order, cfg, CarrierPeer(cfg)?.Bags, localBags(), metadata, worldRegion, bridge());
        if (refusal != null || plan == null)
            return Ack(requestId, null, "rejected", refusal?.Code ?? "PlanningFailed", refusal?.Message ?? "No plan.");
        // A clean, finished previous run is archived first; an unresolved one was refused above.
        Guid? clear = journal.Run != Guid.Empty && journal.State is "Completed" or "Refused" && journal.Custody.IsEmpty ? journal.Run : null;
        var run = Guid.NewGuid();
        var runSettings = cfg with { ReturnOwner = request.ReturnOwner, ReturnCarrier = request.ReturnCarrier };
        if (!session.StartPlan(plan, runSettings, clear, run))
            return Ack(requestId, null, "rejected", "CourierAlreadyRunning", "Another courier run is active.");
        launched = (requestId, run);
        var gil = plan.Trips.SelectMany(t => t.Manifest).Where(s => s.IsGil).Sum(s => (long)s.Units);
        var stacks = plan.Trips.SelectMany(t => t.Manifest).Count(s => !s.IsGil);
        return Ack(requestId, run, "accepted", null,
            $"{source.CharacterName} → {destination.CharacterName}: {gil:N0} gil, {stacks} stack(s) in {plan.Trips.Length} trip(s) via " +
            $"{plan.Trips[0].Pickup.Label} / {plan.Trips[^1].Dropoff.Label}.");
    }

    // ---- StopCourierRun / ResumeCourierRun ----------------------------------------------------

    private static Guid? RunOf(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var request = JsonSerializer.Deserialize<CourierRunRequest>(json, JsonOptions);
            if (string.IsNullOrWhiteSpace(request?.RunId))
                return null;
            if (Guid.TryParse(request.RunId, out var run))
                return run;
            error = "runId is not a run id.";
        }
        catch (JsonException e) { error = e.Message; }
        return Guid.Empty;
    }

    public string Stop(string json)
    {
        var wanted = RunOf(json, out var error);
        if (error != null)
            return Ack("", null, "rejected", "InvalidRequest", error);
        if (session.Conductor is not { } conductor)
            return Ack("", null, "rejected", "CourierNotLoaded", "The courier is not loaded.");
        var journal = conductor.Journal();
        if (wanted is { } run && run != journal.Run)
            return Ack("", journal.Run, "rejected", "RunMismatch", "That run is not the current courier run.");
        if (journal.State != "Running" && !session.IsStarting)
            return Ack(journal.RequestId ?? "", journal.Run, "idle", null, $"No run is active ({journal.State}).");
        session.Stop();
        return Ack(journal.RequestId ?? "", journal.Run, "stopping", null, "Stopping at the next verified checkpoint.");
    }

    public string Resume(string json)
    {
        var wanted = RunOf(json, out var error);
        if (error != null)
            return Ack("", null, "rejected", "InvalidRequest", error);
        if (session.Conductor is not { } conductor)
            return Ack("", null, "rejected", "CourierNotLoaded", "The courier is not loaded.");
        var journal = conductor.Journal();
        if (wanted is { } run && run != journal.Run)
            return Ack("", journal.Run, "rejected", "RunMismatch", "That run is not the current courier run.");
        if (session.IsActive)
            return Ack(journal.RequestId ?? "", journal.Run, "rejected", "CourierAlreadyRunning", "The run is already active.");
        if (!conductor.Snapshot().CanResume)
            return Ack(journal.RequestId ?? "", journal.Run, "rejected", "RunNotResumable", $"The run is {journal.State} ({journal.Code}) and cannot resume.");
        session.Resume();
        return Ack(journal.RequestId ?? "", journal.Run, "accepted", null, "Resuming from the journal.");
    }
}
