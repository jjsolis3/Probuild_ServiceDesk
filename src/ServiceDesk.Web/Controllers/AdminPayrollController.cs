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

    public AdminPayrollController(ServiceDeskDbContext context,
        EmailNotificationService emailService,
        PayrollReceiptAttachmentService attachments)
    {
        _context = context;
        _emailService = emailService;
        _attachments = attachments;
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
