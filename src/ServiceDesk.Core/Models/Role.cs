using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class Role
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    // Permissions stored as comma-separated values
    // e.g., "ManageTickets,ManageAssets,ManageEmployees,ViewReports,ManageSettings"
    [StringLength(2000)]
    public string? Permissions { get; set; }

    public bool IsSystem { get; set; } = false;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<PortalUser> Users { get; set; } = new List<PortalUser>();
}
