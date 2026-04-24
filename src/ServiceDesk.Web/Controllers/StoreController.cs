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
            .Include(p => p.Images)
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
    // Accepts a JSON cart payload in the "cartJson" form field, plus optional "notes".
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

        // Parse the cart JSON payload
        var cartJson = form["cartJson"].ToString();
        List<CartLineInput> cartItems;
        try
        {
            cartItems = System.Text.Json.JsonSerializer.Deserialize<List<CartLineInput>>(
                string.IsNullOrWhiteSpace(cartJson) ? "[]" : cartJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<CartLineInput>();
        }
        catch
        {
            TempData["Error"] = "Your cart could not be read. Please try again.";
            return RedirectToAction(nameof(Index));
        }

        cartItems = cartItems.Where(c => c.ProductId > 0 && c.Qty > 0).ToList();

        if (!cartItems.Any())
        {
            TempData["Error"] = "Please add at least one item to your cart before submitting.";
            return RedirectToAction(nameof(Index));
        }

        var productIds = cartItems.Select(c => c.ProductId).Distinct().ToList();
        var products   = await _context.StoreProducts
            .Where(p => p.IsActive && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        var lineItems = new List<StoreOrderItem>();
        foreach (var c in cartItems)
        {
            if (!products.TryGetValue(c.ProductId, out var product)) continue;
            lineItems.Add(new StoreOrderItem
            {
                StoreProductId          = product.Id,
                Quantity                = Math.Clamp(c.Qty, 1, 999),
                ProductNameSnapshot     = product.Name,
                ProductCategorySnapshot = product.Category,
                SelectedSize            = string.IsNullOrWhiteSpace(c.Size)   ? null : c.Size.Trim(),
                SelectedGender          = string.IsNullOrWhiteSpace(c.Gender) ? null : c.Gender.Trim(),
                SelectedColor           = string.IsNullOrWhiteSpace(c.Color)  ? null : c.Color.Trim()
            });
        }

        if (!lineItems.Any())
        {
            TempData["Error"] = "No valid items were found in your cart. Please try again.";
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
            Items        = lineItems
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

    // ── Operations Hub ────────────────────────────────────────────────────────

    // GET /Store/OperationsHub
    public async Task<IActionResult> OperationsHub(int? year, int? quarter, string? status)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        if (!await CanAccessOpsHubAsync(user.Id))
        {
            TempData["Error"] = "You are not authorised to access the Operations Hub.";
            return RedirectToAction("Index", "Portal");
        }

        var now = DateTime.UtcNow;
        year    ??= now.Year;
        quarter ??= (now.Month - 1) / 3 + 1;

        var baseQuery = _context.StoreOrders
            .Where(o => o.Year == year && o.Quarter == quarter);

        // Status counts for the summary cards
        var statusCounts = await baseQuery
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var query = baseQuery
            .Include(o => o.PortalUser)
            .Include(o => o.Items)
                .ThenInclude(i => i.StoreProduct)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var orders = await query
            .OrderBy(o => o.Status)
            .ThenBy(o => o.OrderDate)
            .ToListAsync();

        // Product totals for the current view
        var productTotals = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.StoreProductId, i.ProductNameSnapshot, i.ProductCategorySnapshot })
            .Select(g => new
            {
                Name       = g.Key.ProductNameSnapshot,
                Category   = g.Key.ProductCategorySnapshot,
                TotalQty   = g.Sum(i => i.Quantity),
                OrderCount = g.Select(i => i.StoreOrderId).Distinct().Count()
            })
            .OrderByDescending(p => p.TotalQty)
            .ToList();

        ViewBag.Year         = year;
        ViewBag.Quarter      = quarter;
        ViewBag.Status       = status;
        ViewBag.StatusCounts = statusCounts.ToDictionary(x => x.Status, x => x.Count);
        ViewBag.ProductTotals = productTotals;
        ViewBag.Statuses     = new[] { "Pending", "Confirmed", "Fulfilled", "Cancelled" };
        ViewBag.TotalOrders  = statusCounts.Sum(x => x.Count);

        return View(orders);
    }

    // POST /Store/OpsUpdateStatus
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsUpdateStatus(
        int orderId, string newStatus, int year, int quarter, string? returnStatus)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        if (!await CanAccessOpsHubAsync(user.Id))
            return Forbid();

        var validStatuses = new[] { "Pending", "Confirmed", "Fulfilled", "Cancelled" };
        if (!validStatuses.Contains(newStatus))
        {
            TempData["Error"] = "Invalid status value.";
            return RedirectToAction(nameof(OperationsHub), new { year, quarter, status = returnStatus });
        }

        var order = await _context.StoreOrders
            .Include(o => o.PortalUser)
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return NotFound();

        var oldStatus = order.Status;
        order.Status = newStatus;
        await _context.SaveChangesAsync();

        if (oldStatus != newStatus)
        {
            try
            {
                await _emailNotification.SendOrderStatusUpdateAsync(
                    order, order.PortalUser.Email, order.PortalUser.FullName, newStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OpsHub] Failed to send status update email for order #{OrderId}", order.Id);
            }
        }

        TempData["Success"] = $"Order {order.OrderNumber} marked as {newStatus}. A notification email has been sent to {order.PortalUser.Email}.";
        return RedirectToAction(nameof(OperationsHub), new { year, quarter, status = returnStatus });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> CanAccessOpsHubAsync(int portalUserId)
    {
        if (User.IsInRole("Admin") || User.IsInRole("IT Agent")) return true;

        try
        {
            return await _context.StoreOperationsAccess
                .AnyAsync(a => a.PortalUserId == portalUserId && a.IsActive);
        }
        catch { return false; }
    }

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

    // Cart line item posted as part of the JSON payload from the catalog page
    private class CartLineInput
    {
        public int ProductId { get; set; }
        public int Qty { get; set; }
        public string? Size { get; set; }
        public string? Gender { get; set; }
        public string? Color { get; set; }
    }
}
