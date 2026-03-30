using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class TicketSubCategory
{
    public int Id { get; set; }

    /// <summary>Parent category this sub-category belongs to (maps to TicketCategory enum value).</summary>
    [Required]
    public TicketCategory Category { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [Display(Name = "Sub-Category Name")]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; } = 0;

    public bool IsActive { get; set; } = true;
}
