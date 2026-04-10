using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketNote
{
    public int Id { get; set; }

    [Required]
    [Display(Name = "Ticket")]
    public int TicketId { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Author Name")]
    public string AuthorName { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "Author Email")]
    public string? AuthorEmail { get; set; }

    [Required]
    [Display(Name = "Content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Sanitized HTML version of the email body (null for portal/agent notes).</summary>
    public string? ContentHtml { get; set; }

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [Required]
    [StringLength(20)]
    [Display(Name = "Source")]
    public string Source { get; set; } = "Portal"; // Email, Portal, Agent, System

    [Display(Name = "Internal Note")]
    public bool IsInternal { get; set; } = false;

    // Navigation
    public Ticket? Ticket { get; set; }
}
