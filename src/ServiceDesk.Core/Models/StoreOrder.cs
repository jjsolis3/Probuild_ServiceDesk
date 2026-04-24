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

    [StringLength(1000)]
    public string? Notes { get; set; }

    // Navigation
    public PortalUser PortalUser { get; set; } = null!;
    public ICollection<StoreOrderItem> Items { get; set; } = new List<StoreOrderItem>();
}
