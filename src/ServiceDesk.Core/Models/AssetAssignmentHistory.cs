using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AssetAssignmentHistory
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Display(Name = "Assigned To")]
    public int? AssignedToId { get; set; }
    public Employee? AssignedTo { get; set; }

    [Display(Name = "Assigned By")]
    public int? AssignedById { get; set; }
    public Employee? AssignedBy { get; set; }

    [Display(Name = "Assigned Date")]
    public DateTime AssignedDate { get; set; } = DateTime.UtcNow;

    [Display(Name = "Returned Date")]
    public DateTime? ReturnedDate { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}
