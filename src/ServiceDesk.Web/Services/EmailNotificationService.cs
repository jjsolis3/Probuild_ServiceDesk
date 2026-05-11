using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Web.Services;

public class EmailNotificationService
{
    private readonly ServiceDeskDbContext _context;
    private readonly ILogger<EmailNotificationService> _logger;
    private readonly GmailApiService _gmailApiService;

    public EmailNotificationService(
        ServiceDeskDbContext context,
        ILogger<EmailNotificationService> logger,
        GmailApiService gmailApiService)
    {
        _context = context;
        _logger = logger;
        _gmailApiService = gmailApiService;
    }

    // ── Category name lookup ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves a category ID to its display name by querying the TicketCategories
    /// table. Falls back to enum name for system categories (0-7) when the DB lookup
    /// fails, and to "Category N" for unknown custom categories.
    /// </summary>
    private async Task<string> GetCategoryNameAsync(int categoryId)
    {
        try
        {
            var cat = await _context.TicketCategories.FindAsync(categoryId);
            if (cat != null) return cat.Name;
        }
        catch { /* table may not exist yet on fresh install */ }

        if (Enum.IsDefined(typeof(TicketCategory), categoryId))
            return ((TicketCategory)categoryId).GetDisplayName();
        return $"Category {categoryId}";
    }

    // ── Branding & template helpers ───────────────────────────────────────────

    /// <summary>
    /// Loads branding values from AppSettings used to compose the email wrapper.
    /// </summary>
    private async Task<(string CompanyName, string BrandColor, string LogoUrl, string Tagline, string FooterText, bool ShowLogo)> GetBrandingAsync()
    {
        var keys = new[] { "CompanyName", "BrandColor", "CompanyLogoUrl", "EmailHeaderTagline", "EmailFooterText", "EmailShowLogo" };
        var settings = await _context.AppSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);
        return (
            settings.GetValueOrDefault("CompanyName", "ServiceSphere"),
            settings.GetValueOrDefault("BrandColor", "#4f46e5"),
            settings.GetValueOrDefault("CompanyLogoUrl", string.Empty),
            settings.GetValueOrDefault("EmailHeaderTagline", "IT Service Desk"),
            settings.GetValueOrDefault("EmailFooterText", string.Empty),
            !settings.GetValueOrDefault("EmailShowLogo", "true").Equals("false", StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Loads an active email template by key; returns null if not found or has no custom body.
    /// </summary>
    private async Task<ServiceDesk.Core.Models.EmailTemplate?> GetTemplateAsync(string key)
        => await _context.EmailTemplates.FirstOrDefaultAsync(t => t.Key == key && t.IsActive);

    /// <summary>
    /// Replaces token placeholders in a template string.
    /// Supports both {{Token}} (current seed format) and {Token} (legacy DB records).
    /// Double-brace is tried first so it is never confused with single-brace leftovers.
    /// </summary>
    private static string ApplyTokens(string template, Dictionary<string, string> tokens)
    {
        foreach (var (k, v) in tokens)
        {
            template = template
                .Replace("{{" + k + "}}", v ?? string.Empty, StringComparison.Ordinal)
                .Replace("{" + k + "}", v ?? string.Empty, StringComparison.Ordinal);
        }
        return template;
    }

    /// <summary>
    /// Gets the active email config for sending.
    /// </summary>
    private async Task<EmailConfiguration?> GetActiveConfig()
    {
        return await _context.EmailConfigurations
            .FirstOrDefaultAsync(c => c.IsActive && c.IsAuthorized && c.GmailRefreshToken != null);
    }

    /// <summary>
    /// Builds the threading headers by finding the most recent outbound email for a ticket.
    /// </summary>
    private async Task<(string? InReplyTo, string? References)> GetThreadingHeaders(int ticketId)
    {
        // Get the most recent email for this ticket to chain references
        var lastEmail = await _context.TicketEmails
            .Where(te => te.TicketId == ticketId)
            .OrderByDescending(te => te.ProcessedDate)
            .FirstOrDefaultAsync();

        if (lastEmail == null) return (null, null);

        var inReplyTo = lastEmail.MessageId;
        var references = lastEmail.References;

        // Build references chain
        if (!string.IsNullOrEmpty(lastEmail.MessageId))
        {
            references = string.IsNullOrEmpty(references)
                ? lastEmail.MessageId
                : $"{references} {lastEmail.MessageId}";
        }

        return (inReplyTo, references);
    }

    /// <summary>
    /// Generates the threaded subject line with ticket reference.
    /// Format: [#SS-12345] Original Ticket Title
    /// </summary>
    private static string BuildThreadedSubject(Ticket ticket, string? prefix = null)
    {
        var baseSubject = $"[#SS-{ticket.Id}] {ticket.Title}";
        return prefix != null ? $"{prefix} {baseSubject}" : baseSubject;
    }

    private async Task LogNotificationAsync(string type, string recipientEmail, string? recipientName,
        string? subject, int? ticketId, bool success, string? errorMessage = null)
    {
        try
        {
            _context.NotificationLogs.Add(new Core.Models.NotificationLog
            {
                NotificationType = type,
                RecipientEmail   = recipientEmail,
                RecipientName    = recipientName,
                Subject          = subject,
                TicketId         = ticketId,
                Success          = success,
                ErrorMessage     = errorMessage,
                SentDate         = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write notification log entry");
        }
    }

    /// <summary>
    /// Returns true when a notification trigger key is enabled in AppSettings.
    /// Defaults to true if the key has never been seeded (safe fallback).
    /// </summary>
    private async Task<bool> IsNotificationEnabled(string key)
    {
        var setting = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting == null || setting.Value?.ToLower() == "true";
    }

    /// <summary>
    /// Sends a confirmation email when a new ticket is created from an inbound email.
    /// Includes the [#SS-XXXXX] reference so future replies thread correctly.
    /// </summary>
    public async Task SendTicketCreatedConfirmation(Ticket ticket, string recipientEmail, string recipientName)
    {
        if (!await IsNotificationEnabled("NotifyOnTicketCreated")) return;

        var config = await GetActiveConfig();
        if (config == null)
        {
            _logger.LogWarning("No active Gmail configuration. Cannot send confirmation.");
            return;
        }

        var (inReplyTo, references) = await GetThreadingHeaders(ticket.Id);
        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("TicketCreated");

        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]       = ticket.Id.ToString(),
            ["TicketTitle"]    = System.Net.WebUtility.HtmlEncode(ticket.Title),
            ["TicketPriority"] = ticket.Priority.ToString(),
            ["TicketStatus"]   = ticket.Status.ToString(),
            ["RecipientName"]  = System.Net.WebUtility.HtmlEncode(recipientName),
            ["CompanyName"]    = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : BuildThreadedSubject(ticket);

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>Ticket Received — #{ticket.Id}</h3>
            <p>Hi {System.Net.WebUtility.HtmlEncode(recipientName)},</p>
            <p>We've received your request and created ticket <strong>[#SS-{ticket.Id}]</strong>.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:120px;'>Ticket #</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>SS-{ticket.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Subject</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(ticket.Title)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Priority</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{ticket.Priority}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Status</td><td style='padding:8px;'>{ticket.Status}</td></tr>
            </table>
            <p>To add information to this ticket, simply <strong>reply to this email</strong>. Your reply will be automatically attached to ticket [#SS-{ticket.Id}].</p>
            <p style='color:#6b7280;font-size:13px;'>Please keep <strong>[#SS-{ticket.Id}]</strong> in the subject line so we can track your conversation.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
            _logger.LogInformation("Sent ticket confirmation for #{TicketId} to {Email}", ticket.Id, recipientEmail);
            await LogNotificationAsync("TicketCreated", recipientEmail, recipientName, subject, ticket.Id, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send ticket confirmation for #{TicketId}", ticket.Id);
            await LogNotificationAsync("TicketCreated", recipientEmail, recipientName, subject, ticket.Id, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends notification when a ticket is assigned to an agent.
    /// </summary>
    public async Task NotifyTicketAssigned(Ticket ticket)
    {
        if (ticket.AssignedTo == null) return;
        if (!await IsNotificationEnabled("NotifyOnAssignment")) return;

        var config = await GetActiveConfig();
        if (config == null) return;

        var (inReplyTo, references) = await GetThreadingHeaders(ticket.Id);
        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("TicketAssigned");

        var assigneeName = ticket.AssignedTo.FirstName + " " + ticket.AssignedTo.LastName;
        var categoryName = await GetCategoryNameAsync(ticket.Category);
        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]          = ticket.Id.ToString(),
            ["TicketTitle"]       = System.Net.WebUtility.HtmlEncode(ticket.Title),
            ["TicketPriority"]    = ticket.Priority.ToString(),
            ["TicketCategory"]    = categoryName,
            ["TicketDescription"] = ticket.Description ?? string.Empty,
            ["AssigneeName"]      = System.Net.WebUtility.HtmlEncode(assigneeName),
            ["CompanyName"]       = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : BuildThreadedSubject(ticket, "Assigned:");

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>New Ticket Assigned to You</h3>
            <p>Hi {System.Net.WebUtility.HtmlEncode(assigneeName)},</p>
            <p>A new ticket has been assigned to you.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:120px;'>Ticket #</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>SS-{ticket.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Title</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(ticket.Title)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Priority</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{ticket.Priority}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Category</td><td style='padding:8px;'>{categoryName}</td></tr>
            </table>
            <p><strong>Description:</strong></p>
            <div style='background:#f9fafb;padding:12px;border-radius:6px;margin:10px 0;'>{ticket.Description}</div>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        var assigneeEmail = ticket.AssignedTo.Email;
        var assigneeName2 = ticket.AssignedTo.FirstName + " " + ticket.AssignedTo.LastName;
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, assigneeEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
            await LogNotificationAsync("TicketAssigned", assigneeEmail, assigneeName2, subject, ticket.Id, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send assignment notification for #{TicketId}", ticket.Id);
            await LogNotificationAsync("TicketAssigned", assigneeEmail, assigneeName2, subject, ticket.Id, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends notification when a ticket status is updated.
    /// </summary>
    public async Task NotifyTicketUpdated(Ticket ticket, string recipientEmail, string? updateMessage = null)
    {
        // Check the appropriate setting — escalation messages bypass the status-change gate
        var isEscalation = updateMessage?.StartsWith("Ticket has been escalated") == true;
        var settingKey   = isEscalation ? "NotifyOnEscalation" : "NotifyOnStatusChange";
        if (!await IsNotificationEnabled(settingKey)) return;

        var config = await GetActiveConfig();
        if (config == null) return;

        var (inReplyTo, references) = await GetThreadingHeaders(ticket.Id);
        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("TicketUpdated");

        var resolutionRow = !string.IsNullOrEmpty(ticket.ResolutionNotes)
            ? $"<tr><td style='padding:8px;font-weight:bold;'>Resolution</td><td style='padding:8px;'>{System.Net.WebUtility.HtmlEncode(ticket.ResolutionNotes)}</td></tr>"
            : "";
        var updateBlock = !string.IsNullOrEmpty(updateMessage)
            ? $"<div style='background:#f0fdf4;padding:12px;border-radius:6px;border-left:4px solid #22c55e;margin:15px 0;'><strong>Update:</strong> {System.Net.WebUtility.HtmlEncode(updateMessage)}</div>"
            : "";

        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]       = ticket.Id.ToString(),
            ["TicketTitle"]    = System.Net.WebUtility.HtmlEncode(ticket.Title),
            ["TicketStatus"]   = ticket.Status.ToString(),
            ["TicketPriority"] = ticket.Priority.ToString(),
            ["ResolutionRow"]  = resolutionRow,
            ["UpdateBlock"]    = updateBlock,
            ["CompanyName"]    = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : BuildThreadedSubject(ticket, "Updated:");

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>Ticket Update — [#SS-{ticket.Id}]</h3>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:120px;'>Ticket #</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>SS-{ticket.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Title</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(ticket.Title)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Status</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{ticket.Status}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Priority</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{ticket.Priority}</td></tr>
                {resolutionRow}
            </table>
            {updateBlock}
            <p style='color:#6b7280;font-size:13px;'>Reply to this email to add comments to ticket [#SS-{ticket.Id}].</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
            await LogNotificationAsync("TicketUpdated", recipientEmail, null, subject, ticket.Id, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send update notification for #{TicketId}", ticket.Id);
            await LogNotificationAsync("TicketUpdated", recipientEmail, null, subject, ticket.Id, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends notification when a note/comment is added to a ticket.
    /// </summary>
    public async Task NotifyNoteAdded(Ticket ticket, TicketNote note, string recipientEmail)
    {
        if (!await IsNotificationEnabled("NotifyOnNoteAdded")) return;

        var config = await GetActiveConfig();
        if (config == null) return;

        var (inReplyTo, references) = await GetThreadingHeaders(ticket.Id);
        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("NoteAdded");

        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]     = ticket.Id.ToString(),
            ["TicketTitle"]  = System.Net.WebUtility.HtmlEncode(ticket.Title),
            ["NoteAuthor"]   = System.Net.WebUtility.HtmlEncode(note.AuthorName ?? "IT Support"),
            ["NoteContent"]  = note.Content ?? string.Empty,
            ["CompanyName"]  = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : BuildThreadedSubject(ticket, "Re:");

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>New Comment on [#SS-{ticket.Id}]</h3>
            <p><strong>{System.Net.WebUtility.HtmlEncode(note.AuthorName ?? "IT Support")}</strong> added a comment:</p>
            <div style='background:#f9fafb;padding:12px;border-radius:6px;border-left:4px solid #4f46e5;margin:15px 0;'>{note.Content}</div>
            <p style='color:#6b7280;font-size:13px;'>Reply to this email to continue the conversation on ticket [#SS-{ticket.Id}].</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
            await LogNotificationAsync("NoteAdded", recipientEmail, null, subject, ticket.Id, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send note notification for #{TicketId}", ticket.Id);
            await LogNotificationAsync("NoteAdded", recipientEmail, null, subject, ticket.Id, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends a password reset link to the user's email address.
    /// </summary>
    public async Task SendPasswordResetEmail(string recipientEmail, string recipientName, string resetUrl)
    {
        var config = await GetActiveConfig();
        if (config == null)
        {
            _logger.LogWarning("No active email configuration — cannot send password reset to {Email}", recipientEmail);
            return;
        }

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("PasswordReset");

        var tokens = new Dictionary<string, string>
        {
            ["RecipientName"] = System.Net.WebUtility.HtmlEncode(recipientName),
            ["ResetUrl"]      = resetUrl,
            ["CompanyName"]   = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : $"Reset Your {companyName} Password";

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>Password Reset Request</h3>
            <p>Hi {System.Net.WebUtility.HtmlEncode(recipientName)},</p>
            <p>We received a request to reset the password for your {System.Net.WebUtility.HtmlEncode(companyName)} account.</p>
            <p style='margin:24px 0;'>
                <a href='{resetUrl}' style='background:{brandColor};color:white;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block;'>
                    Reset My Password
                </a>
            </p>
            <p style='color:#6b7280;font-size:13px;'>This link expires in <strong>1 hour</strong>. If you did not request a password reset, you can safely ignore this email — your password will not change.</p>
            <p style='color:#6b7280;font-size:12px;'>If the button above doesn't work, copy and paste this URL into your browser:<br/>
                <a href='{resetUrl}' style='color:{brandColor};'>{resetUrl}</a></p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, null, null, null);
            _logger.LogInformation("Sent password reset email to {Email}", recipientEmail);
            await LogNotificationAsync("PasswordReset", recipientEmail, recipientName, subject, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", recipientEmail);
            await LogNotificationAsync("PasswordReset", recipientEmail, recipientName, subject, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends a test email for the given template key, filling all tokens with
    /// sample values so the recipient can see an accurate rendered preview.
    /// Returns (success, message) so the caller can surface the result to the UI.
    /// </summary>
    public async Task<(bool Success, string Message)> SendTestEmailAsync(
        string templateKey,
        string? customBody,
        string? customSubject,
        string recipientEmail)
    {
        var config = await GetActiveConfig();
        if (config == null)
            return (false, "No active, authorized email configuration found. Set one up in Email Integration first.");

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]          = "1042",
            ["TicketTitle"]       = "Sample Ticket — Test Preview",
            ["TicketStatus"]      = "Open",
            ["TicketPriority"]    = "High",
            ["TicketCategory"]    = "Software Issue",
            ["TicketDescription"] = "This is a sample description used to preview the email template.",
            ["RecipientName"]     = "Test Recipient",
            ["AssigneeName"]      = "Support Agent",
            ["NoteAuthor"]        = "Support Agent",
            ["NoteContent"]       = "This is a sample comment used to preview the template.",
            ["CompanyName"]       = companyName,
            ["ResetUrl"]          = "https://example.com/reset-password",
            ["ResolutionRow"]     = string.Empty,
            ["UpdateBlock"]       = string.Empty,
        };

        var subject = !string.IsNullOrWhiteSpace(customSubject)
            ? $"[TEST] {ApplyTokens(customSubject, tokens)}"
            : $"[TEST] Email Template — {templateKey}";

        var innerContent = !string.IsNullOrWhiteSpace(customBody)
            ? ApplyTokens(customBody, tokens)
            : $"<p><em>This is a test send for the <strong>{templateKey}</strong> template. No custom body is set — the system default will be used when this email is actually triggered.</em></p>";

        // Add a test banner so recipients know this is not a real notification
        var testBanner = "<div style='background:#fef3c7;border:1px solid #f59e0b;border-radius:6px;padding:10px 14px;margin-bottom:16px;font-size:13px;color:#92400e;'>"
            + "<strong>Test Email</strong> — This message was sent from the ServiceDesk email template preview. Sample data is used in place of real ticket values.</div>";

        var htmlBody = BuildHtmlEmail(testBanner + innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);

        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, null, null, null);
            _logger.LogInformation("Sent test email for template '{Key}' to {Email}", templateKey, recipientEmail);
            return (true, $"Test email sent successfully to {recipientEmail}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send test email for template '{Key}' to {Email}", templateKey, recipientEmail);
            return (false, $"Send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a store order confirmation to the user who placed the order.
    /// </summary>
    /// <param name="baseUrl">Optional absolute base URL (e.g. "https://portal.example.com")
    /// used to build absolute image src and a "View Order" link. Empty string disables both.</param>
    public async Task SendStoreOrderConfirmationAsync(
        ServiceDesk.Core.Models.StoreOrder order,
        string recipientEmail,
        string recipientName,
        List<ServiceDesk.Core.Models.StoreOrderItem> items,
        string baseUrl = "")
    {
        var config = await GetActiveConfig();
        if (config == null)
        {
            _logger.LogWarning("[Store] No active Gmail configuration. Cannot send order confirmation for #{OrderId}.", order.Id);
            return;
        }

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("StoreOrderConfirmation");

        var itemsHtml = BuildStoreOrderItemsTable(items, baseUrl, showPricing: true);
        var totalQty   = items.Sum(i => i.Quantity);
        var totalCost  = items.Where(i => i.UnitPriceSnapshot.HasValue)
                              .Sum(i => (i.UnitPriceSnapshot ?? 0m) * i.Quantity);
        var hasPricing = items.Any(i => i.UnitPriceSnapshot.HasValue);

        var viewOrderButton = !string.IsNullOrWhiteSpace(baseUrl)
            ? $@"<p style='margin:20px 0;'>
                    <a href='{baseUrl}/Store/OrderDetail/{order.Id}' style='background:{brandColor};color:white;padding:10px 22px;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block;'>
                        View My Order
                    </a>
                </p>"
            : string.Empty;

        var branchRow = !string.IsNullOrEmpty(order.BranchNameSnapshot)
            ? $@"<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Branch</td>
                     <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(order.BranchNameSnapshot)}</td></tr>"
            : string.Empty;

        var totalsBlock = $@"<div style='background:#f9fafb;border-radius:6px;padding:12px 14px;margin-top:10px;display:flex;justify-content:space-between;'>
            <span style='font-weight:600;color:#374151;'>Total items: {totalQty}</span>"
            + (hasPricing
                ? $@"<span style='font-weight:700;color:#059669;'>Total: {totalCost:C}</span>"
                : "<span style='color:#9ca3af;font-size:13px;'>No-charge order</span>")
            + "</div>";

        var tokens = new Dictionary<string, string>
        {
            ["OrderNumber"]    = order.OrderNumber,
            ["OrderDate"]      = order.OrderDate.ToString("MMMM d, yyyy 'at' h:mm tt") + " UTC",
            ["Quarter"]        = $"Q{order.Quarter} {order.Year}",
            ["RecipientName"]  = System.Net.WebUtility.HtmlEncode(recipientName),
            ["CompanyName"]    = System.Net.WebUtility.HtmlEncode(companyName),
            ["OrderItemsHtml"] = itemsHtml,
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : $"Order Confirmation — {order.OrderNumber}";

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3 style='margin-top:0;'>Order Confirmed — {System.Net.WebUtility.HtmlEncode(order.OrderNumber)}</h3>
            <p>Hi {System.Net.WebUtility.HtmlEncode(recipientName)},</p>
            <p>Thanks for your order! The operations team will review and process it shortly. You'll receive another email when your order status changes.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:130px;'>Order #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(order.OrderNumber)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Date</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{order.OrderDate:MMMM d, yyyy 'at' h:mm tt} UTC</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Quarter</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>Q{order.Quarter} {order.Year}</td></tr>
                {branchRow}
                <tr><td style='padding:8px;font-weight:bold;'>Status</td>
                    <td style='padding:8px;'><span style='background:#fef3c7;color:#92400e;padding:3px 10px;border-radius:12px;font-size:12px;font-weight:600;'>Pending Review</span></td></tr>
            </table>
            <h4 style='margin-top:20px;margin-bottom:6px;'>Items Ordered</h4>
            {itemsHtml}
            {totalsBlock}
            {viewOrderButton}
            <p style='color:#6b7280;font-size:13px;margin-top:20px;'>If you have questions about your order, please contact your operations department.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, null, null, null);
            _logger.LogInformation("[Store] Sent confirmation for order #{OrderNumber} to {Email}", order.OrderNumber, recipientEmail);
            await LogNotificationAsync("StoreOrderConfirmation", recipientEmail, recipientName, subject, null, true);

            // Mark confirmation sent
            order.ConfirmationEmailSent = true;
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send confirmation for order #{OrderNumber} to {Email}", order.OrderNumber, recipientEmail);
            await LogNotificationAsync("StoreOrderConfirmation", recipientEmail, recipientName, subject, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Sends a "new order placed" digest to every active operations user. One
    /// email per recipient; failures for one recipient don't block the others.
    /// </summary>
    public async Task SendStoreOrderOpsNotificationAsync(
        ServiceDesk.Core.Models.StoreOrder order,
        string placedByName,
        List<ServiceDesk.Core.Models.StoreOrderItem> items,
        string baseUrl = "")
    {
        var config = await GetActiveConfig();
        if (config == null)
        {
            _logger.LogWarning("[Store] No active Gmail configuration. Cannot send ops notification for #{OrderId}.", order.Id);
            return;
        }

        // Recipients: explicit Ops Hub users, plus Admins as a fallback so a new
        // store always reaches someone.
        var opsUserIds = await _context.StoreOperationsAccess
            .Where(a => a.IsActive)
            .Select(a => a.PortalUserId)
            .ToListAsync();

        var recipients = await _context.PortalUsers
            .Include(u => u.Role)
            .Where(u => u.IsActive && !string.IsNullOrEmpty(u.Email))
            .Where(u => opsUserIds.Contains(u.Id)
                     || (u.Role != null && u.Role.Name == "Admin"))
            .ToListAsync();

        if (recipients.Count == 0)
        {
            _logger.LogWarning("[Store] No ops recipients configured; skipping ops notification for order #{OrderNumber}.", order.OrderNumber);
            return;
        }

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var itemsHtml  = BuildStoreOrderItemsTable(items, baseUrl, showPricing: true);
        var totalQty   = items.Sum(i => i.Quantity);
        var totalCost  = items.Where(i => i.UnitPriceSnapshot.HasValue)
                              .Sum(i => (i.UnitPriceSnapshot ?? 0m) * i.Quantity);
        var hasPricing = items.Any(i => i.UnitPriceSnapshot.HasValue);

        var hubButton = !string.IsNullOrWhiteSpace(baseUrl)
            ? $@"<p style='margin:18px 0;'>
                    <a href='{baseUrl}/Store/OperationsHub?year={order.Year}&quarter={order.Quarter}&status=Pending' style='background:{brandColor};color:white;padding:10px 22px;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block;'>
                        Open in Operations Hub
                    </a>
                </p>"
            : string.Empty;

        var branchRow = !string.IsNullOrEmpty(order.BranchNameSnapshot)
            ? $@"<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Branch</td>
                     <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(order.BranchNameSnapshot)}</td></tr>"
            : string.Empty;

        var notesBlock = !string.IsNullOrWhiteSpace(order.Notes)
            ? $@"<div style='background:#fef9c3;border-left:4px solid #facc15;padding:10px 12px;border-radius:4px;margin:14px 0;'>
                    <strong style='color:#92400e;font-size:13px;'>Order notes:</strong>
                    <div style='margin-top:4px;color:#374151;'>{System.Net.WebUtility.HtmlEncode(order.Notes)}</div>
                 </div>"
            : string.Empty;

        var totalsBlock = $@"<div style='background:#f9fafb;border-radius:6px;padding:12px 14px;margin-top:10px;display:flex;justify-content:space-between;'>
            <span style='font-weight:600;color:#374151;'>Total items: {totalQty}</span>"
            + (hasPricing
                ? $@"<span style='font-weight:700;color:#059669;'>Total: {totalCost:C}</span>"
                : "<span style='color:#9ca3af;font-size:13px;'>No-charge order</span>")
            + "</div>";

        var subject = $"[New Store Order] {order.OrderNumber} — {placedByName} ({totalQty} item{(totalQty == 1 ? "" : "s")})";

        var innerContent = $@"<h3 style='margin-top:0;'>New Store Order Placed</h3>
            <p>A new store order is awaiting review in the Operations Hub.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:140px;'>Order #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(order.OrderNumber)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Placed by</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(placedByName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Date</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{order.OrderDate:MMMM d, yyyy 'at' h:mm tt} UTC</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Quarter</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>Q{order.Quarter} {order.Year}</td></tr>
                {branchRow}
            </table>
            {notesBlock}
            <h4 style='margin-top:20px;margin-bottom:6px;'>Items Requested</h4>
            {itemsHtml}
            {totalsBlock}
            {hubButton}
            <p style='color:#6b7280;font-size:12px;margin-top:20px;'>You're receiving this because you have access to the Store Operations Hub.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);

        foreach (var rec in recipients)
        {
            try
            {
                await _gmailApiService.SendEmailViaGmailApi(config, _context, rec.Email, subject, htmlBody, null, null, null);
                await LogNotificationAsync("StoreOrderOpsAlert", rec.Email, rec.FullName, subject, null, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Store] Failed to send ops notification for order #{OrderNumber} to {Email}", order.OrderNumber, rec.Email);
                await LogNotificationAsync("StoreOrderOpsAlert", rec.Email, rec.FullName, subject, null, false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Builds the HTML table of order line items used in both the user
    /// confirmation and the ops notification. Renders product thumbnails when a
    /// non-empty <paramref name="baseUrl"/> is supplied so the relative
    /// <c>/uploads/...</c> paths resolve in email clients.
    /// </summary>
    private static string BuildStoreOrderItemsTable(
        List<ServiceDesk.Core.Models.StoreOrderItem> items,
        string baseUrl,
        bool showPricing)
    {
        var hasPricing = showPricing && items.Any(i => i.UnitPriceSnapshot.HasValue);
        var rows = new List<string>();

        foreach (var i in items)
        {
            var variants = new List<string>();
            if (!string.IsNullOrEmpty(i.SelectedGender)) variants.Add(i.SelectedGender);
            if (!string.IsNullOrEmpty(i.SelectedSize))   variants.Add(i.SelectedSize);
            if (!string.IsNullOrEmpty(i.SelectedColor))  variants.Add(i.SelectedColor);
            if (!string.IsNullOrWhiteSpace(i.CustomSelectionsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(i.CustomSelectionsJson);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        variants.Add($"{p.Name}: {p.Value.GetString()}");
                }
                catch { /* malformed — skip */ }
            }
            var variantText = variants.Count > 0
                ? $"<div style='color:#6b7280;font-size:12px;margin-top:3px;'>{System.Net.WebUtility.HtmlEncode(string.Join(" · ", variants))}</div>"
                : string.Empty;

            // Thumbnail: only include when a base URL is provided and the product has an image.
            var imgPath = i.StoreProduct?.ImagePath ?? string.Empty;
            string thumbCell;
            if (!string.IsNullOrWhiteSpace(imgPath) && !string.IsNullOrWhiteSpace(baseUrl))
            {
                var src = imgPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? imgPath
                    : baseUrl.TrimEnd('/') + (imgPath.StartsWith("/") ? imgPath : "/" + imgPath);
                thumbCell = $"<td style='padding:8px;border-bottom:1px solid #e5e7eb;width:54px;'>" +
                            $"<img src='{System.Net.WebUtility.HtmlEncode(src)}' alt='' style='width:46px;height:46px;object-fit:cover;border-radius:6px;border:1px solid #e5e7eb;' /></td>";
            }
            else
            {
                thumbCell = "<td style='padding:8px;border-bottom:1px solid #e5e7eb;width:54px;'></td>";
            }

            var nameCell = "<td style='padding:8px;border-bottom:1px solid #e5e7eb;'>" +
                           $"<div style='font-weight:600;color:#111827;'>{System.Net.WebUtility.HtmlEncode(i.ProductNameSnapshot)}</div>" +
                           (string.IsNullOrEmpty(i.ProductCategorySnapshot)
                               ? string.Empty
                               : $"<div style='color:#9ca3af;font-size:11px;text-transform:uppercase;letter-spacing:.04em;'>{System.Net.WebUtility.HtmlEncode(i.ProductCategorySnapshot)}</div>") +
                           variantText + "</td>";

            var qtyCell = $"<td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:center;font-weight:bold;'>{i.Quantity}</td>";

            var priceCells = string.Empty;
            if (hasPricing)
            {
                var unit = i.UnitPriceSnapshot?.ToString("C") ?? "—";
                var sub  = i.UnitPriceSnapshot.HasValue
                    ? (i.UnitPriceSnapshot.Value * i.Quantity).ToString("C")
                    : "—";
                priceCells = $"<td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;color:#6b7280;'>{unit}</td>" +
                             $"<td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;font-weight:600;'>{sub}</td>";
            }

            rows.Add($"<tr>{thumbCell}{nameCell}{qtyCell}{priceCells}</tr>");
        }

        var priceHeaders = hasPricing
            ? "<th style='padding:8px;text-align:right;'>Price</th><th style='padding:8px;text-align:right;'>Subtotal</th>"
            : string.Empty;

        return "<table style='width:100%;border-collapse:collapse;margin:10px 0;border:1px solid #e5e7eb;border-radius:6px;'>" +
               "<thead><tr style='background:#f3f4f6;'>" +
               "<th style='padding:8px;text-align:left;width:54px;'></th>" +
               "<th style='padding:8px;text-align:left;'>Product</th>" +
               "<th style='padding:8px;text-align:center;'>Qty</th>" +
               priceHeaders +
               "</tr></thead><tbody>" +
               string.Join("\n", rows) +
               "</tbody></table>";
    }

    /// <summary>
    /// Sends an order status update notification to the user who placed the order.
    /// Called by the Operations Hub when ops staff change an order's status.
    /// </summary>
    public async Task SendOrderStatusUpdateAsync(
        ServiceDesk.Core.Models.StoreOrder order,
        string recipientEmail,
        string recipientName,
        string newStatus,
        string baseUrl = "")
    {
        var config = await GetActiveConfig();
        if (config == null)
        {
            _logger.LogWarning("[Store] No active Gmail configuration. Cannot send status update for #{OrderId}.", order.Id);
            return;
        }

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("StoreOrderStatusUpdate");

        var statusMessage = newStatus switch
        {
            "Confirmed" => "Your order has been reviewed and confirmed by the operations team. It is now being processed.",
            "Fulfilled" => "Great news! Your order has been fulfilled. Your items are on their way or ready for pickup.",
            "Cancelled" => "Your order has been cancelled. Please contact the operations team if you have any questions.",
            _           => $"Your order status has been updated to: <strong>{System.Net.WebUtility.HtmlEncode(newStatus)}</strong>."
        };

        var tokens = new Dictionary<string, string>
        {
            ["OrderNumber"]   = order.OrderNumber,
            ["NewStatus"]     = System.Net.WebUtility.HtmlEncode(newStatus),
            ["StatusMessage"] = statusMessage,
            ["Quarter"]       = $"Q{order.Quarter} {order.Year}",
            ["RecipientName"] = System.Net.WebUtility.HtmlEncode(recipientName),
            ["CompanyName"]   = System.Net.WebUtility.HtmlEncode(companyName),
        };

        var subject = tmpl?.SubjectTemplate != null
            ? ApplyTokens(tmpl.SubjectTemplate, tokens)
            : $"Order Update — {order.OrderNumber} is now {newStatus}";

        var viewOrderButton = !string.IsNullOrWhiteSpace(baseUrl)
            ? $@"<p style='margin:20px 0;'>
                    <a href='{baseUrl}/Store/OrderDetail/{order.Id}' style='background:{brandColor};color:white;padding:10px 22px;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block;'>
                        View Order Details
                    </a>
                </p>"
            : string.Empty;

        var innerContent = tmpl?.BodyTemplate != null
            ? ApplyTokens(tmpl.BodyTemplate, tokens)
            : $@"<h3>Order Status Update</h3>
            <p>Hi {System.Net.WebUtility.HtmlEncode(recipientName)},</p>
            <p>{statusMessage}</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:130px;'>Order #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(order.OrderNumber)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Quarter</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>Q{order.Quarter} {order.Year}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>New Status</td>
                    <td style='padding:8px;'><strong>{System.Net.WebUtility.HtmlEncode(newStatus)}</strong></td></tr>
            </table>
            {viewOrderButton}
            <p style='color:#6b7280;font-size:13px;margin-top:20px;'>If you have questions about your order, please contact your operations department.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, null, null, null);
            _logger.LogInformation("[Store] Sent status update ({Status}) for order #{OrderNumber} to {Email}", newStatus, order.OrderNumber, recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send status update for order #{OrderNumber} to {Email}", order.OrderNumber, recipientEmail);
        }
    }

    // ── Payroll notifications ─────────────────────────────────────────────────

    /// <summary>
    /// Notifies the first active Admin user when a contractor submits a payroll receipt.
    /// </summary>
    public async Task NotifyReceiptSubmittedAsync(Core.Models.PayrollReceipt receipt)
    {
        if (!await IsNotificationEnabled("NotifyOnPayrollSubmit")) return;

        var config = await GetActiveConfig();
        if (config == null) return;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "Contractor";

        // Find first Admin user with an email
        var admin = await _context.PortalUsers
            .Include(u => u.Role)
            .Where(u => u.Role != null && u.Role.Name == "Admin" && !string.IsNullOrEmpty(u.Email))
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync();

        if (admin == null)
        {
            _logger.LogWarning("[Payroll] No Admin user found to notify on receipt #{Id} submit.", receipt.Id);
            return;
        }

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var subject = $"Receipt #{receipt.Id} submitted by {contractorName} — {receipt.TotalAmount:C}";

        var innerContent = $@"<h3>New Payroll Receipt Submitted</h3>
            <p>A contractor has submitted a payroll receipt for review.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:140px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Contractor</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(contractorName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Total Hours</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.TotalHours:0.##}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Total Amount</td>
                    <td style='padding:8px;'><strong>{receipt.TotalAmount:C}</strong></td></tr>
            </table>
            <p>Open the <strong>Contractor Payroll</strong> page in ServiceSphere to review, approve, or reject this receipt.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, admin.Email, subject, htmlBody, null, null, null);
            await LogNotificationAsync("PayrollSubmitted", admin.Email, admin.FullName, subject, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Payroll] Failed to notify admin of receipt #{Id} submit", receipt.Id);
            await LogNotificationAsync("PayrollSubmitted", admin.Email, admin.FullName, subject, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Notifies the contractor that their submitted receipt has been approved.
    /// </summary>
    public async Task NotifyReceiptApprovedAsync(Core.Models.PayrollReceipt receipt)
    {
        if (!await IsNotificationEnabled("NotifyOnPayrollApproved")) return;
        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Approved",
            subject: $"Your receipt #{receipt.Id} has been approved",
            heading: "Receipt Approved",
            body: $"Your payroll receipt has been approved and is now scheduled for payment. You will receive a separate confirmation when payment is processed.",
            barColor: "#10b981",
            logType: "PayrollApproved");
    }

    /// <summary>
    /// Notifies the contractor that their receipt was returned for revision with a note.
    /// </summary>
    public async Task NotifyReceiptRejectedAsync(Core.Models.PayrollReceipt receipt)
    {
        if (!await IsNotificationEnabled("NotifyOnPayrollRejected")) return;

        var note = string.IsNullOrWhiteSpace(receipt.RejectionNote)
            ? "Please review and resubmit."
            : receipt.RejectionNote;
        var noteBlock = $@"<div style='background:#fef3c7;border-left:4px solid #f59e0b;padding:12px 14px;border-radius:4px;margin:14px 0;'>
            <strong style='color:#92400e;'>HR Note:</strong>
            <div style='margin-top:6px;color:#78350f;'>{System.Net.WebUtility.HtmlEncode(note)}</div>
        </div>";

        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Returned",
            subject: $"Your receipt #{receipt.Id} was returned for revision",
            heading: "Receipt Returned for Revision",
            body: $"Your payroll receipt has been returned for revision. Please review the note below, update your time entries or receipt as needed, and resubmit.{noteBlock}",
            barColor: "#f59e0b",
            logType: "PayrollRejected");
    }

    /// <summary>
    /// Notifies the contractor that payment has been confirmed.
    /// </summary>
    public async Task NotifyReceiptPaidAsync(Core.Models.PayrollReceipt receipt)
    {
        if (!await IsNotificationEnabled("NotifyOnPayrollPaid")) return;
        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Paid",
            subject: $"Payment confirmed for receipt #{receipt.Id} — {receipt.TotalAmount:C}",
            heading: "Payment Confirmed",
            body: $"Payment for your payroll receipt has been processed. Please allow 1–3 business days for the funds to appear in your account.",
            barColor: "#0ea5e9",
            logType: "PayrollPaid");
    }

    /// <summary>
    /// Shared helper for sending contractor-facing receipt status emails.
    /// </summary>
    private async Task SendContractorReceiptStatusEmailAsync(
        Core.Models.PayrollReceipt receipt,
        string statusLabel,
        string subject,
        string heading,
        string body,
        string barColor,
        string logType)
    {
        var config = await GetActiveConfig();
        if (config == null) return;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        if (receipt.Contractor == null || string.IsNullOrEmpty(receipt.Contractor.Email))
        {
            _logger.LogWarning("[Payroll] Receipt #{Id} contractor missing or has no email; skipping {LogType}.", receipt.Id, logType);
            return;
        }

        var contractorName = $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}";
        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var innerContent = $@"<div style='border-left:4px solid {barColor};padding-left:14px;margin-bottom:16px;'>
                <h3 style='margin:0;color:#111827;'>{heading}</h3>
                <p style='margin:4px 0 0;color:#6b7280;font-size:13px;'>Receipt #{receipt.Id}</p>
            </div>
            <p>Hi {System.Net.WebUtility.HtmlEncode(receipt.Contractor.FirstName)},</p>
            <p>{body}</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:140px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Total Hours</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.TotalHours:0.##}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Total Amount</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'><strong>{receipt.TotalAmount:C}</strong></td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Status</td>
                    <td style='padding:8px;'><span style='background:{barColor};color:white;padding:3px 10px;border-radius:12px;font-size:12px;font-weight:600;'>{statusLabel}</span></td></tr>
            </table>
            <p style='color:#6b7280;font-size:13px;'>Sign in to ServiceSphere to view the full receipt and time entries.</p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, receipt.Contractor.Email, subject, htmlBody, null, null, null);
            await LogNotificationAsync(logType, receipt.Contractor.Email, contractorName, subject, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Payroll] Failed to send {LogType} email for receipt #{Id}", logType, receipt.Id);
            await LogNotificationAsync(logType, receipt.Contractor.Email, contractorName, subject, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Builds a branded HTML email wrapper. Company name and brand colour come from AppSettings.
    /// </summary>
    private static string BuildHtmlEmail(
        string innerContent,
        string companyName,
        string brandColor,
        string logoUrl = "",
        string tagline = "IT Service Desk",
        string footerText = "",
        bool showLogo = true)
    {
        var encodedCompany = System.Net.WebUtility.HtmlEncode(companyName);
        var encodedTagline  = System.Net.WebUtility.HtmlEncode(tagline);
        var encodedFooter   = string.IsNullOrWhiteSpace(footerText)
            ? $"This is an automated notification from {encodedCompany}. Replies to this email are processed automatically and attached to the relevant ticket."
            : System.Net.WebUtility.HtmlEncode(footerText);

        // Logo block: only render if ShowLogo is true and a URL is provided
        var logoBlock = showLogo && !string.IsNullOrWhiteSpace(logoUrl)
            ? $"<div style='margin-bottom:10px;'><img src='{System.Net.WebUtility.HtmlEncode(logoUrl)}' alt='{encodedCompany}' style='max-height:50px;max-width:200px;display:block;' /></div>"
            : string.Empty;

        return $@"<!DOCTYPE html>
<html>
<body style='margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Arial, sans-serif;'>
    <div style='max-width: 600px; margin: 0 auto;'>
        <div style='background: linear-gradient(135deg, {brandColor} 0%, {brandColor}cc 100%); color: white; padding: 24px 20px; border-radius: 8px 8px 0 0;'>
            {logoBlock}
            <h2 style='margin: 0; font-size: 20px;'>{encodedCompany}</h2>
            <p style='margin: 4px 0 0; opacity: 0.85; font-size: 13px;'>{encodedTagline}</p>
        </div>
        <div style='padding: 24px 20px; border: 1px solid #e5e7eb; border-top: none; border-radius: 0 0 8px 8px; background: #ffffff;'>
            {innerContent}
            <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 24px 0 16px;' />
            <p style='color: #9ca3af; font-size: 11px; margin: 0;'>
                {encodedFooter}
            </p>
        </div>
    </div>
</body>
</html>";
    }
}
