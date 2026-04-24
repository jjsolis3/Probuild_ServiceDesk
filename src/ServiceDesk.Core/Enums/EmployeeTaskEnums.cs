namespace ServiceDesk.Core.Enums;

public enum EmployeeTaskType
{
    Onboarding = 0,
    Offboarding = 1
}

public enum EmployeeTaskStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Skipped = 3,
    Blocked = 4
}
