using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

public class EmailPollingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailPollingService> _logger;

    public EmailPollingService(IServiceProvider serviceProvider, ILogger<EmailPollingService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Polling Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllMailboxes(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in email polling loop.");
            }

            // Wait 1 minute before checking again which configs need polling
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task PollAllMailboxes(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var configs = await context.EmailConfigurations
            .Where(c => c.IsActive && !string.IsNullOrEmpty(c.Password))
            .ToListAsync(stoppingToken);

        foreach (var config in configs)
        {
            // Check if enough time has passed since last poll
            if (config.LastPolledDate.HasValue &&
                DateTime.UtcNow - config.LastPolledDate.Value < TimeSpan.FromMinutes(config.PollIntervalMinutes))
            {
                continue;
            }

            try
            {
                await PollMailbox(context, config, stoppingToken);
                config.LastPolledDate = DateTime.UtcNow;
                config.LastError = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling mailbox {Email}", config.EmailAddress);
                config.LastError = $"{DateTime.UtcNow:g}: {ex.Message}";
                config.LastPolledDate = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(stoppingToken);
        }
    }

    private async Task PollMailbox(ServiceDeskDbContext context, EmailConfiguration config, CancellationToken stoppingToken)
    {
        using var client = new ImapClient();

        await client.ConnectAsync(config.ImapServer, config.ImapPort, config.UseSsl, stoppingToken);
        await client.AuthenticateAsync(config.Username, config.Password, stoppingToken);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, stoppingToken);

        // Search for unseen messages
        var uids = await inbox.SearchAsync(SearchQuery.NotSeen, stoppingToken);

        _logger.LogInformation("Found {Count} unread emails in {Email}", uids.Count, config.EmailAddress);

        foreach (var uid in uids)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var message = await inbox.GetMessageAsync(uid, stoppingToken);

            if (config.CreateTicketsFromEmails)
            {
                // Find or use default submitter
                var senderEmail = message.From.Mailboxes.FirstOrDefault()?.Address;
                var submitter = await context.Employees
                    .FirstOrDefaultAsync(e => e.Email == senderEmail, stoppingToken);

                var ticket = new Ticket
                {
                    Title = string.IsNullOrWhiteSpace(message.Subject)
                        ? "Email Ticket (No Subject)"
                        : message.Subject.Length > 200
                            ? message.Subject.Substring(0, 200)
                            : message.Subject,
                    Description = message.TextBody ?? message.HtmlBody ?? "(No content)",
                    Category = config.DefaultTicketCategoryId.HasValue
                        ? (TicketCategory)config.DefaultTicketCategoryId.Value
                        : TicketCategory.ServiceRequest,
                    Status = TicketStatus.Open,
                    Priority = TicketPriority.Medium,
                    CreatedDate = DateTime.UtcNow,
                    SubmittedById = submitter?.Id ?? config.DefaultAssigneeId ?? 1,
                    AssignedToId = config.DefaultAssigneeId,
                };

                // Truncate description if too long
                if (ticket.Description.Length > 2000)
                {
                    ticket.Description = ticket.Description.Substring(0, 2000);
                }

                context.Tickets.Add(ticket);
                _logger.LogInformation("Created ticket from email: {Subject}", ticket.Title);
            }

            // Mark as read
            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, stoppingToken);
        }

        await context.SaveChangesAsync(stoppingToken);
        await client.DisconnectAsync(true, stoppingToken);
    }
}
