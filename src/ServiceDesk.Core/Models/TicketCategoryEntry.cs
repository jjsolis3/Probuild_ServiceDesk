using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Represents a ticket category stored in the database.
/// System categories (IsSystem = true) mirror the original TicketCategory enum values (0-7)
/// and cannot be deleted. Custom categories (IsSystem = false) are user-defined.
/// </summary>
public class TicketCategoryEntry
{
    public int Id { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [Display(Name = "Category Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>True for the 8 built-in categories seeded at startup. They cannot be deleted.</summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// Bootstrap icon class (e.g. "bi-pc-display") shown next to the category name.
    /// </summary>
    [StringLength(60)]
    public string? Icon { get; set; }

    /// <summary>
    /// Hex colour used in reports and the Keywords page (e.g. "#0d6efd").
    /// </summary>
    [StringLength(20)]
    public string? Color { get; set; }
}
