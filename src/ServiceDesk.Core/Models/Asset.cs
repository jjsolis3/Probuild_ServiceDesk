using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class Asset
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Asset Tag")]
    public string AssetTag { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Asset Type")]
    public AssetType AssetType { get; set; }

    [Required]
    [Display(Name = "Status")]
    public AssetStatus Status { get; set; } = AssetStatus.Available;

    [StringLength(100)]
    public string? Manufacturer { get; set; }

    [StringLength(100)]
    public string? Model { get; set; }

    [StringLength(100)]
    [Display(Name = "Serial Number")]
    public string? SerialNumber { get; set; }

    [Display(Name = "Purchase Date")]
    [DataType(DataType.Date)]
    public DateTime? PurchaseDate { get; set; }

    [Display(Name = "Purchase Cost")]
    [DataType(DataType.Currency)]
    public decimal? PurchaseCost { get; set; }

    [Display(Name = "Warranty Expiry")]
    [DataType(DataType.Date)]
    public DateTime? WarrantyExpiry { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    // ── Network / System fields ───────────────────────────────────────────────
    [StringLength(50)]
    [Display(Name = "IP Address")]
    public string? IpAddress { get; set; }

    [StringLength(17)]
    [Display(Name = "MAC Address")]
    public string? MacAddress { get; set; }

    [StringLength(200)]
    public string? Hostname { get; set; }

    [StringLength(100)]
    [Display(Name = "OS Version")]
    public string? OsVersion { get; set; }

    [StringLength(50)]
    [Display(Name = "OS Build")]
    public string? OsBuild { get; set; }

    // ── Foreign key ───────────────────────────────────────────────────────────
    [Display(Name = "Assigned To")]
    public int? AssignedToId { get; set; }

    // ── Navigation properties ─────────────────────────────────────────────────
    [Display(Name = "Assigned To")]
    public Employee? AssignedTo { get; set; }

    public ICollection<AssetAssignmentHistory> AssignmentHistory { get; set; } = new List<AssetAssignmentHistory>();
    public ICollection<AssetAuditLog> AuditLogs { get; set; } = new List<AssetAuditLog>();
    public ICollection<AssetCredential> Credentials { get; set; } = new List<AssetCredential>();
    public ICollection<AssetAttachment> Attachments { get; set; } = new List<AssetAttachment>();
    public ICollection<AssetRelationship> RelationshipsFrom { get; set; } = new List<AssetRelationship>();
    public ICollection<AssetRelationship> RelationshipsTo { get; set; } = new List<AssetRelationship>();
    public ICollection<Ticket> RelatedTickets { get; set; } = new List<Ticket>();
    public ICollection<AssetMaintenanceLog> MaintenanceLogs { get; set; } = new List<AssetMaintenanceLog>();
    public ICollection<AssetCheckout> Checkouts { get; set; } = new List<AssetCheckout>();
}
