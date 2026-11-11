using System.Security.Claims;
using System.Text.RegularExpressions;
using MedFinder.Domain;

namespace MedFinder.Common;

public static class Ext
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static Guid? PharmacyId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue("pharmacy_id"), out var g) ? g : null;

    private static string Digits(string raw) =>
        string.Concat(raw.Where(char.IsDigit).Select(c => ((int)char.GetNumericValue(c)).ToString()));

    /// يرجّع رقم موبايل مصري بصيغة 01xxxxxxxxx أو null
    public static string? NormalizeEgPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = Digits(raw);
        if (d.StartsWith("0020")) d = d[4..];
        else if (d.StartsWith("20") && d.Length == 12) d = d[2..];
        if (d.Length == 10 && d[0] == '1') d = "0" + d;
        return Regex.IsMatch(d, @"^01[0125]\d{8}$") ? d : null;
    }

    /// رقم صيدلية (موبايل أو أرضي): 8–15 رقم
    public static string? LoosePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = Digits(raw);
        return d.Length is >= 8 and <= 15 ? d : null;
    }

    /// بداية اليوم بتوقيت القاهرة، محوّلة UTC (للفلاتر بالتاريخ)
    public static DateTime CairoStartUtc(DateOnly d)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);
    }

    public static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };
}

public static class Governorates
{
    public static readonly string[] All =
    {
        "القاهرة","الجيزة","الإسكندرية","الدقهلية","البحر الأحمر","البحيرة","الفيوم","الغربية","الإسماعيلية",
        "المنوفية","المنيا","القليوبية","الوادي الجديد","السويس","الشرقية","أسوان","أسيوط","بني سويف",
        "بورسعيد","دمياط","جنوب سيناء","كفر الشيخ","مطروح","الأقصر","قنا","شمال سيناء","سوهاج"
    };
}

// ───────── DTOs ─────────
public record SendOtpDto(string Phone);
public record VerifyOtpDto(string Phone, string Code, string? FullName, int? Age);
public record LoginDto(string Email, string Password);
public record AuthResponse(string Token, string Role, string FullName, string? PharmacyStatus);

public record CreateRequestDto(Guid DrugId, int Quantity, double Lat, double Lng, double RadiusKm);
public record ExpandRequestDto(double RadiusKm);
public record CreateResult(string? Error, Guid? RequestId, int PharmaciesCount);

public record RespondDto(TargetStatus Status, int? AvailableQty);

/// اللي بيوصل للصيدلية: بدون أي بيانات عن المستخدم (خصوصية)
public record IncomingRequestDto(Guid TargetId, Guid RequestId, string DrugNameEn, string DrugNameAr,
    string? ActiveIngredient, int Quantity, double DistanceKm, DateTime CreatedAt, DateTime ExpiresAt);

public record ContactDto(string Phone, string? Address, double Lat, double Lng, string MapsUrl);
public record PharmacyReplyDto(Guid PharmacyId, string Name, double DistanceKm, TargetStatus Status,
    int? AvailableQty, DateTime? RespondedAt, ContactDto? Contact);
public record RequestDetailsDto(Guid Id, Guid DrugId, string DrugNameEn, string DrugNameAr, int Quantity,
    double RadiusKm, DateTime CreatedAt, DateTime ExpiresAt, bool IsExpired, List<PharmacyReplyDto> Pharmacies,
    string OverallStatus, bool IsClosed, bool NeedsConfirmation, bool CanExpand);
public record RequestSummaryDto(Guid Id, string DrugNameEn, string DrugNameAr, int Quantity, DateTime CreatedAt,
    DateTime ExpiresAt, bool IsExpired, int TotalPharmacies, int AvailableCount,
    string OverallStatus, bool IsClosed, bool NeedsConfirmation);
public record HistoryItemDto(Guid TargetId, string DrugNameEn, string DrugNameAr, int Quantity, DateTime DeliveredAt, bool ConfirmedByUser);
public record UpdatePharmacyProfileDto(string? PharmacyName, string? PharmacistName, string? Phone, string? Address,
    string? Governorate, string? MapsUrl, double? Lat, double? Lng);
public record ReadyItemDto(Guid TargetId, string DrugNameEn, string DrugNameAr, int Quantity, int AvailableQty,
    TargetStatus Status, DateTime RespondedAt, DateTime ExpiresAt);

public record BarcodeLinkDto(string Code, Guid DrugId);
public record ReasonDto(string? Reason);
public record DeviceDto(string Token, string Platform);
public record RepublishDto(DateTime StartDate, DateTime EndDate);
public record CreateComplaintDto(ComplaintCategory Category, string Subject, string Message);
public record ComplaintReplyDto(ComplaintStatus Status, string? Reply);
public record MyComplaintDto(Guid Id, ComplaintCategory Category, string Subject, string Message, ComplaintStatus Status,
    string? AdminReply, DateTime CreatedAt, DateTime? ReviewedAt);
public record ComplaintRowDto(Guid Id, string SenderRole, string SenderName, string? SenderPhone, string? SenderEmail,
    string? PharmacyName, ComplaintCategory Category, string Subject, string Message, ComplaintStatus Status,
    string? AdminReply, DateTime CreatedAt, DateTime? ReviewedAt);

public class PharmacyRegisterForm
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? PharmacyName { get; set; }
    public string? PharmacistName { get; set; }
    public string? LicenseNumber { get; set; }
    public string? Governorate { get; set; }
    public string? MapsUrl { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public IFormFile? LicenseImage { get; set; }
}

public class AdForm
{
    public string? Title { get; set; }
    public string? LinkUrl { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public IFormFile? Image { get; set; }
}
