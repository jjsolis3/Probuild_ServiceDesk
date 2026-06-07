namespace ServiceDesk.Core.Models;

/// <summary>
/// Persistent cart line for a portal user. One row per "add to cart" action;
/// the cart is the set of rows where <c>PortalUserId</c> matches the current
/// user. Cleared on order placement.
/// </summary>
public class StoreCartItem
{
    public int Id { get; set; }
    public int PortalUserId { get; set; }
    public int StoreProductId { get; set; }

    public int Quantity { get; set; }

    // Variant selections at the time the item was added.
    public string? SelectedSize { get; set; }
    public string? SelectedGender { get; set; }
    public string? SelectedColor { get; set; }

    // JSON object of additional custom-option selections, mirroring the format
    // used on StoreOrderItem.CustomSelectionsJson.
    public string? CustomSelectionsJson { get; set; }

    public DateTime AddedDate { get; set; } = DateTime.UtcNow;

    public PortalUser? PortalUser { get; set; }
    public StoreProduct? StoreProduct { get; set; }
}
