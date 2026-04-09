using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class SubscriptionsController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public SubscriptionsController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(SubscriptionStatus[]? statuses, string? q)
    {
        var query = _context.Subscriptions.AsQueryable();

        if (statuses is { Length: > 0 })
            query = query.Where(s => statuses.Contains(s.Status));
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(s => s.Name.Contains(q)
                || (s.Provider != null && s.Provider.Contains(q)));

        ViewBag.SelectedStatuses = statuses ?? Array.Empty<SubscriptionStatus>();
        ViewBag.CurrentQuery     = q;

        var subscriptions = await query.OrderBy(s => s.Name).ToListAsync();
        return View(subscriptions);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var subscription = await _context.Subscriptions.FindAsync(id);
        if (subscription == null) return NotFound();
        return View(subscription);
    }

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Subscription subscription)
    {
        if (ModelState.IsValid)
        {
            _context.Add(subscription);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(subscription);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var subscription = await _context.Subscriptions.FindAsync(id);
        if (subscription == null) return NotFound();
        return View(subscription);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Subscription subscription)
    {
        if (id != subscription.Id) return NotFound();

        if (ModelState.IsValid)
        {
            _context.Update(subscription);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(subscription);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var subscription = await _context.Subscriptions.FindAsync(id);
        if (subscription == null) return NotFound();
        return View(subscription);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var subscription = await _context.Subscriptions.FindAsync(id);
        if (subscription != null)
        {
            _context.Subscriptions.Remove(subscription);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }
}
