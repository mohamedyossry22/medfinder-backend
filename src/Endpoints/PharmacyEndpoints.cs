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

        // الطلبات الواردة (اللي لسه ماتردّش عليها)
        g.MapGet("/requests/incoming", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var now = DateTime.UtcNow;
            var rows = await db.RequestTargets.AsNoTracking()
                .Where(t => t.PharmacyId == ph.Id && t.Status == TargetStatus.Pending && t.Request.ExpiresAt > now)
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
            if (t.Request.ExpiresAt <= DateTime.UtcNow) return Results.Json(new { error = "expired" }, statusCode: 410);
            if (t.Status is TargetStatus.Delivered or TargetStatus.Expired) return Results.Json(new { error = "locked" }, statusCode: 409);

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
                            && t.Request.ExpiresAt > now)
                .OrderByDescending(t => t.RespondedAt)
                .Select(t => new ReadyItemDto(t.Id, t.Request.Drug.NameEn, t.Request.Drug.NameAr, t.Request.Quantity,
                    t.AvailableQty ?? 0, t.Status, t.RespondedAt ?? t.Request.CreatedAt, t.Request.ExpiresAt))
                .ToListAsync();
            return Results.Ok(rows);
        });

        g.MapPost("/ready/{targetId:guid}/deliver", async (Guid targetId, ClaimsPrincipal me, AppDbContext db) =>
        {
            var ph = await ApprovedAsync(me, db); if (ph == null) return NotApproved();
            var t = await db.RequestTargets.FirstOrDefaultAsync(x => x.Id == targetId && x.PharmacyId == ph.Id
                        && (x.Status == TargetStatus.Available || x.Status == TargetStatus.Partial));
            if (t == null) return Results.NotFound();
            t.Status = TargetStatus.Delivered; t.DeliveredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { status = t.Status });
        });

        // ── الإعلانات (تحتاج موافقة الأدمن) ──
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
