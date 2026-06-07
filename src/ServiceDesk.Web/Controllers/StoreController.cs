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
    private readonly PortalNotificationService _portalNotifications;
    private readonly StoreCartService _cartService;
    private readonly StoreProductAdminService _productAdmin;
    private readonly ILogger<StoreController> _logger;

    public StoreController(ServiceDeskDbContext context,
        EmailNotificationService emailNotification,
        PortalNotificationService portalNotifications,
        StoreCartService cartService,
        StoreProductAdminService productAdmin,
        ILogger<StoreController> logger)
    {
        _context             = context;
        _emailNotification   = emailNotification;
        _portalNotifications = portalNotifications;
        _cartService         = cartService;
        _productAdmin        = productAdmin;
        _logger              = logger;
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

        // Favorited product IDs so the catalog can render heart icons + filter.
        var favoriteIds = await _context.StoreProductFavorites
            .Where(f => f.PortalUserId == user.Id)
            .Select(f => f.StoreProductId)
            .ToListAsync();

        // The most recent non-cancelled, fulfilled-or-confirmed order from a
        // previous quarter. Drives the "Reorder from last quarter" CTA. We
        // explicitly skip the current quarter so users don't reorder what
        // they already submitted this window.
        var lastOrder = await _context.StoreOrders
            .Include(o => o.Items)
            .Where(o => o.PortalUserId == user.Id
                     && o.Status != "Cancelled"
                     && !(o.Year == yr && o.Quarter == q))
            .OrderByDescending(o => o.Year)
            .ThenByDescending(o => o.Quarter)
            .ThenByDescending(o => o.OrderDate)
            .FirstOrDefaultAsync();

        ViewBag.WelcomeMessage       = welcome;
        ViewBag.Quarter              = q;
        ViewBag.Year                 = yr;
        ViewBag.CurrentUser          = user;
        ViewBag.ExistingOrderNumber  = existingOrder?.OrderNumber;
        ViewBag.PreviousOrderBadges  = prevOrderBadges;
        ViewBag.StoreCloseDate       = storeCloseDate;
        ViewBag.FavoriteProductIds   = favoriteIds;
        ViewBag.LastOrderId          = lastOrder?.Id;
        ViewBag.LastOrderLabel       = lastOrder != null
            ? $"Q{lastOrder.Quarter} {lastOrder.Year}"
            : null;
        ViewBag.LastOrderItemCount   = lastOrder?.Items.Count ?? 0;
        ViewBag.PreferredSize        = user.PreferredStoreSize;
        ViewBag.PreferredGender      = user.PreferredStoreGender;
        ViewBag.PreferredColor       = user.PreferredStoreColor;

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

    // ── Favorites API ─────────────────────────────────────────────────────────

    public class FavoriteToggleRequest { public int ProductId { get; set; } }

    // POST /Store/FavoriteToggle — toggles a product's favorite state for the
    // current user. Returns the new state so the UI can update instantly.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FavoriteToggle([FromBody] FavoriteToggleRequest req)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();
        if (req == null || req.ProductId <= 0) return BadRequest();

        var existing = await _context.StoreProductFavorites
            .FirstOrDefaultAsync(f => f.PortalUserId == user.Id && f.StoreProductId == req.ProductId);

        bool isFavorite;
        if (existing != null)
        {
            _context.StoreProductFavorites.Remove(existing);
            isFavorite = false;
        }
        else
        {
            // Verify the product exists and is active before letting the user
            // favorite it. Cheaper than enforcing it via a FK constraint check.
            var exists = await _context.StoreProducts
                .AnyAsync(p => p.Id == req.ProductId && p.IsActive);
            if (!exists) return NotFound();
            _context.StoreProductFavorites.Add(new StoreProductFavorite
            {
                PortalUserId   = user.Id,
                StoreProductId = req.ProductId,
                AddedDate      = DateTime.UtcNow
            });
            isFavorite = true;
        }
        await _context.SaveChangesAsync();
        return Json(new { productId = req.ProductId, isFavorite });
    }

    // ── Quick reorder ─────────────────────────────────────────────────────────

    // POST /Store/ReorderLastOrder — copies eligible items from the user's most
    // recent non-cancelled order (prior quarter) into the current cart.
    // Items whose product is no longer active or whose stored variants are
    // missing are skipped silently; the response reports how many were added.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderLastOrder()
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return Unauthorized();

        var (hasAccess, isOpen, _) = await CheckStoreStatusAsync(user.Id);
        if (!hasAccess || !isOpen)
            return BadRequest(new { error = "Store is closed." });

        var (q, yr) = GetCurrentQuarter();

        var lastOrder = await _context.StoreOrders
            .Include(o => o.Items)
            .Where(o => o.PortalUserId == user.Id
                     && o.Status != "Cancelled"
                     && !(o.Year == yr && o.Quarter == q))
            .OrderByDescending(o => o.Year)
            .ThenByDescending(o => o.Quarter)
            .ThenByDescending(o => o.OrderDate)
            .FirstOrDefaultAsync();

        if (lastOrder == null)
            return NotFound(new { error = "No previous order to reorder from." });

        int added = 0, skipped = 0;
        foreach (var item in lastOrder.Items)
        {
            var custom = DeserializeCustomSelections(item.CustomSelectionsJson);
            var line = await _cartService.AddAsync(
                user.Id,
                item.StoreProductId,
                item.Quantity,
                item.SelectedSize,
                item.SelectedGender,
                item.SelectedColor,
                custom);
            if (line != null) added++; else skipped++;
        }

        return Json(new { added, skipped, sourceOrder = lastOrder.OrderNumber });
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
        var gate = await EnforceOpsAccessAsync(
            forbiddenMessage: "You are not authorised to manage store products.");
        if (gate != null) return gate;

        return View(await _productAdmin.ListAsync());
    }

    // GET /Store/OpsProductCreate
    public async Task<IActionResult> OpsProductCreate()
    {
        var gate = await EnforceOpsAccessAsync();
        if (gate != null) return gate;

        ViewBag.ExistingCategories = await _productAdmin.GetExistingCategoriesAsync();
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
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;

        if (!ModelState.IsValid)
        {
            ViewBag.ExistingCategories = await _productAdmin.GetExistingCategoriesAsync();
            return View(product);
        }

        var created = await _productAdmin.CreateAsync(product, imageFile, galleryFiles, galleryTags);
        TempData["Success"] = $"Product \"{created.Name}\" created.";
        return RedirectToAction(nameof(OpsProducts));
    }

    // GET /Store/OpsProductEdit/{id}
    public async Task<IActionResult> OpsProductEdit(int id)
    {
        var gate = await EnforceOpsAccessAsync();
        if (gate != null) return gate;

        var product = await _productAdmin.GetWithImagesAsync(id);
        if (product == null) return NotFound();

        ViewBag.ExistingCategories = await _productAdmin.GetExistingCategoriesAsync();
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
        Dictionary<string, string>? imageTags,
        Dictionary<string, string>? imageAlts,
        Dictionary<string, string>? imageOrders,
        bool clearImage = false)
    {
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;
        if (id != product.Id) return BadRequest();

        if (!ModelState.IsValid)
        {
            // Mirror the Settings flow's diagnostic dump so we can correlate
            // a parse error in the UI with its server-side (key, value).
            foreach (var kvp in ModelState)
            {
                foreach (var err in kvp.Value.Errors)
                {
                    var attempted = kvp.Value.AttemptedValue ?? "(null)";
                    Console.WriteLine(
                        $"[ModelState] {kvp.Key}: '{attempted}' - {err.ErrorMessage}");
                }
            }

            ViewBag.ExistingCategories = await _productAdmin.GetExistingCategoriesAsync();
            // Reload image data from DB so the view renders existing images correctly.
            var dbSnap = await _context.StoreProducts.Include(p => p.Images).AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (dbSnap != null) { product.ImagePath = dbSnap.ImagePath; product.Images = dbSnap.Images; }
            return View(product);
        }

        var ok = await _productAdmin.UpdateAsync(
            id, product, imageFile, galleryFiles, galleryTags,
            imageTags, imageAlts, imageOrders, clearImage);
        if (!ok) return NotFound();

        TempData["Success"] = $"Product \"{product.Name}\" updated.";
        return RedirectToAction(nameof(OpsProductEdit), new { id });
    }

    // POST /Store/OpsProductImageDelete
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductImageDelete(int imageId)
    {
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;

        var productId = await _productAdmin.DeleteImageAsync(imageId);
        if (productId == null) return NotFound();
        TempData["Success"] = "Image removed.";
        return RedirectToAction(nameof(OpsProductEdit), new { id = productId.Value });
    }

    // POST /Store/OpsProductDelete/{id}
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductDelete(int id)
    {
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;

        var (found, deactivated, name) = await _productAdmin.DeleteOrDeactivateAsync(id);
        if (!found) return NotFound();

        TempData["Success"] = deactivated
            ? $"Product \"{name}\" deactivated (it has existing orders)."
            : $"Product \"{name}\" deleted.";
        return RedirectToAction(nameof(OpsProducts));
    }

    // POST /Store/OpsProductDuplicate/{id}
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductDuplicate(int id)
    {
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;

        var copy = await _productAdmin.DuplicateAsync(id);
        if (copy == null) return NotFound();

        TempData["Success"] = $"Duplicated as \"{copy.Name}\". Review and activate when ready.";
        return RedirectToAction(nameof(OpsProductEdit), new { id = copy.Id });
    }

    // POST /Store/OpsProductBulkSetActive
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpsProductBulkSetActive(List<int> ids, bool isActive)
    {
        var gate = await EnforceOpsAccessAsync(returnForbidOnPost: true);
        if (gate != null) return gate;

        if (ids == null || ids.Count == 0)
        {
            TempData["Error"] = "Select at least one product first.";
            return RedirectToAction(nameof(OpsProducts));
        }

        var changed = await _productAdmin.BulkSetActiveAsync(ids, isActive);
        var verb = isActive ? "activated" : "deactivated";
        TempData["Success"] = changed == 0
            ? $"No changes — selected product(s) were already {verb}."
            : $"{changed} product(s) {verb}.";
        return RedirectToAction(nameof(OpsProducts));
    }

    /// <summary>
    /// Enforces the OpsHub auth gate. Returns null when the current portal user
    /// passes — otherwise returns the redirect / Forbid result the caller should
    /// short-circuit with.
    /// </summary>
    private async Task<IActionResult?> EnforceOpsAccessAsync(
        string? forbiddenMessage = null, bool returnForbidOnPost = false)
    {
        var user = await GetCurrentPortalUserAsync();
        if (user == null) return RedirectToAction("Login", "Account");
        if (!await CanAccessOpsHubAsync(user.Id))
        {
            if (returnForbidOnPost) return Forbid();
            if (forbiddenMessage != null) TempData["Error"] = forbiddenMessage;
            return RedirectToAction(nameof(OperationsHub));
        }
        return null;
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
