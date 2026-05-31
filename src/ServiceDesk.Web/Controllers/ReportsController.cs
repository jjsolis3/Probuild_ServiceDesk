using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent,Viewer")]
public class ReportsController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public ReportsController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public IActionResult Index()
    {
        return View();
    }

    public async Task<IActionResult> Executive()
    {
        var now = DateTime.UtcNow;
        var cutoff30 = now.AddDays(-30);

        var tickets = await _context.Tickets
            .Include(t => t.AssignedTo)
            .Where(t => t.Status != TicketStatus.Cancelled)
            .Take(10000)
            .ToListAsync();

        // MTTA: first agent note per ticket
        var firstNotes = await _context.TicketNotes
            .Where(n => n.Source == "Agent" && !n.IsInternal)
            .GroupBy(n => n.TicketId)
            .Select(g => new { TicketId = g.Key, FirstDate = g.Min(n => n.CreatedDate) })
            .ToListAsync();
        var firstNoteDict = firstNotes.ToDictionary(n => n.TicketId, n => n.FirstDate);

        var catLookup = await LoadCategoryLookupAsync();

        // MTTR
        var resolvedTickets = tickets.Where(t => t.ResolvedDate.HasValue).ToList();
        var mttr = resolvedTickets.Any()
            ? resolvedTickets.Average(t => (t.ResolvedDate!.Value - t.CreatedDate).TotalHours)
            : 0;

        // MTTA
        var mttaValues = tickets
            .Where(t => firstNoteDict.ContainsKey(t.Id))
            .Select(t => (firstNoteDict[t.Id] - t.CreatedDate).TotalHours)
            .Where(h => h >= 0)
            .ToList();
        var mtta = mttaValues.Any() ? mttaValues.Average() : 0;

        // SLA compliance per priority
        var slaRows = new List<SlaPriorityRow>();
        foreach (var pri in new[] { TicketPriority.Critical, TicketPriority.High, TicketPriority.Medium, TicketPriority.Low })
        {
            var priResolved = resolvedTickets.Where(t => t.Priority == pri).ToList();
            if (!priResolved.Any()) continue;
            var slaHours = SlaPolicy.GetHours(pri);
            var met = priResolved.Count(t =>
            {
                var due = t.DueDate ?? t.CreatedDate.AddHours(slaHours);
                return t.ResolvedDate!.Value <= due;
            });
            slaRows.Add(new SlaPriorityRow { Priority = pri, Met = met, Total = priResolved.Count });
        }
        var totalSlaMet   = slaRows.Sum(r => r.Met);
        var totalSlaTotal = slaRows.Sum(r => r.Total);
        var slaCompliance = totalSlaTotal > 0 ? (double)totalSlaMet / totalSlaTotal * 100 : 0;

        // Open/volume counts
        var openTickets    = tickets.Where(t => t.Status is TicketStatus.Open or TicketStatus.InProgress or TicketStatus.OnHold).ToList();
        var resolvedLast30 = tickets.Count(t => t.ResolvedDate.HasValue && t.ResolvedDate.Value >= cutoff30);
        var createdLast30  = tickets.Count(t => t.CreatedDate >= cutoff30);

        // Backlog aging
        var backlogOver7  = openTickets.Count(t => (now - t.CreatedDate).TotalDays > 7);
        var backlogOver14 = openTickets.Count(t => (now - t.CreatedDate).TotalDays > 14);
        var backlogOver30 = openTickets.Count(t => (now - t.CreatedDate).TotalDays > 30);

        // Weekly volume: last 12 weeks
        var weekly = new List<WeeklyPoint>();
        for (int i = 11; i >= 0; i--)
        {
            var ws = now.AddDays(-7 * (i + 1)).Date;
            var we = ws.AddDays(7);
            weekly.Add(new WeeklyPoint
            {
                Label = ws.ToString("MMM d"),
                Count = tickets.Count(t => t.CreatedDate >= ws && t.CreatedDate < we)
            });
        }

        // Category breakdown (last 30 days)
        var byCategory = tickets
            .Where(t => t.CreatedDate >= cutoff30)
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        // Agent workload
        var agentWorkload = tickets
            .Where(t => t.AssignedTo != null)
            .GroupBy(t => t.AssignedTo!.FullName)
            .Select(g =>
            {
                var res30 = g.Where(t => t.ResolvedDate.HasValue && t.ResolvedDate.Value >= cutoff30).ToList();
                return new AgentWorkloadRow
                {
                    AgentName          = g.Key,
                    OpenTickets        = g.Count(t => t.Status == TicketStatus.Open),
                    InProgressTickets  = g.Count(t => t.Status == TicketStatus.InProgress),
                    ResolvedLast30Days = res30.Count,
                    AvgResolutionHours = res30.Any()
                        ? Math.Round(res30.Average(t => (t.ResolvedDate!.Value - t.CreatedDate).TotalHours), 1)
                        : 0
                };
            })
            .Where(a => a.OpenTickets + a.InProgressTickets + a.ResolvedLast30Days > 0)
            .OrderByDescending(a => a.OpenTickets + a.InProgressTickets)
            .ToList();

        // AI effectiveness
        var decidedRecs = await _context.AiRecommendations
            .Where(r => r.Status == "Approved" || r.Status == "Dismissed")
            .ToListAsync();
        var aiAcceptance = decidedRecs.Any()
            ? (double)decidedRecs.Count(r => r.Status == "Approved") / decidedRecs.Count * 100
            : 0;

        var approvedRecs = decidedRecs.Where(r => r.Status == "Approved").ToList();
        var resolvedDict = resolvedTickets.ToDictionary(t => t.Id);

        var catCorrect = approvedRecs.Count(r =>
            r.SuggestedCategory.HasValue &&
            resolvedDict.TryGetValue(r.TicketId, out var t2) && t2.Category == r.SuggestedCategory);
        var catTotal = approvedRecs.Count(r =>
            r.SuggestedCategory.HasValue && resolvedDict.ContainsKey(r.TicketId));

        var priCorrect = approvedRecs.Count(r =>
            r.SuggestedPriority.HasValue &&
            resolvedDict.TryGetValue(r.TicketId, out var t3) && (int)t3.Priority == r.SuggestedPriority);
        var priTotal = approvedRecs.Count(r =>
            r.SuggestedPriority.HasValue && resolvedDict.ContainsKey(r.TicketId));

        var aiCatAccuracy = catTotal > 0 ? (double)catCorrect / catTotal * 100 : 0;
        var aiPriAccuracy = priTotal > 0 ? (double)priCorrect / priTotal * 100 : 0;
        var aiRecsLast30  = await _context.AiRecommendations.CountAsync(r => r.CreatedDate >= cutoff30);
        var latestRun     = await _context.AiRunLogs.OrderByDescending(r => r.RunDate).FirstOrDefaultAsync();

        // CSAT
        var csatSetting = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == "CsatSurveyEnabled");
        var csatEnabled = csatSetting?.Value == "true";
        double? csatAvg  = null;
        int csatCount    = 0;
        double? csatRate = null;
        if (csatEnabled)
        {
            try
            {
                var surveys   = await _context.CsatSurveys.ToListAsync();
                var completed = surveys.Where(s => s.Score.HasValue).ToList();
                csatCount = completed.Count;
                csatAvg   = completed.Any() ? Math.Round(completed.Average(s => (double)s.Score!.Value), 1) : null;
                csatRate  = surveys.Any() ? Math.Round((double)completed.Count / surveys.Count * 100, 1) : null;
            }
            catch { /* table not yet created */ }
        }

        ViewBag.CategoriesById = catLookup;

        var model = new ExecReportViewModel
        {
            MttrHours          = Math.Round(mttr,         1),
            MttaHours          = Math.Round(mtta,         1),
            SlaCompliancePct   = Math.Round(slaCompliance, 1),
            TotalOpen          = openTickets.Count,
            ResolvedLast30Days = resolvedLast30,
            CreatedLast30Days  = createdLast30,
            TotalTicketsAllTime = tickets.Count,
            BacklogOver7Days   = backlogOver7,
            BacklogOver14Days  = backlogOver14,
            BacklogOver30Days  = backlogOver30,
            SlaByPriority      = slaRows,
            WeeklyVolume       = weekly,
            ByCategory         = byCategory,
            AgentWorkload      = agentWorkload,
            AiAcceptanceRate      = Math.Round(aiAcceptance,  1),
            AiCategoryAccuracyPct = Math.Round(aiCatAccuracy, 1),
            AiPriorityAccuracyPct = Math.Round(aiPriAccuracy, 1),
            AiRecsLast30Days   = aiRecsLast30,
            AiHasData          = decidedRecs.Any(),
            LatestTrainingRun  = latestRun,
            CsatEnabled        = csatEnabled,
            CsatAvgScore       = csatAvg,
            CsatResponseCount  = csatCount,
            CsatResponseRate   = csatRate
        };

        return View(model);
    }

    /// <summary>
    /// CSAT trend dashboard — score-over-time, distribution, slice-by-
    /// category / assignee, and recent feedback. Range defaults to the
    /// last 90 days. All charts are computed in-memory off a single
    /// query so a 1-year range with a few thousand responses is still
    /// snappy.
    /// </summary>
    public async Task<IActionResult> Csat(DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-90)).Date;
        var spanDays  = Math.Max(1, (rangeTo - rangeFrom).TotalDays);

        // Pull surveys + needed nav data in one query. CsatSurveys is
        // small (one row per surveyed ticket) so projecting in-memory
        // is the simplest correct path here.
        var surveys = await _context.CsatSurveys
            .Include(s => s.Ticket).ThenInclude(t => t!.AssignedTo)
            .Where(s => s.SentDate >= rangeFrom && s.SentDate <= rangeTo)
            .ToListAsync();

        var completed = surveys.Where(s => s.Score.HasValue && s.CompletedDate.HasValue).ToList();

        // ── KPIs ───────────────────────────────────────────────────
        var avgScore      = completed.Count > 0
            ? Math.Round(completed.Average(s => (double)s.Score!.Value), 2)
            : (double?)null;
        var responseRate  = surveys.Count > 0
            ? Math.Round((double)completed.Count / surveys.Count * 100, 1)
            : (double?)null;
        var promoters     = completed.Count(s => s.Score >= 4);
        var neutrals      = completed.Count(s => s.Score == 3);
        var detractors    = completed.Count(s => s.Score <= 2);

        // Trend delta — compare to the immediately-prior period of the
        // same length. Lets the dashboard show "↑ 0.3 vs prior 90 days."
        var priorFrom = rangeFrom.AddDays(-spanDays);
        var priorTo   = rangeFrom.AddTicks(-1);
        var priorScores = await _context.CsatSurveys
            .Where(s => s.Score.HasValue
                     && s.CompletedDate.HasValue
                     && s.SentDate >= priorFrom && s.SentDate <= priorTo)
            .Select(s => s.Score!.Value)
            .ToListAsync();
        double? priorAvg = priorScores.Count > 0
            ? Math.Round(priorScores.Average(s => (double)s), 2) : (double?)null;
        double? delta = (avgScore.HasValue && priorAvg.HasValue)
            ? Math.Round(avgScore.Value - priorAvg.Value, 2) : (double?)null;

        // ── Score over time (monthly buckets) ──────────────────────
        // Build the bucket spine up front so months with zero responses
        // still appear on the chart as gaps rather than collapsing the
        // x-axis.
        var monthlyBuckets = new List<DateTime>();
        var cursor = new DateTime(rangeFrom.Year, rangeFrom.Month, 1);
        var endMonth = new DateTime(rangeTo.Year, rangeTo.Month, 1);
        while (cursor <= endMonth)
        {
            monthlyBuckets.Add(cursor);
            cursor = cursor.AddMonths(1);
        }

        var byMonth = completed
            .GroupBy(s => new DateTime(s.CompletedDate!.Value.Year, s.CompletedDate.Value.Month, 1))
            .ToDictionary(g => g.Key, g => g.ToList());

        var trendLabels = monthlyBuckets.Select(m => m.ToString("MMM yyyy")).ToArray();
        var trendAvg    = monthlyBuckets
            .Select(m => byMonth.TryGetValue(m, out var bucket) && bucket.Count > 0
                ? (object)Math.Round(bucket.Average(s => (double)s.Score!.Value), 2)
                : null!)
            .ToArray();
        var trendCount = monthlyBuckets
            .Select(m => byMonth.TryGetValue(m, out var bucket) ? bucket.Count : 0)
            .ToArray();

        // ── Score distribution (1..5) ──────────────────────────────
        var distribution = new int[5];
        foreach (var s in completed)
            distribution[s.Score!.Value - 1]++;

        // ── By assignee (top 10 with at least 3 responses) ─────────
        var byAssignee = completed
            .Where(s => s.Ticket?.AssignedTo != null)
            .GroupBy(s => new { Id = s.Ticket!.AssignedToId!.Value, Name = s.Ticket!.AssignedTo!.FullName })
            .Select(g => new {
                AgentId  = g.Key.Id,
                Name     = g.Key.Name,
                Count    = g.Count(),
                Avg      = Math.Round(g.Average(s => (double)s.Score!.Value), 2)
            })
            .Where(x => x.Count >= 3)
            .OrderByDescending(x => x.Avg)
            .ThenByDescending(x => x.Count)
            .Take(10)
            .ToList();

        // ── By category ────────────────────────────────────────────
        var byCategory = completed
            .Where(s => s.Ticket != null)
            .GroupBy(s => ((Core.Enums.TicketCategory)s.Ticket!.Category).ToString())
            .Select(g => new {
                Name  = g.Key,
                Count = g.Count(),
                Avg   = Math.Round(g.Average(s => (double)s.Score!.Value), 2)
            })
            .OrderByDescending(x => x.Avg)
            .ToList();

        // ── Recent feedback (latest 20 completed surveys w/ a comment) ──
        var recent = completed
            .Where(s => !string.IsNullOrWhiteSpace(s.Feedback))
            .OrderByDescending(s => s.CompletedDate)
            .Take(20)
            .Select(s => new {
                s.Id, s.TicketId,
                Score          = s.Score!.Value,
                Feedback       = s.Feedback,
                CompletedDate  = s.CompletedDate!.Value,
                Assignee       = s.Ticket?.AssignedTo?.FullName ?? "—",
                Category       = s.Ticket != null
                    ? ((Core.Enums.TicketCategory)s.Ticket.Category).ToString()
                    : "—",
                TicketTitle    = s.Ticket?.Title
            })
            .ToList();

        ViewBag.From            = rangeFrom;
        ViewBag.To              = rangeTo;
        ViewBag.AvgScore        = avgScore;
        ViewBag.PriorAvg        = priorAvg;
        ViewBag.Delta           = delta;
        ViewBag.ResponseRate    = responseRate;
        ViewBag.TotalSent       = surveys.Count;
        ViewBag.TotalCompleted  = completed.Count;
        ViewBag.Promoters       = promoters;
        ViewBag.Neutrals        = neutrals;
        ViewBag.Detractors      = detractors;
        ViewBag.TrendLabels     = trendLabels;
        ViewBag.TrendAvg        = trendAvg;
        ViewBag.TrendCount      = trendCount;
        ViewBag.Distribution    = distribution;
        ViewBag.ByAssignee      = byAssignee;
        ViewBag.ByCategory      = byCategory;
        ViewBag.RecentFeedback  = recent;
        ViewData["Title"]       = "CSAT Trends";
        return View();
    }

    /// <summary>
    /// Agent Performance leaderboard — for each active agent, surfaces
    /// resolved-count, open-count, avg resolution hours, and avg CSAT
    /// score across the configured date range. Defaults to last 30 days
    /// so the page reflects "recent" performance without an admin having
    /// to pick a range first.
    /// </summary>
    public async Task<IActionResult> Agents(DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;

        // Two pulls: tickets assigned to an agent in range (for volume +
        // MTTR) and CSAT surveys completed in the same range (for the
        // satisfaction column). Both are bounded so a multi-year DB
        // doesn't pull the world.
        var ticketsInRange = await _context.Tickets
            .Include(t => t.AssignedTo)
            .Where(t => t.AssignedToId != null
                     && t.CreatedDate <= rangeTo
                     && (t.ResolvedDate == null || t.ResolvedDate >= rangeFrom))
            .ToListAsync();

        var resolvedSurveys = await _context.CsatSurveys
            .Include(s => s.Ticket)
            .Where(s => s.Score.HasValue
                     && s.CompletedDate.HasValue
                     && s.CompletedDate >= rangeFrom
                     && s.CompletedDate <= rangeTo
                     && s.Ticket != null && s.Ticket.AssignedToId != null)
            .ToListAsync();

        var perAgent = ticketsInRange
            .GroupBy(t => new { t.AssignedToId, Name = t.AssignedTo!.FullName })
            .Select(g =>
            {
                var resolved = g.Where(t =>
                    (t.Status == Core.Enums.TicketStatus.Resolved || t.Status == Core.Enums.TicketStatus.Closed)
                    && t.ResolvedDate.HasValue
                    && t.ResolvedDate >= rangeFrom
                    && t.ResolvedDate <= rangeTo).ToList();

                var open = g.Where(t =>
                    t.Status != Core.Enums.TicketStatus.Resolved
                    && t.Status != Core.Enums.TicketStatus.Closed
                    && t.Status != Core.Enums.TicketStatus.Cancelled).Count();

                var mttrHours = resolved.Count > 0
                    ? resolved.Average(t => (t.ResolvedDate!.Value - t.CreatedDate).TotalHours)
                    : (double?)null;

                var agentSurveys = resolvedSurveys
                    .Where(s => s.Ticket!.AssignedToId == g.Key.AssignedToId)
                    .ToList();
                var csat = agentSurveys.Count > 0
                    ? Math.Round(agentSurveys.Average(s => (double)s.Score!.Value), 2)
                    : (double?)null;

                return new
                {
                    AgentId      = g.Key.AssignedToId!.Value,
                    Name         = g.Key.Name,
                    Resolved     = resolved.Count,
                    Open         = open,
                    MttrHours    = mttrHours.HasValue ? Math.Round(mttrHours.Value, 1) : (double?)null,
                    CsatAvg      = csat,
                    CsatCount    = agentSurveys.Count
                };
            })
            .OrderByDescending(x => x.Resolved)
            .ThenByDescending(x => x.CsatAvg ?? 0)
            .ToList();

        // Headline KPIs across all agents in scope.
        var totalResolved = perAgent.Sum(a => a.Resolved);
        var totalOpen     = perAgent.Sum(a => a.Open);
        var avgMttr       = perAgent.Where(a => a.MttrHours.HasValue).Select(a => a.MttrHours!.Value).ToList();
        var fleetMttr     = avgMttr.Count > 0 ? Math.Round(avgMttr.Average(), 1) : (double?)null;
        var fleetCsat     = perAgent.Where(a => a.CsatAvg.HasValue).Select(a => a.CsatAvg!.Value).ToList();
        var fleetCsatAvg  = fleetCsat.Count > 0 ? Math.Round(fleetCsat.Average(), 2) : (double?)null;

        ViewBag.From          = rangeFrom;
        ViewBag.To            = rangeTo;
        ViewBag.PerAgent      = perAgent;
        ViewBag.TotalResolved = totalResolved;
        ViewBag.TotalOpen     = totalOpen;
        ViewBag.FleetMttr     = fleetMttr;
        ViewBag.FleetCsat     = fleetCsatAvg;
        ViewData["Title"]     = "Agent Performance";
        return View();
    }

    public async Task<IActionResult> Tickets()
    {
        var tickets = await _context.Tickets
            .Include(t => t.AssignedTo)
            .Take(5000)
            .ToListAsync();

        // Load categories from DB for display names; fall back to enum for IDs 0-7
        var categoryLookup = await LoadCategoryLookupAsync();
        ViewBag.CategoriesById = categoryLookup;

        var model = new TicketReportViewModel
        {
            TotalTickets = tickets.Count,
            OpenTickets = tickets.Count(t => t.Status == TicketStatus.Open),
            InProgressTickets = tickets.Count(t => t.Status == TicketStatus.InProgress),
            ResolvedTickets = tickets.Count(t => t.Status == TicketStatus.Resolved),
            ClosedTickets = tickets.Count(t => t.Status == TicketStatus.Closed),
            ByCategory = tickets.GroupBy(t => t.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByPriority = tickets.GroupBy(t => t.Priority)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByMonth = tickets.GroupBy(t => t.CreatedDate.ToString("yyyy-MM"))
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count()),
        };

        // Average resolution time for resolved/closed tickets
        var resolvedTickets = tickets.Where(t => t.ResolvedDate.HasValue).ToList();
        if (resolvedTickets.Any())
        {
            model.AverageResolutionHours = resolvedTickets
                .Average(t => (t.ResolvedDate!.Value - t.CreatedDate).TotalHours);
        }

        // Top assignees
        model.TopAssignees = tickets
            .Where(t => t.AssignedTo != null)
            .GroupBy(t => t.AssignedTo!.FullName)
            .Select(g => new AssigneeStats
            {
                Name = g.Key,
                TotalAssigned = g.Count(),
                Resolved = g.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
                Open = g.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress)
            })
            .OrderByDescending(a => a.TotalAssigned)
            .ToList();

        return View(model);
    }

    private async Task<Dictionary<int, string>> LoadCategoryLookupAsync()
    {
        try
        {
            return await _context.TicketCategories
                .ToDictionaryAsync(c => c.Id, c => c.Name);
        }
        catch
        {
            // Table not yet created — fall back to enum
            return Enum.GetValues<TicketCategory>()
                .ToDictionary(c => (int)c, c => c.GetDisplayName());
        }
    }

    private string CategoryName(int id, Dictionary<int, string> lookup) =>
        lookup.TryGetValue(id, out var name) ? name : $"Category {id}";

    public async Task<IActionResult> Assets()
    {
        var assets = await _context.Assets
            .Include(a => a.AssignedTo)
            .ToListAsync();

        var today = DateTime.Today;

        // Age distribution
        var ageDistribution = new Dictionary<string, int>
        {
            ["< 1 year"]  = assets.Count(a => a.PurchaseDate.HasValue && (today - a.PurchaseDate.Value).TotalDays < 365),
            ["1–3 years"] = assets.Count(a => a.PurchaseDate.HasValue && (today - a.PurchaseDate.Value).TotalDays is >= 365 and < 365 * 3),
            ["3–5 years"] = assets.Count(a => a.PurchaseDate.HasValue && (today - a.PurchaseDate.Value).TotalDays is >= 365 * 3 and < 365 * 5),
            ["5+ years"]  = assets.Count(a => a.PurchaseDate.HasValue && (today - a.PurchaseDate.Value).TotalDays >= 365 * 5),
            ["Unknown"]   = assets.Count(a => !a.PurchaseDate.HasValue),
        };

        // Refresh cycle planner — recommended life by type
        var lifespans = new Dictionary<AssetType, int>
        {
            [AssetType.Laptop]          = 3,
            [AssetType.Desktop]         = 4,
            [AssetType.Monitor]         = 6,
            [AssetType.Printer]         = 5,
            [AssetType.Phone]           = 3,
            [AssetType.Tablet]          = 3,
            [AssetType.Server]          = 5,
            [AssetType.NetworkEquipment]= 5,
        };
        var refreshRows = lifespans.Select(kv =>
        {
            var typeAssets = assets.Where(a => a.AssetType == kv.Key && a.Status != AssetStatus.Retired && a.Status != AssetStatus.Disposed).ToList();
            var overdue = typeAssets.Where(a => a.PurchaseDate.HasValue
                && (today - a.PurchaseDate.Value).TotalDays > kv.Value * 365).ToList();
            var dueSoon = typeAssets.Where(a => a.PurchaseDate.HasValue
                && !overdue.Contains(a)
                && (today - a.PurchaseDate.Value).TotalDays > (kv.Value - 1) * 365).ToList();
            return new RefreshCycleRow
            {
                Type                 = kv.Key,
                RecommendedLifeYears = kv.Value,
                TotalCount           = typeAssets.Count,
                OverdueCount         = overdue.Count,
                DueSoonCount         = dueSoon.Count,
                OverdueAssets        = overdue.OrderBy(a => a.PurchaseDate).Take(10).ToList(),
            };
        }).Where(r => r.TotalCount > 0).OrderByDescending(r => r.OverdueCount).ToList();

        // Cost center breakdown
        var costCenterRows = assets
            .Where(a => a.AssignedTo != null)
            .GroupBy(a => a.AssignedTo!.Department)
            .Select(g => new CostCenterRow
            {
                Department = g.Key,
                AssetCount = g.Count(),
                TotalCost  = g.Where(a => a.PurchaseCost.HasValue).Sum(a => a.PurchaseCost!.Value),
            })
            .OrderByDescending(r => r.TotalCost)
            .ToList();

        // Hardware standardization (top make+model combos by type)
        var hardwareRows = assets
            .Where(a => !string.IsNullOrEmpty(a.Manufacturer) && !string.IsNullOrEmpty(a.Model))
            .GroupBy(a => new { a.Manufacturer, a.Model, a.AssetType })
            .Select(g => new HardwareStdRow
            {
                Make  = g.Key.Manufacturer!,
                Model = g.Key.Model!,
                Type  = g.Key.AssetType,
                Count = g.Count(),
            })
            .OrderBy(r => r.Type).ThenByDescending(r => r.Count)
            .ToList();

        var model = new AssetReportViewModel
        {
            TotalAssets = assets.Count,
            TotalValue  = assets.Where(a => a.PurchaseCost.HasValue).Sum(a => a.PurchaseCost!.Value),
            ByType      = assets.GroupBy(a => a.AssetType).ToDictionary(g => g.Key, g => g.Count()),
            ByStatus    = assets.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count()),
            ExpiringWarranties = assets
                .Where(a => a.WarrantyExpiry.HasValue
                         && a.WarrantyExpiry.Value <= DateTime.UtcNow.AddMonths(3)
                         && a.WarrantyExpiry.Value >= DateTime.UtcNow)
                .OrderBy(a => a.WarrantyExpiry).ToList(),
            ByDepartment          = assets.Where(a => a.AssignedTo != null)
                                          .GroupBy(a => a.AssignedTo!.Department)
                                          .ToDictionary(g => g.Key, g => g.Count()),
            AgeDistribution       = ageDistribution,
            RefreshCyclePlanner   = refreshRows,
            CostCenterBreakdown   = costCenterRows,
            HardwareStandardization = hardwareRows,
        };

        return View(model);
    }

    public async Task<IActionResult> Users()
    {
        // Project employee base data — no SubmittedTickets Include
        var employees = await _context.Employees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new {
                e.Id,
                FullName       = e.FirstName + " " + e.LastName,
                Department     = e.Department ?? "—",
                BranchName     = e.Branch != null ? e.Branch.Name : "—",
                AssetsAssigned = e.AssignedAssets.Count()
            })
            .ToListAsync();

        var empIds = employees.Select(e => e.Id).ToList();

        // Single DB query aggregates all ticket stats grouped by submitter
        var ticketStats = await _context.Tickets
            .AsNoTracking()
            .Where(t => empIds.Contains(t.SubmittedById))
            .GroupBy(t => t.SubmittedById)
            .Select(g => new {
                EmployeeId = g.Key,
                Total    = g.Count(),
                Open     = g.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress),
                Resolved = g.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
            })
            .ToDictionaryAsync(x => x.EmployeeId);

        var userStats = employees.Select(e => {
            ticketStats.TryGetValue(e.Id, out var s);
            return new UserTicketStats {
                EmployeeName     = e.FullName,
                Department       = e.Department,
                Branch           = e.BranchName,
                TicketsSubmitted = s?.Total    ?? 0,
                TicketsOpen      = s?.Open     ?? 0,
                TicketsResolved  = s?.Resolved ?? 0,
                AssetsAssigned   = e.AssetsAssigned
            };
        })
        .OrderByDescending(u => u.TicketsSubmitted)
        .ToList();

        var model = new UserReportViewModel
        {
            UserStats = userStats,
            TicketsByDepartment = userStats
                .GroupBy(u => u.Department)
                .ToDictionary(g => g.Key, g => g.Sum(u => u.TicketsSubmitted)),
            TicketsByBranch = userStats
                .GroupBy(u => u.Branch)
                .ToDictionary(g => g.Key, g => g.Sum(u => u.TicketsSubmitted))
        };

        return View(model);
    }

    // API: Get tickets filtered by status for report KPI modals
    [HttpGet]
    public async Task<IActionResult> TicketsByStatus(string status)
    {
        var query = _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .AsQueryable();

        query = status switch
        {
            "Open" => query.Where(t => t.Status == TicketStatus.Open),
            "InProgress" => query.Where(t => t.Status == TicketStatus.InProgress),
            "Resolved" => query.Where(t => t.Status == TicketStatus.Resolved),
            "Closed" => query.Where(t => t.Status == TicketStatus.Closed),
            _ => query
        };

        var catLookup = await LoadCategoryLookupAsync();

        // Materialize with raw values first, then apply display names in memory
        var raw = await query
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title, t.Category, t.Priority, t.Status,
                SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName,
                AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned",
                Created = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToListAsync();

        return Json(raw.Select(t => new {
            t.Id, t.Title,
            Category = CategoryName(t.Category, catLookup),
            Priority = t.Priority.GetDisplayName(),
            Status = t.Status.GetDisplayName(),
            t.SubmittedBy, t.AssignedTo, t.Created
        }));
    }

    // API: Get tickets by category for report table modals
    // The `category` param is a numeric category ID (as string) e.g. "1"
    [HttpGet]
    public async Task<IActionResult> TicketsByCategory(string category)
    {
        var catLookup = await LoadCategoryLookupAsync();

        var tickets = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .ToListAsync();

        // Support both numeric IDs ("1") and legacy enum names ("HardwareIssue")
        var filtered = int.TryParse(category, out int catId)
            ? tickets.Where(t => t.Category == catId)
            : tickets.Where(t => Enum.TryParse<TicketCategory>(category, out var e) && t.Category == (int)e);

        return Json(filtered
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title,
                Category = CategoryName(t.Category, catLookup),
                Priority = t.Priority.GetDisplayName(),
                Status   = t.Status.GetDisplayName(),
                SubmittedBy = t.SubmittedBy?.FullName ?? "Unknown",
                AssignedTo  = t.AssignedTo?.FullName  ?? "Unassigned",
                Created = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToList());
    }

    // API: Get tickets by priority for report table modals
    [HttpGet]
    public async Task<IActionResult> TicketsByPriority(string priority)
    {
        var catLookup = await LoadCategoryLookupAsync();

        var tickets = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .ToListAsync();

        var filtered = tickets
            .Where(t => t.Priority.ToString() == priority)
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title,
                Category = CategoryName(t.Category, catLookup),
                Priority = t.Priority.GetDisplayName(),
                Status   = t.Status.GetDisplayName(),
                SubmittedBy = t.SubmittedBy?.FullName ?? "Unknown",
                AssignedTo  = t.AssignedTo?.FullName  ?? "Unassigned",
                Created = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToList();

        return Json(filtered);
    }

    // API: Get tickets assigned to a specific agent for report table modals
    [HttpGet]
    public async Task<IActionResult> TicketsByAssignee(string assignee)
    {
        var catLookup = await LoadCategoryLookupAsync();

        var tickets = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .Where(t => t.AssignedTo != null)
            .ToListAsync();

        var filtered = tickets
            .Where(t => (t.AssignedTo!.FirstName + " " + t.AssignedTo.LastName).Trim() == assignee)
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title,
                Category = CategoryName(t.Category, catLookup),
                Priority = t.Priority.GetDisplayName(),
                Status   = t.Status.GetDisplayName(),
                SubmittedBy = t.SubmittedBy?.FullName ?? "Unknown",
                AssignedTo  = t.AssignedTo?.FullName  ?? "Unassigned",
                Created = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToList();

        return Json(filtered);
    }

    // ==================== Drill-down JSON endpoints for the new reports ====================
    //
    // Each endpoint returns a shape compatible with the matching profile
    // in _DrillDownModal.cshtml (tickets / surveys). Range params mirror
    // the parent report's filters so a click on a card opens exactly the
    // slice that produced that number — no double-counting, no skew.

    // GET /Reports/TicketsForAgent?agentId=12&scope=resolved&from=...&to=...
    // scope: "resolved" → tickets the agent moved to Resolved/Closed in
    //                     the window (matches the leaderboard Resolved cell)
    //        "open"     → that agent's currently open tickets (live, not
    //                     date-bounded — matches the Open cell)
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> TicketsForAgent(int agentId, string scope, DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;

        var query = _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .Where(t => t.AssignedToId == agentId)
            .AsQueryable();

        if (string.Equals(scope, "resolved", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(t =>
                (t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
                && t.ResolvedDate.HasValue
                && t.ResolvedDate >= rangeFrom
                && t.ResolvedDate <= rangeTo);
        }
        else if (string.Equals(scope, "open", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(t =>
                t.Status != TicketStatus.Resolved
                && t.Status != TicketStatus.Closed
                && t.Status != TicketStatus.Cancelled);
        }

        var catLookup = await LoadCategoryLookupAsync();
        var raw = await query
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title, t.Category, t.Priority, t.Status,
                SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName,
                AssignedTo  = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned",
                Created     = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToListAsync();

        return Json(raw.Select(t => new {
            t.Id, t.Title,
            Category = CategoryName(t.Category, catLookup),
            Priority = t.Priority.GetDisplayName(),
            Status   = t.Status.GetDisplayName(),
            t.SubmittedBy, t.AssignedTo, t.Created
        }));
    }

    // GET /Reports/SurveysForAgent?agentId=12&from=...&to=...
    // Returns the surveys (completed, scored) for tickets the agent
    // handled in the window. Powers the CSAT cell drill-down on the
    // Agent leaderboard and the Top Performers row on the CSAT page.
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> SurveysForAgent(int agentId, DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;

        var surveys = await _context.CsatSurveys
            .Include(s => s.Ticket).ThenInclude(t => t!.AssignedTo)
            .Where(s => s.Score.HasValue
                     && s.CompletedDate.HasValue
                     && s.CompletedDate >= rangeFrom
                     && s.CompletedDate <= rangeTo
                     && s.Ticket != null
                     && s.Ticket.AssignedToId == agentId)
            .OrderByDescending(s => s.CompletedDate)
            .ToListAsync();

        return Json(surveys.Select(s => new {
            ticketId      = s.TicketId,
            score         = s.Score!.Value,
            feedback      = s.Feedback,
            category      = ((Core.Enums.TicketCategory)s.Ticket!.Category).ToString(),
            assignee      = s.Ticket?.AssignedTo?.FullName ?? "—",
            title         = s.Ticket?.Title,
            completedDate = s.CompletedDate!.Value.ToString("MMM d, yyyy")
        }));
    }

    // GET /Reports/SurveysByTone?tone=promoter|neutral|detractor&from=...&to=...
    // Powers the Promoters / Detractors KPI cards on the CSAT page.
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> SurveysByTone(string tone, DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-90)).Date;

        var q = _context.CsatSurveys
            .Include(s => s.Ticket).ThenInclude(t => t!.AssignedTo)
            .Where(s => s.Score.HasValue
                     && s.CompletedDate.HasValue
                     && s.CompletedDate >= rangeFrom
                     && s.CompletedDate <= rangeTo);

        q = (tone?.ToLowerInvariant()) switch
        {
            "promoter"  => q.Where(s => s.Score >= 4),
            "neutral"   => q.Where(s => s.Score == 3),
            "detractor" => q.Where(s => s.Score <= 2),
            _           => q
        };

        var surveys = await q.OrderByDescending(s => s.CompletedDate).ToListAsync();

        return Json(surveys.Select(s => new {
            ticketId      = s.TicketId,
            score         = s.Score!.Value,
            feedback      = s.Feedback,
            category      = s.Ticket != null ? ((Core.Enums.TicketCategory)s.Ticket.Category).ToString() : "—",
            assignee      = s.Ticket?.AssignedTo?.FullName ?? "—",
            title         = s.Ticket?.Title,
            completedDate = s.CompletedDate!.Value.ToString("MMM d, yyyy")
        }));
    }

    // GET /Reports/SurveysByCategory?category=HardwareIssue&from=...&to=...
    // Drill-down for "Avg Score by Category" rows on the CSAT page.
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> SurveysByCategory(string category, DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-90)).Date;

        // Parse the category string back to its enum int for the WHERE.
        if (!Enum.TryParse<Core.Enums.TicketCategory>(category, true, out var catEnum))
            return Json(Array.Empty<object>());
        var catInt = (int)catEnum;

        var surveys = await _context.CsatSurveys
            .Include(s => s.Ticket).ThenInclude(t => t!.AssignedTo)
            .Where(s => s.Score.HasValue
                     && s.CompletedDate.HasValue
                     && s.CompletedDate >= rangeFrom
                     && s.CompletedDate <= rangeTo
                     && s.Ticket != null
                     && s.Ticket.Category == catInt)
            .OrderByDescending(s => s.CompletedDate)
            .ToListAsync();

        return Json(surveys.Select(s => new {
            ticketId      = s.TicketId,
            score         = s.Score!.Value,
            feedback      = s.Feedback,
            category      = ((Core.Enums.TicketCategory)s.Ticket!.Category).ToString(),
            assignee      = s.Ticket?.AssignedTo?.FullName ?? "—",
            title         = s.Ticket?.Title,
            completedDate = s.CompletedDate!.Value.ToString("MMM d, yyyy")
        }));
    }

    // GET /Reports/TicketsResolvedInRange?from=...&to=...
    // Fleet-wide drill — powers the Resolved KPI tile on the Agent
    // Performance page (all agents combined).
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> TicketsResolvedInRange(DateTime? from, DateTime? to)
    {
        var rangeTo   = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);
        var rangeFrom = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;

        var catLookup = await LoadCategoryLookupAsync();
        var raw = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .Where(t => (t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
                     && t.ResolvedDate.HasValue
                     && t.ResolvedDate >= rangeFrom
                     && t.ResolvedDate <= rangeTo)
            .OrderByDescending(t => t.ResolvedDate)
            .Select(t => new {
                t.Id, t.Title, t.Category, t.Priority, t.Status,
                SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName,
                AssignedTo  = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned",
                Created     = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToListAsync();

        return Json(raw.Select(t => new {
            t.Id, t.Title,
            Category = CategoryName(t.Category, catLookup),
            Priority = t.Priority.GetDisplayName(),
            Status   = t.Status.GetDisplayName(),
            t.SubmittedBy, t.AssignedTo, t.Created
        }));
    }

    // GET /Reports/TicketsCurrentlyOpen
    // Fleet-wide drill — powers the Open KPI tile on the Agent
    // Performance page. Not date-bounded by design: "what's on the
    // queue right now."
    [HttpGet]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> TicketsCurrentlyOpen()
    {
        var catLookup = await LoadCategoryLookupAsync();
        var raw = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .Where(t => t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new {
                t.Id, t.Title, t.Category, t.Priority, t.Status,
                SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName,
                AssignedTo  = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned",
                Created     = t.CreatedDate.ToString("MMM dd, yyyy")
            })
            .ToListAsync();

        return Json(raw.Select(t => new {
            t.Id, t.Title,
            Category = CategoryName(t.Category, catLookup),
            Priority = t.Priority.GetDisplayName(),
            Status   = t.Status.GetDisplayName(),
            t.SubmittedBy, t.AssignedTo, t.Created
        }));
    }

    // ==================== STORE ORDERS REPORT ====================

    // GET: Reports/StoreOrders
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> StoreOrders(
        int? year = null, int? quarter = null, string? status = null)
    {
        var now = DateTime.UtcNow;
        year    ??= now.Year;
        quarter ??= (now.Month - 1) / 3 + 1;

        var query = _context.StoreOrders
            .Include(o => o.PortalUser)
            .Include(o => o.Items)
            .AsQueryable();

        query = query.Where(o => o.Year == year && o.Quarter == quarter);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var orders = await query
            .OrderBy(o => o.PortalUser.LastName)
            .ThenBy(o => o.OrderDate)
            .ToListAsync();

        // Summary rollup per product
        var productTotals = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.StoreProductId, i.ProductNameSnapshot, i.ProductCategorySnapshot })
            .Select(g => new
            {
                ProductId = g.Key.StoreProductId,
                Name      = g.Key.ProductNameSnapshot,
                Category  = g.Key.ProductCategorySnapshot,
                TotalQty  = g.Sum(i => i.Quantity),
                OrderCount = g.Select(i => i.StoreOrderId).Distinct().Count()
            })
            .OrderBy(p => p.Category).ThenBy(p => p.Name)
            .ToList();

        ViewBag.Year         = year;
        ViewBag.Quarter      = quarter;
        ViewBag.Status       = status;
        ViewBag.ProductTotals = productTotals;
        ViewBag.Statuses     = new[] { "Pending", "Confirmed", "Fulfilled", "Cancelled" };

        return View(orders);
    }

    // GET: Reports/StoreOrdersExport  — XLSX download
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> StoreOrdersExport(int? year, int? quarter, string? status)
    {
        var now = DateTime.UtcNow;
        year    ??= now.Year;
        quarter ??= (now.Month - 1) / 3 + 1;

        var query = _context.StoreOrders
            .Include(o => o.PortalUser)
            .Include(o => o.Branch)
            .Include(o => o.Items)
            .Where(o => o.Year == year && o.Quarter == quarter)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var orders = await query.OrderBy(o => o.OrderNumber).ToListAsync();

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ProBuild";

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Store Orders");

        // ── Colour palette ────────────────────────────────────────────────
        var brandBlue   = XLColor.FromHtml("#0d6efd");
        var headerGray  = XLColor.FromHtml("#343a40");
        var altRow      = XLColor.FromHtml("#f8f9fa");
        var totalBg     = XLColor.FromHtml("#e9ecef");

        // ── Row 1: Company name ───────────────────────────────────────────
        var r1 = ws.Cell(1, 1);
        r1.Value = companyName;
        r1.Style.Font.Bold        = true;
        r1.Style.Font.FontSize    = 18;
        r1.Style.Font.FontColor   = brandBlue;
        ws.Range(1, 1, 1, 18).Merge();

        // ── Row 2: Document title ─────────────────────────────────────────
        var r2 = ws.Cell(2, 1);
        r2.Value = "Quarterly Store — Purchase Order Sheet";
        r2.Style.Font.Bold      = true;
        r2.Style.Font.FontSize  = 13;
        r2.Style.Font.FontColor = XLColor.FromHtml("#495057");
        ws.Range(2, 1, 2, 18).Merge();

        // ── Row 3: Quarter / Year ─────────────────────────────────────────
        var r3 = ws.Cell(3, 1);
        r3.Value = $"Q{quarter} — {year}";
        r3.Style.Font.Bold      = true;
        r3.Style.Font.FontSize  = 11;
        r3.Style.Font.FontColor = XLColor.FromHtml("#6c757d");
        ws.Range(3, 1, 3, 18).Merge();

        // ── Row 4: Meta info ──────────────────────────────────────────────
        var r4 = ws.Cell(4, 1);
        var filterDesc = !string.IsNullOrEmpty(status) ? $"  |  Status filter: {status}" : "  |  All statuses";
        r4.Value = $"Generated: {now:MMM d, yyyy 'at' h:mm tt} UTC{filterDesc}  |  Orders: {orders.Count}";
        r4.Style.Font.FontSize  = 9;
        r4.Style.Font.FontColor = XLColor.FromHtml("#6c757d");
        ws.Range(4, 1, 4, 18).Merge();

        // ── Row 5: spacer ─────────────────────────────────────────────────
        ws.Row(5).Height = 6;

        // ── Row 6: Column headers ─────────────────────────────────────────
        var headers = new[]
        {
            "Order #", "Employee Name", "Email", "Branch / Location",
            "Order Date", "Quarter", "Year", "Status",
            "Product", "Category", "Gender", "Size", "Color", "Custom Options",
            "Qty", "Unit Price", "Subtotal", "Notes"
        };

        for (int col = 1; col <= headers.Length; col++)
        {
            var hCell = ws.Cell(6, col);
            hCell.Value = headers[col - 1];
            hCell.Style.Font.Bold           = true;
            hCell.Style.Font.FontColor      = XLColor.White;
            hCell.Style.Fill.BackgroundColor = headerGray;
            hCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            hCell.Style.Border.BottomBorder  = XLBorderStyleValues.Thin;
        }

        // ── Data rows ─────────────────────────────────────────────────────
        int row = 7;
        decimal grandTotal = 0m;
        int    itemCount   = 0;

        foreach (var order in orders)
        {
            var branchLabel = order.Branch?.Name ?? order.BranchNameSnapshot ?? "Unassigned";
            bool isFirst    = true;

            foreach (var item in order.Items)
            {
                var customParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(item.CustomSelectionsJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(item.CustomSelectionsJson);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            customParts.Add($"{prop.Name}: {prop.Value.GetString()}");
                    }
                    catch { /* malformed JSON — skip */ }
                }

                decimal subtotalVal = item.UnitPriceSnapshot.HasValue
                    ? item.UnitPriceSnapshot.Value * item.Quantity
                    : 0m;
                grandTotal += subtotalVal;
                itemCount++;

                bool altBg = (row % 2 == 0);
                var rowBg  = altBg ? altRow : XLColor.White;

                void SetCell(int col, object? val, bool isMoney = false, bool isCenter = false)
                {
                    var c = ws.Cell(row, col);
                    if (val is decimal d) c.Value = d;
                    else if (val is int i) c.Value = i;
                    else c.Value = val?.ToString() ?? string.Empty;
                    c.Style.Fill.BackgroundColor     = rowBg;
                    c.Style.Border.BottomBorder      = XLBorderStyleValues.Hair;
                    c.Style.Border.BottomBorderColor = XLColor.FromHtml("#dee2e6");
                    if (isMoney)  c.Style.NumberFormat.Format = "#,##0.00";
                    if (isCenter) c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                SetCell(1,  isFirst ? order.OrderNumber : string.Empty, isCenter: true);
                SetCell(2,  order.PortalUser.FullName);
                SetCell(3,  order.PortalUser.Email);
                SetCell(4,  branchLabel);
                SetCell(5,  order.OrderDate.ToString("yyyy-MM-dd HH:mm"), isCenter: true);
                SetCell(6,  $"Q{order.Quarter}", isCenter: true);
                SetCell(7,  order.Year, isCenter: true);
                SetCell(8,  order.Status, isCenter: true);
                SetCell(9,  item.ProductNameSnapshot);
                SetCell(10, item.ProductCategorySnapshot ?? string.Empty);
                SetCell(11, item.SelectedGender ?? string.Empty, isCenter: true);
                SetCell(12, item.SelectedSize   ?? string.Empty, isCenter: true);
                SetCell(13, item.SelectedColor  ?? string.Empty, isCenter: true);
                SetCell(14, string.Join("; ", customParts));
                SetCell(15, item.Quantity, isCenter: true);
                SetCell(16, item.UnitPriceSnapshot.HasValue ? (object)item.UnitPriceSnapshot.Value : string.Empty, isMoney: true, isCenter: true);
                SetCell(17, subtotalVal > 0 ? (object)subtotalVal : string.Empty, isMoney: true, isCenter: true);
                SetCell(18, isFirst ? (order.Notes ?? string.Empty) : string.Empty);

                row++;
                isFirst = false;
            }
        }

        // ── Totals row ────────────────────────────────────────────────────
        int totalsRow = row + 1;
        var tLabel = ws.Cell(totalsRow, 14);
        tLabel.Value = $"TOTAL  ({itemCount} line item{(itemCount != 1 ? "s" : "")})";
        tLabel.Style.Font.Bold           = true;
        tLabel.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        tLabel.Style.Fill.BackgroundColor = totalBg;
        ws.Range(totalsRow, 1, totalsRow, 14).Style.Fill.BackgroundColor = totalBg;
        ws.Range(totalsRow, 1, totalsRow, 14).Style.Border.TopBorder     = XLBorderStyleValues.Medium;

        var tVal = ws.Cell(totalsRow, 17);
        tVal.Value = grandTotal;
        tVal.Style.Font.Bold             = true;
        tVal.Style.NumberFormat.Format   = "#,##0.00";
        tVal.Style.Fill.BackgroundColor  = totalBg;
        tVal.Style.Border.TopBorder      = XLBorderStyleValues.Medium;
        ws.Range(totalsRow, 15, totalsRow, 18).Style.Fill.BackgroundColor = totalBg;
        ws.Range(totalsRow, 15, totalsRow, 18).Style.Border.TopBorder     = XLBorderStyleValues.Medium;

        // ── Column widths ─────────────────────────────────────────────────
        ws.Column(1).Width  = 13;  // Order #
        ws.Column(2).Width  = 22;  // Name
        ws.Column(3).Width  = 26;  // Email
        ws.Column(4).Width  = 20;  // Branch
        ws.Column(5).Width  = 18;  // Order Date
        ws.Column(6).Width  = 9;   // Quarter
        ws.Column(7).Width  = 7;   // Year
        ws.Column(8).Width  = 12;  // Status
        ws.Column(9).Width  = 24;  // Product
        ws.Column(10).Width = 16;  // Category
        ws.Column(11).Width = 10;  // Gender
        ws.Column(12).Width = 10;  // Size
        ws.Column(13).Width = 12;  // Color
        ws.Column(14).Width = 28;  // Custom Options
        ws.Column(15).Width = 7;   // Qty
        ws.Column(16).Width = 12;  // Unit Price
        ws.Column(17).Width = 12;  // Subtotal
        ws.Column(18).Width = 30;  // Notes

        ws.SheetView.FreezeRows(6);

        using var ms = new System.IO.MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var filename = $"StoreOrders-Q{quarter}-{year}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            filename);
    }

    // POST: Reports/StoreOrderUpdateStatus — update order status from the report dashboard
    [Authorize(Roles = "Admin,IT Agent")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> StoreOrderUpdateStatus(int orderId, string newStatus, int year, int quarter)
    {
        var order = await _context.StoreOrders.FindAsync(orderId);
        if (order == null) return NotFound();

        var allowed = new[] { "Pending", "Confirmed", "Fulfilled", "Cancelled" };
        if (!allowed.Contains(newStatus))
        {
            TempData["Error"] = "Invalid status.";
            return RedirectToAction(nameof(StoreOrders), new { year, quarter });
        }

        order.Status = newStatus;
        await _context.SaveChangesAsync();

        TempData["Success"] = $"Order {order.OrderNumber} updated to {newStatus}.";
        return RedirectToAction(nameof(StoreOrders), new { year, quarter });
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ──────────────────────────── Contractor Payroll Reports ────────────────────────────

    /// <summary>
    /// Payroll report dashboard. Filter receipts by date range, contractor,
    /// rate-type composition, and status. Aggregates totals across the
    /// matching receipts and shows a per-contractor roll-up so AP can see
    /// at a glance who got paid what during the period.
    /// </summary>
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> Payroll(DateTime? from, DateTime? to,
        int? contractorId, string? rateType, string? status)
    {
        // Default window = current calendar month
        var now = DateTime.UtcNow;
        var defaultFrom = new DateTime(now.Year, now.Month, 1);
        var defaultTo   = defaultFrom.AddMonths(1).AddDays(-1);
        from ??= defaultFrom;
        to   ??= defaultTo;
        // Normalize the window endpoints to inclusive day boundaries: the
        // user picks calendar dates, but PeriodStart/PeriodEnd carry full
        // timestamps. Without this, a receipt with PeriodStart=May 31 09:00
        // and `to`=May 31 00:00 silently drops out of the boundary day.
        var fromBoundary = from.Value.Date;
        var toBoundary   = to.Value.Date.AddDays(1).AddTicks(-1);

        var query = _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
            .Where(r => r.PeriodStart <= toBoundary && r.PeriodEnd >= fromBoundary);

        if (contractorId.HasValue)
            query = query.Where(r => r.ContractorId == contractorId.Value);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(r => r.Status == status);

        var receipts = await query.OrderByDescending(r => r.PeriodStart).ToListAsync();

        // Rate-type filter applied client-side (in-memory) so we can use the
        // same enum-derived predicates as the AdminPayroll list.
        if (!string.IsNullOrEmpty(rateType))
        {
            receipts = rateType switch
            {
                "StandardOnly" => receipts.Where(r => r.TotalEmergencyHours == 0).ToList(),
                "HasEmergency" => receipts.Where(r => r.TotalEmergencyHours > 0).ToList(),
                "HasRetainer"  => receipts.Where(r => r.TotalRetainerAmountApplied > 0).ToList(),
                _              => receipts
            };
        }

        // Aggregate metrics
        ViewBag.TotalReceipts        = receipts.Count;
        ViewBag.TotalContractors     = receipts.Select(r => r.ContractorId).Distinct().Count();
        ViewBag.TotalStandardHours   = receipts.Sum(r => r.TotalStandardHours);
        ViewBag.TotalEmergencyHours  = receipts.Sum(r => r.TotalEmergencyHours);
        ViewBag.TotalRetainerHours   = receipts.Sum(r => r.TotalRetainerHoursApplied);
        ViewBag.TotalRetainerAmount  = receipts.Sum(r => r.TotalRetainerAmountApplied);
        ViewBag.TotalAmount          = receipts.Sum(r => r.TotalAmount);
        ViewBag.PaidAmount           = receipts.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount);
        ViewBag.ApprovedAmount       = receipts.Where(r => r.Status == "Approved").Sum(r => r.TotalAmount);
        ViewBag.PendingAmount        = receipts.Where(r => r.Status == "Submitted").Sum(r => r.TotalAmount);

        // Per-contractor roll-up
        var byContractor = receipts
            .GroupBy(r => r.ContractorId)
            .Select(g => new
            {
                ContractorId   = g.Key,
                ContractorName = g.First().Contractor != null
                    ? (g.First().Contractor!.FirstName + " " + g.First().Contractor!.LastName)
                    : "(unknown)",
                ReceiptCount    = g.Count(),
                StandardHours   = g.Sum(r => r.TotalStandardHours),
                EmergencyHours  = g.Sum(r => r.TotalEmergencyHours),
                RetainerApplied = g.Sum(r => r.TotalRetainerAmountApplied),
                TotalAmount     = g.Sum(r => r.TotalAmount),
                PaidAmount      = g.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount)
            })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();
        ViewBag.ByContractor = byContractor;

        ViewBag.Contractors = await _context.Employees
            .Where(e => e.IsContractor)
            .OrderBy(e => e.LastName)
            .ToListAsync();

        ViewBag.FilterFrom         = from;
        ViewBag.FilterTo           = to;
        ViewBag.FilterContractorId = contractorId;
        ViewBag.FilterRateType     = rateType;
        ViewBag.FilterStatus       = status;

        return View(receipts);
    }

    /// <summary>
    /// Excel export of the same payroll report. Two sheets — Summary (one row
    /// per contractor) and Receipts (one row per receipt with full breakdown).
    /// </summary>
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> PayrollExport(DateTime? from, DateTime? to,
        int? contractorId, string? rateType, string? status)
    {
        var now = DateTime.UtcNow;
        var defaultFrom = new DateTime(now.Year, now.Month, 1);
        var defaultTo   = defaultFrom.AddMonths(1).AddDays(-1);
        from ??= defaultFrom;
        to   ??= defaultTo;

        var query = _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
            .Where(r => r.PeriodStart <= to && r.PeriodEnd >= from);

        if (contractorId.HasValue)
            query = query.Where(r => r.ContractorId == contractorId.Value);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(r => r.Status == status);

        var receipts = await query.OrderBy(r => r.PeriodStart).ToListAsync();

        if (!string.IsNullOrEmpty(rateType))
        {
            receipts = rateType switch
            {
                "StandardOnly" => receipts.Where(r => r.TotalEmergencyHours == 0).ToList(),
                "HasEmergency" => receipts.Where(r => r.TotalEmergencyHours > 0).ToList(),
                "HasRetainer"  => receipts.Where(r => r.TotalRetainerAmountApplied > 0).ToList(),
                _              => receipts
            };
        }

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ProBuild";

        using var wb = new XLWorkbook();

        // ── Sheet 1: Summary ──
        var sum = wb.Worksheets.Add("Summary");
        var brandBlue = XLColor.FromHtml("#0d6efd");
        var headerGray = XLColor.FromHtml("#343a40");

        sum.Cell(1, 1).Value = companyName;
        sum.Cell(1, 1).Style.Font.Bold = true;
        sum.Cell(1, 1).Style.Font.FontSize = 18;
        sum.Cell(1, 1).Style.Font.FontColor = brandBlue;
        sum.Range(1, 1, 1, 8).Merge();

        sum.Cell(2, 1).Value = "Contractor Payroll Report";
        sum.Cell(2, 1).Style.Font.Bold = true;
        sum.Cell(2, 1).Style.Font.FontSize = 13;
        sum.Range(2, 1, 2, 8).Merge();

        sum.Cell(3, 1).Value = $"Period: {from:MMM d, yyyy} → {to:MMM d, yyyy}  |  Generated: {now:MMM d, yyyy 'at' h:mm tt} UTC";
        sum.Cell(3, 1).Style.Font.FontSize = 9;
        sum.Cell(3, 1).Style.Font.FontColor = XLColor.FromHtml("#6c757d");
        sum.Range(3, 1, 3, 8).Merge();

        var sumHeaders = new[] { "Contractor", "Receipts", "Standard Hrs", "Emergency Hrs", "Retainer Applied", "Total Amount", "Paid Amount" };
        for (int c = 0; c < sumHeaders.Length; c++)
        {
            var cell = sum.Cell(5, c + 1);
            cell.Value = sumHeaders[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = headerGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        int sRow = 6;
        var byContractor = receipts
            .GroupBy(r => r.ContractorId)
            .Select(g => new
            {
                Name            = g.First().Contractor != null ? g.First().Contractor!.FirstName + " " + g.First().Contractor!.LastName : "(unknown)",
                Count           = g.Count(),
                StandardHours   = g.Sum(r => r.TotalStandardHours),
                EmergencyHours  = g.Sum(r => r.TotalEmergencyHours),
                RetainerApplied = g.Sum(r => r.TotalRetainerAmountApplied),
                TotalAmount     = g.Sum(r => r.TotalAmount),
                PaidAmount      = g.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount)
            })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();

        foreach (var c in byContractor)
        {
            sum.Cell(sRow, 1).Value = c.Name;
            sum.Cell(sRow, 2).Value = c.Count;
            sum.Cell(sRow, 3).Value = c.StandardHours;
            sum.Cell(sRow, 4).Value = c.EmergencyHours;
            sum.Cell(sRow, 5).Value = c.RetainerApplied;
            sum.Cell(sRow, 6).Value = c.TotalAmount;
            sum.Cell(sRow, 7).Value = c.PaidAmount;
            sum.Cell(sRow, 5).Style.NumberFormat.Format = "$#,##0.00";
            sum.Cell(sRow, 6).Style.NumberFormat.Format = "$#,##0.00";
            sum.Cell(sRow, 7).Style.NumberFormat.Format = "$#,##0.00";
            sRow++;
        }

        // Totals row
        if (byContractor.Count > 0)
        {
            var totalsRow = sRow;
            sum.Cell(totalsRow, 1).Value = "TOTAL";
            sum.Cell(totalsRow, 2).Value = byContractor.Sum(x => x.Count);
            sum.Cell(totalsRow, 3).Value = byContractor.Sum(x => x.StandardHours);
            sum.Cell(totalsRow, 4).Value = byContractor.Sum(x => x.EmergencyHours);
            sum.Cell(totalsRow, 5).Value = byContractor.Sum(x => x.RetainerApplied);
            sum.Cell(totalsRow, 6).Value = byContractor.Sum(x => x.TotalAmount);
            sum.Cell(totalsRow, 7).Value = byContractor.Sum(x => x.PaidAmount);
            sum.Range(totalsRow, 1, totalsRow, 7).Style.Font.Bold = true;
            sum.Range(totalsRow, 1, totalsRow, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#e9ecef");
            sum.Range(totalsRow, 5, totalsRow, 7).Style.NumberFormat.Format = "$#,##0.00";
        }

        sum.Columns().AdjustToContents();

        // ── Sheet 2: Receipts ──
        var det = wb.Worksheets.Add("Receipts");
        det.Cell(1, 1).Value = "Contractor Payroll — Receipt Detail";
        det.Cell(1, 1).Style.Font.Bold = true;
        det.Cell(1, 1).Style.Font.FontSize = 13;
        det.Range(1, 1, 1, 13).Merge();

        var detHeaders = new[] {
            "Receipt #", "Contractor", "Period Start", "Period End", "Status",
            "Std Hrs", "Std Rate", "Emerg Hrs", "Emerg Rate",
            "Retainer Hrs", "Retainer Applied", "Total Amount",
            "Approved By"
        };
        for (int c = 0; c < detHeaders.Length; c++)
        {
            var cell = det.Cell(3, c + 1);
            cell.Value = detHeaders[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = headerGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        int dRow = 4;
        foreach (var r in receipts)
        {
            var contractorName = r.Contractor != null ? r.Contractor.FirstName + " " + r.Contractor.LastName : "(unknown)";
            var approverName   = r.ApprovedBy != null ? r.ApprovedBy.FirstName + " " + r.ApprovedBy.LastName : "";
            det.Cell(dRow, 1).Value  = r.Id;
            det.Cell(dRow, 2).Value  = contractorName;
            det.Cell(dRow, 3).Value  = r.PeriodStart;
            det.Cell(dRow, 4).Value  = r.PeriodEnd;
            det.Cell(dRow, 5).Value  = r.Status;
            det.Cell(dRow, 6).Value  = r.TotalStandardHours;
            det.Cell(dRow, 7).Value  = r.HourlyRateSnapshot;
            det.Cell(dRow, 8).Value  = r.TotalEmergencyHours;
            det.Cell(dRow, 9).Value  = r.EmergencyRateSnapshot ?? 0m;
            det.Cell(dRow, 10).Value = r.TotalRetainerHoursApplied;
            det.Cell(dRow, 11).Value = r.TotalRetainerAmountApplied;
            det.Cell(dRow, 12).Value = r.TotalAmount;
            det.Cell(dRow, 13).Value = approverName;
            det.Cell(dRow, 3).Style.DateFormat.Format = "yyyy-mm-dd";
            det.Cell(dRow, 4).Style.DateFormat.Format = "yyyy-mm-dd";
            det.Cell(dRow, 7).Style.NumberFormat.Format = "$#,##0.00";
            det.Cell(dRow, 9).Style.NumberFormat.Format = "$#,##0.00";
            det.Cell(dRow, 11).Style.NumberFormat.Format = "$#,##0.00";
            det.Cell(dRow, 12).Style.NumberFormat.Format = "$#,##0.00";
            dRow++;
        }
        det.Columns().AdjustToContents();

        using var ms = new System.IO.MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var fileName = $"Payroll_{from:yyyyMMdd}_to_{to:yyyyMMdd}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
