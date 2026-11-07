using System.Security.Claims;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Services;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class AdminEndpoints
{
    private static void Audit(AppDbContext db, Guid actor, string action, string type, Guid id, string? details = null)
        => db.AuditLogs.Add(new AuditLog { ActorId = actor, Action = action, EntityType = type, EntityId = id, Details = details });

    private static async Task TrySms(ISmsSender sms, string phone, string msg)
    { try { await sms.SendAsync(phone, msg); } catch { /* ما نوقفش القرار بسبب فشل الرسالة */ } }

    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // ───────── طلبات انضمام الصيدليات ─────────
        g.MapGet("/pharmacies", async (AppDbContext db, PharmacyStatus? status, string? q, int page = 1, int pageSize = 20) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Pharmacies.AsNoTracking().AsQueryable();
            if (status != null) query = query.Where(p => p.Status == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = $"%{q.Trim()}%";
                query = query.Where(p => EF.Functions.ILike(p.Name, s) || EF.Functions.ILike(p.LicenseNumber, s));
            }
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new { p.Id, p.Name, p.PharmacistName, p.LicenseNumber, p.Governorate, p.Status, p.CreatedAt })
                .ToListAsync();
            return Results.Ok(new { total, page, pageSize, items });
        });

        g.MapGet("/pharmacies/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Pharmacies.AsNoTracking().Include(x => x.Owner).FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return Results.NotFound();
            return Results.Ok(new
            {
                p.Id, p.Name, p.PharmacistName, p.LicenseNumber, p.Governorate, p.MapsUrl, p.Lat, p.Lng, p.Phone, p.Address,
                p.Status, Reason = p.StatusReason, p.ReviewedBy, p.ReviewedAt, p.CreatedAt, Email = p.Owner.Email,
                LicenseImageUrl = $"/api/admin/pharmacies/{p.Id}/license"
            });
        });

        g.MapGet("/pharmacies/{id:guid}/license", async (Guid id, AppDbContext db, IFileStorage files) =>
        {
            var path = await db.Pharmacies.AsNoTracking().Where(p => p.Id == id).Select(p => p.LicenseImagePath).FirstOrDefaultAsync();
            var f = path == null ? null : files.OpenPrivate(path);
            return f == null ? Results.NotFound() : Results.File(f.Value.Stream, f.Value.ContentType);
        });

        g.MapPost("/pharmacies/{id:guid}/approve", async (Guid id, ClaimsPrincipal me, AppDbContext db, ISmsSender sms) =>
        {
            var p = await db.Pharmacies.FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return Results.NotFound();
            if (p.Status is not (PharmacyStatus.Pending or PharmacyStatus.Rejected)) return Results.Conflict(new { error = "invalid_transition" });
            p.Status = PharmacyStatus.Approved; p.StatusReason = null; p.ReviewedBy = me.UserId(); p.ReviewedAt = DateTime.UtcNow;
            Audit(db, me.UserId(), "pharmacy.approve", "Pharmacy", p.Id, p.LicenseNumber);
            await db.SaveChangesAsync();
            await TrySms(sms, p.Phone, "تم اعتماد صيدليتك في MedFinder. يمكنك الآن تسجيل الدخول واستقبال الطلبات.");
            return Results.Ok(new { status = p.Status });
        });

        g.MapPost("/pharmacies/{id:guid}/reject", async (Guid id, ReasonDto dto, ClaimsPrincipal me, AppDbContext db, ISmsSender sms) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Reason)) return Results.BadRequest(new { error = "reason_required" });
            var p = await db.Pharmacies.FirstOrDefaultAsync(x => x.Id == id);
            if (p == null) return Results.NotFound();
            if (p.Status != PharmacyStatus.Pending) return Results.Conflict(new { error = "invalid_transition" });
            p.Status = PharmacyStatus.Rejected; p.StatusReason = dto.Reason.Trim(); p.ReviewedBy = me.UserId(); p.ReviewedAt = DateTime.UtcNow;
            Audit(db, me.UserId(), "pharmacy.reject", "Pharmacy", p.Id, dto.Reason);
            await db.SaveChangesAsync();
            await TrySms(sms, p.Phone, $"نعتذر، تم رفض طلب انضمام صيدليتك. السبب: {p.StatusReason}");
            return Results.Ok(new { status = p.Status });
        });

        g.MapPost("/pharmacies/{id:guid}/suspend", async (Guid id, ReasonDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            var p = await db.Pharmacies.FirstOrDefaultAsync(x => x.Id == id && x.Status == PharmacyStatus.Approved);
            if (p == null) return Results.NotFound();
            p.Status = PharmacyStatus.Suspended; p.StatusReason = dto.Reason?.Trim();
            Audit(db, me.UserId(), "pharmacy.suspend", "Pharmacy", p.Id, dto.Reason);
            await db.SaveChangesAsync();
            return Results.Ok(new { status = p.Status });
        });

        g.MapPost("/pharmacies/{id:guid}/activate", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var p = await db.Pharmacies.FirstOrDefaultAsync(x => x.Id == id && x.Status == PharmacyStatus.Suspended);
            if (p == null) return Results.NotFound();
            p.Status = PharmacyStatus.Approved; p.StatusReason = null;
            Audit(db, me.UserId(), "pharmacy.activate", "Pharmacy", p.Id);
            await db.SaveChangesAsync();
            return Results.Ok(new { status = p.Status });
        });

        // ───────── المستخدمين ─────────
        g.MapGet("/users", async (AppDbContext db, string? q, int page = 1, int pageSize = 20) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var users = from u in db.Users.AsNoTracking()
                        join ur in db.UserRoles on u.Id equals ur.UserId
                        join r in db.Roles on ur.RoleId equals r.Id
                        where r.Name == Roles.User
                        select u;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = $"%{q.Trim()}%";
                users = users.Where(u => EF.Functions.ILike(u.FullName, s) || EF.Functions.ILike(u.PhoneNumber!, s));
            }
            var total = await users.CountAsync();
            var items = await users.OrderByDescending(u => u.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(u => new { u.Id, u.FullName, u.PhoneNumber, u.Age, u.IsBanned, u.BanReason, u.CreatedAt }).ToListAsync();
            return Results.Ok(new { total, page, pageSize, items });
        });

        g.MapPost("/users/{id:guid}/ban", async (Guid id, ReasonDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u == null) return Results.NotFound();
            u.IsBanned = true; u.BanReason = dto.Reason?.Trim();
            Audit(db, me.UserId(), "user.ban", "User", u.Id, dto.Reason);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/users/{id:guid}/unban", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u == null) return Results.NotFound();
            u.IsBanned = false; u.BanReason = null;
            Audit(db, me.UserId(), "user.unban", "User", u.Id);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ───────── الإعلانات ─────────
        g.MapGet("/ads", async (AppDbContext db, AdStatus? status) =>
        {
            var q = db.Ads.AsNoTracking().AsQueryable();
            if (status != null) q = q.Where(a => a.Status == status);
            return Results.Ok(await q.OrderByDescending(a => a.CreatedAt).Take(100)
                .Select(a => new { a.Id, a.Title, a.ImagePath, a.LinkUrl, a.StartDate, a.EndDate, a.Status, PharmacyName = a.Pharmacy.Name }).ToListAsync());
        });

        g.MapPost("/ads/{id:guid}/approve", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var a = await db.Ads.FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return Results.NotFound();
            a.Status = AdStatus.Approved; a.RejectionReason = null;
            Audit(db, me.UserId(), "ad.approve", "Ad", a.Id);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/ads/{id:guid}/reject", async (Guid id, ReasonDto dto, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Reason)) return Results.BadRequest(new { error = "reason_required" });
            var a = await db.Ads.FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return Results.NotFound();
            a.Status = AdStatus.Rejected; a.RejectionReason = dto.Reason.Trim();
            Audit(db, me.UserId(), "ad.reject", "Ad", a.Id, dto.Reason);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ───────── الباركودات ─────────
        g.MapGet("/barcodes/pending", async (AppDbContext db) =>
            Results.Ok(await db.DrugBarcodes.AsNoTracking().Where(b => b.Status == BarcodeStatus.Pending)
                .OrderByDescending(b => b.ConfirmCount).Take(100)
                .Select(b => new { b.Id, b.Code, b.ConfirmCount, b.Source, DrugId = b.DrugId, DrugNameEn = b.Drug.NameEn, DrugNameAr = b.Drug.NameAr }).ToListAsync()));

        g.MapPost("/barcodes/{id:int}/confirm", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var b = await db.DrugBarcodes.FirstOrDefaultAsync(x => x.Id == id);
            if (b == null) return Results.NotFound();
            b.Status = BarcodeStatus.Confirmed;
            Audit(db, me.UserId(), "barcode.confirm", "Barcode", Guid.Empty, $"{b.Code}->{b.DrugId}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/barcodes/{id:int}/reject", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var b = await db.DrugBarcodes.FirstOrDefaultAsync(x => x.Id == id);
            if (b == null) return Results.NotFound();
            b.Status = BarcodeStatus.Rejected;
            Audit(db, me.UserId(), "barcode.reject", "Barcode", Guid.Empty, $"{b.Code}->{b.DrugId}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ───────── استيراد الأدوية ─────────
        g.MapPost("/drugs/import", async (DrugImporter importer, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var dir = Path.Combine(env.ContentRootPath, "data");
            return Results.Ok(await importer.ImportAsync(
                Path.Combine(dir, "egyptian-drugs.json"), Path.Combine(dir, "restricted-ingredients.txt"), ct));
        });

        // ───────── الإحصائيات ─────────
        g.MapGet("/stats", async (AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
            var startToday = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date, tz);
            var week = now.AddDays(-7); var d30 = now.AddDays(-30);

            var usersCount = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
                                    where r.Name == Roles.User select ur.UserId).CountAsync();
            var pharmacies = await db.Pharmacies.GroupBy(p => p.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToListAsync();

            var reqToday = await db.DrugRequests.CountAsync(r => r.CreatedAt >= startToday);
            var reqWeek = await db.DrugRequests.CountAsync(r => r.CreatedAt >= week);
            var reqMonth = await db.DrugRequests.CountAsync(r => r.CreatedAt >= d30);

            var answered = await db.DrugRequests.CountAsync(r => r.CreatedAt >= d30 &&
                r.Targets.Any(t => t.Status == TargetStatus.Available || t.Status == TargetStatus.Partial || t.Status == TargetStatus.Delivered));

            var resp = await db.RequestTargets.AsNoTracking()
                .Where(t => t.RespondedAt != null && t.Request.CreatedAt >= d30)
                .Select(t => new { R = t.RespondedAt!.Value, C = t.Request.CreatedAt }).Take(20000).ToListAsync();
            var avgResp = resp.Count == 0 ? (double?)null : resp.Average(x => (x.R - x.C).TotalSeconds);

            var top = await db.DrugRequests.Where(r => r.CreatedAt >= d30).GroupBy(r => r.DrugId)
                .Select(x => new { DrugId = x.Key, Count = x.Count() }).OrderByDescending(x => x.Count).Take(10).ToListAsync();

            // طلب خلص وقته ومفيش حد قال متوفر = نقص حقيقي
            var unanswered = await db.DrugRequests
                .Where(r => r.CreatedAt >= d30 && r.ExpiresAt <= now &&
                    !r.Targets.Any(t => t.Status == TargetStatus.Available || t.Status == TargetStatus.Partial || t.Status == TargetStatus.Delivered))
                .GroupBy(r => r.DrugId).Select(x => new { DrugId = x.Key, Count = x.Count() })
                .OrderByDescending(x => x.Count).Take(10).ToListAsync();

            var byGov = await db.RequestTargets.Where(t => t.Request.CreatedAt >= d30)
                .GroupBy(t => t.Pharmacy.Governorate)
                .Select(x => new { Governorate = x.Key, Requests = x.Select(t => t.RequestId).Distinct().Count() })
                .OrderByDescending(x => x.Requests).ToListAsync();

            var ids = top.Select(x => x.DrugId).Concat(unanswered.Select(x => x.DrugId)).Distinct().ToList();
            var names = await db.Drugs.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new { d.NameEn, d.NameAr });
            object Name(Guid id) => names.TryGetValue(id, out var n) ? n : new { NameEn = "?", NameAr = "?" };

            return Results.Ok(new
            {
                users = usersCount,
                pharmacies = pharmacies.ToDictionary(x => x.Status.ToString(), x => x.Count),
                requests = new { today = reqToday, week = reqWeek, month = reqMonth },
                availabilityRate30d = reqMonth == 0 ? 0 : Math.Round(100.0 * answered / reqMonth, 1),
                avgResponseSeconds30d = avgResp == null ? (double?)null : Math.Round(avgResp.Value),
                topRequested = top.Select(x => new { Drug = Name(x.DrugId), x.Count }),
                unansweredDemand = unanswered.Select(x => new { Drug = Name(x.DrugId), x.Count }),
                byGovernorate = byGov
            });
        });
    }
}
