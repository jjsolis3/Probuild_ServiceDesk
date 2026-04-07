using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class TicketsController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public TicketsController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(
        TicketStatus[]?  statuses,    TicketCategory[]? categories,
        TicketPriority[]? priorities, int[]? assigneeIds,
        bool? unmatched, bool? unassigned,
        string sortBy = "id", string sortDir = "desc",
        int page = 1, int pageSize = 25,
        int? viewId = null)
    {
        var userId = CurrentPortalUserId();

        bool noFilters = (statuses    == null || statuses.Length    == 0)
                      && (categories  == null || categories.Length  == 0)
                      && (priorities  == null || priorities.Length  == 0)
                      && (assigneeIds == null || assigneeIds.Length == 0)
                      && unmatched == null && unassigned == null && viewId == null
                      && sortBy == "id" && sortDir == "desc" && page == 1 && pageSize == 25;

        if (noFilters && userId != null)
        {
            var def = await _context.SavedTicketViews
                .FirstOrDefaultAsync(v => v.IsDefault
                    && (v.OwnerPortalUserId == userId || v.IsShared));
            if (def != null)
                return RedirectToAction(nameof(Index), BuildViewParams(def));
        }

        // Load saved view criteria when viewId supplied
        SavedTicketView? activeView = null;
        if (viewId != null)
        {
            activeView = await _context.SavedTicketViews
                .Include(v => v.FilterBranch).Include(v => v.FilterGroup)
                .FirstOrDefaultAsync(v => v.Id == viewId
                    && (v.OwnerPortalUserId == userId || v.IsShared));
            if (activeView != null)
            {
                if ((statuses    == null || statuses.Length    == 0) && activeView.FilterStatus   != null)
                    statuses    = [activeView.FilterStatus.Value];
                if ((categories  == null || categories.Length  == 0) && activeView.FilterCategory != null)
                    categories  = [activeView.FilterCategory.Value];
                if ((priorities  == null || priorities.Length  == 0) && activeView.FilterPriority != null)
                    priorities  = [activeView.FilterPriority.Value];
                unmatched  ??= activeView.FilterUnmatchedOnly  ? true : null;
                unassigned ??= activeView.FilterUnassignedOnly ? true : null;
                sortBy   = sortBy   == "id"   ? activeView.SortBy   : sortBy;
                sortDir  = sortDir  == "desc" ? activeView.SortDir  : sortDir;
                pageSize = pageSize == 25     ? activeView.PageSize : pageSize;
            }
        }

        var query = _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .AsQueryable();

        if (statuses?.Length    > 0) query = query.Where(t => statuses.Contains(t.Status));
        if (categories?.Length  > 0) query = query.Where(t => categories.Contains(t.Category));
        if (priorities?.Length  > 0) query = query.Where(t => priorities.Contains(t.Priority));
        if (assigneeIds?.Length > 0) query = query.Where(t => t.AssignedToId != null && assigneeIds.Contains(t.AssignedToId.Value));
        if (unmatched  == true)      query = query.Where(t => t.SubmittedBy!.Email == "imported.ticket@servicesphere.local");
        if (unassigned == true)      query = query.Where(t => t.AssignedToId == null);

        // Sort
        query = (sortBy, sortDir) switch
        {
            ("id",       "asc")  => query.OrderBy(t => t.Id),
            ("id",          _)   => query.OrderByDescending(t => t.Id),
            ("title",    "asc")  => query.OrderBy(t => t.Title),
            ("title",       _)   => query.OrderByDescending(t => t.Title),
            ("category", "asc")  => query.OrderBy(t => t.Category),
            ("category",    _)   => query.OrderByDescending(t => t.Category),
            ("priority", "asc")  => query.OrderBy(t => t.Priority),
            ("priority",    _)   => query.OrderByDescending(t => t.Priority),
            ("status",   "asc")  => query.OrderBy(t => t.Status),
            ("status",      _)   => query.OrderByDescending(t => t.Status),
            ("due",      "asc")  => query.OrderBy(t => t.DueDate),
            ("due",         _)   => query.OrderByDescending(t => t.DueDate),
            ("created",  "asc")  => query.OrderBy(t => t.CreatedDate),
            _                    => query.OrderByDescending(t => t.CreatedDate),
        };

        // Count before paginating (single fast COUNT query)
        var totalCount = await query.CountAsync();

        // Clamp page size to allowed values
        pageSize = pageSize is 15 or 25 or 50 or 100 ? pageSize : 25;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var tickets = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.SelectedStatuses    = statuses    ?? Array.Empty<TicketStatus>();
        ViewBag.SelectedCategories  = categories  ?? Array.Empty<TicketCategory>();
        ViewBag.SelectedPriorities  = priorities  ?? Array.Empty<TicketPriority>();
        ViewBag.SelectedAssigneeIds = assigneeIds ?? Array.Empty<int>();
        ViewBag.CurrentUnmatched  = unmatched;
        ViewBag.CurrentUnassigned = unassigned;
        ViewBag.SortBy    = sortBy;
        ViewBag.SortDir   = sortDir;
        ViewBag.Page      = page;
        ViewBag.PageSize  = pageSize;
        ViewBag.TotalCount = totalCount;
        ViewBag.TotalPages = totalPages;

        ViewBag.ITStaffJson = System.Text.Json.JsonSerializer.Serialize(
            await _context.Employees
                .Where(e => e.IsActive && e.Department == "IT")
                .OrderBy(e => e.LastName)
                .Select(e => new { id = e.Id, name = e.FirstName + " " + e.LastName })
                .ToListAsync());

        // Employees linked to IT/Admin portal accounts (for Assignee filter dropdown)
        var itRoleIds = await _context.Roles
            .Where(r => r.Name != "End User")
            .Select(r => r.Id).ToListAsync();
        var assignableEmpIds = await _context.PortalUsers
            .Where(u => u.IsActive && u.EmployeeId != null
                     && u.RoleId != null && itRoleIds.Contains(u.RoleId.Value))
            .Select(u => u.EmployeeId!.Value)
            .Distinct().ToListAsync();
        ViewBag.AssignableStaff = await _context.Employees
            .Where(e => e.IsActive && assignableEmpIds.Contains(e.Id))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName })
            .ToListAsync();

        ViewBag.ActiveViewId   = activeView?.Id;
        ViewBag.ActiveViewName = activeView?.Name;

        // Data for the Save View modal dropdowns
        ViewBag.Branches = await _context.Branches
            .Where(b => b.IsActive).OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name }).ToListAsync();
        ViewBag.Groups = await _context.UserGroups
            .Where(g => g.IsActive).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name }).ToListAsync();
        ViewBag.Departments = await _context.Employees
            .Where(e => e.Department != null && e.Department != "")
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();

        return View(tickets);
    }

    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .Include(t => t.SubCategory)
            .Include(t => t.Branch)
            .Include(t => t.Notes.OrderBy(n => n.CreatedDate))
            .Include(t => t.Attachments)
            .Include(t => t.History.OrderBy(h => h.ChangedDate))
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        return View(ticket);
    }

    public IActionResult Create()
    {
        PopulateDropdowns();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Ticket ticket)
    {
        if (ModelState.IsValid)
        {
            ticket.CreatedDate = DateTime.UtcNow;
            // Auto-set SLA due date based on priority
            ticket.DueDate ??= SlaPolicy.CalculateDueDate(ticket.Priority, ticket.CreatedDate);
            _context.Add(ticket);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(ticket);
        return View(ticket);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .Include(t => t.SubCategory)
            .Include(t => t.Notes.OrderBy(n => n.CreatedDate))
            .Include(t => t.Attachments)
            .Include(t => t.History.OrderBy(h => h.ChangedDate))
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        PopulateDropdowns(ticket);
        return View(ticket);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Ticket ticket)
    {
        if (id != ticket.Id) return NotFound();

        if (ModelState.IsValid)
        {
            // Load the current ticket to detect changes for history
            var existing = await _context.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            var changedBy = User.Identity?.Name ?? "Unknown";

            ticket.UpdatedDate = DateTime.UtcNow;
            if (ticket.Status == TicketStatus.Resolved && ticket.ResolvedDate == null)
                ticket.ResolvedDate = DateTime.UtcNow;
            if (ticket.Status == TicketStatus.Closed && ticket.ClosedDate == null)
                ticket.ClosedDate = DateTime.UtcNow;

            // Recalculate DueDate if priority changed and no DueDate set yet
            if (ticket.DueDate == null)
                ticket.DueDate = SlaPolicy.CalculateDueDate(ticket.Priority, ticket.CreatedDate);

            // Record field-level audit history
            if (existing != null)
            {
                var histories = new List<TicketHistory>();
                if (existing.Status != ticket.Status)
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Status", OldValue = existing.Status.ToString(), NewValue = ticket.Status.ToString() });
                if (existing.Priority != ticket.Priority)
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Priority", OldValue = existing.Priority.ToString(), NewValue = ticket.Priority.ToString() });
                if (existing.AssignedToId != ticket.AssignedToId)
                {
                    var oldAgent = existing.AssignedToId.HasValue ? (await _context.Employees.FindAsync(existing.AssignedToId))?.FullName ?? "Unassigned" : "Unassigned";
                    var newAgent = ticket.AssignedToId.HasValue ? (await _context.Employees.FindAsync(ticket.AssignedToId))?.FullName ?? "Unassigned" : "Unassigned";
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Assigned To", OldValue = oldAgent, NewValue = newAgent });
                }
                if (existing.Category != ticket.Category)
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Category", OldValue = existing.Category.ToString(), NewValue = ticket.Category.ToString() });
                if (histories.Any())
                    _context.TicketHistory.AddRange(histories);
            }

            _context.Update(ticket);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(ticket);
        return View(ticket);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickUpdate(int id, string field, string value)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        if (field == "status")
        {
            if (!Enum.TryParse<TicketStatus>(value, out var newStatus)) return BadRequest();
            ticket.Status = newStatus;
            if (newStatus == TicketStatus.Resolved && ticket.ResolvedDate == null)
                ticket.ResolvedDate = DateTime.UtcNow;
            if (newStatus == TicketStatus.Closed && ticket.ClosedDate == null)
                ticket.ClosedDate = DateTime.UtcNow;
        }
        else if (field == "assignee")
        {
            ticket.AssignedToId = string.IsNullOrEmpty(value) || value == "0" ? null : int.TryParse(value, out var empId) ? empId : (int?)null;
        }
        else
        {
            return BadRequest();
        }

        ticket.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        string? assigneeName = null;
        if (ticket.AssignedToId.HasValue)
        {
            var emp = await _context.Employees.FindAsync(ticket.AssignedToId.Value);
            assigneeName = emp != null ? emp.FirstName + " " + emp.LastName : null;
        }

        return Json(new { success = true, status = ticket.Status.ToString(), assigneeName });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(int id, string content, bool isInternal = false)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        if (string.IsNullOrWhiteSpace(content)) return BadRequest(new { error = "Comment cannot be empty." });

        var note = new TicketNote
        {
            TicketId = id,
            AuthorName = User.Identity?.Name ?? "Agent",
            AuthorEmail = User.FindFirstValue(System.Security.Claims.ClaimTypes.Email),
            Content = content,
            CreatedDate = DateTime.UtcNow,
            Source = "Agent",
            IsInternal = isInternal
        };

        _context.TicketNotes.Add(note);
        ticket.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Json(new
        {
            success = true,
            id = note.Id,
            authorName = note.AuthorName,
            content = note.Content,
            createdDate = note.CreatedDate.ToString("MMM dd, yyyy h:mm tt"),
            isInternal = note.IsInternal,
            source = note.Source
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadAttachment(int id, IFormFile file)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file provided." });
        if (file.Length > 10 * 1024 * 1024) return BadRequest(new { error = "File size exceeds 10 MB limit." });

        var uploadDir = Path.Combine(
            Directory.GetCurrentDirectory(), "wwwroot", "uploads", "tickets", id.ToString());
        Directory.CreateDirectory(uploadDir);

        var ext = Path.GetExtension(file.FileName);
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(uploadDir, storedName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
            await file.CopyToAsync(stream);

        var attachment = new TicketAttachment
        {
            TicketId = id,
            FileName = file.FileName,
            StoredFileName = storedName,
            ContentType = file.ContentType,
            FileSize = file.Length,
            UploadedBy = User.Identity?.Name ?? "Unknown",
            UploadedDate = DateTime.UtcNow
        };
        _context.TicketAttachments.Add(attachment);
        ticket.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Json(new
        {
            success = true,
            id = attachment.Id,
            fileName = attachment.FileName,
            fileSize = attachment.FileSizeDisplay,
            downloadUrl = $"/uploads/tickets/{id}/{storedName}"
        });
    }

    // POST: Tickets/BulkUpdate — assign or change status on multiple tickets at once
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkUpdate(int[] selectedIds, string bulkAction, int? bulkAssigneeId, string? bulkStatus)
    {
        if (selectedIds == null || selectedIds.Length == 0)
        {
            TempData["Error"] = "No tickets selected.";
            return RedirectToAction(nameof(Index));
        }

        var tickets = await _context.Tickets
            .Where(t => selectedIds.Contains(t.Id))
            .ToListAsync();

        var changedBy = User.Identity?.Name ?? "Agent";
        var histories = new List<TicketHistory>();

        foreach (var ticket in tickets)
        {
            if (bulkAction == "assign" && bulkAssigneeId.HasValue)
            {
                histories.Add(new TicketHistory
                {
                    TicketId = ticket.Id, ChangedBy = changedBy, FieldName = "Assigned To",
                    OldValue = ticket.AssignedToId?.ToString() ?? "Unassigned",
                    NewValue = bulkAssigneeId.ToString()!
                });
                ticket.AssignedToId = bulkAssigneeId;
                ticket.UpdatedDate = DateTime.UtcNow;
            }
            else if (bulkAction == "status" && !string.IsNullOrEmpty(bulkStatus)
                     && Enum.TryParse<TicketStatus>(bulkStatus, out var newStatus))
            {
                histories.Add(new TicketHistory
                {
                    TicketId = ticket.Id, ChangedBy = changedBy, FieldName = "Status",
                    OldValue = ticket.Status.ToString(), NewValue = newStatus.ToString()
                });
                ticket.Status = newStatus;
                ticket.UpdatedDate = DateTime.UtcNow;
                if (newStatus == TicketStatus.Resolved && ticket.ResolvedDate == null)
                    ticket.ResolvedDate = DateTime.UtcNow;
                if (newStatus == TicketStatus.Closed && ticket.ClosedDate == null)
                    ticket.ClosedDate = DateTime.UtcNow;
            }
        }

        if (histories.Any()) _context.TicketHistory.AddRange(histories);
        await _context.SaveChangesAsync();

        TempData["Success"] = $"{tickets.Count} ticket(s) updated.";
        return RedirectToAction(nameof(Index));
    }

    // GET: Tickets/SubCategories?category=SoftwareIssue — returns sub-categories for a given parent
    [HttpGet]
    public async Task<IActionResult> SubCategories(TicketCategory category)
    {
        var items = await _context.TicketSubCategories
            .Where(s => s.Category == category && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();
        return Json(items);
    }

    // GET: Tickets/CannedResponses — returns active templates as JSON for the reply box
    [HttpGet]
    public async Task<IActionResult> CannedResponses()
    {
        var responses = await _context.CannedResponses
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Title)
            .Select(r => new { r.Id, r.Title, r.Content, r.Category })
            .ToListAsync();
        return Json(responses);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        return View(ticket);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket != null)
        {
            _context.Tickets.Remove(ticket);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    // ── CSV / TSV Import ─────────────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    public IActionResult ImportCsv() => View();

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10 * 1024 * 1024)]
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
        var tempPath = Path.Combine(Path.GetTempPath(), $"ss_ticket_{Guid.NewGuid():N}.dat");
        await using (var fs = System.IO.File.Create(tempPath))
            await file.CopyToAsync(fs);

        TempData["ImportTempPath"] = tempPath;

        var preview = await ParseTicketImportAsync(tempPath);
        return View("ImportCsvPreview", preview);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsvConfirm()
    {
        var tempPath = TempData["ImportTempPath"] as string;
        if (string.IsNullOrEmpty(tempPath) || !System.IO.File.Exists(tempPath))
        {
            TempData["Error"] = "Import session expired. Please upload the file again.";
            return RedirectToAction(nameof(ImportCsv));
        }

        var rows = await ParseTicketImportAsync(tempPath);
        System.IO.File.Delete(tempPath);

        // Get or create a placeholder employee for tickets whose requester couldn't be matched
        int? placeholderEmpId = null;
        if (rows.Any(r => !r.RequesterMatched))
        {
            const string placeholderEmail = "imported.ticket@servicesphere.local";
            var placeholder = await _context.Employees
                .FirstOrDefaultAsync(e => e.Email == placeholderEmail);
            if (placeholder == null)
            {
                placeholder = new Employee
                {
                    FirstName  = "Imported",
                    LastName   = "Ticket",
                    Email      = placeholderEmail,
                    Department = "System",
                    IsActive   = false,
                    HireDate   = DateTime.Today,
                };
                _context.Employees.Add(placeholder);
                await _context.SaveChangesAsync();
            }
            placeholderEmpId = placeholder.Id;
        }

        var tickets = rows.Select(r =>
        {
            // Build description — for unmatched requesters prepend the original name
            var desc = r.RequesterMatched
                ? r.Description
                : $"[Original Requester: {r.RequesterRaw}]\n{r.Description}";

            return new Ticket
            {
                Title           = Trunc(r.Title, 200),
                Description     = Trunc(desc, 2000),
                Status          = r.Status,
                Priority        = r.Priority,
                Category        = r.Category,
                SubCategoryId   = r.SubCategoryId,
                SubmittedById   = r.SubmittedById ?? placeholderEmpId!.Value,
                AssignedToId    = r.AssignedToId,
                BranchId        = r.BranchId,
                CreatedDate     = r.CreatedDate,
                UpdatedDate     = r.UpdatedDate,
                DueDate         = r.DueDate,
                ResolvedDate    = r.ResolvedDate,
                ClosedDate      = r.ClosedDate,
                ResolutionNotes = r.ResolutionNotes == null ? null : Trunc(r.ResolutionNotes, 2000),
            };
        }).ToList();

        await _context.Tickets.AddRangeAsync(tickets);
        await _context.SaveChangesAsync();

        int unmatched = rows.Count(r => !r.RequesterMatched);
        TempData["Success"] = $"Import complete: {tickets.Count} ticket(s) imported" +
            (unmatched > 0 ? $" ({unmatched} with unmatched requester — original name saved in description)" : "") + ".";
        return RedirectToAction(nameof(Index));
    }

    // ── Import helpers ────────────────────────────────────────────────────────

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    private async Task<List<ImportTicketRow>> ParseTicketImportAsync(string filePath)
    {
        using var reader = new System.IO.StreamReader(filePath, System.Text.Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(headerLine)) return new List<ImportTicketRow>();

        var delimiter = headerLine.Count(c => c == '\t') > 10 ? '\t' : ',';
        var headers = SplitCsvLine(headerLine, delimiter);
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            colIndex[headers[i].Trim()] = i;

        // Load all employees (active or not) for matching during import
        var employees = await _context.Employees
            .Select(e => new { e.Id, e.Email, FullName = (e.FirstName + " " + e.LastName).Trim() })
            .ToListAsync();
        var emailToId = employees
            .GroupBy(e => e.Email.ToLower())
            .ToDictionary(g => g.Key, g => g.First().Id);
        var nameToId = employees
            .GroupBy(e => e.FullName.ToLower())
            .ToDictionary(g => g.Key, g => g.First().Id);

        var branches = await _context.Branches
            .Select(b => new { b.Id, b.Name, b.City })
            .ToListAsync();

        var subCats = await _context.TicketSubCategories
            .Where(s => s.IsActive)
            .Select(s => new { s.Id, s.Name, s.Category })
            .ToListAsync();

        var preview = new List<ImportTicketRow>();
        int rowNum = 1;
        string? line;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            rowNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitCsvLine(line, delimiter);
            string Get(string name) =>
                colIndex.TryGetValue(name, out var i) && i < cols.Count ? cols[i].Trim() : "";

            var title = Get("Title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var row = new ImportTicketRow { RowNumber = rowNum, OriginalTitle = title };
            row.Title = title.Length > 200 ? title[..200] : title;

            var desc = Get("Description");
            if (string.IsNullOrWhiteSpace(desc)) desc = "(No description provided)";
            row.Description = desc.Length > 2000 ? desc[..2000] : desc;

            row.Status   = MapStatus(Get("State"));
            row.Priority = MapPriority(Get("Priority"));
            row.Category = MapCategory(Get("Category"));

            var subCatName = Get("Subcategory");
            if (!string.IsNullOrWhiteSpace(subCatName))
            {
                var match = subCats.FirstOrDefault(s =>
                    s.Category == row.Category &&
                    s.Name.Equals(subCatName, StringComparison.OrdinalIgnoreCase));
                row.SubCategoryId = match?.Id;
            }

            var requester = Get("Requester");
            row.RequesterRaw = requester;
            var reqEmail = ExtractEmail(requester);
            if (!string.IsNullOrEmpty(reqEmail) && emailToId.TryGetValue(reqEmail.ToLower(), out var subId))
                row.SubmittedById = subId;
            else if (!string.IsNullOrEmpty(requester) && nameToId.TryGetValue(requester.Trim().ToLower(), out var subIdByName))
                row.SubmittedById = subIdByName;

            var assigneeRaw = Get("Assignee Email");
            row.AssigneeEmailRaw = assigneeRaw;
            var asnEmail = ExtractEmail(assigneeRaw);
            if (!string.IsNullOrEmpty(asnEmail) && emailToId.TryGetValue(asnEmail.ToLower(), out var asnId))
                row.AssignedToId = asnId;
            else if (!string.IsNullOrEmpty(assigneeRaw) && nameToId.TryGetValue(assigneeRaw.Trim().ToLower(), out var asnIdByName))
                row.AssignedToId = asnIdByName;

            var site = Get("Site");
            if (!string.IsNullOrWhiteSpace(site))
            {
                var branch = branches.FirstOrDefault(b =>
                    b.Name.Contains(site, StringComparison.OrdinalIgnoreCase) ||
                    (b.City != null && b.City.Contains(site, StringComparison.OrdinalIgnoreCase)) ||
                    site.Contains(b.Name, StringComparison.OrdinalIgnoreCase));
                row.BranchId = branch?.Id;
                row.SiteRaw  = site;
            }

            row.CreatedDate  = ParseDate(Get("Created At")) ?? DateTime.UtcNow;
            row.UpdatedDate  = ParseDate(Get("Updated At"));
            row.DueDate      = ParseDate(Get("Due Date"));
            row.ResolvedDate = ParseDate(Get("Resolved At"));
            row.ClosedDate   = ParseDate(Get("Closed At"));

            var resolution = Get("Resolution");
            if (!string.IsNullOrWhiteSpace(resolution))
                row.ResolutionNotes = resolution.Length > 2000 ? resolution[..2000] : resolution;

            row.RequesterMatched = row.SubmittedById.HasValue;
            row.CanImport  = true; // all tickets import — unmatched use a placeholder employee
            row.SkipReason = row.RequesterMatched ? null : $"Requester '{requester}' not matched — original name will be saved in ticket description";

            preview.Add(row);
        }

        return preview;
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static string ExtractEmail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // "Name <email>" format
        var start = raw.LastIndexOf('<');
        var end   = raw.LastIndexOf('>');
        if (start >= 0 && end > start)
            return raw[(start + 1)..end].Trim();
        // plain email
        return raw.Contains('@') ? raw.Trim() : "";
    }

    private static DateTime? ParseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;
    }

    private static TicketStatus MapStatus(string raw) => raw.ToLower().Replace(" ", "") switch
    {
        "inprogress" or "open(inprogress)" => TicketStatus.InProgress,
        "resolved"   or "solved"           => TicketStatus.Resolved,
        "closed"                           => TicketStatus.Closed,
        "cancelled"  or "canceled"         => TicketStatus.Cancelled,
        "onhold"     or "pending"          => TicketStatus.OnHold,
        _                                  => TicketStatus.Open
    };

    private static TicketPriority MapPriority(string raw) => raw.ToLower() switch
    {
        "low"                        => TicketPriority.Low,
        "high" or "urgent"           => TicketPriority.High,
        "critical" or "emergency"    => TicketPriority.Critical,
        _                            => TicketPriority.Medium
    };

    private static TicketCategory MapCategory(string raw) =>
        raw.ToLower().Replace(" ", "").Replace("-", "") switch
        {
            "hardware"                       => TicketCategory.HardwareIssue,
            "software" or "applications"
                or "application"             => TicketCategory.SoftwareIssue,
            "network" or "networkissue"      => TicketCategory.NetworkIssue,
            "employee" or "hr" or "people"   => TicketCategory.EmployeeIssue,
            "security" or "securityincident" => TicketCategory.SecurityIncident,
            "servicerequest" or "service"    => TicketCategory.ServiceRequest,
            _                                => TicketCategory.Other
        };

    // ── Saved Views ──────────────────────────────────────────────────────────

    /// <summary>Returns the current user's saved views + all shared views as JSON.</summary>
    [HttpGet]
    public async Task<IActionResult> GetSavedViews()
    {
        var userId = CurrentPortalUserId();
        var views = await _context.SavedTicketViews
            .Where(v => v.OwnerPortalUserId == userId || v.IsShared)
            .OrderBy(v => v.IsShared).ThenBy(v => v.Name)
            .Select(v => new {
                v.Id, v.Name, v.IsDefault, v.IsShared,
                v.FilterStatus, v.FilterCategory, v.FilterPriority,
                v.FilterBranchId, v.FilterDepartment, v.FilterGroupId,
                v.FilterAssignedToMe, v.FilterUnassignedOnly, v.FilterUnmatchedOnly,
                v.SortBy, v.SortDir, v.PageSize,
                isOwner = v.OwnerPortalUserId == userId
            })
            .ToListAsync();
        return Json(views);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveView(
        string name, bool isShared,
        TicketStatus? filterStatus, TicketCategory? filterCategory, TicketPriority? filterPriority,
        int? filterBranchId, string? filterDepartment, int? filterGroupId,
        bool filterAssignedToMe, bool filterUnassignedOnly, bool filterUnmatchedOnly,
        string sortBy = "id", string sortDir = "desc", int pageSize = 25)
    {
        var userId = CurrentPortalUserId();
        if (userId == null) return Unauthorized();

        var view = new SavedTicketView
        {
            Name                 = name.Trim(),
            OwnerPortalUserId    = userId,
            IsShared             = isShared,
            FilterStatus         = filterStatus,
            FilterCategory       = filterCategory,
            FilterPriority       = filterPriority,
            FilterBranchId       = filterBranchId,
            FilterDepartment     = string.IsNullOrWhiteSpace(filterDepartment) ? null : filterDepartment.Trim(),
            FilterGroupId        = filterGroupId,
            FilterAssignedToMe   = filterAssignedToMe,
            FilterUnassignedOnly = filterUnassignedOnly,
            FilterUnmatchedOnly  = filterUnmatchedOnly,
            SortBy               = sortBy,
            SortDir              = sortDir,
            PageSize             = pageSize,
            CreatedDate          = DateTime.UtcNow
        };
        _context.SavedTicketViews.Add(view);
        await _context.SaveChangesAsync();
        return Json(new { success = true, id = view.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateView(
        int id, string name, bool isShared,
        TicketStatus? filterStatus, TicketCategory? filterCategory, TicketPriority? filterPriority,
        int? filterBranchId, string? filterDepartment, int? filterGroupId,
        bool filterAssignedToMe, bool filterUnassignedOnly, bool filterUnmatchedOnly,
        string sortBy = "id", string sortDir = "desc", int pageSize = 25)
    {
        var userId = CurrentPortalUserId();
        var view = await _context.SavedTicketViews
            .FirstOrDefaultAsync(v => v.Id == id && v.OwnerPortalUserId == userId);
        if (view == null) return NotFound();

        view.Name                 = name.Trim();
        view.IsShared             = isShared;
        view.FilterStatus         = filterStatus;
        view.FilterCategory       = filterCategory;
        view.FilterPriority       = filterPriority;
        view.FilterBranchId       = filterBranchId;
        view.FilterDepartment     = string.IsNullOrWhiteSpace(filterDepartment) ? null : filterDepartment.Trim();
        view.FilterGroupId        = filterGroupId;
        view.FilterAssignedToMe   = filterAssignedToMe;
        view.FilterUnassignedOnly = filterUnassignedOnly;
        view.FilterUnmatchedOnly  = filterUnmatchedOnly;
        view.SortBy               = sortBy;
        view.SortDir              = sortDir;
        view.PageSize             = pageSize;
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteView(int id)
    {
        var userId = CurrentPortalUserId();
        var view = await _context.SavedTicketViews
            .FirstOrDefaultAsync(v => v.Id == id && v.OwnerPortalUserId == userId);
        if (view == null) return NotFound();

        _context.SavedTicketViews.Remove(view);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultView(int id)
    {
        var userId = CurrentPortalUserId();
        if (userId == null) return Unauthorized();

        // Clear any existing default for this user
        var existing = await _context.SavedTicketViews
            .Where(v => v.OwnerPortalUserId == userId && v.IsDefault)
            .ToListAsync();
        existing.ForEach(v => v.IsDefault = false);

        // Set the new default (must belong to this user or be shared)
        var view = await _context.SavedTicketViews
            .FirstOrDefaultAsync(v => v.Id == id && (v.OwnerPortalUserId == userId || v.IsShared));
        if (view != null) view.IsDefault = true;

        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDefaultView()
    {
        var userId = CurrentPortalUserId();
        if (userId == null) return Unauthorized();

        var existing = await _context.SavedTicketViews
            .Where(v => v.OwnerPortalUserId == userId && v.IsDefault)
            .ToListAsync();
        existing.ForEach(v => v.IsDefault = false);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    private int? CurrentPortalUserId()
    {
        var raw = User.FindFirstValue("UserId");
        return int.TryParse(raw, out var id) ? id : null;
    }

    private static object BuildViewParams(SavedTicketView v)
    {
        var d = new Dictionary<string, string?>
        {
            ["viewId"]   = v.Id.ToString(),
            ["sortBy"]   = v.SortBy,
            ["sortDir"]  = v.SortDir,
            ["pageSize"] = v.PageSize.ToString(),
        };
        if (v.FilterStatus   != null) d["statuses"]   = v.FilterStatus.ToString();
        if (v.FilterCategory != null) d["categories"] = v.FilterCategory.ToString();
        if (v.FilterPriority != null) d["priorities"] = v.FilterPriority.ToString();
        if (v.FilterUnmatchedOnly)   d["unmatched"]   = "true";
        if (v.FilterUnassignedOnly)  d["unassigned"]  = "true";
        return d;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateDropdowns(Ticket? ticket = null)
    {
        ViewBag.Employees = new SelectList(
            _context.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.SubmittedById);

        ViewBag.ITStaff = new SelectList(
            _context.Employees.Where(e => e.IsActive && e.Department == "IT").OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.AssignedToId);

        ViewBag.Services = new SelectList(
            _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name),
            "Id", "Name", ticket?.CompanyServiceId);
    }
}
