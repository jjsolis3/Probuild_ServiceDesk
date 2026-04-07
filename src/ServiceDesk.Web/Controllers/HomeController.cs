using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class HomeController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public HomeController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
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
            .Where(t => t.Priority == TicketPriority.Critical
                     && t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .OrderByDescending(t => t.CreatedDate)
            .Take(5)
            .ToListAsync();

        var recentTickets = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .OrderByDescending(t => t.CreatedDate)
            .Take(5)
            .ToListAsync();

        // Phase 4 — 7-day ticket volume
        var today = DateTime.UtcNow.Date;
        var sevenDaysAgo = today.AddDays(-6);
        var recentCreated = await _context.Tickets
            .Where(t => t.CreatedDate >= sevenDaysAgo)
            .Select(t => t.CreatedDate)
            .ToListAsync();
        var volumeDates = Enumerable.Range(0, 7).Select(i => today.AddDays(-6 + i)).ToList();
        var volumeCounts = volumeDates.Select(d => recentCreated.Count(t => t.Date == d)).ToList();

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

        var model = new DashboardViewModel
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
            SlaBreachCount = slaBreachCount
        };

        return View(model);
    }

    // API endpoint for dashboard KPI detail modals
    [HttpGet]
    public async Task<IActionResult> KpiDetail(string type)
    {
        object? data = type switch
        {
            "open" => await _context.Tickets
                .Where(t => t.Status == TicketStatus.Open)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.CreatedDate)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "inprogress" => await _context.Tickets
                .Where(t => t.Status == TicketStatus.InProgress)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.CreatedDate)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "resolved" => await _context.Tickets
                .Where(t => t.ResolvedDate != null && t.ResolvedDate.Value.Month == DateTime.UtcNow.Month && t.ResolvedDate.Value.Year == DateTime.UtcNow.Year)
                .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
                .OrderByDescending(t => t.ResolvedDate)
                .Select(t => new { t.Id, t.Title, Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Resolved = t.ResolvedDate!.Value.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            "assets" => await _context.Assets
                .Include(a => a.AssignedTo)
                .OrderBy(a => a.Name)
                .Select(a => new { a.Id, a.Name, a.AssetTag, Type = a.AssetType.ToString(), Status = a.Status.ToString(), AssignedTo = a.AssignedTo != null ? a.AssignedTo.FirstName + " " + a.AssignedTo.LastName : "Unassigned" })
                .ToListAsync(),

            "employees" => await _context.Employees
                .Where(e => e.IsActive)
                .OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName, e.Email, e.Department, e.JobTitle })
                .ToListAsync(),

            "subscriptions" => (await _context.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active)
                .OrderBy(s => s.Name)
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
