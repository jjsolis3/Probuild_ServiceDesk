using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

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

        var criticalTickets = await _context.Tickets
            .Where(t => t.Priority == TicketPriority.Critical && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled)
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
            RecentTickets = recentTickets
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

            "subscriptions" => await _context.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name, s.Provider, Cost = s.MonthlyCost.ToString("C"), Status = s.Status.ToString(), Renewal = s.RenewalDate.ToString("MMM dd, yyyy") })
                .ToListAsync(),

            _ => null
        };

        if (data == null) return NotFound();
        return Json(data);
    }
}
