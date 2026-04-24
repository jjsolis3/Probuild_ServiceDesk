using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class StoreProduct
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? ImagePath { get; set; }

    [StringLength(50)]
    [Display(Name = "Unit of Measure")]
    public string? UnitOfMeasure { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 100;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Variant options (primarily for apparel)
    [Display(Name = "Has Sizes")]
    public bool HasSizes { get; set; } = false;

    [Display(Name = "Has Gender Option")]
    public bool HasGenderOption { get; set; } = false;

    [Display(Name = "Has Color Options")]
    public bool HasColorOptions { get; set; } = false;

    [StringLength(500)]
    [Display(Name = "Available Sizes")]
    public string? AvailableSizes { get; set; }  // comma-separated e.g. "XS,S,M,L,XL,XXL"

    [StringLength(500)]
    [Display(Name = "Available Colors")]
    public string? AvailableColors { get; set; }  // comma-separated e.g. "Black,White,Navy"

    public ICollection<StoreOrderItem> OrderItems { get; set; } = new List<StoreOrderItem>();

    // Additional product images (gallery shown in the catalog modal)
    public ICollection<StoreProductImage> Images { get; set; } = new List<StoreProductImage>();
}
