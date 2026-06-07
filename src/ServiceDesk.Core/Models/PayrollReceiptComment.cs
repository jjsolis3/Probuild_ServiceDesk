using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// One entry in a receipt's activity / discussion thread. Used for both
/// human comments (admin ↔ contractor back-and-forth) and system-generated
/// audit entries ("Approved by Jane on May 30", "Returned by Jane —
/// please clarify hours on May 23", "Resubmitted by Joe", "Payment
/// confirmed received by Joe"). Both kinds share a table so the timeline
/// renders chronologically as a single thread.
/// </summary>
public class PayrollReceiptComment
{
    public int Id { get; set; }

    [Required]
    public int PayrollReceiptId { get; set; }
    public PayrollReceipt? Receipt { get; set; }

    /// <summary>
    /// Author's portal user id when the author is an admin/HR/etc.
    /// Null for contractor authors and for system entries.
    /// </summary>
    public int? AuthorPortalUserId { get; set; }
    public PortalUser? AuthorPortalUser { get; set; }

    /// <summary>
    /// Author's employee id when the author is a contractor.
    /// Null for admin authors and for system entries.
    /// </summary>
    public int? AuthorEmployeeId { get; set; }
    public Employee? AuthorEmployee { get; set; }

    /// <summary>Denormalized display name so deleted users still show up.</summary>
    [Required, MaxLength(200)]
    public string AuthorName { get; set; } = "";

    /// <summary>"Admin" | "Contractor" | "System" — drives row styling.</summary>
    [Required, MaxLength(20)]
    public string AuthorRole { get; set; } = "System";

    [Required, MaxLength(2000)]
    public string Body { get; set; } = "";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
