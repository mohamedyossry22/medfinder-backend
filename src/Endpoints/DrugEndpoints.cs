using System.Security.Claims;
using System.Text.RegularExpressions;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Search;
using MedFinder.Services;
using Meilisearch;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class DrugEndpoints
{
    private const int ConfirmThreshold = 3;   // مجموع أوزان الأصوات المطلوب لاعتماد باركود

    public static void MapDrugs(this WebApplication app)
    {
        var g = app.MapGroup("/api/drugs").WithTags("Drugs").RequireAuthorization();

        g.MapGet("/search", async (string q, MeilisearchClient meili, int limit = 15) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(Array.Empty<DrugDoc>());
            var res = await meili.Index(MeiliSetup.IndexName).SearchAsync<DrugDoc>(
                ArabicNormalizer.Normalize(q), new SearchQuery { Limit = Math.Clamp(limit, 1, 30) });
            return Results.Ok(res.Hits);
        });

        // مسح روشتة/علبة بالذكاء الاصطناعي: الصورة ← أسماء مقترحة ← مطابقة مع قاعدة الأدوية (المستخدم بيأكد)
        g.MapPost("/scan", async (IFormFile image, IVisionExtractor vision, MeilisearchClient meili, CancellationToken ct) =>
        {
            if (image == null || image.Length == 0 || image.Length > 4 * 1024 * 1024)
                return Results.BadRequest(new { error = "حجم الصورة غير مقبول (الحد الأقصى 4 ميجا)" });

            byte[] bytes;
            using (var ms = new MemoryStream()) { await image.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
            var mime = ImageSniffer.Detect(bytes);
            if (mime == null) return Results.BadRequest(new { error = "الملف ليس صورة صالحة" });

            List<ExtractedDrug> extracted;
            try { extracted = await vision.ExtractAsync(bytes, mime, ct); }
            catch (VisionUnavailableException) { return Results.Json(new { error = "vision_unavailable" }, statusCode: 503); }
            catch (VisionFailedException) { return Results.Json(new { error = "vision_failed" }, statusCode: 502); }

            var index = meili.Index(MeiliSetup.IndexName);
            var items = new List<object>();
            foreach (var e in extracted)
            {
                var res = await index.SearchAsync<DrugDoc>(ArabicNormalizer.Normalize(e.Name), new SearchQuery { Limit = 4 }, cancellationToken: ct);
                var hits = res.Hits.ToList();
                var digits = new string((e.Strength ?? "").Where(char.IsDigit).ToArray());
                if (digits.Length > 0) hits = hits.OrderByDescending(h => h.NameEn.Contains(digits)).ToList();   // نفضّل نفس التركيز
                items.Add(new { text = e.Name, strength = e.Strength, confidence = e.Confidence, matches = hits });
            }
            return Results.Ok(new { items });
        }).DisableAntiforgery().RequireRateLimiting("scan");

        g.MapGet("/barcode/{code}", async (string code, AppDbContext db) =>
        {
            var d = await db.DrugBarcodes
                .Where(b => b.Code == code.Trim() && b.Status == BarcodeStatus.Confirmed && b.Drug.IsSearchable)
                .Select(b => b.Drug).FirstOrDefaultAsync();
            return d == null ? Results.NotFound(new { found = false }) : Results.Ok(DrugDoc.From(d));
        });

        // ربط باركود بدواء (Crowdsourcing): المستخدم وزنه 1، الصيدلي 2، الأدمن بيعتمد فورًا
        g.MapPost("/barcode", async (BarcodeLinkDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            var code = dto.Code?.Trim() ?? "";
            if (!Regex.IsMatch(code, @"^\d{8,14}$")) return Results.BadRequest(new { error = "invalid_barcode" });
            if (!await db.Drugs.AnyAsync(d => d.Id == dto.DrugId && d.IsSearchable))
                return Results.BadRequest(new { error = "drug_not_found" });

            var uid = me.UserId();
            var isAdmin = me.IsInRole(Roles.Admin);
            var isPharmacy = me.IsInRole(Roles.Pharmacy);
            var weight = isAdmin ? ConfirmThreshold : isPharmacy ? 2 : 1;

            var bc = await db.DrugBarcodes.Include(b => b.Votes)
                .FirstOrDefaultAsync(b => b.Code == code && b.DrugId == dto.DrugId);
            if (bc == null)
            {
                bc = new DrugBarcode
                {
                    Code = code, DrugId = dto.DrugId,
                    Source = isAdmin ? BarcodeSource.Admin : isPharmacy ? BarcodeSource.Pharmacist : BarcodeSource.User
                };
                db.DrugBarcodes.Add(bc);
            }
            if (bc.Votes.Any(v => v.VoterId == uid)) return Results.Ok(new { status = bc.Status, note = "already_voted" });
            if (bc.Status == BarcodeStatus.Rejected) return Results.Ok(new { status = bc.Status });

            bc.Votes.Add(new BarcodeVote { VoterId = uid, Weight = weight });
            bc.ConfirmCount = bc.Votes.Sum(v => v.Weight);

            // لو الباركود ده معتمد لدواء تاني، مانعتمدهوش أوتوماتيك (الأدمن يحسم)
            var conflict = await db.DrugBarcodes.AnyAsync(b => b.Code == code && b.DrugId != dto.DrugId && b.Status == BarcodeStatus.Confirmed);
            if (bc.Status == BarcodeStatus.Pending && bc.ConfirmCount >= ConfirmThreshold && (!conflict || isAdmin))
                bc.Status = BarcodeStatus.Confirmed;

            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Ok(new { status = bc.Status, note = "already_voted" }); }
            return Results.Ok(new { status = bc.Status });
        });
    }
}
