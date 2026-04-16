using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AssetAuditLog
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Required]
    [StringLength(100)]
    public string FieldName { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? OldValue { get; set; }

    [StringLength(1000)]
    public string? NewValue { get; set; }

    public DateTime ChangedDate { get; set; } = DateTime.UtcNow;

    [Required]
    [StringLength(200)]
    public string ChangedByEmail { get; set; } = string.Empty;
}
