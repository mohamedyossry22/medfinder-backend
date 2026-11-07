using System.Text;

namespace MedFinder.Services;

public static class ArabicNormalizer
{
    /// يوحّد الهمزات والتاء المربوطة والألف المقصورة ويشيل التشكيل والتطويل.
    /// لازم يتطبق على النص المفهرس وعلى كلمة بحث المستخدم بنفس الطريقة.
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            if (ch is >= '\u064B' and <= '\u065F' or '\u0670' or '\u0640') continue; // تشكيل + تطويل
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => char.ToLowerInvariant(ch)
            });
        }
        return sb.ToString();
    }
}
