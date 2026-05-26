using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class PayrollReceipt
{
    public int Id { get; set; }

    [Required]
    public int ContractorId { get; set; }
    public Employee? Contractor { get; set; }

    [Required]
    [Display(Name = "Period Start")]
    [DataType(DataType.Date)]
    public DateTime PeriodStart { get; set; }

    [Required]
    [Display(Name = "Period End")]
    [DataType(DataType.Date)]
    public DateTime PeriodEnd { get; set; }

    [Display(Name = "Total Hours")]
    public decimal TotalHours { get; set; }

    [Display(Name = "Billable Hours")]
    public decimal TotalBillableHours { get; set; }

    [Display(Name = "Rate ($/hr)")]
    public decimal HourlyRateSnapshot { get; set; }

    /// <summary>
    /// Emergency rate locked at receipt creation. Null when the contractor
    /// had no emergency rate configured (in which case there should be no
    /// Emergency entries on this receipt either).
    /// </summary>
    [Display(Name = "Emergency Rate ($/hr)")]
    public decimal? EmergencyRateSnapshot { get; set; }

    /// <summary>Sum of standard billable hours across all entries.</summary>
    [Display(Name = "Standard Hours")]
    public decimal TotalStandardHours { get; set; }

    /// <summary>Sum of emergency billable hours across all entries.</summary>
    [Display(Name = "Emergency Hours")]
    public decimal TotalEmergencyHours { get; set; }

    /// <summary>
    /// Retainer dollar amount snapshot at receipt creation — copied from
    /// Employee.MonthlyRetainerAmount. Used to render the per-month
    /// retainer line(s); the actual dollar applied across receipts is in
    /// RetainerAmountApplied.
    /// </summary>
    [Display(Name = "Monthly Retainer Amount")]
    public decimal? MonthlyRetainerAmountSnapshot { get; set; }

    /// <summary>
    /// Retainer hours snapshot at receipt creation — copied from
    /// Employee.MonthlyRetainerHoursIncluded.
    /// </summary>
    [Display(Name = "Retainer Hours Included")]
    public decimal? MonthlyRetainerHoursSnapshot { get; set; }

    /// <summary>
    /// Sum of standard hours on this receipt that were absorbed by the
    /// retainer pool (not billed at HourlyRate). The receipt UI shows this
    /// as "Covered by retainer".
    /// </summary>
    [Display(Name = "Retainer Hours Applied")]
    public decimal TotalRetainerHoursApplied { get; set; }

    /// <summary>
    /// Dollar amount of the retainer line(s) added to this receipt — sum of
    /// per-month retainer amounts when this is the first receipt to touch a
    /// month. Zero when retainer not configured or when no first-of-month
    /// claim applied here.
    /// </summary>
    [Display(Name = "Retainer Amount Applied")]
    public decimal TotalRetainerAmountApplied { get; set; }

    [Display(Name = "Total Amount")]
    public decimal TotalAmount { get; set; }

    // Draft | Submitted | Approved | Paid
    [Required]
    [StringLength(20)]
    public string Status { get; set; } = "Draft";

    [StringLength(2000)]
    public string? Notes { get; set; }

    [Display(Name = "Submitted")]
    public DateTime? SubmittedDate { get; set; }

    [Display(Name = "Approved")]
    public DateTime? ApprovedDate { get; set; }

    public int? ApprovedById { get; set; }
    public Employee? ApprovedBy { get; set; }

    [Display(Name = "Paid")]
    public DateTime? PaidDate { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Set by admin when returning a receipt to Draft with feedback
    [StringLength(1000)]
    public string? RejectionNote { get; set; }

    /// <summary>
    /// Optional note recorded when the admin approves the receipt — surfaced
    /// on the receipt detail + email so the contractor sees any context
    /// (e.g. "Reviewed and matches PO #142"). Independent of
    /// <see cref="RejectionNote"/> so a previously-rejected-then-resubmitted
    /// receipt keeps both reasons in its audit trail.
    /// </summary>
    [StringLength(1000)]
    [Display(Name = "Approval Note")]
    public string? ApprovalNote { get; set; }

    // Time entries claimed by this receipt
    public ICollection<TicketTimeEntry> TimeEntries { get; set; } = new List<TicketTimeEntry>();
}
