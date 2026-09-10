namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Picks the right <see cref="ILlmProvider"/> for a given feature role. Per-
/// feature routing lets admins point Draft Reply / KB Article generation at a
/// smarter model (e.g. Gemini 1.5 Pro or Claude Sonnet) while keeping the
/// high-volume Triage classifier on a cheap/fast one (Gemini Flash, Ollama).
///
/// Fallback chain when the requested provider isn't configured:
///   role-specific → default (AiProvider) → Ollama (always registered)
///
/// The router itself is a singleton — providers are singletons too — so the
/// only per-call cost is a settings read (already cached inside the loader
/// via EF Core's built-in query caching).
/// </summary>
public class LlmProviderRouter
{
    public enum FeatureRole { Default, Draft, Triage }

    private readonly LlmSettingsLoader _settings;
    private readonly IReadOnlyDictionary<string, ILlmProvider> _providersByName;
    private readonly ILogger<LlmProviderRouter> _logger;

    public LlmProviderRouter(
        LlmSettingsLoader settings,
        IEnumerable<ILlmProvider> providers,
        ILogger<LlmProviderRouter> logger)
    {
        _settings = settings;
        _providersByName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>All registered providers, keyed by lowercase name.</summary>
    public IReadOnlyDictionary<string, ILlmProvider> AllProviders => _providersByName;

    /// <summary>
    /// Resolves the provider + per-role model override for a call. Returns
    /// <c>(provider, modelOverride)</c> where <c>modelOverride</c> is empty
    /// when the caller should use the provider's default model.
    /// </summary>
    public async Task<(ILlmProvider Provider, string ModelOverride)> ResolveAsync(FeatureRole role, CancellationToken ct = default)
    {
        var s = await _settings.LoadAsync(ct);

        var roleProviderName = role switch
        {
            FeatureRole.Draft  => s.ProviderDraft,
            FeatureRole.Triage => s.ProviderTriage,
            _                  => string.Empty,
        };

        // Prefer role-specific, fall back to global, fall back to ollama.
        var name = FirstNonBlank(roleProviderName, s.Provider, "ollama");
        if (!_providersByName.TryGetValue(name, out var provider))
        {
            _logger.LogWarning("[LlmRouter] Provider '{Name}' not registered — falling back to ollama.", name);
            provider = _providersByName["ollama"];
            name = "ollama";
        }

        // Per-role model overrides are provider-specific — Ollama has
        // OllamaDraftModel/OllamaTriageModel, Gemini has GeminiDraftModel, etc.
        var modelOverride = (name, role) switch
        {
            ("ollama",    FeatureRole.Draft)  => s.OllamaDraftModel,
            ("ollama",    FeatureRole.Triage) => s.OllamaTriageModel,
            ("gemini",    FeatureRole.Draft)  => s.GeminiDraftModel,
            ("gemini",    FeatureRole.Triage) => s.GeminiTriageModel,
            ("openai",    FeatureRole.Draft)  => s.OpenAiDraftModel,
            ("openai",    FeatureRole.Triage) => s.OpenAiTriageModel,
            ("anthropic", FeatureRole.Draft)  => s.AnthropicDraftModel,
            ("anthropic", FeatureRole.Triage) => s.AnthropicTriageModel,
            _                                  => string.Empty,
        };

        return (provider, modelOverride);
    }

    /// <summary>
    /// One-shot generation for the resolved provider. Keeps a per-role
    /// <see cref="LlmCallOptions"/> so callers don't have to know which
    /// provider was picked.
    /// </summary>
    public async Task<LlmGenerationResult> GenerateAsync(string prompt, FeatureRole role, CancellationToken ct = default)
    {
        var (provider, modelOverride) = await ResolveAsync(role, ct);
        var opts = new LlmCallOptions { Model = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride };
        return await provider.GenerateAsync(prompt, opts, ct);
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, FeatureRole role,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (provider, modelOverride) = await ResolveAsync(role, ct);
        var opts = new LlmCallOptions { Model = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride };
        await foreach (var t in provider.StreamAsync(prompt, opts, ct))
            yield return t;
    }

    /// <summary>Runs the Test Connection probe against the currently-selected default provider.</summary>
    public async Task<(string ProviderName, LlmProviderTestResult Result)> TestActiveProviderAsync(CancellationToken ct = default)
    {
        var (provider, _) = await ResolveAsync(FeatureRole.Default, ct);
        return (provider.DisplayName, await provider.TestConnectionAsync(ct));
    }

    /// <summary>
    /// Lists available models for one specific provider (by its lowercase
    /// <see cref="ILlmProvider.Name"/>). Used by the Settings → AI "List
    /// Models" button so admins can pick from what their key actually
    /// serves rather than guessing.
    /// </summary>
    public async Task<LlmModelsResult> ListModelsAsync(string providerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || !_providersByName.TryGetValue(providerName, out var provider))
        {
            return LlmModelsResult.Empty($"Unknown provider '{providerName}'.");
        }
        return await provider.ListModelsAsync(ct);
    }

    private static string FirstNonBlank(params string[] xs)
    {
        foreach (var x in xs)
            if (!string.IsNullOrWhiteSpace(x)) return x.Trim().ToLowerInvariant();
        return "ollama";
    }
}
