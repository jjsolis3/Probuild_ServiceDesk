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

    // Phase 4 — Charts
    // Last 7 days ticket volume: date labels + counts
    public List<string> TicketVolumeDates { get; set; } = new();
    public List<int> TicketVolumeCounts { get; set; } = new();

    // Status distribution for donut
    public int StatusOpen { get; set; }
    public int StatusInProgress { get; set; }
    public int StatusOnHold { get; set; }
    public int StatusResolved { get; set; }
    public int StatusClosed { get; set; }

    // Priority distribution for bar
    public int PriorityLow { get; set; }
    public int PriorityMedium { get; set; }
    public int PriorityHigh { get; set; }
    public int PriorityCritical { get; set; }

    // Warranty expiry alerts
    public List<Asset> ExpiringWarranties { get; set; } = new();

    // SLA
    public int SlaBreachCount { get; set; }
}
