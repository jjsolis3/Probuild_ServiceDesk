using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class UserGroup
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<UserGroupMember> Members { get; set; } = new List<UserGroupMember>();
}
