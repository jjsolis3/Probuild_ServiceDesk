using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class ServicesController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public ServicesController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(ServiceStatus? status)
    {
        var query = _context.CompanyServices.AsQueryable();

        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value);

        ViewBag.CurrentStatus = status;

        var services = await query.OrderBy(s => s.Name).ToListAsync();
        return View(services);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var service = await _context.CompanyServices
            .Include(s => s.RelatedTickets)
                .ThenInclude(t => t.SubmittedBy)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (service == null) return NotFound();
        return View(service);
    }

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CompanyService service)
    {
        if (ModelState.IsValid)
        {
            _context.Add(service);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(service);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var service = await _context.CompanyServices.FindAsync(id);
        if (service == null) return NotFound();
        return View(service);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CompanyService service)
    {
        if (id != service.Id) return NotFound();

        if (ModelState.IsValid)
        {
            _context.Update(service);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(service);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var service = await _context.CompanyServices.FindAsync(id);
        if (service == null) return NotFound();
        return View(service);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var service = await _context.CompanyServices.FindAsync(id);
        if (service != null)
        {
            _context.CompanyServices.Remove(service);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }
}
