using System.Security.Cryptography;
using System.Text.Json;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class MigrationTests
{

    [Fact]
    public void PartialCommit_ResumesFromDurablePendingBundle()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, "{}");
        File.WriteAllText(paths.LegacyConfigurationPath, """
        { "RetainerRestockPlanItems": [{ "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "ItemId": 100, "ItemName": "Ore", "DesiredPlayerQuantity": 12 }] }
        """);
        var interrupted = new LegacyMigrationService(paths, afterCommitStep: step =>
        {
            if (step == "cache")
                throw new IOException("simulated interruption");
        });

        Assert.Throws<IOException>(() => interrupted.Run());
        Assert.True(File.Exists(paths.TargetCachePath));
        Assert.False(File.Exists(paths.TargetStatePath));
        Assert.True(File.Exists(paths.ReceiptPath + ".pending"));

        var resumed = new LegacyMigrationService(paths).Run();

        Assert.True(resumed.Migrated);
        Assert.Contains("Resumed", resumed.Message);
        Assert.Single(new QuartermasterStateStore(paths.TargetStatePath).Load().PlanItems);
        Assert.True(File.Exists(paths.ReceiptPath));
        Assert.False(File.Exists(paths.ReceiptPath + ".pending"));
    }

    [Fact]
    public void PartialCommit_RefusesToFinalizeWhenExistingTargetDiffersFromPendingBundle()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, "{}");
        File.WriteAllText(paths.LegacyConfigurationPath, "{}");
        var interrupted = new LegacyMigrationService(paths, afterCommitStep: step =>
        {
            if (step == "cache")
                throw new IOException("simulated interruption");
        });
        Assert.Throws<IOException>(() => interrupted.Run());
        File.WriteAllText(paths.TargetCachePath, "{\"99\":{\"retainerId\":99,\"retainerName\":\"foreign\"}}");

        Assert.Throws<InvalidDataException>(() => new LegacyMigrationService(paths).Run());

        Assert.True(File.Exists(paths.ReceiptPath + ".pending"));
        Assert.False(File.Exists(paths.ReceiptPath));
    }

    private static LegacyMigrationPaths Paths(string directory) => new(
        Path.Combine(directory, "retainer-cache-legacy.json"),
        Path.Combine(directory, "MarketMafioso.json"),
        Path.Combine(directory, "rq-cache.json"),
        Path.Combine(directory, "rq-state.json"),
        Path.Combine(directory, "migration-receipt.json"));
}
