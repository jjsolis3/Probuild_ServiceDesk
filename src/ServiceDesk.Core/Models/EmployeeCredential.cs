using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// IT-managed credential vault entry for an employee. Stores credentials
/// that IT controls on behalf of the user (AD, VPN, email, etc.).
/// Password is encrypted via ASP.NET Core Data Protection — never stored plaintext.
/// </summary>
public class EmployeeCredential
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [Required]
    [StringLength(100)]
    public string Label { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Username { get; set; }

    /// <summary>Encrypted via IDataProtector.</summary>
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
