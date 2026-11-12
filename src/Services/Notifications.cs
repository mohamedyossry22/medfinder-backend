using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using MedFinder.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Services;

/// FCM: موبايل + ويب. لو Firebase مش متظبط بيتجاهل بهدوء (مفيد في التطوير).
public class PushService(AppDbContext db, ILogger<PushService> log)
{
    /// androidChannel: قناة الإشعارات في تطبيق الموبايل (لازم تكون متعرّفة في MainActivity.kt).
    /// ttl: لو الجهاز أوفلاين، الإشعار بيفضل مستني الفترة دي بس (الطلب بيخلص وقته فمفيش فايدة من إشعار قديم).
    public async Task SendToUsersAsync(IEnumerable<Guid> userIds, string title, string body, Dictionary<string, string>? data = null,
        string? androidChannel = null, TimeSpan? ttl = null)
    {
        if (FirebaseApp.DefaultInstance == null) return;
        try
        {
            var ids = userIds.ToList();
            var tokens = await db.DeviceTokens.Where(t => ids.Contains(t.UserId)).Select(t => t.Token).ToListAsync();
            foreach (var chunk in tokens.Chunk(500))
            {
                var msg = new MulticastMessage
                {
                    Tokens = chunk.ToList(),
                    Notification = new Notification { Title = title, Body = body },
                    Data = data,
                    // High: بيصحّي الجهاز حتى في Doze، وده المهم لوصول الطلب والتطبيق مقفول
                    Android = new AndroidConfig
                    {
                        Priority = Priority.High,
                        TimeToLive = ttl,
                        Notification = androidChannel == null ? null : new AndroidNotification { ChannelId = androidChannel, Sound = "default" }
                    }
                };
                var res = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(msg);

                var dead = new List<string>();
                for (var i = 0; i < res.Responses.Count; i++)
                {
                    var code = res.Responses[i].Exception?.MessagingErrorCode;
                    if (!res.Responses[i].IsSuccess && code is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                        dead.Add(chunk[i]);
                }
                if (dead.Count > 0) await db.DeviceTokens.Where(t => dead.Contains(t.Token)).ExecuteDeleteAsync();
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "FCM send failed"); }
    }
}

/// SignalR (لايف) + FCM (لما يكون التطبيق/الصفحة مقفولة)
public class Notifier(IHubContext<NotificationsHub> hub, PushService push)
{
    public async Task NewRequestAsync(Guid pharmacyId, Guid ownerUserId, IncomingRequestDto dto)
    {
        await hub.Clients.Group($"pharmacy:{pharmacyId}").SendAsync("NewRequest", dto);
        await push.SendToUsersAsync(new[] { ownerUserId }, "طلب دواء جديد",
            $"{dto.DrugNameAr} — على بعد {dto.DistanceKm:0.#} كم",
            new() { ["type"] = "new_request", ["targetId"] = dto.TargetId.ToString() },
            androidChannel: "new_requests", ttl: RequestService.ReplyWindow);   // الإشعار بعد مهلة الرد ملوش لازمة
    }

    /// شكوى جديدة: لايف لأي أدمن فاتح لوحة الإدارة
    public Task NewComplaintAsync(ComplaintRowDto dto) => hub.Clients.Group("admins").SendAsync("NewComplaint", dto);

    /// الأدمن رد على الشكوى: إشعار لصاحبها
    public Task ComplaintAnsweredAsync(Guid userId, ComplaintStatus status, string? reply)
        => push.SendToUsersAsync(new[] { userId }, "تم الرد على شكواك",
            string.IsNullOrWhiteSpace(reply) ? (status == ComplaintStatus.InReview ? "شكواك قيد المراجعة" : "تم تحديث حالة شكواك") : reply!,
            new() { ["type"] = "complaint" });

    public async Task PharmacyRespondedAsync(Guid userId, Guid requestId, string pharmacyName, TargetStatus status)
    {
        await hub.Clients.Group($"user:{userId}").SendAsync("RequestUpdated", new { requestId, status });
        if (status is TargetStatus.Available or TargetStatus.Partial)
            await push.SendToUsersAsync(new[] { userId }, "في صيدلية ردّت عليك",
                status == TargetStatus.Available ? $"{pharmacyName}: الدواء متوفر" : $"{pharmacyName}: متوفر جزئيًا",
                new() { ["type"] = "pharmacy_response", ["requestId"] = requestId.ToString() });
    }

    /// الصيدلية سجّلت تسليم: نطلب من المستخدم يأكد الاستلام
    public async Task DeliveredAsync(Guid userId, Guid requestId, string pharmacyName)
    {
        await hub.Clients.Group($"user:{userId}").SendAsync("RequestUpdated", new { requestId, status = TargetStatus.Delivered });
        await push.SendToUsersAsync(new[] { userId }, "تم تسليم الدواء؟",
            $"{pharmacyName} سجّلت تسليم الدواء. أكّد الاستلام من التطبيق.",
            new() { ["type"] = "delivered", ["requestId"] = requestId.ToString() });
    }

    /// الطلب اتقفل: الصيدليات اللي لسه ما ردتش تشيله من شاشتها لايف
    public async Task RequestClosedAsync(Guid requestId, IEnumerable<Guid> pharmacyIds)
    {
        foreach (var id in pharmacyIds.Distinct())
            await hub.Clients.Group($"pharmacy:{id}").SendAsync("RequestClosed", new { requestId });
    }
}
