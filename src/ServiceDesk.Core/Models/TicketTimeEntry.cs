using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class TicketTimeEntry
{
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    [StringLength(200)]
    [Display(Name = "Logged By")]
    public string? LoggedByEmail { get; set; }

    [Display(Name = "Logged By")]
    public int? LoggedByEmployeeId { get; set; }
    public Employee? LoggedByEmployee { get; set; }

    [Display(Name = "Work Date")]
    [DataType(DataType.Date)]
    public DateTime WorkDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>
    /// Optional clock-in timestamp. When both <see cref="StartTime"/> and
    /// <see cref="EndTime"/> are set, <see cref="Hours"/> is computed from
    /// the interval on save. When only flat hours are entered, both stay
    /// null and Hours holds the manually-entered value.
    /// </summary>
    [Display(Name = "Start Time")]
    public DateTime? StartTime { get; set; }

    /// <summary>Optional clock-out timestamp. See <see cref="StartTime"/>.</summary>
    [Display(Name = "End Time")]
    public DateTime? EndTime { get; set; }

    [Required]
    [Range(0.01, 999)]
    [Display(Name = "Hours")]
    public decimal Hours { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [Display(Name = "Billable")]
    public bool IsBillable { get; set; } = false;

    /// <summary>
    /// Which pay rate this entry charges against. Default Standard; switch to
    /// Emergency for urgent / weekend / after-hours / holiday work that bills
    /// at the EmergencyHourlyRate. Emergency hours never burn down the monthly
    /// retainer pool — the retainer covers Standard hours only.
    /// </summary>
    [Display(Name = "Rate Type")]
    public PayRateType RateType { get; set; } = PayRateType.Standard;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // ── Modification audit ─────────────────────────────────────────────
    // Lets an entry be edited (only while unclaimed) without losing the
    // history of when/who/why it changed. The receipt UI surfaces an
    // "(edited)" pill backed by these fields.

    /// <summary>Timestamp of the most recent edit. Null until first edit.</summary>
    [Display(Name = "Last Modified")]
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Email of the user who made the most recent edit.</summary>
    [StringLength(200)]
    [Display(Name = "Modified By")]
    public string? ModifiedByEmail { get; set; }

    /// <summary>
    /// Free-form note explaining the most recent edit. Required by the edit
    /// form so the audit trail isn't empty (caller-enforced).
    /// </summary>
    [StringLength(500)]
    [Display(Name = "Modification Reason")]
    public string? ModificationReason { get; set; }

    /// <summary>How many times this entry has been edited.</summary>
    [Display(Name = "Edit Count")]
    public int ModificationCount { get; set; } = 0;

    // Claimed by a payroll receipt once submitted — prevents double-billing
    public int? PayrollReceiptId { get; set; }
    public PayrollReceipt? PayrollReceipt { get; set; }
}
