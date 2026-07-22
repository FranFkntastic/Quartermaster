using System.Security.Cryptography;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;
using RQ.Domain;

namespace RQ.Persistence;

public sealed record LegacyMigrationPaths(
    string LegacyCachePath,
    string LegacyConfigurationPath,
    string TargetCachePath,
    string TargetStatePath,
    string ReceiptPath);

public sealed record MigrationSourceReceipt(string Path, string? Sha256, int ImportedCount);

public sealed record MigrationReceipt(
    string Schema,
    DateTime MigratedAtUtc,
    MigrationSourceReceipt Cache,
    MigrationSourceReceipt Plan);

public sealed record MigrationResult(bool Migrated, int CacheCount, int PlanCount, string Message);
public sealed record MigrationBundle(
    string Schema,
    Dictionary<ulong, CachedRetainer> Cache,
    QuartermasterState State,
    MigrationReceipt Receipt);

public sealed class LegacyMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly LegacyMigrationPaths paths;
    private readonly Func<DateTime> utcNow;
    private readonly Action<string>? afterCommitStep;

    public LegacyMigrationService(LegacyMigrationPaths paths, Func<DateTime>? utcNow = null, Action<string>? afterCommitStep = null)
    {
        this.paths = paths;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.afterCommitStep = afterCommitStep;
    }

    public MigrationResult Run()
    {
        if (File.Exists(paths.ReceiptPath))
        {
            File.Delete(PendingPath);
            return new(false, 0, 0, "Migration already completed.");
        }
        if (File.Exists(PendingPath))
        {
            var pending = AtomicJsonFile.Read<MigrationBundle>(PendingPath, JsonOptions)
                ?? throw new InvalidDataException("Quartermaster migration pending bundle is empty.");
            return Commit(pending, resumed: true);
        }
        if (File.Exists(paths.TargetCachePath) || File.Exists(paths.TargetStatePath))
            return new(false, 0, 0, "Retainer Quartermaster (RQ) data already exists; legacy files were not read.");

        var cacheSourcePath = File.Exists(paths.LegacyCachePath) ? paths.LegacyCachePath : paths.LegacyConfigurationPath;
        var cache = File.Exists(paths.LegacyCachePath)
            ? ReadLegacyCache(paths.LegacyCachePath)
            : ReadLegacyCacheFromConfiguration(paths.LegacyConfigurationPath);
        var plan = ReadLegacyPlan(paths.LegacyConfigurationPath);
        var receipt = new MigrationReceipt(
            "gooseworks-quartermaster-migration/v1",
            utcNow(),
            new(cacheSourcePath, HashIfPresent(cacheSourcePath), cache.Count),
            new(paths.LegacyConfigurationPath, HashIfPresent(paths.LegacyConfigurationPath), plan.Count));
        var bundle = new MigrationBundle(
            "gooseworks-quartermaster-migration-pending/v1",
            cache,
            new QuartermasterState { PlanItems = plan },
            receipt);
        AtomicJsonFile.Write(PendingPath, bundle, JsonOptions);
        return Commit(bundle, resumed: false);
    }

    private MigrationResult Commit(MigrationBundle bundle, bool resumed)
    {
        if (bundle.Schema != "gooseworks-quartermaster-migration-pending/v1")
            throw new InvalidDataException($"Unsupported Quartermaster migration bundle '{bundle.Schema}'.");
        if (File.Exists(paths.TargetCachePath))
            VerifyEquivalent(paths.TargetCachePath, new RetainerCacheStore(paths.TargetCachePath).Load(), bundle.Cache);
        else
            new RetainerCacheStore(paths.TargetCachePath).Save(bundle.Cache);
        afterCommitStep?.Invoke("cache");
        if (File.Exists(paths.TargetStatePath))
            VerifyEquivalent(paths.TargetStatePath, new QuartermasterStateStore(paths.TargetStatePath).Load(), bundle.State);
        else
            new QuartermasterStateStore(paths.TargetStatePath).Save(bundle.State);
        afterCommitStep?.Invoke("state");
        if (!File.Exists(paths.ReceiptPath))
            AtomicJsonFile.Write(paths.ReceiptPath, bundle.Receipt, JsonOptions);
        afterCommitStep?.Invoke("receipt");
        File.Delete(PendingPath);
        return new(
            true,
            bundle.Cache.Count,
            bundle.State.PlanItems.Count,
            $"{(resumed ? "Resumed and imported" : "Imported")} {bundle.Cache.Count} retainer caches and {bundle.State.PlanItems.Count} plan rows.");
    }

    private string PendingPath => paths.ReceiptPath + ".pending";

    private static void VerifyEquivalent<T>(string path, T actual, T expected)
    {
        var actualJson = JsonSerializer.Serialize(actual, JsonOptions);
        var expectedJson = JsonSerializer.Serialize(expected, JsonOptions);
        if (!string.Equals(actualJson, expectedJson, StringComparison.Ordinal))
            throw new InvalidDataException($"Existing migration target '{path}' differs from durable pending evidence.");
    }

    private static Dictionary<ulong, CachedRetainer> ReadLegacyCache(string path)
    {
        if (!File.Exists(path))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return ReadLegacyCacheObject(document.RootElement);
    }

    private static Dictionary<ulong, CachedRetainer> ReadLegacyCacheFromConfiguration(string path)
    {
        if (!File.Exists(path))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return TryProperty(document.RootElement, "RetainerCache", out var cache) && cache.ValueKind == JsonValueKind.Object
            ? ReadLegacyCacheObject(cache)
            : [];
    }

    private static Dictionary<ulong, CachedRetainer> ReadLegacyCacheObject(JsonElement sourceObject)
    {
        var result = new Dictionary<ulong, CachedRetainer>();
        foreach (var property in sourceObject.EnumerateObject())
        {
            if (!ulong.TryParse(property.Name, out var key))
                continue;
            var source = property.Value;
            var retainerId = GetUInt64(source, "RetainerId") ?? key;
            var retainer = new CachedRetainer
            {
                RetainerId = retainerId,
                RetainerName = GetString(source, "RetainerName") ?? string.Empty,
                Owner = new OwnerScope
                {
                    CharacterName = GetString(source, "OwnerCharacterName") ?? string.Empty,
                    HomeWorldName = GetString(source, "OwnerHomeWorld") ?? string.Empty,
                },
                ObservedAtUtc = GetDateTime(source, "LastUpdated") ?? DateTime.MinValue,
                Gil = GetUInt64(source, "Gil") ?? 0,
                Bags = ReadBags(source),
                Listings = ReadListings(source),
            };
            foreach (var bag in retainer.Bags.Where(bag => bag.ObservedAtUtc is null))
                bag.ObservedAtUtc = retainer.ObservedAtUtc;
            var observedSources = retainer.Bags.Select(bag => bag.BagName).ToList();
            if (TryProperty(source, "Gil", out _))
            {
                retainer.GilObservedAtUtc = retainer.ObservedAtUtc;
                observedSources.Add("RetainerGil");
            }
            if (TryProperty(source, "MarketListings", out _))
            {
                retainer.ListingsObservedAtUtc = retainer.ObservedAtUtc;
                observedSources.Add("RetainerMarket");
            }
            retainer.RequestedSources = observedSources.Distinct(StringComparer.Ordinal).ToList();
            retainer.ObservedSources = retainer.RequestedSources.ToList();
            result[retainerId] = retainer;
        }
        return result;
    }

    private static List<TargetPlanItem> ReadLegacyPlan(string path)
    {
        if (!File.Exists(path))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!TryProperty(document.RootElement, "RetainerRestockPlanItems", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<TargetPlanItem>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var id = GetGuid(item, "Id") ?? throw new InvalidDataException($"Legacy plan row {index} has no valid ID.");
            var itemId = GetUInt32(item, "ItemId") ?? 0;
            if (itemId == 0)
                throw new InvalidDataException($"Legacy plan row {index} has no valid item ID.");
            result.Add(new TargetPlanItem
            {
                Id = id,
                ItemId = itemId,
                ItemName = GetString(item, "ItemName") ?? string.Empty,
                TargetQuantity = GetInt32(item, "DesiredPlayerQuantity") ?? 0,
                Notes = GetString(item, "Note") ?? string.Empty,
                Enabled = GetBoolean(item, "Enabled") ?? true,
            });
            index++;
        }
        return result;
    }

    private static List<CachedBag> ReadBags(JsonElement source)
    {
        if (!TryProperty(source, "Bags", out var bags) || bags.ValueKind != JsonValueKind.Array)
            return [];
        return bags.EnumerateArray().Select(bag => new CachedBag
        {
            BagName = GetString(bag, "BagName") ?? string.Empty,
            Location = GetString(bag, "Location"),
            ObservedAtUtc = GetDateTime(bag, "ObservedAtUtc"),
            Items = ReadItems(bag),
        }).ToList();
    }

    private static List<CachedItem> ReadItems(JsonElement bag)
    {
        if (!TryProperty(bag, "Items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray().Select(item => new CachedItem
        {
            ItemId = GetUInt32(item, "ItemId") ?? 0,
            ItemName = GetString(item, "ItemName") ?? string.Empty,
            ItemType = GetString(item, "ItemType"),
            Quantity = GetUInt32(item, "Quantity") ?? 0,
            IsHq = GetBoolean(item, "IsHQ") ?? false,
            Condition = GetSingle(item, "Condition") ?? 0,
            ConditionPercent = GetSingle(item, "ConditionPercent"),
            ContainerKey = GetString(item, "ContainerKey"),
            SlotIndex = GetInt32(item, "SlotIndex"),
            Equipped = GetBoolean(item, "Equipped"),
        }).ToList();
    }

    private static List<CachedMarketListing> ReadListings(JsonElement source)
    {
        if (!TryProperty(source, "MarketListings", out var listings) || listings.ValueKind != JsonValueKind.Array)
            return [];
        return listings.EnumerateArray().Select(item => new CachedMarketListing
        {
            ItemId = GetUInt32(item, "ItemId") ?? 0,
            ItemName = GetString(item, "ItemName") ?? string.Empty,
            ItemType = GetString(item, "ItemType"),
            Quantity = GetUInt32(item, "Quantity") ?? 0,
            IsHq = GetBoolean(item, "IsHQ") ?? false,
            Condition = GetSingle(item, "Condition") ?? 0,
            ConditionPercent = GetSingle(item, "ConditionPercent"),
            ContainerKey = GetString(item, "ContainerKey"),
            SlotIndex = GetInt32(item, "SlotIndex"),
            UnitPrice = GetUInt32(item, "UnitPrice"),
            ListedAtUtc = GetDateTime(item, "ListedAt"),
        }).ToList();
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? GetBoolean(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static int? GetInt32(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
    private static uint? GetUInt32(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var x) ? x : null;
    private static ulong? GetUInt64(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var x) ? x : null;
    private static float? GetSingle(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var x) ? x : null;
    private static Guid? GetGuid(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var x) ? x : null;
    private static DateTime? GetDateTime(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var x) ? x.UtcDateTime : null;

    private static string? HashIfPresent(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : null;
}
