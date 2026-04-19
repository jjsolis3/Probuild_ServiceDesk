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
/// Feature flags are read live from AppSettings on each call so admin changes
/// take effect immediately without restarting the app.
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

        return await GenerateAsync(prompt, ct);
    }

    /// <summary>
    /// Generates a professional draft reply for the ticket submitter.
    /// Returns null when Ollama is disabled or the server is unavailable.
    /// </summary>
    public async Task<string?> DraftReplyAsync(string title, string description,
        string? resolutionNotes = null, CancellationToken ct = default)
    {
        var resolutionPart = string.IsNullOrWhiteSpace(resolutionNotes)
            ? string.Empty
            : $"\n\nResolution notes provided by the IT agent:\n{resolutionNotes}";

        var prompt =
            $"You are an IT help desk agent. Write a professional, friendly reply to a " +
            $"support ticket submitter. Keep it concise (3-5 sentences). Do not use " +
            $"placeholder text like [Your Name].\n\n" +
            $"Ticket title: {title}\n\nIssue description:\n{description}" +
            resolutionPart +
            $"\n\nWrite only the reply body — no subject line, no signatures.";

        return await GenerateAsync(prompt, ct);
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
        var notes = noteContents?.ToList() ?? [];
        var noteBlock = notes.Count > 0
            ? "\n\nAgent notes (chronological):\n" + string.Join("\n---\n", notes.Select((n, i) => $"[{i + 1}] {n}"))
            : string.Empty;

        var prompt =
            $"You are an IT help desk assistant. Summarize the following support ticket " +
            $"thread in 3-5 sentences, covering the original issue, any troubleshooting " +
            $"steps taken, and the current state or resolution. Be concise and factual.\n\n" +
            $"Title: {title}\n\nOriginal description:\n{description}" +
            noteBlock;

        return await GenerateAsync(prompt, ct);
    }

    /// <summary>
    /// Tests the Ollama connection and returns a diagnostic result.
    /// Does not require Ollama to be enabled — tests the raw connection.
    /// </summary>
    public async Task<(bool Ok, string Message, string[] Models)> TestConnectionAsync(
        CancellationToken ct = default)
    {
        try
        {
            var (_, url, configuredModel) = await LoadSettingsAsync();
            var client = _httpClientFactory.CreateClient("Ollama");

            using var tagsResponse = await client.GetAsync($"{url.TrimEnd('/')}/api/tags", ct);
            if (!tagsResponse.IsSuccessStatusCode)
                return (false, $"Ollama server at {url} returned HTTP {(int)tagsResponse.StatusCode}.", []);

            var json = await tagsResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArr))
            {
                foreach (var m in modelsArr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                        names.Add(nameProp.GetString() ?? "");
                }
            }

            var modelFound = names.Any(n => n.StartsWith(configuredModel, StringComparison.OrdinalIgnoreCase));
            var msg = modelFound
                ? $"Connected to Ollama at {url}. Model '{configuredModel}' is available."
                : $"Connected to Ollama at {url}, but model '{configuredModel}' was NOT found. Available: {string.Join(", ", names.DefaultIfEmpty("(none)"))}";

            return (modelFound, msg, names.ToArray());
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Cannot reach Ollama server: {ex.Message}", []);
        }
        catch (Exception ex)
        {
            return (false, $"Connection test failed: {ex.Message}", []);
        }
    }

    // ── Core generation ──────────────────────────────────────────────────────

    private async Task<string?> GenerateAsync(string prompt, CancellationToken ct)
    {
        string url = "http://localhost:11434", model = "phi";
        try
        {
            bool enabled;
            (enabled, url, model) = await LoadSettingsAsync();
            if (!enabled) return null;

            var client = _httpClientFactory.CreateClient("Ollama");

            var body = JsonSerializer.Serialize(new
            {
                model  = model,
                prompt = prompt,
                stream = false
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/api/generate")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json");

            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Ollama] HTTP {Code} from {Url} with model '{Model}'. Body: {Body}",
                    (int)response.StatusCode, url, model, errBody);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("response", out var responseProp))
                return responseProp.GetString()?.Trim();

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Ollama] Cannot reach server at {Url}.", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ollama] Generation failed (url={Url}, model={Model}).", url, model);
            return null;
        }
    }

    private async Task<(bool Enabled, string Url, string Model)> LoadSettingsAsync()
    {
        using var scope   = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var settings = await context.AppSettings
            .Where(s => s.Key == "OllamaEnabled"
                     || s.Key == "OllamaUrl"
                     || s.Key == "OllamaModel")
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);

        var enabled = settings.TryGetValue("OllamaEnabled", out var e)
                      && string.Equals(e, "true", StringComparison.OrdinalIgnoreCase);
        var url   = settings.TryGetValue("OllamaUrl",   out var u) && !string.IsNullOrWhiteSpace(u) ? u : "http://localhost:11434";
        var model = settings.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "phi";

        return (enabled, url, model);
    }
}
