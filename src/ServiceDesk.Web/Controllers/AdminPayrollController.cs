using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;
using System.Security.Claims;
using System.Text;

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

        // Defaults for the "Forward on Approve" workflow — pre-fill the
        // HR / AP email fields in the approval modal so the admin can
        // one-click forward without re-typing on every approval.
        var defaultsLookup = await _context.AppSettings
            .Where(s => s.Key == "PayrollHrEmail" || s.Key == "PayrollApEmail")
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        ViewBag.DefaultHrEmail = defaultsLookup.TryGetValue("PayrollHrEmail", out var hr) ? hr : "";
        ViewBag.DefaultApEmail = defaultsLookup.TryGetValue("PayrollApEmail", out var ap) ? ap : "";

        // ── Per-contractor rollup for the collapsible summary card ──
        // Pull every payment row once; group in memory to build the per-
        // contractor Paid / Outstanding totals + oldest outstanding date.
        var receiptIds = allReceipts.Select(r => r.Id).ToHashSet();
        var allPaymentRows = await _context.PayrollReceiptPayments
            .Where(p => receiptIds.Contains(p.PayrollReceiptId))
            .Select(p => new { p.PayrollReceiptId, p.Amount, p.PaymentDate })
            .ToListAsync();
        var paidByReceipt = allPaymentRows
            .GroupBy(p => p.PayrollReceiptId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var jan1 = new DateTime(now.Year, 1, 1);
        var contractorSummaries = allReceipts
            .Where(r => r.Contractor != null)
            .GroupBy(r => r.ContractorId)
            .Select(g =>
            {
                var contractor = g.First().Contractor!;
                var ytdPaid   = g.Where(r => r.Status == "Paid" && r.PaidDate.HasValue && r.PaidDate.Value >= jan1)
                                 .Sum(r => r.TotalAmount);
                var outstandingReceipts = g.Where(r =>
                        r.Status == "Approved" || r.Status == "PartiallyPaid"
                        || (r.Status == "Paid" && paidByReceipt.GetValueOrDefault(r.Id, 0m) < r.TotalAmount))
                    .Select(r =>
                    {
                        var paid = paidByReceipt.GetValueOrDefault(r.Id, 0m);
                        return new
                        {
                            Receipt = r,
                            Outstanding = Math.Max(0m, Math.Round(r.TotalAmount - paid, 2, MidpointRounding.AwayFromZero)),
                        };
                    })
                    .Where(x => x.Outstanding > 0m)
                    .ToList();

                var totalOutstanding = outstandingReceipts.Sum(x => x.Outstanding);
                var partialCount = g.Count(r => r.Status == "PartiallyPaid");
                var approvedUnpaidAmount = g.Where(r => r.Status == "Approved")
                                            .Sum(r => r.TotalAmount - paidByReceipt.GetValueOrDefault(r.Id, 0m));
                var oldestOutstanding = outstandingReceipts
                    .Select(x => (DateTime?)(x.Receipt.ApprovedDate ?? x.Receipt.CreatedDate))
                    .DefaultIfEmpty(null)
                    .Min();

                return new AdminPayrollContractorSummary(
                    ContractorId:        contractor.Id,
                    ContractorName:      $"{contractor.FirstName} {contractor.LastName}",
                    ReceiptCount:        g.Count(),
                    YtdPaid:             ytdPaid,
                    ApprovedUnpaidAmount:Math.Max(0m, approvedUnpaidAmount),
                    PartiallyPaidCount:  partialCount,
                    Outstanding:         totalOutstanding,
                    OldestOutstandingAt: oldestOutstanding);
            })
            .OrderByDescending(s => s.Outstanding)
            .ThenBy(s => s.ContractorName)
            .ToList();

        // Per-receipt paid/outstanding for the new column in the receipt list.
        ViewBag.PaidByReceipt        = paidByReceipt;
        ViewBag.ContractorSummaries  = contractorSummaries;
        ViewBag.TotalOutstanding     = contractorSummaries.Sum(s => s.Outstanding);
        ViewBag.TotalYtdPaid         = contractorSummaries.Sum(s => s.YtdPaid);

        ViewData["Title"]          = "Contractor Payroll";
        return View(receipts.ToList());
    }

    /// <summary>Immutable per-contractor summary row used by the AdminPayroll Index collapsible card.</summary>
    public sealed record AdminPayrollContractorSummary(
        int      ContractorId,
        string   ContractorName,
        int      ReceiptCount,
        decimal  YtdPaid,
        decimal  ApprovedUnpaidAmount,
        int      PartiallyPaidCount,
        decimal  Outstanding,
        DateTime? OldestOutstandingAt);

    // POST /AdminPayroll/Approve/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? approvalNote,
        bool sendToHr = false, string? hrEmail = null,
        bool sendToAp = false, string? apEmail = null)
    {
        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .FirstOrDefaultAsync(r => r.Id == id);
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

        // Forward FIRST so the contractor's email reflects the truth.
        // The helper returns which downstream addresses actually
        // succeeded; those flags feed the contractor email body so it
        // can accurately say "forwarded to HR / AP for processing"
        // instead of guessing. If a forward fails we just don't
        // mention it (better than promising something that didn't
        // happen).
        var (forwardCount, hrSent, apSent) = await ForwardOnApprovalAsync(
            receipt, approver, sendToHr, hrEmail, sendToAp, apEmail);

        try { await _emailService.NotifyReceiptApprovedAsync(receipt,
                forwardedToHr: hrSent, forwardedToAp: apSent); }
        catch (Exception) { /* email failure should not block UI flow */ }

        // Tailor the in-app bell message to the same outcome.
        var bellMessage = (hrSent, apSent) switch
        {
            (true, true)  => $"Forwarded to HR and AP · {receipt.TotalAmount:C}",
            (true, false) => $"Forwarded to HR · {receipt.TotalAmount:C}",
            (false, true) => $"Forwarded to AP · {receipt.TotalAmount:C}",
            _             => receipt.ApprovalNote ?? $"{receipt.TotalAmount:C} · awaiting payment"
        };
        await NotifyContractorOnBellAsync(
            receipt.ContractorId,
            type:    "PayrollApproved",
            title:   $"Your receipt #{receipt.Id} was approved",
            message: bellMessage,
            link:    ContractorReceiptLink(Url, receipt.Id),
            icon:    "bi-check-circle");

        TempData["Success"] = forwardCount > 0
            ? $"Receipt approved and forwarded to {forwardCount} recipient(s)."
            : "Receipt approved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Sends a copy of the just-approved receipt to the HR / AP
    /// addresses the admin chose in the modal. Returns a tuple of
    ///   - total successful sends (for the toast)
    ///   - hrSent flag (so the contractor email can mention it)
    ///   - apSent flag (same)
    /// Persists any newly-typed address to AppSettings so subsequent
    /// approvals pre-fill the same value.
    /// </summary>
    private async Task<(int Count, bool HrSent, bool ApSent)> ForwardOnApprovalAsync(
        PayrollReceipt receipt, PortalUser? approver,
        bool sendToHr, string? hrEmail, bool sendToAp, string? apEmail)
    {
        if (!sendToHr && !sendToAp) return (0, false, false);

        // Build the attachment set once — both addresses receive the
        // same PDF + XLSX bundle.
        var attachments = await _attachments.BuildAsync(receipt, "both");
        var senderDisplay = approver != null
            ? $"{approver.FirstName} {approver.LastName} (Admin)"
            : "Admin";

        var subject = $"Approved Receipt #{receipt.Id} — " +
            (receipt.Contractor != null
                ? $"{receipt.Contractor.FirstName} {receipt.Contractor.LastName}"
                : "Contractor") +
            $" — {receipt.TotalAmount:C}";

        async Task<bool> ForwardOneAsync(string label, string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var message = $"Hi {label} — receipt #{receipt.Id} just cleared approval, please process " +
                          $"in the next pay run. Approval note: " +
                          (string.IsNullOrWhiteSpace(receipt.ApprovalNote)
                              ? "(none)"
                              : receipt.ApprovalNote);
            try
            {
                return await _emailService.ShareReceiptAsync(
                    receipt, email.Trim(), ccEmail: null,
                    subjectOverride: subject, message: message,
                    attachments: attachments, senderDisplay: senderDisplay);
            }
            catch (Exception) { return false; }
        }

        var hrSent = false;
        var apSent = false;
        if (sendToHr && await ForwardOneAsync("HR", hrEmail ?? ""))
        {
            hrSent = true;
            await UpsertSettingAsync("PayrollHrEmail", (hrEmail ?? "").Trim());
        }
        if (sendToAp && await ForwardOneAsync("Accounts Payable", apEmail ?? ""))
        {
            apSent = true;
            await UpsertSettingAsync("PayrollApEmail", (apEmail ?? "").Trim());
        }
        var total = (hrSent ? 1 : 0) + (apSent ? 1 : 0);
        return (total, hrSent, apSent);
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

    // POST /AdminPayroll/RecordPayment/{id}
    //
    // Adds a single payment row against a receipt. The receipt's Status
    // auto-transitions based on the running sum of amounts:
    //   Approved → PartiallyPaid (first sub-total payment)
    //   PartiallyPaid → Paid    (when the sum reaches TotalAmount)
    // Sends a contractor notification with paid-so-far and outstanding balance.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPayment(int id,
        decimal amount, DateTime paymentDate, string paymentMethod,
        string? checkNumber, string? reference, string? note,
        string? returnUrl)
    {
        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (receipt == null) return NotFound();

        if (receipt.Status != "Approved" && receipt.Status != "PartiallyPaid" && receipt.Status != "Paid")
        {
            TempData["Error"] = "Payments can only be recorded on Approved, PartiallyPaid, or Paid receipts.";
            return SafeRedirect(returnUrl, nameof(Index));
        }
        if (amount <= 0m)
        {
            TempData["Error"] = "Payment amount must be greater than zero.";
            return SafeRedirect(returnUrl, nameof(Index));
        }

        var method = string.IsNullOrWhiteSpace(paymentMethod) ? "Other" : paymentMethod.Trim();
        var admin = await GetActingAdminAsync();
        var recorderName = admin?.FullName;

        var payment = new PayrollReceiptPayment
        {
            PayrollReceiptId       = receipt.Id,
            PaymentDate            = paymentDate == default ? DateTime.UtcNow : paymentDate,
            Amount                 = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            PaymentMethod          = method,
            CheckNumber            = string.IsNullOrWhiteSpace(checkNumber) ? null : checkNumber.Trim(),
            Reference              = string.IsNullOrWhiteSpace(reference)   ? null : reference.Trim(),
            Note                   = string.IsNullOrWhiteSpace(note)        ? null : note.Trim(),
            RecordedByEmployeeId   = admin != null ? await _context.Employees
                                        .Where(e => e.Email == admin.Email)
                                        .Select(e => (int?)e.Id).FirstOrDefaultAsync()
                                       : null,
            RecordedByName         = recorderName,
            CreatedDate            = DateTime.UtcNow,
        };
        _context.PayrollReceiptPayments.Add(payment);
        await _context.SaveChangesAsync();

        // Recompute status + last-payment snapshot fields on the receipt.
        var (totalPaid, outstanding, statusChanged) = await RecomputeReceiptPaymentStateAsync(receipt);

        // Log the payment to the activity thread. Include enough detail that
        // the audit trail is self-explanatory when read years later.
        if (admin != null)
        {
            var detail = new StringBuilder();
            detail.Append($"Recorded payment of {payment.Amount:C2} via {payment.PaymentMethod}");
            if (!string.IsNullOrEmpty(payment.CheckNumber)) detail.Append($" (check #{payment.CheckNumber})");
            if (!string.IsNullOrEmpty(payment.Reference))   detail.Append($" — ref {payment.Reference}");
            detail.Append($". Total paid {totalPaid:C2} of {receipt.TotalAmount:C2}");
            if (outstanding > 0) detail.Append($" ({outstanding:C2} outstanding).");
            else                 detail.Append(" — fully paid.");
            await _activity.LogAdminAsync(receipt.Id, admin, detail.ToString());
        }

        // Contractor email + bell. On the final payment we use the existing
        // "receipt paid" template; on partials we use a new one.
        try
        {
            if (outstanding <= 0m)
                await _emailService.NotifyReceiptPaidAsync(receipt);
            else
                await _emailService.NotifyPartialPaymentAsync(receipt, payment, totalPaid, outstanding);
        }
        catch (Exception) { /* email failure should not block UI flow */ }

        var bellTitle = outstanding <= 0m
            ? $"Receipt #{receipt.Id} fully paid"
            : $"Partial payment on receipt #{receipt.Id}";
        var bellMessage = outstanding <= 0m
            ? $"Final payment of {payment.Amount:C2} recorded. Total {totalPaid:C2}."
            : $"{payment.Amount:C2} recorded. Paid {totalPaid:C2} of {receipt.TotalAmount:C2} — {outstanding:C2} outstanding.";
        await NotifyContractorOnBellAsync(
            receipt.ContractorId,
            type:    outstanding <= 0m ? "PayrollPaid" : "PayrollPartialPaid",
            title:   bellTitle,
            message: bellMessage,
            link:    ContractorReceiptLink(Url, receipt.Id),
            icon:    outstanding <= 0m ? "bi-cash-coin" : "bi-cash");

        TempData["Success"] = outstanding <= 0m
            ? $"Payment of {payment.Amount:C2} recorded. Receipt fully paid ({totalPaid:C2}) — status set to Paid."
            : $"Payment of {payment.Amount:C2} recorded. Paid so far {totalPaid:C2} of {receipt.TotalAmount:C2} ({outstanding:C2} outstanding).";
        return SafeRedirect(returnUrl, nameof(Index));
    }

    // POST /AdminPayroll/DeletePayment/{paymentId}
    //
    // Removes one payment row, then recomputes the receipt's status +
    // last-payment snapshot fields. Used when a check bounces or was
    // logged in error. Logged to the activity thread with the admin's name.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePayment(int paymentId, string? returnUrl)
    {
        var payment = await _context.PayrollReceiptPayments
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null || payment.Receipt == null) return NotFound();

        var receipt = payment.Receipt;
        var deletedAmount = payment.Amount;
        var deletedMethod = payment.PaymentMethod;
        var deletedRef    = payment.Reference ?? payment.CheckNumber;

        _context.PayrollReceiptPayments.Remove(payment);
        await _context.SaveChangesAsync();

        var (totalPaid, outstanding, _) = await RecomputeReceiptPaymentStateAsync(receipt);

        var admin = await GetActingAdminAsync();
        if (admin != null)
        {
            var detail = new StringBuilder();
            detail.Append($"Deleted payment of {deletedAmount:C2} ({deletedMethod}");
            if (!string.IsNullOrEmpty(deletedRef)) detail.Append($" — {deletedRef}");
            detail.Append($"). Total paid {totalPaid:C2} of {receipt.TotalAmount:C2}");
            if (outstanding > 0) detail.Append($" ({outstanding:C2} outstanding).");
            else                 detail.Append(" — fully paid.");
            await _activity.LogAdminAsync(receipt.Id, admin, detail.ToString());
        }

        TempData["Success"] = $"Payment removed. Total paid now {totalPaid:C2}.";
        return SafeRedirect(returnUrl, nameof(Index));
    }

    // POST /AdminPayroll/NotifyPaymentStatus/{id}
    //
    // Admin-triggered "here's what's paid, here's what's owed" email to
    // whichever address(es) the admin types in (defaults pre-filled from
    // PayrollHrEmail / PayrollApEmail on the modal, but fully editable).
    // Distinct from the contractor's own Request Payment nudge — no rate
    // limit, no fixed recipient list, and framed as an admin-to-admin/HR/AP
    // status broadcast rather than a payee's reminder.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NotifyPaymentStatus(int id, string toEmail, string? ccEmail,
        string? note, string? returnUrl)
    {
        var receipt = await _context.PayrollReceipts
            .Include(r => r.Contractor)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (receipt == null) return NotFound();

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            TempData["Error"] = "Recipient email is required.";
            return SafeRedirect(returnUrl, nameof(Index));
        }

        var totalPaid = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receipt.Id)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var outstanding = Math.Max(0m,
            Math.Round(receipt.TotalAmount - totalPaid, 2, MidpointRounding.AwayFromZero));

        if (outstanding <= 0m)
        {
            TempData["Warning"] = "This receipt has no outstanding balance — nothing to notify about.";
            return SafeRedirect(returnUrl, nameof(Index));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote?.Length > 500) trimmedNote = trimmedNote[..500];

        var admin = await GetActingAdminAsync();
        var senderDisplay = admin != null ? $"{admin.FullName} (Admin)" : "Admin";

        var attachments = await _attachments.BuildAsync(receipt, "pdf");
        var ok = await _emailService.SendPaymentStatusUpdateAsync(
            receipt, totalPaid, outstanding, toEmail.Trim(), ccEmail, trimmedNote, senderDisplay, attachments);

        if (ok && admin != null)
        {
            var detail = $"Sent payment status update to {toEmail.Trim()}" +
                         (string.IsNullOrEmpty(ccEmail) ? "" : $" (cc {ccEmail})") +
                         $" — {totalPaid:C2} paid, {outstanding:C2} outstanding.";
            await _activity.LogAdminAsync(receipt.Id, admin, detail);
        }

        TempData[ok ? "Success" : "Error"] = ok
            ? $"Payment status sent to {toEmail.Trim()}."
            : "Email send failed. Check the Email Activity log for details.";
        return SafeRedirect(returnUrl, nameof(Index));
    }

    /// <summary>
    /// Sums this receipt's payments, sets the receipt Status based on the
    /// running total, and refreshes the "last payment" snapshot fields on
    /// the receipt (PaidDate / PaymentMethod / PaymentReference) so
    /// downstream code that reads those directly stays in sync. Saves and
    /// returns (totalPaid, outstanding, statusChanged).
    /// </summary>
    private async Task<(decimal TotalPaid, decimal Outstanding, bool StatusChanged)>
        RecomputeReceiptPaymentStateAsync(PayrollReceipt receipt)
    {
        var payments = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receipt.Id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        var totalPaid   = payments.Sum(p => p.Amount);
        var outstanding = Math.Max(0m, Math.Round(receipt.TotalAmount - totalPaid, 2, MidpointRounding.AwayFromZero));
        var oldStatus   = receipt.Status;

        // Status transitions. Only touch the status when the receipt is
        // sitting in a "post-approval" state — never demote from Rejected or
        // pull a Submitted receipt into PartiallyPaid.
        if (receipt.Status == "Approved" || receipt.Status == "PartiallyPaid" || receipt.Status == "Paid")
        {
            if (totalPaid <= 0m)          receipt.Status = "Approved";
            else if (outstanding > 0m)    receipt.Status = "PartiallyPaid";
            else                          receipt.Status = "Paid";
        }

        // Keep the single-payment snapshot fields in sync so PDF / email
        // templates that still read them show the LATEST payment.
        var latest = payments.FirstOrDefault();
        receipt.PaidDate         = latest?.PaymentDate;
        receipt.PaymentMethod    = latest?.PaymentMethod;
        receipt.PaymentReference = latest?.Reference ?? latest?.CheckNumber;

        await _context.SaveChangesAsync();
        return (totalPaid, outstanding, receipt.Status != oldStatus);
    }

    /// <summary>
    /// Post handlers reached from either the AdminPayroll queue or the
    /// contractor-side ReceiptDetail want to bounce back to the caller's
    /// page. Guards against open-redirect abuse by requiring the URL to
    /// be a local path.
    /// </summary>
    private IActionResult SafeRedirect(string? returnUrl, string fallbackAction)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(fallbackAction);
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
            "PayrollNotificationDeliveryMode",
            "PayrollHrEmail",
            "PayrollApEmail"
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
        ViewBag.HrEmail = settings.TryGetValue("PayrollHrEmail", out var hr) ? hr ?? "" : "";
        ViewBag.ApEmail = settings.TryGetValue("PayrollApEmail", out var ap) ? ap ?? "" : "";

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

    // POST /AdminPayroll/SaveForwardDefaults
    //
    // Persists the default HR / AP email addresses the Approve modal
    // will pre-fill from. Empty strings are written through so an admin
    // can intentionally clear a default without it falling back to a
    // stale value. Trim trims away whitespace pastes.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveForwardDefaults(string? hrEmail, string? apEmail)
    {
        await UpsertSettingAsync("PayrollHrEmail", (hrEmail ?? "").Trim());
        await UpsertSettingAsync("PayrollApEmail", (apEmail ?? "").Trim());
        TempData["Success"] = "Forward-on-approval defaults saved.";
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
