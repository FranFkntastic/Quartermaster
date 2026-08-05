using RQ.Domain;
using RQ.Planning;
using RQ.UI;

namespace RQ.Tests;

public sealed class TransferExecutionPolicyTests
{
    [Fact]
    public void ExplicitRun_DoesNotDependOnDefaultPlanFlag()
    {
        var plan = new StowagePlan { Enabled = false };

        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement: true,
            ownerScopeAvailable: true,
            transferAvailable: true,
            refreshActive: false);

        Assert.False(plan.Enabled);
        Assert.True(availability.CanExecute);
        Assert.Null(availability.BlockReason);
    }

    [Fact]
    public void DepositOnlyPlan_RemainsExecutableWhileCapacityEvidenceIsMissing()
    {
        var request = new StowageDepositRequest(null, null, 100, "Ore", false, 7, new StowageRoutingPolicy());
        var deposit = new StowageDepositBatch(
            DateTime.UtcNow,
            [new StowageRoute(request, [], 0, 7)]);

        Assert.True(TransferExecutionPolicy.HasMovement(0, deposit));
        Assert.True(TransferExecutionPolicy.RequiresCapacityRecovery(deposit));
        Assert.True(TransferExecutionPolicy.ForExplicitRun(
            TransferExecutionPolicy.HasMovement(0, deposit),
            ownerScopeAvailable: true,
            transferAvailable: true,
            refreshActive: false).CanExecute);
    }

    [Theory]
    [InlineData(false, true, true, false, "This plan is already satisfied.")]
    [InlineData(true, false, true, false, "Character identity is unavailable.")]
    [InlineData(true, true, true, true, "Waiting for the retainer refresh to finish.")]
    [InlineData(true, true, false, false, "Another retainer operation is active.")]
    public void ExplicitRun_ExplainsEveryDisabledState(
        bool hasMovement,
        bool ownerScopeAvailable,
        bool transferAvailable,
        bool refreshActive,
        string expectedReason)
    {
        var availability = TransferExecutionPolicy.ForExplicitRun(
            hasMovement,
            ownerScopeAvailable,
            transferAvailable,
            refreshActive);

        Assert.False(availability.CanExecute);
        Assert.Equal(expectedReason, availability.BlockReason);
    }
}
