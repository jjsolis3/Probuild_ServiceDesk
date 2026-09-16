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
    private readonly PayrollReceiptPdfService _pdfService;
    private readonly PayrollActivityService _activity;
    private readonly PortalNotificationService _bell;
    private readonly MentionService _mentions;

    public ContractorController(
        ServiceDeskDbContext context,
        EmailNotificationService emailService,
        PayrollCalculatorService payroll,
        PayrollReceiptAttachmentService attachments,
        PayrollReceiptPdfService pdfService,
        PayrollActivityService activity,
        PortalNotificationService bell,
        MentionService mentions)
    {
        _context = context;
        _emailService = emailService;
        _payroll = payroll;
        _attachments = attachments;
        _pdfService = pdfService;
        _activity = activity;
        _bell = bell;
        _mentions = mentions;
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

        // YTD aggregates — split between Paid (cash actually in hand) and
        // Submitted/Approved (in-flight). January 1 of the current year in
        // the contractor's local time is good enough — payroll dates are
        // tracked at day-precision so timezone drift around year-end is a
        // non-issue here.
        var ytdStart = new DateTime(DateTime.UtcNow.Year, 1, 1);
        var ytdReceipts = receipts.Where(r => r.PeriodStart >= ytdStart || r.PeriodEnd >= ytdStart).ToList();

        ViewBag.YtdPaidAmount     = ytdReceipts.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount);
        ViewBag.YtdPaidHours      = ytdReceipts.Where(r => r.Status == "Paid").Sum(r => r.TotalHours);
        ViewBag.YtdPendingAmount  = ytdReceipts.Where(r => r.Status == "Submitted" || r.Status == "Approved").Sum(r => r.TotalAmount);
        ViewBag.YtdPendingHours   = ytdReceipts.Where(r => r.Status == "Submitted" || r.Status == "Approved").Sum(r => r.TotalHours);
        ViewBag.YtdReceiptCount   = ytdReceipts.Count(r => r.Status != "Draft");
        ViewBag.YtdYear           = DateTime.UtcNow.Year;

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

        // Recurring-charge templates active and in-window for this period.
        // Each renders as one pre-checked row on the receipt form with an
        // editable Occurrences input.
        var (templates, snapshots) = await BuildChargeCandidatesAsync(contractor, start, end);
        ViewBag.ChargeTemplates = templates;
        ViewBag.ChargeSnapshots = snapshots;

        // Preview the same totals the POST handler will persist — keeps the
        // user from being surprised by retainer / rate-type math on submit.
        ViewBag.PayrollCalc = await _payroll.CalculateAsync(contractor, entries, charges: snapshots);

        return View(entries);
    }

    // POST /Contractor/NewReceipt
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewReceipt(DateTime periodStart, DateTime periodEnd, string? notes,
        int[]? selectedEntryIds,
        int[]? selectedChargeTemplateIds, int[]? chargeOccurrenceCounts,
        string[]? adhocLabels, string[]? adhocPricingModes, decimal[]? adhocAmounts)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
            return RedirectToAction(nameof(Payroll));

        if (periodEnd < periodStart)
        {
            TempData["Error"] = "Period end must be on or after period start.";
            return RedirectToAction(nameof(NewReceipt), new { periodStart, periodEnd });
        }

        var hasEntries  = selectedEntryIds != null && selectedEntryIds.Length > 0;
        var hasCharges  = selectedChargeTemplateIds != null && selectedChargeTemplateIds.Length > 0;
        var adhocSnapshots = BuildAdhocChargeSnapshots(contractor, adhocLabels, adhocPricingModes, adhocAmounts);
        var hasAdhoc    = adhocSnapshots.Count > 0;
        if (!hasEntries && !hasCharges && !hasAdhoc)
        {
            TempData["Error"] = "Pick at least one entry, recurring charge, or ad-hoc charge to include on the receipt.";
            return RedirectToAction(nameof(NewReceipt), new { periodStart, periodEnd });
        }

        var available = await GetUnclaimedEntriesAsync(contractor.Id, periodStart, periodEnd);
        var requestedSet = (selectedEntryIds ?? Array.Empty<int>()).ToHashSet();
        var entries = available.Where(e => requestedSet.Contains(e.Id)).ToList();

        var snapshots = await BuildSelectedChargeSnapshotsAsync(
            contractor, periodStart, periodEnd, selectedChargeTemplateIds, chargeOccurrenceCounts);

        // Merge ad-hoc snapshots in alongside template-derived ones — the
        // calculator and downstream UI treat both lists identically.
        snapshots.AddRange(adhocSnapshots);

        if (entries.Count == 0 && snapshots.Count == 0)
        {
            TempData["Warning"] = "The selected entries / charges are no longer available (they may have been claimed, deleted, or are out of the period).";
            return RedirectToAction(nameof(NewReceipt), new { periodStart, periodEnd });
        }

        var droppedCount = (selectedEntryIds?.Length ?? 0) - entries.Count;

        var calc = await _payroll.CalculateAsync(contractor, entries, charges: snapshots);

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
            TotalRecurringChargesAmount    = calc.TotalRecurringChargesAmount,
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

        // Attach the recurring-charge snapshots — assigning to the FK is
        // enough (no need to set Receipt nav).
        foreach (var snap in snapshots)
        {
            snap.PayrollReceiptId = receipt.Id;
            _context.PayrollReceiptCharges.Add(snap);
        }

        await _context.SaveChangesAsync();

        TempData["Success"] = droppedCount > 0
            ? $"Payroll receipt created as Draft. ({droppedCount} selected entr{(droppedCount == 1 ? "y was" : "ies were")} no longer available and got skipped.)"
            : "Payroll receipt created as Draft.";
        return RedirectToAction(nameof(ReceiptDetail), new { id = receipt.Id });
    }

    // POST /Contractor/RecalcReceiptPreview
    // Re-renders the summary card body for the New Receipt page when the
    // contractor ticks / unticks entries or charges, or changes a charge's
    // occurrence override. Returns the partial as HTML so the client can
    // swap it in directly.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecalcReceiptPreview(DateTime periodStart, DateTime periodEnd,
        int[]? selectedEntryIds,
        int[]? selectedChargeTemplateIds, int[]? chargeOccurrenceCounts,
        string[]? adhocLabels, string[]? adhocPricingModes, decimal[]? adhocAmounts)
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return Forbid();

        ViewBag.Contractor = contractor;

        var available = await GetUnclaimedEntriesAsync(contractor.Id, periodStart, periodEnd);
        var requestedSet = (selectedEntryIds ?? Array.Empty<int>()).ToHashSet();
        var entries = available.Where(e => requestedSet.Contains(e.Id)).ToList();

        var snapshots = await BuildSelectedChargeSnapshotsAsync(
            contractor, periodStart, periodEnd, selectedChargeTemplateIds, chargeOccurrenceCounts);
        snapshots.AddRange(BuildAdhocChargeSnapshots(contractor, adhocLabels, adhocPricingModes, adhocAmounts));

        var calc = (entries.Count == 0 && snapshots.Count == 0)
            ? null
            : await _payroll.CalculateAsync(contractor, entries, charges: snapshots);

        return PartialView("_NewReceiptSummary", calc);
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
            .Include(r => r.Charges)
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
        // Passing the saved charges snapshot folds them back into TotalAmount.
        ViewBag.PayrollCalc = await _payroll.CalculateAsync(
            receipt.Contractor!, receipt.TimeEntries.ToList(),
            receiptIdToIgnore: receipt.Id,
            charges: receipt.Charges.ToList());

        ViewBag.CompanyName = companyName;
        ViewBag.Comments = await _context.PayrollReceiptComments
            .Where(c => c.PayrollReceiptId == receipt.Id)
            .OrderBy(c => c.CreatedDate)
            .ToListAsync();

        // Payments Received card — newest first for the UI. TotalPaid /
        // Outstanding are computed here so the view stays dumb.
        var payments = await _context.PayrollReceiptPayments
            .Where(p => p.PayrollReceiptId == receipt.Id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();
        var totalPaid = payments.Sum(p => p.Amount);
        var outstanding = Math.Max(0m,
            Math.Round(receipt.TotalAmount - totalPaid, 2, MidpointRounding.AwayFromZero));
        ViewBag.Payments             = payments;
        ViewBag.TotalPaid            = totalPaid;
        ViewBag.OutstandingBalance   = outstanding;

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

        // In-app bell: ping every active admin so the receipt shows up
        // on their notification feed alongside the email. The
        // configured-recipients table isn't relevant for the bell —
        // payroll IS an admin-side task, so admins always see it.
        await NotifyAdminsOnBellAsync(
            type:    "PayrollSubmitted",
            title:   $"Receipt #{receipt.Id} submitted by {contractor.FirstName} {contractor.LastName}",
            message: $"{receipt.TotalHours:0.##} hrs · {receipt.TotalAmount:C}",
            link:    Url.Action(nameof(ReceiptDetail), new { id = receipt.Id }),
            icon:    "bi-file-earmark-check");

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
    public async Task<IActionResult> ConfirmPayment(int paymentId)
    {
        // Per-payment confirmation — used from the Payments Received table
        // on ReceiptDetail so a contractor can confirm each partial payment
        // as it clears their bank instead of waiting until the receipt is
        // fully paid.
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null) return RedirectToAction(nameof(Payroll));

        var payment = await _context.PayrollReceiptPayments
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId
                                   && p.Receipt != null
                                   && p.Receipt.ContractorId == contractor.Id);
        if (payment == null) return NotFound();
        if (payment.ContractorConfirmedDate.HasValue)
        {
            TempData["Warning"] = "This payment was already confirmed.";
            return RedirectToAction(nameof(ReceiptDetail), new { id = payment.PayrollReceiptId });
        }

        payment.ContractorConfirmedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _activity.LogContractorAsync(payment.PayrollReceiptId, contractor,
            $"Confirmed receipt of {payment.Amount:C2} ({payment.PaymentMethod}" +
            (string.IsNullOrEmpty(payment.CheckNumber) ? "" : $", check #{payment.CheckNumber}") + ").");

        await NotifyAdminsOnBellAsync(
            type:    "PayrollPaymentConfirmed",
            title:   $"{contractor.FirstName} confirmed {payment.Amount:C2} on #{payment.PayrollReceiptId}",
            message: $"{payment.PaymentMethod}" +
                     (string.IsNullOrEmpty(payment.Reference)
                         ? (string.IsNullOrEmpty(payment.CheckNumber) ? "" : $" · check #{payment.CheckNumber}")
                         : $" · ref {payment.Reference}"),
            link:    Url.Action(nameof(ReceiptDetail), new { id = payment.PayrollReceiptId }),
            icon:    "bi-check2-all");

        TempData["Success"] = $"Confirmed payment of {payment.Amount:C2}.";
        return RedirectToAction(nameof(ReceiptDetail), new { id = payment.PayrollReceiptId });
    }

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

        await NotifyAdminsOnBellAsync(
            type:    "PayrollConfirmed",
            title:   $"{contractor.FirstName} confirmed payment on #{receipt.Id}",
            message: receipt.PaymentConfirmedNote ?? $"{receipt.TotalAmount:C} received",
            link:    Url.Action(nameof(ReceiptDetail), new { id = receipt.Id }),
            icon:    "bi-check2-all");

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

        var linkUrl = Url.Action(nameof(ReceiptDetail), "Contractor", new { id }) ?? "#";

        // Bell admins on every contractor comment — the receipt
        // belongs in their queue so they need to see the new note.
        await NotifyAdminsOnBellAsync(
            type:    "PayrollComment",
            title:   $"New comment on receipt #{receipt.Id}",
            message: body.Length > 140 ? body[..140] + "…" : body,
            link:    linkUrl,
            icon:    "bi-chat-square-text");

        // @mention scan — anyone @-tagged gets a separate "you were
        // mentioned" ping.
        await _mentions.ProcessMentionsAsync(
            body:              body,
            authorDisplayName: $"{contractor.FirstName} {contractor.LastName}",
            sourceLabel:       $"Receipt #{receipt.Id}",
            linkUrl:           linkUrl,
            notificationType:  "ReceiptMention");

        TempData["Success"] = "Comment posted.";
        return RedirectToAction(nameof(ReceiptDetail), new { id });
    }

    /// <summary>
    /// Broadcasts an in-app bell notification to every active Admin.
    /// Mirrors the safety pattern used by EmailNotificationService — any
    /// exception is logged and swallowed so a flaky NotifyAsync call
    /// can't break the surrounding flow (submit, approve, etc.).
    /// </summary>
    private async Task NotifyAdminsOnBellAsync(string type, string title, string? message, string? link, string? icon)
    {
        try
        {
            var adminIds = await _context.PortalUsers
                .Include(u => u.Role)
                .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin")
                .Select(u => u.Id)
                .ToListAsync();
            if (adminIds.Count == 0) return;
            await _bell.NotifyManyAsync(adminIds, type, title, message, link, icon);
        }
        catch (Exception) { /* never block the flow */ }
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
        // Mirrors ReceiptDetail's audience rules: admins/IT Agents can pull
        // any receipt; contractors can only pull their own.
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("IT Agent");
        var contractor = await GetContractorEmployeeAsync();
        if (!isAdmin && contractor == null)
            return RedirectToAction(nameof(Payroll));

        var query = _context.PayrollReceipts
            .Include(r => r.Contractor)
            .Include(r => r.TimeEntries)
                .ThenInclude(e => e.Ticket)
            .AsQueryable();
        if (!isAdmin)
            query = query.Where(r => r.ContractorId == contractor!.Id);

        var receipt = await query.FirstOrDefaultAsync(r => r.Id == id);
        if (receipt == null) return NotFound();

        var bytes = await _attachments.RenderXlsxAsync(receipt);
        var fileName = $"PayrollReceipt_{receipt.Id}_{receipt.PeriodStart:yyyyMMdd}-{receipt.PeriodEnd:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // ── PDF Export ────────────────────────────────────────────────────────────

    // GET /Contractor/ReceiptPdf/{id}
    //
    // Renders the receipt as a PDF via PayrollReceiptPdfService. Same audience
    // rules as ReceiptDetail/ReceiptExcel: admins/IT Agents can download any
    // receipt, contractors only their own.
    public async Task<IActionResult> ReceiptPdf(int id)
    {
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("IT Agent");
        var contractor = await GetContractorEmployeeAsync();
        if (!isAdmin && contractor == null)
            return RedirectToAction(nameof(Payroll));

        if (!isAdmin)
        {
            var owns = await _context.PayrollReceipts
                .AnyAsync(r => r.Id == id && r.ContractorId == contractor!.Id);
            if (!owns) return NotFound();
        }

        var receipt = await _context.PayrollReceipts
            .AsNoTracking()
            .Select(r => new { r.Id, r.PeriodStart, r.PeriodEnd })
            .FirstOrDefaultAsync(r => r.Id == id);
        if (receipt == null) return NotFound();

        var bytes = await _pdfService.RenderAsync(id);
        var fileName = $"PayrollReceipt_{receipt.Id}_{receipt.PeriodStart:yyyyMMdd}-{receipt.PeriodEnd:yyyyMMdd}.pdf";
        return File(bytes, "application/pdf", fileName);
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


    // ── Analytics Dashboard ───────────────────────────────────────────────────

    // GET /Contractor/Analytics
    //
    // Aggregates the contractor's own receipts into chart-friendly buckets
    // (last 12 months) so the view can render ApexCharts widgets without any
    // client-side computation. Draft receipts are excluded — they're not
    // earnings, just work-in-progress.
    public async Task<IActionResult> Analytics()
    {
        var contractor = await GetContractorEmployeeAsync();
        if (contractor == null)
        {
            TempData["Error"] = "Your employee profile is not flagged as a contractor.";
            return RedirectToAction("Index", "Home");
        }

        var now      = DateTime.UtcNow;
        var monthEnd = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1);
        var rangeStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11);

        var receipts = await _context.PayrollReceipts
            .Where(r => r.ContractorId == contractor.Id
                     && r.Status != "Draft"
                     && r.PeriodStart >= rangeStart)
            .AsNoTracking()
            .ToListAsync();

        // Build a Month -> {earnings, hours} dictionary covering all 12 months
        // so the chart shows a continuous axis even when a month is empty.
        var monthly = new List<MonthlyBucket>();
        for (int i = 0; i < 12; i++)
        {
            var bucketStart = rangeStart.AddMonths(i);
            var bucketEnd   = bucketStart.AddMonths(1).AddDays(-1);
            var bucket = new MonthlyBucket
            {
                Label    = bucketStart.ToString("MMM yyyy"),
                YearMonth = bucketStart.ToString("yyyy-MM"),
            };
            foreach (var r in receipts.Where(r => r.PeriodStart <= bucketEnd && r.PeriodEnd >= bucketStart))
            {
                // Distribute receipts to the month their period starts in. This
                // is good enough for a trend chart — splitting a multi-month
                // receipt across months would require per-entry analysis.
                if (r.PeriodStart >= bucketStart && r.PeriodStart <= bucketEnd)
                {
                    if (r.Status == "Paid")
                    {
                        bucket.PaidAmount += r.TotalAmount;
                        bucket.PaidHours  += r.TotalHours;
                    }
                    else
                    {
                        bucket.PendingAmount += r.TotalAmount;
                        bucket.PendingHours  += r.TotalHours;
                    }
                }
            }
            monthly.Add(bucket);
        }

        // Status distribution across the same 12-month window.
        var statusBuckets = new Dictionary<string, decimal>
        {
            ["Submitted"] = 0m,
            ["Approved"]  = 0m,
            ["Paid"]      = 0m,
        };
        foreach (var r in receipts)
            if (statusBuckets.ContainsKey(r.Status))
                statusBuckets[r.Status] += r.TotalAmount;

        // Standard vs Emergency vs Retainer split — pulled from snapshot fields
        // on the receipt. Avoids re-running the calculator for every receipt.
        decimal totalStdHours       = receipts.Sum(r => r.TotalStandardHours);
        decimal totalEmergencyHours = receipts.Sum(r => r.TotalEmergencyHours);
        decimal totalRetainerAmt    = receipts.Sum(r => r.TotalRetainerAmountApplied);

        var stdRate  = contractor.HourlyRate ?? 0m;
        var emerRate = contractor.EmergencyHourlyRate ?? 0m;
        decimal totalStdAmount  = totalStdHours * stdRate;
        decimal totalEmerAmount = totalEmergencyHours * emerRate;

        // Retainer utilization — only meaningful if the contractor has one.
        // Hours included × number of paid/approved retainer months in window
        // would be ideal, but we approximate using RetainerHoursApplied snapshot.
        decimal retainerHoursIncluded = (contractor.MonthlyRetainerHoursIncluded ?? 0m);
        decimal retainerHoursApplied  = receipts.Sum(r => r.TotalRetainerHoursApplied);

        // Year-to-date totals for the header summary tiles.
        var ytdStart    = new DateTime(now.Year, 1, 1);
        var ytdReceipts = receipts.Where(r => r.PeriodEnd >= ytdStart).ToList();
        decimal ytdPaid    = ytdReceipts.Where(r => r.Status == "Paid").Sum(r => r.TotalAmount);
        decimal ytdPending = ytdReceipts.Where(r => r.Status != "Paid").Sum(r => r.TotalAmount);

        // 12-month average earnings (per active month — months with any receipt).
        var activeMonths = monthly.Count(m => m.PaidAmount + m.PendingAmount > 0);
        decimal totalLast12 = monthly.Sum(m => m.PaidAmount + m.PendingAmount);
        decimal avgPerActiveMonth = activeMonths > 0 ? totalLast12 / activeMonths : 0m;

        ViewBag.Contractor             = contractor;
        ViewBag.Monthly                = monthly;
        ViewBag.StatusBuckets          = statusBuckets;
        ViewBag.TotalStdHours          = totalStdHours;
        ViewBag.TotalEmergencyHours    = totalEmergencyHours;
        ViewBag.TotalStdAmount         = totalStdAmount;
        ViewBag.TotalEmerAmount        = totalEmerAmount;
        ViewBag.TotalRetainerAmount    = totalRetainerAmt;
        ViewBag.RetainerHoursIncluded  = retainerHoursIncluded;
        ViewBag.RetainerHoursApplied   = retainerHoursApplied;
        ViewBag.YtdPaid                = ytdPaid;
        ViewBag.YtdPending             = ytdPending;
        ViewBag.TotalLast12            = totalLast12;
        ViewBag.AvgPerActiveMonth      = avgPerActiveMonth;
        ViewBag.ActiveMonths           = activeMonths;
        ViewBag.ReceiptCount           = receipts.Count;
        ViewData["Title"]              = "Payroll Analytics";
        return View();
    }

    public class MonthlyBucket
    {
        public string Label { get; set; } = "";
        public string YearMonth { get; set; } = "";
        public decimal PaidAmount    { get; set; }
        public decimal PendingAmount { get; set; }
        public decimal PaidHours     { get; set; }
        public decimal PendingHours  { get; set; }
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

    /// <summary>
    /// Loads the contractor's active recurring-charge templates that overlap
    /// the receipt period and builds a parallel list of (not-yet-saved)
    /// PayrollReceiptCharge snapshots with auto-computed occurrence counts.
    /// Used by the New Receipt GET to pre-render the charges table + summary.
    /// </summary>
    private async Task<(List<RecurringChargeTemplate> templates, List<PayrollReceiptCharge> snapshots)>
        BuildChargeCandidatesAsync(Employee contractor, DateTime start, DateTime end)
    {
        var all = await _context.RecurringChargeTemplates
            .Where(t => t.ContractorId == contractor.Id && t.IsActive)
            .OrderBy(t => t.Label)
            .ToListAsync();

        // Date-window overlap done client-side (small list, comparisons are
        // simpler than translating optional bounds to SQL).
        var candidates = all.Where(t => RecurringChargeCalculator.CountOccurrences(t, start, end) > 0).ToList();

        var snapshots = candidates
            .Select(t => BuildChargeSnapshot(t, contractor, RecurringChargeCalculator.CountOccurrences(t, start, end)))
            .ToList();

        return (candidates, snapshots);
    }

    /// <summary>
    /// Builds PayrollReceiptCharge snapshots from the form's selected template
    /// ids + parallel occurrence-override array. Silently drops ids that
    /// don't belong to this contractor, are inactive, or fall outside their
    /// own date window. Override counts are clamped to [0, 2 × auto] to
    /// stop a typo / tampering from producing a runaway number.
    /// </summary>
    private async Task<List<PayrollReceiptCharge>> BuildSelectedChargeSnapshotsAsync(
        Employee contractor, DateTime periodStart, DateTime periodEnd,
        int[]? selectedTemplateIds, int[]? occurrenceOverrides)
    {
        if (selectedTemplateIds == null || selectedTemplateIds.Length == 0)
            return new List<PayrollReceiptCharge>();

        var idSet = selectedTemplateIds.ToHashSet();
        var templates = await _context.RecurringChargeTemplates
            .Where(t => t.ContractorId == contractor.Id && t.IsActive && idSet.Contains(t.Id))
            .ToListAsync();

        // Pair selectedTemplateIds[i] with occurrenceOverrides[i] by position
        // so the override goes with the right template even if templates come
        // back from the DB in a different order.
        var overrideByTemplateId = new Dictionary<int, int>();
        for (int i = 0; i < selectedTemplateIds.Length; i++)
        {
            if (occurrenceOverrides != null && i < occurrenceOverrides.Length)
                overrideByTemplateId[selectedTemplateIds[i]] = occurrenceOverrides[i];
        }

        var result = new List<PayrollReceiptCharge>();
        foreach (var t in templates)
        {
            var auto = RecurringChargeCalculator.CountOccurrences(t, periodStart, periodEnd);
            if (auto <= 0) continue; // out of window

            var count = auto;
            if (overrideByTemplateId.TryGetValue(t.Id, out var ovr))
            {
                if (ovr < 0) ovr = 0;
                var ceiling = auto * 2;
                if (ovr > ceiling) ovr = ceiling;
                count = ovr;
            }

            if (count == 0) continue; // explicitly excluded
            result.Add(BuildChargeSnapshot(t, contractor, count));
        }

        return result;
    }

    /// <summary>
    /// Builds PayrollReceiptCharge snapshots for ad-hoc (one-off) charges the
    /// contractor types directly on the New Receipt page — no template
    /// required. TemplateId stays null so the row lives only on this receipt
    /// and never carries forward.
    ///
    /// Each row is validated independently; rows with a blank label, an
    /// invalid pricing mode, or a non-positive amount are silently dropped.
    /// For Hourly rows where the contractor has no hourly rate configured,
    /// UnitAmountSnapshot is 0 — the row still saves but contributes $0.
    /// </summary>
    private static List<PayrollReceiptCharge> BuildAdhocChargeSnapshots(
        Employee contractor, string[]? labels, string[]? pricingModes, decimal[]? amounts)
    {
        var result = new List<PayrollReceiptCharge>();
        if (labels == null || labels.Length == 0) return result;

        var stdRate = contractor.HourlyRate ?? 0m;
        var now = DateTime.UtcNow;

        for (int i = 0; i < labels.Length; i++)
        {
            var rawLabel = labels[i];
            if (string.IsNullOrWhiteSpace(rawLabel)) continue;

            var rawMode = (pricingModes != null && i < pricingModes.Length)
                ? pricingModes[i] : "Flat";
            if (!Enum.TryParse<Core.Enums.RecurringChargePricingMode>(rawMode, true, out var mode))
                continue;

            var rawAmount = (amounts != null && i < amounts.Length) ? amounts[i] : 0m;
            if (rawAmount <= 0m) continue;

            // Defensive cap — keep one ad-hoc row from accidentally being entered
            // as 99,999 hours / dollars. Same ceiling as occurrence overrides.
            if (rawAmount > 10000m) rawAmount = 10000m;

            // Both pricing modes are normalised to a single-occurrence Flat-style
            // snapshot so the receipt UI renders one clean line per ad-hoc row
            // (`1 × $X.XX = $X.XX`) regardless of fractional hours. For Hourly
            // rows the hours-and-rate breakdown is captured in the label.
            decimal totalAmount;
            string labelSnapshot;
            var trimmedLabel = rawLabel.Trim();
            if (trimmedLabel.Length > 80) trimmedLabel = trimmedLabel[..80];

            if (mode == Core.Enums.RecurringChargePricingMode.Hourly)
            {
                // Skip Hourly rows when the contractor has no hourly rate
                // configured — saving a $0 line would be misleading. The user
                // can switch the row to Flat and enter the dollar amount directly.
                if (stdRate <= 0m) continue;
                totalAmount = Math.Round(rawAmount * stdRate, 2, MidpointRounding.AwayFromZero);
                labelSnapshot = $"{trimmedLabel} ({rawAmount.ToString("0.##")}h × {stdRate.ToString("C2")}/hr)";
            }
            else
            {
                totalAmount = Math.Round(rawAmount, 2, MidpointRounding.AwayFromZero);
                labelSnapshot = trimmedLabel;
            }
            if (labelSnapshot.Length > 120) labelSnapshot = labelSnapshot[..120];

            result.Add(new PayrollReceiptCharge
            {
                TemplateId          = null, // ad-hoc — no template
                LabelSnapshot       = labelSnapshot,
                CadenceSnapshot     = Core.Enums.RecurringChargeCadence.FlatPerReceipt,
                PricingModeSnapshot = Core.Enums.RecurringChargePricingMode.Flat,
                UnitAmountSnapshot  = totalAmount,
                OccurrenceCount     = 1,
                TotalAmount         = totalAmount,
                CreatedDate         = now,
            });
        }

        return result;
    }

    private static PayrollReceiptCharge BuildChargeSnapshot(
        RecurringChargeTemplate template, Employee contractor, int occurrenceCount)
    {
        var unit = RecurringChargeCalculator.UnitDollars(template, contractor);
        return new PayrollReceiptCharge
        {
            TemplateId          = template.Id,
            LabelSnapshot       = template.Label,
            CadenceSnapshot     = template.Cadence,
            PricingModeSnapshot = template.PricingMode,
            UnitAmountSnapshot  = unit,
            OccurrenceCount     = occurrenceCount,
            TotalAmount         = Math.Round(unit * occurrenceCount, 2, MidpointRounding.AwayFromZero),
            CreatedDate         = DateTime.UtcNow,
        };
    }
}
