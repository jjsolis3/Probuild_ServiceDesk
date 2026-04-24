using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class ConsumablesController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public ConsumablesController(ServiceDeskDbContext context)
        => _context = context;

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(string? q, bool? lowStock)
    {
        var query = _context.ConsumableItems.AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(c => c.Name.Contains(q)
                || (c.Manufacturer != null && c.Manufacturer.Contains(q))
                || (c.PartNumber != null && c.PartNumber.Contains(q))
                || (c.Category != null && c.Category.Contains(q)));

        var items = await query.OrderBy(c => c.Name).ToListAsync();

        if (lowStock == true)
            items = items.Where(c => c.IsLowStock).ToList();

        ViewBag.Query    = q;
        ViewBag.LowStock = lowStock;
        ViewBag.TotalItems     = items.Count;
        ViewBag.LowStockCount  = items.Count(c => c.IsLowStock);
        ViewBag.TotalValue     = items.Where(c => c.UnitCost.HasValue).Sum(c => c.UnitCost!.Value * c.QuantityOnHand);

        return View(items);
    }

    public IActionResult Create() => View(new ConsumableItem());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ConsumableItem item)
    {
        if (ModelState.IsValid)
        {
            _context.ConsumableItems.Add(item);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"'{item.Name}' added to consumables.";
            return RedirectToAction(nameof(Index));
        }
        return View(item);
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();
        var item = await _context.ConsumableItems
            .Include(c => c.Transactions)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();
        var item = await _context.ConsumableItems.FindAsync(id);
        if (item == null) return NotFound();
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ConsumableItem item)
    {
        if (id != item.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(item);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Consumable updated.";
            return RedirectToAction(nameof(Index));
        }
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _context.ConsumableItems.FindAsync(id);
        if (item != null)
        {
            _context.ConsumableItems.Remove(item);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "Consumable deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustQuantity(int id, ConsumableTransactionType transactionType,
        int quantity, string? notes)
    {
        var item = await _context.ConsumableItems.FindAsync(id);
        if (item == null)
        {
            TempData["Error"] = "Item not found.";
            return RedirectToAction(nameof(Details), new { id });
        }

        int delta = transactionType switch
        {
            ConsumableTransactionType.Received  =>  Math.Abs(quantity),
            ConsumableTransactionType.Returned  =>  Math.Abs(quantity),
            ConsumableTransactionType.Used      => -Math.Abs(quantity),
            ConsumableTransactionType.Disposed  => -Math.Abs(quantity),
            ConsumableTransactionType.Adjustment=> quantity,
            _ => quantity
        };

        var before = item.QuantityOnHand;
        item.QuantityOnHand = Math.Max(0, item.QuantityOnHand + delta);

        _context.ConsumableTransactions.Add(new ConsumableTransaction
        {
            ConsumableItemId = id,
            TransactionType  = transactionType,
            Quantity         = delta,
            QuantityBefore   = before,
            QuantityAfter    = item.QuantityOnHand,
            Notes            = notes?.Trim(),
            PerformedByEmail = User.Identity?.Name ?? "system",
            TransactionDate  = DateTime.UtcNow,
        });

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Quantity adjusted: {before} → {item.QuantityOnHand}.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
