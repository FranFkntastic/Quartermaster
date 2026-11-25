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
/// level-0 rule for an item that has none. An item's homes are one ranked list across every character of the
/// account: a character bar above the matrix switches its columns to another character's retainers, cells show
/// each home's global rank (#1 fills first) and its cap, and an edit there adds or ranks that character's
/// retainers in the same list. A row with no home on the shown character reads dimmed with where it goes.
/// </summary>
internal sealed class PlacementEditorWindow : Window
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(5);
    private const string AddCharacterPopup = "RQPlacementAddCharacter";

    private readonly Func<QuartermasterRuntimeSnapshot> runtime;
    private readonly WorkbenchStockService service;
    private readonly CourierSession courier;
    private readonly PlacementMatrix<(uint ItemId, ItemQualityPolicy Quality)> matrix;
    private readonly Dictionary<(uint ItemId, ItemQualityPolicy Quality), (List<PlacementHome> Homes, DateTime Until)> pending = [];
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
        saveError = null;
        matrix.ClearSelection();
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        items = [];
        pending.Clear();
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
            IReadOnlyList<PlacementHome> stored = reserve?.Homes ?? [];
            // An edit shows at once; the stored rule takes over once it matches or the edit has had time to land.
            if (pending.TryGetValue(key, out var edit) && (edit.Until <= now || ReservePlacement.Same(edit.Homes, stored)))
                pending.Remove(key);
            return (Item: item, Key: key, Homes: pending.TryGetValue(key, out edit) ? edit.Homes : stored);
        }).ToArray();

        var target = DrawCharacterBar(current, scope, resolved.SelectMany(row => row.Homes));
        DrawSaveError();
        var local = target.Matches(scope);
        var stock = local ? null : courier.Stock(current, target);
        var rows = resolved.Select(row =>
        {
            var (routing, ranks, caps) = PlacementMatrixData.Homes(row.Homes, target);
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
                Ranks = ranks,
                Caps = caps,
                NextRank = row.Homes.Count + 1,
                Elsewhere = ranks.Count == 0 && row.Homes.Count > 0 ? PlacementMatrixData.HomesSummary(current, row.Homes, scope) : null,
            };
        }).ToArray();
        var columns = PlacementMatrixData.Columns(current, rows.Select(row => row.Routing), local ? null : target);
        matrix.DrawHomes(rows, rows, columns, (operation, retainerId, cap, keys) =>
        {
            var edits = new List<WorkbenchStockService.PlacementEditLine>();
            foreach (var key in keys)
            {
                var item = items.FirstOrDefault(candidate => candidate.ItemId == key.ItemId && candidate.Quality == key.Quality);
                if (item is null) continue;
                // Shown at once by the same operation the save applies; the save itself goes through the shared path,
                // which re-reads the stored homes and every listed character's holdings.
                var row = resolved.First(candidate => candidate.Key == key);
                var holdings = rows.First(candidate => candidate.Key == key).Holdings
                    .Select(entry => new PlacementHolding(target, entry.Key, entry.Value));
                var next = ReservePlacement.Operate(operation, row.Homes, target, retainerId, cap, holdings);
                if (ReservePlacement.Same(row.Homes, next)) continue;
                pending[key] = (next, now + PendingLifetime);
                var character = target with { };
                edits.Add(new(item, (before, all) => ReservePlacement.Operate(operation, before, character, retainerId, cap, all)));
            }
            if (edits.Count > 0)
                service.QueuePlacementEdit(scope, edits, Saved);
            return true;
        });
    }

    /// <summary>
    /// A save's result, on the framework thread: a refused or unsaved item drops its pending edit at once (so it shows
    /// the stored homes again and the next edit starts from them) and the reason shows above the matrix.
    /// </summary>
    private void Saved(WorkbenchStockService.PlacementEditResult result)
    {
        var failed = result.Items.Where(outcome => outcome.Outcome is "refused" or "not_applied" && outcome.Quality is not null).ToArray();
        foreach (var outcome in failed)
            pending.Remove((outcome.ItemId, outcome.Quality!.Value));
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

    /// <summary>
    /// One pill per character: the owner, then each character a row already has homes on, then one added
    /// from + (any other character the courier can deliver to). Returns the character the matrix shows.
    /// </summary>
    private OwnerScope DrawCharacterBar(QuartermasterRuntimeSnapshot runtime, OwnerScope scope, IEnumerable<PlacementHome> homes)
    {
        var homed = homes.Select(home => home.Character)
            .Where(character => !ReservePlacement.SameCharacter(character, scope))
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
