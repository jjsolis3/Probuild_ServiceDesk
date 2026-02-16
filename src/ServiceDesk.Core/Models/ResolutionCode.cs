using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class ResolutionCode
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;
}
