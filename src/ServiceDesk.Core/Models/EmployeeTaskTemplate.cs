using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class EmployeeTaskTemplate
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = "";

    [StringLength(1000)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [Required]
    [Display(Name = "Checklist Type")]
    public EmployeeTaskType TaskType { get; set; } = EmployeeTaskType.Onboarding;

    [StringLength(200)]
    [Display(Name = "Default Assignee Email")]
    public string? DefaultAssigneeEmail { get; set; }

    [Display(Name = "Due In Days")]
    public int? DueInDays { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 0;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;
}
