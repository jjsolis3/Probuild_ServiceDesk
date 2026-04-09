using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class Ticket
{
    public int Id { get; set; }

    [Required]
    [StringLength(200, MinimumLength = 3)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, MinimumLength = 10)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Sanitized HTML body stored when a ticket is created from an inbound email.
    /// Null for manually-created tickets. Rendered instead of Description when present.
    /// </summary>
    public string? DescriptionHtml { get; set; }

    [Required]
    [Display(Name = "Category")]
    public TicketCategory Category { get; set; }

    [Display(Name = "Sub-Category")]
    public int? SubCategoryId { get; set; }
    public TicketSubCategory? SubCategory { get; set; }

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

    /// <summary>
    /// Auto-populated from the submitting employee's branch when the ticket is created.
    /// </summary>
    [Display(Name = "Branch / Location")]
    public int? BranchId { get; set; }

    [Display(Name = "Group Assignment")]
    public int? UserGroupId { get; set; }

    // Navigation properties
    public Branch? Branch { get; set; }

    [Display(Name = "Group Assignment")]
    public UserGroup? UserGroup { get; set; }

    [Display(Name = "Submitted By")]
    public Employee? SubmittedBy { get; set; }

    [Display(Name = "Assigned To")]
    public Employee? AssignedTo { get; set; }

    [Display(Name = "Related Service")]
    public CompanyService? CompanyService { get; set; }

    [Display(Name = "Due Date")]
    public DateTime? DueDate { get; set; }

    // Threading navigation
    public ICollection<TicketNote> Notes { get; set; } = new List<TicketNote>();
    public ICollection<TicketEmail> Emails { get; set; } = new List<TicketEmail>();
    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
    public ICollection<TicketHistory> History { get; set; } = new List<TicketHistory>();

    public bool IsOverdue => DueDate.HasValue && DueDate.Value < DateTime.UtcNow
                             && Status != TicketStatus.Resolved
                             && Status != TicketStatus.Closed
                             && Status != TicketStatus.Cancelled;
}
