namespace MedFinder.Services;

public interface IFileStorage
{
    /// بيرجّع المسار المخزّن (private/... للخاص، أو /uploads/... للعام)
    Task<string> SaveImageAsync(IFormFile file, string folder, bool isPrivate);
    (Stream Stream, string ContentType)? OpenPrivate(string storedPath);
}

/// تخزين محلي للتطوير. لاحقًا بدّله بـ S3 / Azure Blob / Cloudinary من غير ما تغيّر باقي الكود.
public class LocalFileStorage(IWebHostEnvironment env) : IFileStorage
{
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp"
    };
    private const long MaxBytes = 5 * 1024 * 1024;

    private string PrivateRoot => Path.Combine(env.ContentRootPath, "private-uploads");
    private string PublicRoot => Path.Combine(env.ContentRootPath, "wwwroot", "uploads");

    private static bool MagicOk(byte[] h, string ext) => ext switch
    {
        ".jpg" => h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        ".png" => h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47,
        ".webp" => h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P',
        _ => false
    };

    public async Task<string> SaveImageAsync(IFormFile file, string folder, bool isPrivate)
    {
        if (file.Length == 0 || file.Length > MaxBytes)
            throw new InvalidOperationException("حجم الصورة غير مقبول (الحد الأقصى 5 ميجا)");
        if (!Allowed.TryGetValue(file.ContentType.ToLowerInvariant(), out var ext))
            throw new InvalidOperationException("نوع الملف غير مدعوم (jpg / png / webp فقط)");

        await using var s = file.OpenReadStream();
        var head = new byte[12];
        var n = await s.ReadAsync(head);
        if (n < 12 || !MagicOk(head, ext))
            throw new InvalidOperationException("الملف ليس صورة صالحة");
        s.Position = 0;

        var dir = Path.Combine(isPrivate ? PrivateRoot : PublicRoot, folder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{ext}";
        await using (var fs = File.Create(Path.Combine(dir, name))) await s.CopyToAsync(fs);

        return isPrivate ? $"private/{folder}/{name}" : $"/uploads/{folder}/{name}";
    }

    public (Stream Stream, string ContentType)? OpenPrivate(string storedPath)
    {
        if (!storedPath.StartsWith("private/")) return null;
        var full = Path.GetFullPath(Path.Combine(PrivateRoot, storedPath["private/".Length..]));
        if (!full.StartsWith(Path.GetFullPath(PrivateRoot)) || !File.Exists(full)) return null;   // حماية path traversal
        var ct = Path.GetExtension(full) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return (File.OpenRead(full), ct);
    }
}
