using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class Ticket
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Category")]
    public TicketCategory Category { get; set; }

    [Required]
    [Display(Name = "Status")]
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    [Required]
    [Display(Name = "Priority")]
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [Display(Name = "Updated Date")]
    public DateTime? UpdatedDate { get; set; }

    [Display(Name = "Resolved Date")]
    public DateTime? ResolvedDate { get; set; }

    [Display(Name = "Closed Date")]
    public DateTime? ClosedDate { get; set; }

    [StringLength(2000)]
    [Display(Name = "Resolution Notes")]
    public string? ResolutionNotes { get; set; }

    // Foreign keys
    [Required]
    [Display(Name = "Submitted By")]
    public int SubmittedById { get; set; }

    [Display(Name = "Assigned To")]
    public int? AssignedToId { get; set; }

    [Display(Name = "Related Service")]
    public int? CompanyServiceId { get; set; }

    // Navigation properties
    [Display(Name = "Submitted By")]
    public Employee? SubmittedBy { get; set; }

    [Display(Name = "Assigned To")]
    public Employee? AssignedTo { get; set; }

    [Display(Name = "Related Service")]
    public CompanyService? CompanyService { get; set; }
}
