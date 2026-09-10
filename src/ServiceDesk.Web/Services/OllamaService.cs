using System.Text.RegularExpressions;
using ServiceDesk.Web.Services.Llm;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Facade over the multi-provider LLM stack. Kept named "OllamaService" so
/// every existing caller (TicketsController, WorkflowEngineService, the
/// enrichment service, the streaming SSE endpoints, …) continues to work
/// unchanged — but underneath it now routes through
/// <see cref="LlmProviderRouter"/>, which picks Ollama, Gemini, OpenAI, or
/// Anthropic Claude based on the admin's Settings → AI selection.
///
/// Public method signatures are stable — the shape of every tuple / record
/// returned to callers is preserved. When you rename this class later, do
/// it in one commit with a solution-wide search/replace to keep the diff
/// mechanical.
/// </summary>
public class OllamaService
{
    private readonly LlmProviderRouter _router;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(LlmProviderRouter router, ILogger<OllamaService> logger)
    {
        _router = router;
        _logger = logger;
    }

    /// <summary>Categorises why an LLM call failed so the UI can show a useful message.</summary>
    /// <remarks>Kept for public callers that referenced the old shape; maps 1:1 to <see cref="LlmErrorKind"/>.</remarks>
    public enum OllamaErrorKind
    {
        None, Disabled, ConnectionRefused, Timeout, HttpError,
        EmptyResponse, InvalidResponse, Unknown,
    }

    /// <summary>Per-feature model role used by the router to pick a provider + model override.</summary>
    public enum ModelRole { Default, Draft, Triage }

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task<string?> SummarizeTicketAsync(string title, string description, CancellationToken ct = default)
    {
        var prompt =
            "You are an IT help desk assistant. Summarize the following support ticket " +
            "in 2-3 concise sentences. Focus on the core problem and any key technical " +
            "details. Do not include greetings.\n\n" +
            $"Title: {title}\n\nDescription:\n{description}";
        var result = await RouteAsync(prompt, ModelRole.Default, ct);
        return result.Text;
    }

    public async Task<string?> DraftReplyAsync(string title, string description,
        string? resolutionNotes = null, CancellationToken ct = default)
    {
        var result = await RouteAsync(BuildDraftReplyPrompt(title, description, resolutionNotes), ModelRole.Draft, ct);
        return result.Text;
    }

    public async Task<(string? Draft, string? Error, double Latency)> DraftReplyWithErrorAsync(
        string title, string description, string? resolutionNotes = null, CancellationToken ct = default)
    {
        var result = await RouteAsync(BuildDraftReplyPrompt(title, description, resolutionNotes), ModelRole.Draft, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    public async IAsyncEnumerable<string> StreamDraftReplyAsync(
        string title, string description, string? resolutionNotes = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in _router.StreamAsync(
            BuildDraftReplyPrompt(title, description, resolutionNotes), LlmProviderRouter.FeatureRole.Draft, ct))
            yield return token;
    }

    public async Task<string?> SummarizeThreadAsync(
        string title, string description, IEnumerable<string> noteContents, CancellationToken ct = default)
    {
        var result = await RouteAsync(BuildThreadPrompt(title, description, noteContents), ModelRole.Default, ct);
        return result.Text;
    }

    public async Task<(string? Summary, string? Error, double Latency)> SummarizeThreadWithErrorAsync(
        string title, string description, IEnumerable<string> noteContents, CancellationToken ct = default)
    {
        var result = await RouteAsync(BuildThreadPrompt(title, description, noteContents), ModelRole.Default, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    public async IAsyncEnumerable<string> StreamSummarizeThreadAsync(
        string title, string description, IEnumerable<string> noteContents,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in _router.StreamAsync(
            BuildThreadPrompt(title, description, noteContents), LlmProviderRouter.FeatureRole.Default, ct))
            yield return token;
    }

    public async Task<(string? Problem, string? Solution)> GenerateKbDraftAsync(
        string title, string description, string? resolutionNotes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionNotes)) return (null, null);

        var prompt =
            "You are an IT knowledge base editor. A support ticket has been resolved. " +
            "Rewrite it as a clean, searchable knowledge base article. " +
            "Return EXACTLY two labeled sections and nothing else:\n" +
            "PROBLEM: (one short paragraph describing the symptom or error a user would search for)\n" +
            "SOLUTION: (numbered step-by-step instructions to resolve the issue)\n\n" +
            $"Ticket title: {title}\n" +
            $"Issue description:\n{description}\n\n" +
            $"Agent resolution notes:\n{resolutionNotes}\n\n" +
            "Write only the two labeled sections.";

        var result = await RouteAsync(prompt, ModelRole.Draft, ct);
        if (result.Text == null) return (null, null);

        var problemMatch  = Regex.Match(result.Text, @"PROBLEM:\s*(.+?)(?=SOLUTION:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var solutionMatch = Regex.Match(result.Text, @"SOLUTION:\s*(.+)$",              RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var problem  = problemMatch.Success  ? problemMatch.Groups[1].Value.Trim()  : null;
        var solution = solutionMatch.Success ? solutionMatch.Groups[1].Value.Trim() : null;
        return (problem, solution);
    }

    public async Task<(bool IsEscalating, string Reason)> DetectEscalationAsync(
        string commentText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commentText)) return (false, "");

        var prompt =
            "You are an IT help desk supervisor assistant. Read the following customer comment " +
            "and decide if it signals escalation: frustration, anger, urgency, a deadline, " +
            "mentions of a manager, legal action, public review, or repeated unresolved requests.\n\n" +
            "Reply with EXACTLY one of these two formats:\n" +
            "ESCALATING: <short reason under 10 words>\n" +
            "NOT_ESCALATING\n\n" +
            $"Comment:\n{(commentText.Length > 500 ? commentText[..500] : commentText)}";

        var result = await RouteAsync(prompt, ModelRole.Triage, ct);
        if (result.Text == null) return (false, "");

        if (result.Text.TrimStart().StartsWith("ESCALATING:", StringComparison.OrdinalIgnoreCase))
        {
            var reason = result.Text.TrimStart()[11..].Trim();
            return (true, reason.Length > 120 ? reason[..120] : reason);
        }
        return (false, "");
    }

    public async Task<(string? Solution, string? Error, double Latency)> SuggestSolutionWithErrorAsync(
        string title, string description, CancellationToken ct = default)
    {
        var result = await RouteAsync(BuildSolutionPrompt(title, description), ModelRole.Default, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    public async IAsyncEnumerable<string> StreamSolutionAsync(
        string title, string description,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var t in _router.StreamAsync(BuildSolutionPrompt(title, description), LlmProviderRouter.FeatureRole.Default, ct))
            yield return t;
    }

    public async Task<int?> ClassifySubCategoryAsync(string title, string description,
        IReadOnlyList<(int Id, string Name)> candidates, CancellationToken ct = default)
    {
        if (candidates == null || candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Id;

        var lines = string.Join("\n", candidates.Select((c, i) => $"[{i + 1}] id={c.Id} — {c.Name}"));
        var prompt =
            "You are an IT help desk classifier. Pick the single sub-category that best fits " +
            "the ticket below. Choose from this list and reply with ONLY the numeric id, " +
            "nothing else — no explanation, no punctuation.\n\n" +
            $"Sub-categories:\n{lines}\n\n" +
            $"Ticket title: {title}\n\nDescription:\n{description}\n\n" +
            "Reply with only the chosen id (e.g. '42').";

        var result = await RouteAsync(prompt, ModelRole.Triage, ct);
        if (string.IsNullOrWhiteSpace(result.Text)) return null;

        var match = Regex.Match(result.Text, @"-?\d+");
        if (!match.Success || !int.TryParse(match.Value, out var picked)) return null;
        if (candidates.Any(c => c.Id == picked)) return picked;
        if (picked >= 1 && picked <= candidates.Count) return candidates[picked - 1].Id;
        return null;
    }

    public async Task<int?> MatchKbArticleAsync(string title, string description,
        IReadOnlyList<(int Id, string Title)> candidates, CancellationToken ct = default)
    {
        if (candidates == null || candidates.Count == 0) return null;

        var lines = string.Join("\n", candidates.Select((c, i) => $"[{i + 1}] id={c.Id} — {c.Title}"));
        var prompt =
            "You are an IT help desk assistant. Pick the single KB article that most closely " +
            "matches the ticket below, so the agent can point the requester at it. If none of " +
            "the articles fit, reply exactly with the word NONE. Otherwise reply with ONLY the " +
            "numeric id, nothing else.\n\n" +
            $"KB articles:\n{lines}\n\n" +
            $"Ticket title: {title}\n\nDescription:\n{description}\n\n" +
            "Reply with only the chosen id (e.g. '42') or NONE.";

        var result = await RouteAsync(prompt, ModelRole.Triage, ct);
        var text = result.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("none", StringComparison.OrdinalIgnoreCase)) return null;

        var match = Regex.Match(text, @"-?\d+");
        if (!match.Success || !int.TryParse(match.Value, out var picked)) return null;
        if (candidates.Any(c => c.Id == picked)) return picked;
        if (picked >= 1 && picked <= candidates.Count) return candidates[picked - 1].Id;
        return null;
    }

    /// <summary>
    /// Tests the currently-active provider's connection with a real generation
    /// probe. Returns a shape compatible with the old Ollama-specific result so
    /// the Settings page's `/Settings/TestOllama` handler stays wire-compatible.
    /// </summary>
    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var (providerName, r) = await _router.TestActiveProviderAsync(ct);
        var message = $"[{providerName}] {r.Message}";
        return r.Ok
            ? new TestConnectionResult(true, message, r.Models, r.LatencyMs)
            : TestConnectionResult.Fail(message, r.Models, r.LatencyMs);
    }

    public sealed record TestConnectionResult(bool Ok, string Message, string[] Models, double LatencyMs)
    {
        public static TestConnectionResult Fail(string message, string[] models, double latencyMs)
            => new(false, message, models, latencyMs);
    }

    public async Task<string?> GenerateRawAsync(string prompt, CancellationToken ct = default)
    {
        var result = await RouteAsync(prompt, ModelRole.Default, ct);
        return result.Text;
    }

    /// <summary>
    /// Fires a tiny prompt at the ACTIVE provider to keep any locally-hosted
    /// model resident. Ollama honours <c>keep_alive</c> for real warmup; the
    /// cloud providers use this as a benign heartbeat (essentially a no-op
    /// but harmless).
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _router.GenerateAsync("ok", LlmProviderRouter.FeatureRole.Default, ct);
            if (result.Kind == LlmErrorKind.None)
                _logger.LogInformation("[Llm] Warmup OK in {Ms:F0}ms.", result.LatencyMs);
            else if (result.Kind == LlmErrorKind.Disabled)
                _logger.LogDebug("[Llm] Warmup skipped — provider disabled.");
            else
                _logger.LogWarning("[Llm] Warmup failed ({Kind}): {Error}", result.Kind, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Llm] Warmup threw — will retry on next interval.");
        }
    }

    // ── Prompt builders ──────────────────────────────────────────────────────

    private static string BuildDraftReplyPrompt(string title, string description, string? resolutionNotes)
    {
        var resolutionPart = string.IsNullOrWhiteSpace(resolutionNotes)
            ? string.Empty
            : $"\n\nResolution notes provided by the IT agent:\n{resolutionNotes}";
        return
            "You are an IT help desk agent. Write a professional, friendly reply to a " +
            "support ticket submitter. Keep it concise (3-5 sentences). Do not use " +
            "placeholder text like [Your Name].\n\n" +
            $"Ticket title: {title}\n\nIssue description:\n{description}" +
            resolutionPart +
            "\n\nWrite only the reply body — no subject line, no signatures.";
    }

    private static string BuildThreadPrompt(string title, string description, IEnumerable<string> noteContents)
    {
        var notes = noteContents?.ToList() ?? [];
        var noteBlock = notes.Count > 0
            ? "\n\nAgent notes (chronological):\n" + string.Join("\n---\n", notes.Select((n, i) => $"[{i + 1}] {n}"))
            : string.Empty;
        return
            "You are an IT help desk assistant. Summarize the following support ticket " +
            "thread in 3-5 sentences, covering the original issue, any troubleshooting " +
            "steps taken, and the current state or resolution. Be concise and factual.\n\n" +
            $"Title: {title}\n\nOriginal description:\n{description}" +
            noteBlock;
    }

    private static string BuildSolutionPrompt(string title, string description) =>
        "You are an experienced IT support engineer. A help desk ticket has been submitted. " +
        "Recommend concise, actionable resolution steps that the IT staff member should follow " +
        "to diagnose and fix the issue. Use a numbered list. Focus only on technical steps — " +
        "do not write a reply to the user, do not use greetings or sign-offs.\n\n" +
        $"Ticket title: {title}\n\nIssue description:\n{description}\n\n" +
        "Write only the numbered resolution steps.";

    // ── Router adapter ───────────────────────────────────────────────────────

    private Task<LlmGenerationResult> RouteAsync(string prompt, ModelRole role, CancellationToken ct) =>
        _router.GenerateAsync(prompt, role switch
        {
            ModelRole.Draft  => LlmProviderRouter.FeatureRole.Draft,
            ModelRole.Triage => LlmProviderRouter.FeatureRole.Triage,
            _                => LlmProviderRouter.FeatureRole.Default,
        }, ct);
}
