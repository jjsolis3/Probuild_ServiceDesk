using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Services;

/// <summary>
/// Defines how many hours each priority level gets before a ticket is considered overdue.
/// Critical = 4h, High = 8h, Medium = 24h, Low = 72h
/// </summary>
public static class SlaPolicy
{
    private static readonly Dictionary<TicketPriority, int> HoursByPriority = new()
    {
        [TicketPriority.Critical] = 4,
        [TicketPriority.High]     = 8,
        [TicketPriority.Medium]   = 24,
        [TicketPriority.Low]      = 72,
    };

    public static int GetHours(TicketPriority priority) =>
        HoursByPriority.TryGetValue(priority, out var h) ? h : 24;

    public static DateTime CalculateDueDate(TicketPriority priority, DateTime? from = null) =>
        (from ?? DateTime.UtcNow).AddHours(GetHours(priority));
}
