using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Central AppSettings loader for LLM configuration. Reads every provider's
/// keys in one query so any single call touches the database exactly once.
/// API keys are stored encrypted via <see cref="IDataProtectionProvider"/>
/// (same "LlmApiKeys.v1" purpose used by the Settings save handler); this
/// loader Unprotects them before returning.
///
/// Malformed / previously-plaintext values are returned as-is so admins can
/// paste a raw key into Settings and have the save handler encrypt on the
/// next round-trip without breaking the current one.
/// </summary>
public class LlmSettingsLoader
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<LlmSettingsLoader> _logger;

    public const string DataProtectorPurpose = "LlmApiKeys.v1";

    public LlmSettingsLoader(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dpProvider,
        ILogger<LlmSettingsLoader> logger)
    {
        _scopeFactory = scopeFactory;
        _protector    = dpProvider.CreateProtector(DataProtectorPurpose);
        _logger       = logger;
    }

    /// <summary>
    /// Loads the full LLM settings snapshot. Called on every generation to
    /// pick up live edits without an app restart.
    /// </summary>
    public async Task<LlmSettingsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var raw = await context.AppSettings
            .Where(s => s.Category == "AI Triage")
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty, ct);

        string Get(string k, string fallback) =>
            raw.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        bool GetBool(string k, bool fallback) =>
            raw.TryGetValue(k, out var v) && bool.TryParse(v, out var b) ? b : fallback;
        int GetInt(string k, int fallback) =>
            raw.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : fallback;

        string GetSecret(string k)
        {
            if (!raw.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return string.Empty;
            // Pre-encryption plaintext (e.g. an admin pasted a key that hasn't
            // been re-saved yet) round-trips as-is; a genuine cipher lump gets
            // decrypted. Failure returns "" and logs — the caller will treat
            // it as a missing key rather than surfacing crypto errors.
            try { return _protector.Unprotect(v); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return v;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LlmSettings] Could not decrypt key '{Key}' — treating as missing.", k);
                return string.Empty;
            }
        }

        return new LlmSettingsSnapshot(
            Provider:      Get("AiProvider",       "ollama").ToLowerInvariant(),
            ProviderDraft: Get("AiProviderDraft",  string.Empty).ToLowerInvariant(),
            ProviderTriage:Get("AiProviderTriage", string.Empty).ToLowerInvariant(),

            OllamaEnabled:        GetBool("OllamaEnabled",        false),
            OllamaUrl:            Get("OllamaUrl",            "http://localhost:11434"),
            OllamaModel:          Get("OllamaModel",          "phi"),
            OllamaTimeoutSeconds: Math.Max(15, GetInt("OllamaTimeoutSeconds", 300)),
            OllamaKeepAlive:      Get("OllamaKeepAlive",      "30m"),
            OllamaWarmupEnabled:  GetBool("OllamaWarmupEnabled",  true),
            OllamaNumPredict:     Math.Max(32, GetInt("OllamaNumPredict",     600)),
            OllamaDraftModel:     Get("OllamaDraftModel",     string.Empty),
            OllamaTriageModel:    Get("OllamaTriageModel",    string.Empty),

            GeminiEnabled:        GetBool("GeminiEnabled",  false),
            GeminiApiKey:         GetSecret("GeminiApiKey"),
            GeminiModel:          Get("GeminiModel",         "gemini-1.5-flash"),
            GeminiTimeoutSeconds: Math.Max(15, GetInt("GeminiTimeoutSeconds", 60)),
            GeminiMaxTokens:      Math.Max(32, GetInt("GeminiMaxTokens",      1024)),
            GeminiDraftModel:     Get("GeminiDraftModel",    string.Empty),
            GeminiTriageModel:    Get("GeminiTriageModel",   string.Empty),

            OpenAiEnabled:        GetBool("OpenAiEnabled", false),
            OpenAiApiKey:         GetSecret("OpenAiApiKey"),
            OpenAiModel:          Get("OpenAiModel",         "gpt-4o-mini"),
            OpenAiTimeoutSeconds: Math.Max(15, GetInt("OpenAiTimeoutSeconds", 60)),
            OpenAiMaxTokens:      Math.Max(32, GetInt("OpenAiMaxTokens",      1024)),
            OpenAiDraftModel:     Get("OpenAiDraftModel",    string.Empty),
            OpenAiTriageModel:    Get("OpenAiTriageModel",   string.Empty),

            AnthropicEnabled:        GetBool("AnthropicEnabled", false),
            AnthropicApiKey:         GetSecret("AnthropicApiKey"),
            AnthropicModel:          Get("AnthropicModel",         "claude-haiku-4-5-20251001"),
            AnthropicTimeoutSeconds: Math.Max(15, GetInt("AnthropicTimeoutSeconds", 60)),
            AnthropicMaxTokens:      Math.Max(32, GetInt("AnthropicMaxTokens",      1024)),
            AnthropicDraftModel:     Get("AnthropicDraftModel",    string.Empty),
            AnthropicTriageModel:    Get("AnthropicTriageModel",   string.Empty)
        );
    }

    /// <summary>Encrypts an API key with the LlmApiKeys.v1 purpose so the Settings save handler stores a cipher lump.</summary>
    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : _protector.Protect(plaintext);
}

/// <summary>Immutable snapshot of the LLM section of AppSettings.</summary>
public sealed record LlmSettingsSnapshot(
    string Provider,
    string ProviderDraft,
    string ProviderTriage,

    bool   OllamaEnabled,
    string OllamaUrl,
    string OllamaModel,
    int    OllamaTimeoutSeconds,
    string OllamaKeepAlive,
    bool   OllamaWarmupEnabled,
    int    OllamaNumPredict,
    string OllamaDraftModel,
    string OllamaTriageModel,

    bool   GeminiEnabled,
    string GeminiApiKey,
    string GeminiModel,
    int    GeminiTimeoutSeconds,
    int    GeminiMaxTokens,
    string GeminiDraftModel,
    string GeminiTriageModel,

    bool   OpenAiEnabled,
    string OpenAiApiKey,
    string OpenAiModel,
    int    OpenAiTimeoutSeconds,
    int    OpenAiMaxTokens,
    string OpenAiDraftModel,
    string OpenAiTriageModel,

    bool   AnthropicEnabled,
    string AnthropicApiKey,
    string AnthropicModel,
    int    AnthropicTimeoutSeconds,
    int    AnthropicMaxTokens,
    string AnthropicDraftModel,
    string AnthropicTriageModel);
