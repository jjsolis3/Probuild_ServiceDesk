using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Google Gemini (AI Studio) provider. Uses the public REST endpoint
/// <c>POST /v1beta/models/{model}:generateContent</c> with an API key
/// header. Cheap and fast — Gemini 1.5 Flash is a good default for
/// ServiceDesk workloads that can't tolerate the 30–90 s Ollama cold-load
/// on CPU-only servers.
///
/// Data privacy: prompts are sent to Google's servers governed by their
/// AI Studio / Vertex AI terms. Admins should confirm this is acceptable
/// before flipping the default provider from Ollama.
/// </summary>
public class GeminiProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmSettingsLoader _settings;
    private readonly ILogger<GeminiProvider> _logger;

    public string Name        => "gemini";
    public string DisplayName => "Google Gemini";

    // Public REST endpoint. Model + api key are appended per-call.
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public GeminiProvider(
        IHttpClientFactory httpClientFactory,
        LlmSettingsLoader settings,
        ILogger<GeminiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        return s.GeminiEnabled && !string.IsNullOrWhiteSpace(s.GeminiApiKey);
    }

    public async Task<LlmGenerationResult> GenerateAsync(string prompt, LlmCallOptions options, CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.GeminiEnabled) return LlmGenerationResult.Disabled();
        if (string.IsNullOrWhiteSpace(s.GeminiApiKey))
            return LlmGenerationResult.Fail(
                "Gemini API key is not configured. Add it under Settings → AI → GeminiApiKey.",
                0, LlmErrorKind.MissingApiKey);

        var model      = string.IsNullOrWhiteSpace(options.Model) ? s.GeminiModel : options.Model;
        var timeoutSec = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : s.GeminiTimeoutSeconds;
        var maxTokens  = options.MaxTokens > 0 ? options.MaxTokens : s.GeminiMaxTokens;

        return await CallGenerateAsync(prompt, model, s.GeminiApiKey, maxTokens, timeoutSec, options.SystemPrompt, options.StopSequences, ct);
    }

    /// <summary>
    /// Gemini streams via <c>:streamGenerateContent</c>; the payload is a JSON
    /// array of chunks. This implementation collects the full response then
    /// yields one large "chunk" — good enough for the SSE UI (still avoids
    /// the "hung until done" UX because Gemini's cold-response latency is
    /// only 1–3 s anyway).
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(string prompt, LlmCallOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await GenerateAsync(prompt, options, ct);
        if (!string.IsNullOrWhiteSpace(result.Text))
            yield return result.Text;
    }

    public async Task<LlmProviderTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(s.GeminiApiKey))
            return LlmProviderTestResult.Fail("Gemini API key is not configured.");

        var probe = await CallGenerateAsync(
            "Reply with the single word: ok",
            s.GeminiModel, s.GeminiApiKey, maxTokens: 4,
            timeoutSec: Math.Max(30, s.GeminiTimeoutSeconds),
            systemPrompt: null, stopSequences: null, ct: ct);

        if (probe.Kind == LlmErrorKind.None && !string.IsNullOrWhiteSpace(probe.Text))
            return new LlmProviderTestResult(true,
                $"Connected to Gemini. Model '{s.GeminiModel}' generated a probe response in {probe.LatencyMs / 1000.0:F1}s.",
                new[] { s.GeminiModel }, probe.LatencyMs);

        return LlmProviderTestResult.Fail(
            $"Gemini probe failed with model '{s.GeminiModel}': {probe.Error ?? "empty response"}.");
    }

    private async Task<LlmGenerationResult> CallGenerateAsync(
        string prompt, string model, string apiKey, int maxTokens, int timeoutSec,
        string? systemPrompt, IReadOnlyList<string>? stopSequences, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, timeoutSec)));

        try
        {
            var url = $"{BaseUrl}/{Uri.EscapeDataString(model)}:generateContent";
            var body = BuildRequestJson(prompt, systemPrompt, maxTokens, stopSequences);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            // Gemini's public REST accepts the key via header (preferred) or query.
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Headers.Add("Accept", "application/json");

            var client = _httpClientFactory.CreateClient("Llm");
            using var response = await client.SendAsync(request, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cts.Token);
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmErrorKind.Unauthorized,
                    (HttpStatusCode)429                                     => LlmErrorKind.RateLimited,
                    _                                                       => LlmErrorKind.HttpError,
                };
                _logger.LogWarning("[Gemini] HTTP {Code} for model '{Model}'. Body: {Body}",
                    (int)response.StatusCode, model, Truncate(errBody, 500));
                var msg = kind switch
                {
                    LlmErrorKind.Unauthorized => "Gemini rejected the API key (HTTP 401/403). Check GeminiApiKey in Settings → AI.",
                    LlmErrorKind.RateLimited  => "Gemini rate limit hit (HTTP 429). Wait a moment and retry, or upgrade the API tier.",
                    _                         => $"Gemini returned HTTP {(int)response.StatusCode}.",
                };
                return LlmGenerationResult.Fail(msg, sw.Elapsed.TotalMilliseconds, kind);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var text = ExtractResponseText(json);
            if (string.IsNullOrWhiteSpace(text))
                return LlmGenerationResult.Fail(
                    $"Gemini returned an empty response (model='{model}'). This can happen when safety filters block the prompt or the model has no relevant knowledge — check server logs.",
                    sw.Elapsed.TotalMilliseconds, LlmErrorKind.EmptyResponse);

            return LlmGenerationResult.Success(text, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return LlmGenerationResult.Fail(
                $"Gemini request timed out after {timeoutSec}s. Bump GeminiTimeoutSeconds or check outbound network.",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return LlmGenerationResult.Fail("Request cancelled by caller.", sw.Elapsed.TotalMilliseconds, LlmErrorKind.None);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Gemini] HTTP call failed.");
            return LlmGenerationResult.Fail($"Could not reach Gemini: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Gemini] Generation failed.");
            return LlmGenerationResult.Fail($"Gemini generation failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
    }

    /// <summary>
    /// Builds a Gemini <c>generateContent</c> request. Structure:
    /// { contents:[{parts:[{text:"..."}]}], systemInstruction:{parts:[{text:"..."}]},
    ///   generationConfig:{maxOutputTokens, stopSequences} }
    /// </summary>
    private static string BuildRequestJson(string prompt, string? systemPrompt, int maxTokens, IReadOnlyList<string>? stops)
    {
        // System.Text.Json handles the nested anonymous shape cleanly.
        object payload = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            systemInstruction = string.IsNullOrWhiteSpace(systemPrompt)
                ? null
                : new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new
            {
                maxOutputTokens = maxTokens,
                stopSequences   = stops?.Take(5).ToArray() ?? Array.Empty<string>(),
            }
        };
        return JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>
    /// Extracts the model's text from Gemini's response envelope:
    /// { candidates: [ { content: { parts: [ { text: "..." } ] } } ] }
    /// Concatenates all part texts to be safe against multi-part outputs.
    /// </summary>
    private static string ExtractResponseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var cand) || cand.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var c in cand.EnumerateArray())
            {
                if (!c.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in parts.EnumerateArray())
                    if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
            }
            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
