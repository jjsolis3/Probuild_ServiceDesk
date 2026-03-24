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

    public PortalController(ServiceDeskDbContext context, EmailNotificationService emailNotification)
    {
        _context = context;
        _emailNotification = emailNotification;
    }

    // GET: /Portal
    public async Task<IActionResult> Index()
    {
        var portalUser = await GetCurrentPortalUserAsync();
        if (portalUser == null) return RedirectToAction("Login", "Account");

        // If IT staff somehow ends up here, redirect to dashboard
        if (User.IsInRole("Admin") || User.IsInRole("IT Agent"))
            return RedirectToAction("Index", "Home");

        if (portalUser.EmployeeId == null)
        {
            TempData["Error"] = "Your portal account is not linked to an employee record. Please contact IT.";
            return View("NoEmployee");
        }

        var myTickets = await _context.Tickets
            .Where(t => t.SubmittedById == portalUser.EmployeeId)
            .Include(t => t.AssignedTo)
            .Include(t => t.Notes)
            .OrderByDescending(t => t.CreatedDate)
            .ToListAsync();

        var model = new PortalDashboardViewModel
        {
            CurrentUser = portalUser,
            MyTickets = myTickets,
            OpenCount = myTickets.Count(t => t.Status == TicketStatus.Open),
            InProgressCount = myTickets.Count(t => t.Status == TicketStatus.InProgress),
            ResolvedCount = myTickets.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
        };

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

            // Try to send confirmation email (fire-and-forget, don't block)
            if (employee != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fullTicket = await _context.Tickets.FindAsync(ticket.Id);
                        if (fullTicket != null)
                            await _emailNotification.SendTicketCreatedConfirmation(fullTicket, employee.Email, employee.FullName);
                    }
                    catch { /* ignore email errors */ }
                });
            }

            TempData["Success"] = $"Ticket #{ticket.Id} submitted successfully. Our IT team will be in touch shortly.";
            return RedirectToAction("TicketDetail", new { id = ticket.Id });
        }

        ViewBag.Services = new SelectList(
            await _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name).ToListAsync(),
            "Id", "Name");
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
            _ = Task.Run(async () =>
            {
                try { await _emailNotification.NotifyNoteAdded(ticket, note, ticket.AssignedTo.Email); }
                catch { }
            });
        }

        TempData["Success"] = "Your reply has been added.";
        return RedirectToAction("TicketDetail", new { id = ticketId });
    }

    // -------------------------------------------------------
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
