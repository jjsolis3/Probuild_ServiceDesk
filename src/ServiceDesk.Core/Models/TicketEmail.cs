using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketEmail
{
    public int Id { get; set; }

    [Required]
    [Display(Name = "Ticket")]
    public int TicketId { get; set; }

    [Required]
    [StringLength(500)]
    [Display(Name = "Gmail Message ID")]
    public string GmailMessageId { get; set; } = string.Empty;

    [StringLength(500)]
    [Display(Name = "RFC Message-ID")]
    public string? MessageId { get; set; }

    /// <summary>
    /// Gmail's own conversation id (distinct from the RFC Message-ID above).
    /// Groups every message in a thread server-side regardless of whether
    /// the sender's mail client preserved In-Reply-To/References, so it
    /// survives forwards and replies from a different participant that
    /// those headers miss.
    /// </summary>
    [StringLength(500)]
    [Display(Name = "Gmail Thread ID")]
    public string? GmailThreadId { get; set; }

    [StringLength(500)]
    [Display(Name = "In-Reply-To")]
    public string? InReplyTo { get; set; }

    [Display(Name = "References")]
    public string? References { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "From Address")]
    public string FromAddress { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "From Name")]
    public string? FromName { get; set; }

    [StringLength(500)]
    [Display(Name = "Subject")]
    public string? Subject { get; set; }

    [Required]
    [StringLength(10)]
    [Display(Name = "Direction")]
    public string Direction { get; set; } = "Inbound"; // Inbound, Outbound

    [Display(Name = "Processed Date")]
    public DateTime ProcessedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket? Ticket { get; set; }
}
