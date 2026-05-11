namespace ServiceDesk.Core.Models;

/// <summary>
/// A user's "favorited" store product. One row per (user, product). Used to
/// drive the favorites filter on the catalog and the heart icon on cards.
/// </summary>
public class StoreProductFavorite
{
    public int Id { get; set; }
    public int PortalUserId { get; set; }
    public int StoreProductId { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;

    public PortalUser? PortalUser { get; set; }
    public StoreProduct? StoreProduct { get; set; }
}
