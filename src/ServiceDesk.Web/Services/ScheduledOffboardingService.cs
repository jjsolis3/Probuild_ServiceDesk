using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

public class ScheduledOffboardingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GoogleWorkspaceService _googleWorkspace;
    private readonly ILogger<ScheduledOffboardingService> _logger;

    public ScheduledOffboardingService(
        IServiceScopeFactory scopeFactory,
        GoogleWorkspaceService googleWorkspace,
        ILogger<ScheduledOffboardingService> logger)
    {
        _scopeFactory    = scopeFactory;
        _googleWorkspace = googleWorkspace;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledOffboardingService started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueOffboardingsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "ScheduledOffboardingService cycle error."); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task RunDueOffboardingsAsync()
    {
        var today = DateTime.UtcNow.Date;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var due = await db.Employees
            .Where(e => e.IsActive
                     && e.ScheduledOffboardingDate.HasValue
                     && e.ScheduledOffboardingDate!.Value.Date <= today)
            .ToListAsync();

        if (due.Count == 0) return;

        _logger.LogInformation("ScheduledOffboarding: {Count} employee(s) due.", due.Count);

        foreach (var emp in due)
        {
            _logger.LogInformation("Auto-offboarding {Email}.", emp.Email);

            await _googleWorkspace.SetSuspendedAsync(emp.Email, true);
            await _googleWorkspace.RevokeAllTokensAsync(emp.Email);

            var (grpOk, grps, _) = await _googleWorkspace.GetUserGroupsAsync(emp.Email);
            if (grpOk)
                foreach (var g in grps)
                    await _googleWorkspace.RemoveFromGroupAsync(g.Email, emp.Email);

            var ooo = new GoogleWorkspaceService.VacationResponder(
                EnableAutoReply:    true,
                ResponseSubject:    $"{emp.FullName} is no longer with the company.",
                ResponseBodyHtml:   $"<p>{emp.FullName} is no longer available. Please contact your account manager.</p>",
                StartTime:          null,
                EndTime:            null,
                RestrictToContacts: false,
                RestrictToDomain:   false);
            await _googleWorkspace.SetVacationResponderAsync(emp.Email, ooo);

            emp.IsActive                 = false;
            emp.ScheduledOffboardingDate = null;
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("ScheduledOffboarding: batch complete.");
    }
}
