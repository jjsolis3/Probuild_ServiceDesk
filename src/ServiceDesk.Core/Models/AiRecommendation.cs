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

    /// <summary>
    /// LLM-generated recommended IT resolution steps (numbered list) shown
    /// inside the AI Triage panel so the agent gets actionable guidance
    /// without clicking a separate "Suggest Solution" button. Populated
    /// asynchronously by AiTriageEnrichmentService after ML.NET triage.
    /// </summary>
    [StringLength(4000)]
    public string? AiSuggestedSolution { get; set; }

    /// <summary>
    /// True when the LLM's escalation-signal check on the ticket text says
    /// the customer is signalling urgency / frustration / deadline pressure.
    /// Surfaced as a red banner in the triage panel.
    /// </summary>
    public bool EscalationSignal { get; set; }

    /// <summary>Short human-readable reason returned by the escalation check (e.g. "mentions manager", "deadline today").</summary>
    [StringLength(200)]
    public string? EscalationReason { get; set; }

    /// <summary>Optional KB article the LLM matched to this ticket at triage time. Rendered as a suggested-read link in the panel.</summary>
    public int? RelatedKbArticleId { get; set; }

    /// <summary>Cadence marker: when the LLM enrichment step last completed. Null = enrichment not yet run.</summary>
    public DateTime? LlmEnrichedDate { get; set; }

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
    public KbArticle? RelatedKbArticle { get; set; }
}
