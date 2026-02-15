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
}
