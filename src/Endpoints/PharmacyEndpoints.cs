using System.Security.Claims;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class PharmacyEndpoints
{
    private static Task<Pharmacy?> ApprovedAsync(ClaimsPrincipal me, AppDbContext db)
    {
        var uid = me.UserId();
        return db.Pharmacies.FirstOrDefaultAsync(p => p.OwnerUserId == uid && p.Status == PharmacyStatus.Approved);
    }
    private static IResult NotApproved() => Results.Json(new { error = "pharmacy_not_approved" }, statusCode: 403);
    private static IResult Err(string m, int code = 400) => Results.Json(new { error = m }, statusCode: code);

    private static object ProfileOf(Pharmacy p, string? email) => new
    {
        p.Name, p.PharmacistName, p.LicenseNumber, p.Governorate, p.Phone, p.Address, p.MapsUrl, p.Lat, p.Lng,
        p.Status, Email = email
    };

    public static void MapPharmacy(this WebApplication app)
    {
        var g = app.MapGroup("/api/pharmacy").WithTags("Pharmacy")
            .RequireAuthorization(p => p.RequireRole(Roles.Pharmacy));

        // حالة الحساب (متاحة حتى لو لسه Pending عشان الصفحة تعرض "جاري المراجعة")
        g.MapGet("/me", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId();
            var p = await db.Pharmacies.AsNoTracking().Where(x => x.OwnerUserId == uid)
                .Select(x => new { x.Id, x.Name, x.PharmacistName, x.Governorate, x.Status, Reason = x.StatusReason }).FirstOrDefaultAsync();
            return p == null ? Results.NotFound() : Results.Ok(p);
        });

        // ───────── بيانات الصيدلية (عرض + تعديل) ─────────
        g.MapGet("/profile", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId();
            var p = await db.Pharmacies.AsNoTracking().Include(x => x.Owner).FirstOrDefaultAsync(x => x.OwnerUserId == uid);
            return p == null ? Results.NotFound() : Results.Ok(ProfileOf(p, p.Owner.Email));
        });

        // رقم الرخصة وصورتها والإيميل مش بيتعدلوا من هنا (محتاجين مراجعة الأدمن)
        g.MapPut("/profile", async (UpdatePharmacyProfileDto dto, ClaimsPrincipal me, AppDbContext db, MapsLinkParser maps) =>
        {
            var uid = me.UserId();
            var p = await db.Pharmacies.Include(x => x.Owner).FirstOrDefaultAsync(x => x.OwnerUserId == uid &&
                (x.Status == PharmacyStatus.Approved || x.Status == PharmacyStatus.Pending));
            if (p == null) return NotApproved();

            var changed = new List<string>();

            if (dto.PharmacyName != null)
            {
                var v = dto.PharmacyName.Trim();
                if (v.Length is < 2 or > 80) return Err("اسم الصيدلية غير صالح");
                if (v != p.Name) { p.Name = v; changed.Add("name"); }
            }
            if (dto.PharmacistName != null)
            {
                var v = dto.PharmacistName.Trim();
                if (v.Length is < 2 or > 80) return Err("اسم الصيدلي غير صالح");
                if (v != p.PharmacistName) { p.PharmacistName = v; p.Owner.FullName = v; changed.Add("pharmacist"); }
            }
            if (dto.Phone != null)
            {
                var v = Ext.LoosePhone(dto.Phone);
                if (v == null) return Err("رقم الهاتف غير صالح");
                if (v != p.Phone) { p.Phone = v; changed.Add("phone"); }
            }
            if (dto.Address != null)
            {
                var v = dto.Address.Trim();
                if (v.Length > 200) return Err("العنوان طويل جدًا");
                if (v != (p.Address ?? "")) { p.Address = v.Length == 0 ? null : v; changed.Add("address"); }
            }
            if (dto.Governorate != null)
            {
                var v = dto.Governorate.Trim();
                if (!Governorates.All.Contains(v)) return Err("المحافظة غير صالحة");
                if (v != p.Governorate) { p.Governorate = v; changed.Add("governorate"); }
            }

            // الموقع: إحداثيات مباشرة (الأدق) أو رابط جوجل ماب جديد
            if (dto.Lat is double la && dto.Lng is double ln)
            {
                if (!Geo.InEgypt(la, ln)) return Err("الموقع خارج مصر أو غير صحيح");
                if (Math.Abs(la - p.Lat) > 1e-6 || Math.Abs(ln - p.Lng) > 1e-6) { p.Lat = la; p.Lng = ln; changed.Add("location"); }
            }
            if (dto.MapsUrl != null && dto.MapsUrl.Trim() != p.MapsUrl)
            {
                var url = dto.MapsUrl.Trim();
                if (url.Length > 0)
                {
                    if (dto.Lat == null)
                    {
                        var parsed = await maps.ParseAsync(url);
                        if (parsed == null) return Err("تعذر استخراج الموقع من الرابط، استخدم زرار موقعي الحالي");
                        p.Lat = parsed.Value.Lat; p.Lng = parsed.Value.Lng; changed.Add("location");
                    }
                    p.MapsUrl = url; changed.Add("mapsUrl");
                }
            }

            if (changed.Count > 0)
                db.AuditLogs.Add(new AuditLog { ActorId = uid, Action = "pharmacy.update_profile", EntityType = "Pharmacy", EntityId = p.Id, Details = string.Join(",", changed) });
            await db.SaveChangesAsync();
            return Results.Ok(ProfileOf(p, p.Owner.Email));
        });

        // ───────── الطلبات ─────────
        g.MapGet("/requests/incoming", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var now = DateTime.UtcNow;
            var rows = await db.RequestTargets.AsNoTracking()
                .Where(t => t.PharmacyId == ph.Id && t.Status == TargetStatus.Pending && t.Request.ExpiresAt > now && t.Request.ClosedAt == null)
                .OrderByDescending(t => t.Request.CreatedAt)
                .Select(t => new IncomingRequestDto(t.Id, t.RequestId, t.Request.Drug.NameEn, t.Request.Drug.NameAr,
                    t.Request.Drug.ActiveIngredient, t.Request.Quantity, t.DistanceKm, t.Request.CreatedAt, t.Request.ExpiresAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        g.MapGet("/requests/{targetId:guid}", async (Guid targetId, ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var t = await db.RequestTargets.AsNoTracking()
                .Where(x => x.Id == targetId && x.PharmacyId == ph.Id)
                .Select(x => new
                {
                    x.Id, x.RequestId, DrugNameEn = x.Request.Drug.NameEn, DrugNameAr = x.Request.Drug.NameAr,
                    x.Request.Drug.ActiveIngredient, x.Request.Drug.Manufacturer, x.Request.Drug.Route,
                    x.Request.Quantity, x.DistanceKm, x.Status, x.AvailableQty, x.Request.CreatedAt, x.Request.ExpiresAt
                }).FirstOrDefaultAsync();
            return t == null ? Results.NotFound() : Results.Ok(t);
        });

        // الرد: متوفر / غير متوفر / متوفر جزئي (بكمية)
        g.MapPost("/requests/{targetId:guid}/respond", async (Guid targetId, RespondDto dto, ClaimsPrincipal me,
            AppDbContext db, Notifier notifier) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            if (dto.Status is not (TargetStatus.Available or TargetStatus.Partial or TargetStatus.Unavailable))
                return Results.BadRequest(new { error = "invalid_status" });

            var t = await db.RequestTargets.Include(x => x.Request)
                .FirstOrDefaultAsync(x => x.Id == targetId && x.PharmacyId == ph.Id);
            if (t == null) return Results.NotFound();
            if (t.Request.ClosedAt != null) return Results.Json(new { error = "closed" }, statusCode: 410);
            if (t.Request.ExpiresAt <= DateTime.UtcNow) return Results.Json(new { error = "expired" }, statusCode: 410);
            if (t.Status is TargetStatus.Delivered or TargetStatus.Expired or TargetStatus.Cancelled)
                return Results.Json(new { error = "locked" }, statusCode: 409);

            int? qty = null;
            if (dto.Status == TargetStatus.Available) qty = t.Request.Quantity;
            if (dto.Status == TargetStatus.Partial)
            {
                if (dto.AvailableQty is not int q || q < 1 || q >= t.Request.Quantity)
                    return Results.BadRequest(new { error = "invalid_quantity" });
                qty = q;
            }

            t.Status = dto.Status; t.AvailableQty = qty; t.RespondedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await notifier.PharmacyRespondedAsync(t.Request.UserId, t.RequestId, ph.Name, t.Status);
            return Results.Ok(new { status = t.Status });
        });

        // صفحة "الجاهزة للتسليم"
        g.MapGet("/ready", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var now = DateTime.UtcNow;
            var rows = await db.RequestTargets.AsNoTracking()
                .Where(t => t.PharmacyId == ph.Id && (t.Status == TargetStatus.Available || t.Status == TargetStatus.Partial)
                            && t.Request.ExpiresAt > now && t.Request.ClosedAt == null)
                .OrderByDescending(t => t.RespondedAt)
                .Select(t => new ReadyItemDto(t.Id, t.Request.Drug.NameEn, t.Request.Drug.NameAr, t.Request.Quantity,
                    t.AvailableQty ?? 0, t.Status, t.RespondedAt ?? t.Request.CreatedAt, t.Request.ExpiresAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        // الصيدلية بتسجّل التسليم، والمستخدم بيوصله طلب تأكيد الاستلام
        g.MapPost("/ready/{targetId:guid}/deliver", async (Guid targetId, ClaimsPrincipal me, AppDbContext db, Notifier notifier) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var t = await db.RequestTargets.Include(x => x.Request).FirstOrDefaultAsync(x => x.Id == targetId && x.PharmacyId == ph.Id
                        && (x.Status == TargetStatus.Available || x.Status == TargetStatus.Partial));
            if (t == null) return Results.NotFound();
            if (t.Request.ClosedAt != null) return Results.Json(new { error = "closed" }, statusCode: 410);
            t.Status = TargetStatus.Delivered; t.DeliveredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            try { await notifier.DeliveredAsync(t.Request.UserId, t.RequestId, ph.Name); } catch { /* ما نوقفش التسليم بسبب إشعار */ }
            return Results.Ok(new { status = t.Status });
        });

        // ───────── سجل الطلبات المسلّمة (من أول يوم) مع فلتر بالتاريخ ─────────
        g.MapGet("/history", async (ClaimsPrincipal me, AppDbContext db, DateOnly? from, DateOnly? to, int page = 1, int pageSize = 20) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);

            var q = db.RequestTargets.AsNoTracking()
                .Where(t => t.PharmacyId == ph.Id && t.Status == TargetStatus.Delivered && t.DeliveredAt != null);
            if (from != null) { var f = Ext.CairoStartUtc(from.Value); q = q.Where(t => t.DeliveredAt >= f); }
            if (to != null) { var e = Ext.CairoStartUtc(to.Value.AddDays(1)); q = q.Where(t => t.DeliveredAt < e); }

            var total = await q.CountAsync();
            var totalQty = await q.SumAsync(t => (int?)(t.AvailableQty ?? t.Request.Quantity)) ?? 0;
            var items = await q.OrderByDescending(t => t.DeliveredAt).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(t => new HistoryItemDto(t.Id, t.Request.Drug.NameEn, t.Request.Drug.NameAr,
                    t.AvailableQty ?? t.Request.Quantity, t.DeliveredAt!.Value, t.Request.DeliveredTargetId == t.Id))
                .ToListAsync();
            return Results.Ok(new { total, totalQuantity = totalQty, page, pageSize, items });
        });

        // ───────── الإعلانات (تحتاج موافقة الأدمن) ─────────
        g.MapGet("/ads", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            return Results.Ok(await db.Ads.AsNoTracking().Where(a => a.PharmacyId == ph.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.Title, a.ImagePath, a.LinkUrl, a.StartDate, a.EndDate, a.Status, a.RejectionReason }).ToListAsync());
        });

        g.MapPost("/ads", async ([FromForm] AdForm f, ClaimsPrincipal me, AppDbContext db, IFileStorage files) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var title = f.Title?.Trim() ?? "";
            if (title.Length is < 3 or > 80) return Results.BadRequest(new { error = "invalid_title" });
            if (f.Image == null) return Results.BadRequest(new { error = "image_required" });
            var start = Ext.Utc(f.StartDate); var end = Ext.Utc(f.EndDate);
            if (end <= start || end <= DateTime.UtcNow || (end - start).TotalDays > 90)
                return Results.BadRequest(new { error = "invalid_dates" });
            if (!string.IsNullOrWhiteSpace(f.LinkUrl) &&
                !(Uri.TryCreate(f.LinkUrl, UriKind.Absolute, out var u) && u.Scheme == "https"))
                return Results.BadRequest(new { error = "invalid_link" });

            string path;
            try { path = await files.SaveImageAsync(f.Image, "ads", isPrivate: false); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }

            var ad = new Ad { PharmacyId = ph.Id, Title = title, ImagePath = path, LinkUrl = f.LinkUrl?.Trim(), StartDate = start, EndDate = end };
            db.Ads.Add(ad);
            await db.SaveChangesAsync();
            return Results.Created($"/api/pharmacy/ads/{ad.Id}", new { ad.Id, status = ad.Status });
        }).DisableAntiforgery();
    }

    /// إعلانات الصفحة الرئيسية للموبايل + تسجيل أجهزة الإشعارات
    public static void MapSharedEndpoints(this WebApplication app)
    {
        app.MapGet("/api/ads", async (AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var rows = await db.Ads.AsNoTracking()
                .Where(a => a.Status == AdStatus.Approved && a.StartDate <= now && a.EndDate > now && a.Pharmacy.Status == PharmacyStatus.Approved)
                .Select(a => new { a.Id, a.Title, a.ImagePath, a.LinkUrl, PharmacyName = a.Pharmacy.Name })
                .ToListAsync();
            return rows.OrderBy(_ => Random.Shared.Next()).Take(10);
        }).RequireAuthorization().WithTags("Ads");

        var d = app.MapGroup("/api/devices").WithTags("Devices").RequireAuthorization();

        d.MapPost("/", async (DeviceDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Token) || dto.Token.Length > 512 || dto.Platform is not ("android" or "ios" or "web"))
                return Results.BadRequest(new { error = "invalid_device" });
            var uid = me.UserId();
            var existing = await db.DeviceTokens.FirstOrDefaultAsync(t => t.Token == dto.Token);
            if (existing == null) db.DeviceTokens.Add(new DeviceToken { UserId = uid, Token = dto.Token, Platform = dto.Platform });
            else { existing.UserId = uid; existing.Platform = dto.Platform; existing.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        d.MapPost("/unregister", async (DeviceDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId();
            await db.DeviceTokens.Where(t => t.Token == dto.Token && t.UserId == uid).ExecuteDeleteAsync();
            return Results.Ok();
        });
    }
}
