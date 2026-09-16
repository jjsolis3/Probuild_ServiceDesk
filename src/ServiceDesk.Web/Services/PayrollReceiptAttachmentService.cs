using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Builds receipt attachments (PDF and/or XLSX) for the share-receipt flow.
/// Owns the XLSX rendering logic that used to live inline in
/// ContractorController.ReceiptExcel so both the file-download endpoint and
/// the email-share path produce byte-identical workbooks. The PDF half
/// delegates to <see cref="PayrollReceiptPdfService"/>.
/// </summary>
public class PayrollReceiptAttachmentService
{
    private readonly ServiceDeskDbContext _context;
    private readonly PayrollReceiptPdfService _pdf;

    public PayrollReceiptAttachmentService(ServiceDeskDbContext context, PayrollReceiptPdfService pdf)
    {
        _context = context;
        _pdf = pdf;
    }

    /// <summary>
    /// Returns one or both files for the share flow. `format` is "pdf",
    /// "xlsx", or "both"; anything else yields an empty list so the caller
    /// can surface a single consistent error message.
    /// </summary>
    public async Task<List<GmailApiService.EmailAttachment>> BuildAsync(PayrollReceipt receipt, string format)
    {
        var list = new List<GmailApiService.EmailAttachment>();
        var basename = $"PayrollReceipt_{receipt.Id}_{receipt.PeriodStart:yyyyMMdd}-{receipt.PeriodEnd:yyyyMMdd}";
        var wantsPdf  = format == "pdf"  || format == "both";
        var wantsXlsx = format == "xlsx" || format == "both";
        if (!wantsPdf && !wantsXlsx) return list;

        if (wantsPdf)
        {
            var pdfBytes = await _pdf.RenderAsync(receipt.Id);
            list.Add(new GmailApiService.EmailAttachment(
                $"{basename}.pdf", "application/pdf", pdfBytes));
        }
        if (wantsXlsx)
        {
            var xlsxBytes = await RenderXlsxAsync(receipt);
            list.Add(new GmailApiService.EmailAttachment(
                $"{basename}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                xlsxBytes));
        }
        return list;
    }

    /// <summary>
    /// Renders the receipt as an XLSX byte buffer. Mirrors the layout that
    /// the download endpoint used to inline. Kept public so the download
    /// endpoint can call it without going through BuildAsync.
    /// </summary>
    public async Task<byte[]> RenderXlsxAsync(PayrollReceipt receipt)
    {
        // Make sure related data is loaded — caller may pass a bare receipt
        // (e.g. from the AdminPayroll list). Touching navigation properties
        // on a no-tracking query is cheap if already loaded.
        if (receipt.Contractor == null)
            await _context.Entry(receipt).Reference(r => r.Contractor).LoadAsync();
        if (receipt.TimeEntries.Count == 0)
            await _context.Entry(receipt).Collection(r => r.TimeEntries).Query().Include(e => e.Ticket).LoadAsync();

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ServiceSphere";

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Payroll Receipt");

        var brandBlue  = XLColor.FromHtml("#0d6efd");
        var headerGray = XLColor.FromHtml("#343a40");
        var altRow     = XLColor.FromHtml("#f8f9fa");

        ws.Cell(1, 1).Value = companyName;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 18;
        ws.Cell(1, 1).Style.Font.FontColor = brandBlue;
        ws.Range(1, 1, 1, 7).Merge();

        ws.Cell(2, 1).Value = "Contractor Payroll Receipt";
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 13;
        ws.Range(2, 1, 2, 7).Merge();

        ws.Cell(3, 1).Value = $"Contractor: {receipt.Contractor?.FullName}";
        ws.Cell(3, 1).Style.Font.FontSize = 11;
        ws.Range(3, 1, 3, 7).Merge();

        ws.Cell(4, 1).Value = $"Period: {receipt.PeriodStart:MMMM dd, yyyy} – {receipt.PeriodEnd:MMMM dd, yyyy}";
        ws.Cell(4, 1).Style.Font.FontSize = 11;
        ws.Range(4, 1, 4, 7).Merge();

        ws.Cell(5, 1).Value = $"Status: {receipt.Status}   |   Generated: {DateTime.UtcNow:MMM d, yyyy 'at' h:mm tt} UTC";
        ws.Cell(5, 1).Style.Font.FontSize = 9;
        ws.Cell(5, 1).Style.Font.FontColor = XLColor.FromHtml("#6c757d");
        ws.Range(5, 1, 5, 7).Merge();

        ws.Row(6).Height = 6;

        var headers = new[] { "Work Date", "Ticket #", "Description", "Hours", "Rate Type", "Billable", "Rate ($/hr)", "Amount" };
        for (int col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(7, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = headerGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var stdRate  = receipt.HourlyRateSnapshot;
        var emerRate = receipt.EmergencyRateSnapshot ?? 0m;

        int row = 8;
        bool alt = false;
        foreach (var entry in receipt.TimeEntries.OrderBy(e => e.WorkDate))
        {
            var isEmer     = entry.RateType == Core.Enums.PayRateType.Emergency;
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
                ws.Cell(row, 5).Style.Font.Bold = true;
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

        var totalsBg = XLColor.FromHtml("#e9ecef");
        ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = totalsBg;
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Range(row, 1, row, 5).Merge();
        ws.Cell(row, 6).Value = receipt.TotalHours.ToString("0.##") + " h";
        ws.Cell(row, 6).Style.Font.Bold = true;
        ws.Cell(row, 8).Value = (double)receipt.TotalAmount;
        ws.Cell(row, 8).Style.Font.Bold = true;
        ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";

        // ── Payments Received block — appended after the time entries totals so
        // the top part of the sheet stays unchanged for downstream tooling.
        var payments = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receipt.Id)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id)
            .ToListAsync();
        if (payments.Count > 0)
        {
            row += 2;
            ws.Cell(row, 1).Value = "Payments Received";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 13;
            ws.Cell(row, 1).Style.Font.FontColor = brandBlue;
            ws.Range(row, 1, row, 8).Merge();
            row++;

            var payHeaders = new[] { "Date", "Method", "Check #", "Reference", "Note", "Confirmed", "", "Amount" };
            for (int col = 1; col <= payHeaders.Length; col++)
            {
                var cell = ws.Cell(row, col);
                cell.Value = payHeaders[col - 1];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = headerGray;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
            row++;

            bool payAlt = false;
            foreach (var p in payments)
            {
                if (payAlt) ws.Range(row, 1, row, payHeaders.Length).Style.Fill.BackgroundColor = altRow;
                ws.Cell(row, 1).Value = p.PaymentDate.ToString("yyyy-MM-dd");
                ws.Cell(row, 2).Value = p.PaymentMethod;
                ws.Cell(row, 3).Value = p.CheckNumber ?? "";
                ws.Cell(row, 4).Value = p.Reference ?? "";
                ws.Cell(row, 5).Value = p.Note ?? "";
                ws.Cell(row, 6).Value = p.ContractorConfirmedDate.HasValue
                                        ? p.ContractorConfirmedDate.Value.ToString("yyyy-MM-dd")
                                        : "";
                ws.Cell(row, 8).Value = (double)p.Amount;
                ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
                payAlt = !payAlt;
                row++;
            }

            var totalPaid = payments.Sum(p => p.Amount);
            var outstanding = Math.Max(0m, Math.Round(receipt.TotalAmount - totalPaid, 2, MidpointRounding.AwayFromZero));
            ws.Range(row, 1, row, payHeaders.Length).Style.Fill.BackgroundColor = totalsBg;
            ws.Cell(row, 1).Value = "TOTAL PAID";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Range(row, 1, row, 7).Merge();
            ws.Cell(row, 8).Value = (double)totalPaid;
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
            row++;
            ws.Cell(row, 1).Value = outstanding > 0 ? "OUTSTANDING" : "FULLY PAID";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = outstanding > 0 ? XLColor.FromHtml("#f59e0b") : XLColor.FromHtml("#198754");
            ws.Range(row, 1, row, 7).Merge();
            ws.Cell(row, 8).Value = (double)outstanding;
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 8).Style.Font.FontColor = outstanding > 0 ? XLColor.FromHtml("#f59e0b") : XLColor.FromHtml("#198754");
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
