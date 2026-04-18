using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class SoftwareLicensesController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public SoftwareLicensesController(ServiceDeskDbContext context)
        => _context = context;

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(string? q, bool? expiring, bool? expired, bool? activeOnly)
    {
        var query = _context.SoftwareLicenses.AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => l.ProductName.Contains(q)
                || (l.Publisher != null && l.Publisher.Contains(q))
                || (l.Vendor != null && l.Vendor.Contains(q)));

        if (activeOnly == true)
            query = query.Where(l => l.IsActive);

        var licenses = await query.OrderBy(l => l.ProductName).ToListAsync();

        if (expired == true)
            licenses = licenses.Where(l => l.IsExpired).ToList();
        else if (expiring == true)
            licenses = licenses.Where(l => l.IsExpiringSoon).ToList();

        ViewBag.Query      = q;
        ViewBag.Expiring   = expiring;
        ViewBag.Expired    = expired;
        ViewBag.ActiveOnly = activeOnly;

        var today = DateTime.Today;
        ViewBag.TotalLicenses   = licenses.Count;
        ViewBag.TotalSeats      = licenses.Sum(l => l.TotalSeats);
        ViewBag.SeatsInUse      = licenses.Sum(l => l.SeatsInUse);
        ViewBag.ExpiringCount   = licenses.Count(l => l.IsExpiringSoon);
        ViewBag.ExpiredCount    = licenses.Count(l => l.IsExpired);
        ViewBag.TotalAnnualCost = licenses.Where(l => l.CostPerSeat.HasValue).Sum(l => l.CostPerSeat!.Value * l.TotalSeats);

        return View(licenses);
    }

    public IActionResult Create() => View(new SoftwareLicense());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SoftwareLicense license)
    {
        if (ModelState.IsValid)
        {
            license.CreatedDate = DateTime.UtcNow;
            license.UpdatedDate = DateTime.UtcNow;
            _context.SoftwareLicenses.Add(license);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"License for '{license.ProductName}' created.";
            return RedirectToAction(nameof(Index));
        }
        return View(license);
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();
        var license = await _context.SoftwareLicenses.FindAsync(id);
        if (license == null) return NotFound();
        return View(license);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();
        var license = await _context.SoftwareLicenses.FindAsync(id);
        if (license == null) return NotFound();
        return View(license);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, SoftwareLicense license)
    {
        if (id != license.Id) return NotFound();
        if (ModelState.IsValid)
        {
            license.UpdatedDate = DateTime.UtcNow;
            _context.Update(license);
            await _context.SaveChangesAsync();
            TempData["Success"] = "License updated.";
            return RedirectToAction(nameof(Index));
        }
        return View(license);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var license = await _context.SoftwareLicenses.FindAsync(id);
        if (license != null)
        {
            _context.SoftwareLicenses.Remove(license);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "License deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustSeats(int id, int delta, string? notes)
    {
        var license = await _context.SoftwareLicenses.FindAsync(id);
        if (license == null) return NotFound();
        license.SeatsInUse = Math.Max(0, Math.Min(license.TotalSeats, license.SeatsInUse + delta));
        license.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Json(new { success = true, seatsInUse = license.SeatsInUse, available = license.AvailableSeats });
    }
}
