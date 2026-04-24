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

    // GET: Reports/StoreOrdersExport  — CSV download
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> StoreOrdersExport(int? year, int? quarter, string? status)
    {
        var now = DateTime.UtcNow;
        year    ??= now.Year;
        quarter ??= (now.Month - 1) / 3 + 1;

        var query = _context.StoreOrders
            .Include(o => o.PortalUser)
            .Include(o => o.Items)
            .Where(o => o.Year == year && o.Quarter == quarter)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var orders = await query.OrderBy(o => o.OrderNumber).ToListAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Order #,User Name,Email,Order Date,Quarter,Year,Status,Product,Category,Qty,Notes");

        foreach (var order in orders)
        {
            foreach (var item in order.Items)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(order.OrderNumber),
                    CsvEscape(order.PortalUser.FullName),
                    CsvEscape(order.PortalUser.Email),
                    CsvEscape(order.OrderDate.ToString("yyyy-MM-dd HH:mm")),
                    $"Q{order.Quarter}",
                    order.Year.ToString(),
                    CsvEscape(order.Status),
                    CsvEscape(item.ProductNameSnapshot),
                    CsvEscape(item.ProductCategorySnapshot ?? string.Empty),
                    item.Quantity.ToString(),
                    CsvEscape(order.Notes ?? string.Empty)
                ));
            }
        }

        var filename = $"StoreOrders-Q{quarter}-{year}.csv";
        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))
            .ToArray();

        return File(bytes, "text/csv", filename);
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
}
