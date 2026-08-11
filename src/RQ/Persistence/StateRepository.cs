using System.Security.Cryptography;
using System.Text.Json;
using RQ.Domain;

namespace RQ.Persistence;

public enum StateChangeKind
{
    Plans,
    Listings,
    Operations,
}

public sealed class StateRepository
{
    private readonly object gate = new();
    private readonly QuartermasterStateStore store;
    private readonly OperationStateStore operationStore;
    private QuartermasterState state;

    public StateRepository(QuartermasterStateStore store)
    {
        this.store = store;
        state = store.Load();
        var directory = Path.GetDirectoryName(store.Path) ?? throw new InvalidOperationException("Quartermaster state directory is unavailable.");
        var databaseName = $"{Path.GetFileNameWithoutExtension(store.Path)}-operations.db";
        operationStore = new OperationStateStore(Path.Combine(directory, databaseName));
        MigrateLegacyOperations();
    }

    public event Action<StateChangeKind>? Changed;

    public QuartermasterState Snapshot()
    {
        lock (gate)
            return Clone(state);
    }

    public QuartermasterState OperationSnapshot() => operationStore.Snapshot();

    public QuartermasterState FullSnapshot()
    {
        lock (gate)
            return Combine(Clone(state), operationStore.Snapshot());
    }

    public T Read<T>(Func<QuartermasterState, T> read)
    {
        lock (gate)
            return operationStore.Read(operations => read(Combine(state, operations)));
    }

    public T Mutate<T>(Func<QuartermasterState, T> mutate) => Mutate(StateChangeKind.Plans, mutate);

    public T Mutate<T>(StateChangeKind changeKind, Func<QuartermasterState, T> mutate)
    {
        if (changeKind == StateChangeKind.Operations)
        {
            T operationResult;
            lock (gate)
                operationResult = operationStore.Mutate(mutate);
            Changed?.Invoke(changeKind);
            return operationResult;
        }

        T result;
        lock (gate)
        {
            var candidate = Clone(state);
            result = mutate(candidate);
            candidate.Revision = checked(state.Revision + 1);
            store.Save(candidate);
            state = candidate;
        }
        Changed?.Invoke(changeKind);
        return result;
    }

    public void Mutate(Action<QuartermasterState> mutate) => Mutate(state =>
    {
        mutate(state);
        return true;
    });

    public void Mutate(StateChangeKind changeKind, Action<QuartermasterState> mutate) => Mutate(changeKind, state =>
    {
        mutate(state);
        return true;
    });

    private static QuartermasterState Clone(QuartermasterState value) =>
        JsonSerializer.Deserialize<QuartermasterState>(
            JsonSerializer.Serialize(value, AtomicDocumentStore<QuartermasterState>.JsonOptions),
            AtomicDocumentStore<QuartermasterState>.JsonOptions) ?? new QuartermasterState();

    private void MigrateLegacyOperations()
    {
        var hasLegacyRows = state.Requests.Count != 0 || state.Operations.Count != 0 ||
                            state.Receipts.Count != 0 || state.PendingCacheInvalidations.Count != 0;
        if (state.OperationJournalMigration is not null)
        {
            if (hasLegacyRows)
                throw new InvalidOperationException("Quartermaster contains both a completed operation-journal migration receipt and embedded legacy operation rows.");
            operationStore.VerifyMigration(state.OperationJournalMigration);
            return;
        }
        if (!hasLegacyRows)
        {
            // Bind even a brand-new configuration to its journal. Otherwise a
            // deleted database could be recreated as empty and mistaken for a
            // legitimate install with no operation history.
            state.OperationJournalMigration = operationStore.CreateEmptyBinding(DateTime.UtcNow);
            store.Save(state);
            return;
        }

        // Preserve the exact source document before changing either authority.
        // A retry sees the same digest and import marker, while a conflicting
        // document or database fails rather than attempting an unsafe merge.
        var sourceBytes = File.ReadAllBytes(store.Path);
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var backupPath = $"{store.Path}.pre-operation-journal-v5.bak";
        if (File.Exists(backupPath))
        {
            var backupHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupPath)));
            if (!string.Equals(backupHash, sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The retained pre-journal backup does not match the legacy state being migrated.");
        }
        else
            File.Copy(store.Path, backupPath, overwrite: false);

        var migration = operationStore.ImportLegacy(state, sourceSha256, DateTime.UtcNow);
        state.Requests = [];
        state.Operations = [];
        state.Receipts = [];
        state.PendingCacheInvalidations = [];
        state.OperationJournalMigration = migration;
        state.Revision = checked(state.Revision + 1);
        store.Save(state);
    }

    private static QuartermasterState Combine(QuartermasterState configuration, QuartermasterState operations) => new()
    {
        Schema = configuration.Schema,
        Revision = Math.Max(configuration.Revision, operations.Revision),
        StowagePlans = configuration.StowagePlans,
        ListingPlans = configuration.ListingPlans,
        TransferPlanListingLinks = configuration.TransferPlanListingLinks,
        StowageMigrations = configuration.StowageMigrations,
        TransferPlanMigrations = configuration.TransferPlanMigrations,
        PlanItems = configuration.PlanItems,
        RestockPlans = configuration.RestockPlans,
        ItemGroups = configuration.ItemGroups,
        LatestRetainerListingCapture = configuration.LatestRetainerListingCapture,
        OperationJournalMigration = configuration.OperationJournalMigration,
        Requests = operations.Requests,
        Operations = operations.Operations,
        Receipts = operations.Receipts,
        PendingCacheInvalidations = operations.PendingCacheInvalidations,
    };
}
