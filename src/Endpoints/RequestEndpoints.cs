using System.Security.Claims;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Services;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class RequestEndpoints
{
    public static void MapRequests(this WebApplication app)
    {
        var g = app.MapGroup("/api/requests").WithTags("Requests (User)")
            .RequireAuthorization(p => p.RequireRole(Roles.User));

        g.MapPost("/", async (CreateRequestDto dto, ClaimsPrincipal me, RequestService svc, AppDbContext db) =>
        {
            var uid = me.UserId();
            if (await db.Users.AnyAsync(u => u.Id == uid && u.IsBanned))
                return Results.Json(new { error = "banned" }, statusCode: 403);

            var r = await svc.CreateAsync(uid, dto);
            if (r.Error == "rate_limited") return Results.Json(new { error = r.Error }, statusCode: 429);
            if (r.Error != null) return Results.BadRequest(new { error = r.Error });
            if (r.RequestId == null)
                return Results.Ok(new { requestId = (Guid?)null, pharmaciesCount = 0, message = "مفيش صيدليات معتمدة في النطاق ده، جرّب توسّع النطاق" });
            return Results.Ok(new { requestId = r.RequestId, pharmaciesCount = r.PharmaciesCount });
        });

        g.MapPost("/{id:guid}/expand", async (Guid id, ExpandRequestDto dto, ClaimsPrincipal me, RequestService svc) =>
        {
            var (err, added) = await svc.ExpandAsync(me.UserId(), id, dto.RadiusKm);
            return err == null ? Results.Ok(new { added })
                 : err == "not_found" ? Results.NotFound() : Results.BadRequest(new { error = err });
        });

        g.MapGet("/mine", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId(); var now = DateTime.UtcNow;
            var rows = await db.DrugRequests.AsNoTracking().Where(r => r.UserId == uid)
                .OrderByDescending(r => r.CreatedAt).Take(30)
                .Select(r => new
                {
                    r.Id, r.Drug.NameEn, r.Drug.NameAr, r.Quantity, r.CreatedAt, r.ExpiresAt,
                    Total = r.Targets.Count(),
                    Avail = r.Targets.Count(t => t.Status == TargetStatus.Available || t.Status == TargetStatus.Partial)
                }).ToListAsync();
            return rows.Select(x => new RequestSummaryDto(x.Id, x.NameEn, x.NameAr, x.Quantity, x.CreatedAt,
                x.ExpiresAt, x.ExpiresAt <= now, x.Total, x.Avail));
        });

        g.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId(); var now = DateTime.UtcNow;
            var r = await db.DrugRequests.AsNoTracking().Include(x => x.Drug)
                .Include(x => x.Targets).ThenInclude(t => t.Pharmacy)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
            if (r == null) return Results.NotFound();

            var expired = r.ExpiresAt <= now;
            var list = r.Targets.OrderBy(t => t.DistanceKm).Select(t =>
            {
                // بيانات التواصل تظهر بس للصيدلية اللي قالت متوفر، وطول ما الطلب شغال
                var show = !expired && t.Status is TargetStatus.Available or TargetStatus.Partial;
                var contact = show ? new ContactDto(t.Pharmacy.Phone, t.Pharmacy.Address, t.Pharmacy.Lat, t.Pharmacy.Lng, t.Pharmacy.MapsUrl) : null;
                return new PharmacyReplyDto(t.PharmacyId, t.Pharmacy.Name, t.DistanceKm, t.Status, t.AvailableQty, t.RespondedAt, contact);
            }).ToList();

            return Results.Ok(new RequestDetailsDto(r.Id, r.DrugId, r.Drug.NameEn, r.Drug.NameAr, r.Quantity,
                r.RadiusKm, r.CreatedAt, r.ExpiresAt, expired, list));
        });
    }
}
