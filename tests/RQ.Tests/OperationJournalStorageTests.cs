using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RQ.Domain;
using RQ.Operations;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class OperationJournalStorageTests
{
    [Fact]
    public void LegacyLargeState_MigratesExactlyOnceAndKeepsConfigurationSmall()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        var store = new QuartermasterStateStore(statePath);
        var legacy = LargeLegacyState();
        store.Save(legacy);
        var sourceHash = Hash(statePath);
        var sourceLength = new FileInfo(statePath).Length;

        var first = new StateRepository(store);
        var firstSnapshot = first.FullSnapshot();
        var compact = store.Load();
        var backupPath = $"{statePath}.pre-operation-journal-v5.bak";

        Assert.Equal(105, firstSnapshot.Operations.Count);
        Assert.Equal(2_118, firstSnapshot.Receipts.Count);
        Assert.Equal(39, firstSnapshot.Requests.Count);
        Assert.Single(firstSnapshot.PendingCacheInvalidations);
        Assert.Empty(compact.Operations);
        Assert.Empty(compact.Receipts);
        Assert.Empty(compact.Requests);
        Assert.Empty(compact.PendingCacheInvalidations);
        Assert.NotNull(compact.OperationJournalMigration);
        Assert.Equal(sourceHash, compact.OperationJournalMigration!.SourceSha256);
        Assert.Equal(sourceHash, Hash(backupPath));
        Assert.True(new FileInfo(statePath).Length < sourceLength / 10);

        var second = new StateRepository(store);
        var secondSnapshot = second.FullSnapshot();
        Assert.Equal(firstSnapshot.Operations.Select(operation => operation.OperationId), secondSnapshot.Operations.Select(operation => operation.OperationId));
        Assert.Equal(firstSnapshot.Receipts.Select(receipt => (receipt.OperationId, receipt.Revision)), secondSnapshot.Receipts.Select(receipt => (receipt.OperationId, receipt.Revision)));
        Assert.Equal(105, Scalar(Path.Combine(directory.Path, "state-operations.db"), "SELECT COUNT(*) FROM operations;"));
        Assert.Equal(2_118, Scalar(Path.Combine(directory.Path, "state-operations.db"), "SELECT COUNT(*) FROM receipts;"));
    }

    [Fact]
    public void OperationTransition_AppendsRowsWithoutRewritingConfigurationOrHistory()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        var store = new QuartermasterStateStore(statePath);
        store.Save(LargeLegacyState());
        var repository = new StateRepository(store);
        var journal = new OperationJournal(repository);
        var operation = journal.CreateManual(TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 50, Enabled = true }]);
        var compactHash = Hash(statePath);
        var databasePath = Path.Combine(directory.Path, "state-operations.db");
        var operationCount = Scalar(databasePath, "SELECT COUNT(*) FROM operations;");
        var receiptCount = Scalar(databasePath, "SELECT COUNT(*) FROM receipts;");

        var stopwatch = Stopwatch.StartNew();
        journal.Transition(operation.OperationId, OperationStatuses.Running, "ExecutionStarted", "Started.");
        stopwatch.Stop();

        Assert.Equal(compactHash, Hash(statePath));
        Assert.Equal(operationCount, Scalar(databasePath, "SELECT COUNT(*) FROM operations;"));
        Assert.Equal(receiptCount + 1, Scalar(databasePath, "SELECT COUNT(*) FROM receipts;"));
        Assert.Equal(OperationStatuses.Running, journal.Get(operation.OperationId)!.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"One normalized transition took {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
    }

    [Fact]
    public void MissingJournalAfterMigration_FailsInsteadOfPresentingEmptyHistory()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        var store = new QuartermasterStateStore(statePath);
        store.Save(LargeLegacyState());
        _ = new StateRepository(store);
        var databasePath = Path.Combine(directory.Path, "state-operations.db");
        File.Delete(databasePath);

        var exception = Assert.Throws<InvalidOperationException>(() => new StateRepository(store));

        Assert.Contains("journal identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingJournalAfterFreshInstall_FailsInsteadOfPresentingEmptyHistory()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        var store = new QuartermasterStateStore(statePath);
        var repository = new StateRepository(store);
        var journal = new OperationJournal(repository);
        _ = journal.CreateManual(TestData.Owner,
            [new TargetPlanItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 50, Enabled = true }]);
        File.Delete(Path.Combine(directory.Path, "state-operations.db"));

        var exception = Assert.Throws<InvalidOperationException>(() => new StateRepository(store));

        Assert.Contains("journal identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static QuartermasterState LargeLegacyState()
    {
        var state = new QuartermasterState
        {
            Revision = 7_432,
            PlanItems = [new TargetPlanItem { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 50, Enabled = true }],
        };
        for (var index = 0; index < 105; index++)
        {
            var operationId = $"operation-{index:D3}";
            state.Operations.Add(new OperationRecord
            {
                OperationId = operationId,
                RequestId = operationId,
                Kind = OperationKinds.Retrieval,
                Owner = TestData.Owner,
                Status = OperationStatuses.Succeeded,
                Revision = 21,
                CreatedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                UpdatedAtUtc = new DateTime(2026, 7, 1, 12, 5, 0, DateTimeKind.Utc).AddMinutes(index),
                Message = "Completed.",
                Lines = [new OperationLine { ItemId = 100, ItemName = "Darksteel Ore", TargetQuantity = 999, TransferredQuantity = 999 }],
            });
        }
        for (var index = 0; index < 2_118; index++)
        {
            state.Receipts.Add(new OperationReceipt
            {
                OperationId = $"operation-{index % 105:D3}",
                Revision = (index / 105) + 1,
                OccurredAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(index),
                Status = OperationStatuses.Running,
                Code = "TransferVerified",
                Message = "Verified one movement.",
                ItemId = 100,
                RetainerId = 10,
                Quantity = 999,
            });
        }
        for (var index = 0; index < 39; index++)
        {
            state.Requests.Add(new SubmittedRequestRecord
            {
                RequestId = $"request-{index:D3}",
                OperationId = $"operation-{index:D3}",
                CanonicalHash = $"hash-{index:D3}",
                AcceptedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            });
        }
        state.PendingCacheInvalidations.Add(new PendingCacheInvalidation
        {
            OperationId = "operation-104",
            RetainerId = 10,
            Owner = TestData.Owner,
        });
        return state;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
