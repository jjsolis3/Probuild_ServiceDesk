using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

public class AccountController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailService;
    private readonly IConfiguration _configuration;

    public AccountController(ServiceDeskDbContext context, EmailNotificationService emailService, IConfiguration configuration)
    {
        _context = context;
        _emailService = emailService;
        _configuration = configuration;
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
        // Normalize role name: "Administrator" is an alias for the canonical "Admin" role.
        // All [Authorize] attributes use "Admin" as the exact string, so we map here once
        // rather than updating every controller and view across the codebase.
        var roleName = user.Role?.Name ?? "End User";
        var claimRole = roleName.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : roleName;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.FullName),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Role,           claimRole),
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

    // GET: /Account/ForgotPassword
    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    // POST: /Account/ForgotPassword
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Always show success to prevent user enumeration
        var user = await _context.PortalUsers
            .FirstOrDefaultAsync(u => u.Email == model.Email && u.IsActive);

        if (user != null)
        {
            // Generate a secure token (URL-safe base64)
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            user.PasswordResetToken = token;
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await _context.SaveChangesAsync();

            var baseUrl = _configuration["App:BaseUrl"]?.TrimEnd('/') ?? string.Empty;
            var resetUrl = $"{baseUrl}/Account/ResetPassword?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(user.Email)}";

            // Fire and forget — don't expose email errors to the user
            _ = Task.Run(() => _emailService.SendPasswordResetEmail(user.Email, user.FullName, resetUrl));
        }

        TempData["Success"] = "If that email is registered, a reset link has been sent. Check your inbox.";
        return RedirectToAction(nameof(Login));
    }

    // GET: /Account/ResetPassword
    [HttpGet]
    public async Task<IActionResult> ResetPassword(string? token, string? email)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email))
            return RedirectToAction(nameof(Login));

        var user = await _context.PortalUsers
            .FirstOrDefaultAsync(u => u.Email == email
                && u.PasswordResetToken == token
                && u.PasswordResetTokenExpiry > DateTime.UtcNow);

        if (user == null)
        {
            TempData["Error"] = "This password reset link is invalid or has expired.";
            return RedirectToAction(nameof(ForgotPassword));
        }

        return View(new ResetPasswordViewModel { Token = token, Email = email });
    }

    // POST: /Account/ResetPassword
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _context.PortalUsers
            .FirstOrDefaultAsync(u => u.Email == model.Email
                && u.PasswordResetToken == model.Token
                && u.PasswordResetTokenExpiry > DateTime.UtcNow);

        if (user == null)
        {
            TempData["Error"] = "This password reset link is invalid or has expired.";
            return RedirectToAction(nameof(ForgotPassword));
        }

        user.PasswordHash = PasswordService.HashPassword(model.Password);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Your password has been reset. You can now sign in with your new password.";
        return RedirectToAction(nameof(Login));
    }
}
