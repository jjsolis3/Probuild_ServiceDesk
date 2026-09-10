using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Local Ollama backend for <see cref="ILlmProvider"/>. Owns the HTTP call
/// against <c>POST /api/generate</c> plus the streaming JSON parser. All
/// settings are read live from <see cref="LlmSettingsLoader"/> so admin edits
/// take effect immediately.
///
/// Adds per-call CTS enforcement of <c>OllamaTimeoutSeconds</c> and a
/// single retry on transient cold-load failures — same behaviour the
/// pre-refactor OllamaService had, just relocated here.
/// </summary>
public class OllamaProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmSettingsLoader _settings;
    private readonly ILogger<OllamaProvider> _logger;

    public string Name        => "ollama";
    public string DisplayName => "Ollama (local LLM)";

    public OllamaProvider(
        IHttpClientFactory httpClientFactory,
        LlmSettingsLoader settings,
        ILogger<OllamaProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings          = settings;
        _logger            = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        return s.OllamaEnabled && !string.IsNullOrWhiteSpace(s.OllamaUrl);
    }

    public async Task<LlmGenerationResult> GenerateAsync(string prompt, LlmCallOptions options, CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.OllamaEnabled) return LlmGenerationResult.Disabled();

        var model      = string.IsNullOrWhiteSpace(options.Model) ? s.OllamaModel : options.Model;
        var timeoutSec = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : s.OllamaTimeoutSeconds;
        var maxTokens  = options.MaxTokens > 0 ? options.MaxTokens : s.OllamaNumPredict;
        var keepAlive  = string.IsNullOrWhiteSpace(options.KeepAlive) ? s.OllamaKeepAlive : options.KeepAlive;

        var first = await GenerateOnceAsync(prompt, s.OllamaUrl, model, keepAlive, maxTokens, timeoutSec, ct);

        // Retry once on transient cold-start failures — the first attempt
        // typically loaded the model into memory; the retry should succeed.
        if (first.Kind is LlmErrorKind.Timeout or LlmErrorKind.ConnectionRefused && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("[Ollama] First attempt {Kind} — retrying once (model={Model}).", first.Kind, model);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return await GenerateOnceAsync(prompt, s.OllamaUrl, model, keepAlive, maxTokens, timeoutSec, ct);
        }
        return first;
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, LlmCallOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.OllamaEnabled) yield break;

        var model      = string.IsNullOrWhiteSpace(options.Model) ? s.OllamaModel : options.Model;
        var timeoutSec = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : s.OllamaTimeoutSeconds;
        var maxTokens  = options.MaxTokens > 0 ? options.MaxTokens : s.OllamaNumPredict;
        var keepAlive  = string.IsNullOrWhiteSpace(options.KeepAlive) ? s.OllamaKeepAlive : options.KeepAlive;

        var bodyJson = BuildRequestJson(model, prompt, stream: true, keepAlive, maxTokens);

        using var cts = LinkTimeout(ct, timeoutSec);
        var client = _httpClientFactory.CreateClient("Ollama");

        HttpResponseMessage? resp = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{s.OllamaUrl.TrimEnd('/')}/api/generate")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Accept", "application/json");
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Ollama] Stream HTTP {Code} from {Url} model={Model}.", (int)resp.StatusCode, s.OllamaUrl, model);
                resp.Dispose();
                resp = null;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Ollama] Stream timed out connecting to {Url}.", s.OllamaUrl);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            _logger.LogWarning("[Ollama] Stream connection refused at {Url}. Is Ollama running?", s.OllamaUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ollama] Stream connect failed to {Url}.", s.OllamaUrl);
        }

        if (resp == null) yield break;

        using (resp)
        {
            var responseStream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using (responseStream)
            {
                using var reader = new StreamReader(responseStream);
                while (!cts.Token.IsCancellationRequested)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(cts.Token); }
                    catch { yield break; }
                    if (line == null) yield break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string? token = null;
                    bool done = false;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("response", out var r)) token = r.GetString();
                        if (doc.RootElement.TryGetProperty("done", out var d)) done = d.GetBoolean();
                    }
                    catch { /* malformed line — skip */ }

                    if (token is not null) yield return token;
                    if (done) yield break;
                }
            }
        }
    }

    public async Task<LlmProviderTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        try
        {
            var client = _httpClientFactory.CreateClient("Ollama");

            using var tagsResponse = await client.GetAsync($"{s.OllamaUrl.TrimEnd('/')}/api/tags", ct);
            if (!tagsResponse.IsSuccessStatusCode)
                return LlmProviderTestResult.Fail($"Ollama at {s.OllamaUrl} returned HTTP {(int)tagsResponse.StatusCode}.");

            var json = await tagsResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                foreach (var m in modelsArr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nameProp))
                        names.Add(nameProp.GetString() ?? "");

            var modelFound = names.Any(n => n.StartsWith(s.OllamaModel, StringComparison.OrdinalIgnoreCase));
            if (!modelFound)
            {
                return new LlmProviderTestResult(
                    false,
                    $"Connected to Ollama at {s.OllamaUrl}, but model '{s.OllamaModel}' was NOT found. Available: {string.Join(", ", names.DefaultIfEmpty("(none)"))}",
                    names.ToArray(), 0);
            }

            // Real generation probe so admins see whether the model can actually produce tokens.
            var probe = await GenerateAsync("Reply with the single word: ok",
                new LlmCallOptions { TimeoutSeconds = Math.Max(60, s.OllamaTimeoutSeconds), MaxTokens = 4 }, ct);

            if (probe.Kind == LlmErrorKind.None && !string.IsNullOrWhiteSpace(probe.Text))
                return new LlmProviderTestResult(true,
                    $"Connected to Ollama at {s.OllamaUrl}. Model '{s.OllamaModel}' generated a probe response in {probe.LatencyMs / 1000.0:F1}s.",
                    names.ToArray(), probe.LatencyMs);

            return new LlmProviderTestResult(false,
                $"Connected to Ollama at {s.OllamaUrl} and model '{s.OllamaModel}' is listed, but generation probe failed: {probe.Error ?? "empty response"}.",
                names.ToArray(), probe.LatencyMs);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            return LlmProviderTestResult.Fail($"Cannot reach Ollama at {s.OllamaUrl} — the server is not accepting connections. Verify Ollama is running.");
        }
        catch (HttpRequestException ex)
        {
            return LlmProviderTestResult.Fail($"Cannot reach Ollama server: {ex.Message}");
        }
        catch (Exception ex)
        {
            return LlmProviderTestResult.Fail($"Connection test failed: {ex.Message}");
        }
    }

    private async Task<LlmGenerationResult> GenerateOnceAsync(
        string prompt, string url, string model, string keepAlive, int maxTokens, int timeoutSeconds, CancellationToken outerCt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = LinkTimeout(outerCt, timeoutSeconds);

        try
        {
            var client = _httpClientFactory.CreateClient("Ollama");
            var body = BuildRequestJson(model, prompt, stream: false, keepAlive, maxTokens);

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
                return LlmGenerationResult.Fail(
                    $"Ollama returned HTTP {(int)response.StatusCode}. Check the server log for details.",
                    sw.Elapsed.TotalMilliseconds, LlmErrorKind.HttpError);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var responseProp))
                return LlmGenerationResult.Fail("Ollama returned an unexpected response format.",
                    sw.Elapsed.TotalMilliseconds, LlmErrorKind.InvalidResponse);

            var text = responseProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return LlmGenerationResult.Fail(
                    $"Ollama returned an empty response (model='{model}'). The model loaded but generated no tokens — try a different model in Settings → AI.",
                    sw.Elapsed.TotalMilliseconds, LlmErrorKind.EmptyResponse);

            return LlmGenerationResult.Success(text, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            sw.Stop();
            return LlmGenerationResult.Fail(
                $"Ollama request timed out after {timeoutSeconds}s. CPU-only servers may need a longer OllamaTimeoutSeconds, or switch to a smaller model.",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return LlmGenerationResult.Fail("Request cancelled by caller.", sw.Elapsed.TotalMilliseconds, LlmErrorKind.None);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            sw.Stop();
            _logger.LogWarning("[Ollama] Connection refused at {Url}. Is the Ollama service running?", url);
            return LlmGenerationResult.Fail(
                $"Cannot reach Ollama at {url} — connection refused. Verify the Ollama service is running on the server.",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.ConnectionRefused);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Ollama] Cannot reach server at {Url}.", url);
            return LlmGenerationResult.Fail($"Cannot reach Ollama server: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.ConnectionRefused);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Ollama] Generation failed (url={Url}, model={Model}).", url, model);
            return LlmGenerationResult.Fail($"Ollama generation failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds, LlmErrorKind.Unknown);
        }
    }

    private static string BuildRequestJson(string model, string prompt, bool stream, string keepAlive, int maxTokens) =>
        JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream,
            keep_alive = keepAlive,
            options = new { num_predict = maxTokens }
        });

    private static CancellationTokenSource LinkTimeout(CancellationToken outer, int timeoutSeconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, timeoutSeconds)));
        return cts;
    }

    private static bool IsConnectionRefused(Exception ex)
    {
        for (var cur = (Exception?)ex; cur != null; cur = cur.InnerException)
            if (cur is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                return true;
        return false;
    }

    /// <inheritdoc />
    public async Task<LlmModelsResult> ListModelsAsync(CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);
        if (!s.OllamaEnabled)
            return LlmModelsResult.Empty("Ollama is disabled in Settings.");

        try
        {
            var client = _httpClientFactory.CreateClient("Ollama");
            using var resp = await client.GetAsync($"{s.OllamaUrl.TrimEnd('/')}/api/tags", ct);
            if (!resp.IsSuccessStatusCode)
                return LlmModelsResult.Empty($"Ollama returned HTTP {(int)resp.StatusCode} listing models.");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        var name = n.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                    }
            }
            return names.Count == 0
                ? LlmModelsResult.Empty("Ollama has no models installed. Run `ollama pull <model>` on the server.")
                : LlmModelsResult.Live(names.OrderBy(x => x).ToArray());
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            return LlmModelsResult.Empty($"Cannot reach Ollama at {s.OllamaUrl} — connection refused.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] ListModels failed.");
            return LlmModelsResult.Empty($"Could not list Ollama models: {ex.Message}");
        }
    }
}
