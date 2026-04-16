using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// CMDB-lite relationship between two assets (e.g. "Laptop ConnectedTo DockingStation").
/// Directional: SourceAsset [RelationshipType] TargetAsset.
/// </summary>
public class AssetRelationship
{
    public int Id { get; set; }

    public int SourceAssetId { get; set; }
    public Asset SourceAsset { get; set; } = null!;

    public int TargetAssetId { get; set; }
    public Asset TargetAsset { get; set; } = null!;

    [Required]
    [StringLength(80)]
    public string RelationshipType { get; set; } = "ConnectedTo";

    [StringLength(300)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedByEmail { get; set; }
}
