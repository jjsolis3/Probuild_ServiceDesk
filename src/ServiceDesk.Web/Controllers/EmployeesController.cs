using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class EmployeesController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly IDataProtector _protector;
    private readonly GoogleWorkspaceService _googleWorkspace;

    public EmployeesController(ServiceDeskDbContext context,
        IDataProtectionProvider dpProvider,
        GoogleWorkspaceService googleWorkspace)
    {
        _context         = context;
        _protector       = dpProvider.CreateProtector("EmployeeCredentials.v1");
        _googleWorkspace = googleWorkspace;
    }

    public async Task<IActionResult> Index(string[]? departments, bool? active, int[]? branchIds, bool? hasLogin, string? q)
    {
        var query = _context.Employees.AsQueryable();

        if (departments is { Length: > 0 })
            query = query.Where(e => departments.Contains(e.Department));
        if (active.HasValue)
            query = query.Where(e => e.IsActive == active.Value);
        if (branchIds is { Length: > 0 })
            query = query.Where(e => e.BranchId != null && branchIds.Contains(e.BranchId.Value));
        if (hasLogin.HasValue)
        {
            var linkedIds = _context.PortalUsers
                .Where(u => u.EmployeeId != null)
                .Select(u => u.EmployeeId!.Value);
            query = hasLogin.Value
                ? query.Where(e => linkedIds.Contains(e.Id))
                : query.Where(e => !linkedIds.Contains(e.Id));
        }
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => e.FirstName.Contains(q) || e.LastName.Contains(q)
                || (e.Email != null && e.Email.Contains(q))
                || (e.Department != null && e.Department.Contains(q))
                || (e.JobTitle != null && e.JobTitle.Contains(q)));

        ViewBag.SelectedDepartments = departments ?? Array.Empty<string>();
        ViewBag.CurrentActive       = active;
        ViewBag.SelectedBranchIds   = branchIds ?? Array.Empty<int>();
        ViewBag.CurrentHasLogin     = hasLogin;
        ViewBag.CurrentQuery        = q;
        ViewBag.Departments         = await _context.Employees
            .Where(e => e.Department != null && e.Department != "")
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();
        ViewBag.Branches            = await _context.Branches
            .OrderBy(b => b.Name).Select(b => new { b.Id, b.Name }).ToListAsync();

        var employees = await query
            .Include(e => e.Branch)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
        return View(employees);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var employee = await _context.Employees
            .Include(e => e.SubmittedTickets)
            .Include(e => e.AssignedTickets)
            .Include(e => e.AssignedAssets)
            .Include(e => e.Credentials)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (employee == null) return NotFound();

        // Trend analytics: category breakdown of submitted tickets
        var submitted = employee.SubmittedTickets.ToList();
        var categoryIds = submitted.Select(t => t.Category).Distinct().ToList();
        var categoryNames = await _context.TicketCategories
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        ViewBag.CategoryBreakdown = submitted
            .GroupBy(t => t.Category)
            .Select(g => new {
                CategoryId   = g.Key,
                CategoryName = categoryNames.GetValueOrDefault(g.Key, $"Category {g.Key}"),
                Count        = g.Count(),
                OpenCount    = g.Count(t => t.Status == ServiceDesk.Core.Enums.TicketStatus.Open
                                         || t.Status == ServiceDesk.Core.Enums.TicketStatus.InProgress),
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);
        ViewBag.MonthlyTrend = submitted
            .Where(t => t.CreatedDate >= sixMonthsAgo)
            .GroupBy(t => new { t.CreatedDate.Year, t.CreatedDate.Month })
            .Select(g => new {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yy"),
                Count = g.Count()
            })
            .OrderBy(x => x.Label)
            .ToList();

        ViewBag.RecurringCategories = (ViewBag.CategoryBreakdown as IEnumerable<dynamic>)
            ?.Where(x => x.Count >= 3 && x.OpenCount > 0)
            .ToList();

        return View(employee);
    }

    // ── Employee Credential Vault ─────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCredential(int id, string label, string? username,
        string password, string? url, string? credNotes)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(password))
        {
            TempData["Error"] = "Label and password are required.";
            return RedirectToAction(nameof(Details), new { id, tab = "credentials" });
        }

        _context.EmployeeCredentials.Add(new EmployeeCredential
        {
            EmployeeId        = id,
            Label             = label.Trim(),
            Username          = username?.Trim(),
            EncryptedPassword = _protector.Protect(password),
            Url               = url?.Trim(),
            Notes             = credNotes?.Trim(),
            CreatedDate       = DateTime.UtcNow,
            CreatedByEmail    = User.Identity?.Name ?? "system",
        });
        await _context.SaveChangesAsync();

        TempData["Success"] = "Credential added.";
        return RedirectToAction(nameof(Details), new { id, tab = "credentials" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCredential(int id, int credentialId)
    {
        var cred = await _context.EmployeeCredentials.FindAsync(credentialId);
        if (cred != null && cred.EmployeeId == id)
        {
            _context.EmployeeCredentials.Remove(cred);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "Credential deleted.";
        return RedirectToAction(nameof(Details), new { id, tab = "credentials" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealPassword(int credentialId)
    {
        var cred = await _context.EmployeeCredentials.FindAsync(credentialId);
        if (cred == null) return Json(new { success = false, message = "Not found" });

        try
        {
            var plain = _protector.Unprotect(cred.EncryptedPassword);
            return Json(new { success = true, password = plain });
        }
        catch
        {
            return Json(new { success = false, message = "Decryption failed." });
        }
    }

    public async Task<IActionResult> Create()
    {
        ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Employee employee)
    {
        if (ModelState.IsValid)
        {
            _context.Add(employee);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
        return View(employee);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var employee = await _context.Employees
            .Include(e => e.Branch)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (employee == null) return NotFound();
        ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
        return View(employee);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Employee employee)
    {
        if (id != employee.Id) return NotFound();

        if (ModelState.IsValid)
        {
            _context.Update(employee);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        ViewBag.Branches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
        return View(employee);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var employee = await _context.Employees
            .FirstOrDefaultAsync(e => e.Id == id);

        if (employee == null) return NotFound();
        return View(employee);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return RedirectToAction(nameof(Index));

        // Check for FK-blocking records before attempting delete
        var submittedTickets = await _context.Tickets.CountAsync(t => t.SubmittedById == id);
        var assignedTickets  = await _context.Tickets.CountAsync(t => t.AssignedToId  == id);
        var assignedAssets   = await _context.Assets.CountAsync(a => a.AssignedToId   == id);

        if (submittedTickets > 0 || assignedTickets > 0 || assignedAssets > 0)
        {
            var parts = new List<string>();
            if (submittedTickets > 0) parts.Add($"{submittedTickets} submitted ticket(s)");
            if (assignedTickets  > 0) parts.Add($"{assignedTickets} assigned ticket(s)");
            if (assignedAssets   > 0) parts.Add($"{assignedAssets} assigned asset(s)");

            TempData["Error"] = $"Cannot delete {employee.FirstName} {employee.LastName} — they have {string.Join(", ", parts)}. " +
                                 "Reassign or delete those records first.";
            return RedirectToAction(nameof(Index));
        }

        // Unlink portal user (nullify FK) so the login account isn't orphaned
        var portalUser = await _context.PortalUsers.FirstOrDefaultAsync(u => u.EmployeeId == id);
        if (portalUser != null)
            portalUser.EmployeeId = null;

        _context.Employees.Remove(employee);
        await _context.SaveChangesAsync();

        TempData["Success"] = $"Employee {employee.FirstName} {employee.LastName} deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ── Employee / User Import ────────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    public IActionResult ImportCsv() => View();

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> ImportCsv(IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            ModelState.AddModelError("", "Please select a file to upload.");
            return View();
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".csv" && ext != ".tsv" && ext != ".txt")
        {
            ModelState.AddModelError("", "Only .csv, .tsv, or .txt files are supported.");
            return View();
        }

        // Save uploaded file to a server temp path — avoids TempData cookie overflow
        var tempPath = Path.Combine(Path.GetTempPath(), $"ss_employee_{Guid.NewGuid():N}.dat");
        await using (var fs = System.IO.File.Create(tempPath))
            await file.CopyToAsync(fs);

        TempData["ImportEmployeeTempPath"] = tempPath;

        var preview = await ParseEmployeeImportAsync(tempPath);
        return View("ImportCsvPreview", preview);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsvConfirm()
    {
        var tempPath = TempData["ImportEmployeeTempPath"] as string;
        if (string.IsNullOrEmpty(tempPath) || !System.IO.File.Exists(tempPath))
        {
            TempData["Error"] = "Import session expired. Please upload the file again.";
            return RedirectToAction(nameof(ImportCsv));
        }

        var rows = await ParseEmployeeImportAsync(tempPath);
        System.IO.File.Delete(tempPath);

        var toImport = rows.Where(r => r.CanImport).ToList();

        int empCount = 0, userCount = 0;
        const string defaultPassword = "Welcome@1";

        foreach (var row in toImport)
        {
            var emp = new Employee
            {
                FirstName  = row.FirstName,
                LastName   = row.LastName,
                Email      = row.Email,
                Phone      = string.IsNullOrWhiteSpace(row.Phone) ? null : row.Phone,
                Department = string.IsNullOrWhiteSpace(row.Department) ? "General" : row.Department,
                JobTitle   = string.IsNullOrWhiteSpace(row.JobTitle) ? null : row.JobTitle,
                IsActive   = row.IsActive,
                HireDate   = row.HireDate,
                BranchId   = row.BranchId,
            };
            _context.Employees.Add(emp);
            await _context.SaveChangesAsync(); // get emp.Id before creating portal user
            empCount++;

            if (row.WillCreateLogin && !row.LoginAlreadyExists)
            {
                _context.PortalUsers.Add(new PortalUser
                {
                    FirstName    = emp.FirstName,
                    LastName     = emp.LastName,
                    Email        = emp.Email,
                    PasswordHash = PasswordService.HashPassword(defaultPassword),
                    IsActive     = emp.IsActive,
                    RoleId       = row.RoleId,
                    EmployeeId   = emp.Id,
                    CreatedDate  = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync();
                userCount++;
            }
        }

        int skipped = rows.Count - toImport.Count;
        TempData["Success"] = $"Import complete: {empCount} employee(s) added" +
            (userCount > 0 ? $", {userCount} login account(s) created (password: {defaultPassword})" : "") +
            (skipped > 0 ? $", {skipped} skipped." : ".");

        return RedirectToAction(nameof(Index));
    }

    private async Task<List<ImportEmployeeRow>> ParseEmployeeImportAsync(string filePath)
    {
        using var reader = new System.IO.StreamReader(filePath, System.Text.Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(headerLine)) return new List<ImportEmployeeRow>();

        var delimiter = headerLine.Count(c => c == '\t') > 5 ? '\t' : ',';
        var headers = SplitLine(headerLine, delimiter);
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            col[headers[i].Trim()] = i;

        var existingEmails = await _context.Employees
            .Select(e => e.Email.ToLower()).ToHashSetAsync();
        var existingUserEmails = await _context.PortalUsers
            .Select(u => u.Email.ToLower()).ToHashSetAsync();
        var branches = await _context.Branches
            .Select(b => new { b.Id, b.Name, b.City }).ToListAsync();
        var roles = await _context.Roles
            .Select(r => new { r.Id, r.Name }).ToListAsync();

        var preview = new List<ImportEmployeeRow>();
        int rowNum = 1;
        string? line;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            rowNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitLine(line, delimiter);
            string Get(string name) =>
                col.TryGetValue(name, out var i) && i < cols.Count ? cols[i].Trim() : "";

            var firstName = Get("FirstName");
            var lastName  = Get("LastName");
            var email     = Get("Email");

            if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(email)) continue;

            var row = new ImportEmployeeRow
            {
                RowNumber  = rowNum,
                FirstName  = firstName.Length > 100 ? firstName[..100] : firstName,
                LastName   = lastName.Length  > 100 ? lastName[..100]  : lastName,
                Email      = email,
                Phone      = Get("Phone"),
                Department = Get("Department"),
                JobTitle   = Get("JobTitle"),
                IsActive   = !Get("IsActive").Equals("No", StringComparison.OrdinalIgnoreCase),
                RoleName   = Get("Role"),
            };

            var hireDateRaw = Get("HireDate");
            row.HireDate = DateTime.TryParse(hireDateRaw, out var hd) ? hd : DateTime.Today;

            var site = Get("Site");
            if (!string.IsNullOrWhiteSpace(site))
            {
                var branch = branches.FirstOrDefault(b =>
                    b.Name.Contains(site, StringComparison.OrdinalIgnoreCase) ||
                    (b.City != null && b.City.Contains(site, StringComparison.OrdinalIgnoreCase)));
                row.BranchId = branch?.Id;
                row.SiteRaw  = site;
            }

            if (!string.IsNullOrWhiteSpace(row.RoleName))
            {
                var role = roles.FirstOrDefault(r =>
                    r.Name.Equals(row.RoleName, StringComparison.OrdinalIgnoreCase));
                row.RoleId = role?.Id;
            }

            if (string.IsNullOrWhiteSpace(row.FirstName))
                row.SkipReason = "FirstName is required";
            else if (string.IsNullOrWhiteSpace(row.Email))
                row.SkipReason = "Email is required";
            else if (!row.Email.Contains('@'))
                row.SkipReason = $"'{row.Email}' is not a valid email address";
            else if (existingEmails.Contains(row.Email.ToLower()))
                row.SkipReason = $"Employee with email '{row.Email}' already exists";
            else if (!string.IsNullOrEmpty(row.RoleName) && row.RoleId == null)
                row.SkipReason = $"Role '{row.RoleName}' not found — check Settings → Roles";

            row.WillCreateLogin    = row.SkipReason == null && row.RoleId.HasValue;
            row.LoginAlreadyExists = existingUserEmails.Contains(row.Email.ToLower());
            row.CanImport          = row.SkipReason == null;

            preview.Add(row);
        }

        return preview;
    }

    private static List<string> SplitLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    // ── Gmail Signature Management ────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetSignature(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        var (success, html, error) = await _googleWorkspace.GetSignatureAsync(employee.Email);
        return Json(new { success, html = html ?? "", error });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSignature(int id, string html, bool includeAliases = false)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        bool ok;
        string? errorMsg;
        int addressesUpdated = 1;

        if (includeAliases)
        {
            var (s, updated, _, err) = await _googleWorkspace.UpdateSignatureAllAddressesAsync(employee.Email, html ?? "");
            ok = s; errorMsg = err; addressesUpdated = updated;
        }
        else
        {
            var (s, err) = await _googleWorkspace.UpdateSignatureAsync(employee.Email, html ?? "");
            ok = s; errorMsg = err;
        }

        if (ok)
        {
            employee.LastGoogleSignatureSync = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            var msg = includeAliases
                ? $"Signature applied to {addressesUpdated} address(es) for {employee.FullName}."
                : $"Signature updated for {employee.FullName}.";
            return Json(new { success = true, message = msg,
                syncDate = employee.LastGoogleSignatureSync!.Value.ToString("MMM d, yyyy h:mm tt") });
        }

        return Json(new { success = false, message = errorMsg ?? "Unknown error." });
    }

    // ── Google Workspace Admin Actions ────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetWorkspaceInfo(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        var (userOk, user, userErr) = await _googleWorkspace.GetGoogleUserAsync(employee.Email);
        var (vacOk,  vac,  vacErr)  = await _googleWorkspace.GetVacationResponderAsync(employee.Email);
        var (grpOk,  grps, grpErr)  = await _googleWorkspace.GetUserGroupsAsync(employee.Email);

        return Json(new
        {
            user = userOk ? new
            {
                suspended  = user!.Suspended,
                mustChange = user.ChangePasswordAtNextLogin,
                orgUnit    = user.OrgUnit,
                error      = (string?)null
            } : new { suspended = false, mustChange = false, orgUnit = (string?)null, error = userErr },

            vacation = vacOk ? new
            {
                enabled  = vac!.EnableAutoReply,
                subject  = vac.ResponseSubject,
                body     = vac.ResponseBodyHtml,
                start    = vac.StartTime?.ToString("yyyy-MM-dd"),
                end      = vac.EndTime?.ToString("yyyy-MM-dd"),
                contacts = vac.RestrictToContacts,
                domain   = vac.RestrictToDomain,
                error    = (string?)null
            } : (object)new { error = vacErr },

            groups = grpOk
                ? grps.Select(g => new { g.Email, g.Name, g.MemberCount }).ToList()
                : (object)new { error = grpErr }
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendUser(int id, bool suspended)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
        var (ok, err) = await _googleWorkspace.SetSuspendedAsync(employee.Email, suspended);
        return Json(new { success = ok,
            message = ok ? $"Account {(suspended ? "suspended" : "unsuspended")} successfully." : err });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetWorkspacePassword(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
        var (ok, pw, err) = await _googleWorkspace.ResetPasswordAsync(employee.Email);
        return Json(new { success = ok, tempPassword = pw, message = err });
    }

    [HttpGet]
    public async Task<IActionResult> GetStorageInfo(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
        var (ok, info, err) = await _googleWorkspace.GetStorageAsync(employee.Email);
        if (!ok) return Json(new { success = false, error = err });
        return Json(new
        {
            success    = true,
            gmailMb    = info!.GmailUsedMb,
            driveMb    = info.DriveUsedMb,
            totalMb    = info.TotalQuotaMb,
            asOf       = info.AsOfDate?.ToString("MMM d, yyyy")
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetVacationResponder(int id,
        bool enabled, string? subject, string? body,
        string? startDate, string? endDate,
        bool restrictToContacts = false, bool restrictToDomain = false)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        DateTimeOffset? start = DateTime.TryParse(startDate, out var sd)
            ? new DateTimeOffset(sd, TimeSpan.Zero) : null;
        DateTimeOffset? end = DateTime.TryParse(endDate, out var ed)
            ? new DateTimeOffset(ed, TimeSpan.Zero) : null;

        var settings = new GoogleWorkspaceService.VacationResponder(
            enabled, subject, body, start, end, restrictToContacts, restrictToDomain);
        var (ok, err) = await _googleWorkspace.SetVacationResponderAsync(employee.Email, settings);
        return Json(new { success = ok,
            message = ok ? (enabled ? "Out-of-office responder enabled." : "Out-of-office responder disabled.") : err });
    }

    [HttpGet]
    public async Task<IActionResult> GetDomainGroups()
    {
        var (ok, groups, err) = await _googleWorkspace.GetDomainGroupsAsync();
        if (!ok) return Json(new { success = false, error = err });
        return Json(new { success = true,
            groups = groups.Select(g => new { g.Email, g.Name, g.MemberCount }) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToGroup(int id, string groupEmail)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
        var (ok, err) = await _googleWorkspace.AddToGroupAsync(groupEmail, employee.Email);
        return Json(new { success = ok, message = ok ? $"Added to {groupEmail}." : err });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveFromGroup(int id, string groupEmail)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
        var (ok, err) = await _googleWorkspace.RemoveFromGroupAsync(groupEmail, employee.Email);
        return Json(new { success = ok, message = ok ? $"Removed from {groupEmail}." : err });
    }

    // ── Onboarding ────────────────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionWorkspaceAccount(int id, string? orgUnit)
    {
        var employee = await _context.Employees.Include(e => e.Branch).FirstOrDefaultAsync(e => e.Id == id);
        if (employee == null) return NotFound();

        if (string.IsNullOrWhiteSpace(employee.Email))
            return Json(new { success = false, message = "Employee has no email address on file." });

        var ou = string.IsNullOrWhiteSpace(orgUnit) ? "/" : orgUnit.Trim();
        var (ok, pw, err) = await _googleWorkspace.CreateGoogleUserAsync(
            employee.FirstName, employee.LastName, employee.Email, ou);

        return Json(new
        {
            success = ok,
            message = ok ? $"Google account created for {employee.Email}." : err,
            tempPassword = pw
        });
    }

    // ── Offboarding ───────────────────────────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunOffboarding(int id,
        bool suspend         = true,
        bool removeGroups    = true,
        bool setOoo          = true,
        string? oooSubject   = null,
        string? oooBody      = null,
        bool revokeTokens    = true,
        bool transferDrive   = false,
        string? transferToEmail = null)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        var email   = employee.Email;
        var steps   = new List<object>();
        bool anyFail = false;

        // 1. Suspend
        if (suspend)
        {
            var (ok, err) = await _googleWorkspace.SetSuspendedAsync(email, true);
            steps.Add(new { step = "Suspend account", ok, message = ok ? "Account suspended." : err });
            if (!ok) anyFail = true;
        }

        // 2. Revoke OAuth tokens
        if (revokeTokens)
        {
            var (ok, err) = await _googleWorkspace.RevokeAllTokensAsync(email);
            steps.Add(new { step = "Revoke OAuth tokens", ok, message = ok ? "All app access revoked." : err });
            if (!ok) anyFail = true;
        }

        // 3. Remove from all groups
        if (removeGroups)
        {
            var (grpOk, grps, _) = await _googleWorkspace.GetUserGroupsAsync(email);
            int removed = 0, grpFail = 0;
            if (grpOk)
                foreach (var g in grps)
                {
                    var (ok2, _) = await _googleWorkspace.RemoveFromGroupAsync(g.Email, email);
                    if (ok2) removed++; else grpFail++;
                }
            steps.Add(new { step = "Remove from groups",
                ok = grpFail == 0,
                message = grpOk ? $"Removed from {removed} group(s)." : "Groups API unavailable." });
        }

        // 4. OOO Responder
        if (setOoo)
        {
            var vac = new GoogleWorkspaceService.VacationResponder(
                EnableAutoReply:    true,
                ResponseSubject:    oooSubject ?? $"{employee.FullName} is no longer with the company.",
                ResponseBodyHtml:   oooBody    ?? $"<p>{employee.FullName} is no longer available. Please contact your account manager.</p>",
                StartTime:          null,
                EndTime:            null,
                RestrictToContacts: false,
                RestrictToDomain:   false);
            var (ok, err) = await _googleWorkspace.SetVacationResponderAsync(email, vac);
            steps.Add(new { step = "Set out-of-office", ok, message = ok ? "OOO auto-reply enabled." : err });
            if (!ok) anyFail = true;
        }

        // 5. Drive transfer
        if (transferDrive && !string.IsNullOrWhiteSpace(transferToEmail))
        {
            var (ok, transferId, err) = await _googleWorkspace.StartDriveTransferAsync(email, transferToEmail);
            steps.Add(new
            {
                step    = "Transfer Drive files",
                ok,
                message = ok ? $"Drive transfer initiated (ID: {transferId}). Files will appear in {transferToEmail}'s Drive shortly." : err
            });
            if (!ok) anyFail = true;
        }

        return Json(new
        {
            success = !anyFail,
            message = anyFail ? "Offboarding completed with some errors." : "Offboarding completed successfully.",
            steps
        });
    }

    // ── Security ──────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetOAuthApps(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        var (ok, tokens, err) = await _googleWorkspace.GetUserTokensAsync(employee.Email);
        if (!ok) return Json(new { success = false, error = err });

        return Json(new
        {
            success = true,
            tokens  = tokens.Select(t => new { t.AppName, t.ClientId, scopeCount = t.Scopes.Count, t.Scopes })
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetLoginActivity(int id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();

        var (ok, events, err) = await _googleWorkspace.GetUserLoginActivityAsync(employee.Email, 25);
        if (!ok) return Json(new { success = false, error = err });

        return Json(new
        {
            success = true,
            events  = events.Select(e => new
            {
                time      = e.Time.ToString("MMM d, yyyy h:mm tt"),
                eventName = e.EventName,
                ip        = e.IpAddress
            })
        });
    }

    [HttpGet]
    public async Task<IActionResult> ListSharedDrives()
    {
        var (ok, drives, err) = await _googleWorkspace.ListSharedDrivesAsync();
        if (!ok) return Json(new { success = false, error = err });
        return Json(new { success = true, drives = drives.Select(d => new { d.Id, d.Name }) });
    }

    [HttpGet]
    public async Task<IActionResult> GetSignatureTemplate(int id)
    {
        var employee = await _context.Employees
            .Include(e => e.Branch)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (employee == null) return NotFound();

        var settings = await _googleWorkspace.GetSettingsAsync();
        if (settings?.SignatureTemplate == null)
            return Json(new { success = false, message = "No signature template configured in Settings → Google Workspace." });

        var rendered = await _googleWorkspace.RenderTemplateAsync(settings.SignatureTemplate, employee);
        return Json(new { success = true, html = rendered });
    }
}
