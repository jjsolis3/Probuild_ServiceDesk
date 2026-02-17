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

        var data = await query
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new { t.Id, t.Title, Category = t.Category.ToString(), Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy!.FirstName + " " + t.SubmittedBy.LastName, AssignedTo = t.AssignedTo != null ? t.AssignedTo.FirstName + " " + t.AssignedTo.LastName : "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
            .ToListAsync();

        return Json(data);
    }

    // API: Get tickets by category for report table modals
    [HttpGet]
    public async Task<IActionResult> TicketsByCategory(string category)
    {
        var tickets = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .ToListAsync();

        var filtered = tickets
            .Where(t => t.Category.ToString() == category)
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new { t.Id, t.Title, Category = t.Category.ToString(), Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy?.FullName ?? "Unknown", AssignedTo = t.AssignedTo?.FullName ?? "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
            .ToList();

        return Json(filtered);
    }

    // API: Get tickets by priority for report table modals
    [HttpGet]
    public async Task<IActionResult> TicketsByPriority(string priority)
    {
        var tickets = await _context.Tickets
            .Include(t => t.SubmittedBy).Include(t => t.AssignedTo)
            .ToListAsync();

        var filtered = tickets
            .Where(t => t.Priority.ToString() == priority)
            .OrderByDescending(t => t.CreatedDate)
            .Select(t => new { t.Id, t.Title, Category = t.Category.ToString(), Priority = t.Priority.ToString(), Status = t.Status.ToString(), SubmittedBy = t.SubmittedBy?.FullName ?? "Unknown", AssignedTo = t.AssignedTo?.FullName ?? "Unassigned", Created = t.CreatedDate.ToString("MMM dd, yyyy") })
            .ToList();

        return Json(filtered);
    }
}
