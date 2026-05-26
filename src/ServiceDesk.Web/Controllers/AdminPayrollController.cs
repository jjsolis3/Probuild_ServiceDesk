using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class AdminPayrollController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailService;

    public AdminPayrollController(ServiceDeskDbContext context, EmailNotificationService emailService)
    {
        _context = context;
        _emailService = emailService;
    }

    // GET /AdminPayroll
    public async Task<IActionResult> Index(string? status, int? contractorId, string? rateType)
    {
        // Summary stats from ALL receipts (unfiltered) for KPI tiles
        var allReceipts = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
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

        // Rate-type filter — narrows the list by which kind of hours the
        // receipt contains. "StandardOnly" means no Emergency hours posted;
        // "HasEmergency" surfaces every receipt with any Emergency hour at
        // all (useful for AP review). "HasRetainer" finds receipts that
        // included the monthly retainer payout.
        if (!string.IsNullOrEmpty(rateType))
        {
            receipts = rateType switch
            {
                "StandardOnly" => receipts.Where(r => r.TotalEmergencyHours == 0),
                "HasEmergency" => receipts.Where(r => r.TotalEmergencyHours > 0),
                "HasRetainer"  => receipts.Where(r => r.TotalRetainerAmountApplied > 0),
                _              => receipts
            };
        }

        var contractors = await _context.Employees
            .Where(e => e.IsContractor && e.IsActive)
            .OrderBy(e => e.LastName)
            .ToListAsync();

        ViewBag.Contractors        = contractors;
        ViewBag.FilterStatus       = status;
        ViewBag.FilterContractorId = contractorId;
        ViewBag.FilterRateType     = rateType;
        ViewData["Title"]          = "Contractor Payroll";
        return View(receipts.ToList());
    }

    // POST /AdminPayroll/Approve/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? approvalNote)
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
        receipt.ApprovalNote = string.IsNullOrWhiteSpace(approvalNote) ? null : approvalNote.Trim();

        await _context.SaveChangesAsync();

        try { await _emailService.NotifyReceiptApprovedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

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

        try { await _emailService.NotifyReceiptPaidAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        TempData["Success"] = "Receipt marked as Paid.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/Reject/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? rejectionNote)
    {
        var receipt = await _context.PayrollReceipts.FindAsync(id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Submitted")
        {
            TempData["Error"] = "Only Submitted receipts can be rejected.";
            return RedirectToAction(nameof(Index));
        }

        receipt.Status        = "Draft";
        receipt.RejectionNote = rejectionNote?.Trim();
        receipt.ApprovedDate  = null;
        receipt.ApprovedById  = null;

        await _context.SaveChangesAsync();

        try { await _emailService.NotifyReceiptRejectedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        TempData["Success"] = "Receipt returned to contractor for revision.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/BulkApprove
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkApprove(int[] ids, string? approvalNote)
    {
        if (ids == null || ids.Length == 0)
        {
            TempData["Error"] = "No receipts selected.";
            return RedirectToAction(nameof(Index));
        }

        var approverEmail = User.Identity?.Name;
        var approver = approverEmail != null
            ? await _context.PortalUsers.FirstOrDefaultAsync(u => u.Email == approverEmail)
            : null;

        var receipts = await _context.PayrollReceipts
            .Where(r => ids.Contains(r.Id) && r.Status == "Submitted")
            .ToListAsync();

        var trimmedNote = string.IsNullOrWhiteSpace(approvalNote) ? null : approvalNote.Trim();
        foreach (var r in receipts)
        {
            r.Status       = "Approved";
            r.ApprovedDate = DateTime.UtcNow;
            r.ApprovedById = approver?.EmployeeId;
            r.ApprovalNote = trimmedNote;
        }

        await _context.SaveChangesAsync();

        foreach (var r in receipts)
        {
            try { await _emailService.NotifyReceiptApprovedAsync(r); }
            catch (Exception) { /* email failure should not block UI flow */ }
        }

        TempData["Success"] = $"{receipts.Count} receipt(s) approved.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/BulkMarkPaid
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkMarkPaid(int[] ids)
    {
        if (ids == null || ids.Length == 0)
        {
            TempData["Error"] = "No receipts selected.";
            return RedirectToAction(nameof(Index));
        }

        var receipts = await _context.PayrollReceipts
            .Where(r => ids.Contains(r.Id) && r.Status == "Approved")
            .ToListAsync();

        foreach (var r in receipts)
        {
            r.Status   = "Paid";
            r.PaidDate = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        foreach (var r in receipts)
        {
            try { await _emailService.NotifyReceiptPaidAsync(r); }
            catch (Exception) { /* email failure should not block UI flow */ }
        }

        TempData["Success"] = $"{receipts.Count} receipt(s) marked as Paid.";
        return RedirectToAction(nameof(Index));
    }
}
