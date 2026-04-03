using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class KbArticle
{
    public int Id { get; set; }

    [Required]
    [StringLength(300)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The problem/symptom description (pulled from ticket description or written manually).
    /// </summary>
    [Required]
    [StringLength(4000)]
    public string Problem { get; set; } = string.Empty;

    /// <summary>
    /// The resolution / how-to steps.
    /// </summary>
    [Required]
    [StringLength(8000)]
    public string Solution { get; set; } = string.Empty;

    public TicketCategory Category { get; set; }

    /// <summary>
    /// Optional link back to the ticket this was promoted from.
    /// </summary>
    public int? SourceTicketId { get; set; }
    public Ticket? SourceTicket { get; set; }

    public bool IsPublished { get; set; } = true;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdated { get; set; }

    public int ViewCount { get; set; }
}
