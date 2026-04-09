using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Enums;

public enum TicketCategory
{
    [Display(Name = "Service Request")]
    ServiceRequest,
    [Display(Name = "Hardware Issue")]
    HardwareIssue,
    [Display(Name = "Software Issue")]
    SoftwareIssue,
    [Display(Name = "Employee Issue")]
    EmployeeIssue,
    [Display(Name = "Network Issue")]
    NetworkIssue,
    [Display(Name = "Security Incident")]
    SecurityIncident,
    [Display(Name = "Other")]
    Other
}

public enum TicketStatus
{
    Open,
    [Display(Name = "In Progress")]
    InProgress,
    [Display(Name = "On Hold")]
    OnHold,
    Resolved,
    Closed,
    Cancelled
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Critical
}
