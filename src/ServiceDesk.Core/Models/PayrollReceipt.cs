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

    // Time entries claimed by this receipt
    public ICollection<TicketTimeEntry> TimeEntries { get; set; } = new List<TicketTimeEntry>();
}
