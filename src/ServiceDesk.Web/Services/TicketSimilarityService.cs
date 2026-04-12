using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Infrastructure.Data;
using System.Text.RegularExpressions;

namespace ServiceDesk.Web.Services;

/// <summary>Represents a ticket that is similar to a query, with a 0–100 similarity score.</summary>
public record SimilarTicketResult(
    int TicketId,
    string Title,
    TicketStatus Status,
    TicketPriority Priority,
    double ScorePct);

/// <summary>
/// Singleton TF-IDF similarity engine.
/// Builds an in-memory vector corpus from the last 365 days of tickets
/// and uses cosine similarity to find the most similar open tickets
/// for a given query.
///
/// The corpus is rebuilt automatically every hour via a lazy-init pattern;
/// a SemaphoreSlim(1,1) prevents concurrent rebuilds.
/// </summary>
public class TicketSimilarityService
{
    // ── Internal document record ─────────────────────────────────────────────
    private sealed record TicketDocument(
        int Id,
        string Title,
        TicketStatus Status,
        TicketPriority Priority,
        Dictionary<string, double> TfIdf);

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TicketSimilarityService> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private List<TicketDocument> _corpus = [];
    private Dictionary<string, double> _idf = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBuilt = DateTime.MinValue;

    private static readonly TimeSpan RebuildInterval = TimeSpan.FromHours(1);

    // ── English stop words ────────────────────────────────────────────────────
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","is","are","was","were","have","has","had","be","been","being",
        "do","does","did","will","would","could","should","may","might","shall",
        "can","a","an","and","or","but","in","on","at","to","for","of","with",
        "by","from","as","into","through","during","before","after","above","below",
        "up","down","out","off","over","under","again","then","once","i","me","my",
        "we","our","you","your","he","she","it","its","they","them","their",
        "what","which","who","when","where","why","how","all","both","each","more",
        "most","other","some","such","no","nor","not","only","own","same","so",
        "than","too","very","s","t","just","because","also","while","this","that",
        "these","those","am","him","his","her","if","about","please","hi","hello",
        "dear","regards","thank","thanks","need","want","get","got","ticket",
        "issue","problem","request","help","support","new","old","one","use","using"
    };

    // ── Constructor ───────────────────────────────────────────────────────────
    public TicketSimilarityService(
        IServiceScopeFactory scopeFactory,
        ILogger<TicketSimilarityService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the top N tickets most similar to the given title+description.
    /// Excludes the ticket with <paramref name="excludeTicketId"/> (the current ticket).
    /// Returns an empty list when the corpus has not been built yet.
    /// </summary>
    public async Task<List<SimilarTicketResult>> FindSimilarAsync(
        string title,
        string description,
        int excludeTicketId = 0,
        int topN = 5)
    {
        await EnsureCorpusBuiltAsync();
        if (_corpus.Count == 0) return [];

        var queryText = ((title ?? string.Empty) + " " + (description ?? string.Empty)).Trim();
        var queryTokens = Tokenize(queryText);
        if (queryTokens.Count == 0) return [];

        // Build TF-IDF vector for the query
        var tf = queryTokens
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (double)g.Count() / queryTokens.Count, StringComparer.OrdinalIgnoreCase);

        var queryVec = tf
            .Where(kv => _idf.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value * _idf[kv.Key], StringComparer.OrdinalIgnoreCase);

        if (queryVec.Count == 0) return [];

        // Score corpus and return top N above the minimum similarity threshold
        return _corpus
            .Where(doc => doc.Id != excludeTicketId)
            .Select(doc => (doc, score: CosineSimilarity(queryVec, doc.TfIdf)))
            .Where(x => x.score > 0.12)
            .OrderByDescending(x => x.score)
            .Take(topN)
            .Select(x => new SimilarTicketResult(
                x.doc.Id, x.doc.Title, x.doc.Status, x.doc.Priority,
                Math.Round(x.score * 100, 1)))
            .ToList();
    }

    /// <summary>Force-rebuilds the corpus (e.g. after a bulk import).</summary>
    public async Task RebuildAsync()
    {
        _lastBuilt = DateTime.MinValue;
        await EnsureCorpusBuiltAsync();
    }

    // ── Corpus build ──────────────────────────────────────────────────────────

    private async Task EnsureCorpusBuiltAsync()
    {
        if (_corpus.Count > 0 && DateTime.UtcNow - _lastBuilt < RebuildInterval)
            return;

        if (!await _buildLock.WaitAsync(0)) return; // skip if already building

        try
        {
            if (_corpus.Count > 0 && DateTime.UtcNow - _lastBuilt < RebuildInterval) return;

            using var scope = _scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

            var cutoff = DateTime.UtcNow.AddDays(-365);
            var tickets = await ctx.Tickets
                .Where(t => t.CreatedDate >= cutoff)
                .Select(t => new { t.Id, t.Title, t.Description, t.Status, t.Priority })
                .ToListAsync();

            if (tickets.Count == 0) { _lastBuilt = DateTime.UtcNow; return; }

            // Tokenize all documents
            var tokenized = tickets
                .Select(t => (
                    t.Id, t.Title, t.Status, t.Priority,
                    Tokens: Tokenize((t.Title + " " + (t.Description ?? string.Empty)).Trim())
                ))
                .ToList();

            // Build IDF — log(N / df + 1) for smoothing
            int n = tokenized.Count;
            var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, _, _, _, tokens) in tokenized)
                foreach (var term in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
                    df[term] = df.GetValueOrDefault(term) + 1;

            _idf = df.ToDictionary(
                kv => kv.Key,
                kv => Math.Log((double)n / (kv.Value + 1) + 1),
                StringComparer.OrdinalIgnoreCase);

            // Build TF-IDF document vectors
            _corpus = tokenized.Select(t =>
            {
                var tff = t.Tokens
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => (double)g.Count() / t.Tokens.Count, StringComparer.OrdinalIgnoreCase);

                var vec = tff
                    .Where(kv => _idf.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value * _idf[kv.Key], StringComparer.OrdinalIgnoreCase);

                return new TicketDocument(t.Id, t.Title, t.Status, t.Priority, vec);
            }).ToList();

            _lastBuilt = DateTime.UtcNow;
            _logger.LogInformation("[Similarity] Corpus built: {Count} tickets indexed.", _corpus.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Similarity] Failed to build corpus.");
        }
        finally
        {
            _buildLock.Release();
        }
    }

    // ── Maths helpers ─────────────────────────────────────────────────────────

    private static List<string> Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
             .Where(t => t.Length >= 3 && !StopWords.Contains(t))
             .ToList();

    private static double CosineSimilarity(
        Dictionary<string, double> a,
        Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        double dot = 0, magA = 0, magB = 0;

        foreach (var kv in a)
        {
            magA += kv.Value * kv.Value;
            if (b.TryGetValue(kv.Key, out var bVal))
                dot += kv.Value * bVal;
        }
        foreach (var kv in b)
            magB += kv.Value * kv.Value;

        return (magA == 0 || magB == 0) ? 0
            : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
