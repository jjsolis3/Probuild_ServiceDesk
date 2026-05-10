using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class StoreProductImage
{
    public int Id { get; set; }

    public int StoreProductId { get; set; }

    [Required]
    [StringLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Alt { get; set; }

    [StringLength(100)]
    public string? VariantTag { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 100;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public StoreProduct StoreProduct { get; set; } = null!;
}
