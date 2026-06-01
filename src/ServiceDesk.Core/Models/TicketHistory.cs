using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketHistory
{
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Changed By")]
    public string ChangedBy { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Field")]
    public string FieldName { get; set; } = string.Empty;

    [StringLength(500)]
    [Display(Name = "Old Value")]
    public string? OldValue { get; set; }

    [StringLength(500)]
    [Display(Name = "New Value")]
    public string? NewValue { get; set; }

    [Display(Name = "Changed")]
    public DateTime ChangedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional admin-supplied justification for the change — e.g. the
    /// "why" behind a due-date extension. Surfaces in the ticket
    /// history view + makes audits answerable without a side query.
    /// </summary>
    [StringLength(500)]
    [Display(Name = "Reason")]
    public string? Reason { get; set; }

    // Navigation
    public Ticket? Ticket { get; set; }
}
