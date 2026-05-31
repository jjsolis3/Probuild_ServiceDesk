using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;
using System.Security.Claims;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class AdminPayrollController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly EmailNotificationService _emailService;
    private readonly PayrollReceiptAttachmentService _attachments;
    private readonly PayrollActivityService _activity;
    private readonly PortalNotificationService _bell;
    private readonly MentionService _mentions;

    public AdminPayrollController(ServiceDeskDbContext context,
        EmailNotificationService emailService,
        PayrollReceiptAttachmentService attachments,
        PayrollActivityService activity,
        PortalNotificationService bell,
        MentionService mentions)
    {
        _context = context;
        _emailService = emailService;
        _attachments = attachments;
        _activity = activity;
        _bell = bell;
        _mentions = mentions;
    }

    /// <summary>
    /// Pings the contractor's portal account (if any) about a lifecycle
    /// event on their receipt. No-ops cleanly when the contractor has no
    /// portal user. Wrapped in try/catch so a bell failure can't break
    /// the surrounding admin action.
    /// </summary>
    private async Task NotifyContractorOnBellAsync(int contractorEmployeeId, string type, string title, string? message, string? link, string? icon)
    {
        try
        {
            var portalUser = await _context.PortalUsers
                .Where(u => u.EmployeeId == contractorEmployeeId && u.IsActive)
                .FirstOrDefaultAsync();
            if (portalUser == null) return;
            await _bell.NotifyAsync(portalUser.Id, type, title, message, link, icon);
        }
        catch (Exception) { /* never block the flow */ }
    }

    private static string ContractorReceiptLink(IUrlHelper url, int receiptId) =>
        url.Action("ReceiptDetail", "Contractor", new { id = receiptId }) ?? "#";

    /// <summary>
    /// Resolves the acting admin's PortalUser row from the auth cookie.
    /// Used by activity logging + comment posting. Cached per-request via
    /// a small private field so we don't hit the DB twice in one action.
    /// </summary>
    private PortalUser? _actingAdminCache;
    private async Task<PortalUser?> GetActingAdminAsync()
    {
        if (_actingAdminCache != null) return _actingAdminCache;
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email)) return null;
        _actingAdminCache = await _context.PortalUsers
            .Include(u => u.Employee)
            .FirstOrDefaultAsync(u => u.Email == email);
        return _actingAdminCache;
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

        var approver = await GetActingAdminAsync();

        receipt.Status       = "Approved";
        receipt.ApprovedDate = DateTime.UtcNow;
        receipt.ApprovedById = approver?.EmployeeId;
        receipt.ApprovalNote = string.IsNullOrWhiteSpace(approvalNote) ? null : approvalNote.Trim();

        await _context.SaveChangesAsync();

        if (approver != null)
        {
            var summary = string.IsNullOrWhiteSpace(receipt.ApprovalNote)
                ? "Approved."
                : $"Approved — {receipt.ApprovalNote}";
            await _activity.LogAdminAsync(receipt.Id, approver, summary);
        }

        try { await _emailService.NotifyReceiptApprovedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        await NotifyContractorOnBellAsync(
            receipt.ContractorId,
            type:    "PayrollApproved",
            title:   $"Your receipt #{receipt.Id} was approved",
            message: receipt.ApprovalNote ?? $"{receipt.TotalAmount:C} · awaiting payment",
            link:    ContractorReceiptLink(Url, receipt.Id),
            icon:    "bi-check-circle");

        TempData["Success"] = "Receipt approved.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/MarkPaid/{id}
    //
    // Capture method (Check/ACH/Zelle/Wire/Other) + reference so the
    // contractor sees actionable detail in the "Payment Confirmed" email
    // and can reconcile against their bank.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, string? paymentMethod, string? paymentReference)
    {
        var receipt = await _context.PayrollReceipts.FindAsync(id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Approved")
        {
            TempData["Error"] = "Only Approved receipts can be marked as Paid.";
            return RedirectToAction(nameof(Index));
        }

        receipt.Status           = "Paid";
        receipt.PaidDate         = DateTime.UtcNow;
        receipt.PaymentMethod    = string.IsNullOrWhiteSpace(paymentMethod)    ? null : paymentMethod.Trim();
        receipt.PaymentReference = string.IsNullOrWhiteSpace(paymentReference) ? null : paymentReference.Trim();

        await _context.SaveChangesAsync();

        var admin = await GetActingAdminAsync();
        if (admin != null)
        {
            var summary = (receipt.PaymentMethod, receipt.PaymentReference) switch
            {
                (null, null) => "Marked as Paid.",
                (var m, null) => $"Marked as Paid via {m}.",
                (null, var r) => $"Marked as Paid — reference {r}.",
                (var m, var r) => $"Marked as Paid via {m} — reference {r}."
            };
            await _activity.LogAdminAsync(receipt.Id, admin, summary);
        }

        try { await _emailService.NotifyReceiptPaidAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        var paidDetail = (receipt.PaymentMethod, receipt.PaymentReference) switch
        {
            (null, null)   => $"{receipt.TotalAmount:C} issued",
            (var m, null)  => $"{receipt.TotalAmount:C} · {m}",
            (null, var r)  => $"{receipt.TotalAmount:C} · ref {r}",
            (var m, var r) => $"{receipt.TotalAmount:C} · {m} · {r}"
        };
        await NotifyContractorOnBellAsync(
            receipt.ContractorId,
            type:    "PayrollPaid",
            title:   $"Payment issued for receipt #{receipt.Id}",
            message: paidDetail,
            link:    ContractorReceiptLink(Url, receipt.Id),
            icon:    "bi-cash-coin");

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

        var admin = await GetActingAdminAsync();
        if (admin != null)
        {
            var note = string.IsNullOrWhiteSpace(receipt.RejectionNote)
                ? "Returned for revision."
                : $"Returned for revision — {receipt.RejectionNote}";
            await _activity.LogAdminAsync(receipt.Id, admin, note);
        }

        try { await _emailService.NotifyReceiptRejectedAsync(receipt); }
        catch (Exception) { /* email failure should not block UI flow */ }

        await NotifyContractorOnBellAsync(
            receipt.ContractorId,
            type:    "PayrollRejected",
            title:   $"Receipt #{receipt.Id} returned for revision",
            message: receipt.RejectionNote ?? "Please review and resubmit.",
            link:    ContractorReceiptLink(Url, receipt.Id),
            icon:    "bi-arrow-counterclockwise");

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

        var approver = await GetActingAdminAsync();

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

        if (approver != null)
        {
            var msg = string.IsNullOrWhiteSpace(trimmedNote)
                ? "Approved (bulk)."
                : $"Approved (bulk) — {trimmedNote}";
            foreach (var r in receipts)
                await _activity.LogAdminAsync(r.Id, approver, msg);
        }

        foreach (var r in receipts)
        {
            try { await _emailService.NotifyReceiptApprovedAsync(r); }
            catch (Exception) { /* email failure should not block UI flow */ }

            await NotifyContractorOnBellAsync(
                r.ContractorId, "PayrollApproved",
                title:   $"Your receipt #{r.Id} was approved",
                message: r.ApprovalNote ?? $"{r.TotalAmount:C} · awaiting payment",
                link:    ContractorReceiptLink(Url, r.Id),
                icon:    "bi-check-circle");
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

        var admin = await GetActingAdminAsync();
        if (admin != null)
        {
            foreach (var r in receipts)
                await _activity.LogAdminAsync(r.Id, admin, "Marked as Paid (bulk).");
        }

        foreach (var r in receipts)
        {
            try { await _emailService.NotifyReceiptPaidAsync(r); }
            catch (Exception) { /* email failure should not block UI flow */ }

            await NotifyContractorOnBellAsync(
                r.ContractorId, "PayrollPaid",
                title:   $"Payment issued for receipt #{r.Id}",
                message: $"{r.TotalAmount:C} issued",
                link:    ContractorReceiptLink(Url, r.Id),
                icon:    "bi-cash-coin");
        }

        TempData["Success"] = $"{receipts.Count} receipt(s) marked as Paid.";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/SendWeeklyDigest
    //
    // Emails every active contractor a summary of their last 7 days of logged
    // hours (Standard / Emergency split, billable vs. total, per-day rows).
    // Triggered manually from the AdminPayroll page so payroll can nudge
    // contractors before a submission window closes.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendWeeklyDigest(DateTime? weekEnd)
    {
        // Default: the most recently completed week, Mon–Sun. If the admin
        // passes a specific weekEnd date, treat it as the inclusive end of
        // the 7-day window.
        var end = (weekEnd ?? DateTime.UtcNow.Date).Date;
        var start = end.AddDays(-6);

        var contractors = await _context.Employees
            .Where(e => e.IsContractor && e.IsActive && !string.IsNullOrEmpty(e.Email))
            .ToListAsync();

        if (contractors.Count == 0)
        {
            TempData["Error"] = "No active contractors with email addresses.";
            return RedirectToAction(nameof(Index));
        }

        var contractorIds = contractors.Select(c => c.Id).ToList();
        var entries = await _context.TicketTimeEntries
            .AsNoTracking()
            .Where(e => e.WorkDate >= start && e.WorkDate <= end)
            .Where(e => e.LoggedByEmployeeId != null && contractorIds.Contains(e.LoggedByEmployeeId!.Value))
            .ToListAsync();

        var entriesByContractor = entries
            .GroupBy(e => e.LoggedByEmployeeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ServiceDesk.Core.Models.TicketTimeEntry>)g.OrderBy(x => x.WorkDate).ToList());

        int sent = 0;
        int failed = 0;
        foreach (var contractor in contractors)
        {
            var weekEntries = entriesByContractor.TryGetValue(contractor.Id, out var list)
                ? list
                : (IReadOnlyList<ServiceDesk.Core.Models.TicketTimeEntry>)Array.Empty<ServiceDesk.Core.Models.TicketTimeEntry>();
            try
            {
                await _emailService.NotifyContractorWeeklyHoursAsync(contractor, start, end, weekEntries);
                sent++;
            }
            catch (Exception)
            {
                failed++;
                // Per-contractor failure is logged inside the service; keep
                // sending the rest of the batch so one bad address doesn't
                // tank the whole digest run.
            }
        }

        TempData["Success"] = failed == 0
            ? $"Weekly digest sent to {sent} contractor(s) for {start:MMM d}–{end:MMM d, yyyy}."
            : $"Weekly digest sent to {sent} contractor(s); {failed} failed (see Notification Log).";
        return RedirectToAction(nameof(Index));
    }

    // POST /AdminPayroll/ShareReceipt/{id}
    //
    // Admin-side counterpart to /Contractor/ShareReceipt. Lets the admin
    // forward an approved/paid receipt to AP/Payroll without first
    // impersonating the contractor. Same Submitted+ gate (Draft is never
    // shareable) and the same PDF/XLSX attachment options.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShareReceipt(int id, string toEmail, string? ccEmail,
        string? subject, string? message, string format)
    {
        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (receipt == null) return NotFound();

        if (receipt.Status == "Draft")
        {
            TempData["Error"] = "Draft receipts cannot be shared — wait for the contractor to submit.";
            return RedirectToAction(nameof(Index));
        }
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            TempData["Error"] = "Recipient email is required.";
            return RedirectToAction(nameof(Index));
        }

        var attachments = await _attachments.BuildAsync(receipt, format);
        if (attachments.Count == 0)
        {
            TempData["Error"] = "Invalid attachment format.";
            return RedirectToAction(nameof(Index));
        }

        var senderEmail = User.Identity?.Name ?? "Admin";
        var ok = await _emailService.ShareReceiptAsync(
            receipt, toEmail, ccEmail, subject, message, attachments,
            senderDisplay: $"{senderEmail} (Admin)");

        TempData[ok ? "Success" : "Error"] = ok
            ? $"Receipt #{receipt.Id} sent to {toEmail}."
            : "Email send failed. Check the Email Activity log for details.";
        return RedirectToAction(nameof(Index));
    }

    // GET /AdminPayroll/AnnualExport?year=2026
    //
    // CSV export of every Paid receipt in the requested calendar year.
    // Two row types: one summary line per contractor ("CONTRACTOR_TOTAL")
    // and one detail line per receipt. AP / bookkeeping can ingest this
    // directly when issuing 1099s without any reshaping.
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AnnualExport(int? year)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var rangeStart = new DateTime(y, 1, 1);
        var rangeEnd   = new DateTime(y + 1, 1, 1);

        // "In year Y" = paid OR the period falls within Y. We use PaidDate
        // when present (the money-movement event) and fall back to
        // PeriodEnd so receipts paid in early next year for late-Dec work
        // still show up where you'd expect.
        var receipts = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Where(r => r.Status == "Paid"
                && ((r.PaidDate.HasValue && r.PaidDate >= rangeStart && r.PaidDate < rangeEnd)
                    || (!r.PaidDate.HasValue && r.PeriodEnd >= rangeStart && r.PeriodEnd < rangeEnd)))
            .OrderBy(r => r.ContractorId)
            .ThenBy(r => r.PeriodStart)
            .ToListAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("RowType,ContractorId,ContractorName,ContractorEmail,ReceiptId,PeriodStart,PeriodEnd,PaidDate,PaymentMethod,PaymentReference,TotalHours,BillableHours,Rate,Amount");

        string Esc(string? s) =>
            string.IsNullOrEmpty(s) ? "" :
            (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

        foreach (var group in receipts.GroupBy(r => r.ContractorId))
        {
            var firstContractor = group.First().Contractor;
            var name  = firstContractor != null ? $"{firstContractor.FirstName} {firstContractor.LastName}" : "(unknown)";
            var email = firstContractor?.Email ?? "";
            var totalAmount = group.Sum(r => r.TotalAmount);
            var totalHours  = group.Sum(r => r.TotalHours);
            var totalBill   = group.Sum(r => r.TotalBillableHours);

            sb.AppendLine(string.Join(",",
                "CONTRACTOR_TOTAL",
                group.Key,
                Esc(name),
                Esc(email),
                "",
                "",
                "",
                "",
                "",
                "",
                totalHours.ToString("0.##"),
                totalBill.ToString("0.##"),
                "",
                totalAmount.ToString("F2")));

            foreach (var r in group)
            {
                sb.AppendLine(string.Join(",",
                    "RECEIPT",
                    r.ContractorId,
                    Esc(name),
                    Esc(email),
                    r.Id,
                    r.PeriodStart.ToString("yyyy-MM-dd"),
                    r.PeriodEnd.ToString("yyyy-MM-dd"),
                    r.PaidDate?.ToString("yyyy-MM-dd") ?? "",
                    Esc(r.PaymentMethod),
                    Esc(r.PaymentReference),
                    r.TotalHours.ToString("0.##"),
                    r.TotalBillableHours.ToString("0.##"),
                    r.HourlyRateSnapshot.ToString("F2"),
                    r.TotalAmount.ToString("F2")));
            }
        }

        var fileName = $"PayrollAnnualExport_{y}.csv";
        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", fileName);
    }

    // POST /AdminPayroll/PostComment/{id}
    //
    // Admin posts into a receipt's activity thread. Useful for asking a
    // clarification on a Submitted receipt without having to formally
    // reject it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostComment(int id, string body, string? returnUrl)
    {
        var receipt = await _context.PayrollReceipts.FindAsync(id);
        if (receipt == null) return NotFound();

        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Comment cannot be empty.";
        }
        else
        {
            var admin = await GetActingAdminAsync();
            if (admin != null)
            {
                await _activity.LogAdminAsync(receipt.Id, admin, body.Trim());

                var linkUrl = ContractorReceiptLink(Url, receipt.Id);
                // Always ping the contractor about an admin comment on
                // their receipt — they own the receipt; they should know
                // someone weighed in.
                await NotifyContractorOnBellAsync(
                    receipt.ContractorId, "PayrollComment",
                    title:   $"New comment on receipt #{receipt.Id}",
                    message: body.Length > 140 ? body[..140] + "…" : body,
                    link:    linkUrl,
                    icon:    "bi-chat-square-text");

                // @mentions get an explicit "you were mentioned" ping
                // (separate notification type so the user sees the
                // distinction in their feed). Author is excluded so the
                // admin doesn't notify themselves.
                await _mentions.ProcessMentionsAsync(
                    body:              body,
                    authorDisplayName: $"{admin.FirstName} {admin.LastName}",
                    sourceLabel:       $"Receipt #{receipt.Id}",
                    linkUrl:           linkUrl,
                    notificationType:  "ReceiptMention",
                    excludeUserId:     admin.Id);

                TempData["Success"] = "Comment posted.";
            }
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("ReceiptDetail", "Contractor", new { id });
    }

    // ── Notification recipients management ────────────────────────────────
    //
    // Lets an admin curate exactly who receives the "receipt submitted"
    // email. Useful when the alert should go to HR or AP rather than to
    // every admin. Empty list falls back to "all admins" — see
    // EmailNotificationService.NotifyReceiptSubmittedAsync.

    // GET /AdminPayroll/NotificationRecipients
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> NotificationRecipients()
    {
        var recipients = await _context.PayrollNotificationRecipients
            .Include(r => r.PortalUser)
            .OrderByDescending(r => r.IsActive)
            .ThenBy(r => r.DisplayName ?? (r.PortalUser != null ? r.PortalUser.FirstName : r.Email))
            .ToListAsync();

        // Per-event email toggles — load all 4 in one round-trip and bind
        // sensible defaults so a fresh install with no seed rows still
        // shows the panel in a reasonable state (Submit/Approved/Rejected
        // default on; Paid defaults on as well).
        var toggleKeys = new[]
        {
            "NotifyOnPayrollSubmit",
            "NotifyOnPayrollApproved",
            "NotifyOnPayrollRejected",
            "NotifyOnPayrollPaid",
            "PayrollReminderEnabled",
            "PayrollReminderDays",
            "PayrollNotificationDeliveryMode"
        };
        var settings = await _context.AppSettings
            .Where(s => toggleKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        bool BoolFromSettings(string key, bool defaultValue = true)
            => settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;

        ViewBag.NotifyOnSubmit    = BoolFromSettings("NotifyOnPayrollSubmit");
        ViewBag.NotifyOnApproved  = BoolFromSettings("NotifyOnPayrollApproved");
        ViewBag.NotifyOnRejected  = BoolFromSettings("NotifyOnPayrollRejected");
        ViewBag.NotifyOnPaid      = BoolFromSettings("NotifyOnPayrollPaid");
        ViewBag.ReminderEnabled   = BoolFromSettings("PayrollReminderEnabled", defaultValue: false);
        ViewBag.ReminderDays      = settings.TryGetValue("PayrollReminderDays", out var dv)
                                    && int.TryParse(dv, out var dn) && dn > 0 ? dn : 3;
        ViewBag.DeliveryMode      = settings.TryGetValue("PayrollNotificationDeliveryMode", out var dm)
                                    && string.Equals(dm, "Combined", StringComparison.OrdinalIgnoreCase)
                                    ? "Combined" : "Individual";

        // For the "Add from portal user" dropdown — active users only,
        // excluding anyone already on the recipient list.
        var existingPortalIds = recipients
            .Where(r => r.PortalUserId.HasValue)
            .Select(r => r.PortalUserId!.Value)
            .ToHashSet();

        var portalCandidates = await _context.PortalUsers
            .Include(u => u.Role)
            .Where(u => u.IsActive && !string.IsNullOrEmpty(u.Email)
                     && !existingPortalIds.Contains(u.Id))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .ToListAsync();

        ViewBag.PortalCandidates = portalCandidates;
        return View(recipients);
    }

    // POST /AdminPayroll/SaveNotificationToggles
    //
    // Per-event email switches. Each toggle is rendered as a single
    // checkbox so an unchecked box doesn't post anything — the action
    // params default to false and we write false to AppSettings in that
    // case. That way the saved state always matches what's in the UI.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNotificationToggles(
        bool notifyOnSubmit,
        bool notifyOnApproved,
        bool notifyOnRejected,
        bool notifyOnPaid)
    {
        await UpsertSettingAsync("NotifyOnPayrollSubmit",   notifyOnSubmit   ? "true" : "false");
        await UpsertSettingAsync("NotifyOnPayrollApproved", notifyOnApproved ? "true" : "false");
        await UpsertSettingAsync("NotifyOnPayrollRejected", notifyOnRejected ? "true" : "false");
        await UpsertSettingAsync("NotifyOnPayrollPaid",     notifyOnPaid     ? "true" : "false");
        TempData["Success"] = "Email notification toggles saved.";
        return RedirectToAction(nameof(NotificationRecipients));
    }

    // POST /AdminPayroll/SaveDeliveryMode
    //
    // Switches between "Individual" (one email per recipient, default
    // and privacy-safe) and "Combined" (one email with first recipient
    // in To and the rest in Cc — recipients see each other and can
    // Reply-All).
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDeliveryMode(string deliveryMode)
    {
        var normalized = string.Equals(deliveryMode, "Combined", StringComparison.OrdinalIgnoreCase)
            ? "Combined" : "Individual";
        await UpsertSettingAsync("PayrollNotificationDeliveryMode", normalized);
        TempData["Success"] = normalized == "Combined"
            ? "Payroll alerts will now be sent as a single email with all recipients on the To/Cc lines."
            : "Payroll alerts will now be sent individually — one email per recipient.";
        return RedirectToAction(nameof(NotificationRecipients));
    }

    // POST /AdminPayroll/SaveReminderSettings
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReminderSettings(bool reminderEnabled, int reminderDays)
    {
        if (reminderDays < 1) reminderDays = 1;
        if (reminderDays > 30) reminderDays = 30;

        await UpsertSettingAsync("PayrollReminderEnabled", reminderEnabled ? "true" : "false");
        await UpsertSettingAsync("PayrollReminderDays", reminderDays.ToString());

        TempData["Success"] = reminderEnabled
            ? $"Reminders enabled — admins will be nudged when a Submitted receipt sits for more than {reminderDays} day(s)."
            : "Reminders disabled.";
        return RedirectToAction(nameof(NotificationRecipients));
    }

    private async Task UpsertSettingAsync(string key, string value)
    {
        var existing = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (existing == null)
            _context.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            existing.Value = value;
        await _context.SaveChangesAsync();
    }

    // POST /AdminPayroll/AddNotificationRecipient
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNotificationRecipient(int? portalUserId, string? email, string? displayName)
    {
        var addedBy = int.TryParse(User.FindFirstValue("PortalUserId"), out var pid) ? pid : (int?)null;

        if (portalUserId.HasValue)
        {
            var user = await _context.PortalUsers.FindAsync(portalUserId.Value);
            if (user == null || string.IsNullOrEmpty(user.Email))
            {
                TempData["Error"] = "Selected user has no email on file.";
                return RedirectToAction(nameof(NotificationRecipients));
            }

            var dupe = await _context.PayrollNotificationRecipients
                .AnyAsync(r => r.PortalUserId == user.Id);
            if (dupe)
            {
                TempData["Error"] = $"{user.FirstName} {user.LastName} is already on the recipient list.";
                return RedirectToAction(nameof(NotificationRecipients));
            }

            _context.PayrollNotificationRecipients.Add(new PayrollNotificationRecipient
            {
                PortalUserId = user.Id,
                Email = user.Email,
                DisplayName = $"{user.FirstName} {user.LastName}",
                IsActive = true,
                AddedByPortalUserId = addedBy
            });
            await _context.SaveChangesAsync();
            TempData["Success"] = $"{user.FirstName} {user.LastName} will receive payroll receipt notifications.";
            return RedirectToAction(nameof(NotificationRecipients));
        }

        // External email path
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Provide either a portal user or an email address.";
            return RedirectToAction(nameof(NotificationRecipients));
        }

        var normalized = email.Trim();
        var existing = await _context.PayrollNotificationRecipients
            .AnyAsync(r => r.PortalUserId == null && r.Email == normalized);
        if (existing)
        {
            TempData["Error"] = $"{normalized} is already on the recipient list.";
            return RedirectToAction(nameof(NotificationRecipients));
        }

        _context.PayrollNotificationRecipients.Add(new PayrollNotificationRecipient
        {
            PortalUserId = null,
            Email = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            IsActive = true,
            AddedByPortalUserId = addedBy
        });
        await _context.SaveChangesAsync();
        TempData["Success"] = $"{normalized} will receive payroll receipt notifications.";
        return RedirectToAction(nameof(NotificationRecipients));
    }

    // POST /AdminPayroll/ToggleNotificationRecipient/{id}
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleNotificationRecipient(int id)
    {
        var r = await _context.PayrollNotificationRecipients.FindAsync(id);
        if (r == null) return NotFound();
        r.IsActive = !r.IsActive;
        await _context.SaveChangesAsync();
        TempData["Success"] = $"Recipient {(r.IsActive ? "enabled" : "paused")}.";
        return RedirectToAction(nameof(NotificationRecipients));
    }

    // POST /AdminPayroll/RemoveNotificationRecipient/{id}
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveNotificationRecipient(int id)
    {
        var r = await _context.PayrollNotificationRecipients.FindAsync(id);
        if (r == null) return NotFound();
        _context.PayrollNotificationRecipients.Remove(r);
        await _context.SaveChangesAsync();
        TempData["Success"] = "Recipient removed.";
        return RedirectToAction(nameof(NotificationRecipients));
    }
}
