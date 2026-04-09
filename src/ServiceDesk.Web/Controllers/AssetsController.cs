using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class AssetsController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public AssetsController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(AssetType[]? types, AssetStatus[]? statuses, string[]? manufacturers, bool? assigned, string? q)
    {
        var query = _context.Assets.Include(a => a.AssignedTo).AsQueryable();

        if (types is { Length: > 0 })
            query = query.Where(a => types.Contains(a.AssetType));
        if (statuses is { Length: > 0 })
            query = query.Where(a => statuses.Contains(a.Status));
        if (manufacturers is { Length: > 0 })
            query = query.Where(a => manufacturers.Contains(a.Manufacturer));
        if (assigned.HasValue)
            query = assigned.Value
                ? query.Where(a => a.AssignedToId != null)
                : query.Where(a => a.AssignedToId == null);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a => a.Name.Contains(q) || a.AssetTag.Contains(q)
                || (a.Manufacturer != null && a.Manufacturer.Contains(q))
                || (a.Model != null && a.Model.Contains(q)));

        ViewBag.SelectedTypes         = types ?? Array.Empty<AssetType>();
        ViewBag.SelectedStatuses      = statuses ?? Array.Empty<AssetStatus>();
        ViewBag.SelectedManufacturers = manufacturers ?? Array.Empty<string>();
        ViewBag.CurrentAssigned       = assigned;
        ViewBag.CurrentQuery          = q;
        ViewBag.Manufacturers         = await _context.Assets
            .Where(a => a.Manufacturer != null && a.Manufacturer != "")
            .Select(a => a.Manufacturer!).Distinct().OrderBy(m => m).ToListAsync();

        var assets = await query.OrderBy(a => a.AssetTag).ToListAsync();
        return View(assets);
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (asset == null) return NotFound();
        return View(asset);
    }

    public IActionResult Create()
    {
        PopulateDropdowns();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Asset asset)
    {
        if (ModelState.IsValid)
        {
            _context.Add(asset);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(asset);
        return View(asset);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets.FindAsync(id);
        if (asset == null) return NotFound();

        PopulateDropdowns(asset);
        return View(asset);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Asset asset)
    {
        if (id != asset.Id) return NotFound();

        if (ModelState.IsValid)
        {
            _context.Update(asset);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(asset);
        return View(asset);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (asset == null) return NotFound();
        return View(asset);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var asset = await _context.Assets.FindAsync(id);
        if (asset != null)
        {
            _context.Assets.Remove(asset);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private void PopulateDropdowns(Asset? asset = null)
    {
        ViewBag.Employees = new SelectList(
            _context.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", asset?.AssignedToId);
    }
}
