using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// A configured recipient of the "payroll receipt submitted" email.
/// Can either point at an existing portal user (then we use their
/// up-to-date name / email) or carry a free-form external address —
/// AP, payroll vendors, or a bookkeeper who doesn't log in to the
/// portal. When the table is empty the notification falls back to
/// "every active admin" so a fresh install still receives alerts.
/// </summary>
public class PayrollNotificationRecipient
{
    public int Id { get; set; }

    /// <summary>Optional FK — when set, the portal user's email/name win.</summary>
    public int? PortalUserId { get; set; }
    public PortalUser? PortalUser { get; set; }

    /// <summary>Free-form display name (used when no portal user is linked).</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Free-form email (used when no portal user is linked).</summary>
    [Required, EmailAddress, MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>Who added this recipient (for audit).</summary>
    public int? AddedByPortalUserId { get; set; }
}
