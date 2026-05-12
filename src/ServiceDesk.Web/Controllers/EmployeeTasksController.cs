using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class EmployeeTasksController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public EmployeeTasksController(ServiceDeskDbContext context)
        => _context = context;

    // ── Template management (Admin) ────────────────────────────────────────
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Templates()
    {
        var templates = await _context.EmployeeTaskTemplates
            .OrderBy(t => t.TaskType).ThenBy(t => t.SortOrder).ThenBy(t => t.Title)
            .ToListAsync();
        return View(templates);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTemplate(EmployeeTaskTemplate template)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Template could not be saved. Check required fields.";
            return RedirectToAction(nameof(Templates));
        }
        template.CreatedDate = DateTime.UtcNow;
        template.UpdatedDate = DateTime.UtcNow;
        _context.EmployeeTaskTemplates.Add(template);
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Template '{template.Title}' created.";
        return RedirectToAction(nameof(Templates));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTemplate(int id, EmployeeTaskTemplate template)
    {
        var existing = await _context.EmployeeTaskTemplates.FindAsync(id);
        if (existing == null) return NotFound();
        existing.Title = template.Title;
        existing.Description = template.Description;
        existing.Category = template.Category;
        existing.TaskType = template.TaskType;
        existing.DefaultAssigneeEmail = template.DefaultAssigneeEmail;
        existing.DueInDays = template.DueInDays;
        existing.SortOrder = template.SortOrder;
        existing.IsActive = template.IsActive;
        existing.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["Success"] = "Template updated.";
        return RedirectToAction(nameof(Templates));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(int id)
    {
        var template = await _context.EmployeeTaskTemplates.FindAsync(id);
        if (template != null)
        {
            _context.EmployeeTaskTemplates.Remove(template);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Template deleted.";
        }
        return RedirectToAction(nameof(Templates));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SeedDefaultTemplates()
    {
        try
        {
            DbInitializer.SeedEmployeeTaskTemplates(_context);
            var count = _context.EmployeeTaskTemplates.Count();
            TempData["Success"] = count > 0
                ? $"Default templates loaded — {count} templates are now available."
                : "Templates already exist; no new templates were added.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to load default templates: {ex.Message}";
        }
        return RedirectToAction(nameof(Templates));
    }

    // ── Per-employee checklist actions ─────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyTemplate(int employeeId, EmployeeTaskType taskType)
    {
        var employee = await _context.Employees.FindAsync(employeeId);
        if (employee == null) return NotFound();

        var templates = await _context.EmployeeTaskTemplates
            .Where(t => t.IsActive && t.TaskType == taskType)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Title)
            .ToListAsync();

        if (!templates.Any())
        {
            TempData["Error"] = $"No active {taskType} templates configured. Add templates in Settings → Employee Checklists.";
            var errTab = taskType == EmployeeTaskType.Onboarding ? "onboarding" : "offboarding";
            return RedirectToAction("Details", "Employees", new { id = employeeId, tab = errTab });
        }

        var today = DateTime.UtcNow.Date;
        int created = 0;
        foreach (var template in templates)
        {
            // Skip if a task with the same title already exists for this employee and type
            bool exists = await _context.EmployeeTasks.AnyAsync(t =>
                t.EmployeeId == employeeId && t.TaskType == taskType && t.Title == template.Title);
            if (exists) continue;

            _context.EmployeeTasks.Add(new EmployeeTask
            {
                EmployeeId = employeeId,
                Title = template.Title,
                Description = template.Description,
                Category = template.Category,
                TaskType = taskType,
                Status = EmployeeTaskStatus.Pending,
                AssignedToEmail = template.DefaultAssigneeEmail,
                DueDate = template.DueInDays.HasValue ? today.AddDays(template.DueInDays.Value) : null,
                SortOrder = template.SortOrder,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });
            created++;
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = created > 0
            ? $"Applied {taskType} checklist — {created} task(s) added."
            : $"{taskType} checklist already applied.";
        var tab = taskType == EmployeeTaskType.Onboarding ? "onboarding" : "offboarding";
        return RedirectToAction("Details", "Employees", new { id = employeeId, tab });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTask(int employeeId, EmployeeTaskType taskType,
        string title, string? description, string? assignedToEmail, DateTime? dueDate)
    {
        var tab = TabFor(taskType);
        if (string.IsNullOrWhiteSpace(title))
        {
            TempData["Error"] = "Task title is required.";
            return RedirectToAction("Details", "Employees", new { id = employeeId, tab });
        }

        _context.EmployeeTasks.Add(new EmployeeTask
        {
            EmployeeId = employeeId,
            Title = title.Trim(),
            Description = description,
            TaskType = taskType,
            Status = EmployeeTaskStatus.Pending,
            AssignedToEmail = assignedToEmail,
            DueDate = dueDate,
            SortOrder = 999,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        TempData["Success"] = "Task added.";
        return RedirectToAction("Details", "Employees", new { id = employeeId, tab });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTaskStatus(int taskId, EmployeeTaskStatus status)
    {
        var task = await _context.EmployeeTasks.FindAsync(taskId);
        if (task == null) return NotFound();

        task.Status = status;
        task.UpdatedDate = DateTime.UtcNow;
        if (status == EmployeeTaskStatus.Completed)
        {
            task.CompletedDate = DateTime.UtcNow;
            task.CompletedByEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
        }
        else
        {
            task.CompletedDate = null;
            task.CompletedByEmail = null;
        }

        await _context.SaveChangesAsync();

        if (IsAjaxRequest())
            return Json(await BuildTaskUpdateResponseAsync(task.EmployeeId, task.TaskType, task));

        return RedirectToAction("Details", "Employees",
            new { id = task.EmployeeId, tab = TabFor(task.TaskType) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTask(int taskId)
    {
        var task = await _context.EmployeeTasks.FindAsync(taskId);
        if (task == null) return NotFound();
        var empId    = task.EmployeeId;
        var taskType = task.TaskType;

        _context.EmployeeTasks.Remove(task);
        await _context.SaveChangesAsync();

        if (IsAjaxRequest())
            return Json(await BuildTaskUpdateResponseAsync(empId, taskType, deletedTaskId: taskId));

        TempData["Success"] = "Task deleted.";
        return RedirectToAction("Details", "Employees",
            new { id = empId, tab = TabFor(taskType) });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string TabFor(EmployeeTaskType taskType)
        => taskType == EmployeeTaskType.Onboarding ? "onboarding" : "offboarding";

    private bool IsAjaxRequest()
        => string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest",
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the JSON payload returned to the checklist UI after a successful
    /// task update or delete. Includes the changed task's current state plus
    /// recomputed counts for the affected category and the overall task type
    /// so the page can refresh its badges without reloading.
    /// </summary>
    private async Task<object> BuildTaskUpdateResponseAsync(
        int employeeId, EmployeeTaskType taskType,
        EmployeeTask? updatedTask = null, int? deletedTaskId = null)
    {
        var tasksOfType = await _context.EmployeeTasks
            .Where(t => t.EmployeeId == employeeId && t.TaskType == taskType)
            .Select(t => new { t.Category, t.Status })
            .ToListAsync();

        int totalAll = tasksOfType.Count;
        int doneAll  = tasksOfType.Count(t => t.Status == EmployeeTaskStatus.Completed);

        var categoryCounts = tasksOfType
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "General" : t.Category)
            .Select(g => new {
                category = g.Key,
                pending  = g.Count(t => t.Status != EmployeeTaskStatus.Completed && t.Status != EmployeeTaskStatus.Skipped),
                done     = g.Count(t => t.Status == EmployeeTaskStatus.Completed),
                skipped  = g.Count(t => t.Status == EmployeeTaskStatus.Skipped),
                total    = g.Count()
            })
            .OrderBy(g => g.category)
            .ToList();

        object? taskJson = null;
        if (updatedTask != null)
        {
            taskJson = new {
                id                = updatedTask.Id,
                status            = updatedTask.Status.ToString(),
                category          = string.IsNullOrWhiteSpace(updatedTask.Category) ? "General" : updatedTask.Category,
                completedDate     = updatedTask.CompletedDate,
                completedByEmail  = updatedTask.CompletedByEmail
            };
        }

        return new
        {
            ok            = true,
            taskType      = taskType.ToString(),
            task          = taskJson,
            deletedTaskId,
            overall       = new { total = totalAll, done = doneAll },
            categoryCounts
        };
    }
}
