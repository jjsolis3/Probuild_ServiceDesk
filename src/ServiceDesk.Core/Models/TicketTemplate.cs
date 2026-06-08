using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Reusable boilerplate for common ticket types (new hire, offboarding,
/// equipment request, password reset, etc.). When an agent picks a template
/// on the Create Ticket page, the Title, Description, Category, SubCategory,
/// and Priority fields are pre-filled — they can then tweak before submitting.
/// </summary>
public class TicketTemplate
{
    public int Id { get; set; }

    /// <summary>Short admin-facing name shown in the picker.</summary>
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional one-line hint shown under the template name in the picker.</summary>
    [StringLength(300)]
    public string? Description { get; set; }

    /// <summary>Pre-filled ticket Title. Required so the template is immediately useful.</summary>
    [Required]
    [StringLength(200, MinimumLength = 3)]
    [Display(Name = "Ticket Title")]
    public string TitleTemplate { get; set; } = string.Empty;

    /// <summary>Pre-filled ticket Description / body.</summary>
    [Required]
    [StringLength(2000, MinimumLength = 10)]
    [Display(Name = "Ticket Body")]
    public string BodyTemplate { get; set; } = string.Empty;

    /// <summary>Category id (matches TicketCategoryEntry.Id used on Ticket.Category).</summary>
    [Required]
    public int Category { get; set; }

    /// <summary>Optional sub-category. Validated against the chosen Category in the admin UI.</summary>
    public int? SubCategoryId { get; set; }

    [Required]
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public int SortOrder { get; set; } = 0;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedDate { get; set; }
}
