using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Stores an AI triage recommendation for a ticket.
/// Status lifecycle: Pending → Approved | Dismissed
/// </summary>
public class AiRecommendation
{
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }

    // ── Predicted classifications ────────────────────────────────────────────
    /// <summary>Predicted ticket category (TicketCategory enum int value). Null = not predicted.</summary>
    public int? SuggestedCategory { get; set; }

    /// <summary>Predicted ticket priority (TicketPriority enum int value). Null = not predicted.</summary>
    public int? SuggestedPriority { get; set; }

    /// <summary>Suggested assignee employee ID (based on assignment rules + category). Null = no suggestion.</summary>
    public int? SuggestedAssigneeId { get; set; }

    /// <summary>Most common sub-category ID for the predicted category from historical data. Null = no suggestion.</summary>
    public int? SuggestedSubCategoryId { get; set; }

    // ── Confidence scores (0.0–1.0) ─────────────────────────────────────────
    public float CategoryConfidence { get; set; }
    public float PriorityConfidence { get; set; }

    // ── AI-generated text (Ollama / LLM) ─────────────────────────────────────
    /// <summary>Short AI-generated summary of the ticket issue.</summary>
    [StringLength(2000)]
    public string? AiSummary { get; set; }

    /// <summary>AI-generated draft reply to the ticket submitter.</summary>
    public string? AiDraftReply { get; set; }

    // ── Workflow status ───────────────────────────────────────────────────────
    /// <summary>Pending, Approved, or Dismissed.</summary>
    [Required]
    [StringLength(20)]
    public string Status { get; set; } = "Pending";

    // ── Audit ─────────────────────────────────────────────────────────────────
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedDate { get; set; }

    [StringLength(200)]
    public string? ReviewedBy { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────
    public Ticket? Ticket { get; set; }
    public Employee? SuggestedAssignee { get; set; }
    public TicketSubCategory? SuggestedSubCategory { get; set; }
}
