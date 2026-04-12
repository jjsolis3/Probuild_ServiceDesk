using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

public enum SlaRiskLevel { None, Low, Medium, High, Overdue }

/// <summary>
/// Singleton SLA breach-risk calculator.
///
/// Builds baselines by computing the average resolution time per
/// (Category × Priority) combination from all resolved/closed tickets.
/// For each open ticket, it computes how far along the expected resolution
/// window the ticket is, and returns a risk level.
///
/// Baselines are rebuilt every 6 hours or on explicit request.
/// Falls back to hard-coded defaults when historical data is insufficient.
/// </summary>
public class SlaRiskService
{
    // ── State ─────────────────────────────────────────────────────────────────
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlaRiskService> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private Dictionary<(int Cat, int Pri), double> _avgHours = [];
    private DateTime _lastBuilt = DateTime.MinValue;
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromHours(6);

    // Fallback SLA thresholds (hours) when fewer than 3 resolved samples exist
    private static readonly Dictionary<int, double> DefaultSlaHours = new()
    {
        [(int)TicketPriority.Critical] = 4,
        [(int)TicketPriority.High]     = 8,
        [(int)TicketPriority.Medium]   = 48,
        [(int)TicketPriority.Low]      = 120
    };

    public SlaRiskService(IServiceScopeFactory scopeFactory, ILogger<SlaRiskService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures baselines are fresh. Call once per request before iterating
    /// GetRisk() across a page of tickets.
    /// </summary>
    public async Task EnsureBaselinesBuiltAsync()
    {
        if (_avgHours.Count > 0 && DateTime.UtcNow - _lastBuilt < RebuildInterval)
            return;

        if (!await _buildLock.WaitAsync(0)) return;

        try
        {
            if (_avgHours.Count > 0 && DateTime.UtcNow - _lastBuilt < RebuildInterval) return;

            using var scope = _scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

            var resolved = await ctx.Tickets
                .Where(t => (t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
                         && (t.ResolvedDate != null || t.ClosedDate != null))
                .Select(t => new
                {
                    Cat     = (int)t.Category,
                    Pri     = (int)t.Priority,
                    t.CreatedDate,
                    EndDate = t.ResolvedDate ?? t.ClosedDate
                })
                .ToListAsync();

            _avgHours = resolved
                .Where(t => t.EndDate.HasValue)
                .GroupBy(t => (t.Cat, t.Pri))
                .Where(g => g.Count() >= 3) // minimum 3 samples for a meaningful average
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(t => (t.EndDate!.Value - t.CreatedDate).TotalHours));

            _lastBuilt = DateTime.UtcNow;
            _logger.LogInformation("[SlaRisk] Baselines built: {Count} cat/pri combinations.", _avgHours.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SlaRisk] Baseline build failed.");
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Returns the SLA risk level for a single open ticket.
    /// This is a fast, synchronous, in-memory computation.
    /// </summary>
    public SlaRiskLevel GetRisk(int category, int priority, DateTime createdDate)
    {
        var elapsed = (DateTime.UtcNow - createdDate).TotalHours;

        double threshold;
        if (_avgHours.TryGetValue((category, priority), out var avg))
            threshold = avg;
        else if (DefaultSlaHours.TryGetValue(priority, out var def))
            threshold = def;
        else
            threshold = 72;

        var ratio = elapsed / threshold;

        return ratio switch
        {
            >= 1.5  => SlaRiskLevel.Overdue,
            >= 1.0  => SlaRiskLevel.High,
            >= 0.75 => SlaRiskLevel.Medium,
            >= 0.5  => SlaRiskLevel.Low,
            _       => SlaRiskLevel.None
        };
    }

    /// <summary>
    /// Returns a human-readable SLA window label (e.g. "8h", "2d")
    /// for the given category/priority combination.
    /// </summary>
    public string GetThresholdLabel(int category, int priority)
    {
        double hours;
        if (_avgHours.TryGetValue((category, priority), out var avg))
            hours = avg;
        else if (DefaultSlaHours.TryGetValue(priority, out var def))
            hours = def;
        else
            return "72h";

        return hours >= 48 ? $"{hours / 24:F0}d" : $"{hours:F0}h";
    }

    // ── Convenience helpers ───────────────────────────────────────────────────

    public static string RiskBadgeClass(SlaRiskLevel risk) => risk switch
    {
        SlaRiskLevel.Overdue => "danger",
        SlaRiskLevel.High    => "warning",
        SlaRiskLevel.Medium  => "info",
        SlaRiskLevel.Low     => "secondary",
        _                    => ""
    };

    public static string RiskLabel(SlaRiskLevel risk) => risk switch
    {
        SlaRiskLevel.Overdue => "Overdue",
        SlaRiskLevel.High    => "At Risk",
        SlaRiskLevel.Medium  => "Due Soon",
        SlaRiskLevel.Low     => "On Track",
        _                    => ""
    };
}
