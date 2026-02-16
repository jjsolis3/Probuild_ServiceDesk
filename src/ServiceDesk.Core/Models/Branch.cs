using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class Branch
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Address { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(50)]
    public string? State { get; set; }

    [StringLength(20)]
    [Display(Name = "Zip Code")]
    public string? ZipCode { get; set; }

    [StringLength(20)]
    public string? Phone { get; set; }

    [Display(Name = "Site Manager")]
    public int? SiteManagerId { get; set; }

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    [Display(Name = "Site Manager")]
    public Employee? SiteManager { get; set; }
}
