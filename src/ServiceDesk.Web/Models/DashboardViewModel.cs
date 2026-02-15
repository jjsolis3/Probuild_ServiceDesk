using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

public class DashboardViewModel
{
    public int OpenTickets { get; set; }
    public int InProgressTickets { get; set; }
    public int ResolvedThisMonth { get; set; }
    public int TotalAssets { get; set; }
    public int AssignedAssets { get; set; }
    public int ActiveEmployees { get; set; }
    public int ActiveSubscriptions { get; set; }
    public decimal MonthlySubscriptionCost { get; set; }
    public List<Ticket> CriticalTickets { get; set; } = new();
    public List<Ticket> RecentTickets { get; set; } = new();
}
