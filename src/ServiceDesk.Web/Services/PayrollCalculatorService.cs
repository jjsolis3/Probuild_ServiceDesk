using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Computes per-receipt payroll totals when the contractor has any combination
/// of a second (Emergency) rate and a monthly burn-down retainer.
///
/// Each time entry "belongs" to the calendar month of its <c>WorkDate</c>,
/// not the receipt's period. A single receipt that spans two months has its
/// entries grouped per-month and each month's retainer logic is applied
/// independently. That correctly handles weekly / bi-weekly cadences AND
/// receipts that straddle month boundaries (e.g. Mar 28 → Apr 3).
/// </summary>
public sealed class PayrollCalculatorService
{
    private readonly ServiceDeskDbContext _context;

    public PayrollCalculatorService(ServiceDeskDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Snapshot of how a single receipt's hours and amounts shake out after
    /// applying Standard / Emergency rates and the monthly retainer burn-down.
    /// Populated by <see cref="CalculateAsync"/> and consumed by the receipt
    /// controller (to persist) and the receipt view (to render).
    /// </summary>
    public sealed class PayrollCalculation
    {
        // Aggregate totals across all month buckets
        public decimal TotalHours              { get; set; }
        public decimal TotalBillableHours      { get; set; }
        public decimal TotalStandardHours      { get; set; }
        public decimal TotalEmergencyHours     { get; set; }
        public decimal TotalRetainerHoursApplied  { get; set; }
        public decimal StandardBillableHours   { get; set; } // standard - retainer-absorbed
        public decimal StandardBillableAmount  { get; set; }
        public decimal EmergencyBillableAmount { get; set; }
        public decimal TotalRetainerAmountApplied { get; set; }
        public decimal TotalAmount             { get; set; }

        // Per-month breakdown for the receipt view's footer
        public List<MonthBucket> Months { get; set; } = new();
    }

    public sealed class MonthBucket
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string Label => new DateTime(Year, Month, 1).ToString("MMMM yyyy");

        public decimal StandardHours          { get; set; }
        public decimal EmergencyHours         { get; set; }
        public decimal RetainerHoursAvailable { get; set; }   // remaining at start of this receipt
        public decimal RetainerHoursApplied   { get; set; }   // absorbed by this receipt's entries
        public decimal StandardBillableHours  { get; set; }   // standard − retainer-absorbed
        public decimal StandardBillableAmount { get; set; }
        public decimal EmergencyAmount        { get; set; }
        public decimal RetainerAmountThisReceipt { get; set; }   // full retainer if 1st of month, else 0
        public bool IsFirstReceiptOfMonth     { get; set; }
    }

    /// <summary>
    /// Calculates the per-month breakdown for a set of time entries that will
    /// be claimed by a (not-yet-persisted) receipt. Does NOT mutate anything —
    /// the caller is responsible for copying the result onto the receipt and
    /// saving. Pass <paramref name="receiptIdToIgnore"/> when recalculating an
    /// existing receipt (so its own entries aren't double-counted).
    /// </summary>
    public async Task<PayrollCalculation> CalculateAsync(
        Employee contractor,
        IReadOnlyList<TicketTimeEntry> entries,
        int? receiptIdToIgnore = null,
        CancellationToken ct = default)
    {
        var standardRate  = contractor.HourlyRate ?? 0m;
        var emergencyRate = contractor.EmergencyHourlyRate ?? 0m;
        var retainerAmt   = contractor.MonthlyRetainerAmount ?? 0m;
        var retainerHrs   = contractor.MonthlyRetainerHoursIncluded ?? 0m;
        var hasRetainer   = retainerAmt > 0m && retainerHrs > 0m;

        var calc = new PayrollCalculation
        {
            TotalHours         = entries.Sum(e => e.Hours),
            TotalBillableHours = entries.Where(e => e.IsBillable).Sum(e => e.Hours)
        };

        // Group billable entries by (year, month) of WorkDate. Non-billable
        // entries still contribute to TotalHours but don't drive money.
        var monthGroups = entries
            .Where(e => e.IsBillable)
            .GroupBy(e => (e.WorkDate.Year, e.WorkDate.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

        foreach (var grp in monthGroups)
        {
            var (year, month) = grp.Key;
            var monthStart = new DateTime(year, month, 1);
            var monthEnd   = monthStart.AddMonths(1).AddTicks(-1);

            var stdHours  = grp.Where(e => e.RateType == PayRateType.Standard).Sum(e => e.Hours);
            var emerHours = grp.Where(e => e.RateType == PayRateType.Emergency).Sum(e => e.Hours);

            var bucket = new MonthBucket
            {
                Year           = year,
                Month          = month,
                StandardHours  = stdHours,
                EmergencyHours = emerHours,
                EmergencyAmount = emerHours * emergencyRate
            };

            if (hasRetainer)
            {
                // Pull standard hours already claimed by OTHER non-cancelled
                // receipts in this calendar month for this contractor. Time
                // entries are only ever assigned to a single receipt, so this
                // is just an existing-receipts scan.
                var alreadyClaimedQuery = _context.TicketTimeEntries
                    .Where(e => e.LoggedByEmployeeId == contractor.Id
                             && e.IsBillable
                             && e.RateType == PayRateType.Standard
                             && e.PayrollReceiptId != null
                             && e.WorkDate >= monthStart
                             && e.WorkDate <= monthEnd);
                if (receiptIdToIgnore.HasValue)
                    alreadyClaimedQuery = alreadyClaimedQuery.Where(e => e.PayrollReceiptId != receiptIdToIgnore.Value);

                var alreadyClaimedHrs = await alreadyClaimedQuery.SumAsync(e => (decimal?)e.Hours, ct) ?? 0m;
                var remaining         = Math.Max(0m, retainerHrs - alreadyClaimedHrs);
                var applied           = Math.Min(stdHours, remaining);

                bucket.RetainerHoursAvailable = remaining;
                bucket.RetainerHoursApplied   = applied;
                bucket.StandardBillableHours  = stdHours - applied;

                // The retainer dollar amount is paid out exactly once per
                // contractor per calendar month — on the FIRST non-cancelled
                // receipt to touch that month. Detect that by checking whether
                // any prior receipt's entries already touched this month.
                var firstReceiptCheck = _context.TicketTimeEntries
                    .Where(e => e.LoggedByEmployeeId == contractor.Id
                             && e.PayrollReceiptId != null
                             && e.WorkDate >= monthStart
                             && e.WorkDate <= monthEnd);
                if (receiptIdToIgnore.HasValue)
                    firstReceiptCheck = firstReceiptCheck.Where(e => e.PayrollReceiptId != receiptIdToIgnore.Value);

                bucket.IsFirstReceiptOfMonth = !await firstReceiptCheck.AnyAsync(ct);
                bucket.RetainerAmountThisReceipt = bucket.IsFirstReceiptOfMonth ? retainerAmt : 0m;
            }
            else
            {
                bucket.StandardBillableHours = stdHours;
            }

            bucket.StandardBillableAmount = bucket.StandardBillableHours * standardRate;

            calc.Months.Add(bucket);
        }

        // Roll up the aggregate fields the receipt persists.
        foreach (var b in calc.Months)
        {
            calc.TotalStandardHours          += b.StandardHours;
            calc.TotalEmergencyHours         += b.EmergencyHours;
            calc.TotalRetainerHoursApplied   += b.RetainerHoursApplied;
            calc.StandardBillableHours       += b.StandardBillableHours;
            calc.StandardBillableAmount      += b.StandardBillableAmount;
            calc.EmergencyBillableAmount     += b.EmergencyAmount;
            calc.TotalRetainerAmountApplied  += b.RetainerAmountThisReceipt;
        }

        calc.TotalAmount =
            calc.StandardBillableAmount
            + calc.EmergencyBillableAmount
            + calc.TotalRetainerAmountApplied;

        return calc;
    }
}
