using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class EmailTemplate
{
    public int Id { get; set; }

    /// <summary>
    /// Unique key identifying the template type (e.g. "TicketCreated", "PasswordReset").
    /// </summary>
    [Required][StringLength(50)]
    public string Key { get; set; } = string.Empty;

    [Required][StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Email subject line. Supports tokens like {{TicketId}}, {{TicketTitle}}.
    /// When null, the system default subject is used.
    /// </summary>
    [StringLength(300)]
    public string? SubjectTemplate { get; set; }

    /// <summary>
    /// Inner HTML body (without the outer branded wrapper).
    /// Supports tokens like {{TicketId}}, {{RecipientName}}, {{CompanyName}}.
    /// When null, the system default body is used.
    /// </summary>
    public string? BodyTemplate { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;
}
