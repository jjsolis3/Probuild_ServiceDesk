namespace ServiceDesk.Core.Enums;

public enum TicketCategory
{
    ServiceRequest,
    HardwareIssue,
    SoftwareIssue,
    EmployeeIssue,
    NetworkIssue,
    SecurityIncident,
    Other
}

public enum TicketStatus
{
    Open,
    InProgress,
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
