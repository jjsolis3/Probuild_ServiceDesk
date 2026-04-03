using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class PortalUser
{
    public int Id { get; set; }

    [Required]
    [EmailAddress]
    [StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Last Name")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(256)]
    public string? PasswordHash { get; set; }

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Last Login")]
    public DateTime? LastLogin { get; set; }

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Password reset
    [StringLength(200)]
    public string? PasswordResetToken { get; set; }

    public DateTime? PasswordResetTokenExpiry { get; set; }

    // Foreign keys
    [Display(Name = "Linked Employee")]
    public int? EmployeeId { get; set; }

    [Display(Name = "Role")]
    public int? RoleId { get; set; }

    [Display(Name = "Full Name")]
    public string FullName => $"{FirstName} {LastName}";

    // Navigation
    public Employee? Employee { get; set; }
    public Role? Role { get; set; }
    public ICollection<UserGroupMember> GroupMemberships { get; set; } = new List<UserGroupMember>();
}
