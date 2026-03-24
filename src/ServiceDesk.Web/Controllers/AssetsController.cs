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

    public async Task<IActionResult> Index(AssetType? type, AssetStatus? status)
    {
        var query = _context.Assets.Include(a => a.AssignedTo).AsQueryable();

        if (type.HasValue)
            query = query.Where(a => a.AssetType == type.Value);
        if (status.HasValue)
            query = query.Where(a => a.Status == status.Value);

        ViewBag.CurrentType = type;
        ViewBag.CurrentStatus = status;

        var assets = await query.OrderBy(a => a.AssetTag).ToListAsync();
        return View(assets);
    }

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
