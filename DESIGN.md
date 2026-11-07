# MedFinder — هوية التصميم

## الروح العامة
**هادي، نظيف، مطمّن.** الناس بتدور على دواء وهي قلقانة، فالواجهة لازم تهدّي مش تزحم.
مساحات بيضاء كبيرة، زوايا مدوّرة، ألوان ناعمة، وحركة بسيطة بتدّي إحساس إن "الطلب شغّال".

## الألوان
| الدور | Light | Dark |
|---|---|---|
| Primary (أخضر نعناعي-تيل) | `#0E9F83` | `#2DD4B0` |
| Primary Soft (خلفية أزرار/شيبس) | `#E3F6F1` | `#12322C` |
| Background | `#F6FAF9` | `#0E1513` |
| Surface (الكروت) | `#FFFFFF` | `#17211E` |
| Text | `#10201C` | `#E8F2EF` |
| Text Muted | `#5F7A73` | `#8FA8A1` |
| Accent (مرجاني — للتنبيه والـ CTA الثانوي) | `#FF7A59` | `#FF9174` |

### ألوان حالة الرد
| الحالة | اللون | ملاحظة |
|---|---|---|
| متوفر | `#12B76A` | أخضر + أيقونة ✓ |
| متوفر جزئي | `#F5A524` | كهرماني + أيقونة ½ |
| غير متوفر | `#98A2B3` | رمادي هادي (مش أحمر صارخ، عشان مايبقاش مقلق) |
| بانتظار الرد | `#5B8DEF` | أزرق + نبض خفيف |

> ما تعتمدش على اللون وحده: كل حالة ليها أيقونة ونص.

## الخطوط
- **عربي:** Cairo أو IBM Plex Sans Arabic (Google Fonts)
- **إنجليزي/أرقام:** Plus Jakarta Sans أو Inter
- الأحجام: عنوان 24/700، عنصر 16/600، نص 14/400، ملاحظة 12/400
- **RTL أولًا** في كل شاشة، والإنجليزي يتعامل معاه كـ LTR داخل النص.

## الشكل
- Radius: كروت 20، أزرار 14، شيبس 999
- ظلال ناعمة جدًا: `0 6px 24px rgba(14,159,131,.08)`
- مسافات على شبكة 4/8px
- حجم لمس 48px على الأقل في الموبايل

## الأفكار الإبداعية (Signature moments)
1. **رادار البحث:** بعد الإرسال، دواير متوسعة بتنبض من موقع المستخدم على خريطة مبسطة، ودبابيس الصيدليات بتظهر وتتلوّن لما ترد. الانتظار يبقى تجربة بدل ما يبقى شاشة فاضية.
2. **شريط البحث الكبير (Hero):** كبسولة بحث عملاقة في أول شاشة، وتحتها 3 أزرار دايرية واضحة: ⌨️ كتابة، 📷 روشتة/علبة، ▮▮ باركود.
3. **كروت الأدوية:** الاسم الإنجليزي بخط كبير، العربي تحته، والمادة الفعالة والتركيز والعبوة في شيبس صغيرة. المستخدم يتأكد إنه اختار الصنف الصح بنظرة.
4. **عدّاد الـ 6 ساعات:** حلقة تقدّم دائرية على كل طلب (موبايل وويب) بتفرغ تدريجيًا.
5. **لوحة الصيدلي (Live):** كروت الطلبات بتنزلق من فوق مع نغمة تنبيه، والأزرار الأربعة بألوانها: ✓ متوفر / ½ جزئي / ✕ غير متوفر / ⓘ تفاصيل. نقطة خضرا صغيرة "متصل Live" بتبيّن إن SignalR شغال.
6. **الإعلانات:** Carousel أعلى الرئيسية بكروت عريضة وزوايا مدوّرة، وعلامة "إعلان" واضحة دايمًا.

## تنفيذ الألوان في Angular (CSS variables)
```css
:root {
  --primary: #0E9F83;  --primary-soft: #E3F6F1;
  --bg: #F6FAF9;       --surface: #FFFFFF;
  --text: #10201C;     --muted: #5F7A73;
  --accent: #FF7A59;
  --ok: #12B76A; --partial: #F5A524; --no: #98A2B3; --pending: #5B8DEF;
  --radius-card: 20px; --radius-btn: 14px;
  --shadow: 0 6px 24px rgba(14,159,131,.08);
}
@media (prefers-color-scheme: dark) {
  :root { --primary:#2DD4B0; --primary-soft:#12322C; --bg:#0E1513;
          --surface:#17211E; --text:#E8F2EF; --muted:#8FA8A1; --accent:#FF9174; }
}
body { font-family: 'Cairo', 'Plus Jakarta Sans', sans-serif; direction: rtl;
       background: var(--bg); color: var(--text); }
```
مكتبة الـ UI المقترحة لـ Angular: **Angular Material 3** أو **PrimeNG** مع تخصيص الألوان دي.

## تنفيذ الألوان في Flutter
```dart
final lightTheme = ThemeData(
  useMaterial3: true,
  fontFamily: 'Cairo',
  colorScheme: ColorScheme.fromSeed(
    seedColor: const Color(0xFF0E9F83),
    secondary: const Color(0xFFFF7A59),
    surface: Colors.white,
  ),
  scaffoldBackgroundColor: const Color(0xFFF6FAF9),
  cardTheme: CardThemeData(
    elevation: 0,
    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
  ),
);
```
الحزم: `google_fonts`, `flutter_animate` (حركات خفيفة), `lottie` (الرادار), `flutter_map` أو `google_maps_flutter`.
