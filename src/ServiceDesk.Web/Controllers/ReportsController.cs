using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

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

    public async Task<IActionResult> Tickets()
    {
        var tickets = await _context.Tickets
            .Include(t => t.AssignedTo)
            .ToListAsync();

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

    public async Task<IActionResult> Assets()
    {
        var assets = await _context.Assets
            .Include(a => a.AssignedTo)
            .ToListAsync();

        var model = new AssetReportViewModel
        {
            TotalAssets = assets.Count,
            TotalValue = assets.Where(a => a.PurchaseCost.HasValue).Sum(a => a.PurchaseCost!.Value),
            ByType = assets.GroupBy(a => a.AssetType)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByStatus = assets.GroupBy(a => a.Status)
                .ToDictionary(g => g.Key, g => g.Count()),
            ExpiringWarranties = assets
                .Where(a => a.WarrantyExpiry.HasValue && a.WarrantyExpiry.Value <= DateTime.UtcNow.AddMonths(3) && a.WarrantyExpiry.Value >= DateTime.UtcNow)
                .OrderBy(a => a.WarrantyExpiry)
                .ToList(),
            ByDepartment = assets
                .Where(a => a.AssignedTo != null)
                .GroupBy(a => a.AssignedTo!.Department)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        return View(model);
    }

    public async Task<IActionResult> Users()
    {
        var employees = await _context.Employees
            .Include(e => e.SubmittedTickets)
            .Include(e => e.AssignedAssets)
            .Where(e => e.IsActive)
            .ToListAsync();

        var model = new UserReportViewModel
        {
            UserStats = employees.Select(e => new UserTicketStats
            {
                EmployeeName = e.FullName,
                Department = e.Department,
                TicketsSubmitted = e.SubmittedTickets.Count,
                TicketsOpen = e.SubmittedTickets.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress),
                TicketsResolved = e.SubmittedTickets.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
                AssetsAssigned = e.AssignedAssets.Count
            })
            .OrderByDescending(u => u.TicketsSubmitted)
            .ToList(),

            TicketsByDepartment = employees
                .GroupBy(e => e.Department)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.SubmittedTickets.Count))
        };

        return View(model);
    }
}
