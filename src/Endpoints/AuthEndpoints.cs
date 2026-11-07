using System.Net.Mail;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Endpoints;

public static class AuthEndpoints
{
    private static IResult Err(string m, int code = 400) => Results.Json(new { error = m }, statusCode: code);

    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/meta/governorates", () => Governorates.All).WithTags("Meta");

        var g = app.MapGroup("/api/auth").WithTags("Auth");

        // ── مستخدم الموبايل: OTP ──
        g.MapPost("/otp/send", async (SendOtpDto dto, OtpService otp) =>
        {
            var phone = Ext.NormalizeEgPhone(dto.Phone);
            if (phone == null) return Err("invalid_phone");
            var (ok, err) = await otp.SendAsync(phone);
            return ok ? Results.Ok(new { sent = true }) : Err(err!, 429);
        }).RequireRateLimiting("otp");

        g.MapPost("/otp/verify", async (VerifyOtpDto dto, OtpService otp) =>
        {
            var phone = Ext.NormalizeEgPhone(dto.Phone);
            if (phone == null) return Err("invalid_phone");
            var r = await otp.VerifyAsync(phone, dto.Code?.Trim() ?? "", dto.FullName?.Trim(), dto.Age);
            return r.Ok ? Results.Ok(r.Auth) : Err(r.Error!);
        }).RequireRateLimiting("otp");

        // ── صيدلية / أدمن: إيميل + باسورد ──
        g.MapPost("/login", async (LoginDto dto, UserManager<AppUser> um, TokenService tokens, AppDbContext db) =>
        {
            var u = await um.FindByEmailAsync((dto.Email ?? "").Trim());
            if (u == null) return Err("invalid_credentials", 401);
            if (await um.IsLockedOutAsync(u)) return Err("locked_out", 423);
            if (!await um.CheckPasswordAsync(u, dto.Password ?? ""))
            {
                await um.AccessFailedAsync(u);
                return Err("invalid_credentials", 401);
            }
            await um.ResetAccessFailedCountAsync(u);
            if (u.IsBanned) return Err("banned", 403);

            var roles = await um.GetRolesAsync(u);
            if (!roles.Contains(Roles.Pharmacy) && !roles.Contains(Roles.Admin)) return Err("invalid_credentials", 401);

            string? status = null;
            if (roles.Contains(Roles.Pharmacy))
                status = (await db.Pharmacies.Where(p => p.OwnerUserId == u.Id)
                    .Select(p => (PharmacyStatus?)p.Status).FirstOrDefaultAsync())?.ToString();

            return Results.Ok(new AuthResponse(await tokens.CreateAsync(u), roles[0], u.FullName, status));
        }).RequireRateLimiting("otp");

        g.MapPost("/pharmacy/register", RegisterPharmacy).DisableAntiforgery().RequireRateLimiting("otp");
    }

    private static async Task<IResult> RegisterPharmacy([FromForm] PharmacyRegisterForm f, UserManager<AppUser> um,
        AppDbContext db, IFileStorage files, MapsLinkParser maps)
    {
        var email = f.Email?.Trim() ?? "";
        if (!MailAddress.TryCreate(email, out var addr) || addr.Address != email) return Err("البريد الإلكتروني غير صالح");
        if (string.IsNullOrWhiteSpace(f.PharmacyName) || string.IsNullOrWhiteSpace(f.PharmacistName))
            return Err("اسم الصيدلية واسم الصيدلي مطلوبين");
        var license = f.LicenseNumber?.Trim() ?? "";
        if (license.Length is < 3 or > 40) return Err("رقم الرخصة غير صالح");
        var gov = f.Governorate?.Trim() ?? "";
        if (!Governorates.All.Contains(gov)) return Err("المحافظة غير صالحة");
        var phone = Ext.LoosePhone(f.Phone);
        if (phone == null) return Err("رقم الهاتف غير صالح");
        if (f.LicenseImage == null) return Err("صورة الرخصة مطلوبة");

        if (await um.FindByEmailAsync(email) != null) return Err("هذا البريد مسجل بالفعل", 409);
        if (await db.Pharmacies.AnyAsync(p => p.LicenseNumber == license)) return Err("رقم الرخصة مسجل بالفعل", 409);

        // الموقع: إحداثيات مباشرة (من دبوس الخريطة) أو من رابط جوجل ماب
        double lat, lng;
        if (f.Lat is double la && f.Lng is double ln && Geo.InEgypt(la, ln)) { lat = la; lng = ln; }
        else
        {
            var parsed = await maps.ParseAsync(f.MapsUrl ?? "");
            if (parsed == null) return Err("تعذر استخراج الموقع من الرابط، من فضلك حدد موقع الصيدلية على الخريطة");
            (lat, lng) = parsed.Value;
        }

        string licensePath;
        try { licensePath = await files.SaveImageAsync(f.LicenseImage, "licenses", isPrivate: true); }
        catch (InvalidOperationException ex) { return Err(ex.Message); }

        var user = new AppUser { UserName = email, Email = email, FullName = f.PharmacistName.Trim(), PhoneNumber = phone };
        var cr = await um.CreateAsync(user, f.Password ?? "");
        if (!cr.Succeeded) return Err(string.Join(" | ", cr.Errors.Select(e => e.Description)));
        await um.AddToRoleAsync(user, Roles.Pharmacy);

        try
        {
            db.Pharmacies.Add(new Pharmacy
            {
                OwnerUserId = user.Id, Name = f.PharmacyName.Trim(), PharmacistName = f.PharmacistName.Trim(),
                LicenseNumber = license, LicenseImagePath = licensePath, Governorate = gov,
                MapsUrl = f.MapsUrl?.Trim() ?? "", Lat = lat, Lng = lng, Phone = phone, Address = f.Address?.Trim()
            });
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await um.DeleteAsync(user);
            return Err("تعذر إكمال التسجيل، رقم الرخصة مسجل بالفعل", 409);
        }

        return Results.Created("/api/pharmacy/me", new { status = "Pending", message = "تم استلام طلبك وجاري مراجعته" });
    }
}
