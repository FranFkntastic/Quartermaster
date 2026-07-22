using System.Security.Cryptography;
using System.Text.Json;
using RQ.Persistence;

namespace RQ.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void FirstLoad_PreservesLegacyCachePlanAndWritesProvenance()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, """
        {
          "123": {
            "RetainerId": 123,
            "RetainerName": "Eris",
            "OwnerCharacterName": "Current Character",
            "OwnerHomeWorld": "Maduin",
            "LastUpdated": "2026-07-20T12:30:00Z",
            "Bags": [{ "BagName": "RetainerPage1", "Items": [{ "ItemId": 100, "ItemName": "Darksteel Ore", "Quantity": 37, "Condition": 1234, "ConditionPercent": 42.5, "Equipped": true }] }],
            "MarketListings": [{ "ItemId": 100, "ItemName": "Darksteel Ore", "Quantity": 2, "Condition": 765, "UnitPrice": 44, "ListedAt": "2026-07-19T08:15:00-04:00" }]
          }
        }
        """);
        File.WriteAllText(paths.LegacyConfigurationPath, """
        {
          "RetainerRestockPlanItems": [
            {
              "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "ItemId": 100,
              "ItemName": "Darksteel Ore",
              "DesiredPlayerQuantity": 50,
              "Note": "Workshop reserve",
              "Enabled": false
            },
            {
              "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "ItemId": 200,
              "ItemName": "Zero target evidence",
              "DesiredPlayerQuantity": 0,
              "Enabled": false
            }
          ]
        }
        """);

        var result = new LegacyMigrationService(paths, () => new DateTime(2026, 7, 21, 14, 0, 0, DateTimeKind.Utc)).Run();

        Assert.True(result.Migrated);
        var retainer = Assert.Single(new RetainerCacheStore(paths.TargetCachePath).Load().Values);
        Assert.Equal("Current Character", retainer.Owner.CharacterName);
        Assert.Equal(new DateTime(2026, 7, 20, 12, 30, 0, DateTimeKind.Utc), retainer.ObservedAtUtc);
        var cachedItem = Assert.Single(Assert.Single(retainer.Bags).Items);
        Assert.Equal(1234, cachedItem.Condition);
        Assert.Equal(42.5f, cachedItem.ConditionPercent);
        Assert.True(cachedItem.Equipped);
        Assert.Equal(765, Assert.Single(retainer.Listings).Condition);
        Assert.Equal(new DateTime(2026, 7, 19, 12, 15, 0, DateTimeKind.Utc), Assert.Single(retainer.Listings).ListedAtUtc);
        var plans = new QuartermasterStateStore(paths.TargetStatePath).Load().PlanItems;
        Assert.Equal(2, plans.Count);
        var plan = plans[0];
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), plan.Id);
        Assert.Equal(50, plan.TargetQuantity);
        Assert.Equal("Workshop reserve", plan.Notes);
        Assert.False(plan.Enabled);
        Assert.Equal(0, plans[1].TargetQuantity);

        using var receipt = JsonDocument.Parse(File.ReadAllText(paths.ReceiptPath));
        Assert.Equal("gooseworks-quartermaster-migration/v1", receipt.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("cache").GetProperty("importedCount").GetInt32());
        Assert.Equal(2, receipt.RootElement.GetProperty("plan").GetProperty("importedCount").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.LegacyCachePath))).ToLowerInvariant(), receipt.RootElement.GetProperty("cache").GetProperty("sha256").GetString());
    }

    [Fact]
    public void MigrationReceipt_MakesMigrationIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, "{}");
        File.WriteAllText(paths.LegacyConfigurationPath, "{}");
        var service = new LegacyMigrationService(paths);
        Assert.True(service.Run().Migrated);
        File.WriteAllText(paths.LegacyConfigurationPath, "{ broken");

        var replay = service.Run();

        Assert.False(replay.Migrated);
        Assert.Contains("already completed", replay.Message);
    }

    [Fact]
    public void ExistingRqData_PreventsLegacyRead()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, "{ broken");
        new QuartermasterStateStore(paths.TargetStatePath).Save(new());

        var result = new LegacyMigrationService(paths).Run();

        Assert.False(result.Migrated);
        Assert.False(File.Exists(paths.ReceiptPath));
    }

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
    public void MalformedPlanIdentity_FailsBeforeWritingTargets()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyConfigurationPath, """
        { "RetainerRestockPlanItems": [{ "Id": "not-a-guid", "ItemId": 100, "ItemName": "Ore", "DesiredPlayerQuantity": 12 }] }
        """);

        Assert.Throws<InvalidDataException>(() => new LegacyMigrationService(paths).Run());

        Assert.False(File.Exists(paths.TargetCachePath));
        Assert.False(File.Exists(paths.TargetStatePath));
        Assert.False(File.Exists(paths.ReceiptPath));
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

    [Fact]
    public void ConfigOnlyRetainerCache_IsImportedWithoutDependingOnMmfStartupOrder()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyConfigurationPath, """
        {
          "RetainerCache": {
            "123": {
              "RetainerId": 123,
              "RetainerName": "Config Source",
              "OwnerCharacterName": "Current Character",
              "OwnerHomeWorld": "Maduin",
              "LastUpdated": "2026-07-20T12:30:00Z",
              "Bags": []
            }
          }
        }
        """);

        var result = new LegacyMigrationService(paths).Run();

        Assert.True(result.Migrated);
        Assert.Equal("Config Source", Assert.Single(new RetainerCacheStore(paths.TargetCachePath).Load().Values).RetainerName);
        using var receipt = JsonDocument.Parse(File.ReadAllText(paths.ReceiptPath));
        Assert.Equal(paths.LegacyConfigurationPath, receipt.RootElement.GetProperty("cache").GetProperty("path").GetString());
    }

    [Fact]
    public void NullLegacyNumbers_AreTreatedAsMissingValues()
    {
        using var directory = new TemporaryDirectory();
        var paths = Paths(directory.Path);
        File.WriteAllText(paths.LegacyCachePath, """
        {
          "123": {
            "RetainerId": null,
            "Gil": null,
            "Bags": [{
              "BagName": "RetainerPage1",
              "Items": [{
                "ItemId": null,
                "Quantity": null,
                "Condition": null,
                "ConditionPercent": null,
                "SlotIndex": null
              }]
            }],
            "MarketListings": [{ "Condition": null, "UnitPrice": null }]
          }
        }
        """);

        var result = new LegacyMigrationService(paths).Run();

        Assert.True(result.Migrated);
        var retainer = Assert.Single(new RetainerCacheStore(paths.TargetCachePath).Load().Values);
        Assert.Equal(123UL, retainer.RetainerId);
        Assert.Equal(0UL, retainer.Gil);
        var item = Assert.Single(Assert.Single(retainer.Bags).Items);
        Assert.Equal(0U, item.ItemId);
        Assert.Equal(0U, item.Quantity);
        Assert.Equal(0, item.Condition);
        Assert.Null(item.ConditionPercent);
        Assert.Null(item.SlotIndex);
        var listing = Assert.Single(retainer.Listings);
        Assert.Equal(0, listing.Condition);
        Assert.Null(listing.UnitPrice);
    }

    private static LegacyMigrationPaths Paths(string directory) => new(
        Path.Combine(directory, "retainer-cache-legacy.json"),
        Path.Combine(directory, "MarketMafioso.json"),
        Path.Combine(directory, "rq-cache.json"),
        Path.Combine(directory, "rq-state.json"),
        Path.Combine(directory, "migration-receipt.json"));
}
