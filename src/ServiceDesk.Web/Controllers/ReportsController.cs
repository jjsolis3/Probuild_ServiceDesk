using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Enums;
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
}
