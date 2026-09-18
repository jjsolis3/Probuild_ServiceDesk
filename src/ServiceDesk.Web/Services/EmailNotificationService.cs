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
        // Don't gate on IsAuthorized — that flag can be auto-cleared by a
        // transient token-refresh failure in GmailApiService, and the next
        // successful refresh sets it back to true. Outbound sends should keep
        // trying as long as we still hold a refresh token (admin "Revoke"
        // wipes the token, which still excludes the row here).
        return await _context.EmailConfigurations
            .FirstOrDefaultAsync(c => c.IsActive && c.GmailRefreshToken != null);
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
    /// Resolves the configured delivery mode for payroll group-emails.
    /// "Combined" sends one email with the first address in To and the
    /// rest in Cc (recipients see each other and can Reply-All).
    /// Anything else — including missing/blank — means "Individual": one
    /// independent send per recipient, which is privacy-safe and the
    /// default. Returns lower-case strings so callers can string-compare.
    /// </summary>
    private async Task<string> GetPayrollDeliveryModeAsync()
    {
        var setting = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == "PayrollNotificationDeliveryMode");
        var value = setting?.Value?.Trim().ToLowerInvariant();
        return value == "combined" ? "combined" : "individual";
    }

    /// <summary>
    /// Sends a payroll alert to one or more recipients, honoring the
    /// admin-configured delivery mode. "Combined" mode makes a single
    /// Gmail API call (first address as To, rest as Cc) but still logs
    /// one row per recipient so the notification log stays useful when
    /// auditing who saw what. "Individual" mode preserves the original
    /// behavior of one send + one log row per recipient — bouncing on
    /// recipient #3 doesn't block #4.
    /// </summary>
    private async Task SendToPayrollRecipientsAsync(
        EmailConfiguration config,
        IList<(string Email, string Name)> recipients,
        string subject,
        string htmlBody,
        string logType)
    {
        if (recipients.Count == 0) return;

        var mode = await GetPayrollDeliveryModeAsync();

        if (mode == "combined" && recipients.Count > 1)
        {
            var primary = recipients[0];
            var ccList  = string.Join(", ", recipients.Skip(1).Select(r => r.Email));
            try
            {
                await _gmailApiService.SendEmailViaGmailApi(
                    config, _context, primary.Email, subject, htmlBody,
                    ticketId: null, inReplyTo: null, references: null, ccEmail: ccList);
                // Log each recipient — both To and Cc — so the activity
                // log captures the full audience.
                foreach (var r in recipients)
                    await LogNotificationAsync(logType, r.Email, r.Name, subject, null, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Payroll] Combined send failed for {LogType} ({Count} recipients)", logType, recipients.Count);
                foreach (var r in recipients)
                    await LogNotificationAsync(logType, r.Email, r.Name, subject, null, false, ex.Message);
            }
            return;
        }

        foreach (var (email, name) in recipients)
        {
            try
            {
                await _gmailApiService.SendEmailViaGmailApi(config, _context, email, subject, htmlBody, null, null, null);
                await LogNotificationAsync(logType, email, name, subject, null, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Payroll] Failed to send {LogType} to {Email}", logType, email);
                await LogNotificationAsync(logType, email, name, subject, null, false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Same delivery fan-out as <see cref="SendToPayrollRecipientsAsync"/> but routes
    /// through <see cref="GmailApiService.SendEmailWithAttachmentsAsync"/> so a PDF/XLSX
    /// of the receipt can ride along with the notification (e.g. a payment request or
    /// status update, so the recipient sees hard evidence instead of just numbers).
    /// </summary>
    private async Task SendToPayrollRecipientsWithAttachmentsAsync(
        EmailConfiguration config,
        IList<(string Email, string Name)> recipients,
        string subject,
        string htmlBody,
        IList<GmailApiService.EmailAttachment> attachments,
        string logType)
    {
        if (recipients.Count == 0) return;

        var mode = await GetPayrollDeliveryModeAsync();

        if (mode == "combined" && recipients.Count > 1)
        {
            var primary = recipients[0];
            var ccList  = string.Join(", ", recipients.Skip(1).Select(r => r.Email));
            try
            {
                await _gmailApiService.SendEmailWithAttachmentsAsync(
                    config, _context, primary.Email, ccList, subject, htmlBody, attachments);
                foreach (var r in recipients)
                    await LogNotificationAsync(logType, r.Email, r.Name, subject, null, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Payroll] Combined send failed for {LogType} ({Count} recipients)", logType, recipients.Count);
                foreach (var r in recipients)
                    await LogNotificationAsync(logType, r.Email, r.Name, subject, null, false, ex.Message);
            }
            return;
        }

        foreach (var (email, name) in recipients)
        {
            try
            {
                await _gmailApiService.SendEmailWithAttachmentsAsync(config, _context, email, null, subject, htmlBody, attachments);
                await LogNotificationAsync(logType, email, name, subject, null, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Payroll] Failed to send {LogType} to {Email}", logType, email);
                await LogNotificationAsync(logType, email, name, subject, null, false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Renders the itemized "Payments Received" table for a receipt so payment-related
    /// emails (contractor's payment request, admin's status update) show the actual
    /// recorded payments — date, method, reference, amount, confirmation state — rather
    /// than just an aggregate paid/outstanding total.
    /// </summary>
    private async Task<string> BuildPaymentsTableHtmlAsync(int receiptId)
    {
        var payments = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receiptId)
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .ToListAsync();

        if (payments.Count == 0)
        {
            return @"<div style='background:#f9fafb;border-left:4px solid #9ca3af;padding:12px 14px;border-radius:4px;margin:14px 0;color:#4b5563;'>
                    No payments have been recorded against this receipt yet.
                </div>";
        }

        var rows = string.Join(string.Empty, payments.Select(p =>
        {
            var reference = string.Join(" ", new[]
            {
                p.PaymentMethod == "Check" && !string.IsNullOrWhiteSpace(p.CheckNumber) ? $"#{p.CheckNumber}" : null,
                p.Reference
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var confirmed = p.ContractorConfirmedDate.HasValue
                ? $"<span style='color:#166534;'>✓ {p.ContractorConfirmedDate.Value:MMM d, yyyy}</span>"
                : "<span style='color:#b45309;'>Awaiting</span>";

            return $@"<tr>
                <td style='padding:6px 8px;border-bottom:1px solid #e5e7eb;'>{p.PaymentDate:MMM d, yyyy}</td>
                <td style='padding:6px 8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(p.PaymentMethod)}</td>
                <td style='padding:6px 8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(reference)}</td>
                <td style='padding:6px 8px;border-bottom:1px solid #e5e7eb;text-align:right;'>{p.Amount:C}</td>
                <td style='padding:6px 8px;border-bottom:1px solid #e5e7eb;'>{confirmed}</td>
            </tr>";
        }));

        return $@"<div style='margin:14px 0;'>
            <strong style='font-size:.9rem;color:#374151;'>Payments Received ({payments.Count})</strong>
            <table style='width:100%;border-collapse:collapse;margin-top:6px;font-size:.9rem;'>
                <thead>
                    <tr style='background:#f9fafb;'>
                        <th style='padding:6px 8px;text-align:left;border-bottom:2px solid #e5e7eb;'>Date</th>
                        <th style='padding:6px 8px;text-align:left;border-bottom:2px solid #e5e7eb;'>Method</th>
                        <th style='padding:6px 8px;text-align:left;border-bottom:2px solid #e5e7eb;'>Reference</th>
                        <th style='padding:6px 8px;text-align:right;border-bottom:2px solid #e5e7eb;'>Amount</th>
                        <th style='padding:6px 8px;text-align:left;border-bottom:2px solid #e5e7eb;'>Confirmed</th>
                    </tr>
                </thead>
                <tbody>{rows}</tbody>
            </table>
        </div>";
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
            await LogNotificationAsync($"Test:{templateKey}", recipientEmail, null, subject, null, true);
            return (true, $"Test email sent successfully to {recipientEmail}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send test email for template '{Key}' to {Email}", templateKey, recipientEmail);
            await LogNotificationAsync($"Test:{templateKey}", recipientEmail, null, subject, null, false, ex.Message);
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
            "Shipped"   => string.IsNullOrEmpty(order.TrackingNumber)
                              ? "Your order has shipped."
                              : $"Your order has shipped via <strong>{System.Net.WebUtility.HtmlEncode(order.Carrier ?? "carrier")}</strong>. Tracking number: <code>{System.Net.WebUtility.HtmlEncode(order.TrackingNumber)}</code>.",
            "Fulfilled" => "Great news! Your order has been fulfilled. Your items are on their way or ready for pickup.",
            "Cancelled" => string.IsNullOrEmpty(order.CancellationReason)
                              ? "Your order has been cancelled. Please contact the operations team if you have any questions."
                              : $"Your order has been cancelled. Reason: <em>{System.Net.WebUtility.HtmlEncode(order.CancellationReason)}</em>",
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
            await LogNotificationAsync("StoreOrderStatusUpdate", recipientEmail, recipientName, subject, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send status update for order #{OrderNumber} to {Email}", order.OrderNumber, recipientEmail);
            await LogNotificationAsync("StoreOrderStatusUpdate", recipientEmail, recipientName, subject, null, false, ex.Message);
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

        // Recipients are resolved from the PayrollNotificationRecipients
        // configuration table — admin/HR/owner/AP can each be added
        // individually, mixing portal users with free-form external
        // emails. When the table is empty (fresh install or admin hasn't
        // configured it yet) we fall back to "every active Admin" so
        // notifications never silently disappear.
        var configured = await _context.PayrollNotificationRecipients
            .Include(r => r.PortalUser)
            .Where(r => r.IsActive)
            .ToListAsync();

        var recipients = configured
            .Select(r =>
            {
                // Linked portal user wins — picks up rename/email change
                // without us having to sync.
                var email = r.PortalUser?.Email ?? r.Email;
                var name  = r.PortalUser?.FullName
                            ?? r.DisplayName
                            ?? r.Email;
                return (Email: email, Name: name);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0)
        {
            // Fallback: every active Admin with an email on file.
            recipients = await _context.PortalUsers
                .Include(u => u.Role)
                .Where(u => u.IsActive
                         && u.Role != null && u.Role.Name == "Admin"
                         && !string.IsNullOrEmpty(u.Email))
                .Select(u => new ValueTuple<string, string>(u.Email!, u.FirstName + " " + u.LastName))
                .ToListAsync();
        }

        if (recipients.Count == 0)
        {
            _logger.LogWarning("[Payroll] No recipients configured (and no active Admins) — receipt #{Id} submit notification skipped.", receipt.Id);
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
        await SendToPayrollRecipientsAsync(config, recipients, subject, htmlBody, "PayrollSubmitted");
    }

    /// <summary>
    /// <summary>
    /// Notifies the contractor that their submitted receipt has been
    /// approved. When the admin chose to forward a copy in the Approve
    /// modal, the corresponding flags tell the contractor which
    /// downstream team (HR / AP) now has the receipt for processing —
    /// without exposing internal email addresses.
    /// </summary>
    public async Task NotifyReceiptApprovedAsync(Core.Models.PayrollReceipt receipt,
        bool forwardedToHr = false, bool forwardedToAp = false)
    {
        if (!await IsNotificationEnabled("NotifyOnPayrollApproved")) return;

        // Surface the optional approval note inside a styled call-out so the
        // contractor sees the context the admin captured at approval time.
        var noteBlock = string.IsNullOrWhiteSpace(receipt.ApprovalNote)
            ? string.Empty
            : $@"<div style='background:#ecfdf5;border-left:4px solid #10b981;padding:12px 14px;border-radius:4px;margin:14px 0;'>
                    <strong style='color:#047857;'>Note from approver:</strong>
                    <div style='margin-top:6px;color:#065f46;'>{System.Net.WebUtility.HtmlEncode(receipt.ApprovalNote)}</div>
                 </div>";

        // Forwarded-to call-out — only renders if at least one downstream
        // copy actually went out. Naming is generic ("our HR / Accounts
        // Payable team") so we don't leak internal email addresses to
        // the contractor.
        string forwardBlock = string.Empty;
        if (forwardedToHr || forwardedToAp)
        {
            var targets = new List<string>();
            if (forwardedToHr) targets.Add("our <strong>HR</strong> team");
            if (forwardedToAp) targets.Add("our <strong>Accounts Payable</strong> team");
            var targetText = string.Join(" and ", targets);
            forwardBlock = $@"<div style='background:#eef4ff;border-left:4px solid #0d6efd;padding:12px 14px;border-radius:4px;margin:14px 0;'>
                    <strong style='color:#0a58ca;'>Sent for processing</strong>
                    <div style='margin-top:6px;color:#1e40af;'>
                        A copy of your approved receipt has been forwarded to {targetText} for payment processing.
                    </div>
                </div>";
        }

        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Approved",
            subject: $"Your receipt #{receipt.Id} has been approved",
            heading: "Receipt Approved",
            body: $"Your payroll receipt has been approved and is now scheduled for payment. You will receive a separate confirmation when payment is processed.{noteBlock}{forwardBlock}",
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

        // Bundle method + reference into a styled call-out so the
        // contractor has the exact strings they need to find the
        // payment in their bank statement (check #, ACH ID, etc.).
        var detailRows = new List<string>();
        if (!string.IsNullOrWhiteSpace(receipt.PaymentMethod))
            detailRows.Add($"<div><strong>Method:</strong> {System.Net.WebUtility.HtmlEncode(receipt.PaymentMethod)}</div>");
        if (!string.IsNullOrWhiteSpace(receipt.PaymentReference))
            detailRows.Add($"<div><strong>Reference:</strong> <code>{System.Net.WebUtility.HtmlEncode(receipt.PaymentReference)}</code></div>");
        if (receipt.PaidDate.HasValue)
            detailRows.Add($"<div><strong>Issued:</strong> {receipt.PaidDate:MMM d, yyyy}</div>");

        var paymentBlock = detailRows.Count == 0
            ? string.Empty
            : $@"<div style='background:#ecfeff;border-left:4px solid #06b6d4;padding:12px 14px;border-radius:4px;margin:14px 0;color:#0e7490;'>
                    <strong>Payment Details</strong>
                    <div style='margin-top:6px;line-height:1.5;'>{string.Join("", detailRows)}</div>
                 </div>";

        var confirmBlock = receipt.PaymentConfirmedDate.HasValue
            ? string.Empty
            : @"<p style='margin-top:14px;font-size:.9rem;color:#475569;'>
                    Once the funds land in your account, please open the receipt in
                    the portal and click <strong>Confirm Received</strong> so we can
                    close the loop on this payment.
                </p>";

        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Paid",
            subject: $"Payment issued for receipt #{receipt.Id} — {receipt.TotalAmount:C}",
            heading: "Payment Issued",
            body: $"Payment for your payroll receipt has been processed. Please allow 1–3 business days for the funds to appear in your account.{paymentBlock}{confirmBlock}",
            barColor: "#0ea5e9",
            logType: "PayrollPaid");
    }

    /// <summary>
    /// Sends the contractor a "partial payment received" email whenever an
    /// admin records a payment that doesn't fully cover the receipt. The
    /// running total and outstanding balance are called out so the
    /// contractor can reconcile against their bank without opening the app.
    /// </summary>
    public async Task NotifyPartialPaymentAsync(
        Core.Models.PayrollReceipt receipt,
        Core.Models.PayrollReceiptPayment payment,
        decimal totalPaid,
        decimal outstanding)
    {
        // Reuse the same feature flag as the fully-paid notification. Any
        // admin who wants to hear about the fully-paid event will want to
        // hear about partials too.
        if (!await IsNotificationEnabled("NotifyOnPayrollPaid")) return;

        var rows = new List<string>
        {
            $"<div><strong>Payment:</strong> {payment.Amount:C}</div>",
            $"<div><strong>Method:</strong> {System.Net.WebUtility.HtmlEncode(payment.PaymentMethod)}</div>",
        };
        if (!string.IsNullOrWhiteSpace(payment.CheckNumber))
            rows.Add($"<div><strong>Check #:</strong> <code>{System.Net.WebUtility.HtmlEncode(payment.CheckNumber)}</code></div>");
        if (!string.IsNullOrWhiteSpace(payment.Reference))
            rows.Add($"<div><strong>Reference:</strong> <code>{System.Net.WebUtility.HtmlEncode(payment.Reference)}</code></div>");
        rows.Add($"<div><strong>Payment date:</strong> {payment.PaymentDate:MMM d, yyyy}</div>");
        if (!string.IsNullOrWhiteSpace(payment.Note))
            rows.Add($"<div><strong>Note:</strong> {System.Net.WebUtility.HtmlEncode(payment.Note)}</div>");

        var paymentBlock =
            $@"<div style='background:#ecfeff;border-left:4px solid #06b6d4;padding:12px 14px;border-radius:4px;margin:14px 0;color:#0e7490;'>
                    <strong>Payment Details</strong>
                    <div style='margin-top:6px;line-height:1.5;'>{string.Join("", rows)}</div>
               </div>";

        var runningBlock =
            $@"<div style='background:#fef3c7;border-left:4px solid #f59e0b;padding:12px 14px;border-radius:4px;margin:14px 0;color:#78350f;'>
                    <strong>Running total on receipt #{receipt.Id}</strong>
                    <div style='margin-top:6px;line-height:1.5;'>
                        <div>Paid so far: <strong>{totalPaid:C}</strong></div>
                        <div>Receipt total: <strong>{receipt.TotalAmount:C}</strong></div>
                        <div style='color:#b45309;'>Outstanding: <strong>{outstanding:C}</strong></div>
                    </div>
               </div>";

        await SendContractorReceiptStatusEmailAsync(receipt,
            statusLabel: "Partially Paid",
            subject: $"Partial payment received on receipt #{receipt.Id} — {payment.Amount:C} of {receipt.TotalAmount:C}",
            heading: "Partial Payment Received",
            body: $"A partial payment has been recorded against your payroll receipt. Details below.{paymentBlock}{runningBlock}<p style='margin-top:14px;font-size:.9rem;color:#475569;'>Once the funds land in your account, open the receipt in the portal and click <strong>Confirm Received</strong> next to this row so the admin knows it's cleared.</p>",
            barColor: "#f59e0b",
            logType: "PayrollPartialPaid");
    }

    /// <summary>
    /// Daily nudge to the configured recipients for any Submitted receipt
    /// that's been sitting unapproved past the grace period. Reuses the
    /// PayrollNotificationRecipients table for routing — same audience
    /// that gets the initial submit notification.
    /// </summary>
    public async Task SendStaleReceiptReminderAsync(Core.Models.PayrollReceipt receipt, int graceDays, int? elapsedBusinessDays = null)
    {
        var config = await GetActiveConfig();
        if (config == null) return;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "Contractor";

        // Prefer the caller-supplied business-day count; fall back to the
        // grace threshold so the email still reads sensibly if the
        // reminder service ever invokes us without that argument.
        var pendingBizDays = elapsedBusinessDays ?? graceDays;

        var configured = await _context.PayrollNotificationRecipients
            .Include(r => r.PortalUser)
            .Where(r => r.IsActive)
            .ToListAsync();

        var recipients = configured
            .Select(r => (Email: r.PortalUser?.Email ?? r.Email,
                          Name:  r.PortalUser?.FullName ?? r.DisplayName ?? r.Email))
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0)
        {
            recipients = await _context.PortalUsers
                .Include(u => u.Role)
                .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin"
                         && !string.IsNullOrEmpty(u.Email))
                .Select(u => new ValueTuple<string, string>(u.Email!, u.FirstName + " " + u.LastName))
                .ToListAsync();
        }
        if (recipients.Count == 0) return;

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var dayWord = pendingBizDays == 1 ? "business day" : "business days";
        var subject = $"Reminder: Receipt #{receipt.Id} has been awaiting approval for {pendingBizDays} {dayWord}";

        var innerContent = $@"<h3 style='color:#b45309;'>Receipt Awaiting Approval</h3>
            <p>This payroll receipt has been sitting in <strong>Submitted</strong> status for <strong>{pendingBizDays} {dayWord}</strong> — past the {graceDays}-business-day grace period (weekends and company holidays excluded).</p>
            <div style='background:#fffbeb;border-left:4px solid #f59e0b;padding:12px 14px;border-radius:4px;margin:14px 0;'>
                <strong>Action needed:</strong> open the Contractor Payroll page and approve, reject, or comment so {System.Net.WebUtility.HtmlEncode(contractorName)} knows where things stand.
            </div>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:160px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Contractor</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(contractorName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Submitted</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.SubmittedDate:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Total Amount</td>
                    <td style='padding:8px;'><strong>{receipt.TotalAmount:C}</strong></td></tr>
            </table>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        await SendToPayrollRecipientsAsync(config, recipients, subject, htmlBody, "PayrollStaleReminder");
    }

    /// <summary>
    /// Contractor-triggered nudge: the contractor is asking admins to please
    /// pay the outstanding balance on a specific receipt. Routes to the same
    /// PayrollNotificationRecipients that get the initial submit notification,
    /// falling back to all Admin portal users if that list is empty.
    ///
    /// Rate-limiting is enforced by the caller (ContractorController checks
    /// LastPaymentRequestDate) so this method just sends the mail.
    /// </summary>
    public async Task SendContractorPaymentRequestAsync(
        Core.Models.PayrollReceipt receipt,
        decimal totalPaid,
        decimal outstanding,
        string? contractorNote,
        IList<GmailApiService.EmailAttachment>? attachments = null)
    {
        var config = await GetActiveConfig();
        if (config == null) return;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "Contractor";

        var configured = await _context.PayrollNotificationRecipients
            .Include(r => r.PortalUser)
            .Where(r => r.IsActive)
            .ToListAsync();

        var recipients = configured
            .Select(r => (Email: r.PortalUser?.Email ?? r.Email,
                          Name:  r.PortalUser?.FullName ?? r.DisplayName ?? r.Email))
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0)
        {
            recipients = await _context.PortalUsers
                .Include(u => u.Role)
                .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin"
                         && !string.IsNullOrEmpty(u.Email))
                .Select(u => new ValueTuple<string, string>(u.Email!, u.FirstName + " " + u.LastName))
                .ToListAsync();
        }
        if (recipients.Count == 0) return;

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var subject = $"Payment request from {contractorName} — receipt #{receipt.Id} ({outstanding:C} outstanding)";

        var noteBlock = string.IsNullOrWhiteSpace(contractorNote)
            ? string.Empty
            : $@"<div style='background:#f0f9ff;border-left:4px solid #0284c7;padding:12px 14px;border-radius:4px;margin:14px 0;color:#075985;'>
                    <strong>Note from {System.Net.WebUtility.HtmlEncode(contractorName)}:</strong>
                    <div style='margin-top:6px;white-space:pre-wrap;'>{System.Net.WebUtility.HtmlEncode(contractorNote.Trim())}</div>
               </div>";

        var innerContent = $@"<h3 style='color:#b45309;'>Payment Request from Contractor</h3>
            <p><strong>{System.Net.WebUtility.HtmlEncode(contractorName)}</strong> is asking about the pending balance on payroll receipt <strong>#{receipt.Id}</strong>.</p>
            {noteBlock}
            <div style='background:#fff7ed;border-left:4px solid #ea580c;padding:12px 14px;border-radius:4px;margin:14px 0;'>
                <strong>Balance summary</strong>
                <div style='margin-top:6px;line-height:1.6;'>
                    <div>Receipt total: <strong>{receipt.TotalAmount:C}</strong></div>
                    <div>Paid so far: <strong>{totalPaid:C}</strong></div>
                    <div style='color:#b45309;'>Outstanding: <strong>{outstanding:C}</strong></div>
                </div>
            </div>
            {await BuildPaymentsTableHtmlAsync(receipt.Id)}
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:160px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Contractor</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(contractorName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Status</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Status}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Approved</td>
                    <td style='padding:8px;'>{(receipt.ApprovedDate.HasValue ? receipt.ApprovedDate.Value.ToString("MMM d, yyyy") : "—")}</td></tr>
            </table>
            <p style='margin-top:14px;font-size:.9rem;color:#475569;'>
                Open the Contractor Payroll queue and record a payment (or reply in the receipt thread) so the contractor sees where things stand.
            </p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        if (attachments != null && attachments.Count > 0)
        {
            await SendToPayrollRecipientsWithAttachmentsAsync(config, recipients, subject, htmlBody, attachments, "PayrollPaymentRequest");
        }
        else
        {
            await SendToPayrollRecipientsAsync(config, recipients, subject, htmlBody, "PayrollPaymentRequest");
        }
    }

    /// <summary>
    /// Admin-triggered payment status update — explicitly emails whichever
    /// address(es) the admin types in (unlike
    /// <see cref="SendContractorPaymentRequestAsync"/>, which routes through
    /// the fixed PayrollNotificationRecipients list and is contractor-
    /// initiated with a 24h cooldown). No rate limit here since it's a
    /// deliberate one-off action, not a repeatable nudge.
    ///
    /// Distinct from <see cref="ShareReceiptAsync"/>: that email attaches
    /// the PDF/XLSX with just Status + Total Amount; this one puts the
    /// paid/outstanding breakdown directly in the email body so the
    /// recipient doesn't have to open an attachment to see where things
    /// stand.
    /// </summary>
    public async Task<bool> SendPaymentStatusUpdateAsync(
        Core.Models.PayrollReceipt receipt,
        decimal totalPaid,
        decimal outstanding,
        string toEmail,
        string? ccEmail,
        string? note,
        string senderDisplay,
        IList<GmailApiService.EmailAttachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(toEmail)) return false;

        var config = await GetActiveConfig();
        if (config == null) return false;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "Contractor";

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var subject = $"Payment status update — receipt #{receipt.Id} ({contractorName}) — {outstanding:C} outstanding";

        var noteBlock = string.IsNullOrWhiteSpace(note)
            ? string.Empty
            : $@"<div style='background:#f0f9ff;border-left:4px solid #0284c7;padding:12px 14px;border-radius:4px;margin:14px 0;color:#075985;'>
                    <strong>Note from {System.Net.WebUtility.HtmlEncode(senderDisplay)}:</strong>
                    <div style='margin-top:6px;white-space:pre-wrap;'>{System.Net.WebUtility.HtmlEncode(note.Trim())}</div>
               </div>";

        var innerContent = $@"<h3 style='margin-top:0;color:#b45309;'>Payment Status Update</h3>
            <p>{System.Net.WebUtility.HtmlEncode(senderDisplay)} is sharing the current payment status for
                <strong>{System.Net.WebUtility.HtmlEncode(contractorName)}</strong>'s payroll receipt <strong>#{receipt.Id}</strong>.</p>
            {noteBlock}
            <div style='background:#fff7ed;border-left:4px solid #ea580c;padding:12px 14px;border-radius:4px;margin:14px 0;'>
                <strong>Balance summary</strong>
                <div style='margin-top:6px;line-height:1.6;'>
                    <div>Receipt total: <strong>{receipt.TotalAmount:C}</strong></div>
                    <div>Paid so far: <strong style='color:#166534;'>{totalPaid:C}</strong></div>
                    <div style='color:#b45309;'>Outstanding: <strong>{outstanding:C}</strong></div>
                </div>
            </div>
            {await BuildPaymentsTableHtmlAsync(receipt.Id)}
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:160px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Contractor</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(contractorName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Status</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Status}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Period</td>
                    <td style='padding:8px;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
            </table>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            if (attachments != null && attachments.Count > 0)
            {
                var msgId = await _gmailApiService.SendEmailWithAttachmentsAsync(config, _context, toEmail, ccEmail, subject, htmlBody, attachments);
                await LogNotificationAsync("PayrollPaymentStatusUpdate", toEmail, toEmail, subject, null, msgId != null);
                return msgId != null;
            }

            await _gmailApiService.SendEmailViaGmailApi(config, _context, toEmail, subject, htmlBody, null, null, null, ccEmail);
            await LogNotificationAsync("PayrollPaymentStatusUpdate", toEmail, toEmail, subject, null, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Payroll] Payment status update send failed for receipt #{Id}", receipt.Id);
            await LogNotificationAsync("PayrollPaymentStatusUpdate", toEmail, toEmail, subject, null, false, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Acknowledgement email back to the admin team once the contractor
    /// clicks Confirm Received on a Paid receipt. Closes the loop on the
    /// payment lifecycle. Sent to the same recipient list configured for
    /// receipt-submitted alerts (so HR / AP / owner all hear about it).
    /// </summary>
    public async Task NotifyReceiptPaymentConfirmedAsync(Core.Models.PayrollReceipt receipt)
    {
        var config = await GetActiveConfig();
        if (config == null) return;

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "Contractor";

        // Same resolution logic as receipt-submitted — recipients table
        // first, then fall back to all active Admins.
        var configured = await _context.PayrollNotificationRecipients
            .Include(r => r.PortalUser)
            .Where(r => r.IsActive)
            .ToListAsync();

        var recipients = configured
            .Select(r => (Email: r.PortalUser?.Email ?? r.Email,
                          Name:  r.PortalUser?.FullName ?? r.DisplayName ?? r.Email))
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0)
        {
            recipients = await _context.PortalUsers
                .Include(u => u.Role)
                .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin"
                         && !string.IsNullOrEmpty(u.Email))
                .Select(u => new ValueTuple<string, string>(u.Email!, u.FirstName + " " + u.LastName))
                .ToListAsync();
        }
        if (recipients.Count == 0) return;

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();
        var subject = $"Receipt #{receipt.Id} payment confirmed received by {contractorName}";

        var noteBlock = string.IsNullOrWhiteSpace(receipt.PaymentConfirmedNote)
            ? string.Empty
            : $@"<div style='background:#ecfdf5;border-left:4px solid #10b981;padding:10px 12px;border-radius:4px;margin:12px 0;color:#065f46;'>
                    <strong>Note from contractor:</strong>
                    <div style='margin-top:4px;'>{System.Net.WebUtility.HtmlEncode(receipt.PaymentConfirmedNote)}</div>
                </div>";

        var innerContent = $@"<h3>Payment Confirmed Received</h3>
            <p>{System.Net.WebUtility.HtmlEncode(contractorName)} has confirmed they received payment for receipt #{receipt.Id}.</p>
            {noteBlock}
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:160px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Amount</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'><strong>{receipt.TotalAmount:C}</strong></td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Paid On</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PaidDate:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Confirmed On</td>
                    <td style='padding:8px;'>{receipt.PaymentConfirmedDate:MMM d, yyyy}</td></tr>
            </table>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        await SendToPayrollRecipientsAsync(config, recipients, subject, htmlBody, "PayrollPaymentConfirmed");
    }

    /// <summary>
    /// Shares a submitted/approved/paid receipt with one or more external
    /// recipients (typically Accounts Payable). Sender supplies the To,
    /// optional Cc, optional subject override, and an optional message that
    /// renders above the receipt summary. The receipt itself is attached as
    /// PDF and/or XLSX so the recipient has an offline copy.
    /// </summary>
    public async Task<bool> ShareReceiptAsync(
        Core.Models.PayrollReceipt receipt,
        string toEmail,
        string? ccEmail,
        string? subjectOverride,
        string? message,
        IList<GmailApiService.EmailAttachment> attachments,
        string senderDisplay)
    {
        if (string.IsNullOrWhiteSpace(toEmail)) return false;

        var config = await GetActiveConfig();
        if (config == null) return false;

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        receipt.Contractor ??= await _context.Employees.FindAsync(receipt.ContractorId);
        var contractorName = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "(unknown)";

        var subject = !string.IsNullOrWhiteSpace(subjectOverride)
            ? subjectOverride
            : $"Contractor Receipt #{receipt.Id} — {contractorName} — {receipt.TotalAmount:C}";

        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? ""
            : $@"<div style='background:#f8f9fa;border-left:4px solid {brandColor};padding:12px;margin:0 0 16px;'>
                    <div style='font-size:.75rem;color:#6c757d;text-transform:uppercase;letter-spacing:.04em;font-weight:600;margin-bottom:4px;'>Message from {System.Net.WebUtility.HtmlEncode(senderDisplay)}</div>
                    <div style='white-space:pre-wrap;'>{System.Net.WebUtility.HtmlEncode(message)}</div>
                </div>";

        var attachmentList = string.Join("<br/>",
            attachments.Select(a => $"<span style='color:#6c757d;'>• {System.Net.WebUtility.HtmlEncode(a.FileName)}</span>"));

        var innerContent = $@"<h3 style='margin-top:0;'>Contractor Payroll Receipt</h3>
            <p>{System.Net.WebUtility.HtmlEncode(senderDisplay)} has shared a payroll receipt with you. The full receipt is attached for your records.</p>
            {safeMessage}
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:160px;'>Receipt #</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Id}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Contractor</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{System.Net.WebUtility.HtmlEncode(contractorName)}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Period</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.PeriodStart:MMM d, yyyy} – {receipt.PeriodEnd:MMM d, yyyy}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Status</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.Status}</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Total Hours</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{receipt.TotalHours:0.##} ({receipt.TotalBillableHours:0.##} billable)</td></tr>
                <tr><td style='padding:8px;font-weight:bold;'>Total Amount</td>
                    <td style='padding:8px;'><strong>{receipt.TotalAmount:C}</strong></td></tr>
            </table>
            <div style='font-size:.85rem;color:#6c757d;'>
                <strong>Attached:</strong><br/>{attachmentList}
            </div>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);

        var primaryName = receipt.Contractor != null ? contractorName : "External";
        try
        {
            var msgId = await _gmailApiService.SendEmailWithAttachmentsAsync(
                config, _context, toEmail, ccEmail, subject, htmlBody, attachments);
            await LogNotificationAsync("PayrollShare", toEmail, primaryName, subject, null, msgId != null);
            return msgId != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Payroll] Failed to share receipt #{Id} to {Email}", receipt.Id, toEmail);
            await LogNotificationAsync("PayrollShare", toEmail, primaryName, subject, null, false, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends a contractor a single-week summary of their logged time —
    /// total hours, Standard vs. Emergency split, billable vs. non-billable,
    /// and a per-day breakdown. Triggered on-demand from the AdminPayroll
    /// "Send Weekly Digest" action.
    /// </summary>
    public async Task NotifyContractorWeeklyHoursAsync(
        Core.Models.Employee contractor, DateTime weekStart, DateTime weekEnd,
        IReadOnlyList<Core.Models.TicketTimeEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(contractor.Email)) return;

        var config = await GetActiveConfig();
        if (config == null) return;

        var (companyName, brandColor, logoUrl, tagline, footerText, showLogo) = await GetBrandingAsync();

        var totalHours      = entries.Sum(e => e.Hours);
        var billableHours   = entries.Where(e => e.IsBillable).Sum(e => e.Hours);
        var standardHours   = entries.Where(e => e.RateType == Core.Enums.PayRateType.Standard).Sum(e => e.Hours);
        var emergencyHours  = entries.Where(e => e.RateType == Core.Enums.PayRateType.Emergency).Sum(e => e.Hours);

        // ── Outstanding-balance snapshot ──
        // Pulls every receipt owned by this contractor whose sum-of-payments
        // is short of TotalAmount and summarises. Rendered as a "Payment
        // status" block in the digest so the contractor sees at a glance
        // what's still open — and gets a reminder that they can nudge
        // admins from the receipt page.
        var openReceipts = await _context.PayrollReceipts
            .Where(r => r.ContractorId == contractor.Id
                     && (r.Status == "Approved" || r.Status == "PartiallyPaid"))
            .AsNoTracking()
            .ToListAsync();
        var openReceiptIds = openReceipts.Select(r => r.Id).ToList();
        var paidLookup = await _context.PayrollReceiptPayments
            .Where(p => openReceiptIds.Contains(p.PayrollReceiptId))
            .GroupBy(p => p.PayrollReceiptId)
            .Select(g => new { PayrollReceiptId = g.Key, Paid = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.PayrollReceiptId, x => x.Paid);

        var openWithBalance = openReceipts
            .Select(r =>
            {
                var paid = paidLookup.TryGetValue(r.Id, out var pv) ? pv : 0m;
                var outstanding = Math.Max(0m, Math.Round(r.TotalAmount - paid, 2, MidpointRounding.AwayFromZero));
                return new { Receipt = r, Paid = paid, Outstanding = outstanding };
            })
            .Where(x => x.Outstanding > 0m)
            .OrderBy(x => x.Receipt.ApprovedDate ?? x.Receipt.CreatedDate)
            .ToList();

        var totalOutstanding = openWithBalance.Sum(x => x.Outstanding);
        var paymentStatusBlock = new System.Text.StringBuilder();
        if (openWithBalance.Count > 0)
        {
            paymentStatusBlock.Append($@"<h4 style='margin-top:24px;'>Payment Status</h4>
                <div style='background:#fff7ed;border-left:4px solid #ea580c;padding:12px 14px;border-radius:4px;margin:8px 0 12px;color:#7c2d12;'>
                    <strong>Outstanding balance: {totalOutstanding:C}</strong>
                    across {openWithBalance.Count} receipt{(openWithBalance.Count == 1 ? "" : "s")}.
                </div>
                <table style='width:100%;border-collapse:collapse;margin:8px 0 15px;'>
                    <thead><tr style='background:#f8f9fa;'>
                        <th style='padding:8px;text-align:left;border-bottom:2px solid #dee2e6;'>Receipt</th>
                        <th style='padding:8px;text-align:left;border-bottom:2px solid #dee2e6;'>Period</th>
                        <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Total</th>
                        <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Paid</th>
                        <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Outstanding</th>
                    </tr></thead>
                    <tbody>");
            foreach (var row in openWithBalance)
            {
                paymentStatusBlock.Append($@"<tr>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>#{row.Receipt.Id}</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{row.Receipt.PeriodStart:MMM d} – {row.Receipt.PeriodEnd:MMM d, yyyy}</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;'>{row.Receipt.TotalAmount:C}</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;color:#166534;'>{row.Paid:C}</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;color:#b45309;font-weight:bold;'>{row.Outstanding:C}</td>
                </tr>");
            }
            paymentStatusBlock.Append(@"</tbody></table>
                <p style='margin:8px 0 0;font-size:.9em;color:#6c757d;'>
                    If a receipt has been waiting a while, open it in the portal and click
                    <strong>Request Payment</strong> to send a reminder to the admin team.
                </p>");
        }
        var paymentStatusHtml = paymentStatusBlock.ToString();

        var rows = new System.Text.StringBuilder();
        if (entries.Count == 0)
        {
            rows.Append("<tr><td colspan='4' style='padding:12px;text-align:center;color:#6c757d;'>No hours logged this week.</td></tr>");
        }
        else
        {
            foreach (var dayGroup in entries.GroupBy(e => e.WorkDate.Date).OrderBy(g => g.Key))
            {
                var dayTotal     = dayGroup.Sum(e => e.Hours);
                var dayBillable  = dayGroup.Where(e => e.IsBillable).Sum(e => e.Hours);
                var dayEmergency = dayGroup.Where(e => e.RateType == Core.Enums.PayRateType.Emergency).Sum(e => e.Hours);
                rows.Append($@"<tr>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{dayGroup.Key:ddd, MMM d}</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;'>{dayTotal:0.##}h</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;'>{dayBillable:0.##}h</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;text-align:right;color:#dc3545;'>{(dayEmergency > 0 ? $"{dayEmergency:0.##}h" : "—")}</td>
                </tr>");
            }
        }

        var subject = $"Weekly hours summary — {weekStart:MMM d} to {weekEnd:MMM d, yyyy}";
        var contractorName = System.Net.WebUtility.HtmlEncode($"{contractor.FirstName} {contractor.LastName}");
        var innerContent = $@"<h3>Weekly Hours Summary</h3>
            <p>Hi {contractorName}, here's your time-logged summary for the week of
                <strong>{weekStart:MMM d}</strong>–<strong>{weekEnd:MMM d, yyyy}</strong>.</p>
            <table style='width:100%;border-collapse:collapse;margin:15px 0;'>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:180px;'>Total Hours</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{totalHours:0.##}h</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Billable</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{billableHours:0.##}h</td></tr>
                <tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Standard / Emergency</td>
                    <td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{standardHours:0.##}h / <span style='color:#dc3545;'>{emergencyHours:0.##}h</span></td></tr>
            </table>
            <h4 style='margin-top:24px;'>By Day</h4>
            <table style='width:100%;border-collapse:collapse;margin:8px 0 15px;'>
                <thead><tr style='background:#f8f9fa;'>
                    <th style='padding:8px;text-align:left;border-bottom:2px solid #dee2e6;'>Day</th>
                    <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Total</th>
                    <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Billable</th>
                    <th style='padding:8px;text-align:right;border-bottom:2px solid #dee2e6;'>Emergency</th>
                </tr></thead>
                <tbody>{rows}</tbody>
            </table>
            {paymentStatusHtml}
            <p style='margin-top:24px;font-size:.9em;color:#6c757d;'>
                Heads up — entries don't appear on a payroll receipt until you submit one from the Contractor portal.
            </p>";

        var htmlBody = BuildHtmlEmail(innerContent, companyName, brandColor, logoUrl, tagline, footerText, showLogo);
        try
        {
            await _gmailApiService.SendEmailViaGmailApi(config, _context, contractor.Email, subject, htmlBody, null, null, null);
            await LogNotificationAsync("PayrollWeeklyDigest", contractor.Email, $"{contractor.FirstName} {contractor.LastName}", subject, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Payroll] Weekly-digest send failed for contractor #{Id}", contractor.Id);
            await LogNotificationAsync("PayrollWeeklyDigest", contractor.Email, $"{contractor.FirstName} {contractor.LastName}", subject, null, false, ex.Message);
        }
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
