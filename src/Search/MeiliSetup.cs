using System.Text.Json;
using System.Text.Json.Serialization;
using MedFinder.Domain;
using MedFinder.Services;
using Meilisearch;

namespace MedFinder.Search;

public class DrugDoc
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";
    [JsonPropertyName("name_ar")] public string NameAr { get; set; } = "";
    [JsonPropertyName("name_ar_norm")] public string NameArNorm { get; set; } = "";
    [JsonPropertyName("scientific_name")] public string? Scientific { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }

    public static DrugDoc From(Drug d) => new()
    {
        Id = d.Id.ToString(),
        NameEn = d.NameEn,
        NameAr = d.NameAr,
        NameArNorm = d.NameArNorm,
        Scientific = d.ActiveIngredient,
        Manufacturer = d.Manufacturer,
        Route = d.Route
    };
}

public static class MeiliSetup
{
    public const string IndexName = "drugs";

    /// شغّلها مرة عند بدء التطبيق (أو قبل الاستيراد).
    public static async Task ConfigureAsync(MeilisearchClient client, string synonymsPath)
    {
        await client.CreateIndexAsync(IndexName, "id"); // لو موجود مش هيضر

        var synonyms = await LoadSynonymsAsync(synonymsPath);

        var settings = new Settings
        {
            // الترتيب = الأولوية: الاسم الإنجليزي ثم العربي ثم المادة الفعالة
            SearchableAttributes = new[] { "name_en", "name_ar_norm", "scientific_name", "manufacturer" },
            DisplayedAttributes = new[] { "id", "name_en", "name_ar", "scientific_name", "manufacturer", "route" },
            FilterableAttributes = new[] { "route", "manufacturer" },
            Synonyms = synonyms
        };

        await client.Index(IndexName).UpdateSettingsAsync(settings);
    }

    /// الملف: { "باراسيتامول": ["paracetamol"] } — نعمل الاتجاهين بعد التطبيع.
    private static async Task<Dictionary<string, IEnumerable<string>>> LoadSynonymsAsync(string path)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(await File.ReadAllTextAsync(path))
                  ?? new();
        var result = new Dictionary<string, IEnumerable<string>>();
        foreach (var (ar, en) in raw)
        {
            var arN = ArabicNormalizer.Normalize(ar);
            result[arN] = en;
            foreach (var e in en) result[e.ToLowerInvariant()] = new[] { arN };
        }
        return result;
    }
}
