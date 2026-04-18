using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AssetMaintenanceLog
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Required]
    [DataType(DataType.Date)]
    [Display(Name = "Service Date")]
    public DateTime ServiceDate { get; set; } = DateTime.UtcNow.Date;

    [Required]
    [StringLength(100)]
    [Display(Name = "Service Type")]
    public string ServiceType { get; set; } = "Repair";

    [Required]
    [StringLength(1000)]
    public string Description { get; set; } = "";

    [DataType(DataType.Currency)]
    public decimal? Cost { get; set; }

    [StringLength(200)]
    public string? Vendor { get; set; }

    [StringLength(200)]
    [Display(Name = "Performed By")]
    public string? PerformedBy { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Next Service Date")]
    public DateTime? NextServiceDate { get; set; }

    [StringLength(200)]
    public string CreatedByEmail { get; set; } = "";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
