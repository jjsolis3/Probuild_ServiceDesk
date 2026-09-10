using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// OpenAI Chat Completions provider. Uses <c>POST /v1/chat/completions</c>
/// with <c>Authorization: Bearer</c>. Shipped disabled by default (see
/// <c>OpenAiEnabled = false</c> seed) so no accidental billing — flip it on
/// in Settings → AI when ready.
///
/// Model IDs follow OpenAI's public naming: <c>gpt-4o-mini</c> is a good
/// cheap/fast default; <c>gpt-4o</c> is smarter but pricier.
/// </summary>
public class OpenAiProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmSettingsLoader _settings;
    private readonly ILogger<OpenAiProvider> _logger;

    public string Name        => "openai";
    public string DisplayName => "OpenAI (GPT)";

    private const string BaseUrl = "https://api.openai.com/v1/chat/completions";

    public OpenAiProvider(
        IHttpClientFactory httpClientFactory,
        LlmSettingsLoader settings,
        ILogger<OpenAiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        return s.OpenAiEnabled && !string.IsNullOrWhiteSpace(s.OpenAiApiKey);
    }

    public async Task<LlmGenerationResult> GenerateAsync(string prompt, LlmCallOptions options, CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.OpenAiEnabled) return LlmGenerationResult.Disabled();
        if (string.IsNullOrWhiteSpace(s.OpenAiApiKey))
            return LlmGenerationResult.Fail(
                "OpenAI API key is not configured. Add it under Settings → AI → OpenAiApiKey.",
                0, LlmErrorKind.MissingApiKey);

        var model      = string.IsNullOrWhiteSpace(options.Model) ? s.OpenAiModel : options.Model;
        var timeoutSec = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : s.OpenAiTimeoutSeconds;
        var maxTokens  = options.MaxTokens > 0 ? options.MaxTokens : s.OpenAiMaxTokens;

        return await CallGenerateAsync(prompt, model, s.OpenAiApiKey, maxTokens, timeoutSec,
            options.SystemPrompt, options.StopSequences, ct);
    }

    /// <summary>
    /// OpenAI supports SSE streaming; kept as a single-yield here for parity
    /// with the SSE UI which just needs the final text. Wire up chunked
    /// streaming later if per-token feedback becomes important.
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
        if (string.IsNullOrWhiteSpace(s.OpenAiApiKey))
            return LlmProviderTestResult.Fail("OpenAI API key is not configured.");

        var probe = await CallGenerateAsync(
            "Reply with the single word: ok",
            s.OpenAiModel, s.OpenAiApiKey, maxTokens: 4,
            timeoutSec: Math.Max(30, s.OpenAiTimeoutSeconds),
            systemPrompt: null, stopSequences: null, ct: ct);

        if (probe.Kind == LlmErrorKind.None && !string.IsNullOrWhiteSpace(probe.Text))
            return new LlmProviderTestResult(true,
                $"Connected to OpenAI. Model '{s.OpenAiModel}' generated a probe response in {probe.LatencyMs / 1000.0:F1}s.",
                new[] { s.OpenAiModel }, probe.LatencyMs);

        return LlmProviderTestResult.Fail(
            $"OpenAI probe failed with model '{s.OpenAiModel}': {probe.Error ?? "empty response"}.");
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
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = prompt });

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages,
                max_tokens = maxTokens,
                stop = stopSequences?.Take(4).ToArray(),
                temperature = 0.5,
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
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
                _logger.LogWarning("[OpenAI] HTTP {Code} for model '{Model}'. Body: {Body}",
                    (int)response.StatusCode, model, Truncate(errBody, 2000));
                var openAiMsg = ExtractErrorMessage(errBody);
                var msg = kind switch
                {
                    LlmErrorKind.Unauthorized => "OpenAI rejected the API key (HTTP 401/403). Check OpenAiApiKey in Settings → AI." +
                                                 (openAiMsg != null ? $" ({openAiMsg})" : ""),
                    LlmErrorKind.RateLimited  => "OpenAI rate limit hit (HTTP 429). Wait a moment and retry, or upgrade the plan." +
                                                 (openAiMsg != null ? $" ({openAiMsg})" : ""),
                    _ when response.StatusCode == HttpStatusCode.NotFound =>
                        openAiMsg != null
                            ? $"OpenAI 404: {openAiMsg}"
                            : $"OpenAI returned HTTP 404 — the model '{model}' probably doesn't exist for this key. Click 'List Models'.",
                    _ => openAiMsg != null
                            ? $"OpenAI {(int)response.StatusCode}: {openAiMsg}"
                            : $"OpenAI returned HTTP {(int)response.StatusCode}.",
                };
                return LlmGenerationResult.Fail(msg, sw.Elapsed.TotalMilliseconds, kind);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var text = ExtractResponseText(json);
            if (string.IsNullOrWhiteSpace(text))
            {
                var diagnostic = DiagnoseEmpty(json)
                    ?? $"OpenAI returned an empty response (model='{model}'). Server log has the raw payload.";
                _logger.LogWarning("[OpenAI] Empty response for model '{Model}'. Raw body: {Body}",
                    model, Truncate(json, 2000));
                return LlmGenerationResult.Fail(diagnostic, sw.Elapsed.TotalMilliseconds, LlmErrorKind.EmptyResponse);
            }

            return LlmGenerationResult.Success(text, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return LlmGenerationResult.Fail(
                $"OpenAI request timed out after {timeoutSec}s.",
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
            _logger.LogError(ex, "[OpenAI] HTTP call failed.");
            return LlmGenerationResult.Fail($"Could not reach OpenAI: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[OpenAI] Generation failed.");
            return LlmGenerationResult.Fail($"OpenAI generation failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
    }

    /// <summary>Extracts <c>choices[0].message.content</c> from the OpenAI response envelope.</summary>
    private static string ExtractResponseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return string.Empty;
            var sb = new StringBuilder();
            foreach (var c in choices.EnumerateArray())
            {
                if (!c.TryGetProperty("message", out var msg)) continue;
                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    sb.Append(content.GetString());
            }
            return sb.ToString().Trim();
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// When the text extract came back empty, look for OpenAI's structured
    /// diagnostics: <c>choices[].finish_reason</c> (stop | length | content_filter |
    /// tool_calls) plus the top-level <c>error.message</c>.
    /// </summary>
    private static string? DiagnoseEmpty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                return $"OpenAI error: {m.GetString()}";
            }

            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in choices.EnumerateArray())
                {
                    if (!c.TryGetProperty("finish_reason", out var fr) || fr.ValueKind != JsonValueKind.String) continue;
                    var reason = fr.GetString();
                    if (string.IsNullOrWhiteSpace(reason) || reason == "stop") continue;
                    return reason switch
                    {
                        "length"         => "OpenAI stopped at MaxTokens before producing text — increase OpenAiMaxTokens.",
                        "content_filter" => "OpenAI content filter stopped the response.",
                        _                => $"OpenAI finished with reason '{reason}' but produced no text.",
                    };
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Pulls <c>error.message</c> from OpenAI's structured error body; returns null when absent.</summary>
    private static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <inheritdoc />
    public async Task<LlmModelsResult> ListModelsAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(s.OpenAiApiKey))
            return LlmModelsResult.Empty("OpenAI API key is not configured.");

        try
        {
            var client = _httpClientFactory.CreateClient("Llm");
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.Add("Authorization", $"Bearer {s.OpenAiApiKey}");
            req.Headers.Add("Accept", "application/json");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return LlmModelsResult.Empty($"OpenAI returned HTTP {(int)resp.StatusCode} when listing models.");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var name = id.GetString();
                        // Filter to the chat-completion-capable families — the
                        // /models endpoint also lists embedding, audio, and
                        // fine-tuning models that this app can't use.
                        if (!string.IsNullOrWhiteSpace(name)
                            && (name.StartsWith("gpt-", StringComparison.Ordinal)
                                || name.StartsWith("o1", StringComparison.Ordinal)
                                || name.StartsWith("o3", StringComparison.Ordinal)
                                || name.StartsWith("o4", StringComparison.Ordinal)
                                || name.StartsWith("chatgpt-", StringComparison.Ordinal)))
                        {
                            names.Add(name);
                        }
                    }
            }
            return names.Count == 0
                ? LlmModelsResult.Empty("OpenAI returned no chat-completion models for this API key.")
                : LlmModelsResult.Live(names.OrderBy(x => x).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OpenAI] ListModels failed.");
            return LlmModelsResult.Empty($"Could not list OpenAI models: {ex.Message}");
        }
    }
}
