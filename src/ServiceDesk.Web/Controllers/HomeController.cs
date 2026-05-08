using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using ServiceDesk.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent,Viewer")]
public class HomeController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly SlaRiskService _slaRisk;
    private readonly IMemoryCache _cache;

    // Cache keys
    private const string CK_DASHBOARD     = "dashboard:viewmodel";
    private const string CK_CATEGORIES    = "dashboard:categoriesById";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public HomeController(ServiceDeskDbContext context, SlaRiskService slaRisk, IMemoryCache cache)
    {
        _context = context;
        _slaRisk = slaRisk;
        _cache   = cache;
    }

    public async Task<IActionResult> Index()
    {
        // Build (or reuse cached) dashboard view model. The 60-second TTL means
        // ~98% of dashboard hits skip the DB while still feeling near-real-time.
        var model = await _cache.GetOrCreateAsync(CK_DASHBOARD, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await BuildDashboardModelAsync();
        });

        // Categories are nearly static; cache for 5 minutes.
        ViewBag.CategoriesById = await _cache.GetOrCreateAsync(CK_CATEGORIES, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            try
            {
                return await _context.TicketCategories
                    .ToDictionaryAsync(c => c.Id, c => c.Name);
            }
            catch
            {
                return Enum.GetValues<TicketCategory>()
                    .ToDictionary(c => (int)c, c => c.GetDisplayName());
            }
        });

        return View(model);
    }

    private async Task<DashboardViewModel> BuildDashboardModelAsync()
    {
        var openTickets = await _context.Tickets.CountAsync(t => t.Status == TicketStatus.Open);
        var inProgressTickets = await _context.Tickets.CountAsync(t => t.Status == TicketStatus.InProgress);
        var resolvedThisMonth = await _context.Tickets.CountAsync(t =>
            t.ResolvedDate != null &&
            t.ResolvedDate.Value.Month == DateTime.UtcNow.Month &&
            t.ResolvedDate.Value.Year == DateTime.UtcNow.Year);
        var totalAssets = await _context.Assets.CountAsync();
        var assignedAssets = await _context.Assets.CountAsync(a => a.Status == AssetStatus.Assigned);
        var activeEmployees = await _context.Employees.CountAsync(e => e.IsActive);
        var activeSubscriptions = await _context.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active);
        var monthlyCost = await _context.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active)
            .SumAsync(s => s.MonthlyCost);

        var slaBreachCount = await _context.Tickets
            .CountAsync(t => t.DueDate.HasValue
                && t.DueDate.Value < DateTime.UtcNow
                && t.Status != TicketStatus.Resolved
                && t.Status != TicketStatus.Closed
                && t.Status != TicketStatus.Cancelled);

        var criticalTickets = await _context.Tickets
            .Where(t => (t.Priority == TicketPriority.Critical || t.Priority == TicketPriority.High)
                     && t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .OrderByDescending(t => t.Priority)   // Critical first, then High
            .ThenByDescending(t => t.CreatedDate)
            .Take(10)
            .ToListAsync();

        var recentTickets = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .OrderByDescending(t => t.CreatedDate)
            .Take(5)
            .ToListAsync();

        // Phase 4 — 7-day ticket volume — group by date in SQL to avoid loading
        // all rows into memory.
        var today = DateTime.UtcNow.Date;
        var sevenDaysAgo = today.AddDays(-6);
        var volumeBuckets = await _context.Tickets
            .Where(t => t.CreatedDate >= sevenDaysAgo)
            .GroupBy(t => t.CreatedDate.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync();
        var volumeDates = Enumerable.Range(0, 7).Select(i => today.AddDays(-6 + i)).ToList();
        var volumeCounts = volumeDates
            .Select(d => volumeBuckets.FirstOrDefault(b => b.Date == d)?.Count ?? 0)
            .ToList();

        // Phase 4 — Status distribution (active tickets only, excluding Cancelled)
        var allActiveTickets = await _context.Tickets
            .Where(t => t.Status != TicketStatus.Cancelled)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        // Phase 4 — Priority distribution (open + in-progress tickets)
        var openByPriority = await _context.Tickets
            .Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress)
            .GroupBy(t => t.Priority)
            .Select(g => new { Priority = g.Key, Count = g.Count() })
            .ToListAsync();

        // Phase 4 — Expiring warranties (next 90 days)
        var warrantyThreshold = DateTime.UtcNow.AddDays(90);
        var expiringWarranties = await _context.Assets
            .Where(a => a.WarrantyExpiry.HasValue && a.WarrantyExpiry.Value >= DateTime.UtcNow && a.WarrantyExpiry.Value <= warrantyThreshold)
            .Include(a => a.AssignedTo)
            .OrderBy(a => a.WarrantyExpiry)
            .Take(10)
            .ToListAsync();

        // SLA risk — load active tickets and score with SlaRiskService
        await _slaRisk.EnsureBaselinesBuiltAsync();
        var activeForSla = await _context.Tickets
            .Where(t => t.Status == TicketStatus.Open
                     || t.Status == TicketStatus.InProgress
                     || t.Status == TicketStatus.OnHold)
            .Include(t => t.AssignedTo)
            .OrderBy(t => t.CreatedDate)
            .Take(200)
            .ToListAsync();

        var slaAtRisk = activeForSla
            .Select(t =>
            {
                var risk = _slaRisk.GetRisk((int)t.Category, (int)t.Priority, t.CreatedDate);
                return (Ticket: t, Risk: risk);
            })
            .Where(x => x.Risk >= SlaRiskLevel.High)
            .OrderByDescending(x => x.Risk)
            .ThenBy(x => x.Ticket.CreatedDate)
            .Take(10)
            .Select(x =>
            {
                var dueLabel = x.Ticket.DueDate.HasValue
                    ? (x.Ticket.DueDate.Value < DateTime.UtcNow
                        ? $"Overdue by {(int)(DateTime.UtcNow - x.Ticket.DueDate.Value).TotalHours}h"
                        : $"Due in {(int)(x.Ticket.DueDate.Value - DateTime.UtcNow).TotalHours}h")
                    : null;
                return new SlaAtRiskTicket(
                    Id: x.Ticket.Id,
                    Title: x.Ticket.Title,
                    Priority: x.Ticket.Priority.ToString(),
                    Status: x.Ticket.Status.ToString(),
                    AssigneeName: x.Ticket.AssignedTo?.FullName,
                    RiskLabel: SlaRiskService.RiskLabel(x.Risk),
                    RiskBadge: SlaRiskService.RiskBadgeClass(x.Risk),
                    DueLabel: dueLabel);
            })
            .ToList();

        return new DashboardViewModel
        {
            OpenTickets = openTickets,
            InProgressTickets = inProgressTickets,
            ResolvedThisMonth = resolvedThisMonth,
            TotalAssets = totalAssets,
            AssignedAssets = assignedAssets,
            ActiveEmployees = activeEmployees,
            ActiveSubscriptions = activeSubscriptions,
            MonthlySubscriptionCost = monthlyCost,
            CriticalTickets = criticalTickets,
            RecentTickets = recentTickets,
            TicketVolumeDates = volumeDates.Select(d => d.ToString("MMM dd")).ToList(),
            TicketVolumeCounts = volumeCounts,
            StatusOpen = allActiveTickets.FirstOrDefault(x => x.Status == TicketStatus.Open)?.Count ?? 0,
            StatusInProgress = allActiveTickets.FirstOrDefault(x => x.Status == TicketStatus.InProgress)?.Count ?? 0,
            StatusOnHold = allActiveTickets.FirstOrDefault(x => x.Status == TicketStatus.OnHold)?.Count ?? 0,
            StatusResolved = allActiveTickets.FirstOrDefault(x => x.Status == TicketStatus.Resolved)?.Count ?? 0,
            StatusClosed = allActiveTickets.FirstOrDefault(x => x.Status == TicketStatus.Closed)?.Count ?? 0,
            PriorityLow = openByPriority.FirstOrDefault(x => x.Priority == TicketPriority.Low)?.Count ?? 0,
            PriorityMedium = openByPriority.FirstOrDefault(x => x.Priority == TicketPriority.Medium)?.Count ?? 0,
            PriorityHigh = openByPriority.FirstOrDefault(x => x.Priority == TicketPriority.High)?.Count ?? 0,
            PriorityCritical = openByPriority.FirstOrDefault(x => x.Priority == TicketPriority.Critical)?.Count ?? 0,
            ExpiringWarranties = expiringWarranties,
            SlaBreachCount = slaBreachCount,
            SlaAtRiskTickets = slaAtRisk
        };
    }

    // API endpoint for dashboard KPI detail modals.
    // Capped at MaxKpiRows to avoid pulling thousands of rows into memory.
    private const int MaxKpiRows = 500;

    [HttpGet]
    public async Task<IActionResult> KpiDetail(string type)
    {
        object? data = type switch
        {
            "open" => await _context.Tickets
                .Where(t => t.Status == TicketStatus.Open)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.CreatedDate)
                .Take(MaxKpiRows)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "inprogress" => await _context.Tickets
                .Where(t => t.Status == TicketStatus.InProgress)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.CreatedDate)
                .Take(MaxKpiRows)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "resolved" => await _context.Tickets
                .Where(t => t.ResolvedDate != null && t.ResolvedDate.Value.Month == DateTime.UtcNow.Month && t.ResolvedDate.Value.Year == DateTime.UtcNow.Year)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.ResolvedDate)
                .Take(MaxKpiRows)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Resolved = t.ResolvedDate!.Value.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "assets" => await _context.Assets
                .Include(a => a.AssignedTo)
                .OrderBy(a => a.Name)
                .Take(MaxKpiRows)
                .Select(a => new { a.Id, a.Name, a.AssetTag, Type = a.AssetType.ToString(), Status = a.Status.ToString(), AssignedTo = a.AssignedTo != null ? a.AssignedTo.FirstName + " " + a.AssignedTo.LastName : "Unassigned" })
                .ToListAsync(),

            "employees" => await _context.Employees
                .Where(e => e.IsActive)
                .OrderBy(e => e.LastName)
                .Take(MaxKpiRows)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName, e.Email, e.Department, e.JobTitle })
                .ToListAsync(),

            "subscriptions" => (await _context.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active)
                .OrderBy(s => s.Name)
                .Take(MaxKpiRows)
                .ToListAsync())
                .Select(s => new { s.Id, s.Name, s.Provider, Cost = s.MonthlyCost.ToString("C"), Status = s.Status.ToString(), Renewal = s.RenewalDate.HasValue ? s.RenewalDate.Value.ToString("MMM dd, yyyy") : "N/A" })
                .ToList(),

            _ => null
        };

        if (data == null) return NotFound();
        return Json(data);
    }

    // Error handler — called by UseExceptionHandler in production
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        ViewBag.RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        ViewBag.ErrorPath = feature?.Path;
        return View();
    }
}
