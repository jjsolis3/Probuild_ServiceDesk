namespace ServiceDesk.Core.Models;

public class NotificationLog
{
    public int Id { get; set; }
    public int? TicketId { get; set; }
    public string NotificationType { get; set; } = string.Empty; // TicketCreated, TicketAssigned, TicketUpdated, NoteAdded, PasswordReset, PayrollSubmitted, etc.
    public string RecipientEmail { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string? Subject { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime SentDate { get; set; } = DateTime.UtcNow;

    public Ticket? Ticket { get; set; }
}
