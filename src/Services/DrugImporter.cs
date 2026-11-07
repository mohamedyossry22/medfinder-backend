using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Search;
using Meilisearch;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Services;

public class DrugImporter(AppDbContext db, MeilisearchClient meili, ILogger<DrugImporter> log)
{
    private record RawDrug(
        [property: JsonPropertyName("commercial_name_en")] string? NameEn,
        [property: JsonPropertyName("commercial_name_ar")] string? NameAr,
        [property: JsonPropertyName("scientific_name")] string? Scientific,
        [property: JsonPropertyName("manufacturer")] string? Manufacturer,
        [property: JsonPropertyName("drug_class")] string? DrugClass,
        [property: JsonPropertyName("route")] string? Route,
        [property: JsonPropertyName("price_egp")] decimal? Price);

    /// ID ثابت: نفس الدواء = نفس الـ ID في كل مرة نستورد فيها (مهم للطلبات والباركودات).
    private static Guid StableId(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    public async Task<ImportResult> ImportAsync(string jsonPath, string restrictedPath, CancellationToken ct = default)
    {
        var raw = JsonSerializer.Deserialize<List<RawDrug>>(
            await File.ReadAllTextAsync(jsonPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("ملف الأدوية فاضي أو غير صالح");

        var restricted = (await File.ReadAllLinesAsync(restrictedPath, ct))
            .Select(l => l.Trim().ToUpperInvariant())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        var existing = await db.Drugs.ToDictionaryAsync(d => d.Id, ct);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, skipped = 0, restrictedCount = 0;
        var docs = new List<DrugDoc>();
        var restrictedIds = new List<string>();

        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r.NameEn)) { skipped++; continue; }

            var key = $"{r.NameEn.Trim()}|{r.Manufacturer?.Trim()}".ToUpperInvariant();
            var id = StableId(key);
            for (var n = 2; !seen.Add(id); n++) id = StableId($"{key}#{n}"); // تكرار نادر

            var ingredient = r.Scientific?.ToUpperInvariant() ?? "";
            var isRestricted = restricted.Any(t => ingredient.Contains(t));
            if (isRestricted) restrictedCount++;

            if (!existing.TryGetValue(id, out var drug))
            {
                drug = new Drug { Id = id };
                db.Drugs.Add(drug);
                added++;
            }
            else updated++;

            drug.NameEn = r.NameEn.Trim();
            drug.NameAr = r.NameAr?.Trim() ?? "";
            drug.NameArNorm = ArabicNormalizer.Normalize(r.NameAr);
            drug.ActiveIngredient = r.Scientific?.Trim();
            drug.Manufacturer = r.Manufacturer?.Trim();
            drug.DrugClass = r.DrugClass?.Trim();
            drug.Route = r.Route?.Trim();
            drug.Price = r.Price;
            drug.IsSearchable = !isRestricted;
            drug.UpdatedAt = DateTime.UtcNow;

            if (isRestricted) restrictedIds.Add(id.ToString());
            else docs.Add(DrugDoc.From(drug));
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("DB: +{Added} ~{Updated} skipped {Skipped} restricted {R}", added, updated, skipped, restrictedCount);

        // الفهرس: نضيف/نحدّث المسموح، ونشيل المقيّد لو كان اتفهرس قبل كده
        var index = meili.Index(MeiliSetup.IndexName);
        if (restrictedIds.Count > 0) await index.DeleteDocumentsAsync(restrictedIds, ct);
        await index.AddDocumentsInBatchesAsync(docs, 5000, "id", ct);

        return new ImportResult(added, updated, skipped, restrictedCount, docs.Count);
    }
}

public record ImportResult(int Added, int Updated, int Skipped, int Restricted, int Indexed);
