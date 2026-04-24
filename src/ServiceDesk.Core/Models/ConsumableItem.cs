using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class ConsumableItem
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = "";

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = "";

    [StringLength(100)]
    public string? Manufacturer { get; set; }

    [StringLength(100)]
    [Display(Name = "Part / SKU")]
    public string? PartNumber { get; set; }

    [Display(Name = "Quantity on Hand")]
    public int QuantityOnHand { get; set; } = 0;

    [Display(Name = "Reorder Point")]
    public int? ReorderPoint { get; set; }

    [Display(Name = "Unit Cost")]
    [DataType(DataType.Currency)]
    public decimal? UnitCost { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

    public bool IsLowStock => ReorderPoint.HasValue && QuantityOnHand <= ReorderPoint.Value;

    public ICollection<ConsumableTransaction> Transactions { get; set; } = [];
}
