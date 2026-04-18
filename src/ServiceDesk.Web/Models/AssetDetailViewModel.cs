using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

public class AssetDetailViewModel
{
    public Asset Asset { get; set; } = null!;

    public List<AssetAssignmentHistory> AssignmentHistory { get; set; } = new();
    public List<AssetAuditLog> AuditLog { get; set; } = new();
    public List<AssetCredential> Credentials { get; set; } = new();
    public List<AssetAttachment> Attachments { get; set; } = new();
    public List<AssetRelationship> RelationshipsFrom { get; set; } = new();
    public List<AssetRelationship> RelationshipsTo { get; set; } = new();
    public List<Ticket> RelatedTickets { get; set; } = new();
    public List<AssetMaintenanceLog> MaintenanceLogs { get; set; } = new();
    public List<AssetCheckout> Checkouts { get; set; } = new();

    /// <summary>All assets except this one, for the Add Relationship dropdown.</summary>
    public List<Asset> AllOtherAssets { get; set; } = new();

    /// <summary>All active employees for the Assign Asset dropdown.</summary>
    public List<Employee> ActiveEmployees { get; set; } = new();

    // Computed helpers
    public decimal? BookValue
    {
        get
        {
            if (Asset.PurchaseCost == null || Asset.PurchaseDate == null) return null;
            var ageYears = (DateTime.Today - Asset.PurchaseDate.Value).TotalDays / 365.25;
            const double usefulLifeYears = 3.0;
            var depreciated = (double)Asset.PurchaseCost.Value * Math.Max(0, 1 - ageYears / usefulLifeYears);
            return Math.Round((decimal)depreciated, 2);
        }
    }

    public bool WarrantyExpired =>
        Asset.WarrantyExpiry.HasValue && Asset.WarrantyExpiry.Value < DateTime.Today;

    public bool WarrantyExpiringSoon =>
        Asset.WarrantyExpiry.HasValue
        && Asset.WarrantyExpiry.Value >= DateTime.Today
        && Asset.WarrantyExpiry.Value <= DateTime.Today.AddDays(90);
}
