using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using ServiceDesk.Core.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin")]
public class SettingsController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly GmailApiService _gmailApiService;
    private readonly EmailNotificationService _emailService;
    private readonly IMemoryCache _cache;
    private readonly IWebHostEnvironment _env;
    private readonly GoogleWorkspaceService _googleWorkspace;

    public SettingsController(ServiceDeskDbContext context, GmailApiService gmailApiService,
        EmailNotificationService emailService, IMemoryCache cache, IWebHostEnvironment env,
        GoogleWorkspaceService googleWorkspace)
    {
        _context          = context;
        _gmailApiService  = gmailApiService;
        _emailService     = emailService;
        _cache            = cache;
        _env              = env;
        _googleWorkspace  = googleWorkspace;
    }

    // GET: Settings - Landing page with all settings sections
    public IActionResult Index()
    {
        return View();
    }

    // ==================== ACCOUNT SETTINGS ====================

    // GET: Settings/Account
    public async Task<IActionResult> Account()
    {
        // Only show General/Account-level settings here.
        // AI Triage → Settings/Ai, Notifications → Settings/Notifications, Branding → Settings/Branding
        var settings = await _context.AppSettings
            .Where(s => s.Category != "Branding"
                     && s.Category != "AI Triage"
                     && s.Category != "Notifications")
            .ToListAsync();
        var employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        ViewBag.Employees = employees;
        return View(settings);
    }

    // POST: Settings/Account
    // Form sends each setting as name="[SettingKey]" so we read directly from IFormCollection.
    // Boolean toggles use a hidden name="[Key]" value="false" + checkbox value="true" pattern.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Account(IFormCollection form)
    {
        var settings = await _context.AppSettings.ToListAsync();
        foreach (var setting in settings)
        {
            if (form.ContainsKey(setting.Key))
            {
                // For booleans the form may send ["false","true"] when checked —
                // take "true" if present, otherwise "false".
                var values = form[setting.Key];
                setting.Value = values.Contains("true") ? "true" : values.FirstOrDefault() ?? setting.Value;
            }
        }
        await _context.SaveChangesAsync();
        TempData["Success"] = "Account settings saved successfully.";
        return RedirectToAction(nameof(Account));
    }

    // ==================== BRANDING ====================

    // GET: Settings/Branding
    public async Task<IActionResult> Branding()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Branding")
            .ToListAsync();
        return View(settings);
    }

    // POST: Settings/Branding
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Branding(IFormCollection form, IFormFile? logoFile)
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Branding")
            .ToListAsync();

        // Apply all submitted text/toggle fields first
        foreach (var setting in settings)
        {
            if (form.ContainsKey(setting.Key))
                setting.Value = form[setting.Key].FirstOrDefault() ?? setting.Value;
        }

        // If a logo file was provided, validate, save it, and override CompanyLogoUrl
        if (logoFile != null && logoFile.Length > 0)
        {
            var allowedExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };
            var ext = Path.GetExtension(logoFile.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(ext))
            {
                TempData["Error"] = "Invalid logo file type. Allowed: PNG, JPG, GIF, SVG, WEBP.";
                return RedirectToAction(nameof(Branding));
            }
            if (logoFile.Length > 2 * 1024 * 1024)
            {
                TempData["Error"] = "Logo file is too large. Maximum size is 2 MB.";
                return RedirectToAction(nameof(Branding));
            }

            // Delete any previously uploaded logo file from disk
            var logoSetting = settings.FirstOrDefault(s => s.Key == "CompanyLogoUrl");
            if (logoSetting?.Value?.StartsWith("/uploads/branding/") == true)
            {
                var oldPath = Path.Combine(_env.WebRootPath,
                    logoSetting.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
            }

            // Save the uploaded file
            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "branding");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"logo_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsDir, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
                await logoFile.CopyToAsync(stream);

            // Override CompanyLogoUrl with the uploaded path
            if (logoSetting != null)
                logoSetting.Value = $"/uploads/branding/{fileName}";
        }

        await _context.SaveChangesAsync();
        _cache.Remove("ss_branding_v1");
        TempData["Success"] = "Branding settings saved.";
        return RedirectToAction(nameof(Branding));
    }

    // POST: Settings/RemoveLogo
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLogo()
    {
        var logoSetting = await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyLogoUrl");

        if (logoSetting != null)
        {
            // Delete the file from disk if it was an uploaded asset
            if (logoSetting.Value?.StartsWith("/uploads/branding/") == true)
            {
                var filePath = Path.Combine(_env.WebRootPath,
                    logoSetting.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }

            logoSetting.Value = string.Empty;
            await _context.SaveChangesAsync();
            _cache.Remove("ss_branding_v1");
        }

        TempData["Success"] = "Logo removed.";
        return RedirectToAction(nameof(Branding));
    }

    // ==================== EMAIL TEMPLATES ====================

    // GET: Settings/EmailTemplates
    public async Task<IActionResult> EmailTemplates()
    {
        var templates = await _context.EmailTemplates.OrderBy(t => t.Id).ToListAsync();
        return View(templates);
    }

    // GET: Settings/EditEmailTemplate/5
    public async Task<IActionResult> EditEmailTemplate(int id)
    {
        var template = await _context.EmailTemplates.FindAsync(id);
        if (template == null) return NotFound();

        var brandColor = await _context.AppSettings
            .Where(s => s.Key == "BrandColor")
            .Select(s => s.Value)
            .FirstOrDefaultAsync() ?? "#4f46e5";
        ViewBag.BrandColor = string.IsNullOrWhiteSpace(brandColor) ? "#4f46e5" : brandColor;

        return View(template);
    }

    // POST: Settings/EditEmailTemplate/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEmailTemplate(int id, string subjectTemplate, string bodyTemplate, bool isActive)
    {
        var template = await _context.EmailTemplates.FindAsync(id);
        if (template == null) return NotFound();

        template.SubjectTemplate = string.IsNullOrWhiteSpace(subjectTemplate) ? null : subjectTemplate.Trim();
        template.BodyTemplate    = string.IsNullOrWhiteSpace(bodyTemplate)    ? null : bodyTemplate.Trim();
        template.IsActive        = isActive;
        template.UpdatedDate     = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Template '{template.Name}' saved.";
        return RedirectToAction(nameof(EmailTemplates));
    }

    // POST: Settings/SendTestEmail — sends a rendered test email for the given template
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTestEmail(int templateId, string recipientEmail)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
            return Json(new { success = false, message = "Please enter a recipient email address." });

        var template = await _context.EmailTemplates.FindAsync(templateId);
        if (template == null)
            return Json(new { success = false, message = "Template not found." });

        var (success, message) = await _emailService.SendTestEmailAsync(
            template.Key,
            template.BodyTemplate,
            template.SubjectTemplate,
            recipientEmail.Trim());

        return Json(new { success, message });
    }

    // POST: Settings/ResetEmailTemplate/5 — clears customisation, reverts to system default
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetEmailTemplate(int id)
    {
        var template = await _context.EmailTemplates.FindAsync(id);
        if (template == null) return NotFound();

        template.BodyTemplate = null;
        template.UpdatedDate  = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["Success"] = $"Template '{template.Name}' reset to system default.";
        return RedirectToAction(nameof(EmailTemplates));
    }

    // ==================== ROLES & PERMISSIONS ====================

    // GET: Settings/Roles
    public async Task<IActionResult> Roles()
    {
        var roles = await _context.Roles.Include(r => r.Users).ToListAsync();
        return View(roles);
    }

    // GET: Settings/CreateRole
    public IActionResult CreateRole()
    {
        return View(new Role());
    }

    // POST: Settings/CreateRole
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRole(Role role)
    {
        if (ModelState.IsValid)
        {
            _context.Roles.Add(role);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Role created successfully.";
            return RedirectToAction(nameof(Roles));
        }
        return View(role);
    }

    // GET: Settings/EditRole/5
    public async Task<IActionResult> EditRole(int? id)
    {
        if (id == null) return NotFound();
        var role = await _context.Roles.FindAsync(id);
        if (role == null) return NotFound();
        return View(role);
    }

    // POST: Settings/EditRole/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRole(int id, Role role)
    {
        if (id != role.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(role);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Role updated successfully.";
            return RedirectToAction(nameof(Roles));
        }
        return View(role);
    }

    // POST: Settings/DeleteRole/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRole(int id)
    {
        var role = await _context.Roles.FindAsync(id);
        if (role != null && !role.IsSystem)
        {
            _context.Roles.Remove(role);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Role deleted successfully.";
        }
        else if (role?.IsSystem == true)
        {
            TempData["Error"] = "System roles cannot be deleted.";
        }
        return RedirectToAction(nameof(Roles));
    }

    // ==================== BRANCHES ====================

    // GET: Settings/Branches
    public async Task<IActionResult> Branches()
    {
        var branches = await _context.Branches.Include(b => b.SiteManager).ToListAsync();
        return View(branches);
    }

    // GET: Settings/CreateBranch
    public async Task<IActionResult> CreateBranch()
    {
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(new Branch());
    }

    // POST: Settings/CreateBranch
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBranch(Branch branch)
    {
        if (ModelState.IsValid)
        {
            _context.Branches.Add(branch);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Branch created successfully.";
            return RedirectToAction(nameof(Branches));
        }
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(branch);
    }

    // GET: Settings/EditBranch/5
    public async Task<IActionResult> EditBranch(int? id)
    {
        if (id == null) return NotFound();
        var branch = await _context.Branches.FindAsync(id);
        if (branch == null) return NotFound();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(branch);
    }

    // POST: Settings/EditBranch/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditBranch(int id, Branch branch)
    {
        if (id != branch.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(branch);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Branch updated successfully.";
            return RedirectToAction(nameof(Branches));
        }
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(branch);
    }

    // POST: Settings/DeleteBranch/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBranch(int id)
    {
        var branch = await _context.Branches.FindAsync(id);
        if (branch != null)
        {
            _context.Branches.Remove(branch);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Branch deleted.";
        }
        return RedirectToAction(nameof(Branches));
    }

    // ==================== CUSTOM TICKET STATES ====================

    // GET: Settings/TicketStates
    public async Task<IActionResult> TicketStates()
    {
        var states = await _context.TicketStates.OrderBy(s => s.SortOrder).ToListAsync();
        return View(states);
    }

    // GET: Settings/CreateTicketState
    public IActionResult CreateTicketState()
    {
        return View(new TicketState { SortOrder = _context.TicketStates.Any() ? _context.TicketStates.Max(s => s.SortOrder) + 1 : 1 });
    }

    // POST: Settings/CreateTicketState
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTicketState(TicketState state)
    {
        if (ModelState.IsValid)
        {
            _context.TicketStates.Add(state);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Ticket state created.";
            return RedirectToAction(nameof(TicketStates));
        }
        return View(state);
    }

    // GET: Settings/EditTicketState/5
    public async Task<IActionResult> EditTicketState(int? id)
    {
        if (id == null) return NotFound();
        var state = await _context.TicketStates.FindAsync(id);
        if (state == null) return NotFound();
        return View(state);
    }

    // POST: Settings/EditTicketState/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTicketState(int id, TicketState state)
    {
        if (id != state.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(state);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Ticket state updated.";
            return RedirectToAction(nameof(TicketStates));
        }
        return View(state);
    }

    // POST: Settings/DeleteTicketState/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTicketState(int id)
    {
        var state = await _context.TicketStates.FindAsync(id);
        if (state != null && !state.IsSystem)
        {
            _context.TicketStates.Remove(state);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Ticket state deleted.";
        }
        else if (state?.IsSystem == true)
        {
            TempData["Error"] = "System states cannot be deleted.";
        }
        return RedirectToAction(nameof(TicketStates));
    }

    // ==================== RESOLUTION CODES ====================

    // GET: Settings/ResolutionCodes
    public async Task<IActionResult> ResolutionCodes()
    {
        var codes = await _context.ResolutionCodes.OrderBy(c => c.SortOrder).ToListAsync();
        return View(codes);
    }

    // GET: Settings/CreateResolutionCode
    public IActionResult CreateResolutionCode()
    {
        return View(new ResolutionCode { SortOrder = _context.ResolutionCodes.Any() ? _context.ResolutionCodes.Max(c => c.SortOrder) + 1 : 1 });
    }

    // POST: Settings/CreateResolutionCode
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateResolutionCode(ResolutionCode code)
    {
        if (ModelState.IsValid)
        {
            _context.ResolutionCodes.Add(code);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Resolution code created.";
            return RedirectToAction(nameof(ResolutionCodes));
        }
        return View(code);
    }

    // GET: Settings/EditResolutionCode/5
    public async Task<IActionResult> EditResolutionCode(int? id)
    {
        if (id == null) return NotFound();
        var code = await _context.ResolutionCodes.FindAsync(id);
        if (code == null) return NotFound();
        return View(code);
    }

    // POST: Settings/EditResolutionCode/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditResolutionCode(int id, ResolutionCode code)
    {
        if (id != code.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(code);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Resolution code updated.";
            return RedirectToAction(nameof(ResolutionCodes));
        }
        return View(code);
    }

    // POST: Settings/DeleteResolutionCode/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteResolutionCode(int id)
    {
        var code = await _context.ResolutionCodes.FindAsync(id);
        if (code != null)
        {
            _context.ResolutionCodes.Remove(code);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Resolution code deleted.";
        }
        return RedirectToAction(nameof(ResolutionCodes));
    }

    // ==================== USERS & GROUPS ====================

    // GET: Settings/Users
    public async Task<IActionResult> Users()
    {
        var users = await _context.PortalUsers
            .Include(u => u.Role)
            .Include(u => u.Employee)
            .Include(u => u.GroupMemberships)
                .ThenInclude(m => m.UserGroup)
            .ToListAsync();
        return View(users);
    }

    // GET: Settings/CreateUser
    public async Task<IActionResult> CreateUser()
    {
        ViewBag.Roles = await _context.Roles.ToListAsync();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(new PortalUser());
    }

    // POST: Settings/CreateUser
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(PortalUser user, string? initialPassword)
    {
        ModelState.Remove("PasswordHash");
        if (ModelState.IsValid)
        {
            if (!string.IsNullOrWhiteSpace(initialPassword))
                user.PasswordHash = PasswordService.HashPassword(initialPassword);
            user.CreatedDate = DateTime.UtcNow;
            _context.PortalUsers.Add(user);
            await _context.SaveChangesAsync();
            TempData["Success"] = "User created successfully." +
                (string.IsNullOrWhiteSpace(initialPassword)
                    ? " No password was set — use Send Password Reset to let them set their own."
                    : "");
            return RedirectToAction(nameof(Users));
        }
        ViewBag.Roles = await _context.Roles.ToListAsync();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(user);
    }

    // GET: Settings/EditUser/5
    public async Task<IActionResult> EditUser(int? id)
    {
        if (id == null) return NotFound();
        var user = await _context.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        ViewBag.Roles = await _context.Roles.ToListAsync();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(user);
    }

    // POST: Settings/EditUser/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(int id, PortalUser updated)
    {
        if (id != updated.Id) return NotFound();
        ModelState.Remove("PasswordHash");
        if (ModelState.IsValid)
        {
            var existing = await _context.PortalUsers.FindAsync(id);
            if (existing == null) return NotFound();
            // Only update profile fields — never touch PasswordHash here
            existing.FirstName   = updated.FirstName;
            existing.LastName    = updated.LastName;
            existing.Email       = updated.Email;
            existing.RoleId      = updated.RoleId;
            existing.EmployeeId  = updated.EmployeeId;
            existing.IsActive    = updated.IsActive;
            await _context.SaveChangesAsync();
            TempData["Success"] = "User updated.";
            return RedirectToAction(nameof(Users));
        }
        ViewBag.Roles = await _context.Roles.ToListAsync();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(updated);
    }

    // POST: Settings/AdminResetPassword
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminResetPassword(int id, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            TempData["Error"] = "Password must be at least 8 characters.";
            return RedirectToAction(nameof(EditUser), new { id });
        }
        var user = await _context.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        user.PasswordHash = PasswordService.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Password for {user.FullName} has been reset.";
        return RedirectToAction(nameof(EditUser), new { id });
    }

    // POST: Settings/AdminSendPasswordReset
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSendPasswordReset(int id)
    {
        var user = await _context.PortalUsers.FindAsync(id);
        if (user == null) return NotFound();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        user.PasswordResetToken = token;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _context.SaveChangesAsync();
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var resetUrl = $"{baseUrl}/Account/ResetPassword?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(user.Email)}";
        _ = Task.Run(() => _emailService.SendPasswordResetEmail(user.Email, user.FullName, resetUrl));
        TempData["Success"] = $"Password reset email sent to {user.Email}.";
        return RedirectToAction(nameof(EditUser), new { id });
    }

    // POST: Settings/DeleteUser/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _context.PortalUsers.FindAsync(id);
        if (user != null)
        {
            _context.PortalUsers.Remove(user);
            await _context.SaveChangesAsync();
            TempData["Success"] = "User deleted.";
        }
        return RedirectToAction(nameof(Users));
    }

    // GET: Settings/Groups
    public async Task<IActionResult> Groups()
    {
        var groups = await _context.UserGroups
            .Include(g => g.Members)
                .ThenInclude(m => m.PortalUser)
            .ToListAsync();
        return View(groups);
    }

    // GET: Settings/CreateGroup
    public IActionResult CreateGroup()
    {
        return View(new UserGroup());
    }

    // POST: Settings/CreateGroup
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(UserGroup group)
    {
        if (ModelState.IsValid)
        {
            _context.UserGroups.Add(group);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Group created.";
            return RedirectToAction(nameof(Groups));
        }
        return View(group);
    }

    // GET: Settings/EditGroup/5
    public async Task<IActionResult> EditGroup(int? id)
    {
        if (id == null) return NotFound();
        var group = await _context.UserGroups
            .Include(g => g.Members)
                .ThenInclude(m => m.PortalUser)
            .FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound();
        ViewBag.AvailableUsers = await _context.PortalUsers
            .Where(u => u.IsActive && !u.GroupMemberships.Any(m => m.UserGroupId == id))
            .ToListAsync();
        return View(group);
    }

    // POST: Settings/EditGroup/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditGroup(int id, UserGroup group)
    {
        if (id != group.Id) return NotFound();
        if (ModelState.IsValid)
        {
            var existing = await _context.UserGroups.FindAsync(id);
            if (existing == null) return NotFound();
            existing.Name = group.Name;
            existing.Description = group.Description;
            existing.IsActive = group.IsActive;
            await _context.SaveChangesAsync();
            TempData["Success"] = "Group updated.";
            return RedirectToAction(nameof(Groups));
        }
        return View(group);
    }

    // POST: Settings/AddGroupMember
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddGroupMember(int groupId, int userId)
    {
        var exists = await _context.UserGroupMembers
            .AnyAsync(m => m.UserGroupId == groupId && m.PortalUserId == userId);
        if (!exists)
        {
            _context.UserGroupMembers.Add(new UserGroupMember
            {
                UserGroupId = groupId,
                PortalUserId = userId
            });
            await _context.SaveChangesAsync();
            TempData["Success"] = "Member added to group.";
        }
        return RedirectToAction(nameof(EditGroup), new { id = groupId });
    }

    // POST: Settings/RemoveGroupMember
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveGroupMember(int groupId, int memberId)
    {
        var member = await _context.UserGroupMembers.FindAsync(memberId);
        if (member != null)
        {
            _context.UserGroupMembers.Remove(member);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Member removed from group.";
        }
        return RedirectToAction(nameof(EditGroup), new { id = groupId });
    }

    // POST: Settings/DeleteGroup/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGroup(int id)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group != null)
        {
            _context.UserGroups.Remove(group);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Group deleted.";
        }
        return RedirectToAction(nameof(Groups));
    }

    // ==================== EMAIL INTEGRATION ====================

    // GET: Settings/EmailIntegration
    public async Task<IActionResult> EmailIntegration()
    {
        var configs = await _context.EmailConfigurations
            .Include(e => e.DefaultAssignee)
            .ToListAsync();
        return View(configs);
    }

    // GET: Settings/CreateEmailConfig
    public async Task<IActionResult> CreateEmailConfig()
    {
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(new EmailConfiguration());
    }

    // POST: Settings/CreateEmailConfig
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEmailConfig(EmailConfiguration config)
    {
        if (ModelState.IsValid)
        {
            _context.EmailConfigurations.Add(config);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Email configuration created. You can now authorize Gmail access.";
            return RedirectToAction(nameof(EmailIntegration));
        }
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(config);
    }

    // GET: Settings/EditEmailConfig/5
    public async Task<IActionResult> EditEmailConfig(int? id)
    {
        if (id == null) return NotFound();
        var config = await _context.EmailConfigurations.FindAsync(id);
        if (config == null) return NotFound();
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(config);
    }

    // POST: Settings/EditEmailConfig/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEmailConfig(int id, EmailConfiguration config)
    {
        if (id != config.Id) return NotFound();
        if (ModelState.IsValid)
        {
            var existing = await _context.EmailConfigurations.FindAsync(id);
            if (existing == null) return NotFound();

            // Update editable fields only - preserve OAuth tokens and authorization state
            existing.Name = config.Name;
            existing.EmailAddress = config.EmailAddress;
            existing.GmailClientId = config.GmailClientId;
            existing.GmailClientSecret = config.GmailClientSecret;
            existing.SmtpServer = config.SmtpServer;
            existing.SmtpPort = config.SmtpPort;
            existing.UseSsl = config.UseSsl;
            existing.SmtpUsername = config.SmtpUsername;
            if (!string.IsNullOrWhiteSpace(config.SmtpPassword))
                existing.SmtpPassword = config.SmtpPassword;
            existing.PollIntervalMinutes = config.PollIntervalMinutes;
            existing.CreateTicketsFromEmails = config.CreateTicketsFromEmails;
            existing.AutoReplyOnNewTicket = config.AutoReplyOnNewTicket;
            existing.DefaultAssigneeId = config.DefaultAssigneeId;
            existing.IsActive = config.IsActive;

            // Preserve: GmailRefreshToken, GmailAccessToken, GmailTokenExpiry, GmailHistoryId, IsAuthorized

            await _context.SaveChangesAsync();
            TempData["Success"] = "Email configuration updated.";
            return RedirectToAction(nameof(EmailIntegration));
        }
        ViewBag.Employees = await _context.Employees.Where(e => e.IsActive).ToListAsync();
        return View(config);
    }

    // POST: Settings/DeleteEmailConfig/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEmailConfig(int id)
    {
        var config = await _context.EmailConfigurations.FindAsync(id);
        if (config != null)
        {
            _context.EmailConfigurations.Remove(config);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Email configuration deleted.";
        }
        return RedirectToAction(nameof(EmailIntegration));
    }

    // GET: Settings/GmailAuthorize/5
    public async Task<IActionResult> GmailAuthorize(int id)
    {
        var config = await _context.EmailConfigurations.FindAsync(id);
        if (config == null) return NotFound();

        if (string.IsNullOrEmpty(config.GmailClientId) || string.IsNullOrEmpty(config.GmailClientSecret))
        {
            TempData["Error"] = "Please set the Gmail Client ID and Client Secret before authorizing.";
            return RedirectToAction(nameof(EmailIntegration));
        }

        var redirectUri = $"{Request.Scheme}://{Request.Host}/Settings/GmailCallback";
        var scopes = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.modify");

        var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={Uri.EscapeDataString(config.GmailClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={scopes}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={config.Id}";

        return Redirect(authUrl);
    }

    // GET: Settings/GmailCallback
    public async Task<IActionResult> GmailCallback(string code, string state)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            TempData["Error"] = "Gmail authorization failed: missing authorization code or state.";
            return RedirectToAction(nameof(EmailIntegration));
        }

        if (!int.TryParse(state, out var configId))
        {
            TempData["Error"] = "Gmail authorization failed: invalid state parameter.";
            return RedirectToAction(nameof(EmailIntegration));
        }

        var config = await _context.EmailConfigurations.FindAsync(configId);
        if (config == null)
        {
            TempData["Error"] = "Gmail authorization failed: email configuration not found.";
            return RedirectToAction(nameof(EmailIntegration));
        }

        var redirectUri = $"{Request.Scheme}://{Request.Host}/Settings/GmailCallback";
        var (success, error) = await _gmailApiService.ExchangeAuthorizationCode(config, code, redirectUri);

        if (success)
        {
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Gmail authorization successful for {config.EmailAddress}.";
        }
        else
        {
            TempData["Error"] = $"Gmail authorization failed: {error}";
        }

        return RedirectToAction(nameof(EmailIntegration));
    }

    // POST: Settings/RevokeGmailAuth/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeGmailAuth(int id)
    {
        var config = await _context.EmailConfigurations.FindAsync(id);
        if (config == null) return NotFound();

        config.GmailRefreshToken = null;
        config.GmailAccessToken = null;
        config.GmailTokenExpiry = null;
        config.GmailHistoryId = null;
        config.IsAuthorized = false;

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Gmail authorization revoked for {config.EmailAddress}.";
        return RedirectToAction(nameof(EmailIntegration));
    }

    // POST: Settings/TestEmailConnection/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmailConnection(int id)
    {
        var config = await _context.EmailConfigurations.FindAsync(id);
        if (config == null) return NotFound();

        if (!config.IsAuthorized || string.IsNullOrEmpty(config.GmailRefreshToken))
        {
            TempData["Error"] = "Please authorize Gmail access before testing the connection.";
        }
        else
        {
            TempData["Success"] = "Connection test initiated. The configuration is authorized and ready to poll.";
        }
        return RedirectToAction(nameof(EmailIntegration));
    }

    // ==================== ASSIGNMENT RULES ====================

    // GET: Settings/AssignmentRules
    public async Task<IActionResult> AssignmentRules()
    {
        var rules = await _context.AssignmentRules
            .Include(r => r.Branch)
            .Include(r => r.Assignee)
            .Include(r => r.SubCategory)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync();

        try
        {
            ViewBag.CategoriesById = await _context.TicketCategories
                .ToDictionaryAsync(c => c.Id, c => c.Name);
        }
        catch
        {
            ViewBag.CategoriesById = Enum.GetValues<TicketCategory>()
                .ToDictionary(c => (int)c, c => c.GetDisplayName());
        }

        return View(rules);
    }

    // GET: Settings/CreateAssignmentRule
    public async Task<IActionResult> CreateAssignmentRule()
    {
        await LoadAssignmentRuleViewBag();
        return View(new AssignmentRule { SortOrder = 100, IsActive = true });
    }

    // POST: Settings/CreateAssignmentRule
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAssignmentRule(AssignmentRule rule)
    {
        if (ModelState.IsValid)
        {
            rule.CreatedDate = DateTime.UtcNow;
            _context.AssignmentRules.Add(rule);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Assignment rule '{rule.Name}' created.";
            return RedirectToAction(nameof(AssignmentRules));
        }
        await LoadAssignmentRuleViewBag();
        return View(rule);
    }

    // GET: Settings/EditAssignmentRule/5
    public async Task<IActionResult> EditAssignmentRule(int id)
    {
        var rule = await _context.AssignmentRules.FindAsync(id);
        if (rule == null) return NotFound();
        await LoadAssignmentRuleViewBag();
        return View(rule);
    }

    // POST: Settings/EditAssignmentRule/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditAssignmentRule(int id, AssignmentRule rule)
    {
        if (id != rule.Id) return BadRequest();

        if (ModelState.IsValid)
        {
            _context.Update(rule);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Assignment rule '{rule.Name}' updated.";
            return RedirectToAction(nameof(AssignmentRules));
        }
        await LoadAssignmentRuleViewBag();
        return View(rule);
    }

    // POST: Settings/DeleteAssignmentRule/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAssignmentRule(int id)
    {
        var rule = await _context.AssignmentRules.FindAsync(id);
        if (rule == null) return NotFound();

        _context.AssignmentRules.Remove(rule);
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Assignment rule '{rule.Name}' deleted.";
        return RedirectToAction(nameof(AssignmentRules));
    }

    private async Task LoadAssignmentRuleViewBag()
    {
        ViewBag.Branches = await _context.Branches
            .Where(b => b.IsActive)
            .OrderBy(b => b.Name)
            .ToListAsync();

        // Only show employees who can actually be assigned tickets:
        // those with an active portal account whose role has ManageTickets permission.
        var staffRoleIds = await _context.Roles
            .Where(r => r.Permissions.Contains("ManageTickets"))
            .Select(r => r.Id)
            .ToListAsync();
        var staffEmpIds = await _context.PortalUsers
            .Where(u => u.IsActive && u.EmployeeId != null
                     && u.RoleId != null && staffRoleIds.Contains(u.RoleId.Value))
            .Select(u => u.EmployeeId!.Value)
            .Distinct()
            .ToListAsync();
        ViewBag.Employees = await _context.Employees
            .Where(e => e.IsActive && staffEmpIds.Contains(e.Id))
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        try
        {
            var dbCats = await _context.TicketCategories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new { Value = c.Id, Text = c.Name })
                .ToListAsync();
            ViewBag.Categories = dbCats;
            ViewBag.CategoriesById = dbCats.ToDictionary(c => c.Value, c => c.Text);
        }
        catch
        {
            var fallback = Enum.GetValues<TicketCategory>()
                .Select(c => new { Value = (int)c, Text = c.GetDisplayName() }).ToList();
            ViewBag.Categories = fallback;
            ViewBag.CategoriesById = fallback.ToDictionary(c => c.Value, c => c.Text);
        }

        ViewBag.SubCategories = await _context.TicketSubCategories
            .Where(s => s.IsActive)
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Category })
            .ToListAsync();
    }

    // ── Canned Responses ─────────────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CannedResponses()
    {
        var responses = await _context.CannedResponses
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Title)
            .ToListAsync();
        return View(responses);
    }

    [Authorize(Roles = "Admin")]
    public IActionResult CreateCannedResponse() => View(new CannedResponse());

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateCannedResponse(CannedResponse response)
    {
        if (ModelState.IsValid)
        {
            _context.CannedResponses.Add(response);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Canned response created.";
            return RedirectToAction(nameof(CannedResponses));
        }
        return View(response);
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditCannedResponse(int id)
    {
        var response = await _context.CannedResponses.FindAsync(id);
        if (response == null) return NotFound();
        return View(response);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditCannedResponse(int id, CannedResponse response)
    {
        if (id != response.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.Update(response);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Canned response updated.";
            return RedirectToAction(nameof(CannedResponses));
        }
        return View(response);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteCannedResponse(int id)
    {
        var response = await _context.CannedResponses.FindAsync(id);
        if (response != null)
        {
            _context.CannedResponses.Remove(response);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Canned response deleted.";
        }
        return RedirectToAction(nameof(CannedResponses));
    }

    // ==================== CATEGORIES & SUB-CATEGORIES ====================

    private async Task<List<SelectListItem>> LoadCategorySelectItemsAsync()
    {
        try
        {
            return await _context.TicketCategories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
                .ToListAsync();
        }
        catch
        {
            return Enum.GetValues<TicketCategory>()
                .Select(c => new SelectListItem(c.GetDisplayName(), ((int)c).ToString()))
                .ToList();
        }
    }

    public async Task<IActionResult> Categories()
    {
        var categories = await _context.TicketCategories
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync();
        var subCategories = await _context.TicketSubCategories
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync();
        ViewBag.TopLevelCategories = categories;
        return View(subCategories);
    }

    public async Task<IActionResult> CreateSubCategory(int? category)
    {
        ViewBag.Categories = await LoadCategorySelectItemsAsync();
        var model = new TicketSubCategory { Category = category ?? 6 }; // default to "Other"
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSubCategory(TicketSubCategory model)
    {
        if (ModelState.IsValid)
        {
            _context.TicketSubCategories.Add(model);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Sub-category created.";
            return RedirectToAction(nameof(Categories));
        }
        ViewBag.Categories = await LoadCategorySelectItemsAsync();
        return View(model);
    }

    public async Task<IActionResult> EditSubCategory(int id)
    {
        var subCat = await _context.TicketSubCategories.FindAsync(id);
        if (subCat == null) return NotFound();
        ViewBag.Categories = await LoadCategorySelectItemsAsync();
        return View(subCat);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSubCategory(int id, TicketSubCategory model)
    {
        if (id != model.Id) return NotFound();
        if (ModelState.IsValid)
        {
            _context.TicketSubCategories.Update(model);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Sub-category updated.";
            return RedirectToAction(nameof(Categories));
        }
        ViewBag.Categories = await LoadCategorySelectItemsAsync();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSubCategory(int id)
    {
        var subCat = await _context.TicketSubCategories.FindAsync(id);
        if (subCat != null)
        {
            _context.TicketSubCategories.Remove(subCat);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Sub-category deleted.";
        }
        return RedirectToAction(nameof(Categories));
    }

    // ==================== TOP-LEVEL CATEGORY MANAGEMENT ====================

    public IActionResult CreateCategory()
    {
        return View(new ServiceDesk.Core.Models.TicketCategoryEntry());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(ServiceDesk.Core.Models.TicketCategoryEntry model)
    {
        if (ModelState.IsValid)
        {
            // Assign next available ID (max existing + 1, minimum 8 to avoid clashing with system IDs 0-7)
            var maxId = await _context.TicketCategories.MaxAsync(c => (int?)c.Id) ?? -1;
            model.Id = Math.Max(8, maxId + 1);
            model.IsSystem = false;
            _context.TicketCategories.Add(model);
            await _context.SaveChangesAsync();
            _cache.Remove("TicketCategories");
            TempData["Success"] = $"Category \"{model.Name}\" created.";
            return RedirectToAction(nameof(Categories));
        }
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleCategory(int id)
    {
        var cat = await _context.TicketCategories.FindAsync(id);
        if (cat != null && !cat.IsSystem)
        {
            cat.IsActive = !cat.IsActive;
            await _context.SaveChangesAsync();
            _cache.Remove("TicketCategories");
        }
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var cat = await _context.TicketCategories.FindAsync(id);
        if (cat == null) return NotFound();
        if (cat.IsSystem)
        {
            TempData["Error"] = "System categories cannot be deleted.";
            return RedirectToAction(nameof(Categories));
        }
        _context.TicketCategories.Remove(cat);
        await _context.SaveChangesAsync();
        _cache.Remove("TicketCategories");
        TempData["Success"] = $"Category \"{cat.Name}\" deleted.";
        return RedirectToAction(nameof(Categories));
    }

    // ==================== CATEGORY KEYWORDS ====================

    public async Task<IActionResult> CategoryKeywords()
    {
        var keywords = await _context.CategoryKeywords
            .OrderBy(k => k.Category).ThenBy(k => k.Keyword)
            .ToListAsync();
        try
        {
            ViewBag.CategoriesById = await _context.TicketCategories
                .ToDictionaryAsync(c => c.Id, c => c.Name);
        }
        catch
        {
            ViewBag.CategoriesById = Enum.GetValues<TicketCategory>()
                .ToDictionary(c => (int)c, c => c.GetDisplayName());
        }
        ViewBag.CategorySelectItems = await LoadCategorySelectItemsAsync();
        return View(keywords);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategoryKeyword(int category, string keyword)
    {
        keyword = keyword?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2)
        {
            TempData["Error"] = "Keyword must be at least 2 characters.";
            return RedirectToAction(nameof(CategoryKeywords));
        }

        var exists = await _context.CategoryKeywords
            .AnyAsync(k => k.Category == category && k.Keyword == keyword);
        if (exists)
        {
            TempData["Error"] = $"The keyword \"{keyword}\" already exists for that category.";
            return RedirectToAction(nameof(CategoryKeywords));
        }

        _context.CategoryKeywords.Add(new CategoryKeyword
        {
            Category = category,
            Keyword = keyword,
            IsActive = true,
            CreatedDate = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Keyword \"{keyword}\" added.";
        return RedirectToAction(nameof(CategoryKeywords));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategoryKeyword(int id)
    {
        var kw = await _context.CategoryKeywords.FindAsync(id);
        if (kw != null)
        {
            _context.CategoryKeywords.Remove(kw);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Keyword deleted.";
        }
        return RedirectToAction(nameof(CategoryKeywords));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleCategoryKeyword(int id)
    {
        var kw = await _context.CategoryKeywords.FindAsync(id);
        if (kw != null)
        {
            kw.IsActive = !kw.IsActive;
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(CategoryKeywords));
    }

    // ==================== AI TRIAGE ====================

    // GET: Settings/Ai
    public async Task<IActionResult> Ai()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "AI Triage")
            .OrderBy(s => s.Key)
            .ToListAsync();

        // Status panel data
        ViewBag.LastRunLog = await _context.AiRunLogs
            .OrderByDescending(l => l.RunDate)
            .FirstOrDefaultAsync();

        ViewBag.TotalTrainingTickets = await _context.Tickets
            .CountAsync(t => t.Status == ServiceDesk.Core.Enums.TicketStatus.Resolved
                          || t.Status == ServiceDesk.Core.Enums.TicketStatus.Closed);

        return View(settings);
    }

    // POST: Settings/Ai — save AI settings
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ai(IFormCollection form)
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "AI Triage")
            .ToListAsync();

        foreach (var setting in settings)
        {
            if (form.ContainsKey(setting.Key))
            {
                var values = form[setting.Key];
                setting.Value = values.Contains("true") ? "true" : values.FirstOrDefault() ?? setting.Value;
            }
        }
        await _context.SaveChangesAsync();
        TempData["Success"] = "AI Triage settings saved.";
        return RedirectToAction(nameof(Ai));
    }

    // POST: Settings/AiRetrain — force-retrain the ML.NET model immediately
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AiRetrain()
    {
        var aiTriage = HttpContext.RequestServices.GetService<ServiceDesk.Web.Services.AiTriageService>();
        if (aiTriage == null)
        {
            TempData["Error"] = "AI Triage service is not registered.";
            return RedirectToAction(nameof(Ai));
        }

        await aiTriage.TrainNowAsync();

        var lastLog = await _context.AiRunLogs
            .OrderByDescending(l => l.RunDate)
            .FirstOrDefaultAsync();

        TempData["Success"] = lastLog?.Success == true
            ? $"Model retrained successfully on {lastLog.TrainingTicketCount} tickets ({lastLog.DurationMs:F0} ms)."
            : lastLog?.ErrorMessage ?? "Training complete (check logs for details).";

        return RedirectToAction(nameof(Ai));
    }

    // GET: Settings/AiDashboard — AI statistics dashboard
    public async Task<IActionResult> AiDashboard()
    {
        var aiTriage = HttpContext.RequestServices
            .GetService<ServiceDesk.Web.Services.AiTriageService>();

        var vm = new ServiceDesk.Web.Models.AiDashboardViewModel
        {
            ModelIsTrained = aiTriage?.IsModelTrained ?? false,
            TotalTrainingTickets = await _context.Tickets
                .CountAsync(t => t.Status == TicketStatus.Resolved
                              || t.Status == TicketStatus.Closed)
        };

        // Load all recommendations with ticket titles
        var allRecs = await _context.AiRecommendations
            .Include(r => r.Ticket)
            .OrderByDescending(r => r.CreatedDate)
            .ToListAsync();

        var cutoff30 = DateTime.UtcNow.AddDays(-30);

        vm.TotalRecommendations = allRecs.Count;
        vm.ApprovedCount  = allRecs.Count(r => r.Status == "Approved");
        vm.DismissedCount = allRecs.Count(r => r.Status == "Dismissed");
        vm.PendingCount   = allRecs.Count(r => r.Status == "Pending");
        vm.RecsLast30Days = allRecs.Count(r => r.CreatedDate >= cutoff30);

        var reviewed = allRecs.Where(r => r.Status is "Approved" or "Dismissed").ToList();
        vm.ApprovalRate = reviewed.Count > 0
            ? Math.Round((double)vm.ApprovedCount / reviewed.Count * 100, 1)
            : 0;

        if (allRecs.Count > 0)
        {
            vm.AvgCategoryConfidence = Math.Round(
                allRecs.Average(r => (double)r.CategoryConfidence) * 100, 1);
            vm.AvgPriorityConfidence = Math.Round(
                allRecs.Average(r => (double)r.PriorityConfidence) * 100, 1);
        }

        // Confidence bands — use max(cat, pri) as the headline score per rec
        foreach (var r in allRecs)
        {
            var score = Math.Max(r.CategoryConfidence, r.PriorityConfidence);
            if      (score < 0.50f) vm.ConfUnder50++;
            else if (score < 0.65f) vm.Conf50To65++;
            else if (score < 0.80f) vm.Conf65To80++;
            else                    vm.ConfOver80++;
        }

        // Category breakdown
        vm.CategoryStats = allRecs
            .Where(r => r.SuggestedCategory.HasValue)
            .GroupBy(r => r.SuggestedCategory!.Value)
            .Select(g => new ServiceDesk.Web.Models.AiCategoryStat
            {
                CategoryName = Enum.IsDefined(typeof(TicketCategory), g.Key)
                    ? ((TicketCategory)g.Key).ToString()
                    : $"Category {g.Key}",
                Suggested = g.Count(),
                Approved  = g.Count(r => r.Status == "Approved")
            })
            .OrderByDescending(c => c.Suggested)
            .ToList();

        // 30-day daily trend
        var recsIn30 = allRecs.Where(r => r.CreatedDate >= cutoff30).ToList();
        vm.DailyTrend = Enumerable.Range(0, 30)
            .Select(i =>
            {
                var day = DateTime.UtcNow.Date.AddDays(-29 + i);
                return new ServiceDesk.Web.Models.AiDailyTrendPoint
                {
                    DateLabel = day.ToString("MMM d"),
                    Count     = recsIn30.Count(r => r.CreatedDate.Date == day)
                };
            })
            .ToList();

        // Training history (last 10)
        vm.RecentTrainingRuns = await _context.AiRunLogs
            .OrderByDescending(l => l.RunDate)
            .Take(10)
            .ToListAsync();

        // Recent recommendations (last 50)
        vm.RecentRecs = allRecs.Take(50).Select(r => new ServiceDesk.Web.Models.AiRecentRecRow
        {
            RecId      = r.Id,
            TicketId   = r.TicketId,
            TicketTitle = r.Ticket?.Title ?? $"Ticket #{r.TicketId}",
            SuggestedCategory = r.SuggestedCategory.HasValue && Enum.IsDefined(typeof(TicketCategory), r.SuggestedCategory.Value)
                ? ((TicketCategory)r.SuggestedCategory.Value).ToString()
                : "—",
            SuggestedPriority = r.SuggestedPriority.HasValue && Enum.IsDefined(typeof(TicketPriority), r.SuggestedPriority.Value)
                ? ((TicketPriority)r.SuggestedPriority.Value).ToString()
                : "—",
            CategoryConfidencePct = (int)Math.Round(r.CategoryConfidence * 100),
            PriorityConfidencePct = (int)Math.Round(r.PriorityConfidence * 100),
            Status      = r.Status,
            CreatedDate = r.CreatedDate,
            ReviewedBy  = r.ReviewedBy
        }).ToList();

        // ── Accuracy tracking ─────────────────────────────────────────────────
        // For Approved recs whose ticket is now closed/resolved, check if the
        // final ticket category/priority still matches what was suggested.
        var closedApproved = allRecs
            .Where(r => r.Status == "Approved" && r.Ticket != null
                     && (r.Ticket.Status == TicketStatus.Resolved
                      || r.Ticket.Status == TicketStatus.Closed))
            .ToList();

        vm.AccuracyCategoryTotal   = closedApproved.Count(r => r.SuggestedCategory.HasValue);
        vm.AccuracyCategoryCorrect = closedApproved.Count(r =>
            r.SuggestedCategory.HasValue &&
            r.SuggestedCategory.Value == (int)r.Ticket!.Category);

        vm.AccuracyPriorityTotal   = closedApproved.Count(r => r.SuggestedPriority.HasValue);
        vm.AccuracyPriorityCorrect = closedApproved.Count(r =>
            r.SuggestedPriority.HasValue &&
            r.SuggestedPriority.Value == (int)r.Ticket!.Priority);

        // ── Category gap detection ────────────────────────────────────────────
        // Find low-confidence recs and extract the most common terms from their
        // ticket titles to surface potential missing categories.
        var lowConfRecs = allRecs
            .Where(r => Math.Max(r.CategoryConfidence, r.PriorityConfidence) < 0.50f
                     && r.Ticket != null)
            .ToList();

        if (lowConfRecs.Count >= 3)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","is","are","was","were","have","has","had","be","a","an","and","or",
                "in","on","at","to","for","of","with","by","as","not","can","my","our",
                "your","its","this","that","i","we","they","he","she","it","do","did",
                "please","help","issue","problem","request","ticket","need","new","old"
            };

            // Count word frequency across all low-confidence ticket titles
            var wordCounts = new Dictionary<string, List<(int Id, string Title)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in lowConfRecs)
            {
                var title = rec.Ticket!.Title ?? string.Empty;
                var words = System.Text.RegularExpressions.Regex
                    .Split(title.ToLowerInvariant(), @"[^a-z0-9]+")
                    .Where(w => w.Length >= 3 && !stopWords.Contains(w));

                foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!wordCounts.ContainsKey(word))
                        wordCounts[word] = new();
                    wordCounts[word].Add((rec.TicketId, title));
                }
            }

            vm.CategoryGaps = wordCounts
                .Where(kv => kv.Value.Count >= 2) // minimum 2 tickets for a cluster
                .OrderByDescending(kv => kv.Value.Count)
                .Take(8)
                .Select(kv => new ServiceDesk.Web.Models.AiCategoryGapCluster
                {
                    KeyTerm     = kv.Key,
                    TicketCount = kv.Value.Count,
                    Samples     = kv.Value.DistinctBy(t => t.Id).Take(3).ToList()
                })
                .ToList();
        }

        // ── Tickets awaiting triage ───────────────────────────────────────────
        var openTicketIds = await _context.Tickets
            .Where(t => t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .Select(t => t.Id)
            .ToListAsync();

        var ticketsWithPendingRec = await _context.AiRecommendations
            .Where(r => r.Status == "Pending" && openTicketIds.Contains(r.TicketId))
            .Select(r => r.TicketId)
            .Distinct()
            .ToListAsync();

        vm.TicketsAwaitingTriage = openTicketIds.Count - ticketsWithPendingRec.Count;

        return View(vm);
    }

    // POST: Settings/AiBatchRetriage — queue AI triage for all open tickets without a pending rec
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AiBatchRetriage()
    {
        var aiTriage = HttpContext.RequestServices.GetService<ServiceDesk.Web.Services.AiTriageService>();
        if (aiTriage == null)
        {
            TempData["Error"] = "AI Triage service is not registered.";
            return RedirectToAction(nameof(AiDashboard));
        }

        // Find open tickets without a current Pending recommendation
        var openTickets = await _context.Tickets
            .Where(t => t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .Select(t => new { t.Id, t.Title, t.Description, t.BranchId })
            .ToListAsync();

        var pendingTicketIds = await _context.AiRecommendations
            .Where(r => r.Status == "Pending")
            .Select(r => r.TicketId)
            .Distinct()
            .ToListAsync();

        var toProcess = openTickets
            .Where(t => !pendingTicketIds.Contains(t.Id))
            .ToList();

        if (toProcess.Count == 0)
        {
            TempData["Success"] = "All open tickets already have a pending AI recommendation.";
            return RedirectToAction(nameof(AiDashboard));
        }

        // Fire-and-forget — triage runs in background via IServiceScopeFactory
        _ = Task.Run(async () =>
        {
            foreach (var t in toProcess)
                await aiTriage.TriageAndSaveAsync(t.Id, t.Title, t.Description ?? string.Empty, t.BranchId);
        });

        TempData["Success"] = $"Batch triage started for {toProcess.Count} ticket(s). Results will appear shortly.";
        return RedirectToAction(nameof(AiDashboard));
    }

    // ==================== PORTAL BRANDING ====================

    // GET: Settings/PortalBranding
    public async Task<IActionResult> PortalBranding()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Portal Branding")
            .ToListAsync();
        return View(settings);
    }

    // POST: Settings/PortalBranding
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PortalBranding(IFormCollection form)
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Portal Branding")
            .ToListAsync();
        foreach (var setting in settings)
        {
            if (form.ContainsKey(setting.Key))
                setting.Value = form[setting.Key].FirstOrDefault() ?? setting.Value;
        }
        await _context.SaveChangesAsync();
        _cache.Remove("ss_branding_v1");
        TempData["Success"] = "Portal branding settings saved.";
        return RedirectToAction(nameof(PortalBranding));
    }

    // ==================== SLA POLICY ====================

    // GET: Settings/Sla
    public async Task<IActionResult> Sla()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "SLA")
            .ToListAsync();
        return View(settings);
    }

    // POST: Settings/Sla
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sla(IFormCollection form)
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "SLA")
            .ToListAsync();

        foreach (var setting in settings)
        {
            if (form.ContainsKey(setting.Key) &&
                int.TryParse(form[setting.Key].FirstOrDefault(), out var hours) &&
                hours >= 1)
            {
                setting.Value = hours.ToString();
            }
        }
        await _context.SaveChangesAsync();

        // Immediately apply new hours to the live static policy (no restart needed)
        int GetH(string key, int fallback) =>
            int.TryParse(settings.FirstOrDefault(s => s.Key == key)?.Value, out var h) && h > 0 ? h : fallback;
        ServiceDesk.Core.Services.SlaPolicy.Configure(
            GetH("SlaHoursCritical", 4),
            GetH("SlaHoursHigh",     8),
            GetH("SlaHoursMedium",   24),
            GetH("SlaHoursLow",      72));

        TempData["Success"] = "SLA policy saved. New tickets will use the updated deadlines immediately.";
        return RedirectToAction(nameof(Sla));
    }

    // ==================== NOTIFICATION SETTINGS ====================

    // GET: Settings/Notifications
    public async Task<IActionResult> Notifications()
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Notifications")
            .OrderBy(s => s.Key)
            .ToListAsync();
        return View(settings);
    }

    // POST: Settings/Notifications
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Notifications(IFormCollection form)
    {
        var settings = await _context.AppSettings
            .Where(s => s.Category == "Notifications")
            .ToListAsync();

        foreach (var setting in settings)
        {
            // Checkboxes: present = true, absent = false
            setting.Value = form.ContainsKey(setting.Key) ? "true" : "false";
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = "Notification settings saved.";
        return RedirectToAction(nameof(Notifications));
    }

    // ── Google Workspace ──────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GoogleWorkspace()
    {
        var settings = await _googleWorkspace.GetSettingsAsync();
        return View(settings);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GoogleWorkspace(string adminEmail, string domain,
        IFormFile? serviceAccountFile, string? signatureTemplate)
    {
        string? jsonContent = null;

        if (serviceAccountFile != null && serviceAccountFile.Length > 0)
        {
            if (!serviceAccountFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Please upload a valid JSON service account key file.";
                return RedirectToAction(nameof(GoogleWorkspace));
            }

            using var reader = new System.IO.StreamReader(serviceAccountFile.OpenReadStream());
            jsonContent = await reader.ReadToEndAsync();

            // Basic validation: must contain client_email and private_key
            if (!jsonContent.Contains("client_email") || !jsonContent.Contains("private_key"))
            {
                TempData["Error"] = "The uploaded file does not look like a valid Google service account key.";
                return RedirectToAction(nameof(GoogleWorkspace));
            }
        }

        await _googleWorkspace.SaveSettingsAsync(adminEmail, domain, jsonContent, signatureTemplate);
        TempData["Success"] = "Google Workspace settings saved.";
        return RedirectToAction(nameof(GoogleWorkspace));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestGoogleWorkspace()
    {
        var (passed, message) = await _googleWorkspace.TestConnectionAsync();
        TempData[passed ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(GoogleWorkspace));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkApplySignatures(bool includeAliases = false, bool activeOnly = true)
    {
        var settings = await _googleWorkspace.GetSettingsAsync();
        if (settings?.SignatureTemplate == null)
            return Json(new { success = false, message = "No signature template configured. Add one in the Default Signature Template field above." });

        var query = _context.Employees.Include(e => e.Branch).AsQueryable();
        if (activeOnly) query = query.Where(e => e.IsActive);
        var employees = await query.ToListAsync();

        if (employees.Count == 0)
            return Json(new { success = false, message = "No employees found." });

        var (successCount, failedCount, skippedCount, results) =
            await _googleWorkspace.BulkApplySignatureAsync(employees, settings.SignatureTemplate, includeAliases);

        // Stamp sync date on successful employees
        var successEmails = results.Where(r => r.Status == "success").Select(r => r.Email)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var emp in employees.Where(e => successEmails.Contains(e.Email)))
            emp.LastGoogleSignatureSync = now;
        if (successEmails.Count > 0)
            await _context.SaveChangesAsync();

        return Json(new { success = true, successCount, failedCount, skippedCount, results });
    }

    // ── Google Groups management ──────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GoogleGroups()
    {
        var settings = await _googleWorkspace.GetSettingsAsync();
        if (settings == null || !settings.IsConfigured)
        {
            TempData["Error"] = "Google Workspace is not configured. Please set it up first.";
            return RedirectToAction(nameof(GoogleWorkspace));
        }
        var (ok, groups, err) = await _googleWorkspace.GetDomainGroupsAsync();
        ViewBag.Error = ok ? null : err;
        return View(groups);
    }

    // ==================== CSAT SURVEYS ====================

    // GET: Settings/Csat
    public async Task<IActionResult> Csat()
    {
        var enabled = await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CsatSurveyEnabled");
        ViewBag.CsatEnabled = enabled?.Value == "true";
        return View();
    }

    // POST: Settings/Csat
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Csat(bool csatEnabled)
    {
        var setting = await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CsatSurveyEnabled");

        if (setting != null)
        {
            setting.Value = csatEnabled ? "true" : "false";
            await _context.SaveChangesAsync();
        }

        TempData["Success"] = $"CSAT surveys {(csatEnabled ? "enabled" : "disabled")}.";
        return RedirectToAction(nameof(Csat));
    }
}
