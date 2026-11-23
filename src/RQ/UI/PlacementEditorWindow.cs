using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Windows;
using RQ.Domain;
using RQ.Interop;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The market run's placement matrix for a caller-supplied set of items (opened over IPC by another
/// plugin). Rows read the owner's reserve rules; edits persist straight to them through
/// <see cref="WorkbenchStockService.ApplyPlacementEdit"/> (the path the placement IPC uses too), creating a
/// level-0 rule for an item that has none. A character bar above the matrix switches it to another character
/// of this account: its retainers become the columns and an edit there sets the item's remote homes
/// (<see cref="ReservePlacement.Resolve"/>). An item has remote homes on one other
/// character at most; a row homed on a character other than the one shown reads dimmed with where it goes,
/// and editing it there moves its remote homes to the shown character. Local homes stay as they are.
/// </summary>
internal sealed class PlacementEditorWindow : Window
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(5);
    private const string AddCharacterPopup = "RQPlacementAddCharacter";

    private readonly Func<QuartermasterRuntimeSnapshot> runtime;
    private readonly WorkbenchStockService service;
    private readonly CourierSession courier;
    private readonly PlacementMatrix<(uint ItemId, ItemQualityPolicy Quality)> matrix;
    private readonly Dictionary<(uint ItemId, ItemQualityPolicy Quality), (StowageRoutingPolicy Routing, DateTime Until)> pending = [];
    private readonly Dictionary<(uint ItemId, ItemQualityPolicy Quality), (ReserveRemoteHomes? Remote, DateTime Until)> pendingRemote = [];
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private OwnerScope? owner;
    private OwnerScope? shown;
    private OwnerScope? added;
    private IReadOnlyList<WorkbenchStockService.PlacementEditorItem> items = [];
    private static readonly Vector4 ErrorColor = new(1f, .45f, .4f, 1f);
    private string? saveError;

    public PlacementEditorWindow(Func<QuartermasterRuntimeSnapshot> runtime, WorkbenchStockService service, CourierSession courier)
        : base("Placement###RQPlacementEditor", ImGuiWindowFlags.NoCollapse)
    {
        this.runtime = runtime;
        this.service = service;
        this.courier = courier;
        // Not wired to the agent bridge: this window is opened by another plugin, not a reviewed surface.
        // The matrix still registers its controls, so every draw opens and closes a frame on this registry.
        matrix = new("quartermaster.placement-editor", "Bags", reviewRegistry);
        Size = new Vector2(720, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Shows the matrix for these items of the owner, replacing any earlier set. Framework thread.</summary>
    public void Open(OwnerScope scope, IReadOnlyList<WorkbenchStockService.PlacementEditorItem> requested)
    {
        owner = scope;
        shown = null;
        added = null;
        items = requested;
        pending.Clear();
        pendingRemote.Clear();
        saveError = null;
        matrix.ClearSelection();
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        items = [];
        pending.Clear();
        pendingRemote.Clear();
        saveError = null;
        matrix.ClearSelection();
    }

    public override void Draw()
    {
        reviewRegistry.BeginFrame();
        try
        {
            DrawMatrix();
        }
        finally
        {
            reviewRegistry.EndFrame();
        }
    }

    private void DrawMatrix()
    {
        var current = runtime();
        if (owner is not { } scope || !current.Owner.Matches(scope))
        {
            // Placement belongs to the character it was opened for.
            IsOpen = false;
            return;
        }
        var plan = ListingPlanCatalog.OwnerPlan(current.State, scope);
        var now = DateTime.UtcNow;
        var resolved = items.Select(item =>
        {
            var key = (item.ItemId, item.Quality);
            var reserve = plan?.Reserves.FirstOrDefault(rule => rule.Enabled && rule.ItemId == item.ItemId && rule.Quality == item.Quality);
            var storedLocal = reserve?.Location ?? new StowageRoutingPolicy();
            var storedRemote = reserve?.RemoteHomes is { RetainerIds.Count: > 0 } stored ? stored : null;
            // An edit shows at once; the stored rule takes over once it matches or the edit has had time to land.
            if (pending.TryGetValue(key, out var edit) &&
                (edit.Until <= now || WorkbenchStockService.SameLocation(edit.Routing, storedLocal)))
                pending.Remove(key);
            if (pendingRemote.TryGetValue(key, out var remoteEdit) &&
                (remoteEdit.Until <= now || ReserveRemoteHomes.Same(remoteEdit.Remote, storedRemote)))
                pendingRemote.Remove(key);
            return (Item: item, Key: key,
                Local: pending.TryGetValue(key, out edit) ? edit.Routing : storedLocal,
                Remote: pendingRemote.TryGetValue(key, out remoteEdit) ? remoteEdit.Remote : storedRemote);
        }).ToArray();

        var target = DrawCharacterBar(current, scope, resolved.Select(row => row.Remote));
        DrawSaveError();
        var local = target.Matches(scope);
        var stock = local ? null : courier.Stock(current, target);
        var rows = resolved.Select(row =>
        {
            var remoteHere = row.Remote is { } remote && remote.Character.Matches(target);
            var routing = local ? row.Local
                : remoteHere ? new StowageRoutingPolicy { PreferredRetainerIds = [.. row.Remote!.RetainerIds] }
                : new StowageRoutingPolicy();
            return new PlacementMatrixRow<(uint, ItemQualityPolicy)>(
                row.Key,
                $"{row.Item.ItemId}-{row.Item.Quality}",
                $"{row.Item.ItemName} {TransferPresentation.QualityLabel(row.Item.Quality, anyLabel: "NQ")}",
                PlacementMatrixData.BagHoldings(current, row.Item.ItemId, row.Item.Quality).ToString("N0"),
                routing,
                local
                    ? PlacementMatrixData.Holdings(current, row.Item.ItemId, row.Item.Quality)
                    : PlacementMatrixData.Holdings(current, row.Item.ItemId, row.Item.Quality, target, stock))
            {
                Elsewhere = HasHome(routing) ? null : Elsewhere(current, scope, target, row.Local, row.Remote),
            };
        }).ToArray();
        var columns = PlacementMatrixData.Columns(current, rows.Select(row => row.Routing), local ? null : target);
        matrix.Draw(rows, rows, columns, changes =>
        {
            var edits = new List<WorkbenchStockService.PlacementEditLine>();
            foreach (var (key, routing) in changes)
            {
                var item = items.FirstOrDefault(candidate => candidate.ItemId == key.ItemId && candidate.Quality == key.Quality);
                if (item is null) continue;
                // Shown at once by the same rules the save applies; the save itself goes through the shared path.
                var row = resolved.First(candidate => candidate.Key == key);
                var next = ReservePlacement.Resolve(scope, target, row.Local, row.Remote, routing);
                if (!next.Changed) continue;
                if (next.LocalChanged)
                    pending[key] = (StowageRoutingPolicy.CopyOf(next.Local), now + PendingLifetime);
                if (next.RemoteChanged)
                    pendingRemote[key] = (ReserveRemoteHomes.CopyOf(next.Remote), now + PendingLifetime);
                var edited = StowageRoutingPolicy.CopyOf(routing);
                edits.Add(new(item, (_, _) => edited));
            }
            if (edits.Count > 0)
                service.QueuePlacementEdit(scope, target, edits, Saved);
            return true;
        }, anySpace: local);
    }

    /// <summary>
    /// A save's result, on the framework thread: a refused or unsaved item drops its pending edit at once (so it shows
    /// the stored homes again and the next edit starts from them) and the reason shows above the matrix.
    /// </summary>
    private void Saved(WorkbenchStockService.PlacementEditResult result)
    {
        var failed = result.Items.Where(outcome => outcome.Outcome is "refused" or "not_applied" && outcome.Quality is not null).ToArray();
        foreach (var outcome in failed)
        {
            pending.Remove((outcome.ItemId, outcome.Quality!.Value));
            pendingRemote.Remove((outcome.ItemId, outcome.Quality.Value));
        }
        if (failed.Length > 0)
            saveError = failed.Length == 1
                ? $"Not saved: {failed[0].Message}"
                : $"Not saved for {failed.Length:N0} items: {string.Join(" ", failed.Select(outcome => outcome.Message))}";
        else if (result.Items.Any(outcome => outcome.Outcome == "applied"))
            saveError = null;
    }

    private void DrawSaveError()
    {
        if (string.IsNullOrWhiteSpace(saveError))
            return;
        var shown = DalamudUiLayout.ElideText(saveError, ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(ErrorColor, shown);
        if (shown.Length != saveError.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(saveError);
    }

    private static bool HasHome(StowageRoutingPolicy routing) => routing.AnySpace || routing.PreferredRetainerIds.Count > 0;

    /// <summary>Where a row with no home on the shown character goes instead; null when it has no home anywhere.</summary>
    private static string? Elsewhere(QuartermasterRuntimeSnapshot runtime, OwnerScope owner, OwnerScope target,
        StowageRoutingPolicy local, ReserveRemoteHomes? remote)
    {
        var parts = new List<string>();
        if (!target.Matches(owner) && HasHome(local))
            parts.Add($"{CourierSession.CharacterLabel(owner)} · {TransferPresentation.RouteSummary(local, runtime.Retainers, owner)}");
        if (remote is not null && !remote.Character.Matches(target))
            parts.Add(RemoteSummary(runtime, remote));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>"Dredgen Hope · Gringle → Scrongle": a remote home list by name.</summary>
    internal static string RemoteSummary(QuartermasterRuntimeSnapshot runtime, ReserveRemoteHomes remote) =>
        $"{CourierSession.CharacterLabel(remote.Character)} · " +
        TransferPresentation.RouteSummary(new StowageRoutingPolicy { PreferredRetainerIds = [.. remote.RetainerIds] }, runtime.Retainers, remote.Character);

    /// <summary>
    /// One pill per character: the owner, then each character a row already has homes on, then one added
    /// from + (any other character the courier can deliver to). Returns the character the matrix shows.
    /// </summary>
    private OwnerScope DrawCharacterBar(QuartermasterRuntimeSnapshot runtime, OwnerScope scope, IEnumerable<ReserveRemoteHomes?> remotes)
    {
        var homed = remotes.OfType<ReserveRemoteHomes>().Select(remote => remote.Character)
            .Where(character => !character.Matches(scope))
            .DistinctBy(character => (character.LocalContentId, character.HomeWorldId))
            .OrderBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pills = new List<OwnerScope> { scope };
        pills.AddRange(homed);
        if (added is { } extra && !pills.Any(character => character.Matches(extra)))
            pills.Add(extra);
        if (shown is not { } chosen || !pills.Any(character => character.Matches(chosen)))
            shown = scope;
        var target = shown;

        var style = ImGui.GetStyle();
        var first = true;
        foreach (var character in pills)
        {
            var label = $"{CourierSession.CharacterLabel(character)}##placement-character:{character.LocalContentId}";
            if (!first)
                DalamudUiLayout.SameLineIfFits(label);
            first = false;
            var selected = target.Matches(character);
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonActive]);
            var pick = character;
            if (ImGui.Button(label))
                Show(pick);
            if (selected)
                ImGui.PopStyleColor();
            reviewRegistry.RegisterLastSelectable(
                $"quartermaster.placement-editor.character.{character.LocalContentId}",
                $"Place items on {character.CharacterName}'s retainers",
                true,
                selected,
                () => Show(pick),
                selected ? "Selected" : "Available");
        }

        var others = courier.DestinationCharacters(runtime)
            .Where(character => !pills.Any(shownPill => shownPill.Matches(character)))
            .ToArray();
        ImGui.SameLine();
        ImGui.BeginDisabled(others.Length == 0);
        if (ImGui.Button("+##RQPlacementAddCharacter"))
            ImGui.OpenPopup(AddCharacterPopup);
        ImGui.EndDisabled();
        if (others.Length == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("No other known character");
        foreach (var other in others)
        {
            var pick = other;
            reviewRegistry.RegisterLastButton(
                $"quartermaster.placement-editor.add-character.{other.LocalContentId}",
                $"Place items on {other.CharacterName}'s retainers",
                true,
                () => Add(pick),
                "Available");
        }
        if (DalamudOwnedPopup.BeginOpaquePopup(AddCharacterPopup))
        {
            foreach (var other in others)
                if (ImGui.Selectable($"{CourierSession.CharacterLabel(other)}##add:{other.LocalContentId}"))
                    Add(other);
            ImGui.EndPopup();
        }
        ImGui.Separator();
        return target;
    }

    private void Add(OwnerScope character)
    {
        added = character with { };
        Show(character);
    }

    /// <summary>Switches the matrix to another character; the row selection belongs to the old one.</summary>
    private void Show(OwnerScope character)
    {
        if (shown?.Matches(character) != true)
            matrix.ClearSelection();
        shown = character with { };
    }
}
