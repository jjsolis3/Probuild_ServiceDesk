using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Password/credential vault entry for an asset. Password is stored encrypted
/// using ASP.NET Core Data Protection (IDataProtectionProvider).
/// </summary>
public class AssetCredential
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Required]
    [StringLength(100)]
    public string Label { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Username { get; set; }

    /// <summary>Encrypted via IDataProtector — never store plaintext here.</summary>
    [Required]
    public string EncryptedPassword { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Url { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedDate { get; set; }

    [Required]
    [StringLength(200)]
    public string CreatedByEmail { get; set; } = string.Empty;
}
