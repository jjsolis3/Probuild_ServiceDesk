using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;
using System.Security.Claims;

namespace ServiceDesk.Web.Controllers;

[Authorize]
public class ContractorController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailService;
    private readonly PayrollCalculatorService _payroll;
    private readonly PayrollReceiptAttachmentService _attachments;
    private readonly PayrollActivityService _activity;

    public ContractorController(
        ServiceDeskDbContext context,
        EmailNotificationService emailService,
        PayrollCalculatorService payroll,
        PayrollReceiptAttachmentService attachments,
        PayrollActivityService activity)
    {
        _context = context;
        _emailService = emailService;
        _payroll = payroll;
        _attachments = attachments;
        _activity = activity;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Employee?> GetContractorEmployeeAsync()
    {
        // EmployeeId claim is baked into the login cookie — use it directly rather than
        // looking up by User.Identity.Name, which is the display name (FullName), not email.
        var empIdStr = User.FindFirstValue("EmployeeId");
        if (!int.TryParse(empIdStr, out var empId) || empId == 0) return null;

        return await _context.Employees
            .FirstOrDefaultAsync(e => e.Id == empId && e.IsContractor);
    }

    // ── Payroll Dashboard ─────────────────────────────────────────────────────

    // GET /Contractor/Payroll
    public async Task<IActionResult> Payroll()
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
        {
            TempData["Error"] = "Your employee profile is not flagged as a contractor. Ask an Admin to enable 'Is Contractor' on your employee record.";
            return RedirectToAction("Index", "Home");
        }

        var receipts = await _context.PayrollReceipts
            .Where(r => r.ContractorId == contractor.Id)
            .OrderByDescending(r => r.PeriodStart)
            .ToListAsync();

        // All unclaimed time entries — no date filter — for the outstanding-hours dashboard
        var unclaimed = await _context.TicketTimeEntries
            .Include(e => e.Ticket)
            .Where(e => e.LoggedByEmployeeId == contractor.Id && e.PayrollReceiptId == null)
            .OrderByDescending(e => e.WorkDate)
            .ToListAsync();

        // Estimated unclaimed amount uses per-entry rate (Standard or Emergency)
        // so contractors with a mixed workload see a realistic figure on the
        // dashboard, not a single-rate approximation.
        var standardRateForDashboard  = contractor.HourlyRate ?? 0m;
        var emergencyRateForDashboard = contractor.EmergencyHourlyRate ?? 0m;
        var unclaimedAmount = unclaimed
            .Where(e => e.IsBillable)
            .Sum(e => e.Hours * (e.RateType == Core.Enums.PayRateType.Emergency
                                    ? emergencyRateForDashboard
                                    : standardRateForDashboard));

        ViewBag.Contractor          = contractor;
        ViewBag.UnclaimedEntries    = unclaimed;
        ViewBag.UnclaimedBillableHrs = unclaimed.Where(e => e.IsBillable).Sum(e => e.Hours);
        ViewBag.UnclaimedAmount      = unclaimedAmount;
        ViewData["Title"] = "Payroll";
        return View(receipts);
    }

    // POST /Contractor/EditUnclaimedEntry
    //
    // Lets a contractor correct an unclaimed time entry they own — rate-type
    // mislabel (Standard ↔ Emergency), hours typo, description, billable flag.
    // Only their own entries, only while still unclaimed (no PayrollReceiptId),
    // and an audit row (ModifiedDate/By/Reason/Count) is always written.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUnclaimedEntry(int entryId, decimal hours,
        string? description, bool isBillable, bool isEmergency,
        string? modificationReason)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var entry = await _context.TicketTimeEntries
            .FirstOrDefaultAsync(e => e.Id == entryId
                                   && e.LoggedByEmployeeId == contractor.Id
                                   && e.PayrollReceiptId == null);
        if (entry == null)
        {
            TempData["Error"] = "Entry not found, claimed on a receipt, or not yours to edit.";
            return RedirectToAction(nameof(Payroll));
        }

        if (string.IsNullOrWhiteSpace(modificationReason))
        {
            TempData["Error"] = "A reason is required when editing a time entry.";
            return RedirectToAction(nameof(Payroll));
        }

        if (hours <= 0)
        {
            TempData["Error"] = "Hours must be greater than zero.";
            return RedirectToAction(nameof(Payroll));
        }

        var rateType = isEmergency
            ? Core.Enums.PayRateType.Emergency
            : Core.Enums.PayRateType.Standard;

        // Emergency requires the contractor to actually have an emergency
        // rate configured. Silently fall back to Standard otherwise so the
        // entry doesn't end up billing against a null rate.
        if (rateType == Core.Enums.PayRateType.Emergency
            && (contractor.EmergencyHourlyRate == null || contractor.EmergencyHourlyRate <= 0))
        {
            rateType = Core.Enums.PayRateType.Standard;
        }

        entry.Hours              = hours;
        entry.Description        = description;
        entry.IsBillable         = isBillable;
        entry.RateType           = rateType;
        entry.ModifiedDate       = DateTime.UtcNow;
        entry.ModifiedByEmail    = contractor.Email;
        entry.ModificationReason = modificationReason.Trim();
        entry.ModificationCount += 1;

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Entry on Ticket #{entry.TicketId} updated.";
        return RedirectToAction(nameof(Payroll));
    }

    // POST /Contractor/DeleteUnclaimedEntry
    //
    // Same ownership and "still unclaimed" gate as EditUnclaimedEntry. Once
    // an entry hits a receipt it can only be unstuck by deleting the receipt
    // (existing path) — direct delete here is intentionally blocked.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUnclaimedEntry(int entryId)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var entry = await _context.TicketTimeEntries
            .FirstOrDefaultAsync(e => e.Id == entryId
                                   && e.LoggedByEmployeeId == contractor.Id
                                   && e.PayrollReceiptId == null);
        if (entry == null)
        {
            TempData["Error"] = "Entry not found, claimed on a receipt, or not yours to delete.";
            return RedirectToAction(nameof(Payroll));
        }

        _context.TicketTimeEntries.Remove(entry);
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Entry on Ticket #{entry.TicketId} deleted.";
        return RedirectToAction(nameof(Payroll));
    }

    // ── New Receipt ───────────────────────────────────────────────────────────

    // GET /Contractor/NewReceipt
    public async Task<IActionResult> NewReceipt(DateTime? periodStart, DateTime? periodEnd)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        ViewBag.Contractor = contractor;
        ViewData["Title"] = "New Payroll Receipt";

        // Default: current calendar month
        var start = periodStart ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var end   = periodEnd   ?? start.AddMonths(1).AddDays(-1);

        ViewBag.PeriodStart = start.ToString("yyyy-MM-dd");
        ViewBag.PeriodEnd   = end.ToString("yyyy-MM-dd");

        var entries = await GetUnclaimedEntriesAsync(contractor.Id, start, end);

        // Preview the same totals the POST handler will persist — keeps the
        // user from being surprised by retainer / rate-type math on submit.
        ViewBag.PayrollCalc = await _payroll.CalculateAsync(contractor, entries);

        return View(entries);
    }

    // POST /Contractor/NewReceipt
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewReceipt(DateTime periodStart, DateTime periodEnd, string? notes)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        if (periodEnd < periodStart)
        {
            TempData["Error"] = "Period end must be on or after period start.";
            return RedirectToAction(nameof(NewReceipt), new { periodStart, periodEnd });
        }

        var entries = await GetUnclaimedEntriesAsync(contractor.Id, periodStart, periodEnd);

        if (!entries.Any())
        {
            TempData["Warning"] = "No unclaimed billable time entries found for the selected period.";
            return RedirectToAction(nameof(NewReceipt), new { periodStart, periodEnd });
        }

        var calc = await _payroll.CalculateAsync(contractor, entries);

        var receipt = new PayrollReceipt
        {
            ContractorId                   = contractor.Id,
            PeriodStart                    = periodStart,
            PeriodEnd                      = periodEnd,
            TotalHours                     = calc.TotalHours,
            TotalBillableHours             = calc.TotalBillableHours,
            HourlyRateSnapshot             = contractor.HourlyRate ?? 0m,
            EmergencyRateSnapshot          = contractor.EmergencyHourlyRate,
            TotalStandardHours             = calc.TotalStandardHours,
            TotalEmergencyHours            = calc.TotalEmergencyHours,
            MonthlyRetainerAmountSnapshot  = contractor.MonthlyRetainerAmount,
            MonthlyRetainerHoursSnapshot   = contractor.MonthlyRetainerHoursIncluded,
            TotalRetainerHoursApplied      = calc.TotalRetainerHoursApplied,
            TotalRetainerAmountApplied     = calc.TotalRetainerAmountApplied,
            TotalAmount                    = calc.TotalAmount,
            Status                         = "Draft",
            Notes                          = notes,
            CreatedDate                    = DateTime.UtcNow,
        };

        _context.PayrollReceipts.Add(receipt);
        await _context.SaveChangesAsync();

        // Claim the entries
        foreach (var entry in entries)
            entry.PayrollReceiptId = receipt.Id;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Payroll receipt created as Draft.";
        return RedirectToAction(nameof(ReceiptDetail), new { id = receipt.Id });
    }

    // ── Receipt Detail (printable) ────────────────────────────────────────────

    // GET /Contractor/ReceiptDetail/{id}
    public async Task<IActionResult> ReceiptDetail(int id)
    {
        // Admins / IT Agents can view any receipt (needed for the AdminPayroll
        // "View" link to work and for the activity thread to be visible to
        // them); contractors can only see their own.
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("IT Agent");
        var contractor = await GetContractorEmployeeAsync();
        if (!isAdmin && contractor == null)
            return RedirectToAction(nameof(Payroll));

        var query = _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .AsQueryable();

        if (!isAdmin)
            query = query.Where(r => r.ContractorId == contractor!.Id);

        var receipt = await query.FirstOrDefaultAsync(r => r.Id == id);

        if (receipt == null) return NotFound();

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ServiceSphere";

        // Rebuild the per-month breakdown for display. The receipt's own
        // entries are excluded from the "already claimed" check so the
        // retainer math reflects the moment this receipt was created.
        ViewBag.PayrollCalc = await _payroll.CalculateAsync(
            receipt.Contractor!, receipt.TimeEntries.ToList(), receiptIdToIgnore: receipt.Id);

        ViewBag.CompanyName = companyName;
        ViewBag.Comments = await _context.PayrollReceiptComments
            .Where(c => c.PayrollReceiptId == receipt.Id)
            .OrderBy(c => c.CreatedDate)
            .ToListAsync();
        ViewData["Title"] = $"Receipt #{receipt.Id}";
        return View(receipt);
    }

    // ── Submit Receipt ────────────────────────────────────────────────────────

    // POST /Contractor/SubmitReceipt/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReceipt(int id)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);

        if (receipt == null) return NotFound();
        if (receipt.Status != "Draft")
        {
            TempData["Warning"] = "Only Draft receipts can be submitted.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        // Detect resubmit vs initial submit by looking for a prior
        // rejection. The timeline message differs so the admin sees that
        // the contractor responded to feedback.
        var isResubmit = !string.IsNullOrWhiteSpace(receipt.RejectionNote);
        var priorRejectionNote = receipt.RejectionNote;

        receipt.Status        = "Submitted";
        receipt.SubmittedDate = DateTime.UtcNow;
        receipt.RejectionNote = null;  // clear any prior rejection note on resubmit
        receipt.LastReminderSentUtc = null; // restart the reminder clock
        await _context.SaveChangesAsync();

        await _activity.LogContractorAsync(receipt.Id, contractor,
            isResubmit
                ? $"Resubmitted after revision."
                : "Submitted for review.");

        receipt.Contractor ??= contractor;
        try { await _emailService.NotifyReceiptSubmittedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        TempData["Success"] = isResubmit ? "Receipt resubmitted for review." : "Receipt submitted for review.";
        return RedirectToAction(nameof(ReceiptDetail), new { id });
    }

    // POST /Contractor/ConfirmPaymentReceived/{id}
    //
    // Closes the loop on a Paid receipt — contractor attests that the
    // funds landed. We don't change Status (still "Paid") because Paid
    // is the payer's claim; ConfirmedDate is the payee's attestation.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPaymentReceived(int id, string? confirmationNote)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Paid")
        {
            TempData["Error"] = "Only Paid receipts can be confirmed.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }
        if (receipt.PaymentConfirmedDate.HasValue)
        {
            TempData["Warning"] = "Payment has already been confirmed for this receipt.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        receipt.PaymentConfirmedDate = DateTime.UtcNow;
        receipt.PaymentConfirmedNote = string.IsNullOrWhiteSpace(confirmationNote) ? null : confirmationNote.Trim();
        await _context.SaveChangesAsync();

        var summary = string.IsNullOrWhiteSpace(receipt.PaymentConfirmedNote)
            ? "Confirmed payment received."
            : $"Confirmed payment received — {receipt.PaymentConfirmedNote}";
        await _activity.LogContractorAsync(receipt.Id, contractor, summary);

        try { await _emailService.NotifyReceiptPaymentConfirmedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        TempData["Success"] = "Thanks — payment confirmed.";
        return RedirectToAction(nameof(ReceiptDetail), new { id });
    }

    // POST /Contractor/PostReceiptComment/{id}
    //
    // Contractor-side write into the receipt activity thread. Allowed on
    // any non-Draft receipt the contractor owns, so they can ask
    // questions on a Submitted receipt or attach a note to a Paid one.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostReceiptComment(int id, string body)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);
        if (receipt == null) return NotFound();

        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Comment cannot be empty.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        await _activity.LogContractorAsync(receipt.Id, contractor, body.Trim());
        TempData["Success"] = "Comment posted.";
        return RedirectToAction(nameof(ReceiptDetail), new { id });
    }

    // ── Delete Draft Receipt ──────────────────────────────────────────────────

    // POST /Contractor/DeleteReceipt/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReceipt(int id)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .Include(r => r.TimeEntries)
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);

        if (receipt == null) return NotFound();
        if (receipt.Status != "Draft")
        {
            TempData["Error"] = "Only Draft receipts can be deleted.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        // Release claimed time entries before deleting
        foreach (var entry in receipt.TimeEntries)
            entry.PayrollReceiptId = null;

        _context.PayrollReceipts.Remove(receipt);
        await _context.SaveChangesAsync();

        TempData["Success"] = "Draft receipt deleted and time entries released.";
        return RedirectToAction(nameof(Payroll));
    }

    // ── Excel Export ──────────────────────────────────────────────────────────

    // GET /Contractor/ReceiptExcel/{id}
    public async Task<IActionResult> ReceiptExcel(int id)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);

        if (receipt == null) return NotFound();

        var bytes = await _attachments.RenderXlsxAsync(receipt);
        var fileName = $"PayrollReceipt_{receipt.Id}_{receipt.PeriodStart:yyyyMMdd}-{receipt.PeriodEnd:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // POST /Contractor/ShareReceipt/{id}
    //
    // Emails a Submitted/Approved/Paid receipt to the recipient(s) the
    // contractor supplies — typically Accounts Payable. Attaches the
    // receipt as PDF, XLSX, or both based on the form selection. Draft
    // receipts are intentionally blocked — sending a half-finished receipt
    // to AP would be confusing.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShareReceipt(int id, string toEmail, string? ccEmail,
        string? subject, string? message, string format)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);
        if (receipt == null) return NotFound();

        if (receipt.Status == "Draft")
        {
            TempData["Error"] = "Submit the receipt before sharing it. Draft receipts cannot be emailed.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            TempData["Error"] = "Recipient email is required.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        var attachments = await _attachments.BuildAsync(receipt, format);
        if (attachments.Count == 0)
        {
            TempData["Error"] = "Invalid attachment format.";
            return RedirectToAction(nameof(ReceiptDetail), new { id });
        }

        var senderDisplay = $"{contractor.FirstName} {contractor.LastName} ({contractor.Email})";
        var ok = await _emailService.ShareReceiptAsync(receipt, toEmail, ccEmail, subject, message, attachments, senderDisplay);

        TempData[ok ? "Success" : "Error"] = ok
            ? $"Receipt #{receipt.Id} sent to {toEmail}."
            : $"Email send failed. Check the Email Activity log for details.";
        return RedirectToAction(nameof(ReceiptDetail), new { id });
    }


    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<TicketTimeEntry>> GetUnclaimedEntriesAsync(int contractorId, DateTime start, DateTime end)
    {
        return await _context.TicketTimeEntries
            .Include(e => e.Ticket)
            .Where(e =>
                e.LoggedByEmployeeId == contractorId &&
                e.PayrollReceiptId == null &&
                e.WorkDate >= start.Date &&
                e.WorkDate <= end.Date)
            .OrderBy(e => e.WorkDate)
            .ToListAsync();
    }
}
