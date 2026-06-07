using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Reusable line item attached to a contractor — when the contractor creates
/// a payroll receipt the system auto-suggests this template as a row priced
/// by cadence × unit amount. Lives on the Employee profile and managed by
/// admins on the Edit page; once snapshot to a <see cref="PayrollReceiptCharge"/>
/// on a saved receipt, later edits here don't retroactively change history.
/// </summary>
public class RecurringChargeTemplate
{
    public int Id { get; set; }

    [Required]
    public int ContractorId { get; set; }
    public Employee? Contractor { get; set; }

    [Required]
    [StringLength(120)]
    [Display(Name = "Label")]
    public string Label { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Cadence")]
    public RecurringChargeCadence Cadence { get; set; } = RecurringChargeCadence.PerDay;

    /// <summary>
    /// Bitmask of weekdays this charge applies on when
    /// <see cref="Cadence"/> = <see cref="RecurringChargeCadence.PerDay"/>.
    /// Sun=1, Mon=2, Tue=4, Wed=8, Thu=16, Fri=32, Sat=64. A value of 0 is
    /// interpreted as "all 7 days" (127). Mon–Fri is 62.
    /// Ignored for non-PerDay cadences.
    /// </summary>
    [Display(Name = "Weekday Mask")]
    public byte WeekdayMask { get; set; } = 62; // Mon–Fri default

    [Required]
    [Display(Name = "Pricing Mode")]
    public RecurringChargePricingMode PricingMode { get; set; } = RecurringChargePricingMode.Flat;

    /// <summary>
    /// Flat mode: dollars per occurrence. Hourly mode: hours per occurrence
    /// (dollars resolved at receipt-create time via Contractor.HourlyRate).
    /// Four decimals so 0.25 h is exact.
    /// </summary>
    [Required]
    [Range(0, 100000)]
    [Display(Name = "Unit Amount")]
    public decimal UnitAmount { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Start Date")]
    public DateTime? StartDate { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "End Date")]
    public DateTime? EndDate { get; set; }

    /// <summary>Soft disable — inactive templates don't auto-appear on new
    /// receipts but still render on historical receipts via snapshot.</summary>
    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    /// <summary>Internal notes for the admin — never shown on receipts or to contractors.</summary>
    [StringLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
