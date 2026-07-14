using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Enriches an <see cref="AiRecommendation"/> row with LLM-derived fields
/// after the ML.NET triage step has already saved the initial category /
/// priority prediction. Runs on a background task (fired from
/// <see cref="AiTriageService.TriageAndSaveAsync"/>) so it never blocks
/// ticket creation.
///
/// Each enrichment step is best-effort — a failure in one (e.g. Ollama
/// returned an empty response for the sub-category classifier) will not
/// stop the others from persisting. Whatever succeeds is saved with
/// <c>LlmEnrichedDate</c> stamped so the panel can show a "populated" state.
/// </summary>
public class AiTriageEnrichmentService
{
    private readonly ServiceDeskDbContext _context;
    private readonly OllamaService _ollama;
    private readonly ILogger<AiTriageEnrichmentService> _logger;

    public AiTriageEnrichmentService(
        ServiceDeskDbContext context,
        OllamaService ollama,
        ILogger<AiTriageEnrichmentService> logger)
    {
        _context = context;
        _ollama  = ollama;
        _logger  = logger;
    }

    /// <summary>
    /// Runs the enrichment pipeline for a specific recommendation. Reads
    /// AppSettings on entry so the admin can flip enrichment off without a
    /// restart; skips silently when the feature is disabled or the row is
    /// missing / already enriched.
    /// </summary>
    public async Task EnrichForRecommendationAsync(int recommendationId, CancellationToken ct = default)
    {
        try
        {
            var settings = await _context.AppSettings
                .Where(s => s.Key == "AiLlmEnrichmentEnabled" || s.Key == "AiLlmEnrichmentDelaySeconds")
                .AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty, ct);

            var enrichmentEnabled = settings.TryGetValue("AiLlmEnrichmentEnabled", out var e)
                                    && string.Equals(e, "true", StringComparison.OrdinalIgnoreCase);
            if (!enrichmentEnabled)
            {
                _logger.LogDebug("[AiTriageEnrichment] Disabled by setting — skipping rec #{Id}.", recommendationId);
                return;
            }

            int delaySeconds = 2;
            if (settings.TryGetValue("AiLlmEnrichmentDelaySeconds", out var dStr)
                && int.TryParse(dStr, out var d))
                delaySeconds = Math.Clamp(d, 0, 30);

            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

            var rec = await _context.AiRecommendations
                .Include(r => r.Ticket)
                .FirstOrDefaultAsync(r => r.Id == recommendationId, ct);
            if (rec == null)
            {
                _logger.LogWarning("[AiTriageEnrichment] Recommendation #{Id} not found — did the ticket get deleted?", recommendationId);
                return;
            }
            if (rec.LlmEnrichedDate.HasValue)
            {
                _logger.LogDebug("[AiTriageEnrichment] Recommendation #{Id} already enriched — skipping.", recommendationId);
                return;
            }

            var title = rec.Ticket?.Title ?? string.Empty;
            var description = rec.Ticket?.Description ?? string.Empty;

            // Each step is independent: a failure just leaves that field null.
            await RefineSubCategoryAsync(rec, title, description, ct);
            await AddSuggestedSolutionAsync(rec, title, description, ct);
            await DetectEscalationSignalAsync(rec, description, ct);
            await MatchKbArticleAsync(rec, title, description, ct);

            rec.LlmEnrichedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[AiTriageEnrichment] Recommendation #{Id} enriched: subcat={SubCat}, solution={SolLen}, escalation={Esc}, kb={KbId}",
                rec.Id,
                rec.SuggestedSubCategoryId?.ToString() ?? "—",
                rec.AiSuggestedSolution?.Length ?? 0,
                rec.EscalationSignal,
                rec.RelatedKbArticleId?.ToString() ?? "—");
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — nothing to log at Error.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AiTriageEnrichment] Unexpected failure enriching recommendation #{Id}.", recommendationId);
        }
    }

    // ── Individual steps ──────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the naive "most-common sub-category" hint with a content-aware
    /// LLM pick from the valid candidates under the predicted parent category.
    /// If the LLM can't decide or returns an id we don't recognise, we leave
    /// the ML.NET-set value alone.
    /// </summary>
    private async Task RefineSubCategoryAsync(AiRecommendation rec, string title, string description, CancellationToken ct)
    {
        if (!rec.SuggestedCategory.HasValue) return;

        try
        {
            var candidates = await _context.TicketSubCategories
                .Where(s => s.Category == rec.SuggestedCategory!.Value)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .AsNoTracking()
                .ToListAsync(ct);
            if (candidates.Count == 0) return;

            var picked = await _ollama.ClassifySubCategoryAsync(
                title, description,
                candidates.Select(c => (c.Id, c.Name)).ToList(),
                ct);
            if (picked.HasValue) rec.SuggestedSubCategoryId = picked.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiTriageEnrichment] Sub-cat refinement failed for rec #{Id}.", rec.Id);
        }
    }

    private async Task AddSuggestedSolutionAsync(AiRecommendation rec, string title, string description, CancellationToken ct)
    {
        try
        {
            var (solution, _, _) = await _ollama.SuggestSolutionWithErrorAsync(title, description, ct);
            if (!string.IsNullOrWhiteSpace(solution))
            {
                // Guard against a runaway model producing something huge — the DB
                // column is capped at 4000 chars but truncating here keeps the UI
                // clean and matches the model annotation.
                rec.AiSuggestedSolution = solution.Length > 4000
                    ? solution[..4000]
                    : solution;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiTriageEnrichment] Suggested-solution failed for rec #{Id}.", rec.Id);
        }
    }

    private async Task DetectEscalationSignalAsync(AiRecommendation rec, string description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        try
        {
            var (isEscalating, reason) = await _ollama.DetectEscalationAsync(description, ct);
            rec.EscalationSignal = isEscalating;
            rec.EscalationReason = isEscalating
                ? (string.IsNullOrWhiteSpace(reason) ? "Customer signals urgency" : reason)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiTriageEnrichment] Escalation detection failed for rec #{Id}.", rec.Id);
        }
    }

    /// <summary>
    /// Shortlists the most-recent published KB articles (arbitrary but bounded
    /// cheap query) and asks the LLM whether one of them fits. We keep the
    /// candidate list short (25) so the prompt stays cheap; the LLM's own
    /// context handles the "does any of these actually match" judgement.
    /// </summary>
    private async Task MatchKbArticleAsync(AiRecommendation rec, string title, string description, CancellationToken ct)
    {
        try
        {
            var kb = await _context.KbArticles
                .Where(a => a.IsPublished)
                .OrderByDescending(a => a.CreatedDate)
                .Take(25)
                .Select(a => new { a.Id, a.Title })
                .AsNoTracking()
                .ToListAsync(ct);
            if (kb.Count == 0) return;

            var picked = await _ollama.MatchKbArticleAsync(
                title, description,
                kb.Select(k => (k.Id, k.Title)).ToList(),
                ct);
            if (picked.HasValue) rec.RelatedKbArticleId = picked.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiTriageEnrichment] KB match failed for rec #{Id}.", rec.Id);
        }
    }
}
