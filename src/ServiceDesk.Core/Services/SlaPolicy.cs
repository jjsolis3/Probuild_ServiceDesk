using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Services;

/// <summary>
/// Defines how many hours each priority level gets before a ticket is considered overdue.
/// Critical = 4h, High = 8h, Medium = 24h, Low = 72h
/// </summary>
public static class SlaPolicy
{
    private static Dictionary<TicketPriority, int> HoursByPriority = new()
    {
        [TicketPriority.Critical] = 4,
        [TicketPriority.High]     = 8,
        [TicketPriority.Medium]   = 24,
        [TicketPriority.Low]      = 72,
    };

    /// <summary>
    /// Overrides the default SLA hours from persistent settings.
    /// Called on startup from Program.cs and again whenever settings are saved.
    /// </summary>
    public static void Configure(int critical, int high, int medium, int low)
    {
        HoursByPriority[TicketPriority.Critical] = Math.Max(1, critical);
        HoursByPriority[TicketPriority.High]     = Math.Max(1, high);
        HoursByPriority[TicketPriority.Medium]   = Math.Max(1, medium);
        HoursByPriority[TicketPriority.Low]      = Math.Max(1, low);
    }

    public static int GetHours(TicketPriority priority) =>
        HoursByPriority.TryGetValue(priority, out var h) ? h : 24;

    public static DateTime CalculateDueDate(TicketPriority priority, DateTime? from = null) =>
        (from ?? DateTime.UtcNow).AddHours(GetHours(priority));
}
