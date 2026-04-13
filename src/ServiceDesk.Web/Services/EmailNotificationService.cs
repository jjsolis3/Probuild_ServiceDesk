using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

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

    // ── Branding & template helpers ───────────────────────────────────────────

    /// <summary>
    /// Loads CompanyName and BrandColor from AppSettings, with sensible defaults.
    /// </summary>
    private async Task<(string CompanyName, string BrandColor)> GetBrandingAsync()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Key == "CompanyName" || s.Key == "BrandColor")
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);
        return (
            settings.GetValueOrDefault("CompanyName", "ServiceSphere"),
            settings.GetValueOrDefault("BrandColor", "#4f46e5")
        );
    }

    /// <summary>
    /// Loads an active email template by key; returns null if not found or has no custom body.
    /// </summary>
    private async Task<ServiceDesk.Core.Models.EmailTemplate?> GetTemplateAsync(string key)
        => await _context.EmailTemplates.FirstOrDefaultAsync(t => t.Key == key && t.IsActive);

    /// <summary>
    /// Replaces {{Token}} placeholders in a template string.
    /// </summary>
    private static string ApplyTokens(string template, Dictionary<string, string> tokens)
    {
        foreach (var (k, v) in tokens)
            template = template.Replace("{{" + k + "}}", v ?? string.Empty, StringComparison.Ordinal);
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
        var (companyName, brandColor) = await GetBrandingAsync();
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

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
            _logger.LogInformation("Sent ticket confirmation for #{TicketId} to {Email}", ticket.Id, recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send ticket confirmation for #{TicketId}", ticket.Id);
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
        var (companyName, brandColor) = await GetBrandingAsync();
        var tmpl = await GetTemplateAsync("TicketAssigned");

        var assigneeName = ticket.AssignedTo.FirstName + " " + ticket.AssignedTo.LastName;
        var tokens = new Dictionary<string, string>
        {
            ["TicketId"]          = ticket.Id.ToString(),
            ["TicketTitle"]       = System.Net.WebUtility.HtmlEncode(ticket.Title),
            ["TicketPriority"]    = ticket.Priority.ToString(),
            ["TicketCategory"]    = ticket.Category.GetDisplayName(),
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
                <tr><td style='padding:8px;font-weight:bold;'>Category</td><td style='padding:8px;'>{ticket.Category.GetDisplayName()}</td></tr>
            </table>
            <p><strong>Description:</strong></p>
            <div style='background:#f9fafb;padding:12px;border-radius:6px;margin:10px 0;'>{ticket.Description}</div>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, ticket.AssignedTo.Email, subject, htmlBody, ticket.Id, inReplyTo, references);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send assignment notification for #{TicketId}", ticket.Id);
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
        var (companyName, brandColor) = await GetBrandingAsync();
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

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send update notification for #{TicketId}", ticket.Id);
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
        var (companyName, brandColor) = await GetBrandingAsync();
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

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, ticket.Id, inReplyTo, references);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send note notification for #{TicketId}", ticket.Id);
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

        var (companyName, brandColor) = await GetBrandingAsync();
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

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, recipientEmail, subject, htmlBody, null, null, null);
            _logger.LogInformation("Sent password reset email to {Email}", recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", recipientEmail);
        }
    }

    /// <summary>
    /// Builds a branded HTML email wrapper. Company name and brand colour come from AppSettings.
    /// </summary>
    private static string BuildHtmlEmail(string innerContent, string companyName, string brandColor)
    {
        var encodedCompany = System.Net.WebUtility.HtmlEncode(companyName);
        return $@"<!DOCTYPE html>
<html>
<body style='margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Arial, sans-serif;'>
    <div style='max-width: 600px; margin: 0 auto;'>
        <div style='background: linear-gradient(135deg, {brandColor} 0%, {brandColor}cc 100%); color: white; padding: 24px 20px; border-radius: 8px 8px 0 0;'>
            <h2 style='margin: 0; font-size: 20px;'>{encodedCompany}</h2>
            <p style='margin: 4px 0 0; opacity: 0.85; font-size: 13px;'>IT Service Desk</p>
        </div>
        <div style='padding: 24px 20px; border: 1px solid #e5e7eb; border-top: none; border-radius: 0 0 8px 8px; background: #ffffff;'>
            {innerContent}
            <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 24px 0 16px;' />
            <p style='color: #9ca3af; font-size: 11px; margin: 0;'>
                This is an automated notification from {encodedCompany}.
                Replies to this email are processed automatically and attached to the relevant ticket.
            </p>
        </div>
    </div>
</body>
</html>";
    }
}
