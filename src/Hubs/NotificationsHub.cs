using MedFinder.Common;
using MedFinder.Data;
using MedFinder.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MedFinder.Hubs;

/// الصيدلية المعتمدة بتدخل group بتاعها، والمستخدم بيدخل group بتاعه.
/// الأحداث: NewRequest (للصيدلية) و RequestUpdated (للمستخدم) و NewComplaint (للأدمن).
[Authorize]
public class NotificationsHub(AppDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var uid = Context.User!.UserId();
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{uid}");

        if (Context.User.IsInRole(Roles.Admin)) await Groups.AddToGroupAsync(Context.ConnectionId, "admins");   // الشكاوى الجديدة لايف

        var pid = Context.User.PharmacyId();
        if (pid != null && await db.Pharmacies.AnyAsync(p =>
                p.Id == pid && p.OwnerUserId == uid && p.Status == PharmacyStatus.Approved))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"pharmacy:{pid}");

        await base.OnConnectedAsync();
    }
}
