using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

// ── Executive Dashboard ────────────────────────────────────────────────────────

public class ExecReportViewModel
{
    // Service Level KPIs
    public double MttrHours { get; set; }
    public double MttaHours { get; set; }
    public double SlaCompliancePct { get; set; }

    // Volume
    public int TotalOpen { get; set; }
    public int ResolvedLast30Days { get; set; }
    public int CreatedLast30Days { get; set; }
    public int TotalTicketsAllTime { get; set; }

    // Backlog aging (open tickets)
    public int BacklogOver7Days { get; set; }
    public int BacklogOver14Days { get; set; }
    public int BacklogOver30Days { get; set; }

    // SLA compliance broken out by priority
    public List<SlaPriorityRow> SlaByPriority { get; set; } = new();

    // Weekly volume trend (last 12 weeks)
    public List<WeeklyPoint> WeeklyVolume { get; set; } = new();

    // Category breakdown (last 30 days)
    public Dictionary<int, int> ByCategory { get; set; } = new();

    // Agent workload
    public List<AgentWorkloadRow> AgentWorkload { get; set; } = new();

    // AI effectiveness
    public double AiAcceptanceRate { get; set; }
    public double AiCategoryAccuracyPct { get; set; }
    public double AiPriorityAccuracyPct { get; set; }
    public int AiRecsLast30Days { get; set; }
    public bool AiHasData { get; set; }
    public AiRunLog? LatestTrainingRun { get; set; }

    // CSAT
    public bool CsatEnabled { get; set; }
    public double? CsatAvgScore { get; set; }
    public int CsatResponseCount { get; set; }
    public double? CsatResponseRate { get; set; }
}

public class SlaPriorityRow
{
    public TicketPriority Priority { get; set; }
    public int Met { get; set; }
    public int Total { get; set; }
    public double CompliancePct => Total > 0 ? Math.Round((double)Met / Total * 100, 1) : 0;
}

public class WeeklyPoint
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class AgentWorkloadRow
{
    public string AgentName { get; set; } = string.Empty;
    public int OpenTickets { get; set; }
    public int InProgressTickets { get; set; }
    public int ResolvedLast30Days { get; set; }
    public double AvgResolutionHours { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────

public class TicketReportViewModel
{
    public int TotalTickets { get; set; }
    public int OpenTickets { get; set; }
    public int InProgressTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public int ClosedTickets { get; set; }
    /// <summary>Category ID → ticket count.</summary>
    public Dictionary<int, int> ByCategory { get; set; } = new();
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

    // New reporting sections
    public Dictionary<string, int> AgeDistribution { get; set; } = new();
    public List<RefreshCycleRow> RefreshCyclePlanner { get; set; } = new();
    public List<CostCenterRow> CostCenterBreakdown { get; set; } = new();
    public List<HardwareStdRow> HardwareStandardization { get; set; } = new();
}

public class RefreshCycleRow
{
    public AssetType Type { get; set; }
    public int RecommendedLifeYears { get; set; }
    public int TotalCount { get; set; }
    public int OverdueCount { get; set; }
    public int DueSoonCount { get; set; }
    public List<Asset> OverdueAssets { get; set; } = new();
}

public class CostCenterRow
{
    public string Department { get; set; } = string.Empty;
    public int AssetCount { get; set; }
    public decimal TotalCost { get; set; }
    public decimal AvgCost => AssetCount > 0 ? TotalCost / AssetCount : 0;
}

public class HardwareStdRow
{
    public string Make { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public AssetType Type { get; set; }
    public int Count { get; set; }
}

public class UserReportViewModel
{
    public List<UserTicketStats> UserStats { get; set; } = new();
    public Dictionary<string, int> TicketsByDepartment { get; set; } = new();
    public Dictionary<string, int> TicketsByBranch { get; set; } = new();
}

public class UserTicketStats
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public int TicketsSubmitted { get; set; }
    public int TicketsOpen { get; set; }
    public int TicketsResolved { get; set; }
    public int AssetsAssigned { get; set; }
}
