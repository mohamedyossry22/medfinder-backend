using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Hangfire;
using Hangfire.PostgreSql;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Endpoints;
using MedFinder.Hubs;
using MedFinder.Search;
using MedFinder.Services;
using Meilisearch;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var jwtKey = cfg["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key لازم يكون 32 حرف على الأقل (استخدم user-secrets أو متغير بيئة)");

// ── قاعدة البيانات + Identity ──
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
builder.Services.AddIdentityCore<AppUser>(o =>
{
    o.Password.RequiredLength = 8;
    o.Password.RequireNonAlphanumeric = false;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>();

// ── JWT (وبيشتغل مع SignalR عن طريق access_token في الـ query) ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = cfg["Jwt:Issuer"],
        ValidateAudience = true, ValidAudience = cfg["Jwt:Audience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var t = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = t;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

// ── SignalR + JSON (enums كنص) + CORS + Rate limit ──
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>())
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("otp", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// ── Hangfire ──
builder.Services.AddHangfire(c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(cfg.GetConnectionString("Default"))));
builder.Services.AddHangfireServer();

// ── Firebase (اختياري: لو مفيش ملف credentials بيتجاهل الإشعارات) ──
var fcmPath = cfg["Firebase:CredentialsPath"];
if (!string.IsNullOrEmpty(fcmPath) && File.Exists(fcmPath) && FirebaseApp.DefaultInstance == null)
    FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromFile(fcmPath) });

// ── Services ──
builder.Services.AddSingleton(new MeilisearchClient(cfg["Meili:Url"] ?? "http://localhost:7700", cfg["Meili:Key"]));
builder.Services.AddHttpClient("maps", c =>
{
    c.Timeout = TimeSpan.FromSeconds(6);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MedFinder");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { MaxAutomaticRedirections = 5 });

builder.Services.AddSingleton<ISmsSender, ConsoleSmsSender>();   // بدّله بمزود SMS حقيقي
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<OtpService>();
builder.Services.AddScoped<MapsLinkParser>();
builder.Services.AddScoped<PushService>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<ExpiryJob>();
builder.Services.AddScoped<DrugImporter>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "الصق التوكن فقط (من /api/auth/login) بدون كلمة Bearer"
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

// ── تهيئة عند التشغيل: Migration + Roles + Admin + Meilisearch + Jobs ──
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

    var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var r in new[] { Roles.User, Roles.Pharmacy, Roles.Admin })
        if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole<Guid>(r));

    var um = sp.GetRequiredService<UserManager<AppUser>>();
    var adminEmail = cfg["Admin:Email"]; var adminPass = cfg["Admin:Password"];
    if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPass) && await um.FindByEmailAsync(adminEmail) == null)
    {
        var admin = new AppUser { UserName = adminEmail, Email = adminEmail, EmailConfirmed = true, FullName = "Admin" };
        var res = await um.CreateAsync(admin, adminPass);
        if (res.Succeeded) await um.AddToRoleAsync(admin, Roles.Admin);
        else app.Logger.LogError("Admin seed failed: {Errors}", string.Join(", ", res.Errors.Select(e => e.Description)));
    }

    await MeiliSetup.ConfigureAsync(sp.GetRequiredService<MeilisearchClient>(),
        Path.Combine(app.Environment.ContentRootPath, "data", "arabic-synonyms.json"));

    // أول تشغيل والداتابيز فاضية: استورد الأدوية أوتوماتيك لو الملف موجود (ممكن ياخد دقيقة)
    var drugsDb = sp.GetRequiredService<AppDbContext>();
    if (!await drugsDb.Drugs.AnyAsync())
    {
        var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
        var drugsFile = Path.Combine(dataDir, "egyptian-drugs.json");
        if (File.Exists(drugsFile))
        {
            try
            {
                app.Logger.LogInformation("Importing drugs from {File} ...", drugsFile);
                var res = await sp.GetRequiredService<DrugImporter>()
                    .ImportAsync(drugsFile, Path.Combine(dataDir, "restricted-ingredients.txt"));
                app.Logger.LogInformation("Drugs imported: {@Result}", res);
            }
            catch (Exception ex) { app.Logger.LogError(ex, "Auto-import of drugs failed"); }
        }
        else app.Logger.LogWarning("data/egyptian-drugs.json مش موجود: مفيش استيراد تلقائي");
    }
}

var recurringJobManager =
    app.Services.GetRequiredService<IRecurringJobManager>();

recurringJobManager.AddOrUpdate<ExpiryJob>(
    "expire-requests",
    j => j.RunAsync(),
    "*/5 * * * *"
);
// ── Pipeline ──
app.UseSwagger();
app.UseSwaggerUI();
app.UseStaticFiles();                 // /uploads (صور الإعلانات فقط، التراخيص خاصة)
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment()) app.UseHangfireDashboard("/hangfire");   // محلي فقط

app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapAuth();
app.MapDrugs();
app.MapRequests();
app.MapPharmacy();
app.MapSharedEndpoints();
app.MapAdmin();

app.Run();
