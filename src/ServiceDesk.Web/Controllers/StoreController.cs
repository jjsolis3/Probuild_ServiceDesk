using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize]
public class StoreController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailNotification;
    private readonly ILogger<StoreController> _logger;

    public StoreController(ServiceDeskDbContext context,
        EmailNotificationService emailNotification,
        ILogger<StoreController> logger)
    {
        _context           = context;
        _emailNotification = emailNotification;
        _logger            = logger;
    }

    // GET /Store — catalog (access + time-window enforced)
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        var (hasAccess, isOpen, statusMessage) = await CheckStoreStatusAsync(user.Id);

        if (!hasAccess)
        {
            ViewBag.Message = "You are not authorized to access the store. Please contact your administrator.";
            return View("Closed");
        }

        if (!isOpen)
        {
            ViewBag.Message = statusMessage;
            return View("Closed");
        }

        var products = await _context.StoreProducts
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();

        var welcome = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "StoreWelcomeMessage"))?.Value ?? string.Empty;

        var (q, yr) = GetCurrentQuarter();

        ViewBag.WelcomeMessage = welcome;
        ViewBag.Quarter        = q;
        ViewBag.Year           = yr;
        ViewBag.CurrentUser    = user;

        return View(products);
    }

    // POST /Store/PlaceOrder
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PlaceOrder(IFormCollection form)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        var (hasAccess, isOpen, statusMessage) = await CheckStoreStatusAsync(user.Id);
        if (!hasAccess || !isOpen)
        {
            TempData["Error"] = isOpen
                ? "You are not authorized to place orders."
                : "The store is currently closed. Orders cannot be placed.";
            return RedirectToAction(nameof(Index));
        }

        // Parse quantities from form: qty_{productId}
        var products = await _context.StoreProducts
            .Where(p => p.IsActive)
            .ToListAsync();

        var lineItems = new List<(StoreProduct Product, int Qty, string? Size, string? Gender, string? Color)>();
        foreach (var product in products)
        {
            var key = $"qty_{product.Id}";
            if (form.ContainsKey(key) &&
                int.TryParse(form[key], out var qty) && qty > 0)
            {
                var size   = form.ContainsKey($"size_{product.Id}")   ? form[$"size_{product.Id}"].ToString()   : null;
                var gender = form.ContainsKey($"gender_{product.Id}") ? form[$"gender_{product.Id}"].ToString() : null;
                var color  = form.ContainsKey($"color_{product.Id}")  ? form[$"color_{product.Id}"].ToString()  : null;
                lineItems.Add((product, qty,
                    string.IsNullOrWhiteSpace(size)   ? null : size.Trim(),
                    string.IsNullOrWhiteSpace(gender) ? null : gender.Trim(),
                    string.IsNullOrWhiteSpace(color)  ? null : color.Trim()));
            }
        }

        if (!lineItems.Any())
        {
            TempData["Error"] = "Please add at least one item to your order before submitting.";
            return RedirectToAction(nameof(Index));
        }

        var (quarter, year) = GetCurrentQuarter();
        var notes = form["notes"].ToString().Trim();

        var order = new StoreOrder
        {
            PortalUserId = user.Id,
            OrderDate    = DateTime.UtcNow,
            Status       = "Pending",
            Quarter      = quarter,
            Year         = year,
            Notes        = string.IsNullOrEmpty(notes) ? null : notes,
            OrderNumber  = "SO-PENDING",
            Items = lineItems.Select(li => new StoreOrderItem
            {
                StoreProductId          = li.Product.Id,
                Quantity                = li.Qty,
                ProductNameSnapshot     = li.Product.Name,
                ProductCategorySnapshot = li.Product.Category,
                SelectedSize            = li.Size,
                SelectedGender          = li.Gender,
                SelectedColor           = li.Color
            }).ToList()
        };

        _context.StoreOrders.Add(order);
        await _context.SaveChangesAsync();

        // Now that we have the ID, assign the readable order number
        order.OrderNumber = $"SO-{year}-Q{quarter}-{order.Id:D4}";
        await _context.SaveChangesAsync();

        // Send confirmation email — non-blocking for the user; exceptions are swallowed here
        try
        {
            await _emailNotification.SendStoreOrderConfirmationAsync(
                order, user.Email, user.FullName, order.Items.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send confirmation for order #{OrderId}", order.Id);
        }

        TempData["Success"] = $"Order {order.OrderNumber} placed successfully. A confirmation email has been sent to {user.Email}.";
        return RedirectToAction(nameof(OrderConfirmation), new { id = order.Id });
    }

    // GET /Store/OrderConfirmation/{id}
    public async Task<IActionResult> OrderConfirmation(int id)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        var order = await _context.StoreOrders
            .Include(o => o.Items)
                .ThenInclude(i => i.StoreProduct)
            .FirstOrDefaultAsync(o => o.Id == id && o.PortalUserId == user.Id);

        if (order == null) return NotFound();
        return View(order);
    }

    // GET /Store/MyOrders — all historical orders for the current user
    public async Task<IActionResult> MyOrders()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        var orders = await _context.StoreOrders
            .Include(o => o.Items)
            .Where(o => o.PortalUserId == user.Id)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync();

        ViewBag.CurrentUser = user;
        return View(orders);
    }

    // GET /Store/OrderDetail/{id}
    public async Task<IActionResult> OrderDetail(int id)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        var order = await _context.StoreOrders
            .Include(o => o.Items)
                .ThenInclude(i => i.StoreProduct)
            .FirstOrDefaultAsync(o => o.Id == id && o.PortalUserId == user.Id);

        if (order == null) return NotFound();
        ViewBag.CurrentUser = user;
        return View(order);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<PortalUser?> GetCurrentPortalUserAsync()
    {
        var idClaim = User.FindFirst("UserId")?.Value
                   ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var userId)) return null;

        return await _context.PortalUsers
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
    }

    private async Task<(bool HasAccess, bool IsOpen, string StatusMessage)> CheckStoreStatusAsync(int portalUserId)
    {
        var hasAccess = await _context.StoreAccessList
            .AnyAsync(a => a.PortalUserId == portalUserId && a.IsActive);

        if (!hasAccess)
            return (false, false, string.Empty);

        var keys = new[] { "StoreEnabled", "StoreOpenDate", "StoreCloseDate" };
        var settings = await _context.AppSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);

        var enabled = settings.GetValueOrDefault("StoreEnabled", "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
            return (true, false, "The store is currently closed. Check back at the start of the next ordering window.");

        var now       = DateTime.UtcNow;
        var openStr   = settings.GetValueOrDefault("StoreOpenDate",  string.Empty);
        var closeStr  = settings.GetValueOrDefault("StoreCloseDate", string.Empty);

        if (!string.IsNullOrEmpty(openStr) &&
            DateTime.TryParse(openStr, out var openDate) && now < openDate)
        {
            return (true, false,
                $"The store opens on {openDate:MMMM d, yyyy 'at' h:mm tt} UTC. Please check back then.");
        }

        if (!string.IsNullOrEmpty(closeStr) &&
            DateTime.TryParse(closeStr, out var closeDate) && now > closeDate)
        {
            return (true, false,
                $"The ordering window closed on {closeDate:MMMM d, yyyy}. Orders for this quarter are no longer accepted.");
        }

        return (true, true, string.Empty);
    }

    private static (int Quarter, int Year) GetCurrentQuarter()
    {
        var now = DateTime.UtcNow;
        var q   = (now.Month - 1) / 3 + 1;
        return (q, now.Year);
    }
}
