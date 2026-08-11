using RQ.Domain;
using RQ.Planning;
using Franthropy.Dalamud.UI.Filtering;

namespace RQ.UI;

public enum WorkbenchView { Stock, Restock, Stowage, ItemGroups, Listings, Activity }

public sealed class WorkbenchState
{
    private readonly HashSet<uint> expanded = [];
    public WorkbenchView View { get; set; }
    public Guid? SelectedRestockPlanId { get; set; }
    public Guid? SelectedStowagePlanId { get; set; }
    public string ScopeKey { get; set; } = BrowserScope.AllKey;
    public DalamudFilterAutocompleteState ItemFilterState { get; } = new();
    public DalamudFilterAutocompleteState ListingFilterState { get; } = new();
    public string ListingSort { get; set; } = "Item";
    public bool ListingSortDescending { get; set; }
    public ListingItemKey? SelectedListingItem { get; set; }
    public ItemQualityPolicy SelectedStockListingQuality { get; set; } = ItemQualityPolicy.NqOnly;
    public uint? FocusedStockItemId { get; set; }
    public StockGroup? SelectedStock { get; private set; }
    public string StagedTargetText { get; set; } = string.Empty;

    public int? StagedTarget => int.TryParse(StagedTargetText.Trim(), out var value) ? value : null;
    public bool CanApply => SelectedStock is not null && StagedTarget is > 0;

    public void Select(StockGroup stock)
    {
        SelectedStock = stock;
        StagedTargetText = string.Empty;
    }

    public void ClearSelection()
    {
        SelectedStock = null;
        StagedTargetText = string.Empty;
    }

    public bool Apply(IList<TargetPlanItem> plan)
    {
        if (!CanApply || SelectedStock is null || StagedTarget is not { } target)
            return false;
        var applied = WithdrawalPlanStager.TryUpsert(plan, SelectedStock, target);
        if (applied)
            ClearSelection();
        return applied;
    }

    public bool IsExpanded(uint itemId) => expanded.Contains(itemId);
    public void ToggleExpanded(uint itemId)
    {
        if (!expanded.Add(itemId))
            expanded.Remove(itemId);
    }

    public void EnsureScope(BrowserProjection projection)
    {
        if (projection.Scopes.All(scope => scope.Key != ScopeKey))
        {
            ScopeKey = BrowserScope.AllKey;
            ClearSelection();
        }
    }

    public void RequestView(WorkbenchView view) => View = view;
}
