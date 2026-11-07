using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace MedFinder.Services;

public interface ISmsSender { Task SendAsync(string phone, string message); }

/// للتطوير فقط: بيطبع الرسالة في الـ log. استبدله بمزود SMS حقيقي قبل الإطلاق.
public class ConsoleSmsSender(ILogger<ConsoleSmsSender> log) : ISmsSender
{
    public Task SendAsync(string phone, string message)
    {
        log.LogWarning("[DEV SMS] to {Phone}: {Message}", phone, message);
        return Task.CompletedTask;
    }
}

public class TokenService(IConfiguration cfg, UserManager<AppUser> um, AppDbContext db)
{
    public async Task<string> CreateAsync(AppUser u)
    {
        var roles = await um.GetRolesAsync(u);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
            new(ClaimTypes.Name, u.FullName)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        if (roles.Contains(Roles.Pharmacy))
        {
            var pid = await db.Pharmacies.Where(p => p.OwnerUserId == u.Id)
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
            if (pid != null) claims.Add(new Claim("pharmacy_id", pid.Value.ToString()));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var days = roles.Contains(Roles.User) ? 30 : 1;   // الموبايل أطول، الويب/الأدمن يوم
        var token = new JwtSecurityToken(cfg["Jwt:Issuer"], cfg["Jwt:Audience"], claims,
            expires: DateTime.UtcNow.AddDays(days),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record VerifyResult(bool Ok, string? Error, AuthResponse? Auth);

public class OtpService(AppDbContext db, ISmsSender sms, IConfiguration cfg, TokenService tokens, UserManager<AppUser> um)
{
    private string Hash(string phone, string code) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!), Encoding.UTF8.GetBytes($"{phone}:{code}")));

    public async Task<(bool Ok, string? Error)> SendAsync(string phone)
    {
        var since = DateTime.UtcNow.AddMinutes(-10);
        if (await db.OtpCodes.CountAsync(o => o.Phone == phone && o.CreatedAt > since) >= 3)
            return (false, "too_many_requests");

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        db.OtpCodes.Add(new OtpCode { Phone = phone, CodeHash = Hash(phone, code), ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();
        await sms.SendAsync(phone, $"كود التحقق الخاص بك في MedFinder: {code}");
        return (true, null);
    }

    public async Task<VerifyResult> VerifyAsync(string phone, string code, string? fullName, int? age)
    {
        var otp = await db.OtpCodes
            .Where(o => o.Phone == phone && !o.Used && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync();
        if (otp == null) return new(false, "invalid_code", null);

        if (otp.Attempts >= 5) { otp.Used = true; await db.SaveChangesAsync(); return new(false, "too_many_attempts", null); }
        otp.Attempts++;

        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(otp.CodeHash), Encoding.UTF8.GetBytes(Hash(phone, code)));
        if (!match) { await db.SaveChangesAsync(); return new(false, "invalid_code", null); }

        var user = await um.FindByNameAsync(phone);
        if (user == null)
        {
            // مستخدم جديد: لازم الاسم والسن (الكود مايتحرقش، يقدر يبعت تاني بالبيانات)
            if (string.IsNullOrWhiteSpace(fullName) || age is null or < 10 or > 110)
            { await db.SaveChangesAsync(); return new(false, "profile_required", null); }

            user = new AppUser { UserName = phone, PhoneNumber = phone, PhoneNumberConfirmed = true, FullName = fullName.Trim(), Age = age };
            var cr = await um.CreateAsync(user);
            if (!cr.Succeeded) return new(false, "create_failed", null);
            await um.AddToRoleAsync(user, Roles.User);
        }
        if (user.IsBanned) return new(false, "banned", null);

        otp.Used = true;
        await db.SaveChangesAsync();
        return new(true, null, new AuthResponse(await tokens.CreateAsync(user), Roles.User, user.FullName, null));
    }
}
