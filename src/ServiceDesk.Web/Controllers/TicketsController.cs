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

    public async Task<IActionResult> Index(TicketStatus? status, TicketCategory? category, TicketPriority? priority)
    {
        var query = _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);
        if (category.HasValue)
            query = query.Where(t => t.Category == category.Value);
        if (priority.HasValue)
            query = query.Where(t => t.Priority == priority.Value);

        ViewBag.CurrentStatus = status;
        ViewBag.CurrentCategory = category;
        ViewBag.CurrentPriority = priority;

        ViewBag.ITStaffJson = System.Text.Json.JsonSerializer.Serialize(
            await _context.Employees
                .Where(e => e.IsActive && e.Department == "IT")
                .OrderBy(e => e.LastName)
                .Select(e => new { id = e.Id, name = e.FirstName + " " + e.LastName })
                .ToListAsync());

        var tickets = await query.OrderByDescending(t => t.CreatedDate).ToListAsync();
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

        var toImport = rows.Where(r => r.CanImport).ToList();

        var tickets = toImport.Select(r => new Ticket
        {
            Title           = r.Title,
            Description     = r.Description,
            Status          = r.Status,
            Priority        = r.Priority,
            Category        = r.Category,
            SubCategoryId   = r.SubCategoryId,
            SubmittedById   = r.SubmittedById!.Value,
            AssignedToId    = r.AssignedToId,
            BranchId        = r.BranchId,
            CreatedDate     = r.CreatedDate,
            UpdatedDate     = r.UpdatedDate,
            DueDate         = r.DueDate,
            ResolvedDate    = r.ResolvedDate,
            ClosedDate      = r.ClosedDate,
            ResolutionNotes = r.ResolutionNotes,
        }).ToList();

        await _context.Tickets.AddRangeAsync(tickets);
        await _context.SaveChangesAsync();

        int skipped = rows.Count - toImport.Count;
        TempData["Success"] = $"Import complete: {tickets.Count} ticket(s) imported, {skipped} skipped.";
        return RedirectToAction(nameof(Index));
    }

    // ── Import helpers ────────────────────────────────────────────────────────

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

            row.CanImport  = row.SubmittedById.HasValue;
            row.SkipReason = row.CanImport ? null : $"Requester '{requester}' not found in Employees (tried email and name match)";

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
