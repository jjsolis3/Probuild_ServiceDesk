using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Snapshot of a recurring-charge line item on a payroll receipt. Carries its
/// own copies of label / cadence / unit amount so the historical receipt
/// renders the same even after the source template is edited or deleted.
/// </summary>
public class PayrollReceiptCharge
{
    public int Id { get; set; }

    [Required]
    public int PayrollReceiptId { get; set; }
    public PayrollReceipt? Receipt { get; set; }

    /// <summary>Origin template — nullable so a template can be deleted without
    /// losing the historical receipt row (SET NULL on FK).</summary>
    public int? TemplateId { get; set; }
    public RecurringChargeTemplate? Template { get; set; }

    [Required]
    [StringLength(120)]
    public string LabelSnapshot { get; set; } = string.Empty;

    public RecurringChargeCadence CadenceSnapshot { get; set; }

    public RecurringChargePricingMode PricingModeSnapshot { get; set; }

    /// <summary>Dollars per occurrence at snapshot time. For Hourly templates
    /// this is already hours × the contractor's HourlyRate at receipt creation.</summary>
    [Display(Name = "Unit Amount ($)")]
    public decimal UnitAmountSnapshot { get; set; }

    /// <summary>Auto-computed from cadence + period; contractor may override
    /// at receipt creation time (capped at 2× auto in the controller).</summary>
    [Display(Name = "Occurrences")]
    public int OccurrenceCount { get; set; }

    [Display(Name = "Total")]
    public decimal TotalAmount { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
