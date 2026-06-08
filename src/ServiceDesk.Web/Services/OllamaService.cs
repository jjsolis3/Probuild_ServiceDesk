using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;
using System.Text;
using System.Text.Json;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Calls a locally running Ollama server to generate AI text for ticket summaries
/// and draft replies.
///
/// Ollama REST API endpoint: POST {OllamaUrl}/api/generate
/// The response is streamed as newline-delimited JSON; we accumulate until
/// "done":true to get the full response.
///
/// All settings (URL, model, timeout, keep_alive, num_predict, per-feature
/// model overrides) are read live from AppSettings on each call so admin
/// changes take effect immediately without restarting the app. The named
/// HttpClient ("Ollama") provides connection pooling — the per-call timeout
/// is enforced via a CancellationTokenSource so admins can tune it from the
/// Settings page without an app restart.
/// </summary>
public class OllamaService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpClientFactory;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaService> logger)
    {
        _scopeFactory      = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>Categorises why an Ollama call failed so the UI can show a useful message.</summary>
    public enum OllamaErrorKind
    {
        None,
        Disabled,
        ConnectionRefused,
        Timeout,
        HttpError,
        EmptyResponse,
        InvalidResponse,
        Unknown,
    }

    /// <summary>Per-feature model selection so admins can use a faster model for triage and a smarter one for KB drafts.</summary>
    public enum ModelRole { Default, Draft, Triage }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a 2-3 sentence plain-text summary of the ticket issue.
    /// Returns null when Ollama is disabled or the server is unavailable.
    /// </summary>
    public async Task<string?> SummarizeTicketAsync(string title, string description,
        CancellationToken ct = default)
    {
        var prompt =
            $"You are an IT help desk assistant. Summarize the following support ticket " +
            $"in 2-3 concise sentences. Focus on the core problem and any key technical " +
            $"details. Do not include greetings.\n\n" +
            $"Title: {title}\n\nDescription:\n{description}";

        var result = await GenerateAsync(prompt, ModelRole.Default, ct);
        return result.Text;
    }

    /// <summary>
    /// Generates a professional draft reply for the ticket submitter.
    /// Returns null when Ollama is disabled or the server is unavailable.
    /// </summary>
    public async Task<string?> DraftReplyAsync(string title, string description,
        string? resolutionNotes = null, CancellationToken ct = default)
    {
        var result = await GenerateAsync(BuildDraftReplyPrompt(title, description, resolutionNotes), ModelRole.Draft, ct);
        return result.Text;
    }

    /// <summary>
    /// Generates a draft reply and returns a user-facing error message + latency when generation fails.
    /// </summary>
    public async Task<(string? Draft, string? Error, double Latency)> DraftReplyWithErrorAsync(
        string title,
        string description,
        string? resolutionNotes = null,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(BuildDraftReplyPrompt(title, description, resolutionNotes), ModelRole.Draft, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    /// <summary>
    /// Streams a draft reply token by token using Ollama's streaming API.
    /// Yields each text fragment as it arrives so callers can forward to an SSE response.
    /// </summary>
    public async IAsyncEnumerable<string> StreamDraftReplyAsync(
        string title, string description,
        string? resolutionNotes = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in StreamInternalAsync(
            BuildDraftReplyPrompt(title, description, resolutionNotes), ModelRole.Draft, ct))
            yield return token;
    }

    /// <summary>
    /// Summarizes the full ticket thread (description + agent notes) into a concise paragraph.
    /// Returns null when Ollama is disabled or the server is unavailable.
    /// </summary>
    public async Task<string?> SummarizeThreadAsync(
        string title,
        string description,
        IEnumerable<string> noteContents,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(BuildThreadPrompt(title, description, noteContents), ModelRole.Default, ct);
        return result.Text;
    }

    /// <summary>
    /// Summarizes a full ticket thread and returns a user-facing error message + latency when generation fails.
    /// </summary>
    public async Task<(string? Summary, string? Error, double Latency)> SummarizeThreadWithErrorAsync(
        string title,
        string description,
        IEnumerable<string> noteContents,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(BuildThreadPrompt(title, description, noteContents), ModelRole.Default, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    /// <summary>
    /// Streams a thread summary token by token. Mirrors StreamDraftReplyAsync.
    /// </summary>
    public async IAsyncEnumerable<string> StreamSummarizeThreadAsync(
        string title, string description, IEnumerable<string> noteContents,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in StreamInternalAsync(
            BuildThreadPrompt(title, description, noteContents), ModelRole.Default, ct))
            yield return token;
    }

    /// <summary>
    /// Generates a polished Knowledge Base article draft from a resolved ticket.
    /// Returns (Problem, Solution) paragraphs parsed from the LLM output.
    /// Returns (null, null) when Ollama is unavailable or the ticket has no resolution notes.
    /// </summary>
    public async Task<(string? Problem, string? Solution)> GenerateKbDraftAsync(
        string title, string description, string? resolutionNotes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionNotes))
            return (null, null);

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

        var result = await GenerateAsync(prompt, ModelRole.Draft, ct);
        if (result.Text == null) return (null, null);

        var problemMatch  = Regex.Match(result.Text, @"PROBLEM:\s*(.+?)(?=SOLUTION:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var solutionMatch = Regex.Match(result.Text, @"SOLUTION:\s*(.+)$",              RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var problem  = problemMatch.Success  ? problemMatch.Groups[1].Value.Trim()  : null;
        var solution = solutionMatch.Success ? solutionMatch.Groups[1].Value.Trim() : null;

        return (problem, solution);
    }

    /// <summary>
    /// Detects whether a customer comment signals frustration or escalation intent.
    /// Uses the Triage role so admins can point this at a fast/small model
    /// (phi3) while keeping the draft-reply path on a larger model.
    /// </summary>
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

        var result = await GenerateAsync(prompt, ModelRole.Triage, ct);
        if (result.Text == null) return (false, "");

        if (result.Text.TrimStart().StartsWith("ESCALATING:", StringComparison.OrdinalIgnoreCase))
        {
            var reason = result.Text.TrimStart()[11..].Trim();
            return (true, reason.Length > 120 ? reason[..120] : reason);
        }
        return (false, "");
    }

    /// <summary>
    /// Recommends concise resolution steps for IT staff.
    /// </summary>
    public async Task<(string? Solution, string? Error, double Latency)> SuggestSolutionWithErrorAsync(
        string title, string description, CancellationToken ct = default)
    {
        var result = await GenerateAsync(BuildSolutionPrompt(title, description), ModelRole.Default, ct);
        return (result.Text, result.Error, result.LatencyMs);
    }

    /// <summary>Streaming variant of SuggestSolution — yields tokens as Ollama produces them.</summary>
    public async IAsyncEnumerable<string> StreamSolutionAsync(
        string title, string description,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in StreamInternalAsync(BuildSolutionPrompt(title, description), ModelRole.Default, ct))
            yield return token;
    }

    /// <summary>
    /// Tests the Ollama connection AND verifies the configured model can actually
    /// produce tokens — not just that the server is reachable. Returns the model
    /// list plus latency so admins can sanity-check generation speed.
    /// </summary>
    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync();
        try
        {
            var client = _httpClientFactory.CreateClient("Ollama");

            using var tagsResponse = await client.GetAsync($"{settings.Url.TrimEnd('/')}/api/tags", ct);
            if (!tagsResponse.IsSuccessStatusCode)
                return TestConnectionResult.Fail($"Ollama server at {settings.Url} returned HTTP {(int)tagsResponse.StatusCode}.", Array.Empty<string>(), 0);

            var json = await tagsResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                foreach (var m in modelsArr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nameProp))
                        names.Add(nameProp.GetString() ?? "");

            var modelFound = names.Any(n => n.StartsWith(settings.Model, StringComparison.OrdinalIgnoreCase));
            if (!modelFound)
            {
                return TestConnectionResult.Fail(
                    $"Connected to Ollama at {settings.Url}, but model '{settings.Model}' was NOT found. Available: {string.Join(", ", names.DefaultIfEmpty("(none)"))}",
                    names.ToArray(), 0);
            }

            // Real generation probe — we want to know that the model actually
            // produces tokens, not just that the server is alive.
            var probe = await GenerateAsync("Reply with the single word: ok", ModelRole.Default, ct,
                overrideTimeoutSeconds: Math.Max(60, settings.TimeoutSeconds));

            if (probe.Error == null && !string.IsNullOrWhiteSpace(probe.Text))
            {
                return new TestConnectionResult(true,
                    $"Connected to Ollama at {settings.Url}. Model '{settings.Model}' generated a probe response in {probe.LatencyMs / 1000.0:F1}s.",
                    names.ToArray(), probe.LatencyMs);
            }

            return TestConnectionResult.Fail(
                $"Connected to Ollama at {settings.Url} and model '{settings.Model}' is listed, but generation probe failed: {probe.Error ?? "empty response"}.",
                names.ToArray(), probe.LatencyMs);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            return TestConnectionResult.Fail($"Cannot reach Ollama at {settings.Url} — the server is not accepting connections. Verify Ollama is running.", Array.Empty<string>(), 0);
        }
        catch (HttpRequestException ex)
        {
            return TestConnectionResult.Fail($"Cannot reach Ollama server: {ex.Message}", Array.Empty<string>(), 0);
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"Connection test failed: {ex.Message}", Array.Empty<string>(), 0);
        }
    }

    public sealed record TestConnectionResult(bool Ok, string Message, string[] Models, double LatencyMs)
    {
        public static TestConnectionResult Fail(string message, string[] models, double latencyMs)
            => new(false, message, models, latencyMs);
    }

    /// <summary>
    /// Returns the raw Ollama text response for arbitrary prompts.
    /// Used by the workflow engine for AI-based routing/classification.
    /// Returns null when Ollama is disabled or unreachable.
    /// </summary>
    public async Task<string?> GenerateRawAsync(string prompt, CancellationToken ct = default)
    {
        var result = await GenerateAsync(prompt, ModelRole.Default, ct);
        return result.Text;
    }

    /// <summary>
    /// Fires a tiny prompt at the configured model to keep it loaded in memory.
    /// Called by the warmup hosted service on startup and at a regular cadence.
    /// Never throws — failure just gets logged.
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        try
        {
            var s = await LoadSettingsAsync();
            if (!s.Enabled || !s.WarmupEnabled) return;

            // 1-token reply ensures Ollama loads the model into memory but
            // doesn't waste tokens. The keep_alive in the request body tells
            // Ollama how long to keep it resident afterwards.
            var result = await GenerateAsync("ok", ModelRole.Default, ct,
                overrideTimeoutSeconds: Math.Max(s.TimeoutSeconds, 120),
                overrideNumPredict: 1);

            if (result.Error == null)
                _logger.LogInformation("[Ollama] Warmup OK in {Ms:F0}ms (model={Model}).", result.LatencyMs, s.Model);
            else
                _logger.LogWarning("[Ollama] Warmup failed: {Error}", result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] Warmup threw — will retry on next interval.");
        }
    }

    // ── Prompt builders (kept identical to previous implementation) ──────────

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

    // ── Streaming core ───────────────────────────────────────────────────────

    private async IAsyncEnumerable<string> StreamInternalAsync(
        string prompt,
        ModelRole role,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var s = await LoadSettingsAsync();
        if (!s.Enabled) yield break;

        var model = ResolveModel(s, role);
        var bodyJson = BuildRequestJson(model, prompt, stream: true, s.KeepAlive, s.NumPredict);

        // Streaming gets the full configured timeout — large responses can take a while
        using var cts = LinkTimeout(ct, s.TimeoutSeconds);
        var client = _httpClientFactory.CreateClient("Ollama");

        var (resp, _) = await TryConnectOllamaStreamAsync(client, s.Url, model, bodyJson, cts.Token);
        if (resp == null) yield break;

        using (resp)
        {
            var responseStream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using (responseStream)
            {
                using var reader = new StreamReader(responseStream);
                while (!cts.Token.IsCancellationRequested)
                {
                    var (line, eof) = await SafeReadLineAsync(reader, cts.Token);
                    if (eof) yield break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var (token, done) = ParseOllamaStreamLine(line);
                    if (token is not null) yield return token;
                    if (done) yield break;
                }
            }
        }
    }

    private async Task<(HttpResponseMessage? Resp, OllamaErrorKind Kind)> TryConnectOllamaStreamAsync(
        HttpClient client, string url, string model, string bodyJson, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/api/generate")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Accept", "application/json");
            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Ollama] Stream HTTP {Code} from {Url} model={Model}.",
                    (int)resp.StatusCode, url, model);
                resp.Dispose();
                return (null, OllamaErrorKind.HttpError);
            }
            return (resp, OllamaErrorKind.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Ollama] Stream timed out connecting to {Url}.", url);
            return (null, OllamaErrorKind.Timeout);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            _logger.LogWarning("[Ollama] Stream connection refused at {Url}. Is Ollama running?", url);
            return (null, OllamaErrorKind.ConnectionRefused);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ollama] Stream connect failed to {Url}.", url);
            return (null, OllamaErrorKind.Unknown);
        }
    }

    private static async ValueTask<(string? Line, bool Eof)> SafeReadLineAsync(
        StreamReader reader, CancellationToken ct)
    {
        try
        {
            var line = await reader.ReadLineAsync(ct);
            return (line, line is null);
        }
        catch
        {
            return (null, true);
        }
    }

    private static (string? Token, bool Done) ParseOllamaStreamLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            string? token = null;
            bool done = false;
            if (doc.RootElement.TryGetProperty("response", out var r)) token = r.GetString();
            if (doc.RootElement.TryGetProperty("done", out var d)) done = d.GetBoolean();
            return (token, done);
        }
        catch
        {
            return (null, false);
        }
    }

    // ── Core generation (non-streaming, with retry) ─────────────────────────

    /// <summary>
    /// Posts a non-streaming generation request. Retries once on transient
    /// failures (timeout / connection refused) — these are common during
    /// model cold-load and a second attempt usually succeeds because the
    /// model loaded during the first try.
    /// </summary>
    private async Task<OllamaResult> GenerateAsync(string prompt, ModelRole role, CancellationToken ct,
        int? overrideTimeoutSeconds = null, int? overrideNumPredict = null)
    {
        var s = await LoadSettingsAsync();
        if (!s.Enabled) return OllamaResult.Empty;

        var model = ResolveModel(s, role);
        var numPredict = overrideNumPredict ?? s.NumPredict;
        var timeoutSec = overrideTimeoutSeconds ?? s.TimeoutSeconds;

        var first = await GenerateOnceAsync(prompt, s.Url, model, s.KeepAlive, numPredict, timeoutSec, ct);

        // Retry once on transient cold-start failures. The first attempt likely
        // triggered the model load — by the time we retry, it should be hot.
        if (first.RetriableKind is OllamaErrorKind.Timeout or OllamaErrorKind.ConnectionRefused
            && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("[Ollama] First attempt {Kind} — retrying once (model={Model}).", first.RetriableKind, model);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var second = await GenerateOnceAsync(prompt, s.Url, model, s.KeepAlive, numPredict, timeoutSec, ct);
            return second;
        }

        return first;
    }

    private async Task<OllamaResult> GenerateOnceAsync(string prompt, string url, string model,
        string keepAlive, int numPredict, int timeoutSeconds, CancellationToken outerCt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = LinkTimeout(outerCt, timeoutSeconds);

        try
        {
            var client = _httpClientFactory.CreateClient("Ollama");
            var body = BuildRequestJson(model, prompt, stream: false, keepAlive, numPredict);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/api/generate")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json");

            using var response = await client.SendAsync(request, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("[Ollama] HTTP {Code} from {Url} with model '{Model}'. Body: {Body}",
                    (int)response.StatusCode, url, model, errBody);
                return new OllamaResult(null,
                    $"Ollama returned HTTP {(int)response.StatusCode}. Check the server log for details.",
                    sw.Elapsed.TotalMilliseconds, OllamaErrorKind.HttpError);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var responseProp))
                return new OllamaResult(null, "Ollama returned an unexpected response format.",
                    sw.Elapsed.TotalMilliseconds, OllamaErrorKind.InvalidResponse);

            var text = responseProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                // Distinguish empty-but-successful from outright error. Surface a
                // clear message pointing admins at the configured model.
                return new OllamaResult(null,
                    $"Ollama returned an empty response (model='{model}'). The model loaded but generated no tokens — try a different model in Settings → AI.",
                    sw.Elapsed.TotalMilliseconds, OllamaErrorKind.EmptyResponse);
            }

            return new OllamaResult(text, null, sw.Elapsed.TotalMilliseconds, OllamaErrorKind.None);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            sw.Stop();
            return new OllamaResult(null,
                $"Ollama request timed out after {timeoutSeconds}s. CPU-only servers may need a longer OllamaTimeoutSeconds, or switch to a smaller model.",
                sw.Elapsed.TotalMilliseconds, OllamaErrorKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new OllamaResult(null, "Request cancelled by caller.", sw.Elapsed.TotalMilliseconds, OllamaErrorKind.None);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            sw.Stop();
            _logger.LogWarning("[Ollama] Connection refused at {Url}. Is the Ollama service running?", url);
            return new OllamaResult(null,
                $"Cannot reach Ollama at {url} — connection refused. Verify the Ollama service is running on the server.",
                sw.Elapsed.TotalMilliseconds, OllamaErrorKind.ConnectionRefused);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Ollama] Cannot reach server at {Url}.", url);
            return new OllamaResult(null, $"Cannot reach Ollama server: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, OllamaErrorKind.ConnectionRefused);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Ollama] Generation failed (url={Url}, model={Model}).", url, model);
            return new OllamaResult(null, $"Ollama generation failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, OllamaErrorKind.Unknown);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildRequestJson(string model, string prompt, bool stream, string keepAlive, int numPredict)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream,
            keep_alive = keepAlive,
            options = new { num_predict = numPredict }
        });
    }

    /// <summary>Links the caller's cancellation token with a per-call timeout.</summary>
    private static CancellationTokenSource LinkTimeout(CancellationToken outer, int timeoutSeconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, timeoutSeconds)));
        return cts;
    }

    /// <summary>
    /// Resolves which model to use for a given role. Falls back to the default
    /// model whenever a role-specific override is blank.
    /// </summary>
    private static string ResolveModel(OllamaSettings s, ModelRole role) => role switch
    {
        ModelRole.Draft  when !string.IsNullOrWhiteSpace(s.DraftModel)  => s.DraftModel,
        ModelRole.Triage when !string.IsNullOrWhiteSpace(s.TriageModel) => s.TriageModel,
        _ => s.Model
    };

    /// <summary>Walks the exception chain looking for a TCP connection-refused error.</summary>
    private static bool IsConnectionRefused(Exception ex)
    {
        for (var cur = (Exception?)ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused) return true;
        }
        return false;
    }

    private sealed record OllamaResult(string? Text, string? Error, double LatencyMs, OllamaErrorKind Kind)
    {
        public static OllamaResult Empty { get; } = new(null, null, 0, OllamaErrorKind.Disabled);

        /// <summary>Returns the error kind only when the failure is worth retrying.</summary>
        public OllamaErrorKind? RetriableKind => Kind is OllamaErrorKind.Timeout or OllamaErrorKind.ConnectionRefused
            ? Kind
            : null;
    }

    private sealed record OllamaSettings(
        bool Enabled,
        string Url,
        string Model,
        int TimeoutSeconds,
        string KeepAlive,
        bool WarmupEnabled,
        int NumPredict,
        string DraftModel,
        string TriageModel);

    private async Task<OllamaSettings> LoadSettingsAsync()
    {
        using var scope   = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var settings = await context.AppSettings
            .Where(s => s.Key == "OllamaEnabled"
                     || s.Key == "OllamaUrl"
                     || s.Key == "OllamaModel"
                     || s.Key == "OllamaTimeoutSeconds"
                     || s.Key == "OllamaKeepAlive"
                     || s.Key == "OllamaWarmupEnabled"
                     || s.Key == "OllamaNumPredict"
                     || s.Key == "OllamaDraftModel"
                     || s.Key == "OllamaTriageModel")
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);

        string Get(string k, string fallback) =>
            settings.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        bool GetBool(string k, bool fallback) =>
            settings.TryGetValue(k, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || (!settings.ContainsKey(k) && fallback);
        int GetInt(string k, int fallback) =>
            settings.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : fallback;

        return new OllamaSettings(
            Enabled:        settings.TryGetValue("OllamaEnabled", out var e) && string.Equals(e, "true", StringComparison.OrdinalIgnoreCase),
            Url:            Get("OllamaUrl",   "http://localhost:11434"),
            Model:          Get("OllamaModel", "phi"),
            TimeoutSeconds: Math.Max(15, GetInt("OllamaTimeoutSeconds", 300)),
            KeepAlive:      Get("OllamaKeepAlive", "30m"),
            WarmupEnabled:  GetBool("OllamaWarmupEnabled", true),
            NumPredict:     Math.Max(32, GetInt("OllamaNumPredict", 600)),
            DraftModel:     settings.GetValueOrDefault("OllamaDraftModel", "") ?? "",
            TriageModel:    settings.GetValueOrDefault("OllamaTriageModel", "") ?? "");
    }
}
