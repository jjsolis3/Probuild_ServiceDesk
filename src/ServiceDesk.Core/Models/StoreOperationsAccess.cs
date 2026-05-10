using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class StoreOperationsAccess
{
    public int Id { get; set; }

    public int PortalUserId { get; set; }

    public int? GrantedByPortalUserId { get; set; }

    [Display(Name = "Granted Date")]
    public DateTime GrantedDate { get; set; } = DateTime.UtcNow;

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    // Navigation
    public PortalUser PortalUser { get; set; } = null!;
    public PortalUser? GrantedBy { get; set; }
}
