namespace ServiceDesk.Web.Services.Llm;

/// <summary>
/// Provider-neutral abstraction over an LLM backend. Every AI feature in the
/// app funnels its actual generation calls through this so admins can swap
/// providers (Ollama, Gemini, OpenAI, Anthropic Claude) from Settings → AI
/// without any caller-side changes.
///
/// Implementations must be thread-safe (they're typically registered as
/// singletons and shared across concurrent HTTP requests), and must handle
/// their own auth, retries, and error classification. A failure returns an
/// <see cref="LlmGenerationResult"/> with a categorised <see cref="LlmErrorKind"/>
/// rather than throwing so the router can pick the best user-facing message.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Lowercase provider key, e.g. "ollama", "gemini", "openai", "anthropic".</summary>
    string Name { get; }

    /// <summary>Human-facing display name used in the Settings UI and error messages.</summary>
    string DisplayName { get; }

    /// <summary>True when this provider's minimum settings (URL, API key, etc.) are configured.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>
    /// Single-shot completion. Returns the full text when the model finishes,
    /// or an error with <see cref="LlmErrorKind"/> when it doesn't. Never throws
    /// on expected failure classes (timeout, refused, 401, 429, etc.).
    /// </summary>
    Task<LlmGenerationResult> GenerateAsync(string prompt, LlmCallOptions options, CancellationToken ct = default);

    /// <summary>
    /// Streams tokens as the model produces them. Yields nothing and returns
    /// silently on failure — callers that need error detail should use
    /// <see cref="GenerateAsync"/> instead.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(string prompt, LlmCallOptions options, CancellationToken ct = default);

    /// <summary>
    /// Full connection probe — runs an actual tiny generation call so admins
    /// see whether the model can produce tokens, not just whether the HTTP
    /// endpoint is reachable.
    /// </summary>
    Task<LlmProviderTestResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the list of model IDs this provider can currently serve for
    /// the configured API key. Powers the "List Available Models" picker in
    /// Settings → AI so admins never have to guess a valid model name.
    ///
    /// Providers whose API doesn't expose a discovery endpoint (e.g.
    /// Anthropic) return a hardcoded curated list. Providers that require
    /// configuration (missing API key, disabled) return an empty list with
    /// an explanatory Error.
    /// </summary>
    Task<LlmModelsResult> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>Reason a generation call failed. Drives the user-facing message in the UI.</summary>
public enum LlmErrorKind
{
    None,
    /// <summary>Ollama or the provider is turned off in Settings.</summary>
    Disabled,
    /// <summary>TCP-level refused (Ollama not running) — cannot reach endpoint.</summary>
    ConnectionRefused,
    /// <summary>HTTP call exceeded the configured per-call timeout.</summary>
    Timeout,
    /// <summary>Cloud provider returned an HTTP error status (5xx or otherwise unrecognised).</summary>
    HttpError,
    /// <summary>Model returned an empty / whitespace response.</summary>
    EmptyResponse,
    /// <summary>Response could not be parsed as the expected JSON shape.</summary>
    InvalidResponse,
    /// <summary>Configuration is missing an API key or URL required to make the call.</summary>
    MissingApiKey,
    /// <summary>Cloud provider returned HTTP 401/403 — bad or revoked key.</summary>
    Unauthorized,
    /// <summary>Cloud provider returned HTTP 429 — rate-limited or quota exhausted.</summary>
    RateLimited,
    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>Options threaded through a single generation call.</summary>
public sealed record LlmCallOptions
{
    /// <summary>Provider-specific model name. Null falls back to the provider's configured default.</summary>
    public string? Model { get; init; }

    /// <summary>Hard timeout on the HTTP call. Defaults to a per-provider value if 0.</summary>
    public int TimeoutSeconds { get; init; } = 0;

    /// <summary>Maximum tokens the model may generate. 0 = provider default.</summary>
    public int MaxTokens { get; init; } = 0;

    /// <summary>Ollama-only: how long to keep the model resident (e.g. "30m"). Cloud providers ignore this.</summary>
    public string? KeepAlive { get; init; }

    /// <summary>Optional short system instruction (Gemini/Anthropic support natively; Ollama/OpenAI get it prefixed).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Optional stopping strings — providers may or may not honour this.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
}

/// <summary>Result of a single generation call.</summary>
public sealed record LlmGenerationResult(string? Text, string? Error, double LatencyMs, LlmErrorKind Kind)
{
    public static LlmGenerationResult Disabled() => new(null, null, 0, LlmErrorKind.Disabled);
    public static LlmGenerationResult Success(string text, double latencyMs) => new(text, null, latencyMs, LlmErrorKind.None);
    public static LlmGenerationResult Fail(string error, double latencyMs, LlmErrorKind kind) => new(null, error, latencyMs, kind);
}

/// <summary>Result of a "Test Connection" probe from the Settings page.</summary>
public sealed record LlmProviderTestResult(bool Ok, string Message, string[] Models, double LatencyMs)
{
    public static LlmProviderTestResult Fail(string message) => new(false, message, Array.Empty<string>(), 0);
    public static LlmProviderTestResult SuccessNoProbe(string message, string[] models) => new(true, message, models, 0);
}

/// <summary>
/// Result of a "List Available Models" call. When <see cref="Ok"/> is false,
/// <see cref="Error"/> explains why (missing API key, HTTP failure, etc.);
/// <see cref="Curated"/> is true when the list came from a hardcoded static
/// set rather than a live API discovery call.
/// </summary>
public sealed record LlmModelsResult(bool Ok, string[] Models, string? Error, bool Curated)
{
    public static LlmModelsResult Empty(string error) => new(false, Array.Empty<string>(), error, false);
    public static LlmModelsResult Live(string[] models) => new(true, models, null, false);
    public static LlmModelsResult CuratedList(string[] models) => new(true, models, null, true);
}
