using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

public class EmailNotificationService
{
    private readonly ServiceDeskDbContext _context;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(ServiceDeskDbContext context, ILogger<EmailNotificationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task SendTicketNotificationAsync(Ticket ticket, string recipientEmail, string subject, string body)
    {
        var config = await _context.EmailConfigurations
            .FirstOrDefaultAsync(c => c.IsActive && !string.IsNullOrEmpty(c.Password));

        if (config == null)
        {
            _logger.LogWarning("No active email configuration found. Cannot send notification.");
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("ServiceSphere IT Support", config.EmailAddress));
            message.To.Add(MailboxAddress.Parse(recipientEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 600px;'>
                        <div style='background: #4f46e5; color: white; padding: 20px; border-radius: 8px 8px 0 0;'>
                            <h2 style='margin: 0;'>ServiceSphere Notification</h2>
                        </div>
                        <div style='padding: 20px; border: 1px solid #e5e7eb; border-top: none; border-radius: 0 0 8px 8px;'>
                            {body}
                            <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 20px 0;' />
                            <p style='color: #6b7280; font-size: 12px;'>
                                This is an automated notification from ProbuildIQ ServiceSphere.
                                Please do not reply directly to this email.
                            </p>
                        </div>
                    </div>"
            };

            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(config.SmtpServer, config.SmtpPort, config.UseSsl);
            await client.AuthenticateAsync(config.Username, config.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Notification sent to {Email}: {Subject}", recipientEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification to {Email}", recipientEmail);
        }
    }

    public async Task NotifyTicketCreated(Ticket ticket)
    {
        if (ticket.AssignedTo != null)
        {
            await SendTicketNotificationAsync(
                ticket,
                ticket.AssignedTo.Email,
                $"[ServiceSphere] New Ticket Assigned: {ticket.Title}",
                $@"<h3>New Ticket Assigned to You</h3>
                   <p><strong>Ticket:</strong> #{ticket.Id}</p>
                   <p><strong>Title:</strong> {ticket.Title}</p>
                   <p><strong>Priority:</strong> {ticket.Priority}</p>
                   <p><strong>Category:</strong> {ticket.Category}</p>
                   <p><strong>Description:</strong></p>
                   <p>{ticket.Description}</p>");
        }
    }

    public async Task NotifyTicketUpdated(Ticket ticket, string recipientEmail)
    {
        await SendTicketNotificationAsync(
            ticket,
            recipientEmail,
            $"[ServiceSphere] Ticket Updated: {ticket.Title}",
            $@"<h3>Ticket Update</h3>
               <p><strong>Ticket:</strong> #{ticket.Id}</p>
               <p><strong>Title:</strong> {ticket.Title}</p>
               <p><strong>Status:</strong> {ticket.Status}</p>
               <p><strong>Priority:</strong> {ticket.Priority}</p>
               {(ticket.ResolutionNotes != null ? $"<p><strong>Resolution:</strong> {ticket.ResolutionNotes}</p>" : "")}");
    }
}
