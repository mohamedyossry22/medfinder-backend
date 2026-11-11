using System.Security.Claims;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Services;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class ComplaintEndpoints
{
    /// نفس الشكل للقايمة في لوحة الأدمن وللحدث اللايف (NewComplaint)
    public static IQueryable<ComplaintRowDto> ToRows(this IQueryable<Complaint> q, AppDbContext db) =>
        q.Select(c => new ComplaintRowDto(
            c.Id, c.SenderRole, c.User.FullName, c.User.PhoneNumber, c.User.Email,
            db.Pharmacies.Where(p => p.OwnerUserId == c.UserId).Select(p => p.Name).FirstOrDefault(),
            c.Category, c.Subject, c.Message, c.Status, c.AdminReply, c.CreatedAt, c.ReviewedAt));

    public static void MapComplaints(this WebApplication app)
    {
        // المستخدم العادي وصاحب الصيدلية
        var g = app.MapGroup("/api/complaints").WithTags("Complaints")
            .RequireAuthorization(p => p.RequireRole(Roles.User, Roles.Pharmacy));

        g.MapPost("", async (CreateComplaintDto dto, ClaimsPrincipal me, AppDbContext db, Notifier notifier) =>
        {
            var isPharmacy = me.IsInRole(Roles.Pharmacy);
            // "عن مستخدم" للصيدلية بس، و"عن صيدلية" للمستخدم بس
            if (!Enum.IsDefined(dto.Category) || (isPharmacy && dto.Category == ComplaintCategory.Pharmacy) || (!isPharmacy && dto.Category == ComplaintCategory.User))
                return Results.BadRequest(new { error = "invalid_category" });

            var subject = (dto.Subject ?? "").Trim();
            var message = (dto.Message ?? "").Trim();
            if (subject.Length is < 3 or > 150) return Results.BadRequest(new { error = "invalid_subject" });
            if (message.Length is < 10 or > 2000) return Results.BadRequest(new { error = "invalid_message" });

            var c = new Complaint
            {
                UserId = me.UserId(),
                SenderRole = isPharmacy ? Roles.Pharmacy : Roles.User,
                Category = dto.Category,
                Subject = subject,
                Message = message
            };
            db.Complaints.Add(c);
            await db.SaveChangesAsync();

            // لايف للأدمن؛ فشل الإرسال ما يلغيش الشكوى
            try
            {
                var row = await db.Complaints.AsNoTracking().Where(x => x.Id == c.Id).ToRows(db).FirstAsync();
                await notifier.NewComplaintAsync(row);
            }
            catch { /* الأدمن هيشوفها عند التحميل */ }

            return Results.Ok(new { id = c.Id });
        }).RequireRateLimiting("complaint");

        g.MapGet("/mine", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var uid = me.UserId();
            var items = await db.Complaints.AsNoTracking().Where(c => c.UserId == uid)
                .OrderByDescending(c => c.CreatedAt).Take(100)
                .Select(c => new MyComplaintDto(c.Id, c.Category, c.Subject, c.Message, c.Status, c.AdminReply, c.CreatedAt, c.ReviewedAt))
                .ToListAsync();
            return Results.Ok(items);
        });
    }
}
