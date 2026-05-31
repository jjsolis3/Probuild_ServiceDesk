using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Sends a daily reminder for any Submitted receipt that hasn't been
/// approved / rejected within the configured grace period (default 3
/// days). Helps prevent the "the receipt fell through the cracks"
/// pattern when a single admin is out of office.
///
/// Opt-in via the AppSettings.PayrollReminderEnabled key; cadence
/// controlled by AppSettings.PayrollReminderDays. Throttled per receipt
/// via PayrollReceipts.LastReminderSentUtc so a single stale receipt
/// won't generate more than one ping per 24 hours.
/// </summary>
public class PayrollReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PayrollReminderService> _logger;

    public PayrollReminderService(
        IServiceScopeFactory scopeFactory,
        ILogger<PayrollReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PayrollReminderService started.");

        // Stagger first run by 5 minutes so we don't compete with app
        // startup work. Cycle hourly thereafter — the throttle column
        // keeps the actual email cadence at once-per-day per receipt.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "PayrollReminderService cycle error."); }

            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
        var email    = scope.ServiceProvider.GetRequiredService<EmailNotificationService>();
        var calendar = scope.ServiceProvider.GetRequiredService<BusinessDayCalculator>();

        var enabledRaw = (await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "PayrollReminderEnabled", ct))?.Value;
        if (!bool.TryParse(enabledRaw, out var enabled) || !enabled)
            return;

        var daysRaw = (await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "PayrollReminderDays", ct))?.Value;
        if (!int.TryParse(daysRaw, out var days) || days <= 0) days = 3;

        // Business-day cutoff: walks back from today skipping Saturdays,
        // Sundays, and rows in dbo.CompanyHolidays. A receipt is stale
        // when its SubmittedDate is on or before that cutoff date — i.e.
        // at least `days` business days have fully elapsed since submit.
        var cutoffDate = await calendar.NBusinessDaysAgoAsync(days, DateTime.UtcNow);
        var cutoffEndOfDay = cutoffDate.AddDays(1).AddTicks(-1);

        // Once-per-day throttle so a stale receipt produces a single ping
        // per cycle even though we tick hourly.
        var lastRunCutoff = DateTime.UtcNow.AddHours(-23);

        var stale = await db.PayrollReceipts
            .Include(r => r.Contractor)
            .Where(r => r.Status == "Submitted"
                     && r.SubmittedDate.HasValue
                     && r.SubmittedDate <= cutoffEndOfDay
                     && (r.LastReminderSentUtc == null || r.LastReminderSentUtc <= lastRunCutoff))
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        _logger.LogInformation("[PayrollReminder] {Count} stale Submitted receipt(s) past the {Days}-business-day cutoff ({Cutoff:yyyy-MM-dd}).",
            stale.Count, days, cutoffDate);

        foreach (var receipt in stale)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Compute the elapsed business-day count for THIS receipt
                // so the email body says "5 business days" not just the
                // configured threshold of "3 business days."
                var elapsed = receipt.SubmittedDate.HasValue
                    ? await calendar.BusinessDaysBetweenAsync(receipt.SubmittedDate.Value, DateTime.UtcNow)
                    : days;

                await email.SendStaleReceiptReminderAsync(receipt, days, elapsed);
                receipt.LastReminderSentUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PayrollReminder] Failed reminding for receipt #{Id}", receipt.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
