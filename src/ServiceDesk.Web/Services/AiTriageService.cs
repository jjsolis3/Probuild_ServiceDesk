using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.ML.Data;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using System.Diagnostics;

namespace ServiceDesk.Web.Services;

/// <summary>
/// ML.NET-backed triage service that predicts Category and Priority for new tickets.
///
/// Lifecycle:
///  1. On first triage request (or explicit TrainAsync call) the service loads all
///     resolved/closed tickets from the DB and trains two SDCA multiclass classifiers
///     (one for Category, one for Priority).
///  2. Trained PredictionEngines are cached in memory (singleton).
///  3. TriageAsync() returns suggested category, priority, and confidence scores.
///  4. Results are persisted as AiRecommendation rows in the DB by the caller.
///
/// Thread-safety: a SemaphoreSlim(1,1) guards the training phase; PredictionEngine
/// is used per-call after that (each call is fast, &lt;1 ms).
/// </summary>
public class AiTriageService
{
    // ── ML.NET input / output types ──────────────────────────────────────────

    private class TicketTextInput
    {
        public string Text { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    private class MultiClassPrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; } = string.Empty;

        [ColumnName("Score")]
        public float[] Score { get; set; } = [];
    }

    // ── Public result type ───────────────────────────────────────────────────

    public record TriageResult(
        TicketCategory? SuggestedCategory,
        float CategoryConfidence,
        TicketPriority? SuggestedPriority,
        float PriorityConfidence
    );

    // ── Internal state ───────────────────────────────────────────────────────

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiTriageService> _logger;
    private readonly MLContext _mlContext = new(seed: 0);
    private readonly SemaphoreSlim _trainLock = new(1, 1);

    private PredictionEngine<TicketTextInput, MultiClassPrediction>? _categoryEngine;
    private PredictionEngine<TicketTextInput, MultiClassPrediction>? _priorityEngine;
    private DateTime _lastTrainedUtc = DateTime.MinValue;

    // Retrain every 24 h so the model picks up newly resolved tickets.
    private static readonly TimeSpan RetrainInterval = TimeSpan.FromHours(24);

    public AiTriageService(IServiceScopeFactory scopeFactory, ILogger<AiTriageService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public bool IsModelTrained => _categoryEngine != null && _priorityEngine != null;

    /// <summary>
    /// Triages a ticket using the trained model.
    /// Returns null when the model is not trained, the feature flag is off,
    /// or training data is insufficient.
    /// </summary>
    public async Task<TriageResult?> TriageAsync(string title, string description)
    {
        await EnsureModelTrainedAsync();

        if (_categoryEngine == null || _priorityEngine == null)
            return null;

        var text = BuildText(title, description);
        var input = new TicketTextInput { Text = text };

        var catPred  = _categoryEngine.Predict(input);
        var priPred  = _priorityEngine.Predict(input);

        var catScore  = catPred.Score?.Length > 0 ? catPred.Score.Max() : 0f;
        var priScore  = priPred.Score?.Length > 0 ? priPred.Score.Max() : 0f;

        Enum.TryParse<TicketCategory>(catPred.PredictedLabel, out var cat);
        Enum.TryParse<TicketPriority>(priPred.PredictedLabel, out var pri);

        // ── Sentiment / urgency boost ─────────────────────────────────────────
        var boostedPri = SentimentService.GetBoostPriority(title, description, pri);
        if (boostedPri.HasValue)
        {
            _logger.LogInformation(
                "[AiTriage] Urgency detected — boosting priority {From} → {To}",
                pri, boostedPri.Value);
            pri      = boostedPri.Value;
            priScore = Math.Max(priScore, 0.72f); // ensure boosted priority passes threshold
        }

        return new TriageResult(cat, catScore, pri, priScore);
    }

    /// <summary>
    /// Force-retrains the model immediately (e.g. called from admin UI or on startup).
    /// </summary>
    public Task TrainNowAsync() => TrainAsync(force: true);

    /// <summary>
    /// Runs triage for a ticket and persists an AiRecommendation row when confidence
    /// meets the configured threshold.
    ///
    /// Safe to call from fire-and-forget Task.Run — creates its own DB scope so the
    /// HTTP request's DbContext lifetime doesn't matter.
    /// </summary>
    public async Task TriageAndSaveAsync(
        int ticketId, string title, string description, int? branchId,
        AssignmentResolverService? assignmentResolver = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

            // Read feature flag + threshold
            var settings = await context.AppSettings
                .Where(s => s.Key == "AiTriageEnabled" || s.Key == "AiConfidenceThreshold")
                .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);

            if (!settings.TryGetValue("AiTriageEnabled", out var enabled)
                || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                return;

            float threshold = 0.65f;
            if (settings.TryGetValue("AiConfidenceThreshold", out var thStr)
                && float.TryParse(thStr, out var th))
                threshold = th;

            var result = await TriageAsync(title, description);
            if (result == null) return;

            if (result.CategoryConfidence < threshold && result.PriorityConfidence < threshold)
                return;

            // Dismiss prior Pending recommendations for this ticket
            var existing = await context.AiRecommendations
                .Where(r => r.TicketId == ticketId && r.Status == "Pending")
                .ToListAsync();
            foreach (var old in existing)
            {
                old.Status       = "Dismissed";
                old.ReviewedDate = DateTime.UtcNow;
                old.ReviewedBy   = "System (retriage)";
            }

            // Suggest assignee based on predicted category
            int? suggestedAssigneeId = null;
            if (result.SuggestedCategory.HasValue && result.CategoryConfidence >= threshold)
            {
                var resolver = scope.ServiceProvider.GetService<AssignmentResolverService>()
                               ?? assignmentResolver;
                if (resolver != null)
                {
                    suggestedAssigneeId = await resolver.ResolveAsync(
                        result.SuggestedCategory.Value, branchId, defaultAssigneeId: null);
                }
            }

            context.AiRecommendations.Add(new AiRecommendation
            {
                TicketId            = ticketId,
                SuggestedCategory   = result.CategoryConfidence >= threshold ? (int?)result.SuggestedCategory   : null,
                SuggestedPriority   = result.PriorityConfidence >= threshold ? (int?)result.SuggestedPriority   : null,
                SuggestedAssigneeId = suggestedAssigneeId,
                CategoryConfidence  = result.CategoryConfidence,
                PriorityConfidence  = result.PriorityConfidence,
                Status              = "Pending",
                CreatedDate         = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            _logger.LogInformation(
                "[AiTriage] Recommendation saved for ticket #{Id}: cat={Cat} ({CatPct:F0}%), pri={Pri} ({PriPct:F0}%)",
                ticketId,
                result.SuggestedCategory, result.CategoryConfidence * 100,
                result.SuggestedPriority, result.PriorityConfidence * 100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AiTriage] TriageAndSaveAsync failed for ticket #{Id}.", ticketId);
        }
    }

    // ── Training logic ───────────────────────────────────────────────────────

    private async Task EnsureModelTrainedAsync()
    {
        if (IsModelTrained && DateTime.UtcNow - _lastTrainedUtc < RetrainInterval)
            return;
        await TrainAsync(force: false);
    }

    private async Task TrainAsync(bool force)
    {
        if (!await _trainLock.WaitAsync(0))   // non-blocking: skip if already training
            return;

        try
        {
            if (!force && IsModelTrained && DateTime.UtcNow - _lastTrainedUtc < RetrainInterval)
                return;

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

            // ── Feature flag check ────────────────────────────────────────
            var enabledSetting = await context.AppSettings
                .Where(s => s.Key == "AiTriageEnabled")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();

            if (!string.Equals(enabledSetting, "true", StringComparison.OrdinalIgnoreCase))
                return;

            // ── Load minimum training ticket count ────────────────────────
            int minTickets = 20;
            var minSetting = await context.AppSettings
                .Where(s => s.Key == "AiMinTrainingTickets")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();
            if (int.TryParse(minSetting, out var parsed))
                minTickets = Math.Max(10, parsed);

            // ── Load training data (resolved / closed tickets) ────────────
            var sw = Stopwatch.StartNew();

            var trainingTickets = await context.Tickets
                .Where(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
                .Select(t => new { t.Title, t.Description, t.Category, t.Priority })
                .ToListAsync();

            var log = new AiRunLog
            {
                RunType             = "Training",
                TrainingTicketCount = trainingTickets.Count,
                ModelVersion        = $"SDCA-{DateTime.UtcNow:yyyyMMdd}"
            };

            if (trainingTickets.Count < minTickets)
            {
                log.Success      = false;
                log.ErrorMessage = $"Insufficient training data: {trainingTickets.Count} resolved tickets (need {minTickets}).";
                log.DurationMs   = sw.Elapsed.TotalMilliseconds;
                context.AiRunLogs.Add(log);
                await context.SaveChangesAsync();
                _logger.LogInformation("[AiTriage] {Msg}", log.ErrorMessage);
                return;
            }

            // ── Build ML.NET datasets ─────────────────────────────────────
            var categoryData = trainingTickets.Select(t => new TicketTextInput
            {
                Text  = BuildText(t.Title, t.Description),
                Label = t.Category.ToString()
            }).ToList();

            var priorityData = trainingTickets.Select(t => new TicketTextInput
            {
                Text  = BuildText(t.Title, t.Description),
                Label = t.Priority.ToString()
            }).ToList();

            var catDataView = _mlContext.Data.LoadFromEnumerable(categoryData);
            var priDataView = _mlContext.Data.LoadFromEnumerable(priorityData);

            // ── Build pipelines ───────────────────────────────────────────
            var catPipeline = BuildPipeline(_mlContext);
            var priPipeline = BuildPipeline(_mlContext);

            // ── Train ─────────────────────────────────────────────────────
            var catModel = catPipeline.Fit(catDataView);
            var priModel = priPipeline.Fit(priDataView);

            // ── Swap in new engines (atomic pointer swap) ─────────────────
            var newCatEngine = _mlContext.Model.CreatePredictionEngine<TicketTextInput, MultiClassPrediction>(catModel);
            var newPriEngine = _mlContext.Model.CreatePredictionEngine<TicketTextInput, MultiClassPrediction>(priModel);

            _categoryEngine = newCatEngine;
            _priorityEngine = newPriEngine;
            _lastTrainedUtc = DateTime.UtcNow;

            sw.Stop();
            log.Success    = true;
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            context.AiRunLogs.Add(log);
            await context.SaveChangesAsync();

            _logger.LogInformation(
                "[AiTriage] Model trained on {Count} tickets in {Ms:F0} ms.",
                trainingTickets.Count, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AiTriage] Training failed.");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
                ctx.AiRunLogs.Add(new AiRunLog
                {
                    RunType      = "Training",
                    Success      = false,
                    ErrorMessage = ex.Message
                });
                await ctx.SaveChangesAsync();
            }
            catch { /* don't let logging failure mask the original error */ }
        }
        finally
        {
            _trainLock.Release();
        }
    }

    // ── Pipeline factory ─────────────────────────────────────────────────────

    private static IEstimator<ITransformer> BuildPipeline(MLContext ml)
    {
        return ml.Transforms.Conversion
                   .MapValueToKey("Label", "Label")
               .Append(ml.Transforms.Text.FeaturizeText("Features", "Text"))
               .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                           labelColumnName:    "Label",
                           featureColumnName:  "Features"))
               .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel", "PredictedLabel"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildText(string title, string description)
        => (title + " " + (description ?? string.Empty)).Trim();
}
