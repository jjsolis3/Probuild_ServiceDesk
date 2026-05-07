using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class AdminPayrollController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public AdminPayrollController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    // GET /AdminPayroll
    public async Task<IActionResult> Index(string? status, int? contractorId)
    {
        // Summary stats from ALL receipts (unfiltered) for KPI tiles
        var allReceipts = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .ToListAsync();

        var now = DateTime.UtcNow;
        ViewBag.CountSubmitted     = allReceipts.Count(r => r.Status == "Submitted");
        ViewBag.CountApproved      = allReceipts.Count(r => r.Status == "Approved");
        ViewBag.AmountSubmitted    = allReceipts.Where(r => r.Status == "Submitted").Sum(r => r.TotalAmount);
        ViewBag.AmountApproved     = allReceipts.Where(r => r.Status == "Approved").Sum(r => r.TotalAmount);
        ViewBag.CountPaidMonth     = allReceipts.Count(r => r.Status == "Paid" && r.PaidDate.HasValue
                                         && r.PaidDate.Value.Year == now.Year && r.PaidDate.Value.Month == now.Month);
        ViewBag.AmountPaidMonth    = allReceipts.Where(r => r.Status == "Paid" && r.PaidDate.HasValue
                                         && r.PaidDate.Value.Year == now.Year && r.PaidDate.Value.Month == now.Month)
                                         .Sum(r => r.TotalAmount);
        ViewBag.AmountPaidAllTime  = allReceipts.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount);

        // Apply filters
        IEnumerable<ServiceDesk.Core.Models.PayrollReceipt> receipts = allReceipts
            .OrderByDescending(r => r.CreatedDate);

        if (!string.IsNullOrEmpty(status))
            receipts = receipts.Where(r => r.Status == status);
        if (contractorId.HasValue)
            receipts = receipts.Where(r => r.ContractorId == contractorId.Value);

        var contractors = await _context.Employees
            .Where(e => e.IsContractor && e.IsActive)
            .OrderBy(e => e.LastName)
            .ToListAsync();

        ViewBag.Contractors        = contractors;
        ViewBag.FilterStatus       = status;
        ViewBag.FilterContractorId = contractorId;
        ViewData["Title"]          = "Contractor Payroll";
        return View(receipts.ToList());
    }

    // POST /AdminPayroll/Approve/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        var receipt = await _context.PayrollReceipts.FindAsync(id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Submitted")
        {
            TempData["Error"] = "Only Submitted receipts can be approved.";
            return RedirectToAction(nameof(Index));
        }

        var approverEmail = User.Identity?.Name;
        var approver = approverEmail != null
            ? await _context.PortalUsers
                .Include(u => u.Employee)
                .FirstOrDefaultAsync(u => u.Email == approverEmail)
            : null;

        receipt.Status       = "Approved";
        receipt.ApprovedDate = DateTime.UtcNow;
        receipt.ApprovedById = approver?.EmployeeId;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Receipt approved.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/MarkPaid/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id)
    {
        var receipt = await _context.PayrollReceipts.FindAsync(id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Approved")
        {
            TempData["Error"] = "Only Approved receipts can be marked as Paid.";
            return RedirectToAction(nameof(Index));
        }

        receipt.Status   = "Paid";
        receipt.PaidDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Receipt marked as Paid.";
        return RedirectToAction(nameof(Index));
    }
}
