using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class Subscription
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Provider { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Required]
    [Display(Name = "Status")]
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    [Display(Name = "License Count")]
    public int? LicenseCount { get; set; }

    [Display(Name = "Licenses Used")]
    public int? LicensesUsed { get; set; }

    [Required]
    [Display(Name = "Monthly Cost")]
    [DataType(DataType.Currency)]
    public decimal MonthlyCost { get; set; }

    [Display(Name = "Annual Cost")]
    [DataType(DataType.Currency)]
    public decimal? AnnualCost { get; set; }

    [Required]
    [Display(Name = "Start Date")]
    [DataType(DataType.Date)]
    public DateTime StartDate { get; set; }

    [Display(Name = "Renewal Date")]
    [DataType(DataType.Date)]
    public DateTime? RenewalDate { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    /// <summary>Optional link to the asset this license is installed on / assigned to.</summary>
    [Display(Name = "Linked Asset")]
    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }
}
