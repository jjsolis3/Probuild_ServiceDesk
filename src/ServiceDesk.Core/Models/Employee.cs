using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class Employee
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Last Name")]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [StringLength(20)]
    public string? Phone { get; set; }

    [Required]
    [StringLength(100)]
    public string Department { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Job Title")]
    public string? JobTitle { get; set; }

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Hire Date")]
    [DataType(DataType.Date)]
    public DateTime HireDate { get; set; } = DateTime.UtcNow;

    [Display(Name = "Full Name")]
    public string FullName => $"{FirstName} {LastName}";

    // Navigation properties
    public ICollection<Ticket> SubmittedTickets { get; set; } = new List<Ticket>();
    public ICollection<Ticket> AssignedTickets { get; set; } = new List<Ticket>();
    public ICollection<Asset> AssignedAssets { get; set; } = new List<Asset>();
}
