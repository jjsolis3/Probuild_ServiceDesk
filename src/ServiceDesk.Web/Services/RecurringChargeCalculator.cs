using System.Globalization;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Pure helper that converts a <see cref="RecurringChargeTemplate"/> + a
/// receipt period into an occurrence count and a dollar-per-occurrence
/// snapshot. No DB access — easy to unit test and safe to call from
/// anywhere (controllers, views, the payroll calculator).
/// </summary>
public static class RecurringChargeCalculator
{
    /// <summary>
    /// Number of times this template "fires" inside [periodStart..periodEnd].
    /// Range is clamped first by the template's optional Start/End dates so
    /// templates with a closed window only bill while they were live.
    /// </summary>
    public static int CountOccurrences(RecurringChargeTemplate t, DateTime periodStart, DateTime periodEnd)
    {
        var start = periodStart.Date;
        var end   = periodEnd.Date;

        if (t.StartDate.HasValue && t.StartDate.Value.Date > start) start = t.StartDate.Value.Date;
        if (t.EndDate.HasValue   && t.EndDate.Value.Date   < end)   end   = t.EndDate.Value.Date;

        if (end < start) return 0;

        return t.Cadence switch
        {
            RecurringChargeCadence.FlatPerReceipt => 1,
            RecurringChargeCadence.PerDay         => CountWeekdays(t.WeekdayMask, start, end),
            RecurringChargeCadence.PerWeek        => CountIsoWeeks(start, end),
            RecurringChargeCadence.PerMonth       => CountMonths(start, end),
            _ => 0
        };
    }

    /// <summary>
    /// Resolves the template's per-occurrence dollar amount given the
    /// contractor's current rates. For Flat templates UnitAmount IS dollars;
    /// for Hourly templates UnitAmount is hours × the contractor's standard
    /// HourlyRate (returns 0 if HourlyRate is unset — caller surfaces a
    /// warning so the receipt isn't silently zeroed).
    /// </summary>
    public static decimal UnitDollars(RecurringChargeTemplate t, Employee contractor)
    {
        return t.PricingMode switch
        {
            RecurringChargePricingMode.Flat   => Math.Round(t.UnitAmount, 2, MidpointRounding.AwayFromZero),
            RecurringChargePricingMode.Hourly => Math.Round(t.UnitAmount * (contractor.HourlyRate ?? 0m), 2, MidpointRounding.AwayFromZero),
            _ => 0m
        };
    }

    /// <summary>
    /// Human-friendly cadence label for the UI — e.g. "Per weekday (Mon–Fri)",
    /// "Per Mon/Wed/Fri", "Per week", "Per month", "Flat per receipt".
    /// </summary>
    public static string DescribeCadence(RecurringChargeTemplate t) => t.Cadence switch
    {
        RecurringChargeCadence.FlatPerReceipt => "Flat per receipt",
        RecurringChargeCadence.PerWeek        => "Per week",
        RecurringChargeCadence.PerMonth       => "Per month",
        RecurringChargeCadence.PerDay         => DescribeWeekdayMask(t.WeekdayMask),
        _ => t.Cadence.ToString()
    };

    private static string DescribeWeekdayMask(byte mask)
    {
        var effective = mask == 0 ? (byte)127 : mask;
        if (effective == 127) return "Per day (all 7 days)";
        if (effective == 62)  return "Per weekday (Mon–Fri)";
        if (effective == 65)  return "Per weekend (Sat/Sun)";

        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var parts = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if ((effective & (1 << i)) != 0) parts.Add(names[i]);
        }
        return "Per " + string.Join("/", parts);
    }

    private static int CountWeekdays(byte mask, DateTime start, DateTime end)
    {
        var effective = mask == 0 ? (byte)127 : mask;
        if (effective == 0) return 0;

        int count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if ((effective & (1 << (int)d.DayOfWeek)) != 0) count++;
        }
        return count;
    }

    private static int CountIsoWeeks(DateTime start, DateTime end)
    {
        // Distinct (ISO-year, ISO-week) tuples the range touches.
        var seen = new HashSet<(int year, int week)>();
        var cal  = CultureInfo.InvariantCulture.Calendar;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var week = cal.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            // Use Year-of-week — Dec 31 can fall in week 1 of the next ISO year.
            var year = d.Year;
            if (week >= 52 && d.Month == 1) year -= 1;
            else if (week == 1 && d.Month == 12) year += 1;
            seen.Add((year, week));
        }
        return seen.Count;
    }

    private static int CountMonths(DateTime start, DateTime end)
    {
        var months = (end.Year - start.Year) * 12 + (end.Month - start.Month) + 1;
        return months < 0 ? 0 : months;
    }
}
