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

    /// <summary>
    /// Sum of all recurring-charge line items on this receipt. Snapshot at
    /// receipt creation so historical totals don't shift when templates are
    /// later edited or deleted.
    /// </summary>
    [Display(Name = "Recurring Charges Total")]
    public decimal TotalRecurringChargesAmount { get; set; }

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

    /// <summary>
    /// How payment was issued — Check / ACH / Zelle / Wire / Other.
    /// Free-form so we can add channels without a schema change, but
    /// admin UI restricts to a known list.
    /// </summary>
    [StringLength(50)]
    [Display(Name = "Payment Method")]
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// Reference number captured at Mark Paid time — check number,
    /// ACH transaction ID, Zelle confirmation, etc. Stored verbatim and
    /// surfaced on the receipt + in the contractor email so the
    /// contractor can reconcile against their bank.
    /// </summary>
    [StringLength(200)]
    [Display(Name = "Payment Reference")]
    public string? PaymentReference { get; set; }

    /// <summary>
    /// When the contractor confirmed they received the funds. Distinct
    /// from PaidDate (set by admin at issue time) — this closes the
    /// loop by capturing the payee's attestation.
    /// </summary>
    [Display(Name = "Payment Confirmed")]
    public DateTime? PaymentConfirmedDate { get; set; }

    /// <summary>Optional note left by the contractor on confirmation.</summary>
    [StringLength(500)]
    public string? PaymentConfirmedNote { get; set; }

    /// <summary>
    /// Last time the stale-Submitted reminder was sent. Throttles the
    /// daily reminder so a single Submitted receipt doesn't spam
    /// recipients more than once per day.
    /// </summary>
    public DateTime? LastReminderSentUtc { get; set; }

    /// <summary>
    /// Last time the contractor manually sent a "please pay this outstanding
    /// balance" nudge from the Payroll UI. Used to enforce a 24-hour cooldown
    /// so a frustrated contractor can't accidentally flood admins.
    /// Null = never requested payment on this receipt.
    /// </summary>
    [Display(Name = "Last Payment Request")]
    public DateTime? LastPaymentRequestDate { get; set; }

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

    // Activity / discussion thread — see PayrollReceiptComment
    public ICollection<PayrollReceiptComment> Comments { get; set; } = new List<PayrollReceiptComment>();

    // Recurring-charge snapshot rows — see PayrollReceiptCharge
    public ICollection<PayrollReceiptCharge> Charges { get; set; } = new List<PayrollReceiptCharge>();
}
