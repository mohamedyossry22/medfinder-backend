using System.Globalization;
using System.Text.RegularExpressions;

namespace MedFinder.Services;

public static class Geo
{
    private static double Rad(double d) => d * Math.PI / 180;

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    /// مربع تقريبي حوالين النقطة لتصفية سريعة قبل حساب المسافة الدقيقة
    public static (double MinLat, double MaxLat, double MinLng, double MaxLng) BBox(double lat, double lng, double km)
    {
        var dLat = km / 111.0;
        var dLng = km / (111.0 * Math.Max(0.1, Math.Cos(Rad(lat))));
        return (lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    public static bool InEgypt(double lat, double lng) => lat is >= 21.5 and <= 32 && lng is >= 24 and <= 37;
}

/// بيطلّع الإحداثيات من رابط Google Maps (مع تتبّع الروابط المختصرة)
public class MapsLinkParser(IHttpClientFactory factory)
{
    private static readonly Regex[] Patterns =
    {
        new(@"!3d(-?\d+\.\d+)!4d(-?\d+\.\d+)"),                                   // موقع الدبوس نفسه (الأدق)
        new(@"@(-?\d+\.\d+),(-?\d+\.\d+)"),                                       // مركز الخريطة
        new(@"[?&](?:q|ll|query|destination)=(-?\d+\.\d+)(?:,|%2C)(-?\d+\.\d+)")
    };

    private static (double, double)? Try(string url)
    {
        foreach (var p in Patterns)
        {
            var m = p.Match(url);
            if (!m.Success) continue;
            var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var lng = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (Geo.InEgypt(lat, lng)) return (lat, lng);
        }
        return null;
    }

    public async Task<(double Lat, double Lng)?> ParseAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        // حماية SSRF: بس نطاقات جوجل
        var h = uri.Host.ToLowerInvariant();
        var allowed = h.EndsWith("google.com") || h.EndsWith("google.com.eg") || h == "goo.gl" || h.EndsWith(".goo.gl");
        if (!allowed) return null;

        var direct = Try(Uri.UnescapeDataString(url));
        if (direct != null) return direct;

        try
        {
            using var resp = await factory.CreateClient("maps").GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var final = resp.RequestMessage?.RequestUri?.ToString();
            return final == null ? null : Try(Uri.UnescapeDataString(final));
        }
        catch { return null; }
    }
}
