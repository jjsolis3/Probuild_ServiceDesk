using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

public class TicketsController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public TicketsController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(TicketStatus? status, TicketCategory? category, TicketPriority? priority)
    {
        var query = _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);
        if (category.HasValue)
            query = query.Where(t => t.Category == category.Value);
        if (priority.HasValue)
            query = query.Where(t => t.Priority == priority.Value);

        ViewBag.CurrentStatus = status;
        ViewBag.CurrentCategory = category;
        ViewBag.CurrentPriority = priority;

        var tickets = await query.OrderByDescending(t => t.CreatedDate).ToListAsync();
        return View(tickets);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        return View(ticket);
    }

    public IActionResult Create()
    {
        PopulateDropdowns();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Ticket ticket)
    {
        if (ModelState.IsValid)
        {
            ticket.CreatedDate = DateTime.UtcNow;
            _context.Add(ticket);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(ticket);
        return View(ticket);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        PopulateDropdowns(ticket);
        return View(ticket);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Ticket ticket)
    {
        if (id != ticket.Id) return NotFound();

        if (ModelState.IsValid)
        {
            ticket.UpdatedDate = DateTime.UtcNow;

            if (ticket.Status == TicketStatus.Resolved && ticket.ResolvedDate == null)
                ticket.ResolvedDate = DateTime.UtcNow;
            if (ticket.Status == TicketStatus.Closed && ticket.ClosedDate == null)
                ticket.ClosedDate = DateTime.UtcNow;

            _context.Update(ticket);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(ticket);
        return View(ticket);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        return View(ticket);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket != null)
        {
            _context.Tickets.Remove(ticket);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private void PopulateDropdowns(Ticket? ticket = null)
    {
        ViewBag.Employees = new SelectList(
            _context.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.SubmittedById);

        ViewBag.ITStaff = new SelectList(
            _context.Employees.Where(e => e.IsActive && e.Department == "IT").OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.AssignedToId);

        ViewBag.Services = new SelectList(
            _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name),
            "Id", "Name", ticket?.CompanyServiceId);
    }
}
