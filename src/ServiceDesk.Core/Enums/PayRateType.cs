namespace ServiceDesk.Core.Enums;

/// <summary>
/// Per-time-entry pay rate selection. Defaults to <see cref="Standard"/>.
/// <list type="bullet">
///   <item><see cref="Standard"/> — billed at the contractor's regular HourlyRate.</item>
///   <item><see cref="Emergency"/> — billed at EmergencyHourlyRate (urgent / weekend / after-hours).
///     Emergency hours are NEVER absorbed by the monthly retainer's included hours
///     — the retainer covers Standard hours only.</item>
/// </list>
/// </summary>
public enum PayRateType : byte
{
    Standard  = 0,
    Emergency = 1
}
