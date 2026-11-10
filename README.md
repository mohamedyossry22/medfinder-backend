# MedFinder Backend

ASP.NET Core 8 · PostgreSQL · Meilisearch · SignalR · Hangfire · Firebase (FCM)

## الحزم
```
dotnet new web -n MedFinder   # أو استخدم المجلد ده كـ root وحط ملف .csproj بـ Sdk="Microsoft.NET.Sdk.Web"
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package Meilisearch
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.PostgreSql
dotnet add package FirebaseAdmin
dotnet add package Swashbuckle.AspNetCore
```
(في الـ csproj: `Nullable` و`ImplicitUsings` enabled، والـ TargetFramework net8.0)

## الإعداد (اسرار بره الكود)
```
dotnet user-secrets init
dotnet user-secrets set "Jwt:Key" "<عشوائي 64 حرف>"
dotnet user-secrets set "Admin:Email" "you@example.com"
dotnet user-secrets set "Admin:Password" "<باسورد قوي>"
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=medfinder;Username=postgres;Password=..."
```
- Firebase: نزّل service-account JSON من Firebase Console وحط مساره في `Firebase:CredentialsPath`.
- Meilisearch: `docker run -d -p 7700:7700 -e MEILI_MASTER_KEY=devkey getmeili/meilisearch:latest`

## التشغيل
```
dotnet ef migrations add Init
dotnet run
```
1. افتح Swagger، سجّل دخول بالأدمن: `POST /api/auth/login`، وخد الـ token (Authorize).
2. حط `egyptian-drugs.json` في `data/` ونفّذ `POST /api/admin/drugs/import`.
3. أول تشغيل بيعمل الـ Migration والأدوار وحساب الأدمن وإعدادات Meilisearch وجدولة مسح الطلبات.

## الـ Endpoints
| المجموعة | الأهم |
|---|---|
| Auth | `otp/send`, `otp/verify` (موبايل) · `login` (صيدلية/أدمن) · `pharmacy/register` (multipart) |
| Drugs | `search` · `barcode/{code}` · `POST barcode` (ربط باركود) |
| Requests (User) | `POST /api/requests` · `/{id}/expand` · `/mine` · `/{id}` |
| Pharmacy | `requests/incoming` · `requests/{id}/respond` · `ready` · `ready/{id}/deliver` · `ads` |
| Admin | `pharmacies` (اعتماد/رفض/إيقاف + صورة الرخصة) · `users` (حظر) · `ads` · `barcodes` · `stats` |
| Shared | `GET /api/ads` · `POST /api/devices` (تسجيل token الإشعارات) |

## SignalR
`/hubs/notifications?access_token=<JWT>`
- الصيدلية بتسمع على `NewRequest`، والمستخدم على `RequestUpdated`.
- في Angular: `@microsoft/signalr` مع `accessTokenFactory`.

## قرارات تصميمية
- **الطلب مابيتمسحش بعد 6 ساعات**: بينتهي ويختفي من الشاشات (فلترة `ExpiresAt`) لكن بيفضل في الداتابيز للإحصائيات (الطلبات اللي محدش رد عليها = بيانات نقص حقيقية).
- **الخصوصية**: الصيدلية ما بتشوفش أي بيانات عن المستخدم. المستخدم بيشوف بيانات الصيدلية بس لو قالت متوفر وطول ما الطلب شغال.
- **صور التراخيص** في مجلد خاص (`private-uploads`) وما بتتخدمش static، بتتفتح بس من endpoint الأدمن.
- **الموقع**: بيتاخد من دبوس الخريطة (الأفضل) أو من رابط جوجل ماب. الروابط المختصرة بنتبعها بحماية من SSRF، ولو فشلت الصيدلية تحدد الموقع يدوي.

## لسه ناقص قبل الإطلاق
1. **مزود SMS حقيقي** (`ISmsSender` حاليًا بيطبع في الـ log).
2. **تخزين الصور** على S3/Azure/Cloudinary بدل المحلي (`IFileStorage`).
3. **مراجعة قائمة الأدوية المقيّدة** على جداول هيئة الدواء.
4. **Refresh tokens**، و**إيميل** للصيدلية عند الاعتماد/الرفض (حاليًا SMS بس).
5. **Tests** (خصوصًا RequestService والباركود).
6. **قانوني**: سياسة خصوصية، قانون حماية البيانات الشخصية، وضوابط إعلانات الأدوية.
7. الكود ما اتبنيش على جهاز عندي، فأول `dotnet build` ممكن يطلّع أخطاء بسيطة في إصدارات المكتبات، ابعتهالي وأصلحها.


## تحديثات الجولة الأخيرة
**Migration مطلوب** (أعمدة جديدة في الطلبات: `ClosedAt` و`DeliveredTargetId`):
```
dotnet ef migrations add RequestClosing
dotnet run --urls http://0.0.0.0:5000     # بيطبّقها تلقائي عند التشغيل
```
**المسح بالذكاء الاصطناعي (روشتة/علبة)**: `POST /api/drugs/scan` (صورة ← أسماء ← مطابقة مع قاعدة الأدوية).
```
dotnet user-secrets set "Vision:ApiKey" "<مفتاح Claude API>"
dotnet user-secrets set "Vision:Model" "claude-sonnet-5-5"     # أو claude-haiku-5-5 (أرخص وأقل دقة)
```
- المفتاح من console.anthropic.com، والحساب بالاستخدام (رصيد)، مفيش اشتراك شهري.
- الحد: 10 مسحات في الساعة لكل مستخدم (`RateLimiter` سياسة `scan` في `Program.cs`).
- لو المفتاح فاضي أو الخدمة فشلت: الـ API بيرجّع 503/502 والتطبيق بيرجع تلقائيًا للقراءة المحلية.
- الصورة بتتبعت للمزوّد ومابتتخزنش عندنا. اذكر ده في سياسة الخصوصية.

**تأكيد الاستلام**: الصيدلية بتسجّل "تم التسليم" ← المستخدم بيأكد `POST /api/requests/{id}/confirm-delivery` ← الطلب بيتقفل، وباقي الصيدليات بتتلغي (`Cancelled`) وبيتشال من شاشتها لايف (حدث `RequestClosed`).

**الحالة العامة للطلب** (بتتحسب في السيرفر): تم التسليم > انتهى > متوفر > متوفر جزئيًا > غير متوفر (لو الكل رد كده) > بانتظار الرد.

**جديد للصيدلية**: `GET /api/pharmacy/history?from=&to=` (الطلبات المسلّمة مع إجمالي العدد والعلب) و`GET/PUT /api/pharmacy/profile` (تعديل البيانات، رقم الرخصة والإيميل للقراءة بس).
