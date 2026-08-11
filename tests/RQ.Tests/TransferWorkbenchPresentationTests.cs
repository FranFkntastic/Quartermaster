using RQ.Planning;
using RQ.UI;

namespace RQ.Tests;

public sealed class TransferWorkbenchPresentationTests
{
    [Fact]
    public void Accessible_storage_excludes_retainer_stock_known_to_be_inaccessible()
    {
        var accessible = TestData.Retainer(10, "Accessible", (100, "Bridge", 41));
        accessible.IsUiAccessible = true;
        var inaccessible = TestData.Retainer(20, "Inaccessible", (100, "Bridge", 9));
        inaccessible.IsUiAccessible = false;
        var retainers = new Dictionary<ulong, RQ.Domain.CachedRetainer>
        {
            [accessible.RetainerId] = accessible,
            [inaccessible.RetainerId] = inaccessible,
        };
        var stock = BrowserProjectionBuilder.Build([], retainers, TestData.Owner)
            .Items.Single(item => item.ItemId == 100);

        var quantity = TransferWorkbenchPresentation.AccessibleStorageQuantity(
            stock,
            RQ.Domain.ItemQualityPolicy.Any,
            retainers,
            TestData.Owner);

        Assert.Equal(41, quantity);
    }

    [Theory]
    [InlineData(50, 0, "50 (+50)")]
    [InlineData(0, 25, "0 (-25)")]
    [InlineData(50, 25, "50 (+25)")]
    public void Target_combines_goal_with_player_delta(int target, int player, string expected)
    {
        Assert.Equal(expected, TransferWorkbenchPresentation.Target(target, player));
    }

    [Fact]
    public void Retrieval_outcome_reports_presently_executable_quantity_and_row_shortfall()
    {
        var outcome = TransferWorkbenchPresentation.Outcome(
            StowageAction.Retrieve,
            requestedQuantity: 50,
            accessibleStorageQuantity: 41);

        Assert.Equal("Retrieve 41", outcome.Primary);
        Assert.Equal("short 9", outcome.Constraint);
        Assert.Equal("Retrieve 41 · short 9", outcome.Text);
    }

    [Fact]
    public void Deposit_outcome_reports_capacity_constraint_without_calling_it_missing_stock()
    {
        var outcome = TransferWorkbenchPresentation.Outcome(
            StowageAction.Deposit,
            requestedQuantity: 25,
            accessibleStorageQuantity: 50,
            routedDepositQuantity: 16);

        Assert.Equal("Stow 16 · no room for 9", outcome.Text);
    }
}
