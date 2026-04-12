using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

/// <summary>
/// View model for the AI Triage Statistics dashboard.
/// </summary>
public class AiDashboardViewModel
{
    // ── Summary counts ────────────────────────────────────────────────────────
    public int TotalRecommendations { get; set; }
    public int ApprovedCount { get; set; }
    public int DismissedCount { get; set; }
    public int PendingCount { get; set; }
    public int RecsLast30Days { get; set; }

    // ── Rates and averages ────────────────────────────────────────────────────
    /// <summary>Approved / (Approved + Dismissed), expressed as 0–100.</summary>
    public double ApprovalRate { get; set; }
    /// <summary>Average category prediction confidence, 0–100.</summary>
    public double AvgCategoryConfidence { get; set; }
    /// <summary>Average priority prediction confidence, 0–100.</summary>
    public double AvgPriorityConfidence { get; set; }

    // ── Model status ──────────────────────────────────────────────────────────
    public bool ModelIsTrained { get; set; }
    public int TotalTrainingTickets { get; set; }

    // ── Confidence bands (count of recs in each band) ─────────────────────────
    /// <summary>Recommendations where max(cat, pri) confidence &lt; 50%.</summary>
    public int ConfUnder50 { get; set; }
    /// <summary>50–64%.</summary>
    public int Conf50To65 { get; set; }
    /// <summary>65–79%.</summary>
    public int Conf65To80 { get; set; }
    /// <summary>80% and above.</summary>
    public int ConfOver80 { get; set; }

    // ── Category breakdown ────────────────────────────────────────────────────
    public List<AiCategoryStat> CategoryStats { get; set; } = new();

    // ── 30-day daily trend ────────────────────────────────────────────────────
    public List<AiDailyTrendPoint> DailyTrend { get; set; } = new();

    // ── Training history (last 10 runs) ───────────────────────────────────────
    public List<AiRunLog> RecentTrainingRuns { get; set; } = new();

    // ── Recent recommendations (last 50) ─────────────────────────────────────
    public List<AiRecentRecRow> RecentRecs { get; set; } = new();
}

public class AiCategoryStat
{
    public string CategoryName { get; set; } = string.Empty;
    public int Suggested { get; set; }
    public int Approved { get; set; }
}

public class AiDailyTrendPoint
{
    public string DateLabel { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class AiRecentRecRow
{
    public int RecId { get; set; }
    public int TicketId { get; set; }
    public string TicketTitle { get; set; } = string.Empty;
    public string SuggestedCategory { get; set; } = "—";
    public string SuggestedPriority { get; set; } = "—";
    public int CategoryConfidencePct { get; set; }
    public int PriorityConfidencePct { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string? ReviewedBy { get; set; }
}
