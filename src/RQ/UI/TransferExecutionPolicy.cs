using RQ.Planning;

namespace RQ.UI;

internal sealed record TransferExecutionAvailability(bool CanExecute, string? BlockReason);

internal static class TransferExecutionPolicy
{
    public static bool HasMovement(int retrievalQuantity, StowageDepositBatch deposit) =>
        retrievalQuantity > 0 || deposit.RequestedQuantity > 0;

    public static bool RequiresCapacityRecovery(StowageDepositBatch deposit) =>
        deposit.RequestedQuantity > 0 && deposit.RemainingQuantity > 0;

    public static TransferExecutionAvailability ForExplicitRun(
        bool hasMovement,
        bool ownerScopeAvailable,
        bool transferAvailable,
        bool refreshActive)
    {
        var blockReason = !hasMovement
            ? "This plan is already satisfied."
            : !ownerScopeAvailable
                ? "Character identity is unavailable."
                : refreshActive
                    ? "Waiting for the retainer refresh to finish."
                    : !transferAvailable
                        ? "Another retainer operation is active."
                        : null;
        return new(blockReason is null, blockReason);
    }
}
