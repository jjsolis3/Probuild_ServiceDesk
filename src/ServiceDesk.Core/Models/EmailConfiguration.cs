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

    [Required]
    [StringLength(200)]
    [Display(Name = "IMAP Server")]
    public string ImapServer { get; set; } = "imap.gmail.com";

    [Display(Name = "IMAP Port")]
    public int ImapPort { get; set; } = 993;

    [Required]
    [StringLength(200)]
    [Display(Name = "SMTP Server")]
    public string SmtpServer { get; set; } = "smtp.gmail.com";

    [Display(Name = "SMTP Port")]
    public int SmtpPort { get; set; } = 587;

    [Required]
    [StringLength(200)]
    public string Username { get; set; } = string.Empty;

    [StringLength(500)]
    [DataType(DataType.Password)]
    public string? Password { get; set; }

    [Display(Name = "Use SSL")]
    public bool UseSsl { get; set; } = true;

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = false;

    [Display(Name = "Poll Interval (Minutes)")]
    public int PollIntervalMinutes { get; set; } = 5;

    [Display(Name = "Create Tickets from Emails")]
    public bool CreateTicketsFromEmails { get; set; } = true;

    [Display(Name = "Default Ticket Category")]
    public int? DefaultTicketCategoryId { get; set; }

    [Display(Name = "Default Assignee")]
    public int? DefaultAssigneeId { get; set; }

    [Display(Name = "Last Polled")]
    public DateTime? LastPolledDate { get; set; }

    [Display(Name = "Last Error")]
    [StringLength(2000)]
    public string? LastError { get; set; }

    // Navigation
    public Employee? DefaultAssignee { get; set; }
}
