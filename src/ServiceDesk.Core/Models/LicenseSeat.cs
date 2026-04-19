using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class LicenseSeat
{
    public int Id { get; set; }

    [Required]
    public int SoftwareLicenseId { get; set; }
    public SoftwareLicense? SoftwareLicense { get; set; }

    [Display(Name = "Assigned To Employee")]
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    [Display(Name = "Assigned To Asset")]
    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    [Display(Name = "Assigned Date")]
    public DateTime AssignedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    [Display(Name = "Assigned By")]
    public string? AssignedByEmail { get; set; }

    [Display(Name = "Revoked Date")]
    public DateTime? RevokedDate { get; set; }

    [StringLength(200)]
    [Display(Name = "Revoked By")]
    public string? RevokedByEmail { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public bool IsActive => RevokedDate == null;
}
