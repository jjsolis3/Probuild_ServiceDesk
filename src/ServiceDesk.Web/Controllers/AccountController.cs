using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

public class AccountController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public AccountController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    // GET: /Account/Login
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectAfterLogin();

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    // POST: /Account/Login
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = await _context.PortalUsers
            .Include(u => u.Role)
            .Include(u => u.Employee)
            .FirstOrDefaultAsync(u => u.Email == model.Email && u.IsActive);

        if (user == null || !PasswordService.VerifyPassword(model.Password, user.PasswordHash))
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        // Record last login
        user.LastLogin = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Build claims
        var roleName = user.Role?.Name ?? "End User";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.FullName),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Role,           roleName),
            new("EmployeeId",              user.EmployeeId?.ToString() ?? string.Empty),
            new("UserId",                  user.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authProps = new AuthenticationProperties
        {
            IsPersistent = model.RememberMe,
            ExpiresUtc = model.RememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(8)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);

        // Redirect based on role
        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        if (roleName == "End User")
            return RedirectToAction("Index", "Portal");

        return RedirectToAction("Index", "Home");
    }

    // POST: /Account/Logout
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    // GET: /Account/AccessDenied
    public IActionResult AccessDenied()
    {
        // End Users trying to access IT area → send to portal
        if (User.IsInRole("End User"))
            return RedirectToAction("Index", "Portal");

        return View();
    }

    private IActionResult RedirectAfterLogin()
    {
        if (User.IsInRole("End User"))
            return RedirectToAction("Index", "Portal");
        return RedirectToAction("Index", "Home");
    }
}
