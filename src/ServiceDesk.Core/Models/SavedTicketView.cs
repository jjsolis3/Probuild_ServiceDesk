using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// A named, reusable filter preset that an IT staff member can save and reload on the Tickets page.
/// </summary>
public class SavedTicketView
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    // Owner — null means it's a shared/admin-managed view visible to everyone.
    public int? OwnerPortalUserId { get; set; }
    public PortalUser? Owner { get; set; }

    /// <summary>Auto-load this view when the Tickets page is opened (one per user).</summary>
    public bool IsDefault { get; set; }

    /// <summary>Visible to all portal users, not just the owner.</summary>
    public bool IsShared { get; set; }

    // ── Filter criteria (all nullable = "not filtered") ──────────────────────

    public TicketStatus?   FilterStatus   { get; set; }
    public TicketCategory? FilterCategory { get; set; }
    public TicketPriority? FilterPriority { get; set; }

    /// <summary>Filter to tickets whose branch matches this ID.</summary>
    public int?    FilterBranchId   { get; set; }
    public Branch? FilterBranch     { get; set; }

    /// <summary>Filter to tickets where the requester's department matches.</summary>
    [MaxLength(100)]
    public string? FilterDepartment { get; set; }

    /// <summary>Filter to tickets assigned to members of this UserGroup.</summary>
    public int?       FilterGroupId { get; set; }
    public UserGroup? FilterGroup   { get; set; }

    /// <summary>Show only tickets assigned to the currently logged-in user.</summary>
    public bool FilterAssignedToMe  { get; set; }

    /// <summary>Show only tickets with no assignee.</summary>
    public bool FilterUnassignedOnly { get; set; }

    /// <summary>Show only tickets imported without a matched requester.</summary>
    public bool FilterUnmatchedOnly  { get; set; }

    // ── Sort & display ───────────────────────────────────────────────────────

    [MaxLength(20)]
    public string SortBy   { get; set; } = "id";

    [MaxLength(4)]
    public string SortDir  { get; set; } = "desc";

    public int PageSize { get; set; } = 25;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
