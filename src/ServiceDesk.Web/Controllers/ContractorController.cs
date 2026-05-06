using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

[Authorize]
public class ContractorController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public ContractorController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Employee?> GetContractorEmployeeAsync()
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email)) return null;

        var portalUser = await _context.PortalUsers
            .FirstOrDefaultAsync(u => u.Email == email && u.EmployeeId != null);

        if (portalUser?.EmployeeId == null) return null;

        return await _context.Employees
            .FirstOrDefaultAsync(e => e.Id == portalUser.EmployeeId && e.IsContractor);
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

        ViewBag.Contractor = contractor;
        ViewData["Title"] = "Payroll";
        return View(receipts);
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

        var totalHours     = entries.Sum(e => e.Hours);
        var billableHours  = entries.Where(e => e.IsBillable).Sum(e => e.Hours);
        var rate           = contractor.HourlyRate ?? 0m;
        var totalAmount    = billableHours * rate;

        var receipt = new PayrollReceipt
        {
            ContractorId       = contractor.Id,
            PeriodStart        = periodStart,
            PeriodEnd          = periodEnd,
            TotalHours         = totalHours,
            TotalBillableHours = billableHours,
            HourlyRateSnapshot = rate,
            TotalAmount        = totalAmount,
            Status             = "Draft",
            Notes              = notes,
            CreatedDate        = DateTime.UtcNow,
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
        await _context.SaveChangesAsync();

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

        // Row 7: Column headers
        var headers = new[] { "Work Date", "Ticket #", "Description", "Hours", "Billable", "Rate ($/hr)", "Amount" };
        for (int col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(7, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold            = true;
            cell.Style.Font.FontColor       = XLColor.White;
            cell.Style.Fill.BackgroundColor = headerGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // Data rows
        int row = 8;
        bool alt = false;
        foreach (var entry in receipt.TimeEntries.OrderBy(e => e.WorkDate))
        {
            var amount = entry.IsBillable ? entry.Hours * receipt.HourlyRateSnapshot : 0m;
            if (alt)
                ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = altRow;

            ws.Cell(row, 1).Value = entry.WorkDate.ToString("yyyy-MM-dd");
            ws.Cell(row, 2).Value = entry.Ticket?.Id.ToString() ?? "-";
            ws.Cell(row, 3).Value = entry.Description ?? entry.Ticket?.Title ?? "-";
            ws.Cell(row, 4).Value = (double)entry.Hours;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = entry.IsBillable ? "Yes" : "No";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Value = entry.IsBillable ? (double)receipt.HourlyRateSnapshot : 0;
            ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 7).Value = (double)amount;
            ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";

            alt = !alt;
            row++;
        }

        // Totals row
        var totalsBg = XLColor.FromHtml("#e9ecef");
        ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = totalsBg;
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Range(row, 1, row, 3).Merge();
        ws.Cell(row, 4).Value = (double)receipt.TotalHours;
        ws.Cell(row, 4).Style.Font.Bold = true;
        ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 7).Value = (double)receipt.TotalAmount;
        ws.Cell(row, 7).Style.Font.Bold           = true;
        ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";

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
