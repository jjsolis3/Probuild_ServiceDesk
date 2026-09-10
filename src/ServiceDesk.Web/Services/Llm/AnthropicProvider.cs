using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Anthropic Messages API provider (Claude family). Uses
/// <c>POST https://api.anthropic.com/v1/messages</c> with the
/// <c>x-api-key</c> and <c>anthropic-version</c> headers. Shipped disabled
/// by default; flip <c>AnthropicEnabled = true</c> under Settings → AI to
/// enable.
///
/// Default model ships as <c>claude-haiku-4-5-20251001</c> (fast, cheap);
/// use <c>claude-sonnet-5</c> or <c>claude-opus-5</c> for smarter draft-
/// reply / KB-article generation.
/// </summary>
public class AnthropicProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmSettingsLoader _settings;
    private readonly ILogger<AnthropicProvider> _logger;

    public string Name        => "anthropic";
    public string DisplayName => "Anthropic Claude";

    private const string BaseUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    public AnthropicProvider(
        IHttpClientFactory httpClientFactory,
        LlmSettingsLoader settings,
        ILogger<AnthropicProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        return s.AnthropicEnabled && !string.IsNullOrWhiteSpace(s.AnthropicApiKey);
    }

    public async Task<LlmGenerationResult> GenerateAsync(string prompt, LlmCallOptions options, CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.AnthropicEnabled) return LlmGenerationResult.Disabled();
        if (string.IsNullOrWhiteSpace(s.AnthropicApiKey))
            return LlmGenerationResult.Fail(
                "Anthropic API key is not configured. Add it under Settings → AI → AnthropicApiKey.",
                0, LlmErrorKind.MissingApiKey);

        var model      = string.IsNullOrWhiteSpace(options.Model) ? s.AnthropicModel : options.Model;
        var timeoutSec = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : s.AnthropicTimeoutSeconds;
        var maxTokens  = options.MaxTokens > 0 ? options.MaxTokens : s.AnthropicMaxTokens;

        return await CallGenerateAsync(prompt, model, s.AnthropicApiKey, maxTokens, timeoutSec,
            options.SystemPrompt, options.StopSequences, ct);
    }

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
        if (string.IsNullOrWhiteSpace(s.AnthropicApiKey))
            return LlmProviderTestResult.Fail("Anthropic API key is not configured.");

        var probe = await CallGenerateAsync(
            "Reply with the single word: ok",
            s.AnthropicModel, s.AnthropicApiKey, maxTokens: 4,
            timeoutSec: Math.Max(30, s.AnthropicTimeoutSeconds),
            systemPrompt: null, stopSequences: null, ct: ct);

        if (probe.Kind == LlmErrorKind.None && !string.IsNullOrWhiteSpace(probe.Text))
            return new LlmProviderTestResult(true,
                $"Connected to Anthropic. Model '{s.AnthropicModel}' generated a probe response in {probe.LatencyMs / 1000.0:F1}s.",
                new[] { s.AnthropicModel }, probe.LatencyMs);

        return LlmProviderTestResult.Fail(
            $"Anthropic probe failed with model '{s.AnthropicModel}': {probe.Error ?? "empty response"}.");
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
            var body = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = maxTokens,
                system     = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                stop_sequences = stopSequences?.Take(4).ToArray(),
                messages   = new[] { new { role = "user", content = prompt } },
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
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
                _logger.LogWarning("[Anthropic] HTTP {Code} for model '{Model}'. Body: {Body}",
                    (int)response.StatusCode, model, Truncate(errBody, 500));
                var msg = kind switch
                {
                    LlmErrorKind.Unauthorized => "Anthropic rejected the API key (HTTP 401/403). Check AnthropicApiKey in Settings → AI.",
                    LlmErrorKind.RateLimited  => "Anthropic rate limit hit (HTTP 429). Wait a moment and retry, or upgrade the tier.",
                    _                         => $"Anthropic returned HTTP {(int)response.StatusCode}.",
                };
                return LlmGenerationResult.Fail(msg, sw.Elapsed.TotalMilliseconds, kind);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var text = ExtractResponseText(json);
            if (string.IsNullOrWhiteSpace(text))
                return LlmGenerationResult.Fail(
                    $"Anthropic returned an empty response (model='{model}'). Check server logs for the raw response.",
                    sw.Elapsed.TotalMilliseconds, LlmErrorKind.EmptyResponse);

            return LlmGenerationResult.Success(text, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return LlmGenerationResult.Fail(
                $"Anthropic request timed out after {timeoutSec}s.",
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
            _logger.LogError(ex, "[Anthropic] HTTP call failed.");
            return LlmGenerationResult.Fail($"Could not reach Anthropic: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Anthropic] Generation failed.");
            return LlmGenerationResult.Fail($"Anthropic generation failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
    }

    /// <summary>
    /// Anthropic response envelope: <c>{ content: [ { type: "text", text: "..." } ] }</c>.
    /// Skips any non-text blocks (tool_use etc.) to be forward-compatible.
    /// </summary>
    private static string ExtractResponseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return string.Empty;
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t)
                    && t.GetString() == "text"
                    && block.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
            }
            return sb.ToString().Trim();
        }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
