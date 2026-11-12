using Microsoft.AspNetCore.Identity;

namespace MedFinder.Domain;

public static class Roles
{
    public const string User = "User";
    public const string Pharmacy = "Pharmacy";
    public const string Admin = "Admin";
}

public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
    public int? Age { get; set; }
    public bool IsBanned { get; set; }
    public string? BanReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────── الأدوية والباركود ─────────
public class Drug
{
    public Guid Id { get; set; }
    public string NameEn { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameArNorm { get; set; } = "";
    public string? ActiveIngredient { get; set; }
    public string? Manufacturer { get; set; }
    public string? DrugClass { get; set; }
    public string? Route { get; set; }
    public decimal? Price { get; set; }
    public bool IsSearchable { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum BarcodeSource { User, Pharmacist, Admin }
public enum BarcodeStatus { Pending, Confirmed, Rejected }

public class DrugBarcode
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public Guid DrugId { get; set; }
    public Drug Drug { get; set; } = null!;
    public BarcodeSource Source { get; set; }
    public int ConfirmCount { get; set; }          // مجموع أوزان الأصوات
    public BarcodeStatus Status { get; set; } = BarcodeStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<BarcodeVote> Votes { get; set; } = new();
}

public class BarcodeVote
{
    public int Id { get; set; }
    public int BarcodeId { get; set; }
    public Guid VoterId { get; set; }
    public int Weight { get; set; }
}

// ───────── الصيدليات ─────────
public enum PharmacyStatus { Pending, Approved, Rejected, Suspended }

public class Pharmacy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public AppUser Owner { get; set; } = null!;
    public string Name { get; set; } = "";
    public string PharmacistName { get; set; } = "";
    public string LicenseNumber { get; set; } = "";
    public string LicenseImagePath { get; set; } = "";   // مسار خاص (مش public)
    public string Governorate { get; set; } = "";
    public string MapsUrl { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string Phone { get; set; } = "";
    public string? Address { get; set; }
    public PharmacyStatus Status { get; set; } = PharmacyStatus.Pending;
    public string? StatusReason { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // رصيد الإعلانات المجانية (بيتجدد لحد 2 أول كل شهر بتوقيت القاهرة، من غير ما يتراكم)
    public int FreeAds { get; set; }
    public string FreeAdsMonth { get; set; } = "";   // "2026-11": آخر شهر اتجدد فيه الرصيد
}

// ───────── الطلبات ─────────
public class DrugRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DrugId { get; set; }
    public Drug Drug { get; set; } = null!;
    public int Quantity { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double RadiusKm { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ReplyDeadline { get; set; }     // آخر وقت لرد الصيدليات (120 دقيقة من الإنشاء)
    public DateTime ExpiresAt { get; set; }         // نهاية الطلب بالكامل (فترة الاستلام والتسليم: 6 ساعات)
    public bool ExpiryHandled { get; set; }         // اتعالجت نهاية مهلة الرد (خصم عدم الرد + إشعار "مالقيناش")
    public DateTime? ClosedAt { get; set; }        // المستخدم أكد الاستلام: الطلب اتقفل
    public Guid? DeliveredTargetId { get; set; }   // الصيدلية اللي سلّمت
    public List<RequestTarget> Targets { get; set; } = new();
}

public enum TargetStatus { Pending, Available, Partial, Unavailable, Delivered, Expired, Cancelled }

public class RequestTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public DrugRequest Request { get; set; } = null!;
    public Guid PharmacyId { get; set; }
    public Pharmacy Pharmacy { get; set; } = null!;
    public TargetStatus Status { get; set; } = TargetStatus.Pending;
    public int? AvailableQty { get; set; }
    public double DistanceKm { get; set; }
    public DateTime NotifiedAt { get; set; } = DateTime.UtcNow;   // وقت وصول الطلب للصيدلية (أساس حساب سرعة الرد)
    public DateTime? RespondedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

// ───────── الإعلانات وغيرها ─────────
public enum AdStatus { Pending, Approved, Rejected, Stopped }
public enum AdCostSource { None, Free, Points }   // الإعلان اتخصم من إيه

public class Ad
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PharmacyId { get; set; }
    public Pharmacy Pharmacy { get; set; } = null!;
    public string Title { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string? LinkUrl { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public AdStatus Status { get; set; } = AdStatus.Pending;
    public AdCostSource CostSource { get; set; } = AdCostSource.None;
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeviceToken
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public string Platform { get; set; } = "android";   // android | ios | web
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ───────── الشكاوى ─────────
public enum ComplaintCategory { Request, Pharmacy, User, App, Other }
public enum ComplaintStatus { New, InReview, Resolved, Closed }

/// شكوى من مستخدم أو صيدلية للأدمن
public class Complaint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }                 // صاحب الشكوى (مستخدم أو صاحب صيدلية)
    public AppUser User { get; set; } = null!;
    public string SenderRole { get; set; } = "";     // User | Pharmacy
    public ComplaintCategory Category { get; set; }
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public ComplaintStatus Status { get; set; } = ComplaintStatus.New;
    public string? AdminReply { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ───────── نقاط الصيدليات ─────────
public enum PointsReason { Reply, Missed, AdSpend, AdRefund }

/// دفتر النقاط: الرصيد = مجموع الـ Delta. (TargetId, Reason) فريد عشان نفس الطلب مايتحسبش مرتين.
public class PointsEntry
{
    public long Id { get; set; }
    public Guid PharmacyId { get; set; }
    public int Delta { get; set; }
    public PointsReason Reason { get; set; }
    public Guid? TargetId { get; set; }
    public Guid? AdId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OtpCode
{
    public long Id { get; set; }
    public string Phone { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public bool Used { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
