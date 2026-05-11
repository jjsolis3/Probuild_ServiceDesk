namespace ServiceDesk.Core.Models;

/// <summary>
/// In-app notification surfaced through the portal notification bell.
/// One row per recipient — broadcasts to multiple users create multiple rows.
/// </summary>
public class PortalNotification
{
    public int Id { get; set; }
    public int PortalUserId { get; set; }

    // Free-form category, e.g. "StoreOrderPlaced", "StoreOrderStatus", "StoreOpening"
    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }

    // Relative or absolute URL the bell links to when clicked.
    public string? LinkUrl { get; set; }

    // Optional Bootstrap icon name (e.g. "bi-receipt") rendered next to the row.
    public string? Icon { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ReadDate { get; set; }

    public PortalUser? PortalUser { get; set; }
}
