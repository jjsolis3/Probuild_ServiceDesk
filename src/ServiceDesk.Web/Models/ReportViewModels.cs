using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

public class TicketReportViewModel
{
    public int TotalTickets { get; set; }
    public int OpenTickets { get; set; }
    public int InProgressTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public int ClosedTickets { get; set; }
    public Dictionary<TicketCategory, int> ByCategory { get; set; } = new();
    public Dictionary<TicketPriority, int> ByPriority { get; set; } = new();
    public Dictionary<string, int> ByMonth { get; set; } = new();
    public List<AssigneeStats> TopAssignees { get; set; } = new();
    public double AverageResolutionHours { get; set; }
}

public class AssigneeStats
{
    public string Name { get; set; } = string.Empty;
    public int TotalAssigned { get; set; }
    public int Resolved { get; set; }
    public int Open { get; set; }
}

public class AssetReportViewModel
{
    public int TotalAssets { get; set; }
    public decimal TotalValue { get; set; }
    public Dictionary<AssetType, int> ByType { get; set; } = new();
    public Dictionary<AssetStatus, int> ByStatus { get; set; } = new();
    public List<Asset> ExpiringWarranties { get; set; } = new();
    public Dictionary<string, int> ByDepartment { get; set; } = new();
}

public class UserReportViewModel
{
    public List<UserTicketStats> UserStats { get; set; } = new();
    public Dictionary<string, int> TicketsByDepartment { get; set; } = new();
}

public class UserTicketStats
{
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int TicketsSubmitted { get; set; }
    public int TicketsOpen { get; set; }
    public int TicketsResolved { get; set; }
    public int AssetsAssigned { get; set; }
}
