using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using RQ.Domain;
using RQ.Interop;
using RQ.Planning;
using RQ.Runtime;

namespace RQ.UI;

/// <summary>
/// The market run's placement matrix for a caller-supplied set of items (opened over IPC by another
/// plugin). Rows read the owner's reserve rules; edits persist straight to them through
/// <see cref="WorkbenchStockService.SetPlacement"/>, creating a level-0 rule for an item that has none.
/// </summary>
internal sealed class PlacementEditorWindow : Window
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(5);

    private readonly Func<QuartermasterRuntimeSnapshot> runtime;
    private readonly WorkbenchStockService service;
    private readonly PlacementMatrix<(uint ItemId, ItemQualityPolicy Quality)> matrix;
    private readonly Dictionary<(uint ItemId, ItemQualityPolicy Quality), (StowageRoutingPolicy Routing, DateTime Until)> pending = [];
    private OwnerScope? owner;
    private IReadOnlyList<WorkbenchStockService.PlacementEditorItem> items = [];

    public PlacementEditorWindow(Func<QuartermasterRuntimeSnapshot> runtime, WorkbenchStockService service)
        : base("Placement###RQPlacementEditor", ImGuiWindowFlags.NoCollapse)
    {
        this.runtime = runtime;
        this.service = service;
        // Not wired to the agent bridge: this window is opened by another plugin, not a reviewed surface.
        matrix = new("quartermaster.placement-editor", "Bags", new AgentBridgeUiReviewRegistry());
        Size = new Vector2(720, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Shows the matrix for these items of the owner, replacing any earlier set. Framework thread.</summary>
    public void Open(OwnerScope scope, IReadOnlyList<WorkbenchStockService.PlacementEditorItem> requested)
    {
        owner = scope;
        items = requested;
        pending.Clear();
        matrix.ClearSelection();
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        items = [];
        pending.Clear();
        matrix.ClearSelection();
    }

    public override void Draw()
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
        var rows = items.Select(item =>
        {
            var key = (item.ItemId, item.Quality);
            var stored = plan?.Reserves.FirstOrDefault(reserve => reserve.Enabled && reserve.ItemId == item.ItemId && reserve.Quality == item.Quality)
                ?.Location ?? new StowageRoutingPolicy();
            // An edit shows at once; the stored rule takes over once it matches or the edit has had time to land.
            if (pending.TryGetValue(key, out var edit) &&
                (edit.Until <= now || WorkbenchStockService.SameLocation(edit.Routing, stored)))
                pending.Remove(key);
            var routing = pending.TryGetValue(key, out edit) ? edit.Routing : stored;
            return new PlacementMatrixRow<(uint, ItemQualityPolicy)>(
                key,
                $"{item.ItemId}-{item.Quality}",
                $"{item.ItemName} {TransferPresentation.QualityLabel(item.Quality, anyLabel: "NQ")}",
                PlacementMatrixData.BagHoldings(current, item.ItemId, item.Quality).ToString("N0"),
                routing,
                PlacementMatrixData.Holdings(current, item.ItemId, item.Quality));
        }).ToArray();
        var columns = PlacementMatrixData.Columns(current, rows.Select(row => row.Routing));
        matrix.Draw(rows, rows, columns, changes =>
        {
            foreach (var (key, routing) in changes)
            {
                var item = items.FirstOrDefault(candidate => candidate.ItemId == key.ItemId && candidate.Quality == key.Quality);
                if (item is null) continue;
                pending[key] = (ListingPlanCatalog.CopyLocation(routing), now + PendingLifetime);
                service.SetPlacement(scope, item, routing);
            }
            return true;
        });
    }
}
