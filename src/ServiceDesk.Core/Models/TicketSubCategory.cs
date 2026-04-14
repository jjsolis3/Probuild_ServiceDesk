using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketSubCategory
{
    public int Id { get; set; }

    /// <summary>Parent category this sub-category belongs to (references TicketCategories.Id).</summary>
    [Required]
    public int Category { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [Display(Name = "Sub-Category Name")]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; } = 0;

    public bool IsActive { get; set; } = true;
}
