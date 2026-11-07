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
    public async Task SendToUsersAsync(IEnumerable<Guid> userIds, string title, string body, Dictionary<string, string>? data = null)
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
                    Data = data
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
            new() { ["type"] = "new_request", ["targetId"] = dto.TargetId.ToString() });
    }

    public async Task PharmacyRespondedAsync(Guid userId, Guid requestId, string pharmacyName, TargetStatus status)
    {
        await hub.Clients.Group($"user:{userId}").SendAsync("RequestUpdated", new { requestId, status });
        if (status is TargetStatus.Available or TargetStatus.Partial)
            await push.SendToUsersAsync(new[] { userId }, "في صيدلية ردّت عليك",
                status == TargetStatus.Available ? $"{pharmacyName}: الدواء متوفر" : $"{pharmacyName}: متوفر جزئيًا",
                new() { ["type"] = "pharmacy_response", ["requestId"] = requestId.ToString() });
    }
}
