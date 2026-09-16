using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Renders a <see cref="PayrollReceipt"/> as a single-page (or multi-page
/// if time entries spill) PDF. Mirrors the on-screen receipt layout
/// closely — header band, contractor / period / rate panel, totals tiles,
/// and the time-entries table. Returns the byte buffer ready for download
/// or email attachment.
/// </summary>
public class PayrollReceiptPdfService
{
    private readonly ServiceDeskDbContext _context;

    static PayrollReceiptPdfService()
    {
        // QuestPDF requires a license setting before first use. The
        // Community license is free for organizations under USD 1M revenue
        // and is the appropriate choice for this app.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public PayrollReceiptPdfService(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public async Task<byte[]> RenderAsync(int receiptId)
    {
        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.ApprovedBy)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new InvalidOperationException($"Receipt {receiptId} not found.");

        // Payments Received — pulled separately so we don't force a navigation
        // property onto the receipt just for the PDF path.
        var payments = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receipt.Id)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id)
            .AsNoTracking()
            .ToListAsync();
        var totalPaid   = payments.Sum(p => p.Amount);
        var outstanding = Math.Max(0m, Math.Round(receipt.TotalAmount - totalPaid, 2, MidpointRounding.AwayFromZero));

        var companyName = (await _context.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "CompanyName"))?.Value ?? "ServiceSphere";

        var entries = receipt.TimeEntries
            .OrderBy(e => e.WorkDate)
            .ThenBy(e => e.Id)
            .ToList();

        var contractorName  = receipt.Contractor != null
            ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
            : "(unknown)";
        var contractorEmail = receipt.Contractor?.Email ?? "";
        var contractorTitle = receipt.Contractor?.JobTitle ?? "";

        var brandBlue  = "#0d6efd";
        var brandNavy  = "#0b2545";
        var brandRed   = "#dc3545";
        var muted      = "#6c757d";
        var rowAlt     = "#f8f9fa";
        var border     = "#dee2e6";

        var doc = Document.Create(c =>
        {
            c.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor("#212529"));

                // ── Header band ──
                page.Header().PaddingBottom(8).Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Column(left =>
                        {
                            left.Item().Text(companyName)
                                .FontSize(18).Bold().FontColor(brandNavy);
                            left.Item().Text("Contractor Payroll Receipt")
                                .FontSize(11).FontColor(muted);
                            left.Item().PaddingTop(4).Text(t =>
                            {
                                t.Span("Status: ").FontColor(muted);
                                t.Span(receipt.Status).Bold().FontColor(StatusColor(receipt.Status));
                            });
                        });
                        r.ConstantItem(140).Column(right =>
                        {
                            right.Item().AlignRight().Text(t =>
                            {
                                t.Span("Receipt #").FontColor(muted);
                                t.Span($"  #{receipt.Id}").Bold().FontSize(13);
                            });
                            right.Item().AlignRight().Text(t =>
                            {
                                t.Span("Created  ").FontColor(muted);
                                t.Span(receipt.CreatedDate.ToString("MMM d, yyyy"));
                            });
                            right.Item().AlignRight().Text(t =>
                            {
                                t.Span("Period  ").FontColor(muted);
                                t.Span($"{receipt.PeriodStart:MMM d} – {receipt.PeriodEnd:MMM d, yyyy}");
                            });
                        });
                    });
                    col.Item().PaddingTop(8).LineHorizontal(2).LineColor(brandBlue);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    // ── Contractor / Period / Rates panel ──
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Column(c2 =>
                        {
                            c2.Item().Text("CONTRACTOR").FontSize(8).FontColor(muted).Bold();
                            c2.Item().PaddingTop(2).Text(contractorName).Bold();
                            if (!string.IsNullOrWhiteSpace(contractorEmail))
                                c2.Item().Text(contractorEmail).FontSize(9).FontColor(muted);
                            if (!string.IsNullOrWhiteSpace(contractorTitle))
                                c2.Item().Text(contractorTitle).FontSize(9).FontColor(muted);
                        });
                        r.RelativeItem().Column(c2 =>
                        {
                            c2.Item().Text("BILLING PERIOD").FontSize(8).FontColor(muted).Bold();
                            c2.Item().PaddingTop(2).Text($"{receipt.PeriodStart:MMM d, yyyy}").Bold();
                            c2.Item().Text($"to {receipt.PeriodEnd:MMM d, yyyy}").FontSize(9);
                        });
                        r.RelativeItem().Column(c2 =>
                        {
                            c2.Item().Text("RATES").FontSize(8).FontColor(muted).Bold();
                            c2.Item().PaddingTop(2).Text(t =>
                            {
                                t.Span($"{receipt.HourlyRateSnapshot:C}/hr ").Bold();
                                t.Span("standard").FontColor(muted);
                            });
                            if (receipt.EmergencyRateSnapshot is decimal er && er > 0)
                            {
                                c2.Item().Text(t =>
                                {
                                    t.Span($"{er:C}/hr ").Bold().FontColor(brandRed);
                                    t.Span("emergency").FontColor(muted);
                                });
                            }
                        });
                    });

                    // ── Totals tiles ──
                    col.Item().PaddingTop(14).Row(r =>
                    {
                        TotalTile(r, "TOTAL HOURS",   $"{receipt.TotalHours:0.##}",        "#e9ecef", brandNavy);
                        TotalTile(r, "BILLABLE HRS",  $"{receipt.TotalBillableHours:0.##}", "#e7f1ff", brandBlue);
                        TotalTile(r, "RATE / HR",     $"{receipt.HourlyRateSnapshot:C}",   "#f1f3f5", brandNavy);
                        TotalTile(r, "TOTAL DUE",     $"{receipt.TotalAmount:C}",          brandNavy, "#ffffff", emphasize: true);
                    });

                    // ── Std/Emerg/Retainer breakdown (only when relevant) ──
                    if (receipt.TotalEmergencyHours > 0 || receipt.TotalRetainerAmountApplied > 0)
                    {
                        col.Item().PaddingTop(10).Background(rowAlt).Padding(8).Column(c2 =>
                        {
                            c2.Item().Text("Breakdown").FontSize(9).Bold().FontColor(muted);
                            c2.Item().Row(r =>
                            {
                                r.RelativeItem().Text($"Std hours: {receipt.TotalStandardHours:0.##}");
                                r.RelativeItem().Text(t =>
                                {
                                    t.Span("Emerg hours: ").FontColor(muted);
                                    t.Span($"{receipt.TotalEmergencyHours:0.##}").Bold().FontColor(brandRed);
                                });
                                if (receipt.TotalRetainerAmountApplied > 0)
                                {
                                    r.RelativeItem().Text(t =>
                                    {
                                        t.Span("Retainer applied: ").FontColor(muted);
                                        t.Span($"{receipt.TotalRetainerAmountApplied:C}").Bold();
                                    });
                                }
                            });
                        });
                    }

                    // ── Time entries table ──
                    col.Item().PaddingTop(14).Text("TIME ENTRIES")
                        .FontSize(9).Bold().FontColor(muted);

                    col.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(cols =>
                        {
                            cols.RelativeColumn(2);  // Date
                            cols.RelativeColumn(1.4f);  // Ticket
                            cols.RelativeColumn(5);  // Description
                            cols.RelativeColumn(1);  // Hours
                            cols.RelativeColumn(1.4f); // Rate type
                            cols.RelativeColumn(1.5f); // Amount
                        });

                        t.Header(h =>
                        {
                            h.Cell().Background(brandNavy).Padding(4).Text("Date").FontColor("#fff").FontSize(9).Bold();
                            h.Cell().Background(brandNavy).Padding(4).Text("Ticket").FontColor("#fff").FontSize(9).Bold();
                            h.Cell().Background(brandNavy).Padding(4).Text("Description").FontColor("#fff").FontSize(9).Bold();
                            h.Cell().Background(brandNavy).Padding(4).AlignCenter().Text("Hours").FontColor("#fff").FontSize(9).Bold();
                            h.Cell().Background(brandNavy).Padding(4).AlignCenter().Text("Rate").FontColor("#fff").FontSize(9).Bold();
                            h.Cell().Background(brandNavy).Padding(4).AlignRight().Text("Amount").FontColor("#fff").FontSize(9).Bold();
                        });

                        var stdRate = receipt.HourlyRateSnapshot;
                        var emerRate = receipt.EmergencyRateSnapshot ?? 0m;
                        var alt = false;
                        foreach (var e in entries)
                        {
                            var bg = alt ? rowAlt : "#ffffff";
                            alt = !alt;
                            var isEmer = e.RateType == Core.Enums.PayRateType.Emergency;
                            var rate   = isEmer ? emerRate : stdRate;
                            var amt    = e.IsBillable ? e.Hours * rate : 0m;

                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                .Text(e.WorkDate.ToString("MMM d, yyyy")).FontSize(9);
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                .Text($"#{e.TicketId}").FontSize(9).FontColor(brandBlue);
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                .Text(string.IsNullOrWhiteSpace(e.Description) ? "—" : e.Description).FontSize(9);
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4).AlignCenter()
                                .Text($"{e.Hours:0.##}").FontSize(9).Bold();
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4).AlignCenter()
                                .Text(isEmer ? "Emergency" : "Standard").FontSize(8)
                                .FontColor(isEmer ? brandRed : muted);
                            t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4).AlignRight()
                                .Text(e.IsBillable ? $"{amt:C}" : "—").FontSize(9);
                        }
                    });

                    // ── Payments Received ──
                    if (payments.Count > 0)
                    {
                        col.Item().PaddingTop(10).Text("Payments Received")
                            .FontSize(11).Bold().FontColor(brandNavy);

                        col.Item().PaddingTop(4).Table(t =>
                        {
                            t.ColumnsDefinition(cd =>
                            {
                                cd.RelativeColumn(2);  // Date
                                cd.RelativeColumn(2);  // Method
                                cd.RelativeColumn(3);  // Reference
                                cd.RelativeColumn(2);  // Amount
                                cd.RelativeColumn(2);  // Confirmed?
                            });

                            t.Header(h =>
                            {
                                void HeaderCell(string text) =>
                                    h.Cell().Background(brandNavy).Padding(4).Text(text).FontColor("#ffffff").FontSize(9).Bold();
                                HeaderCell("Date");
                                HeaderCell("Method");
                                HeaderCell("Reference");
                                HeaderCell("Amount");
                                HeaderCell("Confirmed");
                            });

                            var alt = false;
                            foreach (var p in payments)
                            {
                                var bg = alt ? rowAlt : "#ffffff"; alt = !alt;
                                string refDisplay = p.CheckNumber != null && p.Reference != null
                                                       ? $"#{p.CheckNumber} · {p.Reference}"
                                                       : (p.CheckNumber != null ? $"#{p.CheckNumber}"
                                                          : p.Reference ?? "—");

                                t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                    .Text(p.PaymentDate.ToString("MMM d, yyyy")).FontSize(9);
                                t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                    .Text(p.PaymentMethod).FontSize(9);
                                t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                    .Text(refDisplay).FontSize(9);
                                t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4).AlignRight()
                                    .Text($"{p.Amount:C}").FontSize(9).Bold();
                                t.Cell().Background(bg).BorderBottom(0.5f).BorderColor(border).Padding(4)
                                    .Text(p.ContractorConfirmedDate.HasValue
                                            ? $"✓ {p.ContractorConfirmedDate.Value:MMM d, yyyy}"
                                            : "Awaiting")
                                    .FontSize(9)
                                    .FontColor(p.ContractorConfirmedDate.HasValue ? "#198754" : muted);
                            }
                        });

                        // Totals footer
                        col.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text($"Total paid: {totalPaid:C}").Bold();
                            r.RelativeItem().AlignRight().Text(text =>
                            {
                                if (outstanding > 0)
                                {
                                    text.Span("Outstanding: ").FontSize(9);
                                    text.Span($"{outstanding:C}").FontSize(9).Bold().FontColor(brandRed);
                                }
                                else
                                {
                                    text.Span("Fully paid").FontSize(9).Bold().FontColor("#198754");
                                }
                            });
                        });
                    }

                    // ── Notes / approval / rejection callouts ──
                    if (!string.IsNullOrWhiteSpace(receipt.Notes))
                    {
                        col.Item().PaddingTop(12).Background("#f1f3f5").Padding(8).Column(c2 =>
                        {
                            c2.Item().Text("Contractor Notes").FontSize(9).Bold().FontColor(muted);
                            c2.Item().PaddingTop(2).Text(receipt.Notes).FontSize(9);
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(receipt.ApprovalNote) && (receipt.Status == "Approved" || receipt.Status == "Paid"))
                    {
                        col.Item().PaddingTop(8).Background("#d1e7dd").Padding(8).Column(c2 =>
                        {
                            var approver = receipt.ApprovedBy != null
                                ? $"{receipt.ApprovedBy.FirstName} {receipt.ApprovedBy.LastName}"
                                : "Admin";
                            c2.Item().Text($"Approved by {approver}{(receipt.ApprovedDate.HasValue ? $" on {receipt.ApprovedDate:MMM d, yyyy}" : "")}").FontSize(9).Bold().FontColor("#0f5132");
                            c2.Item().PaddingTop(2).Text(receipt.ApprovalNote).FontSize(9).FontColor("#0f5132");
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(receipt.RejectionNote))
                    {
                        col.Item().PaddingTop(8).Background("#fff3cd").Padding(8).Column(c2 =>
                        {
                            c2.Item().Text("Returned for Revision").FontSize(9).Bold().FontColor("#664d03");
                            c2.Item().PaddingTop(2).Text(receipt.RejectionNote).FontSize(9).FontColor("#664d03");
                        });
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span($"{companyName}  ·  Receipt #{receipt.Id}  ·  Generated {DateTime.UtcNow:MMM d, yyyy h:mm tt} UTC  ·  Page ")
                        .FontSize(8).FontColor(muted);
                    t.CurrentPageNumber().FontSize(8).FontColor(muted);
                    t.Span(" of ").FontSize(8).FontColor(muted);
                    t.TotalPages().FontSize(8).FontColor(muted);
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static void TotalTile(QuestPDF.Fluent.RowDescriptor r,
        string label, string value, string bg, string fg, bool emphasize = false)
    {
        r.RelativeItem().PaddingHorizontal(4).Background(bg).Padding(8).Column(c =>
        {
            c.Item().AlignCenter().Text(label).FontSize(8).FontColor(emphasize ? "#cfd8dc" : "#6c757d").Bold();
            c.Item().AlignCenter().PaddingTop(2).Text(value)
                .FontSize(emphasize ? 14 : 13).Bold().FontColor(fg);
        });
    }

    private static string StatusColor(string status) => status switch
    {
        "Paid"      => "#198754",
        "Approved"  => "#0d6efd",
        "Submitted" => "#fd7e14",
        "Draft"     => "#6c757d",
        _           => "#6c757d"
    };
}
