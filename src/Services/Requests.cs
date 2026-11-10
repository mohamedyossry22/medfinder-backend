using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Services;

public class RequestService(AppDbContext db, Notifier notifier, ILogger<RequestService> log)
{
    public const int MaxPerHour = 5;          // حد الطلبات للمستخدم في الساعة
    public const int MaxPharmacies = 30;      // أقصى عدد صيدليات للطلب الواحد
    public const double MaxRadiusKm = 20;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    public async Task<CreateResult> CreateAsync(Guid userId, CreateRequestDto dto)
    {
        if (dto.Quantity is < 1 or > 20) return new("invalid_quantity", null, 0);
        if (dto.RadiusKm is < 0.5 or > MaxRadiusKm) return new("invalid_radius", null, 0);
        if (!Geo.InEgypt(dto.Lat, dto.Lng)) return new("invalid_location", null, 0);

        var drug = await db.Drugs.FirstOrDefaultAsync(d => d.Id == dto.DrugId && d.IsSearchable);
        if (drug == null) return new("drug_not_found", null, 0);

        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.DrugRequests.CountAsync(r => r.UserId == userId && r.CreatedAt > since) >= MaxPerHour)
            return new("rate_limited", null, 0);

        var now = DateTime.UtcNow;
        var req = new DrugRequest
        {
            UserId = userId, DrugId = drug.Id, Quantity = dto.Quantity,
            Lat = dto.Lat, Lng = dto.Lng, RadiusKm = dto.RadiusKm,
            CreatedAt = now, ExpiresAt = now + Lifetime
        };

        var targets = await FindTargetsAsync(req, dto.RadiusKm, new HashSet<Guid>());
        if (targets.Count == 0) return new(null, null, 0);     // مفيش صيدليات: مانحفظش حاجة

        db.DrugRequests.Add(req);
        db.RequestTargets.AddRange(targets.Select(t => t.Target));
        await db.SaveChangesAsync();
        await NotifyAsync(req, drug, targets);
        return new(null, req.Id, targets.Count);
    }

    /// توسيع النطاق لطلب شغال: بيضيف الصيدليات الجديدة بس
    public async Task<(string? Error, int Added)> ExpandAsync(Guid userId, Guid requestId, double newRadius)
    {
        var req = await db.DrugRequests.Include(r => r.Drug).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.UserId == userId);
        if (req == null) return ("not_found", 0);
        if (req.ClosedAt != null) return ("closed", 0);
        if (req.ExpiresAt <= DateTime.UtcNow) return ("expired", 0);
        if (newRadius <= req.RadiusKm || newRadius > MaxRadiusKm) return ("invalid_radius", 0);

        var excluded = req.Targets.Select(t => t.PharmacyId).ToHashSet();
        var targets = await FindTargetsAsync(req, newRadius, excluded);
        req.RadiusKm = newRadius;
        db.RequestTargets.AddRange(targets.Select(t => t.Target));
        await db.SaveChangesAsync();
        await NotifyAsync(req, req.Drug, targets);
        return (null, targets.Count);
    }

    /// الحالة العامة للطلب من حالات الصيدليات
    public static string Overall(IReadOnlyCollection<TargetStatus> st, bool closed, bool expired)
    {
        if (closed || st.Contains(TargetStatus.Delivered)) return "Delivered";
        if (expired) return "Expired";
        if (st.Contains(TargetStatus.Available)) return "Available";
        if (st.Contains(TargetStatus.Partial)) return "Partial";
        if (st.Count > 0 && st.All(s => s == TargetStatus.Unavailable)) return "Unavailable";
        return "Pending";
    }

    /// المستخدم بيأكد إنه استلم الدواء: الطلب بيتقفل وبيتلغي عند باقي الصيدليات
    public async Task<string?> ConfirmDeliveryAsync(Guid userId, Guid requestId)
    {
        var req = await db.DrugRequests.Include(r => r.Targets).ThenInclude(t => t.Pharmacy)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.UserId == userId);
        if (req == null) return "not_found";
        if (req.ClosedAt != null) return "already_closed";

        var delivered = req.Targets.Where(t => t.Status == TargetStatus.Delivered)
            .OrderByDescending(t => t.DeliveredAt).FirstOrDefault();
        if (delivered == null) return "not_delivered";

        req.ClosedAt = DateTime.UtcNow;
        req.DeliveredTargetId = delivered.Id;

        var affected = new List<Guid>();
        foreach (var t in req.Targets.Where(t => t.Id != delivered.Id &&
                     (t.Status == TargetStatus.Pending || t.Status == TargetStatus.Available || t.Status == TargetStatus.Partial)))
        {
            t.Status = TargetStatus.Cancelled;
            affected.Add(t.PharmacyId);
        }
        await db.SaveChangesAsync();

        try { await notifier.RequestClosedAsync(req.Id, affected); }
        catch (Exception ex) { log.LogWarning(ex, "notify closed failed"); }
        return null;
    }

    private async Task<List<(RequestTarget Target, Pharmacy Pharmacy)>> FindTargetsAsync(
        DrugRequest req, double radiusKm, HashSet<Guid> excluded)
    {
        var (minLat, maxLat, minLng, maxLng) = Geo.BBox(req.Lat, req.Lng, radiusKm);
        var candidates = await db.Pharmacies.AsNoTracking()
            .Where(p => p.Status == PharmacyStatus.Approved &&
                        p.Lat >= minLat && p.Lat <= maxLat && p.Lng >= minLng && p.Lng <= maxLng)
            .ToListAsync();

        return candidates
            .Where(p => !excluded.Contains(p.Id))
            .Select(p => (P: p, D: Geo.DistanceKm(req.Lat, req.Lng, p.Lat, p.Lng)))
            .Where(x => x.D <= radiusKm)
            .OrderBy(x => x.D)
            .Take(MaxPharmacies)
            .Select(x => (new RequestTarget
            {
                RequestId = req.Id, PharmacyId = x.P.Id, DistanceKm = Math.Round(x.D, 2)
            }, x.P))
            .ToList();
    }

    private async Task NotifyAsync(DrugRequest req, Drug drug, List<(RequestTarget Target, Pharmacy Pharmacy)> targets)
    {
        foreach (var (t, p) in targets)
        {
            var dto = new IncomingRequestDto(t.Id, req.Id, drug.NameEn, drug.NameAr, drug.ActiveIngredient,
                req.Quantity, t.DistanceKm, req.CreatedAt, req.ExpiresAt);
            try { await notifier.NewRequestAsync(p.Id, p.OwnerUserId, dto); }
            catch (Exception ex) { log.LogWarning(ex, "notify pharmacy {Id} failed", p.Id); }   // فشل إشعار مايوقفش الطلب
        }
    }
}

/// Hangfire: كل 5 دقايق. الطلب بينتهي بعد 6 ساعات (بنخفيه ونقفله، مش بنمسحه عشان الإحصائيات).
public class ExpiryJob(AppDbContext db, PushService push)
{
    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        var due = await db.DrugRequests.Include(r => r.Targets).Include(r => r.Drug)
            .Where(r => !r.ExpiryHandled && r.ExpiresAt <= now).Take(500).ToListAsync();

        foreach (var r in due)
        {
            foreach (var t in r.Targets.Where(t => t.Status == TargetStatus.Pending)) t.Status = TargetStatus.Expired;
            r.ExpiryHandled = true;

            var found = r.Targets.Any(t => t.Status is TargetStatus.Available or TargetStatus.Partial or TargetStatus.Delivered);
            if (!found)
                await push.SendToUsersAsync(new[] { r.UserId }, "انتهى وقت طلبك",
                    $"ما لقيناش {r.Drug.NameAr} متوفر. جرّب تبحث تاني بنطاق أوسع.",
                    new() { ["type"] = "request_expired", ["requestId"] = r.Id.ToString() });
        }
        await db.SaveChangesAsync();
    }
}
