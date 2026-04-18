using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class SoftwareLicense
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Product Name")]
    public string ProductName { get; set; } = "";

    [StringLength(200)]
    public string? Publisher { get; set; }

    [StringLength(500)]
    [Display(Name = "License Key / Number")]
    public string? LicenseKey { get; set; }

    [Display(Name = "License Type")]
    public LicenseType LicenseType { get; set; } = LicenseType.Perpetual;

    [Display(Name = "Total Seats")]
    public int TotalSeats { get; set; } = 1;

    [Display(Name = "Seats In Use")]
    public int SeatsInUse { get; set; } = 0;

    [Display(Name = "Cost Per Seat")]
    [DataType(DataType.Currency)]
    public decimal? CostPerSeat { get; set; }

    [Display(Name = "Purchase Date")]
    [DataType(DataType.Date)]
    public DateTime? PurchaseDate { get; set; }

    [Display(Name = "Expiry / Renewal Date")]
    [DataType(DataType.Date)]
    public DateTime? ExpiryDate { get; set; }

    [StringLength(200)]
    public string? Vendor { get; set; }

    [StringLength(100)]
    [Display(Name = "PO Number")]
    public string? PurchaseOrderNumber { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

    // Computed
    public int     AvailableSeats => TotalSeats - SeatsInUse;
    public decimal? TotalCost     => CostPerSeat.HasValue ? CostPerSeat * TotalSeats : null;
    public bool    IsExpired      => ExpiryDate.HasValue && ExpiryDate.Value.Date < DateTime.Today;
    public bool    IsExpiringSoon => ExpiryDate.HasValue && !IsExpired
                                     && (ExpiryDate.Value.Date - DateTime.Today).Days <= 30;
}
