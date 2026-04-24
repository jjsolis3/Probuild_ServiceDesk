using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class StoreOrderItem
{
    public int Id { get; set; }

    public int StoreOrderId { get; set; }

    public int StoreProductId { get; set; }

    [Range(1, 9999)]
    public int Quantity { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Product")]
    public string ProductNameSnapshot { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Category")]
    public string? ProductCategorySnapshot { get; set; }

    // Selected variant options (populated for apparel products)
    [StringLength(50)]
    [Display(Name = "Size")]
    public string? SelectedSize { get; set; }

    [StringLength(50)]
    [Display(Name = "Gender")]
    public string? SelectedGender { get; set; }

    [StringLength(100)]
    [Display(Name = "Color")]
    public string? SelectedColor { get; set; }

    // Navigation
    public StoreOrder StoreOrder { get; set; } = null!;
    public StoreProduct StoreProduct { get; set; } = null!;
}
