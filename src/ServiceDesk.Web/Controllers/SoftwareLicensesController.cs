using System.Security.Claims;
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
        var license = await _context.SoftwareLicenses
            .Include(l => l.Seats.OrderByDescending(s => s.AssignedDate))
                .ThenInclude(s => s.Employee)
            .Include(l => l.Seats)
                .ThenInclude(s => s.Asset)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (license == null) return NotFound();

        ViewBag.Employees = await _context.Employees
            .Where(e => e.IsActive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        ViewBag.Assets = await _context.Assets
            .OrderBy(a => a.AssetTag)
            .Select(a => new { a.Id, a.AssetTag, a.Name })
            .ToListAsync();

        return View(license);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignSeat(int id, int? employeeId, int? assetId, string? notes)
    {
        var license = await _context.SoftwareLicenses
            .Include(l => l.Seats)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (license == null) return NotFound();

        if (employeeId == null && assetId == null)
        {
            TempData["Error"] = "Select an employee or asset to assign the seat to.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var activeCount = license.Seats.Count(s => s.RevokedDate == null);
        if (activeCount >= license.TotalSeats)
        {
            TempData["Error"] = "No available seats remaining. Increase the total seat count first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var seat = new LicenseSeat
        {
            SoftwareLicenseId = id,
            EmployeeId = employeeId,
            AssetId = assetId,
            AssignedDate = DateTime.UtcNow,
            AssignedByEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            Notes = notes
        };
        _context.LicenseSeats.Add(seat);

        license.SeatsInUse = activeCount + 1;
        license.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Seat assigned.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeSeat(int id, int seatId)
    {
        var license = await _context.SoftwareLicenses
            .Include(l => l.Seats)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (license == null) return NotFound();

        var seat = license.Seats.FirstOrDefault(s => s.Id == seatId);
        if (seat == null || seat.RevokedDate != null) return NotFound();

        seat.RevokedDate = DateTime.UtcNow;
        seat.RevokedByEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;

        license.SeatsInUse = license.Seats.Count(s => s.RevokedDate == null);
        license.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Seat revoked.";
        return RedirectToAction(nameof(Details), new { id });
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
