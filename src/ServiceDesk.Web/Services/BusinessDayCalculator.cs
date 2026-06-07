using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Business-day arithmetic that honors weekends AND the admin-managed
/// <c>CompanyHolidays</c> table. Recurring holidays (July 4, New Year's,
/// etc.) are expanded across a +/-1 year window so the math works even
/// when the relevant span crosses a calendar boundary.
///
/// Used today by the stale-receipt reminder; useful anywhere the app
/// needs to ask "N business days from / until X."
/// </summary>
public class BusinessDayCalculator
{
    private readonly ServiceDeskDbContext _context;

    public BusinessDayCalculator(ServiceDeskDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Returns the date that is <paramref name="n"/> business days before
    /// <paramref name="fromDate"/>, skipping Saturdays, Sundays, and
    /// configured holidays. Walks day-by-day rather than computing a
    /// closed form so recurring holidays mid-span are handled cleanly.
    /// </summary>
    public async Task<DateTime> NBusinessDaysAgoAsync(int n, DateTime fromDate)
    {
        if (n <= 0) return fromDate.Date;
        var holidays = await LoadHolidaySetAsync(fromDate.Year - 1, fromDate.Year + 1);
        return NBusinessDaysAgo(n, fromDate, holidays);
    }

    /// <summary>
    /// Counts business days strictly between <paramref name="start"/>
    /// (exclusive) and <paramref name="end"/> (inclusive). i.e. how many
    /// business days have FULLY elapsed since <paramref name="start"/>.
    /// </summary>
    public async Task<int> BusinessDaysBetweenAsync(DateTime start, DateTime end)
    {
        if (end.Date <= start.Date) return 0;
        var holidays = await LoadHolidaySetAsync(
            Math.Min(start.Year, end.Year) - 1,
            Math.Max(start.Year, end.Year) + 1);
        var count = 0;
        for (var d = start.Date.AddDays(1); d <= end.Date; d = d.AddDays(1))
        {
            if (IsBusinessDay(d, holidays)) count++;
        }
        return count;
    }

    // ── Pure helpers (testable without DI) ────────────────────────────────

    public static DateTime NBusinessDaysAgo(int n, DateTime fromDate, ISet<DateTime> holidayDates)
    {
        var date = fromDate.Date;
        var counted = 0;
        // Safety net — if every day were marked a holiday we'd loop forever.
        // Two years is far more than any reminder window will ever need.
        var safety = 0;
        while (counted < n && safety++ < 800)
        {
            date = date.AddDays(-1);
            if (IsBusinessDay(date, holidayDates)) counted++;
        }
        return date;
    }

    public static bool IsBusinessDay(DateTime date, ISet<DateTime> holidayDates)
    {
        if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            return false;
        if (holidayDates.Contains(date.Date)) return false;
        return true;
    }

    /// <summary>
    /// Builds a HashSet of every holiday date that falls inside
    /// [minYear, maxYear]. Non-recurring holidays are included as-is;
    /// recurring holidays are projected onto every year in the range.
    /// </summary>
    private async Task<HashSet<DateTime>> LoadHolidaySetAsync(int minYear, int maxYear)
    {
        var holidays = await _context.CompanyHolidays
            .AsNoTracking()
            .ToListAsync();

        var set = new HashSet<DateTime>();
        foreach (var h in holidays)
        {
            if (h.IsRecurringYearly)
            {
                for (var y = minYear; y <= maxYear; y++)
                {
                    // Try/catch handles Feb 29 on non-leap years gracefully.
                    try { set.Add(new DateTime(y, h.Date.Month, h.Date.Day)); }
                    catch (ArgumentOutOfRangeException) { /* skip impossible date */ }
                }
            }
            else
            {
                set.Add(h.Date.Date);
            }
        }
        return set;
    }
}
