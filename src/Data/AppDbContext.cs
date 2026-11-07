using MedFinder.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Drug> Drugs => Set<Drug>();
    public DbSet<DrugBarcode> DrugBarcodes => Set<DrugBarcode>();
    public DbSet<BarcodeVote> BarcodeVotes => Set<BarcodeVote>();
    public DbSet<Pharmacy> Pharmacies => Set<Pharmacy>();
    public DbSet<DrugRequest> DrugRequests => Set<DrugRequest>();
    public DbSet<RequestTarget> RequestTargets => Set<RequestTarget>();
    public DbSet<Ad> Ads => Set<Ad>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
        => b.Properties<Enum>().HaveConversion<string>();   // الـ enums تتخزن كنص

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Drug>(e =>
        {
            e.Property(x => x.NameEn).HasMaxLength(300);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasIndex(x => x.NameEn);
        });

        b.Entity<DrugBarcode>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasIndex(x => x.Code);
            e.HasIndex(x => new { x.Code, x.DrugId }).IsUnique();
        });
        b.Entity<BarcodeVote>().HasIndex(x => new { x.BarcodeId, x.VoterId }).IsUnique();

        b.Entity<Pharmacy>(e =>
        {
            e.HasIndex(x => x.LicenseNumber).IsUnique();
            e.HasIndex(x => x.OwnerUserId).IsUnique();
            e.HasIndex(x => new { x.Status, x.Lat, x.Lng });
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId);
        });

        b.Entity<DrugRequest>(e =>
        {
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasMany(x => x.Targets).WithOne(t => t.Request).HasForeignKey(t => t.RequestId);
        });

        b.Entity<RequestTarget>(e =>
        {
            e.HasIndex(x => new { x.RequestId, x.PharmacyId }).IsUnique();
            e.HasIndex(x => new { x.PharmacyId, x.Status });
        });

        b.Entity<DeviceToken>().HasIndex(x => x.Token).IsUnique();
        b.Entity<OtpCode>().HasIndex(x => new { x.Phone, x.CreatedAt });
        b.Entity<Ad>().HasIndex(x => new { x.Status, x.StartDate, x.EndDate });
    }
}
