using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class WorkflowRule
{
    public int Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public WorkflowTrigger Trigger { get; set; } = WorkflowTrigger.NewTicket;

    // JSON-serialized List<WorkflowCondition>; ALL conditions must match (AND logic)
    public string ConditionsJson { get; set; } = "[]";

    // JSON-serialized List<WorkflowAction>; executed in order when all conditions match
    public string ActionsJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;

    // Lower value = evaluated first; first matching rule with StopOnMatch=true halts chain
    public int SortOrder { get; set; } = 100;

    public bool StopOnMatch { get; set; } = false;

    public int RunCount { get; set; }

    public DateTime? LastRunAt { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}

public enum WorkflowTrigger
{
    NewTicket    = 0,
    TicketUpdated = 1,
}

// These types are stored serialized inside ConditionsJson / ActionsJson — not DB tables.

public class WorkflowCondition
{
    // Supported fields: category, priority, keyword_title, keyword_description,
    //                   assignee_empty, branch
    public string Field    { get; set; } = string.Empty;
    // Supported operators: equals, not_equals, contains, is_empty
    public string Operator { get; set; } = "equals";
    public string Value    { get; set; } = string.Empty;
}

public class WorkflowAction
{
    // Supported types:
    //   assign_agent  — Value = employeeId (int as string)
    //   set_priority  — Value = "Low"|"Medium"|"High"|"Critical"
    //   add_note      — Value = note body text
    //   ai_route      — uses Ollama to re-evaluate category & priority (Value ignored)
    public string Type  { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    // Human-readable label shown in the rule builder (e.g. agent display name)
    public string? Label { get; set; }
}
