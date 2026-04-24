using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class EmployeeTask
{
    public int Id { get; set; }

    [Required]
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = "";

    [StringLength(1000)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [Required]
    public EmployeeTaskType TaskType { get; set; } = EmployeeTaskType.Onboarding;

    [Required]
    public EmployeeTaskStatus Status { get; set; } = EmployeeTaskStatus.Pending;

    [StringLength(200)]
    [Display(Name = "Assigned To")]
    public string? AssignedToEmail { get; set; }

    [Display(Name = "Due Date")]
    [DataType(DataType.Date)]
    public DateTime? DueDate { get; set; }

    [Display(Name = "Completed Date")]
    public DateTime? CompletedDate { get; set; }

    [StringLength(200)]
    [Display(Name = "Completed By")]
    public string? CompletedByEmail { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 0;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

    public bool IsOverdue => Status != EmployeeTaskStatus.Completed
                          && Status != EmployeeTaskStatus.Skipped
                          && DueDate.HasValue && DueDate.Value.Date < DateTime.UtcNow.Date;
}
