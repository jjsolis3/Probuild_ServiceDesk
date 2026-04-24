using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize]
public class PortalController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailNotification;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PortalController> _logger;

    public PortalController(ServiceDeskDbContext context, EmailNotificationService emailNotification,
        IServiceScopeFactory scopeFactory, ILogger<PortalController> logger)
    {
        _context           = context;
        _emailNotification = emailNotification;
        _scopeFactory      = scopeFactory;
        _logger            = logger;
    }

    // GET: /Portal
    public async Task<IActionResult> Index(string? status = null, int page = 1)
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser == null) return RedirectToAction("Login", "Account");

        if (portalUser.EmployeeId == null)
        {
            TempData["Error"] = "Your portal account is not linked to an employee record. Please contact IT.";
            return View("NoEmployee");
        }

        const int pageSize = 10;

        // KPI counts always from all tickets (unfiltered)
        var allTickets = await _context.Tickets
            .Where(t => t.SubmittedById == portalUser.EmployeeId)
            .ToListAsync();

        // Build filtered query
        var filteredQuery = _context.Tickets
            .Where(t => t.SubmittedById == portalUser.EmployeeId)
            .Include(t => t.AssignedTo)
            .Include(t => t.Notes)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
        {
            filteredQuery = status.ToLower() switch
            {
                "open"       => filteredQuery.Where(t => t.Status == TicketStatus.Open),
                "inprogress" => filteredQuery.Where(t => t.Status == TicketStatus.InProgress),
                "resolved"   => filteredQuery.Where(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
                _            => filteredQuery
            };
        }

        var totalFiltered = await filteredQuery.CountAsync();
        page = Math.Max(1, page);

        var myTickets = await filteredQuery
            .OrderByDescending(t => t.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var model = new PortalDashboardViewModel
        {
            CurrentUser    = portalUser,
            MyTickets      = myTickets,
            OpenCount      = allTickets.Count(t => t.Status == TicketStatus.Open),
            InProgressCount= allTickets.Count(t => t.Status == TicketStatus.InProgress),
            ResolvedCount  = allTickets.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
            StatusFilter   = status,
            TotalFiltered  = totalFiltered,
            Page           = page,
            PageSize       = pageSize
        };

        await LoadCategoryViewBagAsync();
        ViewData["ActivePage"] = "MyTickets";
        return View(model);
    }

    // GET: /Portal/SubmitTicket
    public async Task<IActionResult> SubmitTicket()
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser?.EmployeeId == null)
        {
            TempData["Error"] = "Your account is not linked to an employee record. Please contact IT.";
            return RedirectToAction("Index");
        }

        ViewBag.Services = new SelectList(
            await _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name).ToListAsync(),
            "Id", "Name");
        await LoadCategoryViewBagAsync();
        ViewData["ActivePage"] = "Submit";
        return View(new PortalSubmitTicketViewModel());
    }

    // POST: /Portal/SubmitTicket
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitTicket(PortalSubmitTicketViewModel model)
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser?.EmployeeId == null)
        {
            TempData["Error"] = "Your account is not linked to an employee record. Please contact IT.";
            return RedirectToAction("Index");
        }

        if (ModelState.IsValid)
        {
            var employee = await _context.Employees.FindAsync(portalUser.EmployeeId);

            var ticket = new Ticket
            {
                Title = model.Title,
                Description = model.Description,
                Category = model.Category,
                Priority = model.Priority,
                Status = TicketStatus.Open,
                SubmittedById = portalUser.EmployeeId.Value,
                BranchId = employee?.BranchId,
                CompanyServiceId = model.CompanyServiceId,
                CreatedDate = DateTime.UtcNow
            };

            _context.Tickets.Add(ticket);
            await _context.SaveChangesAsync();

            // Add system note
            _context.TicketNotes.Add(new TicketNote
            {
                TicketId = ticket.Id,
                AuthorName = "System",
                Content = $"Ticket submitted via Self-Service Portal by {portalUser.FullName}.",
                CreatedDate = DateTime.UtcNow,
                Source = "Portal",
                IsInternal = true
            });
            await _context.SaveChangesAsync();

            // Try to send confirmation email (fire-and-forget; use scope factory — HTTP scope may be disposed)
            if (employee != null)
            {
                var capturedTicketId  = ticket.Id;
                var capturedEmail     = employee.Email;
                var capturedFullName  = employee.FullName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db    = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
                        var email = scope.ServiceProvider.GetRequiredService<EmailNotificationService>();
                        var t = await db.Tickets.FindAsync(capturedTicketId);
                        if (t != null)
                            await email.SendTicketCreatedConfirmation(t, capturedEmail, capturedFullName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Notification] Portal ticket-created confirmation failed for Ticket #{Id}.", capturedTicketId);
                    }
                });
            }

            TempData["Success"] = $"Ticket #{ticket.Id} submitted successfully. Our IT team will be in touch shortly.";
            return RedirectToAction("TicketDetail", new { id = ticket.Id });
        }

        ViewBag.Services = new SelectList(
            await _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name).ToListAsync(),
            "Id", "Name");
        await LoadCategoryViewBagAsync();
        ViewData["ActivePage"] = "Submit";
        return View(model);
    }

    // GET: /Portal/TicketDetail/5
    public async Task<IActionResult> TicketDetail(int id)
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser?.EmployeeId == null) return RedirectToAction("Index");

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .Include(t => t.Notes.Where(n => !n.IsInternal).OrderBy(n => n.CreatedDate))
            .Include(t => t.Attachments)
            .FirstOrDefaultAsync(t => t.Id == id && t.SubmittedById == portalUser.EmployeeId);

        if (ticket == null)
        {
            TempData["Error"] = "Ticket not found or you don't have permission to view it.";
            return RedirectToAction("Index");
        }

        var model = new PortalTicketDetailViewModel
        {
            Ticket = ticket,
            CurrentUser = portalUser
        };

        await LoadCategoryViewBagAsync();
        ViewData["ActivePage"] = "MyTickets";
        return View(model);
    }

    // POST: /Portal/AddReply
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddReply(int ticketId, string content)
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser?.EmployeeId == null) return RedirectToAction("Index");

        if (string.IsNullOrWhiteSpace(content))
        {
            TempData["Error"] = "Reply cannot be empty.";
            return RedirectToAction("TicketDetail", new { id = ticketId });
        }

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.SubmittedById == portalUser.EmployeeId);

        if (ticket == null)
        {
            TempData["Error"] = "Ticket not found.";
            return RedirectToAction("Index");
        }

        var note = new TicketNote
        {
            TicketId = ticketId,
            AuthorName = portalUser.FullName,
            AuthorEmail = portalUser.Email,
            Content = content,
            CreatedDate = DateTime.UtcNow,
            Source = "Portal",
            IsInternal = false
        };
        _context.TicketNotes.Add(note);
        ticket.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Notify assigned agent (if any)
        if (ticket.AssignedTo != null)
        {
            var capturedNoteTicketId  = ticket.Id;
            var capturedNoteId        = note.Id;
            var capturedAssigneeEmail = ticket.AssignedTo.Email;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db    = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
                    var email = scope.ServiceProvider.GetRequiredService<EmailNotificationService>();
                    var t = await db.Tickets.Include(x => x.AssignedTo).FirstOrDefaultAsync(x => x.Id == capturedNoteTicketId);
                    var n = await db.TicketNotes.FindAsync(capturedNoteId);
                    if (t != null && n != null)
                        await email.NotifyNoteAdded(t, n, capturedAssigneeEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Notification] Portal note-added notification failed for Ticket #{Id}.", capturedNoteTicketId);
                }
            });
        }

        TempData["Success"] = "Your reply has been added.";
        return RedirectToAction("TicketDetail", new { id = ticketId });
    }

    // GET: /Portal/KnowledgeBase
    public async Task<IActionResult> KnowledgeBase(string? q, int? category)
    {
        var query = _context.KbArticles
            .Where(a => a.IsPublished)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLower();
            query = query.Where(a =>
                a.Title.ToLower().Contains(search) ||
                a.Problem.ToLower().Contains(search) ||
                a.Solution.ToLower().Contains(search));
        }

        if (category.HasValue)
            query = query.Where(a => a.Category == category.Value);

        var articles = await query
            .OrderByDescending(a => a.CreatedDate)
            .ToListAsync();

        ViewBag.SearchQuery = q;
        ViewBag.SelectedCategory = category;
        await LoadCategoryViewBagAsync();
        ViewData["ActivePage"] = "KB";
        return View(articles);
    }

    // -------------------------------------------------------
    private async Task LoadCategoryViewBagAsync()
    {
        try
        {
            var cats = await _context.TicketCategories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync();
            ViewBag.CategoriesById     = cats.ToDictionary(c => c.Id, c => c.Name);
            ViewBag.CategorySelectList = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(cats, "Id", "Name");
        }
        catch
        {
            var cats = Enum.GetValues<TicketCategory>()
                .Select(c => new { Id = (int)c, Name = c.ToString() }).ToList();
            ViewBag.CategoriesById     = cats.ToDictionary(c => c.Id, c => c.Name);
            ViewBag.CategorySelectList = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(cats, "Id", "Name");
        }
    }

    private async Task<ServiceDesk.Core.Models.PortalUser?> GetCurrentPortalUserAsync()
    {
        var userId = User.FindFirstValue("UserId");
        if (!int.TryParse(userId, out var id)) return null;
        return await _context.PortalUsers
            .Include(u => u.Role)
            .Include(u => u.Employee)
            .FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
    }
}
