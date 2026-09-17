using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// One payment recorded against a <see cref="PayrollReceipt"/>. A receipt can
/// have many payments (multiple partial checks, e.g. "$500 by check #4521
/// on Aug 31" and "$832.50 by Zelle on Sep 15"). The receipt's own
/// <c>Status</c> is auto-computed from the sum of payment amounts:
///
///   • Sum &lt; TotalAmount and &gt; 0  → PartiallyPaid
///   • Sum ≥ TotalAmount              → Paid
///   • Sum = 0                        → whatever it was before (Approved usually)
///
/// The historical single-payment fields on <see cref="PayrollReceipt"/>
/// (<c>PaidDate</c>, <c>PaymentMethod</c>, <c>PaymentReference</c>) stay put
/// and act as a "last payment" snapshot for downstream code that hasn't
/// been updated to iterate payments yet.
/// </summary>
public class PayrollReceiptPayment
{
    public int Id { get; set; }

    [Required]
    public int PayrollReceiptId { get; set; }

    /// <summary>When the funds actually moved (bank date, check date, wire date).</summary>
    [Required]
    [Display(Name = "Payment Date")]
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;

    /// <summary>Dollar amount of this individual payment. Must be &gt; 0.</summary>
    [Required]
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    /// <summary>Check | ACH | Zelle | Wire | Cash | Other. Free-form so we can add channels without a schema change.</summary>
    [Required]
    [StringLength(50)]
    [Display(Name = "Payment Method")]
    public string PaymentMethod { get; set; } = "Check";

    /// <summary>Populated for the Check method. Optional otherwise.</summary>
    [StringLength(50)]
    [Display(Name = "Check #")]
    public string? CheckNumber { get; set; }

    /// <summary>Free-form reference — ACH id, Zelle confirmation, wire reference, etc.</summary>
    [StringLength(200)]
    [Display(Name = "Reference")]
    public string? Reference { get; set; }

    /// <summary>Optional note captured by the admin at record time (e.g. "first installment").</summary>
    [StringLength(1000)]
    [Display(Name = "Note")]
    public string? Note { get; set; }

    /// <summary>
    /// Set when the contractor clicks "Confirm Received" against this specific
    /// payment. Mirrors <see cref="PayrollReceipt.PaymentConfirmedDate"/> but
    /// per payment — so a contractor can confirm partial payments individually.
    /// </summary>
    [Display(Name = "Contractor Confirmed")]
    public DateTime? ContractorConfirmedDate { get; set; }

    [StringLength(500)]
    public string? ContractorConfirmedNote { get; set; }

    // ── Audit trail ──────────────────────────────────────────────────────────
    /// <summary>Employee id of the admin who recorded this payment. Null when the payment was backfilled by DbInitializer.</summary>
    public int? RecordedByEmployeeId { get; set; }

    /// <summary>Snapshot of the recorder's full name (survives account renames / offboarding).</summary>
    [StringLength(200)]
    public string? RecordedByName { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // ── Navigation ───────────────────────────────────────────────────────────
    public PayrollReceipt? Receipt { get; set; }
}
