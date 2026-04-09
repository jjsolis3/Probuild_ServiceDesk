using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using ServiceDesk.Core.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
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

    public SettingsController(ServiceDeskDbContext context, GmailApiService gmailApiService, EmailNotificationService emailService)
    {
        _context = context;
        _gmailApiService = gmailApiService;
        _emailService = emailService;
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
        var settings = await _context.AppSettings.ToListAsync();
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

        ViewBag.Categories = Enum.GetValues<TicketCategory>()
            .Select(c => new { Value = (int)c, Text = c.GetDisplayName() })
            .ToList();

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

    public async Task<IActionResult> Categories()
    {
        var subCategories = await _context.TicketSubCategories
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync();
        return View(subCategories);
    }

    public IActionResult CreateSubCategory(TicketCategory? category)
    {
        ViewBag.Categories = Enum.GetValues<TicketCategory>()
            .Select(c => new SelectListItem(c.ToString(), ((int)c).ToString()))
            .ToList();
        var model = new TicketSubCategory { Category = category ?? TicketCategory.Other };
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
        ViewBag.Categories = Enum.GetValues<TicketCategory>()
            .Select(c => new SelectListItem(c.ToString(), ((int)c).ToString()))
            .ToList();
        return View(model);
    }

    public async Task<IActionResult> EditSubCategory(int id)
    {
        var subCat = await _context.TicketSubCategories.FindAsync(id);
        if (subCat == null) return NotFound();
        ViewBag.Categories = Enum.GetValues<TicketCategory>()
            .Select(c => new SelectListItem(c.ToString(), ((int)c).ToString()))
            .ToList();
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
        ViewBag.Categories = Enum.GetValues<TicketCategory>()
            .Select(c => new SelectListItem(c.ToString(), ((int)c).ToString()))
            .ToList();
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
}
