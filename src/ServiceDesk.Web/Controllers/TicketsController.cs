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
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class TicketsController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly AssignmentResolverService _assignmentResolver;
    private readonly EmailNotificationService _emailService;
    private readonly AiTriageService _aiTriage;
    private readonly OllamaService _ollama;
    private readonly TicketSimilarityService _similarity;
    private readonly SlaRiskService _slaRisk;

    public TicketsController(
        ServiceDeskDbContext context,
        AssignmentResolverService assignmentResolver,
        EmailNotificationService emailService,
        AiTriageService aiTriage,
        OllamaService ollama,
        TicketSimilarityService similarity,
        SlaRiskService slaRisk)
    {
        _context            = context;
        _assignmentResolver = assignmentResolver;
        _emailService       = emailService;
        _aiTriage           = aiTriage;
        _ollama             = ollama;
        _similarity         = similarity;
        _slaRisk            = slaRisk;
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(
        TicketStatus[]?  statuses,    TicketCategory[]? categories,
        TicketPriority[]? priorities, int[]? assigneeIds,
        int[]? requesterIds,
        int[]? branchIds, string[]? departments,
        bool? unmatched, bool? unassigned, bool? assignedToMe,
        string? q,
        string sortBy = "id", string sortDir = "desc",
        int page = 1, int pageSize = 25,
        int? viewId = null, bool partial = false)
    {
        var userId = CurrentPortalUserId();

        // Pre-fetch the current user's linked employee ID — used for "Assigned to Me" filter
        // and for the Save View modal pre-fill. One small query, cached for the request.
        var currentUserEmpId = userId.HasValue
            ? await _context.PortalUsers
                .Where(u => u.Id == userId.Value)
                .Select(u => (int?)u.EmployeeId)
                .FirstOrDefaultAsync()
            : null;

        bool noFilters = (statuses     == null || statuses.Length     == 0)
                      && (categories  == null || categories.Length  == 0)
                      && (priorities  == null || priorities.Length  == 0)
                      && (assigneeIds == null || assigneeIds.Length == 0)
                      && (requesterIds == null || requesterIds.Length == 0)
                      && (branchIds   == null || branchIds.Length   == 0)
                      && (departments == null || departments.Length == 0)
                      && unmatched == null && unassigned == null && assignedToMe == null && viewId == null
                      && string.IsNullOrWhiteSpace(q)
                      && sortBy == "id" && sortDir == "desc" && page == 1 && pageSize == 25;

        if (noFilters && userId != null)
        {
            // 1. Personal default takes priority
            var def = await _context.SavedTicketViews
                .FirstOrDefaultAsync(v => v.IsDefault && v.OwnerPortalUserId == userId);

            // 2. Fall back to the system default (OwnerPortalUserId == null, IsDefault, IsShared)
            def ??= await _context.SavedTicketViews
                .FirstOrDefaultAsync(v => v.IsDefault && v.OwnerPortalUserId == null && v.IsShared);

            if (def != null)
                return Redirect(BuildViewUrl(def));
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
                if (statuses == null || statuses.Length == 0)
                {
                    if (!string.IsNullOrEmpty(activeView.FilterStatuses))
                        statuses = activeView.FilterStatuses.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => Enum.TryParse<TicketStatus>(s.Trim(), out var v) ? v : (TicketStatus?)null)
                            .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                    else if (activeView.FilterStatus != null)
                        statuses = [activeView.FilterStatus.Value];
                }
                if (categories == null || categories.Length == 0)
                {
                    if (!string.IsNullOrEmpty(activeView.FilterCategories))
                        categories = activeView.FilterCategories.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(c => Enum.TryParse<TicketCategory>(c.Trim(), out var v) ? v : (TicketCategory?)null)
                            .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                    else if (activeView.FilterCategory != null)
                        categories = [activeView.FilterCategory.Value];
                }
                if (priorities == null || priorities.Length == 0)
                {
                    if (!string.IsNullOrEmpty(activeView.FilterPriorities))
                        priorities = activeView.FilterPriorities.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => Enum.TryParse<TicketPriority>(p.Trim(), out var v) ? v : (TicketPriority?)null)
                            .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                    else if (activeView.FilterPriority != null)
                        priorities = [activeView.FilterPriority.Value];
                }
                // Branch multi-select: FilterBranchIds takes priority over legacy FilterBranchId
                if (branchIds == null || branchIds.Length == 0)
                {
                    if (!string.IsNullOrEmpty(activeView.FilterBranchIds))
                        branchIds = activeView.FilterBranchIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => int.TryParse(s.Trim(), out var bid) ? bid : (int?)null)
                            .Where(b => b.HasValue).Select(b => b!.Value).ToArray();
                    else if (activeView.FilterBranchId != null)
                        branchIds = [activeView.FilterBranchId.Value];
                }
                // Department multi-select: FilterDepartments takes priority over legacy FilterDepartment
                if (departments == null || departments.Length == 0)
                {
                    if (!string.IsNullOrEmpty(activeView.FilterDepartments))
                        departments = activeView.FilterDepartments.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(d => d.Trim()).Where(d => d.Length > 0).ToArray();
                    else if (!string.IsNullOrEmpty(activeView.FilterDepartment))
                        departments = [activeView.FilterDepartment];
                }
                unmatched  ??= activeView.FilterUnmatchedOnly  ? true : null;
                unassigned ??= activeView.FilterUnassignedOnly ? true : null;
                // FilterAssignedToMe → resolve current user's employee ID into assigneeIds
                if (activeView.FilterAssignedToMe && currentUserEmpId != null)
                {
                    var myId = currentUserEmpId.Value;
                    assigneeIds = (assigneeIds == null || assigneeIds.Length == 0)
                        ? [myId]
                        : assigneeIds.Contains(myId) ? assigneeIds : [.. assigneeIds, myId];
                }
                sortBy   = sortBy   == "id"   ? activeView.SortBy   : sortBy;
                sortDir  = sortDir  == "desc" ? activeView.SortDir  : sortDir;
                pageSize = pageSize == 25     ? activeView.PageSize : pageSize;
            }
        }

        // assignedToMe=true URL param (generated by BuildViewUrl from FilterAssignedToMe)
        if (assignedToMe == true && currentUserEmpId != null)
        {
            var myId = currentUserEmpId.Value;
            assigneeIds = (assigneeIds == null || assigneeIds.Length == 0)
                ? [myId]
                : assigneeIds.Contains(myId) ? assigneeIds : [.. assigneeIds, myId];
        }

        var query = _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .AsQueryable();

        if (statuses?.Length     > 0) query = query.Where(t => statuses.Contains(t.Status));
        if (categories?.Length   > 0) query = query.Where(t => categories.Contains(t.Category));
        if (priorities?.Length   > 0) query = query.Where(t => priorities.Contains(t.Priority));
        if (branchIds?.Length    > 0) query = query.Where(t => t.BranchId != null && branchIds.Contains(t.BranchId.Value));
        if (departments?.Length  > 0) query = query.Where(t => t.SubmittedBy != null && departments.Contains(t.SubmittedBy.Department!));
        // Assignee filter uses OR so "Unassigned + Jose Solis" returns tickets that are
        // either unassigned OR assigned to Jose — not the impossible AND intersection.
        if (assigneeIds?.Length > 0 || unassigned == true)
        {
            var ids = assigneeIds ?? Array.Empty<int>();
            query = query.Where(t =>
                (unassigned == true && t.AssignedToId == null) ||
                (ids.Length > 0 && t.AssignedToId != null && ids.Contains(t.AssignedToId.Value))
            );
        }
        if (requesterIds?.Length > 0) query = query.Where(t => requesterIds.Contains(t.SubmittedById));
        if (unmatched  == true)       query = query.Where(t => t.SubmittedBy!.Email == "imported.ticket@servicesphere.local");
        if (!string.IsNullOrWhiteSpace(q))
        {
            q = q.Trim();
            query = query.Where(t =>
                t.Title.Contains(q) ||
                (t.Description != null && t.Description.Contains(q)) ||
                (t.ResolutionNotes != null && t.ResolutionNotes.Contains(q)));
        }

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

        ViewBag.SelectedStatuses     = statuses     ?? Array.Empty<TicketStatus>();
        ViewBag.SelectedCategories   = categories   ?? Array.Empty<TicketCategory>();
        ViewBag.SelectedPriorities   = priorities   ?? Array.Empty<TicketPriority>();
        ViewBag.SelectedAssigneeIds  = assigneeIds  ?? Array.Empty<int>();
        ViewBag.SelectedRequesterIds = requesterIds ?? Array.Empty<int>();
        ViewBag.CurrentUnmatched  = unmatched;
        ViewBag.CurrentUnassigned = unassigned;
        ViewBag.CurrentEmployeeId = currentUserEmpId;
        ViewBag.CurrentQuery = q ?? "";
        ViewBag.SortBy    = sortBy;
        ViewBag.SortDir   = sortDir;
        ViewBag.Page      = page;
        ViewBag.PageSize  = pageSize;
        ViewBag.TotalCount = totalCount;
        ViewBag.TotalPages = totalPages;

        ViewBag.ActiveViewId   = activeView?.Id;
        ViewBag.ActiveViewName = activeView?.Name;

        // When serving only the table partial, skip dropdown data (saves 3 DB queries)
        if (partial)
            return PartialView("_TicketsTable", tickets);

        // Data for filter bar assignee dropdown + bulk reassign dropdown (full-page load only).
        // Use portal-user role membership (not department) so that any IT admin/agent with
        // a portal account appears, regardless of their employee Department field.
        var itRoleIds = await _context.Roles
            .Where(r => r.Name != "End User")
            .Select(r => r.Id).ToListAsync();
        var assignableEmpIds = await _context.PortalUsers
            .Where(u => u.IsActive && u.EmployeeId != null
                     && u.RoleId != null && itRoleIds.Contains(u.RoleId.Value))
            .Select(u => u.EmployeeId!.Value)
            .Distinct().ToListAsync();
        var assignableStaff = await _context.Employees
            .Where(e => e.IsActive && assignableEmpIds.Contains(e.Id))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName })
            .ToListAsync();

        ViewBag.AssignableStaff = assignableStaff;
        // ITStaffJson is used by the bulk-reassign JS dropdown — same list, serialised
        ViewBag.ITStaffJson = System.Text.Json.JsonSerializer.Serialize(
            assignableStaff.Select(e => new { id = e.Id, name = e.Name }));

        // Distinct set of employees who have submitted at least one ticket (for Requester filter)
        var requesterEmpIds = await _context.Tickets
            .Select(t => t.SubmittedById).Distinct().ToListAsync();
        ViewBag.Requesters = await _context.Employees
            .Where(e => requesterEmpIds.Contains(e.Id))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName })
            .ToListAsync();

        ViewBag.Branches = await _context.Branches
            .Where(b => b.IsActive).OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name }).ToListAsync();
        ViewBag.Groups = await _context.UserGroups
            .Where(g => g.IsActive).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name }).ToListAsync();
        ViewBag.Departments = await _context.Employees
            .Where(e => e.Department != null && e.Department != "")
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();

        // ── SLA risk badges ──────────────────────────────────────────────────
        await _slaRisk.EnsureBaselinesBuiltAsync();
        ViewBag.SlaRisks = tickets
            .Where(t => t.Status != TicketStatus.Resolved
                     && t.Status != TicketStatus.Closed
                     && t.Status != TicketStatus.Cancelled)
            .ToDictionary(
                t => t.Id,
                t => _slaRisk.GetRisk((int)t.Category, (int)t.Priority, t.CreatedDate));

        return View(tickets);
    }

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
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

    [HttpGet]
    public async Task<IActionResult> DetailsModal(int? id)
    {
        if (id == null) return NotFound();

        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .Include(t => t.CompanyService)
            .Include(t => t.SubCategory)
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();
        return PartialView("_TicketDetailsModalContent", ticket);
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
            ticket.DueDate ??= SlaPolicy.CalculateDueDate(ticket.Priority, ticket.CreatedDate);

            // Auto-assign via assignment rules when no assignee was explicitly chosen.
            if (ticket.AssignedToId == null)
            {
                var submitter = await _context.Employees.FindAsync(ticket.SubmittedById);
                ticket.AssignedToId = await _assignmentResolver.ResolveAsync(
                    ticket.Category, submitter?.BranchId, defaultAssigneeId: null);
            }

            _context.Add(ticket);
            await _context.SaveChangesAsync();

            // Run AI triage in the background (fire-and-forget — safe: AiTriageService owns its scope)
            _ = _aiTriage.TriageAndSaveAsync(ticket.Id, ticket.Title, ticket.Description, ticket.BranchId);

            // Notify assigned agent (fire-and-forget)
            if (ticket.AssignedToId != null)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var t = await _context.Tickets.Include(x => x.AssignedTo).FirstOrDefaultAsync(x => x.Id == ticket.Id);
                        if (t?.AssignedTo != null) await _emailService.NotifyTicketAssigned(t);
                    }
                    catch { /* email errors must not break ticket creation */ }
                });

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

        // Load pending AI recommendation (if any) so the view can render the triage card
        var pendingRec = await _context.AiRecommendations
            .Include(r => r.SuggestedAssignee)
            .Where(r => r.TicketId == id && r.Status == "Pending")
            .OrderByDescending(r => r.CreatedDate)
            .FirstOrDefaultAsync();
        ViewBag.AiRecommendation = pendingRec;

        // Load Ollama feature flag so the view can show/hide the "Draft AI Reply" button
        var ollamaEnabled = await _context.AppSettings
            .Where(s => s.Key == "OllamaEnabled")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
        ViewBag.OllamaEnabled = string.Equals(ollamaEnabled, "true", StringComparison.OrdinalIgnoreCase);

        // SLA risk for this specific ticket
        await _slaRisk.EnsureBaselinesBuiltAsync();
        ViewBag.SlaRisk           = _slaRisk.GetRisk((int)ticket.Category, (int)ticket.Priority, ticket.CreatedDate);
        ViewBag.SlaThresholdLabel = _slaRisk.GetThresholdLabel((int)ticket.Category, (int)ticket.Priority);

        // Note count for the "Summarize Thread" button visibility check
        ViewBag.NoteCount = ticket.Notes?.Count ?? 0;

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
                if (existing.UserGroupId != ticket.UserGroupId)
                {
                    var oldGroup = existing.UserGroupId.HasValue ? (await _context.UserGroups.FindAsync(existing.UserGroupId))?.Name ?? "Unassigned" : "Unassigned";
                    var newGroup = ticket.UserGroupId.HasValue ? (await _context.UserGroups.FindAsync(ticket.UserGroupId))?.Name ?? "Unassigned" : "Unassigned";
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Group Assignment", OldValue = oldGroup, NewValue = newGroup });
                }
                if (existing.Category != ticket.Category)
                    histories.Add(new TicketHistory { TicketId = id, ChangedBy = changedBy, FieldName = "Category", OldValue = existing.Category.ToString(), NewValue = ticket.Category.ToString() });
                if (histories.Any())
                    _context.TicketHistory.AddRange(histories);
            }

            var prevAssigneeId = existing?.AssignedToId;
            _context.Update(ticket);
            await _context.SaveChangesAsync();

            // Notify new assignee if assignment changed
            if (ticket.AssignedToId != null && ticket.AssignedToId != prevAssigneeId)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var t = await _context.Tickets.Include(x => x.AssignedTo).FirstOrDefaultAsync(x => x.Id == id);
                        if (t?.AssignedTo != null) await _emailService.NotifyTicketAssigned(t);
                    }
                    catch { }
                });

            return RedirectToAction(nameof(Index));
        }
        PopulateDropdowns(ticket);
        return View(ticket);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickUpdate(int id, string field, string value)
    {
        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        var changedBy    = User.Identity?.Name ?? "Unknown";
        var notifyAssignment   = false;
        var notifyStatusChange = false;
        TicketHistory? historyEntry = null;

        if (field == "status")
        {
            if (!Enum.TryParse<TicketStatus>(value, out var newStatus)) return BadRequest();
            var oldStatus = ticket.Status;
            ticket.Status = newStatus;
            if (newStatus == TicketStatus.Resolved && ticket.ResolvedDate == null)
                ticket.ResolvedDate = DateTime.UtcNow;
            if (newStatus == TicketStatus.Closed && ticket.ClosedDate == null)
                ticket.ClosedDate = DateTime.UtcNow;

            if (oldStatus != newStatus)
            {
                historyEntry = new TicketHistory
                {
                    TicketId    = id,
                    ChangedBy   = changedBy,
                    FieldName   = "Status",
                    OldValue    = oldStatus.ToString(),
                    NewValue    = newStatus.ToString(),
                    ChangedDate = DateTime.UtcNow,
                };
                notifyStatusChange = true;
            }
        }
        else if (field == "assignee")
        {
            var prevAssigneeId = ticket.AssignedToId;
            ticket.AssignedToId = string.IsNullOrEmpty(value) || value == "0"
                ? null
                : int.TryParse(value, out var empId) ? empId : (int?)null;

            if (ticket.AssignedToId != prevAssigneeId)
            {
                var oldName = prevAssigneeId.HasValue
                    ? (await _context.Employees.FindAsync(prevAssigneeId.Value))?.FullName ?? "Unknown"
                    : "Unassigned";
                var newName = ticket.AssignedToId.HasValue
                    ? (await _context.Employees.FindAsync(ticket.AssignedToId.Value))?.FullName ?? "Unknown"
                    : "Unassigned";

                historyEntry = new TicketHistory
                {
                    TicketId    = id,
                    ChangedBy   = changedBy,
                    FieldName   = "Assigned To",
                    OldValue    = oldName,
                    NewValue    = newName,
                    ChangedDate = DateTime.UtcNow,
                };

                if (ticket.AssignedToId != null)
                    notifyAssignment = true;
            }
        }
        else
        {
            return BadRequest();
        }

        ticket.UpdatedDate = DateTime.UtcNow;
        if (historyEntry != null)
            _context.TicketHistory.Add(historyEntry);
        await _context.SaveChangesAsync();

        string? assigneeName = null;
        if (ticket.AssignedToId.HasValue)
        {
            var emp = await _context.Employees.FindAsync(ticket.AssignedToId.Value);
            assigneeName = emp != null ? emp.FirstName + " " + emp.LastName : null;
        }

        if (notifyAssignment)
            _ = Task.Run(async () =>
            {
                try
                {
                    var t = await _context.Tickets.Include(x => x.AssignedTo).FirstOrDefaultAsync(x => x.Id == id);
                    if (t?.AssignedTo != null) await _emailService.NotifyTicketAssigned(t);
                }
                catch { }
            });

        if (notifyStatusChange && ticket.SubmittedBy?.Email != null)
            _ = Task.Run(async () =>
            {
                try
                {
                    var t = await _context.Tickets.Include(x => x.SubmittedBy).FirstOrDefaultAsync(x => x.Id == id);
                    if (t?.SubmittedBy?.Email != null)
                        await _emailService.NotifyTicketUpdated(t, t.SubmittedBy.Email,
                            $"Status changed to: {t.Status}");
                }
                catch { }
            });

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
            else if (bulkAction == "delete")
            {
                _context.Tickets.Remove(ticket);
            }
        }

        if (histories.Any()) _context.TicketHistory.AddRange(histories);
        await _context.SaveChangesAsync();

        TempData["Success"] = bulkAction == "delete"
            ? $"{tickets.Count} ticket(s) deleted."
            : $"{tickets.Count} ticket(s) updated.";
        return RedirectToAction(nameof(Index));
    }

    // POST: Tickets/MergeTickets — merge tickets together. By default the lowest ID becomes the
    // primary; pass primaryId explicitly when merging a single selected ticket into a chosen target.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MergeTickets(int[] selectedIds, int? primaryId = null)
    {
        if (selectedIds == null || selectedIds.Length == 0)
        {
            TempData["Error"] = "Select at least 1 ticket to merge.";
            return RedirectToAction(nameof(Index));
        }

        // Build the full id list. If a primaryId was supplied (e.g. single-selection merge),
        // include it so the union has at least 2 distinct ids.
        var idSet = new HashSet<int>(selectedIds);
        if (primaryId.HasValue) idSet.Add(primaryId.Value);

        if (idSet.Count < 2)
        {
            TempData["Error"] = "Select at least 2 tickets to merge, or specify a target ticket ID.";
            return RedirectToAction(nameof(Index));
        }

        // Determine primary: explicit primaryId wins, otherwise lowest id.
        int resolvedPrimaryId = primaryId ?? idSet.Min();
        if (!idSet.Contains(resolvedPrimaryId))
        {
            TempData["Error"] = "Target ticket is not part of the selection.";
            return RedirectToAction(nameof(Index));
        }
        var secondaryIds = idSet.Where(id => id != resolvedPrimaryId).ToArray();
        var changedBy   = User.Identity?.Name ?? "Agent";
        var now         = DateTime.UtcNow;

        var primary = await _context.Tickets.FindAsync(resolvedPrimaryId);
        if (primary == null)
        {
            TempData["Error"] = "Primary ticket not found.";
            return RedirectToAction(nameof(Index));
        }

        var secondaries = await _context.Tickets
            .Include(t => t.Notes)
            .Where(t => secondaryIds.Contains(t.Id))
            .ToListAsync();

        if (!secondaries.Any())
        {
            TempData["Error"] = "Secondary tickets not found.";
            return RedirectToAction(nameof(Index));
        }

        var secondaryList = string.Join(", #", secondaries.Select(t => t.Id));

        // Merge summary note on the primary ticket
        _context.TicketNotes.Add(new TicketNote
        {
            TicketId   = resolvedPrimaryId,
            AuthorName = changedBy,
            Content    = $"Tickets #{secondaryList} were merged into this ticket by {changedBy}.",
            CreatedDate = now, Source = "Agent", IsInternal = true
        });

        foreach (var secondary in secondaries)
        {
            // Copy all public notes from secondary → primary (prefixed for traceability)
            foreach (var note in secondary.Notes.Where(n => !n.IsInternal))
            {
                _context.TicketNotes.Add(new TicketNote
                {
                    TicketId    = resolvedPrimaryId,
                    AuthorName  = note.AuthorName,
                    AuthorEmail = note.AuthorEmail,
                    Content     = $"[Merged from #{secondary.Id}] {note.Content}",
                    CreatedDate = note.CreatedDate,
                    Source      = note.Source,
                    IsInternal  = false
                });
            }

            // Internal merge note on the secondary ticket
            _context.TicketNotes.Add(new TicketNote
            {
                TicketId   = secondary.Id,
                AuthorName = changedBy,
                Content    = $"This ticket was merged into #{resolvedPrimaryId} by {changedBy}. Refer to #{resolvedPrimaryId} for all further updates.",
                CreatedDate = now, Source = "Agent", IsInternal = true
            });

            _context.TicketHistory.Add(new TicketHistory
            {
                TicketId = secondary.Id, ChangedBy = changedBy,
                FieldName = "Status",
                OldValue  = secondary.Status.ToString(),
                NewValue  = $"Closed (Merged into #{resolvedPrimaryId})"
            });

            secondary.Status     = TicketStatus.Closed;
            secondary.ClosedDate ??= now;
            secondary.UpdatedDate = now;
        }

        primary.UpdatedDate = now;
        await _context.SaveChangesAsync();

        TempData["Success"] = $"{secondaries.Count} ticket(s) merged into #{resolvedPrimaryId}.";
        return RedirectToAction(nameof(Edit), new { id = resolvedPrimaryId });
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

    // ── Ticket Escalation ────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EscalateTicket(int id, string reason)
    {
        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (ticket.IsEscalated)
            return Json(new { success = false, error = "Ticket is already escalated." });

        // Resolve the escalating user's employee record for EscalatedById
        var currentUser = User.Identity?.Name;
        var escalatedByEmp = currentUser != null
            ? await _context.Employees.FirstOrDefaultAsync(e => e.Email == currentUser)
            : null;

        var oldPriority = ticket.Priority;
        // Bump priority: Medium→High, Low→High, High→Critical; Critical stays Critical
        ticket.Priority = ticket.Priority switch
        {
            TicketPriority.Low    => TicketPriority.High,
            TicketPriority.Medium => TicketPriority.High,
            TicketPriority.High   => TicketPriority.Critical,
            _                     => TicketPriority.Critical,
        };
        ticket.IsEscalated      = true;
        ticket.EscalationReason = reason?.Trim();
        ticket.EscalatedAt      = DateTime.UtcNow;
        ticket.EscalatedById    = escalatedByEmp?.Id;
        ticket.UpdatedDate      = DateTime.UtcNow;

        // Log to audit trail
        _context.TicketHistory.Add(new TicketHistory
        {
            TicketId    = id,
            ChangedBy   = currentUser ?? "Unknown",
            FieldName   = "Escalation",
            OldValue    = $"Priority: {oldPriority}",
            NewValue    = $"ESCALATED — Priority: {ticket.Priority} — Reason: {ticket.EscalationReason ?? "None"}",
            ChangedDate = DateTime.UtcNow,
        });

        await _context.SaveChangesAsync();

        // Fire-and-forget notification
        _ = Task.Run(async () =>
        {
            try
            {
                var t = await _context.Tickets
                    .Include(x => x.SubmittedBy)
                    .Include(x => x.AssignedTo)
                    .FirstOrDefaultAsync(x => x.Id == id);
                if (t == null) return;

                var msg = $"Ticket has been escalated to {t.Priority} priority. Reason: {t.EscalationReason ?? "Not specified"}";

                // Notify assignee
                if (t.AssignedTo?.Email != null)
                    await _emailService.NotifyTicketUpdated(t, t.AssignedTo.Email, msg);

                // Notify requester
                if (t.SubmittedBy?.Email != null)
                    await _emailService.NotifyTicketUpdated(t, t.SubmittedBy.Email, msg);
            }
            catch { }
        });

        return Json(new { success = true, priority = ticket.Priority.ToString() });
    }

    // ── Ticket Resolution ─────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveTicket(int id, string resolutionType, string? resolutionNotes, string? targetStatus)
    {
        var ticket = await _context.Tickets
            .Include(t => t.SubmittedBy)
            .Include(t => t.AssignedTo)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (string.IsNullOrWhiteSpace(resolutionType))
            return Json(new { success = false, error = "Resolution type is required." });

        var changedBy = User.Identity?.Name ?? "Unknown";
        var oldStatus = ticket.Status;

        if (!Enum.TryParse<TicketStatus>(targetStatus ?? "Resolved", out var newStatus))
            newStatus = TicketStatus.Resolved;

        ticket.Status         = newStatus;
        ticket.ResolutionType = resolutionType.Trim();
        ticket.UpdatedDate    = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(resolutionNotes))
            ticket.ResolutionNotes = resolutionNotes.Trim();

        if (ticket.ResolvedDate == null)
            ticket.ResolvedDate = DateTime.UtcNow;
        if (newStatus == TicketStatus.Closed && ticket.ClosedDate == null)
            ticket.ClosedDate = DateTime.UtcNow;

        _context.TicketHistory.Add(new TicketHistory
        {
            TicketId    = id,
            ChangedBy   = changedBy,
            FieldName   = "Status",
            OldValue    = oldStatus.ToString(),
            NewValue    = newStatus.ToString(),
            ChangedDate = DateTime.UtcNow,
        });
        _context.TicketHistory.Add(new TicketHistory
        {
            TicketId    = id,
            ChangedBy   = changedBy,
            FieldName   = "Resolution Type",
            OldValue    = string.Empty,
            NewValue    = resolutionType.Trim(),
            ChangedDate = DateTime.UtcNow,
        });
        if (!string.IsNullOrWhiteSpace(resolutionNotes))
        {
            _context.TicketHistory.Add(new TicketHistory
            {
                TicketId    = id,
                ChangedBy   = changedBy,
                FieldName   = "Resolution Notes",
                OldValue    = string.Empty,
                NewValue    = resolutionNotes.Trim(),
                ChangedDate = DateTime.UtcNow,
            });
        }

        await _context.SaveChangesAsync();

        // Fire status-change notification (resolution notes are internal — not emailed)
        _ = Task.Run(async () =>
        {
            try
            {
                var t = ticket; // capture for closure
                var msg = $"Status changed to {newStatus}: {resolutionType.Trim()}";
                if (t.SubmittedBy?.Email != null)
                    await _emailService.NotifyTicketUpdated(t, t.SubmittedBy.Email, msg);
            }
            catch { /* swallow — notification is non-critical */ }
        });

        return Json(new { success = true });
    }

    // ── AI Triage endpoints ──────────────────────────────────────────────────

    /// <summary>
    /// Approve an AI recommendation: apply the suggested category / priority / assignee to the ticket
    /// and mark the recommendation as Approved.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAiRecommendation(int id, int recommendationId)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        var rec    = await _context.AiRecommendations
            .FirstOrDefaultAsync(r => r.Id == recommendationId && r.TicketId == id && r.Status == "Pending");

        if (ticket == null || rec == null) return NotFound();

        // Apply suggestions
        if (rec.SuggestedCategory.HasValue)
            ticket.Category = (ServiceDesk.Core.Enums.TicketCategory)rec.SuggestedCategory.Value;
        if (rec.SuggestedPriority.HasValue)
            ticket.Priority = (ServiceDesk.Core.Enums.TicketPriority)rec.SuggestedPriority.Value;
        if (rec.SuggestedAssigneeId.HasValue)
            ticket.AssignedToId = rec.SuggestedAssigneeId.Value;

        ticket.UpdatedDate = DateTime.UtcNow;

        rec.Status       = "Approved";
        rec.ReviewedDate = DateTime.UtcNow;
        rec.ReviewedBy   = User.Identity?.Name ?? "Agent";

        _context.TicketHistory.Add(new TicketHistory
        {
            TicketId    = id,
            ChangedBy   = $"AI Triage (approved by {rec.ReviewedBy})",
            FieldName   = "AI Recommendation",
            OldValue    = null,
            NewValue    = $"Category={ticket.Category}, Priority={ticket.Priority}"
        });

        await _context.SaveChangesAsync();

        TempData["Success"] = "AI suggestion applied.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Dismiss an AI recommendation without applying any changes.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissAiRecommendation(int id, int recommendationId)
    {
        var rec = await _context.AiRecommendations
            .FirstOrDefaultAsync(r => r.Id == recommendationId && r.TicketId == id && r.Status == "Pending");

        if (rec == null) return NotFound();

        rec.Status       = "Dismissed";
        rec.ReviewedDate = DateTime.UtcNow;
        rec.ReviewedBy   = User.Identity?.Name ?? "Agent";

        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Retriage the ticket using the current ML.NET model and save a new Pending recommendation.
    /// Used by the "Re-run AI" button when the AI feature flag is enabled.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetriageTicket(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        await _aiTriage.TriageAndSaveAsync(ticket.Id, ticket.Title, ticket.Description, ticket.BranchId);

        TempData["Success"] = "AI triage re-run complete.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Calls Ollama to generate a draft reply and returns it as JSON.
    /// The Edit page JS inserts it into the reply text box.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DraftAiReply(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        var draft = await _ollama.DraftReplyAsync(
            ticket.Title, ticket.Description, ticket.ResolutionNotes);

        if (string.IsNullOrWhiteSpace(draft))
            return Json(new { success = false, error = "Ollama is not available or returned an empty response." });

        return Json(new { success = true, draft });
    }

    /// <summary>
    /// Promotes a resolved ticket to a KB article suggestion — pre-fills the article
    /// with the ticket title, description, and resolution notes. Redirects to KB Create.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuggestToKb(int id)
    {
        var ticket = await _context.Tickets.FindAsync(id);
        if (ticket == null) return NotFound();

        // Store pre-fill data in TempData so the KB Create action can read it
        TempData["KbSuggest_Title"]       = ticket.Title;
        TempData["KbSuggest_Problem"]     = ticket.Description;
        TempData["KbSuggest_Solution"]    = ticket.ResolutionNotes ?? string.Empty;
        TempData["KbSuggest_Category"]    = (int)ticket.Category;
        TempData["KbSuggest_SourceId"]    = ticket.Id;

        TempData["Success"] = "Ticket pre-filled into a new Knowledge Base article. Review and publish below.";
        return RedirectToAction("Create", "KnowledgeBase");
    }

    /// <summary>
    /// Returns a JSON list of tickets similar to the given ticket (TF-IDF cosine similarity).
    /// Called via AJAX from the ticket Edit page after load.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SimilarTickets(int id)
    {
        var ticket = await _context.Tickets
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.Title, t.Description })
            .FirstOrDefaultAsync();

        if (ticket == null) return NotFound();

        var similar = await _similarity.FindSimilarAsync(
            ticket.Title, ticket.Description ?? string.Empty, excludeTicketId: id, topN: 6);

        return Json(similar.Select(s => new
        {
            ticketId  = s.TicketId,
            title     = s.Title,
            status    = s.Status.ToString(),
            priority  = s.Priority.ToString(),
            scorePct  = s.ScorePct
        }));
    }

    /// <summary>
    /// Calls Ollama to summarize the entire ticket thread (description + all notes).
    /// Returns { success, summary } JSON.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SummarizeThread(int id)
    {
        var ticket = await _context.Tickets
            .Include(t => t.Notes.OrderBy(n => n.CreatedDate))
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        var noteContents = ticket.Notes
            .Where(n => !string.IsNullOrWhiteSpace(n.Content))
            .Select(n => n.Content!);

        var summary = await _ollama.SummarizeThreadAsync(
            ticket.Title, ticket.Description ?? string.Empty, noteContents);

        if (string.IsNullOrWhiteSpace(summary))
            return Json(new { success = false, error = "Ollama returned an empty response. Ensure Ollama is running and configured in Settings." });

        return Json(new { success = true, summary });
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
        // Use RFC-4180-aware reader so multi-line quoted fields don't split into phantom rows
        var headerLine = await ReadCsvRecordAsync(reader);
        if (string.IsNullOrWhiteSpace(headerLine)) return new List<ImportTicketRow>();

        // Detect delimiter by comparing count of tabs vs commas in header
        int tabCount   = headerLine.Count(c => c == '\t');
        int commaCount = headerLine.Count(c => c == ',');
        var delimiter  = tabCount > commaCount ? '\t' : ',';
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
        string? record;

        // ReadCsvRecordAsync handles RFC-4180 multi-line quoted fields
        while ((record = await ReadCsvRecordAsync(reader)) != null)
        {
            rowNum++;
            if (string.IsNullOrWhiteSpace(record)) continue;

            var cols = SplitCsvLine(record, delimiter);
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

    /// <summary>
    /// Reads one logical CSV/TSV record from <paramref name="reader"/>.
    /// Handles RFC-4180 multi-line quoted fields: if a line ends while inside a
    /// quoted field the reader keeps consuming physical lines until the quote closes,
    /// joining them with <c>\n</c> so the embedded newline is preserved in the value.
    /// Returns <c>null</c> when the stream is exhausted.
    /// </summary>
    private static async Task<string?> ReadCsvRecordAsync(System.IO.StreamReader reader)
    {
        var sb = new System.Text.StringBuilder();
        bool firstLine = true;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!firstLine) sb.Append('\n');
            sb.Append(line);
            firstLine = false;

            // Determine whether we are still inside a quoted field by scanning
            // the accumulated buffer for unescaped quote characters.
            bool inQuotes = false;
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '"')
                {
                    // "" inside a quoted field is an escaped literal quote
                    if (inQuotes && i + 1 < sb.Length && sb[i + 1] == '"')
                        i++;          // skip the second quote of the escape pair
                    else
                        inQuotes = !inQuotes;
                }
            }

            if (!inQuotes) break;  // record is complete — stop reading physical lines
        }

        return firstLine ? null : sb.ToString();
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
            // System views (null owner) first, then personal, both sorted by name
            .OrderBy(v => v.OwnerPortalUserId == null ? 0 : 1)
            .ThenBy(v => v.Name)
            .Select(v => new {
                v.Id, v.Name, v.IsDefault, v.IsShared,
                v.FilterStatuses, v.FilterCategories, v.FilterPriorities,
                v.FilterStatus, v.FilterCategory, v.FilterPriority,
                v.FilterBranchId, v.FilterBranchIds,
                v.FilterDepartment, v.FilterDepartments,
                v.FilterGroupId,
                v.FilterAssignedToMe, v.FilterUnassignedOnly, v.FilterUnmatchedOnly,
                v.SortBy, v.SortDir, v.PageSize,
                isOwner      = v.OwnerPortalUserId == userId,
                isSystemView = v.OwnerPortalUserId == null
            })
            .ToListAsync();
        return Json(views);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveView(
        string name, bool isShared,
        string[]? filterStatuses, string[]? filterCategories, string[]? filterPriorities,
        int[]? filterBranchIds, string[]? filterDepartments, int? filterGroupId,
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
            FilterStatuses       = filterStatuses   is { Length: > 0 } ? string.Join(",", filterStatuses)   : null,
            FilterCategories     = filterCategories is { Length: > 0 } ? string.Join(",", filterCategories) : null,
            FilterPriorities     = filterPriorities is { Length: > 0 } ? string.Join(",", filterPriorities) : null,
            FilterBranchIds      = filterBranchIds  is { Length: > 0 } ? string.Join(",", filterBranchIds)  : null,
            FilterDepartments    = filterDepartments is { Length: > 0 } ? string.Join(",", filterDepartments.Select(d => d.Trim()).Where(d => d.Length > 0)) : null,
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
        string[]? filterStatuses, string[]? filterCategories, string[]? filterPriorities,
        int[]? filterBranchIds, string[]? filterDepartments, int? filterGroupId,
        bool filterAssignedToMe, bool filterUnassignedOnly, bool filterUnmatchedOnly,
        string sortBy = "id", string sortDir = "desc", int pageSize = 25)
    {
        var userId = CurrentPortalUserId();
        var view = await _context.SavedTicketViews
            .FirstOrDefaultAsync(v => v.Id == id && v.OwnerPortalUserId == userId);
        if (view == null) return NotFound();

        view.Name                 = name.Trim();
        view.IsShared             = isShared;
        view.FilterStatuses       = filterStatuses   is { Length: > 0 } ? string.Join(",", filterStatuses)   : null;
        view.FilterCategories     = filterCategories is { Length: > 0 } ? string.Join(",", filterCategories) : null;
        view.FilterPriorities     = filterPriorities is { Length: > 0 } ? string.Join(",", filterPriorities) : null;
        // Clear legacy single-value fields; new multi-select fields take over
        view.FilterStatus         = null;
        view.FilterCategory       = null;
        view.FilterPriority       = null;
        view.FilterBranchId       = null;
        view.FilterDepartment     = null;
        view.FilterBranchIds      = filterBranchIds  is { Length: > 0 } ? string.Join(",", filterBranchIds)  : null;
        view.FilterDepartments    = filterDepartments is { Length: > 0 } ? string.Join(",", filterDepartments.Select(d => d.Trim()).Where(d => d.Length > 0)) : null;
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

    /// <summary>
    /// Builds a redirect URL for a saved view, handling multi-select for status, category, and priority.
    /// </summary>
    private static string BuildViewUrl(SavedTicketView v)
    {
        var sb = new System.Text.StringBuilder(
            $"/Tickets?viewId={v.Id}&sortBy={Uri.EscapeDataString(v.SortBy)}&sortDir={v.SortDir}&pageSize={v.PageSize}");

        // Multi-status (FilterStatuses takes priority over legacy FilterStatus)
        if (!string.IsNullOrEmpty(v.FilterStatuses))
        {
            foreach (var s in v.FilterStatuses.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"&statuses={Uri.EscapeDataString(s.Trim())}");
        }
        else if (v.FilterStatus != null)
        {
            sb.Append($"&statuses={v.FilterStatus}");
        }

        // Multi-category (FilterCategories takes priority over legacy FilterCategory)
        if (!string.IsNullOrEmpty(v.FilterCategories))
        {
            foreach (var c in v.FilterCategories.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"&categories={Uri.EscapeDataString(c.Trim())}");
        }
        else if (v.FilterCategory != null)
        {
            sb.Append($"&categories={v.FilterCategory}");
        }

        // Multi-priority (FilterPriorities takes priority over legacy FilterPriority)
        if (!string.IsNullOrEmpty(v.FilterPriorities))
        {
            foreach (var p in v.FilterPriorities.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"&priorities={Uri.EscapeDataString(p.Trim())}");
        }
        else if (v.FilterPriority != null)
        {
            sb.Append($"&priorities={v.FilterPriority}");
        }

        // Branch multi-select
        if (!string.IsNullOrEmpty(v.FilterBranchIds))
        {
            foreach (var b in v.FilterBranchIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"&branchIds={b.Trim()}");
        }
        else if (v.FilterBranchId != null)
        {
            sb.Append($"&branchIds={v.FilterBranchId}");
        }

        // Department multi-select
        if (!string.IsNullOrEmpty(v.FilterDepartments))
        {
            foreach (var d in v.FilterDepartments.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"&departments={Uri.EscapeDataString(d.Trim())}");
        }
        else if (!string.IsNullOrEmpty(v.FilterDepartment))
        {
            sb.Append($"&departments={Uri.EscapeDataString(v.FilterDepartment)}");
        }

        if (v.FilterAssignedToMe)     sb.Append("&assignedToMe=true");
        if (v.FilterUnmatchedOnly)    sb.Append("&unmatched=true");
        if (v.FilterUnassignedOnly)   sb.Append("&unassigned=true");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateDropdowns(Ticket? ticket = null)
    {
        ViewBag.Employees = new SelectList(
            _context.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.SubmittedById);

        // Assignable IT staff = employees linked to a portal user whose role is NOT "End User".
        // (Department-based filtering excluded admins like Jose/Admin who aren't in the IT dept,
        // and incorrectly included end users like Kolby/Chris whose dept is IT.)
        var itRoleIds = _context.Roles
            .Where(r => r.Name != "End User")
            .Select(r => r.Id).ToList();
        var assignableEmpIds = _context.PortalUsers
            .Where(u => u.IsActive && u.EmployeeId != null
                     && u.RoleId != null && itRoleIds.Contains(u.RoleId.Value))
            .Select(u => u.EmployeeId!.Value)
            .Distinct().ToList();
        ViewBag.ITStaff = new SelectList(
            _context.Employees.Where(e => e.IsActive && assignableEmpIds.Contains(e.Id))
                .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", ticket?.AssignedToId);

        ViewBag.UserGroups = new SelectList(
            _context.UserGroups.Where(g => g.IsActive).OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name }),
            "Id", "Name", ticket?.UserGroupId);

        ViewBag.Services = new SelectList(
            _context.CompanyServices.Where(s => s.Status == ServiceStatus.Active).OrderBy(s => s.Name),
            "Id", "Name", ticket?.CompanyServiceId);
    }
}
