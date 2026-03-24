using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

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
}
