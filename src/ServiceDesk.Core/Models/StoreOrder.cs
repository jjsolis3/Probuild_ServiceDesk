using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class StoreOrder
{
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Order #")]
    public string OrderNumber { get; set; } = string.Empty;

    public int PortalUserId { get; set; }

    [Display(Name = "Order Date")]
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;

    [Required]
    [StringLength(50)]
    public string Status { get; set; } = "Pending";

    public int Quarter { get; set; }

    public int Year { get; set; }

    [Display(Name = "Confirmation Sent")]
    public bool ConfirmationEmailSent { get; set; }

    // Timestamp of the most recent status transition. Null for orders created
    // before this column was added; the timeline view falls back to OrderDate.
    [Display(Name = "Last Status Change")]
    public DateTime? LastStatusChangedDate { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    // Branch / location the ordering employee belongs to at the time of order.
    // BranchId links to the live record (nullable so orders aren't deleted with
    // a branch); BranchNameSnapshot is captured so historical exports stay
    // accurate even if the branch is later renamed or removed.
    [Display(Name = "Branch / Location")]
    public int? BranchId { get; set; }

    [StringLength(200)]
    [Display(Name = "Branch / Location")]
    public string? BranchNameSnapshot { get; set; }

    // Navigation
    public PortalUser PortalUser { get; set; } = null!;
    public Branch? Branch { get; set; }
    public ICollection<StoreOrderItem> Items { get; set; } = new List<StoreOrderItem>();
}
