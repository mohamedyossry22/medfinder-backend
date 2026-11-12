using System.Collections.Concurrent;
using MedFinder.Data;
using MedFinder.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Services;

/// قواعد النقاط والإعلانات (مكان واحد للأرقام)
public static class PointsRules
{
    public const int AdCostPoints = 50;     // كل 50 نقطة = إعلان
    public const int MonthlyFreeAds = 2;    // رصيد مجاني بيتجدد كل شهر (لحد 2، من غير تراكم)
    public const int MissPenalty = 2;       // خصم عدم الرد لحد ما الطلب ينتهي
    public const int MaxAdDays = 3;         // أقصى مدة للإعلان
    public const double AdRadiusKm = 10;    // الإعلان بيظهر بس للمستخدمين في نطاق 10 كم من الصيدلية

    /// أول 5 دقايق = 3 نقاط، من 5 لـ 10 = 2، بعد كده = 1
    public static int ForReply(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromMinutes(5) ? 3 : elapsed < TimeSpan.FromMinutes(10) ? 2 : 1;

    /// الطلبات اللي اتعملت من 12 منتصف الليل لحد 9 الصبح (بتوقيت القاهرة) مافيهاش خصم عدم رد
    public const int NoPenaltyUntilHour = 9;
    public static bool IsNoPenaltyTime(DateTime createdUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(createdUtc, Cairo).Hour < NoPenaltyUntilHour;

    private static TimeZoneInfo Cairo => TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    public static string CairoMonth(DateTime utcNow)
    {
        var l = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Cairo);
        return $"{l.Year:D4}-{l.Month:D2}";
    }

    /// أول الشهر الجاي بتوقيت القاهرة (UTC)
    public static DateTime NextRenewalUtc(DateTime utcNow)
    {
        var l = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Cairo);
        var next = new DateTime(l.Year, l.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, Cairo);
    }

    /// تواريخ الإعلان: لحد 3 أيام، ومايبدأش بعد أكتر من 30 يوم
    public static bool ValidAdDates(DateTime start, DateTime end, DateTime now) =>
        end > start && end > now && (end - start).TotalDays <= MaxAdDays && start <= now.AddDays(30);
}

public record ReplyAward(int Delta, int Balance);
public record AdBalance(int FreeAds, int FreeAdsMax, int Points, int PointsPerAd, int AdsFromPoints, int MaxAdDays, DateTime RenewsAt);

public class PointsService(AppDbContext db)
{
    // قفل لكل صيدلية: بيمنع صرف الـ 50 نقطة مرتين لو الطلبين اتبعتوا في نفس اللحظة
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static SemaphoreSlim LockOf(Guid id) => Locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<int> BalanceAsync(Guid pharmacyId) =>
        await db.PointsLedger.Where(l => l.PharmacyId == pharmacyId).SumAsync(l => (int?)l.Delta) ?? 0;

    /// أول رد على الطلب بس بيكسب نقاط. تعديل الرد بعد كده مابيزوّدش.
    public async Task<ReplyAward?> AwardReplyAsync(Guid pharmacyId, RequestTarget t, string drugName)
    {
        if (t.RespondedAt == null) return null;
        var delta = PointsRules.ForReply(t.RespondedAt.Value - t.NotifiedAt);
        var entry = new PointsEntry { PharmacyId = pharmacyId, Delta = delta, Reason = PointsReason.Reply, TargetId = t.Id, Note = drugName };
        db.PointsLedger.Add(entry);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.Entry(entry).State = EntityState.Detached;   // اتحسب قبل كده (ضغطتين متزامنتين)
            return null;
        }
        return new ReplyAward(delta, await BalanceAsync(pharmacyId));
    }

    // ───────── رصيد الإعلانات ─────────

    /// أول مرة في الشهر الجديد: الرصيد المجاني بيكمل لحد 2 (مفيش تراكم)
    private async Task RefillAsync(Guid pharmacyId)
    {
        var month = PointsRules.CairoMonth(DateTime.UtcNow);
        await db.Pharmacies.Where(p => p.Id == pharmacyId && p.FreeAdsMonth != month)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.FreeAdsMonth, month)
                .SetProperty(p => p.FreeAds, p => p.FreeAds < PointsRules.MonthlyFreeAds ? PointsRules.MonthlyFreeAds : p.FreeAds));
    }

    public async Task<AdBalance> GetBalanceAsync(Guid pharmacyId)
    {
        await RefillAsync(pharmacyId);
        var free = await db.Pharmacies.AsNoTracking().Where(p => p.Id == pharmacyId).Select(p => p.FreeAds).FirstAsync();
        var pts = await BalanceAsync(pharmacyId);
        return new AdBalance(free, PointsRules.MonthlyFreeAds, pts, PointsRules.AdCostPoints,
            Math.Max(0, pts) / PointsRules.AdCostPoints, PointsRules.MaxAdDays, PointsRules.NextRenewalUtc(DateTime.UtcNow));
    }

    public async Task<List<object>> HistoryAsync(Guid pharmacyId, int take = 30) =>
        (await db.PointsLedger.AsNoTracking().Where(l => l.PharmacyId == pharmacyId)
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Take(take)
            .Select(l => new { l.Delta, l.Reason, l.Note, l.CreatedAt }).ToListAsync()).Cast<object>().ToList();

    /// أمر نشر: بيخصم من الرصيد المجاني الأول، وبعدين 50 نقطة. بيرجّع null لو مفيش رصيد.
    public async Task<AdCostSource?> ChargeAdAsync(Guid pharmacyId, Guid adId, string title)
    {
        await RefillAsync(pharmacyId);
        var gate = LockOf(pharmacyId);
        await gate.WaitAsync();
        try
        {
            var n = await db.Pharmacies.Where(p => p.Id == pharmacyId && p.FreeAds > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.FreeAds, p => p.FreeAds - 1));
            if (n == 1) return AdCostSource.Free;

            if (await BalanceAsync(pharmacyId) < PointsRules.AdCostPoints) return null;
            db.PointsLedger.Add(new PointsEntry
            {
                PharmacyId = pharmacyId, Delta = -PointsRules.AdCostPoints, Reason = PointsReason.AdSpend, AdId = adId, Note = title
            });
            await db.SaveChangesAsync();
            return AdCostSource.Points;
        }
        finally { gate.Release(); }
    }

    /// الإعلان اترفض (قبل ما يظهر): الرصيد بيرجع. الـ caller بيعمل SaveChanges للنقاط.
    public async Task RefundAdAsync(Ad ad)
    {
        switch (ad.CostSource)
        {
            case AdCostSource.Free:
                await db.Pharmacies.Where(p => p.Id == ad.PharmacyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.FreeAds,
                        p => p.FreeAds < PointsRules.MonthlyFreeAds ? p.FreeAds + 1 : p.FreeAds));
                break;
            case AdCostSource.Points:
                db.PointsLedger.Add(new PointsEntry
                {
                    PharmacyId = ad.PharmacyId, Delta = PointsRules.AdCostPoints, Reason = PointsReason.AdRefund, AdId = ad.Id, Note = ad.Title
                });
                break;
        }
        ad.CostSource = AdCostSource.None;
    }
}
