using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

/// <summary>
/// JSON endpoints used by the portal notification bell. All actions are scoped
/// to the current authenticated portal user.
/// </summary>
[Authorize]
[Route("PortalNotifications")]
public class PortalNotificationsController : Controller
{
    private readonly PortalNotificationService _notifications;

    public PortalNotificationsController(PortalNotificationService notifications)
    {
        _notifications = notifications;
    }

    private int? CurrentUserId()
    {
        var id = User.FindFirst("UserId")?.Value
              ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(id, out var v) ? v : null;
    }

    // GET /PortalNotifications/Feed — recent notifications + unread count
    [HttpGet("Feed")]
    public async Task<IActionResult> Feed()
    {
        var uid = CurrentUserId();
        if (uid == null) return Unauthorized();

        var items  = await _notifications.GetRecentAsync(uid.Value);
        var unread = items.Count(n => !n.IsRead);

        return Json(new
        {
            unread,
            items = items.Select(n => new {
                id        = n.Id,
                type      = n.Type,
                title     = n.Title,
                message   = n.Message,
                linkUrl   = n.LinkUrl,
                icon      = n.Icon,
                isRead    = n.IsRead,
                createdAt = n.CreatedDate
            })
        });
    }

    // POST /PortalNotifications/MarkRead/{id}
    [HttpPost("MarkRead/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(int id)
    {
        var uid = CurrentUserId();
        if (uid == null) return Unauthorized();
        await _notifications.MarkReadAsync(uid.Value, id);
        return Ok();
    }

    // POST /PortalNotifications/MarkAllRead
    [HttpPost("MarkAllRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var uid = CurrentUserId();
        if (uid == null) return Unauthorized();
        await _notifications.MarkAllReadAsync(uid.Value);
        return Ok();
    }
}
