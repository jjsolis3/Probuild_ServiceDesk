using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Resolves which agent should be assigned to a new ticket based on
/// AssignmentRules (category + branch), with fallback to the email
/// configuration's DefaultAssigneeId.
/// </summary>
public class AssignmentResolverService
{
    private readonly ServiceDeskDbContext _context;
    private readonly ILogger<AssignmentResolverService> _logger;

    public AssignmentResolverService(ServiceDeskDbContext context, ILogger<AssignmentResolverService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Determines the best agent to assign a ticket to.
    ///
    /// Evaluation order (first match wins, within each tier sorted by SortOrder asc):
    ///   1. category + subcategory + branch  (most specific)
    ///   2. category + subcategory           (no branch filter)
    ///   3. category + branch                (no subcategory filter)
    ///   4. category only
    ///   5. branch only
    ///   6. Catch-all (no category, no subcategory, no branch)
    ///   7. <paramref name="defaultAssigneeId"/> fallback
    /// </summary>
    public async Task<int?> ResolveAsync(
        TicketCategory category,
        int? branchId,
        int? defaultAssigneeId,
        int? subCategoryId = null)
    {
        var activeRules = await _context.AssignmentRules
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();

        if (!activeRules.Any())
        {
            _logger.LogDebug("No active assignment rules found — using default assignee.");
            return defaultAssigneeId;
        }

        // Tier 1: category + subcategory + branch
        if (subCategoryId.HasValue && branchId.HasValue)
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == category && r.SubCategoryId == subCategoryId && r.BranchId == branchId);
            if (m != null) { Log(m, "category+subcategory+branch"); return m.AssigneeId; }
        }

        // Tier 2: category + subcategory (any branch)
        if (subCategoryId.HasValue)
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == category && r.SubCategoryId == subCategoryId && r.BranchId == null);
            if (m != null) { Log(m, "category+subcategory"); return m.AssigneeId; }
        }

        // Tier 3: category + branch (any subcategory)
        if (branchId.HasValue)
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == category && r.SubCategoryId == null && r.BranchId == branchId);
            if (m != null) { Log(m, "category+branch"); return m.AssigneeId; }
        }

        // Tier 4: category only
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == category && r.SubCategoryId == null && r.BranchId == null);
            if (m != null) { Log(m, "category-only"); return m.AssigneeId; }
        }

        // Tier 5: branch only
        if (branchId.HasValue)
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == null && r.SubCategoryId == null && r.BranchId == branchId);
            if (m != null) { Log(m, "branch-only"); return m.AssigneeId; }
        }

        // Tier 6: catch-all
        {
            var m = activeRules.FirstOrDefault(r =>
                r.Category == null && r.SubCategoryId == null && r.BranchId == null);
            if (m != null) { Log(m, "catch-all"); return m.AssigneeId; }
        }

        _logger.LogDebug(
            "No assignment rule matched for category={Category}, subCategory={SubCategoryId}, branch={BranchId} — using default {DefaultAssigneeId}",
            category, subCategoryId, branchId, defaultAssigneeId);
        return defaultAssigneeId;
    }

    private void Log(AssignmentRule rule, string tier) =>
        _logger.LogInformation(
            "Assignment rule '{Name}' ({Tier}) matched → assignee {AssigneeId}",
            rule.Name, tier, rule.AssigneeId);

    // -------------------------------------------------------------------------
    // Keyword-based category detection
    // -------------------------------------------------------------------------

    private static readonly Dictionary<TicketCategory, string[]> CategoryKeywords = new()
    {
        [TicketCategory.HardwareIssue] = new[]
        {
            "printer", "printing", "keyboard", "mouse", "monitor", "screen", "display",
            "laptop", "desktop", "computer", "pc", "hardware", "device", "battery",
            "charger", "dock", "docking", "headset", "webcam", "scanner", "projector",
            "broken", "damaged", "physical", "power", "overheating", "fan noise"
        },
        [TicketCategory.SoftwareIssue] = new[]
        {
            "software", "application", "app", "program", "install", "installation",
            "uninstall", "update", "upgrade", "crash", "crashes", "error", "errors",
            "bug", "license", "activation", "office", "word", "excel", "outlook",
            "teams", "zoom", "adobe", "browser", "chrome", "firefox", "edge",
            "slow", "freezing", "frozen", "not responding", "blue screen", "bsod",
            "driver", "operating system", "windows", "macos", "patch"
        },
        [TicketCategory.NetworkIssue] = new[]
        {
            "vpn", "network", "internet", "wifi", "wi-fi", "wireless", "ethernet",
            "connection", "connectivity", "firewall", "dns", "dhcp", "ip address",
            "bandwidth", "slow internet", "no internet", "network drive", "mapped drive",
            "remote access", "remote desktop", "rdp", "switch", "router", "cable"
        },
        [TicketCategory.SecurityIncident] = new[]
        {
            "security", "phishing", "phish", "suspicious", "hack", "hacked",
            "virus", "malware", "ransomware", "spyware", "trojan", "spam",
            "unauthorized", "breach", "password reset", "account locked",
            "compromised", "scam", "fraud", "social engineering", "2fa", "mfa"
        },
        [TicketCategory.EmployeeIssue] = new[]
        {
            "onboarding", "new employee", "new hire", "offboarding", "termination",
            "terminated", "access request", "new user", "user setup", "account setup",
            "leave", "absence", "transfer", "promotion", "department change",
            "badge", "id card", "equipment request", "role change"
        },
        [TicketCategory.ServiceRequest] = new[]
        {
            "request", "order", "setup", "configure", "configuration", "provision",
            "provisioning", "access", "permission", "grant", "create account",
            "new account", "service", "question", "help", "how to", "assistance"
        },
    };

    /// <summary>
    /// Scans the email subject and body for keywords and returns the best-matching
    /// <see cref="TicketCategory"/>. Loads active keywords from the database; falls
    /// back to the hardcoded dictionary when the table is empty or unavailable.
    /// </summary>
    public async Task<TicketCategory> DetectCategoryAsync(string subject, string body)
    {
        try
        {
            var dbKeywords = await _context.CategoryKeywords
                .Where(k => k.IsActive)
                .Select(k => new { k.Category, k.Keyword })
                .ToListAsync();

            if (dbKeywords.Count > 0)
            {
                var text = $"{subject} {body}".ToLowerInvariant();
                var scores = dbKeywords
                    .GroupBy(k => k.Category)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Count(k => text.Contains(k.Keyword)));

                var best = scores.OrderByDescending(kv => kv.Value).First();
                return best.Value > 0 ? best.Key : TicketCategory.Other;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load keywords from DB; falling back to hardcoded dictionary.");
        }

        // Fallback: use hardcoded dictionary
        return DetectCategory(subject, body);
    }

    /// <summary>
    /// Scans the email subject and body for keywords and returns the best-matching
    /// <see cref="TicketCategory"/>. Returns <see cref="TicketCategory.Other"/> when
    /// no keywords match. Uses the hardcoded fallback dictionary.
    /// </summary>
    public static TicketCategory DetectCategory(string subject, string body)
    {
        var text = $"{subject} {body}".ToLowerInvariant();

        // Score each category by counting keyword hits
        var scores = new Dictionary<TicketCategory, int>();
        foreach (var (category, keywords) in CategoryKeywords)
        {
            scores[category] = keywords.Count(kw => text.Contains(kw));
        }

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return best.Value > 0 ? best.Key : TicketCategory.Other;
    }
}
