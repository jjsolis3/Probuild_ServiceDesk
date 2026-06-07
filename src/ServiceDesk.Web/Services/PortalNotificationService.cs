using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Creates and reads in-app portal notifications surfaced through the bell in
/// the portal navbar. Outbound email notifications still live in
/// <see cref="EmailNotificationService"/> — this service is purely for the
/// portal UI feed.
/// </summary>
public class PortalNotificationService
{
    private readonly ServiceDeskDbContext _context;
    private readonly ILogger<PortalNotificationService> _logger;

    public PortalNotificationService(
        ServiceDeskDbContext context,
        ILogger<PortalNotificationService> logger)
    {
        _context = context;
        _logger  = logger;
    }

    /// <summary>
    /// Adds a notification for a single recipient. Safe to call from request
    /// paths — exceptions are logged and swallowed so the surrounding action
    /// (e.g. placing an order) is never blocked by a notification failure.
    /// </summary>
    public async Task NotifyAsync(
        int portalUserId, string type, string title,
        string? message = null, string? linkUrl = null, string? icon = null)
    {
        try
        {
            _context.PortalNotifications.Add(new PortalNotification
            {
                PortalUserId = portalUserId,
                Type         = type,
                Title        = title,
                Message      = message,
                LinkUrl      = linkUrl,
                Icon         = icon,
                CreatedDate  = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create portal notification ({Type}) for user {UserId}",
                type, portalUserId);
        }
    }

    /// <summary>
    /// Adds the same notification to multiple recipients.
    /// </summary>
    public async Task NotifyManyAsync(
        IEnumerable<int> portalUserIds, string type, string title,
        string? message = null, string? linkUrl = null, string? icon = null)
    {
        var ids = portalUserIds.Distinct().ToList();
        if (ids.Count == 0) return;

        try
        {
            var now = DateTime.UtcNow;
            foreach (var uid in ids)
            {
                _context.PortalNotifications.Add(new PortalNotification
                {
                    PortalUserId = uid,
                    Type         = type,
                    Title        = title,
                    Message      = message,
                    LinkUrl      = linkUrl,
                    Icon         = icon,
                    CreatedDate  = now
                });
            }
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast portal notification ({Type}) to {Count} users",
                type, ids.Count);
        }
    }

    /// <summary>
    /// Returns the most recent notifications for the user, newest first.
    /// </summary>
    public Task<List<PortalNotification>> GetRecentAsync(int portalUserId, int take = 15)
        => _context.PortalNotifications
            .Where(n => n.PortalUserId == portalUserId)
            .OrderByDescending(n => n.CreatedDate)
            .Take(take)
            .ToListAsync();

    public Task<int> CountUnreadAsync(int portalUserId)
        => _context.PortalNotifications
            .Where(n => n.PortalUserId == portalUserId && !n.IsRead)
            .CountAsync();

    public async Task MarkReadAsync(int portalUserId, int notificationId)
    {
        var n = await _context.PortalNotifications
            .FirstOrDefaultAsync(x => x.Id == notificationId && x.PortalUserId == portalUserId);
        if (n == null || n.IsRead) return;
        n.IsRead   = true;
        n.ReadDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task MarkAllReadAsync(int portalUserId)
    {
        var now = DateTime.UtcNow;
        var unread = await _context.PortalNotifications
            .Where(n => n.PortalUserId == portalUserId && !n.IsRead)
            .ToListAsync();
        foreach (var n in unread)
        {
            n.IsRead   = true;
            n.ReadDate = now;
        }
        if (unread.Count > 0) await _context.SaveChangesAsync();
    }
}
