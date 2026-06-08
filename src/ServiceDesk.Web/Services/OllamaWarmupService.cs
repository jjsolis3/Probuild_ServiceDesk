namespace ServiceDesk.Web.Services;

/// <summary>
/// Background service that periodically fires a tiny generation at the
/// configured Ollama model to keep it loaded in memory. Eliminates the
/// 30-second cold-load latency that would otherwise hit the first user
/// request after a quiet period.
///
/// Honours the OllamaEnabled and OllamaWarmupEnabled AppSettings — when
/// either is off, this service idles. Settings are re-read on every tick
/// so flipping the toggle takes effect within one interval.
///
/// Failure is intentionally swallowed and logged; the host process should
/// not crash because Ollama happens to be down.
/// </summary>
public class OllamaWarmupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OllamaWarmupService> _logger;

    // Ollama's default keep_alive is 5 minutes; with our default of 30m
    // we have ample headroom but 25 minutes guarantees we ping before any
    // default-configured eviction can happen.
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromMinutes(25);

    // Wait briefly on startup so the app finishes wiring up DI / migrations
    // before we hit the database to read settings.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    public OllamaWarmupService(IServiceProvider services, ILogger<OllamaWarmupService> logger)
    {
        _services = services;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var ollama = scope.ServiceProvider.GetService<OllamaService>();
                if (ollama != null)
                    await ollama.WarmupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OllamaWarmup] Tick failed — will retry next interval.");
            }

            try { await Task.Delay(WarmupInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
