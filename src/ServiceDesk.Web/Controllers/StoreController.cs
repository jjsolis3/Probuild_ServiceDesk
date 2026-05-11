using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ServiceDesk.Web.Controllers;

[Authorize]
public class StoreController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailNotification;
    private readonly PortalNotificationService _portalNotifications;
    private readonly StoreCartService _cartService;
    private readonly ILogger<StoreController> _logger;
    private readonly IWebHostEnvironment _env;

    public StoreController(ServiceDeskDbContext context,
        EmailNotificationService emailNotification,
        PortalNotificationService portalNotifications,
        StoreCartService cartService,
        ILogger<StoreController> logger,
        IWebHostEnvironment env)
    {
        _context             = context;
        _emailNotification   = emailNotification;
        _portalNotifications = portalNotifications;
        _cartService         = cartService;
        _logger              = logger;
        _env                 = env;
    }

    /// <summary>
    /// Absolute base URL (scheme + host) for the current request, used in
    /// email templates so deep links and image src attributes resolve.
    /// </summary>
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

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

        // Surface the store close date to the view so it can show a countdown banner.
        var closeDateStr = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "StoreCloseDate"))?.Value ?? string.Empty;
        DateTime? storeCloseDate = null;
        if (!string.IsNullOrWhiteSpace(closeDateStr) && DateTime.TryParse(closeDateStr, out var cd))
            storeCloseDate = cd;

        var (q, yr) = GetCurrentQuarter();

        // Check for an existing non-cancelled order this quarter
        var existingOrder = await _context.StoreOrders
            .Where(o => o.PortalUserId == user.Id
                     && o.Quarter == q && o.Year == yr
                     && o.Status != "Cancelled")
            .OrderByDescending(o => o.OrderDate)
            .FirstOrDefaultAsync();

        // Build previously-ordered badges: total qty + most-recent quarter, keyed by product ID
        var prevOrderRows = await _context.StoreOrders
            .Where(o => o.PortalUserId == user.Id && o.Status != "Cancelled")
            .SelectMany(o => o.Items.Select(i => new {
                o.Quarter, o.Year, i.StoreProductId, i.Quantity }))
            .ToListAsync();

        var prevOrderBadges = prevOrderRows
            .GroupBy(x => x.StoreProductId)
            .ToDictionary(
                g => g.Key,
                g => {
                    var totalQty = g.Sum(x => x.Quantity);
                    var latest   = g.OrderByDescending(x => x.Year)
                                    .ThenByDescending(x => x.Quarter).First();
                    return $"{totalQty} ordered · Q{latest.Quarter} {latest.Year}";
                });

        ViewBag.WelcomeMessage       = welcome;
        ViewBag.Quarter              = q;
        ViewBag.Year                 = yr;
        ViewBag.CurrentUser          = user;
        ViewBag.ExistingOrderNumber  = existingOrder?.OrderNumber;
        ViewBag.PreviousOrderBadges  = prevOrderBadges;
        ViewBag.StoreCloseDate       = storeCloseDate;

        return View(products);
    }

    // POST /Store/PlaceOrder
    // Pulls cart lines from the server cart (StoreCartService). The legacy
    // "cartJson" form field is still honoured as a fallback when the server
    // cart is empty so older browser sessions (pre-DB-cart) still work.
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

        // Source of truth: the user's persisted server cart.
        var serverCart = await _cartService.GetForUserAsync(user.Id);
        List<CartLineInput> cartItems = serverCart
            .Select(c => new CartLineInput
            {
                ProductId        = c.StoreProductId,
                Qty              = c.Quantity,
                Size             = c.SelectedSize,
                Gender           = c.SelectedGender,
                Color            = c.SelectedColor,
                CustomSelections = DeserializeCustomSelections(c.CustomSelectionsJson)
            })
            .ToList();

        // Backward-compat: if the server cart is empty (older browser session
        // before the DB-cart rollout), fall back to the JSON form payload.
        if (cartItems.Count == 0)
        {
            var cartJson = form["cartJson"].ToString();
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

            // Reject lines missing required variants — but only when the product
            // actually has values configured for that variant (otherwise the UI
            // would have no way to satisfy the requirement).
            var hasSizeChoices  = !string.IsNullOrWhiteSpace(product.AvailableSizes);
            var hasColorChoices = !string.IsNullOrWhiteSpace(product.AvailableColors);
            if (product.HasSizes && hasSizeChoices && string.IsNullOrWhiteSpace(c.Size))
            {
                TempData["Error"] = $"Please select a size for \"{product.Name}\".";
                return RedirectToAction(nameof(Index));
            }
            if (product.HasGenderOption && string.IsNullOrWhiteSpace(c.Gender))
            {
                TempData["Error"] = $"Please select a gender option for \"{product.Name}\".";
                return RedirectToAction(nameof(Index));
            }
            if (product.HasColorOptions && hasColorChoices && string.IsNullOrWhiteSpace(c.Color))
            {
                TempData["Error"] = $"Please select a color for \"{product.Name}\".";
                return RedirectToAction(nameof(Index));
            }

            // Reject lines exceeding the per-product max qty
            var qty = Math.Clamp(c.Qty, 1, 999);
            if (product.MaxQtyPerOrder.HasValue && qty > product.MaxQtyPerOrder.Value)
            {
                TempData["Error"] =
                    $"\"{product.Name}\" has a per-order maximum of {product.MaxQtyPerOrder.Value}.";
                return RedirectToAction(nameof(Index));
            }

            // Validate any required custom options
            var customSelections = c.CustomSelections ?? new Dictionary<string, string>();
            string? customJson = null;
            if (!string.IsNullOrWhiteSpace(product.CustomOptionsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(product.CustomOptionsJson);
                    foreach (var opt in doc.RootElement.EnumerateArray())
                    {
                        var label    = opt.TryGetProperty("label", out var l)    ? l.GetString() : null;
                        var required = opt.TryGetProperty("required", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (string.IsNullOrEmpty(label)) continue;
                        var picked = customSelections.TryGetValue(label, out var pv) ? pv?.Trim() : null;
                        if (required && string.IsNullOrWhiteSpace(picked))
                        {
                            TempData["Error"] = $"Please select \"{label}\" for \"{product.Name}\".";
                            return RedirectToAction(nameof(Index));
                        }
                    }
                }
                catch { /* malformed product config — skip */ }

                var trimmed = customSelections
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Trim());
                if (trimmed.Any())
                    customJson = System.Text.Json.JsonSerializer.Serialize(trimmed);
            }

            lineItems.Add(new StoreOrderItem
            {
                StoreProductId          = product.Id,
                Quantity                = qty,
                ProductNameSnapshot     = product.Name,
                ProductCategorySnapshot = product.Category,
                SelectedSize            = string.IsNullOrWhiteSpace(c.Size)   ? null : c.Size.Trim(),
                SelectedGender          = string.IsNullOrWhiteSpace(c.Gender) ? null : c.Gender.Trim(),
                SelectedColor           = string.IsNullOrWhiteSpace(c.Color)  ? null : c.Color.Trim(),
                CustomSelectionsJson    = customJson,
                UnitPriceSnapshot       = product.HasPrice ? product.Price : null
            });
        }

        if (!lineItems.Any())
        {
            TempData["Error"] = "No valid items were found in your cart. Please try again.";
            return RedirectToAction(nameof(Index));
        }

        var (quarter, year) = GetCurrentQuarter();
        var notes = form["notes"].ToString().Trim();

        // Resolve the ordering employee's branch for fulfilment routing.
        int?    branchId   = null;
        string? branchName = null;
        if (user.EmployeeId.HasValue)
        {
            var emp = await _context.Employees
                .Include(e => e.Branch)
                .FirstOrDefaultAsync(e => e.Id == user.EmployeeId.Value);
            if (emp != null)
            {
                branchId   = emp.BranchId;
                branchName = emp.Branch?.Name;
            }
        }

        var order = new StoreOrder
        {
            PortalUserId       = user.Id,
            OrderDate          = DateTime.UtcNow,
            Status             = "Pending",
            Quarter            = quarter,
            Year               = year,
            Notes              = string.IsNullOrEmpty(notes) ? null : notes,
            OrderNumber        = "SO-PENDING",
            BranchId           = branchId,
            BranchNameSnapshot = branchName,
            Items              = lineItems
        };

        _context.StoreOrders.Add(order);
        await _context.SaveChangesAsync();

        // Now that we have the ID, assign the readable order number
        order.OrderNumber = $"SO-{year}-Q{quarter}-{order.Id:D4}";
        await _context.SaveChangesAsync();

        // Cart submitted successfully — clear it so the user has a clean slate
        // for any next-quarter order.
        await _cartService.ClearAsync(user.Id);

        // Re-load items with the product navigation so emails can render thumbnails.
        var itemsWithProducts = await _context.StoreOrderItems
            .Include(i => i.StoreProduct)
            .Where(i => i.StoreOrderId == order.Id)
            .ToListAsync();

        // Send confirmation email + ops digest — non-blocking for the user.
        try
        {
            await _emailNotification.SendStoreOrderConfirmationAsync(
                order, user.Email, user.FullName, itemsWithProducts, BaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send confirmation for order #{OrderId}", order.Id);
        }

        try
        {
            await _emailNotification.SendStoreOrderOpsNotificationAsync(
                order, user.FullName, itemsWithProducts, BaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Store] Failed to send ops notification for order #{OrderId}", order.Id);
        }

        // In-app notification for the user.
        await _portalNotifications.NotifyAsync(
            user.Id,
            type: "StoreOrderPlaced",
            title: $"Order {order.OrderNumber} placed",
            message: $"{order.Items.Sum(i => i.Quantity)} item(s) submitted for Q{order.Quarter} {order.Year}.",
            linkUrl: Url.Action(nameof(OrderDetail), new { id = order.Id }),
            icon: "bi-receipt");

        // In-app notification for every ops user (plus Admins as fallback) so the
        // notification bell mirrors what the ops digest email said.
        try
        {
            var opsIds = await _context.StoreOperationsAccess
                .Where(a => a.IsActive)
                .Select(a => a.PortalUserId)
                .ToListAsync();
            var adminIds = await _context.PortalUsers
                .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin")
                .Select(u => u.Id)
                .ToListAsync();
            var recipients = opsIds.Union(adminIds).Where(id => id != user.Id);

            await _portalNotifications.NotifyManyAsync(
                recipients,
                type: "StoreOrderOpsAlert",
                title: $"New store order: {order.OrderNumber}",
                message: $"{user.FullName} ordered {order.Items.Sum(i => i.Quantity)} item(s).",
                linkUrl: Url.Action(nameof(OperationsHub), new { year = order.Year, quarter = order.Quarter, status = "Pending" }),
                icon: "bi-cart-plus");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Store] Failed to broadcast ops in-app notifications for order #{OrderId}", order.Id);
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

    // ── Cart API ──────────────────────────────────────────────────────────────
    // These endpoints back the catalog UI's cart drawer. The cart is persisted
    // server-side so it survives device switches and refresh.

    private object SerializeCartLine(StoreCartItem c, Dictionary<string, string>? custom = null)
        => new
        {
            lineId    = c.Id,
            productId = c.StoreProductId,
            name      = c.StoreProduct?.Name ?? string.Empty,
            qty       = c.Quantity,
            size      = c.SelectedSize   ?? string.Empty,
            gender    = c.SelectedGender ?? string.Empty,
            color     = c.SelectedColor  ?? string.Empty,
            customSelections = custom ?? DeserializeCustomSelections(c.CustomSelectionsJson)
                                       ?? new Dictionary<string, string>(),
            unitPrice = c.StoreProduct != null && c.StoreProduct.HasPrice
                            ? c.StoreProduct.Price
                            : (decimal?)null,
            maxQty    = c.StoreProduct?.MaxQtyPerOrder ?? 999
        };

    // GET /Store/Cart — returns the user's current cart as a JSON array.
    [HttpGet]
    public async Task<IActionResult> Cart()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        var items = await _cartService.GetForUserAsync(user.Id);
        return Json(items.Select(i => SerializeCartLine(i)).ToArray());
    }

    public class CartAddRequest
    {
        public int ProductId { get; set; }
        public int Qty { get; set; } = 1;
        public string? Size { get; set; }
        public string? Gender { get; set; }
        public string? Color { get; set; }
        public Dictionary<string, string>? CustomSelections { get; set; }
    }

    // POST /Store/CartAdd — adds a single line; returns the new line as JSON.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CartAdd([FromBody] CartAddRequest req)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        if (req == null || req.ProductId <= 0) return BadRequest();

        var line = await _cartService.AddAsync(
            user.Id, req.ProductId, req.Qty,
            req.Size, req.Gender, req.Color, req.CustomSelections);

        if (line == null) return NotFound(new { error = "Product not available." });
        return Json(SerializeCartLine(line, req.CustomSelections));
    }

    public class CartSetQtyRequest
    {
        public int LineId { get; set; }
        public int Qty { get; set; }
    }

    // POST /Store/CartSetQty — updates a line's quantity; qty=0 removes it.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CartSetQty([FromBody] CartSetQtyRequest req)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        if (req == null || req.LineId <= 0) return BadRequest();

        var ok = await _cartService.SetQuantityAsync(user.Id, req.LineId, req.Qty);
        return ok ? Ok() : NotFound();
    }

    public class CartRemoveRequest { public int LineId { get; set; } }

    // POST /Store/CartRemove — removes a line.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CartRemove([FromBody] CartRemoveRequest req)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        if (req == null || req.LineId <= 0) return BadRequest();

        var ok = await _cartService.RemoveAsync(user.Id, req.LineId);
        return ok ? Ok() : NotFound();
    }

    // POST /Store/CartClear — empties the user's cart entirely.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CartClear()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        await _cartService.ClearAsync(user.Id);
        return Ok();
    }

    private static Dictionary<string, string>? DeserializeCustomSelections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
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
            .Include(o => o.Branch)
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
            .Include(o => o.Branch)
            .Include(o => o.Items)
                .ThenInclude(i => i.StoreProduct)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(o => o.Status == status);

        var orders = await query
            .OrderBy(o => o.Status)
            .ThenBy(o => o.OrderDate)
            .ToListAsync();

        // Build product totals with a per-branch breakdown so fulfilment can see
        // which locations need each item.
        var productTotals = orders
            .SelectMany(o => o.Items.Select(i => new {
                Item   = i,
                Order  = o,
                Branch = o.Branch?.Name ?? o.BranchNameSnapshot ?? "Unassigned"
            }))
            .GroupBy(x => new { x.Item.StoreProductId, x.Item.ProductNameSnapshot, x.Item.ProductCategorySnapshot })
            .Select(g => new
            {
                Name       = g.Key.ProductNameSnapshot,
                Category   = g.Key.ProductCategorySnapshot,
                TotalQty   = g.Sum(x => x.Item.Quantity),
                OrderCount = g.Select(x => x.Item.StoreOrderId).Distinct().Count(),
                ByBranch   = g.GroupBy(x => x.Branch)
                              .Select(b => new {
                                  Branch = b.Key,
                                  Qty    = b.Sum(x => x.Item.Quantity)
                              })
                              .OrderByDescending(b => b.Qty)
                              .ToList()
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
        if (oldStatus != newStatus)
            order.LastStatusChangedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        if (oldStatus != newStatus)
        {
            try
            {
                await _emailNotification.SendOrderStatusUpdateAsync(
                    order, order.PortalUser.Email, order.PortalUser.FullName, newStatus, BaseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OpsHub] Failed to send status update email for order #{OrderId}", order.Id);
            }

            await _portalNotifications.NotifyAsync(
                order.PortalUserId,
                type: "StoreOrderStatus",
                title: $"Order {order.OrderNumber} — {newStatus}",
                message: newStatus switch
                {
                    "Confirmed" => "Your order has been reviewed and confirmed.",
                    "Fulfilled" => "Your order has been fulfilled.",
                    "Cancelled" => "Your order was cancelled.",
                    _           => $"Status updated to {newStatus}."
                },
                linkUrl: Url.Action(nameof(OrderDetail), new { id = order.Id }),
                icon: newStatus switch
                {
                    "Confirmed" => "bi-check-circle",
                    "Fulfilled" => "bi-box-seam",
                    "Cancelled" => "bi-x-circle",
                    _           => "bi-receipt"
                });
        }

        TempData["Success"] = $"Order {order.OrderNumber} marked as {newStatus}. A notification email has been sent to {order.PortalUser.Email}.";
        return RedirectToAction(nameof(OperationsHub), new { year, quarter, status = returnStatus });
    }

    // POST /Store/OpsUpdateStatusBulk
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsUpdateStatusBulk(
        List<int> orderIds, string newStatus, int year, int quarter, string? returnStatus)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");

        if (!await CanAccessOpsHubAsync(user.Id))
            return Forbid();

        var validStatuses = new[] { "Pending", "Confirmed", "Fulfilled", "Cancelled" };
        if (!validStatuses.Contains(newStatus) || orderIds == null || !orderIds.Any())
        {
            TempData["Error"] = "Invalid bulk update request.";
            return RedirectToAction(nameof(OperationsHub), new { year, quarter, status = returnStatus });
        }

        var orders = await _context.StoreOrders
            .Include(o => o.PortalUser)
            .Include(o => o.Items)
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync();

        int updated = 0;
        var now = DateTime.UtcNow;
        foreach (var order in orders)
        {
            if (order.Status == newStatus) continue;
            order.Status = newStatus;
            order.LastStatusChangedDate = now;
            updated++;
            try
            {
                await _emailNotification.SendOrderStatusUpdateAsync(
                    order, order.PortalUser.Email, order.PortalUser.FullName, newStatus, BaseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OpsHub] Bulk: failed to send status email for order #{OrderId}", order.Id);
            }

            await _portalNotifications.NotifyAsync(
                order.PortalUserId,
                type: "StoreOrderStatus",
                title: $"Order {order.OrderNumber} — {newStatus}",
                message: newStatus switch
                {
                    "Confirmed" => "Your order has been reviewed and confirmed.",
                    "Fulfilled" => "Your order has been fulfilled.",
                    "Cancelled" => "Your order was cancelled.",
                    _           => $"Status updated to {newStatus}."
                },
                linkUrl: Url.Action(nameof(OrderDetail), new { id = order.Id }),
                icon: newStatus switch
                {
                    "Confirmed" => "bi-check-circle",
                    "Fulfilled" => "bi-box-seam",
                    "Cancelled" => "bi-x-circle",
                    _           => "bi-receipt"
                });
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = $"{updated} order(s) marked as {newStatus}.";
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

    // ── Ops Hub — Store Settings ──────────────────────────────────────────────

    // GET /Store/OpsStoreSettings
    public async Task<IActionResult> OpsStoreSettings()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id))
        {
            TempData["Error"] = "You are not authorised to access store settings.";
            return RedirectToAction(nameof(OperationsHub));
        }

        var settings = await _context.AppSettings
            .Where(s => s.Category == "Store")
            .ToListAsync();
        return View(settings);
    }

    // POST /Store/OpsStoreSettings
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsStoreSettings(IFormCollection form)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id)) return Forbid();

        var keys = new[] { "StoreEnabled", "StoreOpenDate", "StoreCloseDate", "StoreWelcomeMessage" };
        var settings = await _context.AppSettings
            .Where(s => keys.Contains(s.Key))
            .ToListAsync();

        foreach (var setting in settings)
        {
            if (!form.ContainsKey(setting.Key)) continue;
            setting.Value = form[setting.Key].LastOrDefault() ?? string.Empty;
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = "Store settings saved.";
        return RedirectToAction(nameof(OpsStoreSettings));
    }

    // ── Ops Hub — Product Catalog Management ──────────────────────────────────

    // GET /Store/OpsProducts
    public async Task<IActionResult> OpsProducts()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id))
        {
            TempData["Error"] = "You are not authorised to manage store products.";
            return RedirectToAction(nameof(OperationsHub));
        }

        var products = await _context.StoreProducts
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();
        return View(products);
    }

    // GET /Store/OpsProductCreate
    public async Task<IActionResult> OpsProductCreate()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id))
            return RedirectToAction(nameof(OperationsHub));

        ViewBag.ExistingCategories = await GetExistingCategoriesAsync();
        return View(new StoreProduct());
    }

    // POST /Store/OpsProductCreate
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductCreate(
        StoreProduct product,
        IFormFile? imageFile,
        List<IFormFile>? galleryFiles,
        List<string>? galleryTags)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id)) return Forbid();

        if (!ModelState.IsValid)
        {
            ViewBag.ExistingCategories = await GetExistingCategoriesAsync();
            return View(product);
        }

        if (!product.HasPrice) product.Price = null;
        product.Tags = string.IsNullOrWhiteSpace(product.Tags)
            ? null
            : string.Join(",", product.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        product.CustomOptionsJson = NormalizeCustomOptionsJson(product.CustomOptionsJson);
        product.ImagePath  = await SaveStoreImageAsync(imageFile, null);
        product.CreatedDate = DateTime.UtcNow;
        _context.StoreProducts.Add(product);
        await _context.SaveChangesAsync();

        if (galleryFiles != null && galleryFiles.Count > 0)
        {
            var sort = 100;
            for (var i = 0; i < galleryFiles.Count; i++)
            {
                var path = await SaveStoreImageAsync(galleryFiles[i], null);
                if (!string.IsNullOrEmpty(path))
                {
                    var tag = galleryTags != null && i < galleryTags.Count ? galleryTags[i]?.Trim() : null;
                    _context.StoreProductImages.Add(new StoreProductImage
                    {
                        StoreProductId = product.Id,
                        ImagePath      = path,
                        VariantTag     = string.IsNullOrWhiteSpace(tag) ? null : tag,
                        SortOrder      = sort,
                        CreatedDate    = DateTime.UtcNow
                    });
                    sort += 10;
                }
            }
            await _context.SaveChangesAsync();
        }

        TempData["Success"] = $"Product \"{product.Name}\" created.";
        return RedirectToAction(nameof(OpsProducts));
    }

    // GET /Store/OpsProductEdit/{id}
    public async Task<IActionResult> OpsProductEdit(int id)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id))
            return RedirectToAction(nameof(OperationsHub));

        var product = await _context.StoreProducts
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return NotFound();

        ViewBag.ExistingCategories = await GetExistingCategoriesAsync();
        return View(product);
    }

    // POST /Store/OpsProductEdit/{id}
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductEdit(int id,
        [Bind("Id,Name,Description,Category,UnitOfMeasure,IsActive,SortOrder,HasSizes,HasGenderOption,HasColorOptions,AvailableSizes,AvailableColors,HasPrice,Price,CustomOptionsJson,Tags,MaxQtyPerOrder")]
        StoreProduct product,
        IFormFile? imageFile,
        List<IFormFile>? galleryFiles,
        List<string>? galleryTags,
        string? galleryMetaJson,
        bool clearImage = false)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id)) return Forbid();
        if (id != product.Id) return BadRequest();

        if (!ModelState.IsValid)
        {
            ViewBag.ExistingCategories = await GetExistingCategoriesAsync();
            // Reload image data from DB so the view renders existing images correctly.
            var dbSnap = await _context.StoreProducts.Include(p => p.Images).AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (dbSnap != null) { product.ImagePath = dbSnap.ImagePath; product.Images = dbSnap.Images; }
            return View(product);
        }

        var existing = await _context.StoreProducts
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (existing == null) return NotFound();

        existing.Name             = product.Name;
        existing.Description      = product.Description;
        existing.Category         = product.Category;
        existing.UnitOfMeasure    = product.UnitOfMeasure;
        existing.IsActive         = product.IsActive;
        existing.SortOrder        = product.SortOrder;
        existing.HasSizes         = product.HasSizes;
        existing.HasGenderOption  = product.HasGenderOption;
        existing.HasColorOptions  = product.HasColorOptions;
        existing.AvailableSizes   = string.IsNullOrWhiteSpace(product.AvailableSizes)   ? null : product.AvailableSizes.Trim();
        existing.AvailableColors  = string.IsNullOrWhiteSpace(product.AvailableColors)  ? null : product.AvailableColors.Trim();
        existing.HasPrice          = product.HasPrice;
        existing.Price             = product.HasPrice ? product.Price : null;
        existing.MaxQtyPerOrder    = product.MaxQtyPerOrder;
        existing.Tags = string.IsNullOrWhiteSpace(product.Tags)
            ? null
            : string.Join(",", product.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        existing.CustomOptionsJson = NormalizeCustomOptionsJson(product.CustomOptionsJson);

        if (clearImage) { DeleteStoreImage(existing.ImagePath); existing.ImagePath = null; }
        else existing.ImagePath = await SaveStoreImageAsync(imageFile, existing.ImagePath);

        // Apply per-image tag / alt / sort updates from serialised JSON.
        if (!string.IsNullOrWhiteSpace(galleryMetaJson))
        {
            try
            {
                var metas = System.Text.Json.JsonSerializer.Deserialize<List<GalleryImageMeta>>(galleryMetaJson);
                if (metas != null)
                {
                    foreach (var meta in metas)
                    {
                        var img = existing.Images.FirstOrDefault(i => i.Id == meta.Id);
                        if (img == null) continue;
                        img.VariantTag = string.IsNullOrWhiteSpace(meta.Tag) ? null : meta.Tag.Trim();
                        img.Alt        = string.IsNullOrWhiteSpace(meta.Alt) ? null : meta.Alt.Trim();
                        img.SortOrder  = Math.Clamp(meta.Sort, 0, 9999);
                    }
                }
            }
            catch { /* malformed JSON — skip */ }
        }

        if (galleryFiles != null && galleryFiles.Count > 0)
        {
            var sort = (existing.Images.Any() ? existing.Images.Max(i => i.SortOrder) : 100) + 10;
            for (var i = 0; i < galleryFiles.Count; i++)
            {
                var path = await SaveStoreImageAsync(galleryFiles[i], null);
                if (!string.IsNullOrEmpty(path))
                {
                    var tag = galleryTags != null && i < galleryTags.Count ? galleryTags[i]?.Trim() : null;
                    _context.StoreProductImages.Add(new StoreProductImage
                    {
                        StoreProductId = existing.Id,
                        ImagePath      = path,
                        VariantTag     = string.IsNullOrWhiteSpace(tag) ? null : tag,
                        SortOrder      = sort,
                        CreatedDate    = DateTime.UtcNow
                    });
                    sort += 10;
                }
            }
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Product \"{existing.Name}\" updated.";
        return RedirectToAction(nameof(OpsProductEdit), new { id = existing.Id });
    }

    // POST /Store/OpsProductImageDelete
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductImageDelete(int imageId)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id)) return Forbid();

        var img = await _context.StoreProductImages.FindAsync(imageId);
        if (img == null) return NotFound();

        DeleteStoreImage(img.ImagePath);
        var productId = img.StoreProductId;
        _context.StoreProductImages.Remove(img);
        await _context.SaveChangesAsync();
        TempData["Success"] = "Image removed.";
        return RedirectToAction(nameof(OpsProductEdit), new { id = productId });
    }

    // POST /Store/OpsProductDelete/{id}
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductDelete(int id)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id)) return Forbid();

        var product = await _context.StoreProducts.FindAsync(id);
        if (product == null) return NotFound();

        bool hasOrders = await _context.StoreOrderItems.AnyAsync(i => i.StoreProductId == id);
        if (hasOrders)
        {
            product.IsActive = false;
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Product \"{product.Name}\" deactivated (it has existing orders).";
        }
        else
        {
            DeleteStoreImage(product.ImagePath);
            _context.StoreProducts.Remove(product);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Product \"{product.Name}\" deleted.";
        }

        return RedirectToAction(nameof(OpsProducts));
    }

    // ── Shared image / category helpers ───────────────────────────────────────

    private sealed record GalleryImageMeta(int Id, string? Tag, string? Alt, int Sort);

    private const int StoreImageMaxPx = 800;

    private async Task<string?> SaveStoreImageAsync(IFormFile? file, string? existing)
    {
        if (file == null || file.Length == 0) return existing;

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext)) return existing;

        var dir = Path.Combine(_env.WebRootPath, "images", "store");
        Directory.CreateDirectory(dir);

        var saveExt  = ext == ".png" ? ".png" : ".jpg";
        var fileName = $"{Guid.NewGuid()}{saveExt}";
        var path     = Path.Combine(dir, fileName);

        try
        {
            using var img = await SixLabors.ImageSharp.Image.LoadAsync(file.OpenReadStream());
            if (img.Width > StoreImageMaxPx || img.Height > StoreImageMaxPx)
            {
                img.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(StoreImageMaxPx, StoreImageMaxPx),
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
                }));
            }
            if (saveExt == ".png")
                await img.SaveAsPngAsync(path);
            else
                await img.SaveAsJpegAsync(path, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
        }
        catch
        {
            using var stream = new FileStream(path, FileMode.Create);
            await file.CopyToAsync(stream);
        }

        if (!string.IsNullOrEmpty(existing))
            DeleteStoreImage(existing);

        return $"/images/store/{fileName}";
    }

    private void DeleteStoreImage(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        var full = Path.Combine(_env.WebRootPath, relativePath.TrimStart('/'));
        if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
    }

    private async Task<List<string>> GetExistingCategoriesAsync()
    {
        return await _context.StoreProducts
            .Where(p => p.Category != null && p.Category != "")
            .Select(p => p.Category!)
            .Distinct().OrderBy(c => c).ToListAsync();
    }

    private static string? NormalizeCustomOptionsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var keep = new List<object>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var label    = el.TryGetProperty("label",    out var l) ? l.GetString() : null;
                var values   = el.TryGetProperty("values",   out var v) ? v.GetString() : null;
                var required = el.TryGetProperty("required", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(values)) continue;
                keep.Add(new { label = label!.Trim(), values = values!.Trim(), required });
            }
            return keep.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(keep);
        }
        catch { return null; }
    }

    // Cart line item posted as part of the JSON payload from the catalog page
    private class CartLineInput
    {
        public int ProductId { get; set; }
        public int Qty { get; set; }
        public string? Size { get; set; }
        public string? Gender { get; set; }
        public string? Color { get; set; }
        public Dictionary<string, string>? CustomSelections { get; set; }
    }
}
