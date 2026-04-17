using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class CsatSurvey
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    [Required]
    public string Token { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>1–5 rating. Null until the requester submits the survey.</summary>
    public int? Score { get; set; }

    [StringLength(1000)]
    public string? Feedback { get; set; }

    public DateTime SentDate { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedDate { get; set; }
}
