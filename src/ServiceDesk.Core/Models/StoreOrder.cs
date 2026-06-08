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

    // Per-state timestamps so the OrderDetail timeline can show when each
    // transition happened, not just the most recent one.
    public DateTime? ConfirmedDate { get; set; }
    public DateTime? ShippedDate   { get; set; }
    public DateTime? FulfilledDate { get; set; }
    public DateTime? CancelledDate { get; set; }

    // Captured when the order moves to Shipped. Surfaced on OrderDetail and in
    // the status-update email so the requester can self-serve tracking.
    [StringLength(100)]
    [Display(Name = "Tracking Number")]
    public string? TrackingNumber { get; set; }

    [StringLength(50)]
    [Display(Name = "Carrier")]
    public string? Carrier { get; set; }

    // Optional context captured when the order moves to Cancelled, e.g.
    // "Item discontinued — refund issued".
    [StringLength(500)]
    [Display(Name = "Cancellation Reason")]
    public string? CancellationReason { get; set; }

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
