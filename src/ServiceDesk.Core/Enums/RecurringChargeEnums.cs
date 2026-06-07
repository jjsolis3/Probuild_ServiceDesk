namespace ServiceDesk.Core.Enums;

/// <summary>
/// How often a recurring charge template bills against a payroll receipt.
/// The receipt's period is sliced into occurrences using this cadence;
/// the line total is then occurrences × unit amount.
/// </summary>
public enum RecurringChargeCadence : byte
{
    /// <summary>One occurrence per matching weekday in the period.
    /// The matching weekdays are controlled by
    /// <see cref="Models.RecurringChargeTemplate.WeekdayMask"/> — empty
    /// mask is treated as "all 7 days".</summary>
    PerDay         = 0,

    /// <summary>One occurrence per ISO week that intersects the period.</summary>
    PerWeek        = 1,

    /// <summary>One occurrence per calendar (year, month) bucket that intersects the period.</summary>
    PerMonth       = 2,

    /// <summary>Exactly one occurrence per receipt, regardless of period length.</summary>
    FlatPerReceipt = 3
}

/// <summary>
/// How the unit amount on a recurring charge template resolves to dollars.
/// </summary>
public enum RecurringChargePricingMode : byte
{
    /// <summary><see cref="Models.RecurringChargeTemplate.UnitAmount"/> is the dollar amount per occurrence.</summary>
    Flat   = 0,

    /// <summary><see cref="Models.RecurringChargeTemplate.UnitAmount"/> is hours per occurrence; dollars = hours × the contractor's standard HourlyRate at receipt-creation time.</summary>
    Hourly = 1
}
