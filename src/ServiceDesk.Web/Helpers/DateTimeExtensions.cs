namespace ServiceDesk.Web.Helpers;

public static class DateTimeExtensions
{
    /// <summary>
    /// Converts a UTC DateTime to the organisation's configured local time.
    /// Falls back to UTC if the timezone ID is missing or invalid.
    /// Tolerates "America/Los Angeles" (space) as well as "America/Los_Angeles" (underscore).
    /// </summary>
    public static DateTime ToOrgTime(this DateTime utcDate, string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId) || tzId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return utcDate;
        try
        {
            var normalized = tzId.Replace(' ', '_');
            var tz = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcDate, DateTimeKind.Utc), tz);
        }
        catch
        {
            return utcDate;
        }
    }

    public static string ToOrgDisplay(this DateTime utcDate, string? tzId,
        string format = "MMM d, yyyy h:mm tt")
        => utcDate.ToOrgTime(tzId).ToString(format);

    public static string ToOrgDisplay(this DateTime? utcDate, string? tzId,
        string format = "MMM d, yyyy", string fallback = "—")
        => utcDate.HasValue ? utcDate.Value.ToOrgDisplay(tzId, format) : fallback;
}
