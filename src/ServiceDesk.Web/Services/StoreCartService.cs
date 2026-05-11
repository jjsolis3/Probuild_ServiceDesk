using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Server-side cart for the Company Store. Persists cart lines per portal user
/// so the cart survives device switches, refresh, and sign-out. PlaceOrder
/// pulls from this service as the source of truth and clears it on success.
/// </summary>
public class StoreCartService
{
    private readonly ServiceDeskDbContext _context;

    public StoreCartService(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public Task<List<StoreCartItem>> GetForUserAsync(int portalUserId)
        => _context.StoreCartItems
            .Include(c => c.StoreProduct)
            .Where(c => c.PortalUserId == portalUserId)
            .OrderBy(c => c.AddedDate)
            .ThenBy(c => c.Id)
            .ToListAsync();

    /// <summary>
    /// Adds a new cart line. Caller is responsible for any variant validation
    /// (the catalog modal enforces required size/color/gender/custom options).
    /// Always creates a new row so two adds of the same product with the same
    /// variants are separate lines — matches the prior sessionStorage behaviour.
    /// </summary>
    public async Task<StoreCartItem?> AddAsync(
        int portalUserId,
        int productId,
        int quantity,
        string? size, string? gender, string? color,
        Dictionary<string, string>? customSelections)
    {
        // Reject products that aren't currently active so the cart can't hold
        // dangling references.
        var product = await _context.StoreProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);
        if (product == null) return null;

        var qty = Math.Clamp(quantity, 1, 999);
        if (product.MaxQtyPerOrder.HasValue)
            qty = Math.Min(qty, product.MaxQtyPerOrder.Value);

        string? customJson = null;
        if (customSelections != null)
        {
            var trimmed = customSelections
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(k => k.Key, v => v.Value.Trim());
            if (trimmed.Any())
                customJson = System.Text.Json.JsonSerializer.Serialize(trimmed);
        }

        var line = new StoreCartItem
        {
            PortalUserId         = portalUserId,
            StoreProductId       = productId,
            Quantity             = qty,
            SelectedSize         = string.IsNullOrWhiteSpace(size)   ? null : size.Trim(),
            SelectedGender       = string.IsNullOrWhiteSpace(gender) ? null : gender.Trim(),
            SelectedColor        = string.IsNullOrWhiteSpace(color)  ? null : color.Trim(),
            CustomSelectionsJson = customJson,
            AddedDate            = DateTime.UtcNow
        };
        _context.StoreCartItems.Add(line);
        await _context.SaveChangesAsync();

        // Reload with product navigation so the caller can return a complete DTO.
        line.StoreProduct = product;
        return line;
    }

    /// <summary>
    /// Sets the quantity for a cart line. Quantity of zero removes the line.
    /// Quantities are clamped to the product's MaxQtyPerOrder when configured.
    /// Returns false if the line doesn't belong to the user.
    /// </summary>
    public async Task<bool> SetQuantityAsync(int portalUserId, int lineId, int quantity)
    {
        var line = await _context.StoreCartItems
            .Include(c => c.StoreProduct)
            .FirstOrDefaultAsync(c => c.Id == lineId && c.PortalUserId == portalUserId);
        if (line == null) return false;

        if (quantity <= 0)
        {
            _context.StoreCartItems.Remove(line);
            await _context.SaveChangesAsync();
            return true;
        }

        var qty = Math.Clamp(quantity, 1, 999);
        if (line.StoreProduct?.MaxQtyPerOrder is int max)
            qty = Math.Min(qty, max);

        line.Quantity = qty;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveAsync(int portalUserId, int lineId)
    {
        var line = await _context.StoreCartItems
            .FirstOrDefaultAsync(c => c.Id == lineId && c.PortalUserId == portalUserId);
        if (line == null) return false;
        _context.StoreCartItems.Remove(line);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task ClearAsync(int portalUserId)
    {
        var lines = await _context.StoreCartItems
            .Where(c => c.PortalUserId == portalUserId)
            .ToListAsync();
        if (lines.Count == 0) return;
        _context.StoreCartItems.RemoveRange(lines);
        await _context.SaveChangesAsync();
    }
}
