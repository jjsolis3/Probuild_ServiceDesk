using ClosedXML.Excel;
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

    public ContractorController(
        ServiceDeskDbContext context,
        EmailNotificationService emailService,
        PayrollCalculatorService payroll)
    {
        _context = context;
        _emailService = emailService;
        _payroll = payroll;
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
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .FirstOrDefaultAsync(r => r.Id == id && r.ContractorId == contractor.Id);

        if (receipt == null) return NotFound();

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ServiceSphere";

        // Rebuild the per-month breakdown for display. The receipt's own
        // entries are excluded from the "already claimed" check so the
        // retainer math reflects the moment this receipt was created.
        ViewBag.PayrollCalc = await _payroll.CalculateAsync(
            receipt.Contractor!, receipt.TimeEntries.ToList(), receiptIdToIgnore: receipt.Id);

        ViewBag.CompanyName = companyName;
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

        receipt.Status        = "Submitted";
        receipt.SubmittedDate = DateTime.UtcNow;
        receipt.RejectionNote = null;  // clear any prior rejection note on resubmit
        await _context.SaveChangesAsync();

        receipt.Contractor ??= contractor;
        try { await _emailService.NotifyReceiptSubmittedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        TempData["Success"] = "Receipt submitted for review.";
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

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ServiceSphere";

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Payroll Receipt");

        var brandBlue  = XLColor.FromHtml("#0d6efd");
        var headerGray = XLColor.FromHtml("#343a40");
        var altRow     = XLColor.FromHtml("#f8f9fa");

        // Row 1: Company name
        ws.Cell(1, 1).Value = companyName;
        ws.Cell(1, 1).Style.Font.Bold      = true;
        ws.Cell(1, 1).Style.Font.FontSize  = 18;
        ws.Cell(1, 1).Style.Font.FontColor = brandBlue;
        ws.Range(1, 1, 1, 7).Merge();

        // Row 2: Document title
        ws.Cell(2, 1).Value = "Contractor Payroll Receipt";
        ws.Cell(2, 1).Style.Font.Bold     = true;
        ws.Cell(2, 1).Style.Font.FontSize = 13;
        ws.Range(2, 1, 2, 7).Merge();

        // Row 3: Contractor
        ws.Cell(3, 1).Value = $"Contractor: {receipt.Contractor?.FullName}";
        ws.Cell(3, 1).Style.Font.FontSize = 11;
        ws.Range(3, 1, 3, 7).Merge();

        // Row 4: Period
        ws.Cell(4, 1).Value = $"Period: {receipt.PeriodStart:MMMM dd, yyyy} – {receipt.PeriodEnd:MMMM dd, yyyy}";
        ws.Cell(4, 1).Style.Font.FontSize = 11;
        ws.Range(4, 1, 4, 7).Merge();

        // Row 5: Status + generated
        ws.Cell(5, 1).Value = $"Status: {receipt.Status}   |   Generated: {DateTime.UtcNow:MMM d, yyyy 'at' h:mm tt} UTC";
        ws.Cell(5, 1).Style.Font.FontSize  = 9;
        ws.Cell(5, 1).Style.Font.FontColor = XLColor.FromHtml("#6c757d");
        ws.Range(5, 1, 5, 7).Merge();

        ws.Row(6).Height = 6;

        // Row 7: Column headers — Rate Type column inserted between Hours and Billable
        var headers = new[] { "Work Date", "Ticket #", "Description", "Hours", "Rate Type", "Billable", "Rate ($/hr)", "Amount" };
        for (int col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(7, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold            = true;
            cell.Style.Font.FontColor       = XLColor.White;
            cell.Style.Fill.BackgroundColor = headerGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var stdRate  = receipt.HourlyRateSnapshot;
        var emerRate = receipt.EmergencyRateSnapshot ?? 0m;

        // Data rows
        int row = 8;
        bool alt = false;
        foreach (var entry in receipt.TimeEntries.OrderBy(e => e.WorkDate))
        {
            var isEmer     = entry.RateType == ServiceDesk.Core.Enums.PayRateType.Emergency;
            var rateForRow = isEmer ? emerRate : stdRate;
            var amount     = entry.IsBillable ? entry.Hours * rateForRow : 0m;

            if (alt)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = altRow;

            ws.Cell(row, 1).Value = entry.WorkDate.ToString("yyyy-MM-dd");
            ws.Cell(row, 2).Value = entry.Ticket?.Id.ToString() ?? "-";
            ws.Cell(row, 3).Value = entry.Description ?? entry.Ticket?.Title ?? "-";
            ws.Cell(row, 4).Value = (double)entry.Hours;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = isEmer ? "Emergency" : "Standard";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (isEmer)
            {
                ws.Cell(row, 5).Style.Font.Bold      = true;
                ws.Cell(row, 5).Style.Font.FontColor = XLColor.FromHtml("#dc3545");
            }
            ws.Cell(row, 6).Value = entry.IsBillable ? "Yes" : "No";
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 7).Value = entry.IsBillable ? (double)rateForRow : 0;
            ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 8).Value = (double)amount;
            ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";

            alt = !alt;
            row++;
        }

        // Breakdown rows — Standard / Emergency / Retainer subtotals
        var totalsBg = XLColor.FromHtml("#e9ecef");
        var stdHrs   = receipt.TotalStandardHours;
        var emerHrs  = receipt.TotalEmergencyHours;
        var stdAbsorbed = receipt.TotalRetainerHoursApplied;
        var stdBillable = stdHrs - stdAbsorbed;

        void BreakdownRow(string label, string? hoursText, string? rateText, decimal? amount, bool indent = false, bool muted = false)
        {
            ws.Cell(row, 1).Value = indent ? "   " + label : label;
            ws.Range(row, 1, row, 5).Merge();
            ws.Cell(row, 1).Style.Font.Italic = muted;
            if (muted) ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#6c757d");
            if (hoursText != null) ws.Cell(row, 6).Value = hoursText;
            if (rateText  != null) ws.Cell(row, 7).Value = rateText;
            if (amount.HasValue)
            {
                ws.Cell(row, 8).Value = (double)amount.Value;
                ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
            }
            row++;
        }

        row++; // blank spacer row
        BreakdownRow("Standard hours",
            hoursText: stdHrs.ToString("0.##") + " h", rateText: null, amount: null);
        if (stdAbsorbed > 0)
        {
            BreakdownRow("Covered by retainer",
                hoursText: stdAbsorbed.ToString("0.##") + " h",
                rateText: "$0.00", amount: 0m, indent: true, muted: true);
        }
        BreakdownRow("Billable at standard rate",
            hoursText: stdBillable.ToString("0.##") + " h",
            rateText: "$" + stdRate.ToString("N2"),
            amount: stdBillable * stdRate, indent: true);
        if (emerHrs > 0)
        {
            BreakdownRow("Emergency hours",
                hoursText: emerHrs.ToString("0.##") + " h",
                rateText: "$" + emerRate.ToString("N2"),
                amount: emerHrs * emerRate);
        }
        if (receipt.TotalRetainerAmountApplied > 0)
        {
            BreakdownRow($"Monthly retainer (covers up to {(receipt.MonthlyRetainerHoursSnapshot ?? 0m):0.##} h)",
                hoursText: null, rateText: null,
                amount: receipt.TotalRetainerAmountApplied);
        }

        ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = totalsBg;
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Range(row, 1, row, 5).Merge();
        ws.Cell(row, 6).Value = receipt.TotalHours.ToString("0.##") + " h";
        ws.Cell(row, 6).Style.Font.Bold = true;
        ws.Cell(row, 8).Value = (double)receipt.TotalAmount;
        ws.Cell(row, 8).Style.Font.Bold           = true;
        ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var fileName = $"PayrollReceipt_{receipt.Id}_{receipt.PeriodStart:yyyyMMdd}-{receipt.PeriodEnd:yyyyMMdd}.xlsx";
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
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
