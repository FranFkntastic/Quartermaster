using RQ.Domain;
using RQ.Runtime;

namespace RQ.Tests;

public sealed class RuntimeOwnerTransitionTests
{
    [Fact]
    public void StableLiveOwnerRequiresRepairUntilProjectionMatches()
    {
        var unavailable = new OwnerScope();

        Assert.True(RuntimeOwnerTransition.RequiresReconciliation(unavailable, TestData.Owner));
        Assert.False(RuntimeOwnerTransition.RequiresReconciliation(TestData.Owner, TestData.Owner));
    }

    [Fact]
    public void UnavailableLiveOwnerDoesNotCausePerFrameInvalidation()
    {
        Assert.False(RuntimeOwnerTransition.RequiresReconciliation(new OwnerScope(), new OwnerScope()));
    }
}
