using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RQ.Domain;

namespace RQ.Persistence;

/// <summary>
/// Owns Quartermaster's durable operation ledger. Operations are updated and
/// receipts are appended as individual SQLite rows so one verified movement
/// never rewrites the plugin's complete historical state.
/// </summary>
internal sealed class OperationStateStore
{
    private const int SchemaVersion = 1;
    private const string LegacySourceHashKey = "legacy_source_sha256";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private List<SubmittedRequestRecord> requests = [];
    private List<OperationRecord> operations = [];
    private List<OperationReceipt> receipts = [];
    private List<PendingCacheInvalidation> pendingInvalidations = [];
    private long revision;

    public OperationStateStore(string path)
    {
        DatabasePath = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        SQLitePCL.Batteries_V2.Init();
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        CreateSchema(connection);
        JournalId = EnsureJournalId(connection);
        Reload(connection);
    }

    public string DatabasePath { get; }
    public string JournalId { get; }

    public T Read<T>(Func<QuartermasterState, T> read)
    {
        lock (gate)
            return read(CreateView());
    }

    public QuartermasterState Snapshot()
    {
        lock (gate)
            return new QuartermasterState
            {
                Revision = revision,
                Requests = requests.Select(Copy).ToList(),
                Operations = operations.Select(Copy).ToList(),
                Receipts = receipts.Select(Copy).ToList(),
                PendingCacheInvalidations = pendingInvalidations.Select(Copy).ToList(),
            };
    }

    public T Mutate<T>(Func<QuartermasterState, T> mutate)
    {
        lock (gate)
        {
            // Historical receipts and requests are append-only, so only the much
            // smaller mutable operation set requires a rollback-safe deep copy.
            // This removes receipt-history volume from the per-movement cost while
            // retaining the old fail-closed mutation semantics if persistence fails.
            var candidate = new QuartermasterState
            {
                Revision = revision,
                Requests = new List<SubmittedRequestRecord>(requests),
                Operations = operations.Select(Copy).ToList(),
                Receipts = new List<OperationReceipt>(receipts),
                PendingCacheInvalidations = pendingInvalidations.Select(Copy).ToList(),
            };
            var result = mutate(candidate);
            ValidateOperationOnlyMutation(candidate);
            candidate.Revision = checked(revision + 1);
            using var connection = OpenConnection();
            PersistChanges(connection, candidate, requests, operations, receipts, pendingInvalidations);
            requests = candidate.Requests;
            operations = candidate.Operations;
            receipts = candidate.Receipts;
            pendingInvalidations = candidate.PendingCacheInvalidations;
            revision = candidate.Revision;
            return result;
        }
    }

    public OperationJournalMigrationRecord ImportLegacy(QuartermasterState legacy, string sourceSha256, DateTime completedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        lock (gate)
        {
            using var connection = OpenConnection();
            var existingSource = ReadMetadata(connection, LegacySourceHashKey);
            if (!string.IsNullOrWhiteSpace(existingSource))
            {
                if (!string.Equals(existingSource, sourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The operation journal was already imported from a different legacy state document.");
                var existing = MigrationRecordFromMetadata(connection, completedAtUtc);
                VerifyMigrationCore(connection, existing);
                return existing;
            }

            if (requests.Count != 0 || operations.Count != 0 || receipts.Count != 0 || pendingInvalidations.Count != 0)
                throw new InvalidOperationException("The operation journal contains data without a matching legacy-import receipt; refusing to merge two authorities.");

            var migration = new OperationJournalMigrationRecord
            {
                JournalId = JournalId,
                SourceSha256 = sourceSha256.ToUpperInvariant(),
                RequestCount = legacy.Requests.Count,
                OperationCount = legacy.Operations.Count,
                ReceiptCount = legacy.Receipts.Count,
                PendingInvalidationCount = legacy.PendingCacheInvalidations.Count,
                CompletedAtUtc = completedAtUtc,
            };

            using var transaction = connection.BeginTransaction();
            foreach (var request in legacy.Requests)
                InsertRequest(connection, request, transaction);
            foreach (var operation in legacy.Operations)
                InsertOperation(connection, operation, transaction);
            foreach (var receipt in legacy.Receipts)
                InsertReceipt(connection, receipt, transaction);
            foreach (var invalidation in legacy.PendingCacheInvalidations)
                InsertInvalidation(connection, invalidation, transaction);
            WriteMetadata(connection, "revision", legacy.Revision.ToString(CultureInfo.InvariantCulture), transaction);
            WriteMetadata(connection, LegacySourceHashKey, migration.SourceSha256, transaction);
            WriteMetadata(connection, "legacy_request_count", migration.RequestCount.ToString(CultureInfo.InvariantCulture), transaction);
            WriteMetadata(connection, "legacy_operation_count", migration.OperationCount.ToString(CultureInfo.InvariantCulture), transaction);
            WriteMetadata(connection, "legacy_receipt_count", migration.ReceiptCount.ToString(CultureInfo.InvariantCulture), transaction);
            WriteMetadata(connection, "legacy_invalidation_count", migration.PendingInvalidationCount.ToString(CultureInfo.InvariantCulture), transaction);
            transaction.Commit();
            Reload(connection);
            VerifyMigrationCore(connection, migration);
            return migration;
        }
    }

    public void VerifyMigration(OperationJournalMigrationRecord migration)
    {
        lock (gate)
        {
            using var connection = OpenConnection();
            VerifyMigrationCore(connection, migration);
        }
    }

    public OperationJournalMigrationRecord CreateEmptyBinding(DateTime completedAtUtc)
    {
        lock (gate)
        {
            if (requests.Count != 0 || operations.Count != 0 || receipts.Count != 0 || pendingInvalidations.Count != 0)
                throw new InvalidOperationException("An unbound operation journal already contains history.");
            return new OperationJournalMigrationRecord
            {
                JournalId = JournalId,
                CompletedAtUtc = completedAtUtc,
            };
        }
    }

    private void CreateSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, $$"""
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS requests (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS operations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL UNIQUE,
                revision INTEGER NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS receipts (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_receipts_operation_revision
                ON receipts(operation_id, revision, sequence);
            CREATE TABLE IF NOT EXISTS pending_invalidations (
                invalidation_key TEXT PRIMARY KEY,
                payload TEXT NOT NULL
            );
            INSERT INTO metadata(key, value) VALUES ('schema_version', '{{SchemaVersion}}')
            ON CONFLICT(key) DO NOTHING;
            """);
        var storedVersion = ReadMetadata(connection, "schema_version");
        if (!string.Equals(storedVersion, SchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported Quartermaster operation journal schema '{storedVersion ?? "missing"}'.");
    }

    private void Reload(SqliteConnection connection)
    {
        requests = ReadRows<SubmittedRequestRecord>(connection, "SELECT payload FROM requests ORDER BY sequence;");
        operations = ReadRows<OperationRecord>(connection, "SELECT payload FROM operations ORDER BY sequence;");
        receipts = ReadRows<OperationReceipt>(connection, "SELECT payload FROM receipts ORDER BY sequence;");
        pendingInvalidations = ReadRows<PendingCacheInvalidation>(connection, "SELECT payload FROM pending_invalidations ORDER BY rowid;");
        revision = long.TryParse(ReadMetadata(connection, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static List<T> ReadRows<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException($"The operation journal contains an invalid {typeof(T).Name} payload."));
        }
        return result;
    }

    private static void PersistChanges(
        SqliteConnection connection,
        QuartermasterState candidate,
        IReadOnlyList<SubmittedRequestRecord> currentRequests,
        IReadOnlyList<OperationRecord> currentOperations,
        IReadOnlyList<OperationReceipt> currentReceipts,
        IReadOnlyList<PendingCacheInvalidation> currentPendingInvalidations)
    {
        var currentById = currentOperations.ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);
        if (candidate.Operations.Count < currentOperations.Count || candidate.Operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() != candidate.Operations.Count)
            throw new InvalidOperationException("Operation rows are append/update only and must keep unique IDs.");
        if (candidate.Requests.Count < currentRequests.Count || candidate.Requests.Select(request => request.RequestId).Distinct(StringComparer.Ordinal).Count() != candidate.Requests.Count)
            throw new InvalidOperationException("Submitted requests are append-only and must keep unique IDs.");
        if (candidate.Receipts.Count < currentReceipts.Count)
            throw new InvalidOperationException("Operation receipts are append-only.");

        using var transaction = connection.BeginTransaction();
        foreach (var request in candidate.Requests.Skip(currentRequests.Count))
            InsertRequest(connection, request, transaction);
        foreach (var operation in candidate.Operations)
        {
            if (!currentById.TryGetValue(operation.OperationId, out var current))
                InsertOperation(connection, operation, transaction);
            else if (operation.Revision != current.Revision)
                UpdateOperation(connection, operation, transaction);
        }
        foreach (var receipt in candidate.Receipts.Skip(currentReceipts.Count))
            InsertReceipt(connection, receipt, transaction);

        var currentInvalidations = currentPendingInvalidations.ToDictionary(InvalidationKey, StringComparer.Ordinal);
        var candidateInvalidations = candidate.PendingCacheInvalidations.ToDictionary(InvalidationKey, StringComparer.Ordinal);
        foreach (var removed in currentInvalidations.Keys.Except(candidateInvalidations.Keys, StringComparer.Ordinal))
            DeleteInvalidation(connection, removed, transaction);
        foreach (var added in candidateInvalidations.Where(entry => !currentInvalidations.ContainsKey(entry.Key)))
            InsertInvalidation(connection, added.Value, transaction);

        WriteMetadata(connection, "revision", candidate.Revision.ToString(CultureInfo.InvariantCulture), transaction);
        transaction.Commit();
    }

    private void ValidateOperationOnlyMutation(QuartermasterState candidate)
    {
        if (candidate.PlanItems.Count != 0 || candidate.RestockPlans.Count != 0 || candidate.StowagePlans.Count != 0 ||
            candidate.ItemGroups.Count != 0 || candidate.StowageMigrations.Count != 0 || candidate.TransferPlanMigrations.Count != 0 ||
            candidate.LatestRetainerListingCapture is not null || candidate.OperationJournalMigration is not null)
            throw new InvalidOperationException("An operation-journal mutation attempted to change plan or configuration state.");
    }

    private void VerifyMigrationCore(SqliteConnection connection, OperationJournalMigrationRecord migration)
    {
        if (migration.SchemaVersion != SchemaVersion)
            throw new InvalidOperationException($"Operation migration expects schema {migration.SchemaVersion}, but this build supports {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(migration.JournalId) || !string.Equals(JournalId, migration.JournalId, StringComparison.Ordinal))
            throw new InvalidOperationException("The configured operation journal identity does not match the database.");
        if (!string.IsNullOrWhiteSpace(migration.SourceSha256) &&
            !string.Equals(ReadMetadata(connection, LegacySourceHashKey), migration.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The operation journal legacy-source receipt does not match the configuration migration record.");
        if (requests.Count < migration.RequestCount || operations.Count < migration.OperationCount || receipts.Count < migration.ReceiptCount || pendingInvalidations.Count < migration.PendingInvalidationCount)
            throw new InvalidOperationException("The operation journal contains fewer rows than its verified legacy migration receipt.");
    }

    private OperationJournalMigrationRecord MigrationRecordFromMetadata(SqliteConnection connection, DateTime completedAtUtc) => new()
    {
        JournalId = JournalId,
        SourceSha256 = ReadMetadata(connection, LegacySourceHashKey) ?? string.Empty,
        RequestCount = ReadIntMetadata(connection, "legacy_request_count"),
        OperationCount = ReadIntMetadata(connection, "legacy_operation_count"),
        ReceiptCount = ReadIntMetadata(connection, "legacy_receipt_count"),
        PendingInvalidationCount = ReadIntMetadata(connection, "legacy_invalidation_count"),
        CompletedAtUtc = completedAtUtc,
    };

    private static int ReadIntMetadata(SqliteConnection connection, string key) => int.TryParse(ReadMetadata(connection, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new InvalidDataException($"Operation journal metadata '{key}' is invalid.");

    private QuartermasterState CreateView() => new()
    {
        Revision = revision,
        Requests = requests,
        Operations = operations,
        Receipts = receipts,
        PendingCacheInvalidations = pendingInvalidations,
    };

    private static void InsertRequest(SqliteConnection connection, SubmittedRequestRecord request, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO requests(request_id, payload) VALUES ($id, $payload);";
        command.Parameters.AddWithValue("$id", request.RequestId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(request, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static void InsertOperation(SqliteConnection connection, OperationRecord operation, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operations(operation_id, revision, payload) VALUES ($id, $revision, $payload);";
        command.Parameters.AddWithValue("$id", operation.OperationId);
        command.Parameters.AddWithValue("$revision", operation.Revision);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static void UpdateOperation(SqliteConnection connection, OperationRecord operation, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE operations SET revision = $revision, payload = $payload WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operation.OperationId);
        command.Parameters.AddWithValue("$revision", operation.Revision);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation, JsonOptions));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Operation '{operation.OperationId}' disappeared during journal update.");
    }

    private static void InsertReceipt(SqliteConnection connection, OperationReceipt receipt, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO receipts(operation_id, revision, payload) VALUES ($operation, $revision, $payload);";
        command.Parameters.AddWithValue("$operation", receipt.OperationId);
        command.Parameters.AddWithValue("$revision", receipt.Revision);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(receipt, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static void InsertInvalidation(SqliteConnection connection, PendingCacheInvalidation invalidation, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO pending_invalidations(invalidation_key, payload) VALUES ($key, $payload);";
        command.Parameters.AddWithValue("$key", InvalidationKey(invalidation));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(invalidation, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static void DeleteInvalidation(SqliteConnection connection, string key, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM pending_invalidations WHERE invalidation_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMetadata(SqliteConnection connection, string key, string value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO metadata(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string EnsureJournalId(SqliteConnection connection)
    {
        var existing = ReadMetadata(connection, "journal_id");
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;
        var created = Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata(key, value) VALUES ('journal_id', $value);";
        command.Parameters.AddWithValue("$value", created);
        command.ExecuteNonQuery();
        return created;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static string InvalidationKey(PendingCacheInvalidation invalidation) =>
        $"{invalidation.OperationId}:{invalidation.RetainerId:X16}";

    private static SubmittedRequestRecord Copy(SubmittedRequestRecord request) => new()
    {
        RequestId = request.RequestId,
        OperationId = request.OperationId,
        CanonicalHash = request.CanonicalHash,
        AcceptedAtUtc = request.AcceptedAtUtc,
    };

    private static OperationReceipt Copy(OperationReceipt receipt) => new()
    {
        OperationId = receipt.OperationId,
        Revision = receipt.Revision,
        OccurredAtUtc = receipt.OccurredAtUtc,
        Status = receipt.Status,
        Code = receipt.Code,
        Message = receipt.Message,
        ItemId = receipt.ItemId,
        RetainerId = receipt.RetainerId,
        Quantity = receipt.Quantity,
    };

    private static PendingCacheInvalidation Copy(PendingCacheInvalidation invalidation) => new()
    {
        OperationId = invalidation.OperationId,
        RetainerId = invalidation.RetainerId,
        Owner = invalidation.Owner with { },
    };

    private static OperationRecord Copy(OperationRecord operation) => new()
    {
        OperationId = operation.OperationId,
        RequestId = operation.RequestId,
        Kind = operation.Kind,
        ExecuteImmediately = operation.ExecuteImmediately,
        Owner = operation.Owner with { },
        Status = operation.Status,
        Revision = operation.Revision,
        CreatedAtUtc = operation.CreatedAtUtc,
        UpdatedAtUtc = operation.UpdatedAtUtc,
        Message = operation.Message,
        SourcePlanId = operation.SourcePlanId,
        SourcePlanRevision = operation.SourcePlanRevision,
        SourcePlanName = operation.SourcePlanName,
        SourcePlanItems = operation.SourcePlanItems.Select(item => new TargetPlanItem
        {
            Id = item.Id,
            StowagePlanId = item.StowagePlanId,
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            TargetQuantity = item.TargetQuantity,
            Quality = item.Quality,
            Routing = new StowageRoutingPolicy
            {
                Mode = item.Routing?.Mode ?? StowageRoutingMode.ConsolidateFirst,
                Overflow = item.Routing?.Overflow ?? StowageOverflowPolicy.AnyOwnerRetainer,
                PreferredRetainerIds = item.Routing?.PreferredRetainerIds.ToList() ?? [],
            },
            Notes = item.Notes,
            Enabled = item.Enabled,
        }).ToList(),
        DepositCandidates = operation.DepositCandidates.Select(candidate => new DepositCandidateAuthorization
        {
            RetainerId = candidate.RetainerId,
            RetainerName = candidate.RetainerName,
            ObservedAtUtc = candidate.ObservedAtUtc,
            CapacityByItem = candidate.CapacityByItem.ToDictionary(entry => entry.Key, entry => entry.Value),
            CapacityByVariant = candidate.CapacityByVariant.ToDictionary(entry => entry.Key, entry => entry.Value),
        }).ToList(),
        Lines = operation.Lines.Select(line => new OperationLine
        {
            SourcePlanId = line.SourcePlanId,
            SourceRuleId = line.SourceRuleId,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            IsHighQuality = line.IsHighQuality,
            Quality = line.Quality,
            TargetQuantity = line.TargetQuantity,
            ShortageQuantity = line.ShortageQuantity,
            TransferredQuantity = line.TransferredQuantity,
        }).ToList(),
    };
}
