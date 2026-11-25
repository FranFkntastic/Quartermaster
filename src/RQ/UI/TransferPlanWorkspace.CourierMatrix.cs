using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Drawing;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Windows;
using RQ.Courier;
using RQ.Domain;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The Courier plan's two views over the same lines: the allocation matrix (items by containers, each
/// cell a place's bounds and move) and the compact list, with the filter, view toggle and bulk place
/// editing they share. Every edit goes through <see cref="MutateRules"/> like the list's own inputs.
/// </summary>
internal sealed partial class TransferPlanWorkspace
{
    private enum CourierFilter { All, Moving, Attention }

    private enum CourierMatrixSort { ItemAscending, ItemDescending, MovesAscending, MovesDescending }

    private enum CourierCellKind : byte { Blank, Held, Suggested, Drain, Bounded, Pool }

    /// <summary>One character's bags or retainer as a matrix column, with what its header shows.</summary>
    private sealed record CourierColumn(
        OwnerScope Character,
        ulong? Retainer,
        string CharacterLabel,
        string ContainerLabel,
        string Label,
        string Slug,
        string ColumnId,
        string CellId,
        int? Free,
        string FreeText,
        bool IsOwner,
        float LabelWidth)
    {
        /// <summary>The character band this column belongs to, and the band's first and one-past-last column.</summary>
        public int Band { get; set; }
        public int BandStart { get; set; }
        public int BandEnd { get; set; }
        public (ulong? Character, ulong? Retainer) Key => (Character.LocalContentId, Retainer);
    }

    /// <summary>
    /// A matrix cell as drawn: the listed place (an index into the allocation's places, -1 when unlisted),
    /// what the container holds (null when never observed), and the text it shows.
    /// </summary>
    private readonly record struct CourierCellView(
        CourierCellKind Kind,
        int Place,
        int? Held,
        string Top,
        string Bottom,
        Vector4? BottomColor);

    private static readonly Vector4 BandDividerColor = new(.32f, .32f, .32f, 1f);
    private static readonly Vector4 MatrixHoverColor = new(.14f, .19f, .23f, 1f);
    private const float MatrixHeaderAngle = 48f * MathF.PI / 180f;
    private const string CourierCellPopup = "##courier-cell-bounds";

    private static readonly AgentBridgeActionArgumentSchema CellArguments = new(
    [
        new("bounds", AgentBridgeActionArgumentKind.String, Required: false),
        new("remove", AgentBridgeActionArgumentKind.Boolean, Required: false),
    ]);

    private static readonly AgentBridgeActionArgumentSchema BoundsArguments = new(
    [
        new("bounds", AgentBridgeActionArgumentKind.String, Required: true),
    ]);

    // ---- Shared view state ---------------------------------------------------------------

    private CourierFilter courierFilter;
    private bool courierAllContainers;
    private CourierMatrixSort courierMatrixSort;
    private readonly Dictionary<(ulong? Character, ulong? Retainer), OwnerScope> courierPinnedColumns = [];
    private int courierPinnedVersion;
    private (ulong? Character, ulong? Retainer)? courierBulkContainer;
    private string courierBulkBounds = string.Empty;

    private IReadOnlyList<TransferWorkbenchRow>? courierFilterSource;
    private CourierFilter? courierFilterApplied;
    private IReadOnlyList<TransferWorkbenchRow> courierFilteredRows = [];
    private (int All, int Moving, int Attention) courierCounts;

    private IReadOnlyList<TransferWorkbenchRow>? courierMatrixSource;
    private CourierMatrixSort? courierMatrixSortApplied;
    private TransferWorkbenchRow[] courierMatrixRows = [];
    private Guid[] courierMatrixKeys = [];

    private TransferWorkbenchProjection? courierColumnsProjection;
    private bool courierColumnsAll;
    private int courierColumnsPinned = -1;
    private CourierColumn[] courierShownColumns = [];
    private CourierColumn[] courierPlanColumns = [];
    private CourierColumn[] courierReachableColumns = [];
    private CourierColumn[] courierBulkChoices = [];
    private AgentBridgeActionArgumentSchema courierBulkSchema = new([]);

    private CourierColumn[]? courierCellsColumns;
    private float courierCellsWidth;
    private readonly Dictionary<Guid, (CourierAllocation Allocation, CourierCellView[] Cells)> courierCells = [];

    private readonly Dictionary<Guid, (CourierAllocation Allocation, bool Moving, string Text)> courierSummaries = [];
    private readonly Dictionary<Guid, (string Full, float Width, string Shown)> courierElided = [];
    private readonly Dictionary<Guid, (string Full, float Width, string Shown)> courierMatrixNames = [];

    /// <summary>The cell whose bounds popover is open: its line and container.</summary>
    private (Guid RuleId, OwnerScope Character, ulong? Retainer)? courierCellEdit;
    private bool courierCellOpenRequested;
    private Vector2 courierCellAnchor;
    private string courierCellText = string.Empty;
    private bool courierCellInvalid;
    private float courierFrozenRight;

    /// <summary>Clears what belongs to the plan that was open; the view mode is kept.</summary>
    private void ResetCourierView()
    {
        courierFilter = CourierFilter.All;
        courierPinnedColumns.Clear();
        courierPinnedVersion++;
        courierBulkContainer = null;
        courierBulkBounds = string.Empty;
        courierCellEdit = null;
        courierCellOpenRequested = false;
        courierCells.Clear();
        courierSummaries.Clear();
        courierElided.Clear();
        courierMatrixNames.Clear();
    }

    // ---- View toggle ---------------------------------------------------------------------

    /// <summary>The header button names the view it switches to.</summary>
    private string CourierViewLabel() =>
        workbench.CourierMatrixView ? "Compact list###RQCourierViewMode" : "Matrix###RQCourierViewMode";

    private void DrawCourierViewToggle(string label)
    {
        DalamudUiLayout.SameLineIfFits(label);
        if (ImGui.Button(label))
            workbench.CourierMatrixView = !workbench.CourierMatrixView;
        reviewRegistry.RegisterLastButton(
            "quartermaster.courier.view.toggle",
            workbench.CourierMatrixView ? "Show the Courier plan as a compact list" : "Show the Courier plan as a matrix",
            true,
            () => workbench.CourierMatrixView = !workbench.CourierMatrixView,
            workbench.CourierMatrixView ? "Matrix" : "Compact list");
    }

    // ---- Filter --------------------------------------------------------------------------

    private static bool CourierMoving(TransferWorkbenchRow row) => row.Rule.Enabled && row.Distribution is { Moving: > 0 };

    private static bool CourierAttention(TransferWorkbenchRow row) => row.Rule.Enabled && CourierNeedsUser(row);

    /// <summary>
    /// The plan's rows that pass the filter, in plan order. The list instance is kept while the rows and
    /// filter stay the same, so the table's sort cache holds. Hidden rows keep their selection.
    /// </summary>
    private IReadOnlyList<TransferWorkbenchRow> CourierVisibleRows(TransferWorkbenchProjection projection)
    {
        var rows = projection.Rows;
        if (!ReferenceEquals(courierFilterSource, rows))
        {
            courierFilterSource = rows;
            courierFilterApplied = null;
            int moving = 0, attention = 0;
            foreach (var row in rows)
            {
                if (CourierMoving(row)) moving++;
                if (CourierAttention(row)) attention++;
            }
            courierCounts = (rows.Count, moving, attention);
        }
        if (courierFilterApplied != courierFilter)
        {
            courierFilteredRows = courierFilter switch
            {
                CourierFilter.Moving => rows.Where(CourierMoving).ToArray(),
                CourierFilter.Attention => rows.Where(CourierAttention).ToArray(),
                _ => rows,
            };
            courierFilterApplied = courierFilter;
        }
        return courierFilteredRows;
    }

    /// <summary>
    /// The strip under the summary: filter pills, then the view's own controls (containers shown in
    /// the matrix, expand or collapse every visible line in the list).
    /// </summary>
    private void DrawCourierControls(QuartermasterRuntimeSnapshot runtime, TransferWorkbenchProjection projection)
    {
        var rows = CourierVisibleRows(projection);
        var (all, moving, attention) = courierCounts;
        ImGui.AlignTextToFramePadding();
        DrawFilterPill(CourierFilter.All, $"All · {all:N0}", all, null, first: true);
        DrawFilterPill(CourierFilter.Moving, $"Moving · {moving:N0}", moving, TransferPresentation.SendColor);
        DrawFilterPill(CourierFilter.Attention, $"Attention · {attention:N0}", attention, AmberColor);

        if (workbench.CourierMatrixView)
        {
            var allLabel = courierAllContainers ? "Used containers only##RQCourierAllContainers" : "+ Show all containers##RQCourierAllContainers";
            DalamudUiLayout.SameLineIfFits(allLabel);
            if (ImGui.SmallButton(allLabel))
                courierAllContainers = !courierAllContainers;
            reviewRegistry.RegisterLastButton(
                "quartermaster.courier.matrix.all-containers",
                courierAllContainers ? "Show only the containers this plan uses" : "Show every reachable container in the matrix",
                true,
                () => courierAllContainers = !courierAllContainers,
                courierAllContainers ? "All containers" : "Used containers");
            if (!courierAllContainers)
                DrawAddColumn(runtime);
            return;
        }

        var anyOpen = false;
        foreach (var row in rows)
            if (CourierExpanded(row.Rule.Id)) { anyOpen = true; break; }
        var expandLabel = anyOpen ? "Collapse all##RQCourierExpandAll" : "Expand all##RQCourierExpandAll";
        DalamudUiLayout.SameLineIfFits(expandLabel);
        ImGui.BeginDisabled(rows.Count == 0);
        if (ImGui.SmallButton(expandLabel))
            SetCourierExpanded(rows.Select(row => row.Rule.Id), !anyOpen);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastButton(
            "quartermaster.courier.toggle-expand-all",
            anyOpen ? "Collapse every visible courier line" : "Expand every visible courier line",
            rows.Count > 0,
            () =>
            {
                var current = CourierVisibleRows(projection);
                var open = current.Any(row => CourierExpanded(row.Rule.Id));
                SetCourierExpanded(current.Select(row => row.Rule.Id), !open);
            },
            anyOpen ? "Some expanded" : "All collapsed");
    }

    private void SetCourierExpanded(IEnumerable<Guid> ids, bool open)
    {
        foreach (var id in ids)
        {
            courierSeen.Add(id);
            courierExpansion.SetExpanded(CourierKey(id), open);
        }
    }

    private void DrawFilterPill(CourierFilter filter, string label, int count, Vector4? color, bool first = false)
    {
        if (!first)
            DalamudUiLayout.SameLineIfFits(ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f));
        var active = courierFilter == filter;
        var tone = color ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var text = active || count > 0 ? tone : QuartermasterPalette.Dim;
        ImGui.PushStyleColor(ImGuiCol.Button, active ? tone with { W = .22f } : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, tone with { W = .30f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, tone with { W = .38f });
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleColor(ImGuiCol.Border, active ? tone with { W = .75f } : Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, active ? 1f : 0f);
        var clicked = ImGui.SmallButton($"{label}##RQCourierFilter{(int)filter}");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        if (clicked)
            courierFilter = active ? CourierFilter.All : filter;
        var name = filter.ToString().ToLowerInvariant();
        reviewRegistry.RegisterLastSelectable(
            $"quartermaster.courier.filter.{name}",
            $"Show {(filter == CourierFilter.All ? "every" : name)} courier line{(filter == CourierFilter.All ? "" : "s")}",
            true,
            active,
            () => courierFilter = filter,
            $"{count:N0}");
    }

    /// <summary>Shows one more container as a column without listing it on any line.</summary>
    private void DrawAddColumn(QuartermasterRuntimeSnapshot runtime)
    {
        const string label = "+ Container…##RQCourierAddColumn";
        const string popup = "RQCourierAddColumn";
        DalamudUiLayout.SameLineIfFits(label);
        if (ImGui.SmallButton(label))
            ImGui.OpenPopup(popup);
        if (!DalamudOwnedPopup.BeginOpaquePopup(popup))
            return;
        DrawContainerMenus(runtime, courier.DestinationCharacters(runtime), (character, retainer) =>
            {
                courierPinnedColumns[(character.LocalContentId, retainer)] = character with { };
                courierPinnedVersion++;
            },
            null, null, "quartermaster.courier.matrix.add-column", "Show");
        ImGui.EndPopup();
    }

    // ---- Compact-list summaries ----------------------------------------------------------

    /// <summary>
    /// What a list row says beside its name: the route while it moves, else where it stands
    /// ("Bags =0 · Gringle Pool"). Cached per solved line.
    /// </summary>
    private string CourierLineSummary(TransferWorkbenchRow row, CourierAllocation allocation)
    {
        var moving = row.Rule.Enabled && allocation.Moving > 0;
        if (courierSummaries.TryGetValue(row.Rule.Id, out var hit) && ReferenceEquals(hit.Allocation, allocation) && hit.Moving == moving)
            return hit.Text;
        var runtime = row.Runtime;
        var text = moving
            ? CourierSession.RouteSummary(runtime, allocation)
            : string.Join(" · ", allocation.Places.Select(spot =>
                $"{CourierSession.Side(runtime, spot.Character, spot.Retainer)} {PlaceBoundsShort(spot)}"));
        courierSummaries[row.Rule.Id] = (allocation, moving, text);
        return text;
    }

    private static string PlaceBoundsShort(CourierSpot spot) => (spot.Min, spot.Max) switch
    {
        (null, null) => "Pool",
        ({ } low, { } high) when low == high => $"={low:N0}",
        ({ } low, { } high) => $"{low:N0}–{high:N0}",
        ({ } low, null) => $"{low:N0}+",
        (null, { } high) => $"≤{high:N0}",
    };

    private static string ElideCached(Dictionary<Guid, (string Full, float Width, string Shown)> cache, Guid id, string full, float width)
    {
        if (cache.TryGetValue(id, out var hit) && ReferenceEquals(hit.Full, full) && MathF.Abs(hit.Width - width) < .5f)
            return hit.Shown;
        var shown = DalamudUiLayout.ElideText(full, width);
        if (shown.Length == full.Length)
            shown = full;
        cache[id] = (full, width, shown);
        return shown;
    }

    private string ElideCached(Guid id, string full, float width) => ElideCached(courierElided, id, full, width);

    // ---- Columns -------------------------------------------------------------------------

    /// <summary>
    /// The matrix's columns: every container a line lists or is offered (its suggested place), plus any
    /// shown by hand; every reachable container when asked, or when the plan uses none yet. The owner's
    /// containers come first, then other characters by name; each character's bags, then its retainers in
    /// game order. Rebuilt only when the projection or the shown set changes.
    /// </summary>
    private void EnsureCourierColumns(QuartermasterRuntimeSnapshot runtime, TransferWorkbenchProjection projection)
    {
        if (ReferenceEquals(courierColumnsProjection, projection) && courierColumnsAll == courierAllContainers &&
            courierColumnsPinned == courierPinnedVersion)
            return;
        courierColumnsProjection = projection;
        courierColumnsAll = courierAllContainers;
        courierColumnsPinned = courierPinnedVersion;

        var free = new Dictionary<(ulong?, ulong?), int>();
        foreach (var location in courier.Locations(runtime))
            free.TryAdd((location.Character.LocalContentId, location.Retainer), location.Inventory.FreeSlots);

        var used = new Dictionary<(ulong?, ulong?), (OwnerScope Character, ulong? Retainer)>();
        void Use(Dictionary<(ulong?, ulong?), (OwnerScope, ulong?)> into, OwnerScope character, ulong? retainer)
        {
            if (character.LocalContentId is not null)
                into.TryAdd((character.LocalContentId, retainer), (character, retainer));
        }
        foreach (var row in projection.Rows)
        {
            if (row.Distribution is not { } allocation)
                continue;
            foreach (var spot in allocation.Places)
                Use(used, spot.Character, spot.Retainer);
            if (allocation.Suggestion is { } suggestion)
                Use(used, suggestion.Character, suggestion.Retainer);
        }
        var reachable = new Dictionary<(ulong?, ulong?), (OwnerScope, ulong?)>();
        foreach (var character in courier.DestinationCharacters(runtime))
        {
            Use(reachable, character, null);
            foreach (var retainer in CourierSession.Retainers(runtime, character))
                Use(reachable, character, retainer.RetainerId);
        }

        courierPlanColumns = Order(runtime, used, free);
        courierReachableColumns = Order(runtime, reachable, free);
        var shown = new Dictionary<(ulong?, ulong?), (OwnerScope, ulong?)>(used);
        foreach (var (key, character) in courierPinnedColumns)
            shown.TryAdd(key, (character, key.Retainer));
        if (courierAllContainers || used.Count == 0)
            foreach (var (key, value) in reachable)
                shown.TryAdd(key, value);
        courierShownColumns = Order(runtime, shown, free);
        courierBulkChoices = courierPlanColumns.Concat(courierReachableColumns).DistinctBy(column => column.Key).ToArray();
        courierBulkSchema = new AgentBridgeActionArgumentSchema(
            [new("container", AgentBridgeActionArgumentKind.Enum, AllowedValues: courierBulkChoices.Select(column => column.Label).Distinct().ToArray())]);
        // A target no longer shown anywhere falls back to the first column.
        if (courierBulkContainer is { } target && !courierShownColumns.Concat(courierReachableColumns).Any(column => column.Key == target))
            courierBulkContainer = null;
    }

    private static CourierColumn[] Order(QuartermasterRuntimeSnapshot runtime,
        Dictionary<(ulong?, ulong?), (OwnerScope Character, ulong? Retainer)> containers,
        Dictionary<(ulong?, ulong?), int> free)
    {
        var ownerId = runtime.Owner.LocalContentId;
        var columns = new List<CourierColumn>(containers.Count);
        var characters = containers.Values
            .GroupBy(container => container.Character.LocalContentId)
            .OrderBy(group => group.Key == ownerId ? 0 : 1)
            .ThenBy(group => group.First().Character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key);
        var band = 0;
        foreach (var group in characters)
        {
            var character = group.First().Character;
            var order = CourierSession.Retainers(runtime, character)
                .Select((retainer, index) => (retainer.RetainerId, index))
                .ToDictionary(entry => entry.RetainerId, entry => entry.index);
            var start = columns.Count;
            foreach (var (who, retainer) in group
                         .OrderBy(container => container.Retainer is null ? -1 : order.GetValueOrDefault(container.Retainer.Value, int.MaxValue))
                         .ThenBy(container => container.Retainer))
            {
                var characterLabel = CourierSession.CharacterLabel(who);
                var containerLabel = CourierSession.ContainerLabel(runtime, retainer);
                var slug = $"{who.LocalContentId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.{retainer?.ToString(CultureInfo.InvariantCulture) ?? "bags"}";
                int? slots = free.TryGetValue((who.LocalContentId, retainer), out var value) ? value : null;
                columns.Add(new CourierColumn(who, retainer, characterLabel, containerLabel, $"{characterLabel} · {containerLabel}",
                    slug, $"##c-{slug}", $"##cell-{slug}", slots, slots?.ToString("N0") ?? "—", who.LocalContentId == ownerId,
                    ImGui.CalcTextSize(containerLabel).X) { Band = band, BandStart = start });
            }
            for (var index = start; index < columns.Count; index++)
                columns[index].BandEnd = columns.Count;
            band++;
        }
        return [.. columns];
    }

    private CourierColumn? FindColumn((ulong?, ulong?) key)
    {
        foreach (var column in courierShownColumns)
            if (column.Key == key) return column;
        foreach (var column in courierReachableColumns)
            if (column.Key == key) return column;
        foreach (var column in courierPlanColumns)
            if (column.Key == key) return column;
        return null;
    }

    // ---- Rows ----------------------------------------------------------------------------

    private static int MovesRank(TransferWorkbenchRow row) =>
        !row.Rule.Enabled ? 3 : CourierNeedsUser(row) ? 0 : row.Distribution is { Moving: > 0 } ? 1 : 2;

    /// <summary>The visible rows in the matrix's own sort, with their keys for row selection.</summary>
    private TransferWorkbenchRow[] CourierMatrixRows(TransferWorkbenchProjection projection)
    {
        var visible = CourierVisibleRows(projection);
        if (ReferenceEquals(courierMatrixSource, visible) && courierMatrixSortApplied == courierMatrixSort)
            return courierMatrixRows;
        IEnumerable<TransferWorkbenchRow> sorted = courierMatrixSort switch
        {
            CourierMatrixSort.ItemDescending => visible.OrderByDescending(row => row.Rule.ItemName, StringComparer.OrdinalIgnoreCase),
            CourierMatrixSort.MovesAscending => visible.OrderBy(MovesRank).ThenBy(row => row.Rule.ItemName, StringComparer.OrdinalIgnoreCase),
            CourierMatrixSort.MovesDescending => visible.OrderByDescending(MovesRank).ThenBy(row => row.Rule.ItemName, StringComparer.OrdinalIgnoreCase),
            _ => visible.OrderBy(row => row.Rule.ItemName, StringComparer.OrdinalIgnoreCase),
        };
        courierMatrixRows = sorted.ToArray();
        courierMatrixKeys = courierMatrixRows.Select(row => row.Rule.Id).ToArray();
        courierMatrixSource = visible;
        courierMatrixSortApplied = courierMatrixSort;
        return courierMatrixRows;
    }

    // ---- Cells ---------------------------------------------------------------------------

    /// <summary>A row's cells across the shown columns, rebuilt when its solved line or the columns change.</summary>
    private CourierCellView[] CourierCellsFor(TransferWorkbenchRow row, CourierAllocation allocation, CourierColumn[] columns, float cellWidth)
    {
        if (!ReferenceEquals(courierCellsColumns, columns) || MathF.Abs(courierCellsWidth - cellWidth) >= .5f)
        {
            courierCells.Clear();
            courierCellsColumns = columns;
            courierCellsWidth = cellWidth;
        }
        if (courierCells.TryGetValue(row.Rule.Id, out var hit) && ReferenceEquals(hit.Allocation, allocation))
            return hit.Cells;

        var cells = new CourierCellView[columns.Length];
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index];
            var place = -1;
            for (var candidate = 0; candidate < allocation.Places.Length; candidate++)
            {
                var spot = allocation.Places[candidate];
                if (spot.Retainer == column.Retainer && spot.Character.LocalContentId == column.Character.LocalContentId)
                {
                    place = candidate;
                    break;
                }
            }
            cells[index] = place >= 0
                ? ListedCell(allocation.Places[place], place, cellWidth)
                : UnlistedCell(allocation, column);
        }
        courierCells[row.Rule.Id] = (allocation, cells);
        return cells;
    }

    private static CourierCellView UnlistedCell(CourierAllocation allocation, CourierColumn column)
    {
        static bool At(CourierSpot spot, CourierColumn column) =>
            spot.Retainer == column.Retainer && spot.Character.LocalContentId == column.Character.LocalContentId;
        int? held = column.Free is null ? null : 0;
        foreach (var holder in allocation.Holders)
            if (At(holder, column)) { held = holder.Has; break; }
        if (allocation.Suggestion is { } suggestion && At(suggestion, column))
            return new(CourierCellKind.Suggested, -1, suggestion.Has, suggestion.Has is { } units ? CompactNumber(units) : "—", "+ add",
                QuartermasterPalette.Amber);
        return held is > 0 and var units2
            ? new(CourierCellKind.Held, -1, held, CompactNumber(units2), string.Empty, null)
            : new(CourierCellKind.Blank, -1, held, string.Empty, string.Empty, null);
    }

    private static CourierCellView ListedCell(CourierSpot spot, int place, float cellWidth)
    {
        var delta = spot.Delta;
        var deltaText = delta > 0 ? $"+{CompactNumber(delta)}" : delta < 0 ? $"−{CompactNumber(-delta)}" : string.Empty;
        Vector4? deltaColor = delta > 0 ? QuartermasterPalette.Green : delta < 0 ? QuartermasterPalette.Amber : null;
        if (spot is { Min: 0, Max: 0 })
            return new(CourierCellKind.Drain, place, spot.Has, "0", delta < 0 ? deltaText : string.Empty, deltaColor);
        if (!spot.IsPool)
        {
            var full = CourierBoundsText.Format(spot.Min, spot.Max)
                .Replace("<=", "≤", StringComparison.Ordinal)
                .Replace("-", "–", StringComparison.Ordinal);
            var room = cellWidth - (8f * ImGuiHelpers.GlobalScale);
            var text = ImGui.CalcTextSize(full).X * .85f <= room ? full : CompactBounds(spot.Min, spot.Max);
            return new(CourierCellKind.Bounded, place, spot.Has, text, deltaText, deltaColor);
        }
        var bottom = delta != 0 ? deltaText : spot.Has is { } has ? CompactNumber(has) : "—";
        return new(CourierCellKind.Pool, place, spot.Has, "Pool", bottom, delta != 0 ? deltaColor : null);
    }

    /// <summary>"2,426" below ten thousand, else "43.9k", "120k", "1.2M".</summary>
    private static string CompactNumber(int value)
    {
        var culture = CultureInfo.InvariantCulture;
        return value switch
        {
            < 10_000 => value.ToString("N0", culture),
            < 100_000 => (value / 1000d).ToString("0.#", culture) + "k",
            < 1_000_000 => (value / 1000d).ToString("0", culture) + "k",
            _ => (value / 1_000_000d).ToString("0.##", culture) + "M",
        };
    }

    /// <summary>Bounds short enough for a cell: "9.99k", "5k–10k", "5k+", "≤10k".</summary>
    private static string CompactBounds(int? min, int? max)
    {
        static string Short(int value)
        {
            var culture = CultureInfo.InvariantCulture;
            return value switch
            {
                < 1000 => value.ToString(culture),
                < 10_000 => (value / 1000d).ToString("0.##", culture) + "k",
                < 1_000_000 => (value / 1000d).ToString("0.#", culture) + "k",
                _ => (value / 1_000_000d).ToString("0.##", culture) + "M",
            };
        }
        return (min, max) switch
        {
            ({ } low, { } high) when low == high => Short(low),
            ({ } low, { } high) => $"{Short(low)}–{Short(high)}",
            ({ } low, null) => $"{Short(low)}+",
            (null, { } high) => $"≤{Short(high)}",
            _ => string.Empty,
        };
    }

    // ---- Matrix --------------------------------------------------------------------------

    private readonly record struct MatrixLayout(float RowHeight, float Inner, float CellWidth, float LineHeight);

    /// <summary>
    /// Items down, containers across under their character, with Item, Moves and the line menu frozen
    /// on the left. Rows are virtualized; the add box sits under the table so adding never needs a scroll.
    /// </summary>
    private void DrawCourierMatrix(QuartermasterRuntimeSnapshot runtime, StowagePlan plan, TransferWorkbenchProjection projection)
    {
        renderedTransferRowCount = 0;
        EnsureCourierColumns(runtime, projection);
        var columns = courierShownColumns;
        var rows = CourierMatrixRows(projection);
        var keys = courierMatrixKeys;
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var lineHeight = ImGui.GetTextLineHeight();
        var avail = ImGui.GetContentRegionAvail();
        var itemWidth = Math.Clamp(avail.X * .24f, 155f * scale, 185f * scale);
        var movesWidth = 78f * scale;
        var menuWidth = Math.Max(22f * scale, DalamudUiLayout.ButtonWidth("…"));
        var cellWidth = 60f * scale;
        var longest = 0f;
        foreach (var column in columns)
            longest = Math.Max(longest, column.LabelWidth);
        var headerHeight = Math.Clamp((longest * MathF.Sin(MatrixHeaderAngle)) + lineHeight + (8f * scale), 3f * lineHeight, 140f * scale);
        var spacerWidth = Math.Clamp(longest * MathF.Cos(MatrixHeaderAngle), 40f * scale, 140f * scale);
        var rowHeight = Math.Max(34f * scale, (2f * lineHeight) + (2f * style.CellPadding.Y) + (2f * scale));
        var layout = new MatrixLayout(rowHeight, rowHeight - (2f * style.CellPadding.Y), cellWidth, lineHeight);

        var reserve = ImGui.GetFrameHeight() + (style.ItemSpacing.Y * 2f);
        var height = Math.Max(120f * scale, avail.Y - reserve);
        var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.NoSavedSettings;
        if (ImGui.BeginTable("##RQCourierMatrix", 3 + columns.Length + 1, flags, new Vector2(0f, height)))
        {
            try
            {
                ImGui.TableSetupScrollFreeze(3, 3);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, itemWidth);
                ImGui.TableSetupColumn("Moves", ImGuiTableColumnFlags.WidthFixed, movesWidth);
                ImGui.TableSetupColumn("##line-menu", ImGuiTableColumnFlags.WidthFixed, menuWidth);
                foreach (var column in columns)
                    ImGui.TableSetupColumn(column.ColumnId, ImGuiTableColumnFlags.WidthFixed, cellWidth);
                ImGui.TableSetupColumn("##spacer", ImGuiTableColumnFlags.WidthFixed, spacerWidth);

                DrawMatrixBands(columns, lineHeight);
                DrawMatrixNames(columns, headerHeight, lineHeight);
                DrawMatrixFreeSlots(columns);

                if (rows.Length == 0)
                {
                    if (projection.Rows.Count > 0)
                    {
                        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextDisabled("No matching items.");
                    }
                }
                else
                    renderedTransferRowCount = DalamudVirtualizedRows.Draw(rows, (row, index) => DrawMatrixRow(row, index, keys, columns, layout));
                DalamudTableSelectionRenderer.EndRows(selection);
            }
            finally
            {
                ImGui.EndTable();
            }
        }
        DrawCourierCellPopover(runtime, projection);
        DrawAddInput(runtime, plan);
    }

    /// <summary>Header row 1: each character's name across its columns, owner first and brighter.</summary>
    private void DrawMatrixBands(CourierColumn[] columns, float lineHeight)
    {
        var style = ImGui.GetStyle();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, lineHeight + (style.CellPadding.Y * 2f));
        ImGui.TableSetColumnIndex(2);
        courierFrozenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X + style.CellPadding.X;
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var rowBottom = rowTop + lineHeight + (style.CellPadding.Y * 2f);
        var windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X;
        var drawn = -1;
        for (var index = 0; index < columns.Length; index++)
        {
            if (!ImGui.TableSetColumnIndex(3 + index))
                continue;
            var column = columns[index];
            var cell = ImGui.GetCursorScreenPos();
            if (index > 0 && column.BandStart == index)
                DrawBandDivider(cell.X - style.CellPadding.X, rowTop, rowBottom);
            if (column.Band == drawn)
                continue;
            drawn = column.Band;
            // The name runs across the band's remaining columns from the first one in view, staying pinned past the frozen edge.
            var stride = ImGui.GetContentRegionAvail().X + (style.CellPadding.X * 2f);
            var left = Math.Max(courierFrozenRight + style.CellPadding.X, cell.X);
            var right = Math.Min(windowRight, cell.X + (stride * (column.BandEnd - index)) - (style.CellPadding.X * 2f));
            if (right > left)
            {
                var drawList = ImGui.GetWindowDrawList();
                drawList.PushClipRect(new Vector2(left, rowTop), new Vector2(right, rowBottom), false);
                drawList.AddText(new Vector2(left, cell.Y), ImGui.GetColorU32(column.IsOwner ? QuartermasterPalette.Info : QuartermasterPalette.Dim), column.CharacterLabel);
                drawList.PopClipRect();
                if (ImGui.IsMouseHoveringRect(new Vector2(left, rowTop), new Vector2(right, rowBottom)) &&
                    ImGui.IsWindowHovered())
                    ImGui.SetTooltip(column.CharacterLabel);
            }
        }
    }

    /// <summary>
    /// Header row 2: Item and Moves (click to sort), then each container's name slanted over its
    /// column. Clicking a container makes it the bulk bar's target.
    /// </summary>
    private void DrawMatrixNames(CourierColumn[] columns, float headerHeight, float lineHeight)
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, headerHeight);
        ImGui.TableSetColumnIndex(0);
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var labelOffset = headerHeight - lineHeight - (style.CellPadding.Y * 2f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + labelOffset);
        SortHeader("Item", "item", courierMatrixSort is CourierMatrixSort.ItemAscending or CourierMatrixSort.ItemDescending,
            courierMatrixSort == CourierMatrixSort.ItemDescending,
            () => courierMatrixSort = courierMatrixSort == CourierMatrixSort.ItemAscending ? CourierMatrixSort.ItemDescending : CourierMatrixSort.ItemAscending,
            rightAligned: false);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + labelOffset);
        SortHeader("Moves", "moves", courierMatrixSort is CourierMatrixSort.MovesAscending or CourierMatrixSort.MovesDescending,
            courierMatrixSort == CourierMatrixSort.MovesDescending,
            () => courierMatrixSort = courierMatrixSort == CourierMatrixSort.MovesAscending ? CourierMatrixSort.MovesDescending : CourierMatrixSort.MovesAscending,
            rightAligned: true);

        var clipMin = new Vector2(courierFrozenRight, rowTop);
        var clipMax = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X, rowTop + headerHeight);
        var text = ImGui.GetColorU32(ImGuiCol.Text);
        var target = courierBulkContainer ?? (courierPlanColumns.Length > 0 ? courierPlanColumns[0].Key : null);
        for (var index = 0; index < columns.Length; index++)
        {
            if (!ImGui.TableSetColumnIndex(3 + index))
                continue;
            var column = columns[index];
            var cell = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var drawList = ImGui.GetWindowDrawList();
            if (index > 0 && column.BandStart == index)
                DrawBandDivider(cell.X - style.CellPadding.X, rowTop, rowTop + headerHeight);
            var targeted = target == column.Key && selection.Count >= 2;
            if (targeted)
                drawList.AddRectFilled(new Vector2(cell.X - style.CellPadding.X, rowTop), new Vector2(cell.X + width + style.CellPadding.X, rowTop + headerHeight),
                    ImGui.GetColorU32(QuartermasterPalette.Info with { W = .16f }));
            var pivot = new Vector2(cell.X + (width / 2f) - (4f * scale), rowTop + headerHeight - (6f * scale));
            drawList.PushClipRect(clipMin, clipMax, false);
            DalamudDrawListPrimitives.AddRotatedText(drawList, pivot, -MatrixHeaderAngle,
                targeted ? ImGui.GetColorU32(QuartermasterPalette.Info) : text, column.ContainerLabel, lineHeight);
            drawList.PopClipRect();
            var target2 = column.Key;
            if (ImGui.InvisibleButton(column.ColumnId, new Vector2(Math.Max(1f, width), Math.Max(1f, headerHeight - (style.CellPadding.Y * 2f)))))
                courierBulkContainer = target2;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(column.Free is { } free
                    ? $"{column.Label} · {free:N0} free slot{(free == 1 ? string.Empty : "s")}"
                    : $"{column.Label} · free slots unknown");
            reviewRegistry.RegisterLastSelectable(
                $"quartermaster.courier.matrix.column.{column.Slug}",
                $"Target {column.Label} in the bulk bar",
                true,
                target == column.Key,
                () => courierBulkContainer = target2,
                column.Free is { } slots ? $"{slots:N0} free" : "Not observed");
        }
    }

    private void SortHeader(string label, string id, bool active, bool descending, Action toggle, bool rightAligned)
    {
        var width = ImGui.CalcTextSize(label).X + (10f * ImGuiHelpers.GlobalScale);
        if (rightAligned)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.PushStyleColor(ImGuiCol.Text, active ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : QuartermasterPalette.Dim);
        var clicked = ImGui.Selectable($"{label}##sort-{id}", false, ImGuiSelectableFlags.None, new Vector2(width, 0f));
        ImGui.PopStyleColor();
        if (active)
        {
            // A small triangle after the label: up while ascending.
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var size = 3.5f * ImGuiHelpers.GlobalScale;
            var center = new Vector2(max.X - size - 1f, (min.Y + max.Y) / 2f);
            var color = ImGui.GetColorU32(ImGuiCol.Text);
            var drawList = ImGui.GetWindowDrawList();
            if (descending)
                drawList.AddTriangleFilled(center + new Vector2(-size, -size / 2f), center + new Vector2(size, -size / 2f), center + new Vector2(0f, size), color);
            else
                drawList.AddTriangleFilled(center + new Vector2(-size, size / 2f), center + new Vector2(size, size / 2f), center + new Vector2(0f, -size), color);
        }
        if (clicked)
            toggle();
        reviewRegistry.RegisterLastButton($"quartermaster.courier.matrix.sort.{id}", $"Sort the matrix by {label}", true, toggle,
            active ? descending ? "Descending" : "Ascending" : "Not sorted");
    }

    /// <summary>Header row 3: each container's free slots; a dash when never observed.</summary>
    private void DrawMatrixFreeSlots(CourierColumn[] columns)
    {
        var style = ImGui.GetStyle();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
        var rowBottom = rowTop + ImGui.GetTextLineHeight() + (style.CellPadding.Y * 2f);
        DalamudUiLayout.DrawRightAligned("Free slots", QuartermasterPalette.Dim);
        for (var index = 0; index < columns.Length; index++)
        {
            if (!ImGui.TableSetColumnIndex(3 + index))
                continue;
            var column = columns[index];
            if (index > 0 && column.BandStart == index)
                DrawBandDivider(ImGui.GetCursorScreenPos().X - style.CellPadding.X, rowTop, rowBottom);
            var text = column.FreeText;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X) / 2f));
            ImGui.TextColored(column.Free is { } free && free < FreeSlotsBar.Tight ? FreeSlotsBar.Color(free) : QuartermasterPalette.Dim, text);
        }
    }

    private static void DrawBandDivider(float x, float top, float bottom) =>
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, top), new Vector2(x, bottom), ImGui.GetColorU32(BandDividerColor), 2f * ImGuiHelpers.GlobalScale);

    private void DrawMatrixRow(TransferWorkbenchRow row, int index, Guid[] keys, CourierColumn[] columns, MatrixLayout layout)
    {
        var rule = row.Rule;
        var style = ImGui.GetStyle();
        ImGui.TableNextRow(ImGuiTableRowFlags.None, layout.RowHeight);
        if (!rule.Enabled)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(PausedRowColor));
        ImGui.PushID(CourierKey(rule.Id));
        try
        {
            ImGui.TableSetColumnIndex(0);
            var rowTop = ImGui.GetCursorScreenPos().Y - style.CellPadding.Y;
            var rowBottom = rowTop + layout.RowHeight;
            DrawMatrixItem(row, index, keys, layout);

            ImGui.TableSetColumnIndex(1);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (layout.Inner - ImGui.GetFrameHeight()) / 2f));
            DrawCourierMoves(row);

            ImGui.TableSetColumnIndex(2);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (layout.Inner - layout.LineHeight) / 2f));
            DrawLineMenu(row, hoverRemove: false);

            if (row.Distribution is not { } allocation)
                return;
            var cells = CourierCellsFor(row, allocation, columns, layout.CellWidth);
            for (var column = 0; column < columns.Length; column++)
            {
                if (!ImGui.TableSetColumnIndex(3 + column))
                    continue;
                if (column > 0 && columns[column].BandStart == column)
                    DrawBandDivider(ImGui.GetCursorScreenPos().X - style.CellPadding.X, rowTop, rowBottom);
                DrawMatrixCell(row, allocation, cells[column], columns[column], layout);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>The item cell: the row's selection target, with name, quality and marks drawn over it.</summary>
    private void DrawMatrixItem(TransferWorkbenchRow row, int index, Guid[] keys, MatrixLayout layout)
    {
        var rule = row.Rule;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        DalamudTableSelectionRenderer.DrawRow(selection, keys, index, "##matrix-select", new Vector2(Math.Max(1f, width), layout.Inner));
        var hovered = ImGui.IsItemHovered();
        var ruleId = rule.Id;
        reviewRegistry.RegisterLastButton(
            $"quartermaster.transfer.line.select.{ruleId:N}",
            $"Select {rule.ItemName} for bulk actions",
            true,
            () => selection.SetSelected(ruleId, !selection.IsSelected(ruleId)),
            selection.IsSelected(ruleId) ? "Selected" : "Available");

        var drawList = ImGui.GetWindowDrawList();
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var quality = rule.Quality != ItemQualityPolicy.Any ? TransferPresentation.QualityLabel(rule.Quality) : null;
        var noted = !string.IsNullOrWhiteSpace(rule.Notes);
        var error = lineErrors.GetValueOrDefault(rule.Id);
        var tags = (quality is null ? 0f : ImGui.CalcTextSize(quality).X + spacing) +
                   (noted ? ImGui.CalcTextSize("note").X + spacing : 0f) +
                   (error is null ? 0f : ImGui.CalcTextSize("!").X + spacing);
        var name = ElideCached(courierMatrixNames, rule.Id, rule.ItemName, Math.Max(0f, width - tags));
        var y = origin.Y + ((layout.Inner - layout.LineHeight) / 2f);
        var x = origin.X;
        var dim = ImGui.GetColorU32(QuartermasterPalette.Dim);
        drawList.AddText(new Vector2(x, y), rule.Enabled ? ImGui.GetColorU32(ImGuiCol.Text) : dim, name);
        x += ImGui.CalcTextSize(name).X + spacing;
        if (quality is not null)
        {
            drawList.AddText(new Vector2(x, y), dim, quality);
            x += ImGui.CalcTextSize(quality).X + spacing;
        }
        if (noted)
        {
            drawList.AddText(new Vector2(x, y), dim, "note");
            x += ImGui.CalcTextSize("note").X + spacing;
        }
        if (error is not null)
            drawList.AddText(new Vector2(x, y), ImGui.GetColorU32(ErrorColor), "!");
        if (hovered && (name.Length < rule.ItemName.Length || noted || error is not null))
            ImGui.SetTooltip(string.Join("\n", new[] { rule.ItemName, noted ? rule.Notes : null, error }.OfType<string>()));
    }

    /// <summary>
    /// One cell, in one of four shapes: unlisted (blank, or what it holds; amber and dashed when it is
    /// the line's suggested place), drained, bounded, or a pool. Moves show only when something moves.
    /// Left-click lists an unlisted container as a pool or edits a listed one; right-click always edits.
    /// </summary>
    private void DrawMatrixCell(TransferWorkbenchRow row, CourierAllocation allocation, CourierCellView cell, CourierColumn column, MatrixLayout layout)
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        var height = Math.Max(1f, layout.Inner);
        var clicked = ImGui.InvisibleButton(column.CellId, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var spot = cell.Place >= 0 && cell.Place < allocation.Places.Length ? allocation.Places[cell.Place] : null;
        RegisterMatrixCell(row, column, spot);

        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(new Vector2(min.X - style.CellPadding.X, min.Y - style.CellPadding.Y),
                new Vector2(max.X + style.CellPadding.X, max.Y + style.CellPadding.Y), ImGui.GetColorU32(MatrixHoverColor));

        var font = ImGui.GetFont();
        var small = ImGui.GetFontSize() * .85f;
        var lineHeight = layout.LineHeight;
        var center = origin.X + (width / 2f);
        var twoLines = cell.Bottom.Length > 0;
        var topY = twoLines ? origin.Y + (height * .30f) : origin.Y + (height / 2f);
        var bottomY = origin.Y + (height * .74f);
        void Centered(string text, float midY, Vector4 color, float size)
        {
            var measured = ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
            drawList.AddText(font, size, new Vector2(center - (measured.X / 2f), midY - (measured.Y / 2f)), ImGui.GetColorU32(color), text);
        }
        void Chip(string text, Vector4 fill, Vector4 border, Vector4 color)
        {
            var measured = ImGui.CalcTextSize(text) * .85f;
            var pad = new Vector2(4f * scale, 1f * scale);
            var chipMin = new Vector2(center - (measured.X / 2f) - pad.X, topY - (measured.Y / 2f) - pad.Y);
            var chipMax = new Vector2(center + (measured.X / 2f) + pad.X, topY + (measured.Y / 2f) + pad.Y);
            drawList.AddRectFilled(chipMin, chipMax, ImGui.GetColorU32(fill), 3f * scale);
            drawList.AddRect(chipMin, chipMax, ImGui.GetColorU32(border), 3f * scale);
            drawList.AddText(font, small, new Vector2(center - (measured.X / 2f), topY - (measured.Y / 2f)), ImGui.GetColorU32(color), text);
        }

        var dim = QuartermasterPalette.Dim;
        switch (cell.Kind)
        {
            case CourierCellKind.Held:
                Centered(cell.Top, topY, dim, small);
                break;
            case CourierCellKind.Suggested:
                DrawDashedRect(drawList, new Vector2(min.X + scale, min.Y + scale), new Vector2(max.X - scale, max.Y - scale),
                    ImGui.GetColorU32(QuartermasterPalette.Amber with { W = .8f }), scale);
                Centered(cell.Top, topY, QuartermasterPalette.Amber, small);
                break;
            case CourierCellKind.Drain:
                Chip(cell.Top, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg], dim, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                break;
            case CourierCellKind.Bounded:
                Chip(cell.Top, QuartermasterPalette.Info with { W = .18f }, QuartermasterPalette.Info with { W = .6f }, QuartermasterPalette.Info);
                break;
            case CourierCellKind.Pool:
                Centered(cell.Top, topY, QuartermasterPalette.Info with { W = .8f }, small);
                break;
        }
        if (twoLines)
            Centered(cell.Bottom, bottomY, cell.BottomColor ?? dim, cell.Kind == CourierCellKind.Suggested ? small * .9f : small);

        if (hovered)
            ImGui.SetTooltip(CellTooltip(row, column, spot, cell));
        if (clicked)
        {
            if (spot is null)
                MutateCourierLine(row, line => line.WithPlace(column.Character, column.Retainer));
            else
                OpenCellPopover(row, column, spot, max);
        }
        else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            OpenCellPopover(row, column, spot, max);
    }

    private static void DrawDashedRect(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint color, float scale)
    {
        var dash = 3f * scale;
        var gap = 2.5f * scale;
        void Edge(Vector2 from, Vector2 to)
        {
            var length = Vector2.Distance(from, to);
            if (length <= 0f) return;
            var step = (to - from) / length;
            for (var at = 0f; at < length; at += dash + gap)
                drawList.AddLine(from + (step * at), from + (step * Math.Min(length, at + dash)), color, scale);
        }
        Edge(min, new Vector2(max.X, min.Y));
        Edge(new Vector2(max.X, min.Y), max);
        Edge(max, new Vector2(min.X, max.Y));
        Edge(new Vector2(min.X, max.Y), min);
    }

    private static string Signed(int delta) => delta > 0 ? $"+{delta:N0}" : delta < 0 ? $"−{-delta:N0}" : "±0";

    private static string FreeSlotsText(CourierColumn column) =>
        column.Free is { } free ? $"{free:N0} free slot{(free == 1 ? string.Empty : "s")}" : "free slots unknown";

    private static string CellTooltip(TransferWorkbenchRow row, CourierColumn column, CourierSpot? spot, CourierCellView cell)
    {
        if (spot is not null)
        {
            var has = spot.Has?.ToString("N0") ?? "—";
            return $"{column.Label} · Has {has} · {CourierBoundsText.Describe(spot.Min, spot.Max)} → End {spot.End:N0} ({Signed(spot.Delta)})\n" +
                   $"{FreeSlotsText(column)} · Click to edit";
        }
        var holds = cell.Held?.ToString("N0") ?? "—";
        return cell.Kind == CourierCellKind.Suggested
            ? $"{column.Label} · Suggested · Holds {holds} · {FreeSlotsText(column)}\nClick to add as Pool · Right-click to set bounds"
            : $"{column.Label} · Holds {holds} · {FreeSlotsText(column)}\nClick to add as Pool · Right-click to set bounds";
    }

    private void RegisterMatrixCell(TransferWorkbenchRow row, CourierColumn column, CourierSpot? spot)
    {
        var rule = row.Rule;
        var character = column.Character;
        var retainer = column.Retainer;
        reviewRegistry.RegisterLastAction(
            $"quartermaster.courier.matrix.cell.{rule.Id:N}.{column.Slug}",
            spot is null
                ? $"List {column.Label} on {rule.ItemName}: bounds as text (blank or omitted: pool)"
                : $"Set {column.Label} on {rule.ItemName}: bounds as text (blank: pool), or remove it",
            AgentBridgeUiControlKind.Input,
            true,
            spot is not null,
            spot is null ? "Unlisted" : CourierBoundsText.Describe(spot.Min, spot.Max),
            CellArguments,
            arguments =>
            {
                var text = string.Empty;
                var remove = false;
                if (arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value)
                {
                    if (value.TryGetProperty("bounds", out var bounds) && bounds.ValueKind == System.Text.Json.JsonValueKind.String)
                        text = bounds.GetString() ?? string.Empty;
                    if (value.TryGetProperty("remove", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True)
                        remove = true;
                }
                if (remove)
                    return MutateCourierLine(row, line => line.Without(character, retainer))
                        ? AgentBridgeUiActionResult.Ok($"Removed {column.Label} from {rule.ItemName}.")
                        : AgentBridgeUiActionResult.Fail("Not changed.");
                if (!CourierBoundsText.TryParse(text, out var min, out var max))
                    return AgentBridgeUiActionResult.Fail($"Not bounds: {text} ({CourierBoundsText.Syntax}).");
                return MutateCourierLine(row, line => UpsertPlace(line, character, retainer, min, max))
                    ? AgentBridgeUiActionResult.Ok($"{column.Label}: {CourierBoundsText.Describe(min, max)}.")
                    : AgentBridgeUiActionResult.Fail("Not changed.");
            });
    }

    /// <summary>Sets a place's bounds, listing it first when the line does not list it yet.</summary>
    private static CourierLine UpsertPlace(CourierLine line, OwnerScope character, ulong? retainer, int? min, int? max) =>
        line.Lists(character, retainer) ? line.WithBounds(character, retainer, min, max) : line.WithPlace(character, retainer, min, max);

    // ---- Cell bounds popover -------------------------------------------------------------

    private void OpenCellPopover(TransferWorkbenchRow row, CourierColumn column, CourierSpot? spot, Vector2 cellMax)
    {
        courierCellEdit = (row.Rule.Id, column.Character with { }, column.Retainer);
        courierCellText = spot is null ? string.Empty : CourierBoundsText.Format(spot.Min, spot.Max);
        courierCellInvalid = false;
        courierCellAnchor = new Vector2(ImGui.GetItemRectMin().X, cellMax.Y);
        courierCellOpenRequested = true;
    }

    /// <summary>
    /// The anchored editor for one cell, opened and drawn outside the rows so its ID always matches:
    /// typed bounds (Enter applies), or Drain, Pool and Remove in one click.
    /// </summary>
    private void DrawCourierCellPopover(QuartermasterRuntimeSnapshot runtime, TransferWorkbenchProjection projection)
    {
        if (courierCellOpenRequested)
        {
            courierCellOpenRequested = false;
            ImGui.OpenPopup(CourierCellPopup);
        }
        ImGui.SetNextWindowPos(courierCellAnchor, ImGuiCond.Appearing);
        if (!DalamudOwnedPopup.BeginOpaquePopup(CourierCellPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        try
        {
            if (courierCellEdit is not { } edit ||
                projection.Rows.FirstOrDefault(candidate => candidate.Rule.Id == edit.RuleId) is not { Distribution: { } allocation } row)
            {
                ImGui.CloseCurrentPopup();
                return;
            }
            var (character, retainer) = (edit.Character, edit.Retainer);
            var column = FindColumn((character.LocalContentId, retainer));
            var label = column?.Label ?? CourierSession.Side(runtime, character, retainer, includeOwner: true);
            var spot = allocation.Places.FirstOrDefault(place => place.Retainer == retainer && place.Character.LocalContentId == character.LocalContentId);
            ImGui.TextUnformatted($"{row.Rule.ItemName} · {label}");
            if (spot is not null)
                ImGui.TextDisabled($"Has {spot.Has?.ToString("N0") ?? "—"} → End {spot.End:N0} ({Signed(spot.Delta)})");
            else
            {
                int? held = allocation.Holders.FirstOrDefault(holder => holder.Retainer == retainer && holder.Character.LocalContentId == character.LocalContentId)?.Has
                            ?? (column?.Free is null ? null : 0);
                ImGui.TextDisabled($"Unlisted · Holds {held?.ToString("N0") ?? "—"}");
            }

            void Commit(int? min, int? max)
            {
                MutateCourierLine(row, line => UpsertPlace(line, character, retainer, min, max));
                ImGui.CloseCurrentPopup();
            }
            void Apply()
            {
                if (CourierBoundsText.TryParse(courierCellText, out var min, out var max))
                    Commit(min, max);
                else
                    courierCellInvalid = true;
            }
            void Remove()
            {
                MutateCourierLine(row, line => line.Without(character, retainer));
                ImGui.CloseCurrentPopup();
            }

            var scale = ImGuiHelpers.GlobalScale;
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            if (courierCellInvalid)
            {
                ImGui.PushStyleColor(ImGuiCol.Border, QuartermasterPalette.Amber);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            }
            ImGui.SetNextItemWidth(170f * scale);
            var entered = ImGui.InputTextWithHint("##courier-cell-input", "Pool", ref courierCellText, 32,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
            if (courierCellInvalid)
            {
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
            }
            if (ImGui.IsItemEdited())
                courierCellInvalid = false;
            if (entered)
                Apply();
            if (courierCellInvalid)
                ImGui.TextColored(QuartermasterPalette.Amber, CourierBoundsText.Syntax);

            const string ids = "quartermaster.courier.matrix.popover";
            if (UiLayout.PrimaryButton("Apply##courier-cell"))
                Apply();
            reviewRegistry.RegisterLastAction($"{ids}.apply", "Apply typed bounds to this cell", AgentBridgeUiControlKind.Button, true, false,
                courierCellText, BoundsArguments, arguments =>
                {
                    var text = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value && value.TryGetProperty("bounds", out var bounds)
                        ? bounds.GetString() ?? string.Empty
                        : courierCellText;
                    if (!CourierBoundsText.TryParse(text, out var min, out var max))
                        return AgentBridgeUiActionResult.Fail($"Not bounds: {text} ({CourierBoundsText.Syntax}).");
                    MutateCourierLine(row, line => UpsertPlace(line, character, retainer, min, max));
                    courierCellEdit = null;
                    return AgentBridgeUiActionResult.Ok($"{label}: {CourierBoundsText.Describe(min, max)}.");
                });
            ImGui.SameLine();
            if (ImGui.Button("0 (Drain)##courier-cell"))
                Commit(0, 0);
            reviewRegistry.RegisterLastButton($"{ids}.drain", $"Drain {label}", true,
                () => { MutateCourierLine(row, line => UpsertPlace(line, character, retainer, 0, 0)); courierCellEdit = null; });
            ImGui.SameLine();
            if (ImGui.Button("Pool##courier-cell"))
                Commit(null, null);
            reviewRegistry.RegisterLastButton($"{ids}.pool", $"Make {label} a pool", true,
                () => { MutateCourierLine(row, line => UpsertPlace(line, character, retainer, null, null)); courierCellEdit = null; });
            if (spot is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Remove##courier-cell"))
                    Remove();
                reviewRegistry.RegisterLastButton($"{ids}.remove", $"Remove {label} from {row.Rule.ItemName}", true,
                    () => { MutateCourierLine(row, line => line.Without(character, retainer)); courierCellEdit = null; });
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                ImGui.CloseCurrentPopup();
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    // ---- Bulk place editing --------------------------------------------------------------

    /// <summary>
    /// The bulk bar's container target and what it sets on every selected line: drained, a pool, typed
    /// bounds, or removed. A line that does not list the container yet gets it listed with those bounds.
    /// </summary>
    private void DrawCourierBulkPlace(QuartermasterRuntimeSnapshot runtime, StowagePlan plan, TransferWorkbenchProjection projection, Guid[] selectedIds)
    {
        EnsureCourierColumns(runtime, projection);
        var scale = ImGuiHelpers.GlobalScale;
        var target = (courierBulkContainer is { } key ? FindColumn(key) : null)
                     ?? (courierPlanColumns.Length > 0 ? courierPlanColumns[0] : courierReachableColumns.FirstOrDefault());
        var comboWidth = 170f * scale;
        DalamudUiLayout.SameLineIfFits(comboWidth);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##RQCourierBulkContainer", target?.Label ?? "Container"))
        {
            foreach (var column in courierPlanColumns)
                if (ImGui.Selectable($"{column.Label}##bulk-{column.Slug}", target?.Key == column.Key))
                    courierBulkContainer = column.Key;
            var first = true;
            foreach (var column in courierReachableColumns)
            {
                if (courierPlanColumns.Any(used => used.Key == column.Key))
                    continue;
                if (first && courierPlanColumns.Length > 0)
                    ImGui.Separator();
                first = false;
                if (ImGui.Selectable($"{column.Label}##bulk-{column.Slug}", target?.Key == column.Key))
                    courierBulkContainer = column.Key;
            }
            ImGui.EndCombo();
        }
        var choices = courierBulkChoices;
        reviewRegistry.RegisterLastAction(
            "quartermaster.courier.bulk.container",
            "Choose the container the bulk bar sets on the selected lines",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            target?.Label,
            courierBulkSchema,
            arguments =>
            {
                var requested = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value && value.TryGetProperty("container", out var name)
                    ? name.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                var match = choices.FirstOrDefault(column => string.Equals(column.Label, requested, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return AgentBridgeUiActionResult.Fail($"{requested} is not a reachable container.");
                courierBulkContainer = match.Key;
                return AgentBridgeUiActionResult.Ok($"Bulk bar targets {match.Label}.");
            });

        var has = target is not null && selectedIds.Length > 0;
        var owner = runtime.Owner;
        bool Apply(Func<CourierLine, OwnerScope, ulong?, CourierLine> change)
        {
            if (target is null)
                return false;
            var (character, retainer) = (target.Character, target.Retainer);
            var rows = projection.Rows.ToDictionary(row => row.Rule.Id);
            return MutateRules(owner, plan.Id, selectedIds, draft =>
            {
                if (draft.Courier is null)
                    return;
                var line = rows.TryGetValue(draft.Id, out var row) ? EditableLine(row, draft) : draft.Courier;
                draft.Courier = change(line, character, retainer);
            });
        }
        void Button(string label, string id, string description, Func<CourierLine, OwnerScope, ulong?, CourierLine> change, bool enabled = true)
        {
            DalamudUiLayout.SameLineIfFits(label);
            ImGui.BeginDisabled(!has || !enabled);
            var clicked = ImGui.Button(label);
            ImGui.EndDisabled();
            if (clicked)
                Apply(change);
            reviewRegistry.RegisterLastButton($"quartermaster.courier.bulk.{id}", description, has && enabled, () => Apply(change),
                target?.Label ?? "No container");
        }

        var name = target?.Label ?? "the container";
        Button("Drain (0)##RQCourierBulk", "drain", $"Drain {name} on the selected lines",
            (line, character, retainer) => UpsertPlace(line, character, retainer, 0, 0));
        Button("Pool##RQCourierBulk", "pool", $"Make {name} a pool on the selected lines",
            (line, character, retainer) => UpsertPlace(line, character, retainer, null, null));

        var boundsWidth = 80f * scale;
        DalamudUiLayout.SameLineIfFits(boundsWidth + ImGui.GetStyle().ItemSpacing.X + DalamudUiLayout.ButtonWidth("Set"));
        var invalidBefore = courierBulkBounds.Trim().Length > 0 && !CourierBoundsText.TryParse(courierBulkBounds, out _, out _);
        if (invalidBefore)
        {
            ImGui.PushStyleColor(ImGuiCol.Border, QuartermasterPalette.Amber);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        }
        ImGui.SetNextItemWidth(boundsWidth);
        var entered = ImGui.InputTextWithHint("##RQCourierBulkBounds", "Bounds", ref courierBulkBounds, 32,
            ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
        if (invalidBefore)
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        // Re-parse after InputTextWithHint so an edit and Enter in the same frame submit the latest text.
        var typed = courierBulkBounds.Trim().Length > 0;
        var parsed = CourierBoundsText.TryParse(courierBulkBounds, out var low, out var high);
        var invalid = typed && !parsed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(invalid ? $"Not bounds · {CourierBoundsText.Syntax}" : CourierBoundsText.Syntax);
        ImGui.SameLine();
        var canSet = has && typed && parsed;
        ImGui.BeginDisabled(!canSet);
        var set = ImGui.Button("Set##RQCourierBulk") || (entered && canSet);
        ImGui.EndDisabled();
        if (set && Apply((line, character, retainer) => UpsertPlace(line, character, retainer, low, high)))
            courierBulkBounds = string.Empty;
        reviewRegistry.RegisterLastAction("quartermaster.courier.bulk.set", $"Set typed bounds on {name} for the selected lines",
            AgentBridgeUiControlKind.Button, has, false, courierBulkBounds, BoundsArguments, arguments =>
            {
                var text = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value && value.TryGetProperty("bounds", out var bounds)
                    ? bounds.GetString() ?? string.Empty
                    : courierBulkBounds;
                if (!CourierBoundsText.TryParse(text, out var min, out var max))
                    return AgentBridgeUiActionResult.Fail($"Not bounds: {text} ({CourierBoundsText.Syntax}).");
                return Apply((line, character, retainer) => UpsertPlace(line, character, retainer, min, max))
                    ? AgentBridgeUiActionResult.Ok($"{name}: {CourierBoundsText.Describe(min, max)} on {selectedIds.Length:N0} line(s).")
                    : AgentBridgeUiActionResult.Fail("Not changed.");
            });

        Button("Remove place##RQCourierBulk", "remove-place", $"Remove {name} from the selected lines",
            (line, character, retainer) => line.Without(character, retainer));
    }

    /// <summary>Opens the selected lines, or closes them when any is open.</summary>
    private void DrawCourierBulkExpand(Guid[] selectedIds)
    {
        var anyOpen = selectedIds.Any(CourierExpanded);
        var label = anyOpen ? "Collapse##RQCourierBulkExpand" : "Expand##RQCourierBulkExpand";
        DalamudUiLayout.SameLineIfFits(label);
        if (ImGui.Button(label))
            SetCourierExpanded(selectedIds, !anyOpen);
        reviewRegistry.RegisterLastButton("quartermaster.courier.bulk.expand-toggle",
            anyOpen ? "Collapse the selected courier lines" : "Expand the selected courier lines", selectedIds.Length > 0,
            () => SetCourierExpanded(selectedIds, !selectedIds.Any(CourierExpanded)),
            anyOpen ? "Some expanded" : "All collapsed");
    }
}
