using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class EmployeesController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public EmployeesController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(string? department, bool? active)
    {
        var query = _context.Employees.AsQueryable();

        if (!string.IsNullOrEmpty(department))
            query = query.Where(e => e.Department == department);
        if (active.HasValue)
            query = query.Where(e => e.IsActive == active.Value);

        ViewBag.CurrentDepartment = department;
        ViewBag.CurrentActive = active;
        ViewBag.Departments = await _context.Employees
            .Select(e => e.Department).Distinct().OrderBy(d => d).ToListAsync();

        var employees = await query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
        return View(employees);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var employee = await _context.Employees
            .Include(e => e.SubmittedTickets)
            .Include(e => e.AssignedTickets)
            .Include(e => e.AssignedAssets)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (employee == null) return NotFound();
        return View(employee);
    }

    public IActionResult Create()
    {
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
        return View(employee);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var employee = await _context.Employees.FindAsync(id);
        if (employee == null) return NotFound();
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
        if (employee != null)
        {
            _context.Employees.Remove(employee);
            await _context.SaveChangesAsync();
        }
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

        using var reader = new System.IO.StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            ModelState.AddModelError("", "The file appears to be empty.");
            return View();
        }

        var delimiter = headerLine.Count(c => c == '\t') > 5 ? '\t' : ',';
        var headers = SplitLine(headerLine, delimiter);
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            col[headers[i].Trim()] = i;

        // Load lookup data
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

            // HireDate
            var hireDateRaw = Get("HireDate");
            row.HireDate = DateTime.TryParse(hireDateRaw, out var hd) ? hd : DateTime.Today;

            // Branch
            var site = Get("Site");
            if (!string.IsNullOrWhiteSpace(site))
            {
                var branch = branches.FirstOrDefault(b =>
                    b.Name.Contains(site, StringComparison.OrdinalIgnoreCase) ||
                    (b.City != null && b.City.Contains(site, StringComparison.OrdinalIgnoreCase)));
                row.BranchId  = branch?.Id;
                row.SiteRaw   = site;
            }

            // Role lookup (for portal user creation)
            if (!string.IsNullOrWhiteSpace(row.RoleName))
            {
                var role = roles.FirstOrDefault(r =>
                    r.Name.Equals(row.RoleName, StringComparison.OrdinalIgnoreCase));
                row.RoleId = role?.Id;
            }

            // Validation
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

            row.WillCreateLogin = row.SkipReason == null && row.RoleId.HasValue;
            row.LoginAlreadyExists = existingUserEmails.Contains(row.Email.ToLower());
            row.CanImport = row.SkipReason == null;

            preview.Add(row);
        }

        TempData["ImportEmployeePreview"] = System.Text.Json.JsonSerializer.Serialize(preview);
        return View("ImportCsvPreview", preview);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsvConfirm()
    {
        var json = TempData["ImportEmployeePreview"] as string;
        if (string.IsNullOrEmpty(json))
        {
            TempData["Error"] = "Import session expired. Please upload the file again.";
            return RedirectToAction(nameof(ImportCsv));
        }

        var rows = System.Text.Json.JsonSerializer.Deserialize<List<ImportEmployeeRow>>(json)!;
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
}
