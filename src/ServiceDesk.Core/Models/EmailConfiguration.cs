using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class EmailConfiguration
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Configuration Name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(200)]
    [Display(Name = "Email Address")]
    public string EmailAddress { get; set; } = string.Empty;

    // ---- Gmail API OAuth2 ----

    [StringLength(500)]
    [Display(Name = "Gmail Client ID")]
    public string? GmailClientId { get; set; }

    [StringLength(500)]
    [Display(Name = "Gmail Client Secret")]
    public string? GmailClientSecret { get; set; }

    [Display(Name = "Gmail Refresh Token")]
    public string? GmailRefreshToken { get; set; }

    [Display(Name = "Gmail Access Token")]
    public string? GmailAccessToken { get; set; }

    [Display(Name = "Token Expiry (UTC)")]
    public DateTime? GmailTokenExpiry { get; set; }

    [StringLength(100)]
    [Display(Name = "Gmail History ID")]
    public string? GmailHistoryId { get; set; }

    // ---- SMTP Fallback (for non-Gmail or hybrid) ----

    [StringLength(200)]
    [Display(Name = "SMTP Server")]
    public string? SmtpServer { get; set; } = "smtp.gmail.com";

    [Display(Name = "SMTP Port")]
    public int SmtpPort { get; set; } = 587;

    [Display(Name = "Use SSL")]
    public bool UseSsl { get; set; } = true;

    [StringLength(200)]
    [Display(Name = "SMTP Username")]
    public string? SmtpUsername { get; set; }

    [StringLength(500)]
    [Display(Name = "SMTP Password / App Password")]
    public string? SmtpPassword { get; set; }

    // ---- Behavior ----

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = false;

    [Display(Name = "Poll Interval (Minutes)")]
    public int PollIntervalMinutes { get; set; } = 5;

    [Display(Name = "Create Tickets from Emails")]
    public bool CreateTicketsFromEmails { get; set; } = true;

    [Display(Name = "Auto-Reply on New Ticket")]
    public bool AutoReplyOnNewTicket { get; set; } = true;

    [Display(Name = "Default Assignee")]
    public int? DefaultAssigneeId { get; set; }

    // ---- Status ----

    [Display(Name = "Last Polled")]
    public DateTime? LastPolledDate { get; set; }

    [Display(Name = "Last Error")]
    [StringLength(2000)]
    public string? LastError { get; set; }

    [Display(Name = "Is Authorized")]
    public bool IsAuthorized { get; set; } = false;

    // Navigation
    public Employee? DefaultAssignee { get; set; }
}
