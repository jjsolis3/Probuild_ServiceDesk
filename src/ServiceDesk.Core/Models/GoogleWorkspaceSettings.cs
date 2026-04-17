using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class GoogleWorkspaceSettings
{
    public int Id { get; set; }

    /// <summary>Encrypted service account JSON key (via ASP.NET Core Data Protection).</summary>
    public string? EncryptedServiceAccountJson { get; set; }

    [Required]
    [StringLength(300)]
    [Display(Name = "Admin / Impersonation Email")]
    public string AdminEmail { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [Display(Name = "Workspace Domain")]
    public string Domain { get; set; } = string.Empty;

    public bool IsConfigured { get; set; }

    public DateTime? LastTestedDate { get; set; }

    [StringLength(500)]
    public string? LastTestResult { get; set; }

    public bool LastTestPassed { get; set; }

    /// <summary>HTML signature template. Supports {FIRST_NAME}, {LAST_NAME}, {JOB_TITLE},
    /// {EMAIL}, {PHONE}, {DEPARTMENT}, {BRANCH}.</summary>
    public string? SignatureTemplate { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedDate { get; set; }
}
