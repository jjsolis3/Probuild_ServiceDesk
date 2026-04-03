using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
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
    /// Evaluation order (first match wins):
    ///   1. Rules matching BOTH category AND branch  (most specific)
    ///   2. Rules matching category only             (no branch filter)
    ///   3. Rules matching branch only               (no category filter)
    ///   4. Catch-all rules (no category, no branch)
    ///   5. <paramref name="defaultAssigneeId"/> from the email configuration
    ///
    /// Within each tier, rules are sorted by <c>SortOrder</c> ascending.
    /// </summary>
    /// <param name="category">Detected ticket category.</param>
    /// <param name="branchId">Branch of the submitting employee, or null.</param>
    /// <param name="defaultAssigneeId">Fallback from the email configuration.</param>
    public async Task<int?> ResolveAsync(
        TicketCategory category,
        int? branchId,
        int? defaultAssigneeId)
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

        // Tier 1: category + branch both match
        if (branchId.HasValue)
        {
            var match = activeRules.FirstOrDefault(r =>
                r.Category == category && r.BranchId == branchId.Value);
            if (match != null)
            {
                _logger.LogInformation(
                    "Assignment rule '{Name}' (category+branch) matched for category={Category}, branch={BranchId} → assignee {AssigneeId}",
                    match.Name, category, branchId, match.AssigneeId);
                return match.AssigneeId;
            }
        }

        // Tier 2: category matches, rule has no branch restriction
        var categoryOnlyMatch = activeRules.FirstOrDefault(r =>
            r.Category == category && r.BranchId == null);
        if (categoryOnlyMatch != null)
        {
            _logger.LogInformation(
                "Assignment rule '{Name}' (category-only) matched for category={Category} → assignee {AssigneeId}",
                categoryOnlyMatch.Name, category, categoryOnlyMatch.AssigneeId);
            return categoryOnlyMatch.AssigneeId;
        }

        // Tier 3: branch matches, rule has no category restriction
        if (branchId.HasValue)
        {
            var branchOnlyMatch = activeRules.FirstOrDefault(r =>
                r.Category == null && r.BranchId == branchId.Value);
            if (branchOnlyMatch != null)
            {
                _logger.LogInformation(
                    "Assignment rule '{Name}' (branch-only) matched for branch={BranchId} → assignee {AssigneeId}",
                    branchOnlyMatch.Name, branchId, branchOnlyMatch.AssigneeId);
                return branchOnlyMatch.AssigneeId;
            }
        }

        // Tier 4: catch-all rule (no category, no branch)
        var catchAll = activeRules.FirstOrDefault(r =>
            r.Category == null && r.BranchId == null);
        if (catchAll != null)
        {
            _logger.LogInformation(
                "Assignment rule '{Name}' (catch-all) matched → assignee {AssigneeId}",
                catchAll.Name, catchAll.AssigneeId);
            return catchAll.AssigneeId;
        }

        // Tier 5: email config default
        _logger.LogDebug(
            "No assignment rule matched for category={Category}, branch={BranchId} — using default assignee {DefaultAssigneeId}",
            category, branchId, defaultAssigneeId);
        return defaultAssigneeId;
    }

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
    /// <see cref="TicketCategory"/>. Returns <see cref="TicketCategory.Other"/> when
    /// no keywords match.
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
