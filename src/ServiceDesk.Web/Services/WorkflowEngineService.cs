using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Evaluates active WorkflowRules against a ticket and executes matched actions.
/// Called fire-and-forget from TicketsController after a ticket is saved.
/// </summary>
public class WorkflowEngineService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OllamaService _ollama;
    private readonly ILogger<WorkflowEngineService> _logger;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public WorkflowEngineService(
        IServiceScopeFactory scopeFactory,
        OllamaService ollama,
        ILogger<WorkflowEngineService> logger)
    {
        _scopeFactory = scopeFactory;
        _ollama       = ollama;
        _logger       = logger;
    }

    public async Task EvaluateOnNewTicketAsync(int ticketId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

            var ticket = await db.Tickets
                .Include(t => t.SubmittedBy)
                .FirstOrDefaultAsync(t => t.Id == ticketId);

            if (ticket == null) return;

            var rules = await db.WorkflowRules
                .Where(r => r.IsActive && r.Trigger == WorkflowTrigger.NewTicket)
                .OrderBy(r => r.SortOrder)
                .ToListAsync();

            foreach (var rule in rules)
            {
                var conditions = Deserialize<WorkflowCondition>(rule.ConditionsJson);
                var actions    = Deserialize<WorkflowAction>(rule.ActionsJson);

                if (!ConditionsMatch(conditions, ticket)) continue;

                _logger.LogInformation("[Workflow] Rule '{Name}' matched ticket #{Id}", rule.Name, ticket.Id);

                await ExecuteActionsAsync(actions, ticket, db);

                rule.RunCount++;
                rule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                if (rule.StopOnMatch) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] Error evaluating rules for ticket #{TicketId}", ticketId);
        }
    }

    // ── Condition evaluation ─────────────────────────────────────────────────

    private static bool ConditionsMatch(List<WorkflowCondition> conditions, Ticket ticket)
    {
        foreach (var c in conditions)
        {
            if (!EvaluateCondition(c, ticket)) return false;
        }
        return true;
    }

    private static bool EvaluateCondition(WorkflowCondition c, Ticket ticket)
    {
        return c.Field switch
        {
            "category" => c.Operator switch
            {
                "equals"     => ticket.Category.ToString() == c.Value,
                "not_equals" => ticket.Category.ToString() != c.Value,
                _            => false
            },
            "priority" => c.Operator switch
            {
                "equals"     => ticket.Priority.ToString().Equals(c.Value, StringComparison.OrdinalIgnoreCase),
                "not_equals" => !ticket.Priority.ToString().Equals(c.Value, StringComparison.OrdinalIgnoreCase),
                _            => false
            },
            "keyword_title" => c.Operator switch
            {
                "contains"     => ticket.Title.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                "not_contains" => !ticket.Title.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                _              => false
            },
            "keyword_description" => c.Operator switch
            {
                "contains"     => ticket.Description.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                "not_contains" => !ticket.Description.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                _              => false
            },
            "assignee_empty" => c.Operator switch
            {
                "is_empty"  => ticket.AssignedToId == null,
                "not_empty" => ticket.AssignedToId != null,
                _           => false
            },
            "branch" => c.Operator switch
            {
                "equals"     => ticket.BranchId?.ToString() == c.Value,
                "not_equals" => ticket.BranchId?.ToString() != c.Value,
                _            => false
            },
            _ => false
        };
    }

    // ── Action execution ─────────────────────────────────────────────────────

    private async Task ExecuteActionsAsync(List<WorkflowAction> actions, Ticket ticket, ServiceDeskDbContext db)
    {
        foreach (var action in actions)
        {
            try
            {
                await ExecuteActionAsync(action, ticket, db);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Workflow] Action '{Type}' failed for ticket #{Id}", action.Type, ticket.Id);
            }
        }
    }

    private async Task ExecuteActionAsync(WorkflowAction action, Ticket ticket, ServiceDeskDbContext db)
    {
        switch (action.Type)
        {
            case "assign_agent":
                if (int.TryParse(action.Value, out int agentId))
                {
                    ticket.AssignedToId = agentId;
                    AddSystemNote(db, ticket.Id, $"Workflow auto-assigned ticket to agent ID {agentId}.");
                }
                break;

            case "set_priority":
                if (Enum.TryParse<TicketPriority>(action.Value, ignoreCase: true, out var priority))
                {
                    ticket.Priority = priority;
                    AddSystemNote(db, ticket.Id, $"Workflow set priority to {priority}.");
                }
                break;

            case "add_note":
                if (!string.IsNullOrWhiteSpace(action.Value))
                {
                    AddSystemNote(db, ticket.Id, action.Value);
                }
                break;

            case "ai_route":
                await AiRouteAsync(ticket, db);
                break;
        }
    }

    private static void AddSystemNote(ServiceDeskDbContext db, int ticketId, string content)
    {
        db.TicketNotes.Add(new TicketNote
        {
            TicketId    = ticketId,
            AuthorName  = "Automation Engine",
            AuthorEmail = null,
            Content     = $"⚙️ {content}",
            Source      = "System",
            IsInternal  = true,
            CreatedDate = DateTime.UtcNow,
        });
    }

    private async Task AiRouteAsync(Ticket ticket, ServiceDeskDbContext db)
    {
        var prompt =
            $"You are an IT service desk triage assistant. Analyze this ticket and respond " +
            $"with ONLY a JSON object with two fields: " +
            $"\"priority\" (one of: Low, Medium, High, Critical) and " +
            $"\"summary\" (one sentence describing the core issue).\n\n" +
            $"Ticket title: {ticket.Title}\n\nDescription:\n{ticket.Description}\n\n" +
            $"Respond with only valid JSON, no markdown.";

        var result = await _ollama.GenerateRawAsync(prompt);
        if (string.IsNullOrEmpty(result)) return;

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            if (root.TryGetProperty("priority", out var p) &&
                Enum.TryParse<TicketPriority>(p.GetString(), ignoreCase: true, out var aiPriority))
            {
                ticket.Priority = aiPriority;
            }

            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var noteText = $"AI routing analysis complete. Suggested priority: {ticket.Priority}."
                + (string.IsNullOrEmpty(summary) ? "" : $" Summary: {summary}");
            AddSystemNote(db, ticket.Id, noteText);
        }
        catch
        {
            _logger.LogWarning("[Workflow] AI route returned non-JSON for ticket #{Id}: {Result}", ticket.Id, result);
        }
    }

    private static List<T> Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json, _json) ?? []; }
        catch { return []; }
    }
}
