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
    /// <summary>Configured minimum training tickets from AppSettings.AiMinTrainingTickets (default 20).</summary>
    public int MinTrainingTickets { get; set; } = 20;

    // ── Confidence bands (count of recs in each band) ─────────────────────────
    public int ConfUnder50 { get; set; }
    public int Conf50To65 { get; set; }
    public int Conf65To80 { get; set; }
    public int ConfOver80 { get; set; }

    // ── Category breakdown ────────────────────────────────────────────────────
    public List<AiCategoryStat> CategoryStats { get; set; } = new();

    // ── 30-day daily trend ────────────────────────────────────────────────────
    public List<AiDailyTrendPoint> DailyTrend { get; set; } = new();

    // ── Training history (last 10 runs) ───────────────────────────────────────
    public List<AiRunLog> RecentTrainingRuns { get; set; } = new();

    // ── Recent recommendations (last 50) ─────────────────────────────────────
    public List<AiRecentRecRow> RecentRecs { get; set; } = new();

    // ── Accuracy tracking ─────────────────────────────────────────────────────
    /// <summary>
    /// Among Approved recs where the ticket is now resolved/closed,
    /// how many still have the category matching what the AI suggested.
    /// </summary>
    public int AccuracyCategoryCorrect { get; set; }
    public int AccuracyCategoryTotal { get; set; }
    public int AccuracyPriorityCorrect { get; set; }
    public int AccuracyPriorityTotal { get; set; }

    public double CategoryAccuracyRate => AccuracyCategoryTotal > 0
        ? Math.Round((double)AccuracyCategoryCorrect / AccuracyCategoryTotal * 100, 1) : 0;
    public double PriorityAccuracyRate => AccuracyPriorityTotal > 0
        ? Math.Round((double)AccuracyPriorityCorrect / AccuracyPriorityTotal * 100, 1) : 0;

    // ── Category gap detection ────────────────────────────────────────────────
    /// <summary>
    /// Ticket clusters from low-confidence (&lt;50%) recommendations.
    /// Each cluster represents a potential missing category or sub-category.
    /// </summary>
    public List<AiCategoryGapCluster> CategoryGaps { get; set; } = new();

    // ── Batch retriage ────────────────────────────────────────────────────────
    /// <summary>Count of open tickets with no active AI recommendation.</summary>
    public int TicketsAwaitingTriage { get; set; }
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

public class AiCategoryGapCluster
{
    /// <summary>The most frequent keyword in this low-confidence ticket cluster.</summary>
    public string KeyTerm { get; set; } = string.Empty;
    /// <summary>Number of low-confidence tickets containing this term.</summary>
    public int TicketCount { get; set; }
    /// <summary>Sample ticket IDs and titles (up to 3).</summary>
    public List<(int Id, string Title)> Samples { get; set; } = new();
}
