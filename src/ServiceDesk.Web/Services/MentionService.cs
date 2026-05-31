using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;
using System.Text.RegularExpressions;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Parses <c>@First Last</c> patterns out of free-text bodies (ticket notes,
/// receipt comments) and resolves them to portal users so the bell + email
/// notification can fire. Match order:
///   1. Exact case-insensitive <c>First Last</c> match against
///      <c>PortalUsers.FirstName + " " + LastName</c>.
///   2. Same match against <c>Employees</c> when no portal user hits — an
///      employee with an attached PortalUser still fires the bell.
/// Unmatched mentions are silently ignored (no error, no noise) so a typo
/// like <c>@JaneSmith</c> doesn't blow up the surrounding action.
/// </summary>
public class MentionService
{
    private readonly ServiceDeskDbContext _context;
    private readonly PortalNotificationService _portalNotifications;
    private readonly ILogger<MentionService> _logger;

    // Matches "@FirstName LastName" or "@FirstName" — letters, apostrophes,
    // hyphens (so "O'Brien" and "Smith-Jones" both work). Stops at any
    // other character so trailing punctuation isn't captured.
    private static readonly Regex MentionRegex = new(
        @"@([A-Za-z][A-Za-z'\-]*)(?:\s+([A-Za-z][A-Za-z'\-]*))?",
        RegexOptions.Compiled);

    public MentionService(
        ServiceDeskDbContext context,
        PortalNotificationService portalNotifications,
        ILogger<MentionService> logger)
    {
        _context = context;
        _portalNotifications = portalNotifications;
        _logger = logger;
    }

    /// <summary>
    /// Scans the body, resolves every @mention to a unique portal user, and
    /// fires a bell notification for each. <paramref name="sourceLabel"/>
    /// is the short context label that appears in the notification
    /// (e.g. <c>Ticket #1234</c> or <c>Receipt #56</c>).
    /// </summary>
    public async Task<List<int>> ProcessMentionsAsync(
        string body,
        string authorDisplayName,
        string sourceLabel,
        string linkUrl,
        string notificationType = "Mention",
        int? excludeUserId = null)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<int>();

        var matches = MentionRegex.Matches(body);
        if (matches.Count == 0) return new List<int>();

        // Build the candidate name set (lower-cased, deduped) so we hit
        // the DB once per author rather than per match.
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var first = m.Groups[1].Value;
            var last  = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (string.IsNullOrEmpty(first)) continue;
            candidateNames.Add(last == null ? first : $"{first} {last}");
        }
        if (candidateNames.Count == 0) return new List<int>();

        var notifiedUserIds = new HashSet<int>();

        // Pull every active portal user and resolve in-memory — the user
        // table is small enough that this is cheaper than N targeted
        // queries, and casing/whitespace tolerance is trivial here.
        var portalUsers = await _context.PortalUsers
            .Where(u => u.IsActive && !string.IsNullOrEmpty(u.FirstName))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.EmployeeId })
            .ToListAsync();

        foreach (var name in candidateNames)
        {
            var hit = portalUsers.FirstOrDefault(u =>
                string.Equals($"{u.FirstName} {u.LastName}".Trim(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.FirstName, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null) continue;
            if (excludeUserId.HasValue && hit.Id == excludeUserId.Value) continue;
            notifiedUserIds.Add(hit.Id);
        }

        if (notifiedUserIds.Count == 0) return new List<int>();

        var title   = $"{authorDisplayName} mentioned you in {sourceLabel}";
        var preview = body.Length > 160 ? body[..160] + "…" : body;

        foreach (var uid in notifiedUserIds)
        {
            await _portalNotifications.NotifyAsync(
                portalUserId: uid,
                type:         notificationType,
                title:        title,
                message:      preview,
                linkUrl:      linkUrl,
                icon:         "bi-at");
        }

        _logger.LogInformation(
            "[Mentions] {Count} user(s) notified from {Source} (author: {Author})",
            notifiedUserIds.Count, sourceLabel, authorDisplayName);

        return notifiedUserIds.ToList();
    }
}
