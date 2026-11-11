using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RQ.Domain;

namespace RQ.Planning;

/// <param name="Json">The share text, or null when no line can go to the workshop.</param>
/// <param name="WithoutWorkshopProject">Lines no workshop project produces (including every HQ line).</param>
/// <param name="Capped">Lines whose quantity exceeded the format's per-project limit and were exported at that limit.</param>
public sealed record MarketRunCraftExportResult(
    string? Json,
    IReadOnlyList<MarketRunCraftLine> WithoutWorkshopProject,
    IReadOnlyList<MarketRunCraftLine> Capped);

/// <summary>
/// Writes a market run craft queue as a MarketMafioso workshop saved job. Mirrors
/// MarketMafioso's WorkshopSavedJobShare format (id, version and limits) without referencing it.
/// </summary>
public static class MarketRunCraftExport
{
    public const string FormatId = "marketmafioso.workshop-saved-job";
    public const int FormatVersion = 1;
    public const int MaxQuantity = 9999;
    public const int MaxNameLength = 128;
    public const string DefaultJobName = "Market run";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="resolveWorkshopProject">Maps a result item id to its workshop project (CompanyCraftSequence row) id.</param>
    public static MarketRunCraftExportResult Export(
        string jobName,
        IReadOnlyList<MarketRunCraftLine> lines,
        Func<uint, uint?> resolveWorkshopProject,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(resolveWorkshopProject);

        var items = new List<ShareItem>();
        var without = new List<MarketRunCraftLine>();
        var capped = new List<MarketRunCraftLine>();
        foreach (var line in lines.Where(line => line.Quantity > 0))
        {
            // Workshop projects only produce NQ results.
            var project = line.Quality == ItemQualityPolicy.HqOnly ? null : resolveWorkshopProject(line.ItemId);
            if (project is not { } projectId || projectId == 0)
            {
                without.Add(line);
                continue;
            }
            // MarketMafioso merges repeated project ids and rejects a merged total above the limit.
            var existing = items.FindIndex(item => item.WorkshopItemId == projectId);
            var previous = existing < 0 ? 0 : items[existing].Quantity;
            var quantity = Math.Min(MaxQuantity, previous + line.Quantity);
            if (previous + line.Quantity > MaxQuantity)
                capped.Add(line);
            var item = new ShareItem(projectId, string.IsNullOrWhiteSpace(line.ItemName) ? null : line.ItemName, quantity);
            if (existing < 0)
                items.Add(item);
            else
                items[existing] = item;
        }

        var json = items.Count == 0
            ? null
            : JsonSerializer.Serialize(new ShareDocument(FormatId, FormatVersion, JobName(jobName), nowUtc, items), JsonOptions);
        return new(json, without, capped);
    }

    private static string JobName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return DefaultJobName;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }

    private sealed record ShareDocument(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("exportedAt")] DateTime ExportedAt,
        [property: JsonPropertyName("items")] IReadOnlyList<ShareItem> Items);

    private sealed record ShareItem(
        [property: JsonPropertyName("workshopItemId")] uint WorkshopItemId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("quantity")] int Quantity);
}
