using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public const string TransferToRetainerCapability = "courierTransferToRetainer.v1";
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
    private readonly Func<IReadOnlyList<CourierLocation>>? locations;
    private readonly Func<ulong, string?>? retainerName;
    private readonly object gate = new();
    private (string RequestId, Guid Run, string Hash)? launched;

    /// <param name="localBags">The logged-in character's latest bags, observed on the framework thread.</param>
    public CourierIpcService(string providerInstanceId, CourierSession session, Func<CourierSettings> settings,
        Func<InventoryView?> localBags, Func<MeetingBell?> bridge, Func<uint, uint> worldRegion, Func<uint, string> worldName,
        Func<uint, ItemMetadata> metadata, Action<string> warn, Func<IReadOnlyList<CourierLocation>>? locations = null,
        Func<ulong, string?>? retainerName = null)
    {
        this.locations = locations;
        this.retainerName = retainerName;
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
            var carrierPeer = CarrierPeer(cfg);
            var carrierBags = carrierPeer?.Bags;
            bool? IsPaired(OwnerScope who) =>
                carrierPeer?.PairedOwners is { } paired ? paired.Any(p => CourierPlanner.Exact(p, who)) : null;
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
                isPairedWithCarrier = IsPaired(live.Character),
            } : null;
            var carrierScope = cfg.Carrier is { HasStableIdentity: true } c ? c : null;
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
                carrierOptIn = carrierPeer?.CarrierOptIn,
                // Whether a transfer may name the carrier itself as its source (sending stock it already holds).
                canSendOwnStock = carrierPeer?.CarrierSourcing == true,
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
                isPairedWithCarrier = IsPaired(o),
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
                    // The carrier itself is the source: one delivery leg, no pickup.
                    fromCarrier = journal.Plan.Trips.Any(t => CourierConductor.FromCarrier(journal, t)),
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

    /// <summary>
    /// The request's identity-relevant payload: participants by id, gil, items merged by item and quality, and the
    /// return flags. A later request under the same id must hash the same or it is a conflict.
    /// </summary>
    internal static string CanonicalHash(CourierTransferRequest request, string requestId)
    {
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        var canonical = new StringBuilder().Append(TransferSchema).Append('|').Append(requestId)
            .Append('|').Append(N((long)(request.Source?.LocalContentId ?? 0))).Append('|').Append(N(request.Source?.HomeWorldId ?? 0))
            .Append('|').Append(N((long)(request.Destination?.LocalContentId ?? 0))).Append('|').Append(N(request.Destination?.HomeWorldId ?? 0))
            .Append('|').Append(N(request.GilAmount))
            .Append('|').Append(request.ReturnOwner ? '1' : '0').Append(request.ReturnDestinationHome ? '1' : '0').Append(request.ReturnCarrier ? '1' : '0');
        foreach (var item in (request.Items ?? []).GroupBy(i => (i.ItemId, i.IsHq, i.RetainerId)).OrderBy(g => g.Key.ItemId).ThenBy(g => g.Key.IsHq).ThenBy(g => g.Key.RetainerId))
        {
            canonical.Append('|').Append(N(item.Key.ItemId)).Append('|').Append(item.Key.IsHq ? '1' : '0').Append('|').Append(N(item.Sum(i => (long)i.Quantity)));
            // Absent before retainer targets existed: a request without them hashes exactly as it always did.
            if (item.Key.RetainerId is { } retainer)
                canonical.Append("|r").Append(retainer.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private string SubmitLocked(CourierTransferRequest request, string requestId)
    {
        if (session.Conductor is not { } conductor)
            return Ack(requestId, null, "rejected", "CourierNotLoaded", "The courier is not loaded.");
        var hash = CanonicalHash(request, requestId);
        var journal = conductor.Journal();
        // Idempotency covers every persisted run this request started (the current journal and the request index of closed
        // runs), so a request resubmitted after other runs never starts a second transfer. A different payload conflicts.
        if (conductor.RunsFor(requestId) is not { } known)
            return Ack(requestId, null, "rejected", "CourierHistoryIndexing", "The courier's request history is still loading; retry shortly.");
        if (known.FirstOrDefault(j => j.Hash is { } stored && stored != hash) is { } conflicting)
            return Ack(requestId, conflicting.Run, "rejected", "RequestConflict", "Request ID was already used with a different canonical payload.");
        if (launched is { } starting && starting.RequestId == requestId && starting.Hash != hash)
            return Ack(requestId, starting.Run, "rejected", "RequestConflict", "Request ID is starting with a different canonical payload.");
        // A run that provably moved nothing (refused, or stopped before any hand-in) runs again under the same id;
        // the current one is archived by the new run below.
        if (known.FirstOrDefault(j => j.Run == journal.Run) is { } current)
        {
            var answer = current.State switch
            {
                "Running" => Ack(requestId, current.Run, "already_running", null, "This request's run is in progress."),
                "Completed" => Ack(requestId, current.Run, "already_completed", null, "This request's run completed."),
                _ when session.IsStarting => Ack(requestId, current.Run, "already_running", null, "This request's run is starting."),
                _ when current.Untouched => null,
                _ => Ack(requestId, current.Run, "rejected", "CourierRunUnresolved", $"This request's run is {current.State} ({current.Code}); resume or clear it."),
            };
            if (answer != null)
                return answer;
        }
        if (known.FirstOrDefault(j => j.State == "Completed") is { } completed)
            return Ack(requestId, completed.Run, "already_completed", null, "This request's run completed.");
        // A cleared run that may have moved anything could have delivered part of the transfer: sending it again could repeat that.
        if (known.FirstOrDefault(j => !j.Untouched) is { } cleared)
            return Ack(requestId, cleared.Run, "rejected", "CourierRunCleared",
                $"This request's run ended {cleared.State} ({cleared.Code}) and was cleared; it may have delivered part of the transfer. Submit what remains under a new request id.");
        if (launched is { } pending && pending.RequestId == requestId && session.IsStarting)
            return Ack(requestId, pending.Run, "already_running", null, "This request's run is starting.");
        if (session.IsActive)
            return Ack(requestId, journal.Run, "rejected", "CourierAlreadyRunning", "Another courier run is active.");
        if (conductor.IsBusy)
            return Ack(requestId, journal.Run, "stopping", null, "Previous run is still finishing cleanup; retry shortly.");
        // An unresolved previous run blocks only while it holds custody or an unverified trade; a zero-custody
        // one is archived by the new run below, so starting again acknowledges it.
        var unresolved = journal.Run != Guid.Empty && journal.State is "Failed" or "Interrupted" or "Indeterminate";
        if (unresolved && (!journal.Custody.IsEmpty || journal.Pending?.Action.Phase is CourierPhase.HandOut or CourierPhase.HandIn))
            return Ack(requestId, journal.Run, "rejected", "CourierRunUnresolved", $"The previous run is {journal.State} ({journal.Code}); resume or clear it first.");

        var cfg = settings();
        static OwnerScope? Named(CourierTransferCharacter? who) => who == null ? null :
            new OwnerScope { LocalContentId = who.LocalContentId, HomeWorldId = who.HomeWorldId, CharacterName = who.CharacterName, HomeWorldName = who.HomeWorldName };
        OwnerScope? Roster(CourierTransferCharacter? who) => Named(who) is { } named ? cfg.LocalRoster.FirstOrDefault(o => CourierPlanner.Exact(o, named)) : null;
        // The configured carrier may be the source: it then delivers stock it already holds.
        var fromCarrier = cfg.Carrier is { HasStableIdentity: true } && CourierPlanner.Exact(Named(request.Source), cfg.Carrier);
        var source = fromCarrier ? cfg.Carrier : Roster(request.Source);
        var destination = Roster(request.Destination);
        if (source == null || destination == null)
            return Ack(requestId, null, "rejected", "OutsideClientRoster",
                "The destination, and a source other than the carrier, must be characters of this client's service account.");
        var live = localBags();
        var carrierPeer = CarrierPeer(cfg);
        if (carrierPeer?.CarrierOptIn == false)
            return Ack(requestId, null, "rejected", "CarrierOptInOff",
                $"{cfg.Carrier!.CharacterName} does not have 'Carry for another character' enabled in Quartermaster.");
        if (fromCarrier && carrierPeer != null && carrierPeer.CarrierSourcing != true)
            return Ack(requestId, null, "rejected", "CarrierSourceUnsupported",
                $"{cfg.Carrier!.CharacterName}'s Quartermaster cannot send stock it holds; it needs the version this client runs.");
        var initiator = live?.Character is { HasStableIdentity: true } logged ? logged : fromCarrier ? destination : source;
        if (carrierPeer?.PairedOwners is { } paired && !paired.Any(p => CourierPlanner.Exact(p, initiator)))
            return Ack(requestId, null, "rejected", "OwnerNotPaired",
                $"{initiator.CharacterName} is not paired as an owner on {cfg.Carrier!.CharacterName}'s Quartermaster Courier settings.");
        var order = new CourierTransferOrder(source, destination, request.GilAmount,
            [.. (request.Items ?? []).Select(i => new CourierTransferItem(i.ItemId, i.ItemName, i.IsHq, i.Quantity, i.RetainerId))],
            request.ReturnDestinationHome, requestId);
        var named = order.Items.Any(i => i.RetainerId != null);
        var (plan, refusal) = CourierTransferPlanner.Build(order, cfg, carrierPeer?.Bags, live, metadata, worldRegion, bridge(),
            named ? locations?.Invoke() : null, retainerName);
        if (refusal != null || plan == null)
            return Ack(requestId, null, "rejected", refusal?.Code ?? "PlanningFailed", refusal?.Message ?? "No plan.");
        plan = plan with { RequestHash = hash };
        // A finished or zero-custody unresolved previous run is archived first; one holding custody was refused above.
        Guid? clear = journal.Run != Guid.Empty && (journal.State is "Completed" or "Refused" || unresolved) && journal.Custody.IsEmpty ? journal.Run : null;
        var run = Guid.NewGuid();
        var runSettings = cfg with { ReturnOwner = request.ReturnOwner, ReturnCarrier = request.ReturnCarrier };
        if (!session.StartPlan(plan, runSettings, clear, run))
            return Ack(requestId, null, "rejected", "CourierAlreadyRunning", "Another courier run is active.");
        launched = (requestId, run, hash);
        var gil = plan.Trips.SelectMany(t => t.Manifest).Where(s => s.IsGil).Sum(s => (long)s.Units);
        var stacks = plan.Trips.SelectMany(t => t.Manifest).Count(s => !s.IsGil);
        return Ack(requestId, run, "accepted", null, fromCarrier
            ? $"{source.CharacterName} → {destination.CharacterName}: {gil:N0} gil, {stacks} stack(s) from the carrier's bags via {plan.Trips[^1].Dropoff.Label}."
            : $"{source.CharacterName} → {destination.CharacterName}: {gil:N0} gil, {stacks} stack(s) in {plan.Trips.Length} trip(s) via " +
              $"{plan.Trips[0].Pickup.Label} / {plan.Trips[^1].Dropoff.Label}.");
    }

    // ---- PairCourierOwner ---------------------------------------------------------------------

    /// <summary>
    /// Pairs another character of this client's roster on the configured carrier. The carrier accepts it only
    /// from a client that already has a paired character, and only while its carrier opt-in is on.
    /// </summary>
    public string PairOwner(string json)
    {
        CourierTransferCharacter? who;
        try { who = JsonSerializer.Deserialize<CourierTransferCharacter>(json, JsonOptions); }
        catch (JsonException e) { return Ack("", null, "rejected", "InvalidRequest", e.Message); }
        if (who == null || who.LocalContentId == 0 || who.HomeWorldId == 0)
            return Ack("", null, "rejected", "InvalidRequest", "A character with localContentId and homeWorldId is required.");
        try
        {
            var cfg = settings();
            if (cfg.Carrier is not { HasStableIdentity: true } carrierScope)
                return Ack("", null, "rejected", "CarrierNotConfigured", "No carrier is configured on this client.");
            var named = new OwnerScope { LocalContentId = who.LocalContentId, HomeWorldId = who.HomeWorldId, CharacterName = who.CharacterName, HomeWorldName = who.HomeWorldName };
            if (cfg.LocalRoster.FirstOrDefault(o => CourierPlanner.Exact(o, named)) is not { } target)
                return Ack("", null, "rejected", "OutsideClientRoster", $"{who.CharacterName} is not a character of this client's service account.");
            var carrierPeer = CarrierPeer(cfg);
            if (carrierPeer == null)
                return Ack("", null, "rejected", "CarrierUnreachable", $"{carrierScope.CharacterName} is not online on a linked client.");
            if (carrierPeer.CarrierOptIn == false)
                return Ack("", null, "rejected", "CarrierOptInOff",
                    $"{carrierScope.CharacterName} does not have 'Carry for another character' enabled in Quartermaster.");
            var live = localBags()?.Character;
            if (carrierPeer.PairedOwners is { } paired && (live is not { HasStableIdentity: true } || !paired.Any(p => CourierPlanner.Exact(p, live))))
                return Ack("", null, "rejected", "OwnerNotPaired",
                    $"{live?.CharacterName ?? "The logged-in character"} is not paired on {carrierScope.CharacterName}.");
            if (carrierPeer.PairedOwners?.Any(p => CourierPlanner.Exact(p, target)) == true)
                return Ack("", null, "accepted", null, $"{target.CharacterName} is already paired on {carrierScope.CharacterName}.");
            // Never block the IPC callback thread (which may be the framework thread) on a peer round-trip;
            // callers observe confirmation through GetCourierStatus once the carrier republishes presence.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var result = await session.Link.PairOwnerAsync(carrierScope, target, cts.Token).ConfigureAwait(false);
                    if (result.Outcome != CourierOutcome.Succeeded)
                        warn($"CourierIpcPair {target.CharacterName}: {result.Code}: {result.Detail}");
                }
                catch (Exception ex) { warn($"CourierIpcPair {target.CharacterName}: {ex.Message}"); }
            });
            return Ack("", null, "accepted", null, $"Pairing {target.CharacterName} on {carrierScope.CharacterName}...");
        }
        catch (Exception e)
        {
            warn("CourierIpcPair: " + e);
            return Ack("", null, "rejected", "PairFailed", e.Message);
        }
    }

    // ---- StopCourierRun / ResumeCourierRun / ClearCourierRun ----------------------------------------------------

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

    /// <summary>
    /// The escape hatch over IPC, identical to the run pane's Clear: forgets the named run whatever it holds or awaits.
    /// Refused only when the courier is not loaded, a run is active (stop it first), or the named run is not the
    /// current one (already cleared, or another run). Accepted clears report status "clearing"; GetCourierStatus
    /// shows no run once it lands.
    /// </summary>
    public string Clear(string json)
    {
        string requestId = "";
        Guid run = Guid.Empty;
        try
        {
            var request = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CourierRunRequest>(json, JsonOptions);
            requestId = request?.RequestId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(request?.RunId) || !Guid.TryParse(request.RunId, out run) || run == Guid.Empty)
                return Ack(requestId, null, "rejected", "InvalidRequest", "runId must name the run to clear.");
        }
        catch (JsonException e) { return Ack(requestId, null, "rejected", "InvalidRequest", e.Message); }
        if (session.Conductor is not { } conductor)
            return Ack(requestId, run, "rejected", "CourierNotLoaded", "The courier is not loaded.");
        var journal = conductor.Journal();
        if (run != journal.Run)
            return Ack(requestId, journal.Run, "rejected", "RunNotCurrent",
                journal.Run == Guid.Empty ? $"Run {run} is already cleared." : $"Run {run} is not the current courier run.");
        if (session.IsActive)
            return Ack(requestId, run, "rejected", "CourierAlreadyRunning", "The run is active; stop it before clearing.");
        warn($"CourierIpcClear {requestId}: clearing run {run} ({journal.State} {journal.Code}).");
        var launched = session.Clear(run, (_, fault) =>
        {
            if (fault != null)
                warn($"CourierIpcClear {requestId}: run {run} not cleared: {fault.Message}");
        });
        return launched
            ? Ack(requestId, run, "clearing", null, $"Clearing run {run}.")
            : Ack(requestId, run, "rejected", "CourierAlreadyRunning", "The run is active; stop it before clearing.");
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
        if (conductor.IsBusy)
            return Ack(journal.RequestId ?? "", journal.Run, "stopping", null, "Previous run is still finishing cleanup; retry shortly.");
        if (!conductor.Snapshot().CanResume)
            return Ack(journal.RequestId ?? "", journal.Run, "rejected", "RunNotResumable", $"The run is {journal.State} ({journal.Code}) and cannot resume.");
        if (!session.Resume())
            return session.IsActive
                ? Ack(journal.RequestId ?? "", journal.Run, "rejected", "CourierAlreadyRunning", "The run is already active.")
                : Ack(journal.RequestId ?? "", journal.Run, "rejected", "RunNotResumable", $"The run is {journal.State} ({journal.Code}) and cannot resume.");
        return Ack(journal.RequestId ?? "", journal.Run, "accepted", null, "Resuming from the journal.");
    }
}
