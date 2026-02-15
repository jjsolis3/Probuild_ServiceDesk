using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class Asset
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Asset Tag")]
    public string AssetTag { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Asset Type")]
    public AssetType AssetType { get; set; }

    [Required]
    [Display(Name = "Status")]
    public AssetStatus Status { get; set; } = AssetStatus.Available;

    [StringLength(100)]
    public string? Manufacturer { get; set; }

    [StringLength(100)]
    public string? Model { get; set; }

    [StringLength(100)]
    [Display(Name = "Serial Number")]
    public string? SerialNumber { get; set; }

    [Display(Name = "Purchase Date")]
    [DataType(DataType.Date)]
    public DateTime? PurchaseDate { get; set; }

    [Display(Name = "Purchase Cost")]
    [DataType(DataType.Currency)]
    public decimal? PurchaseCost { get; set; }

    [Display(Name = "Warranty Expiry")]
    [DataType(DataType.Date)]
    public DateTime? WarrantyExpiry { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    // Foreign key
    [Display(Name = "Assigned To")]
    public int? AssignedToId { get; set; }

    // Navigation property
    [Display(Name = "Assigned To")]
    public Employee? AssignedTo { get; set; }
}
