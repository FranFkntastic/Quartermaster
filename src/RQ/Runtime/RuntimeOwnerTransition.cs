using RQ.Domain;

namespace RQ.Runtime;

public static class RuntimeOwnerTransition
{
    public static bool RequiresReconciliation(OwnerScope projectedOwner, OwnerScope liveOwner) =>
        liveOwner.HasStableIdentity && !projectedOwner.Matches(liveOwner);
}
