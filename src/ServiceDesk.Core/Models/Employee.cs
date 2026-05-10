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
    [Display(Name = "Direct Phone")]
    public string? Phone { get; set; }

    [StringLength(10)]
    [Display(Name = "Extension")]
    public string? Extension { get; set; }

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

    [Display(Name = "Branch / Location")]
    public int? BranchId { get; set; }

    [Display(Name = "Last Signature Sync")]
    public DateTime? LastGoogleSignatureSync { get; set; }

    [Display(Name = "Scheduled Offboarding Date")]
    [DataType(DataType.Date)]
    public DateTime? ScheduledOffboardingDate { get; set; }

    [StringLength(200)]
    [Display(Name = "Manager Email")]
    [EmailAddress]
    public string? ManagerEmail { get; set; }

    [StringLength(100)]
    [Display(Name = "Employee Type")]
    public string? EmployeeType { get; set; }

    [StringLength(100)]
    [Display(Name = "Team / Floor Section")]
    public string? FloorSection { get; set; }

    [Display(Name = "Is Contractor")]
    public bool IsContractor { get; set; } = false;

    [Display(Name = "Hourly Rate")]
    [DataType(DataType.Currency)]
    public decimal? HourlyRate { get; set; }

    // Navigation properties
    public Branch? Branch { get; set; }
    public ICollection<Ticket> SubmittedTickets { get; set; } = new List<Ticket>();
    public ICollection<Ticket> AssignedTickets { get; set; } = new List<Ticket>();
    public ICollection<Asset> AssignedAssets { get; set; } = new List<Asset>();
    public ICollection<EmployeeCredential> Credentials { get; set; } = new List<EmployeeCredential>();
    public ICollection<EmployeeTask> Tasks { get; set; } = new List<EmployeeTask>();
    public ICollection<LicenseSeat> LicenseSeats { get; set; } = new List<LicenseSeat>();
}
