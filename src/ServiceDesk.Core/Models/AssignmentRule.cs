using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Defines a rule that automatically assigns new tickets to a specific agent
/// based on the ticket's category and/or the submitter's branch/location.
/// Rules are evaluated in ascending SortOrder — the first match wins.
/// </summary>
public class AssignmentRule
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Rule Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When null, this rule matches any category.
    /// </summary>
    [Display(Name = "Category (any if blank)")]
    public TicketCategory? Category { get; set; }

    /// <summary>
    /// When null, this rule matches any branch/location.
    /// </summary>
    [Display(Name = "Branch / Location (any if blank)")]
    public int? BranchId { get; set; }

    /// <summary>
    /// The agent to assign the ticket to when this rule matches.
    /// </summary>
    [Required]
    [Display(Name = "Assign To")]
    public int AssigneeId { get; set; }

    /// <summary>
    /// Lower value = checked first. Allows fine-grained rules (category+branch)
    /// to take priority over broad fallback rules (category-only or branch-only).
    /// </summary>
    [Display(Name = "Priority (lower = checked first)")]
    public int SortOrder { get; set; } = 100;

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Branch? Branch { get; set; }
    public Employee? Assignee { get; set; }
}
