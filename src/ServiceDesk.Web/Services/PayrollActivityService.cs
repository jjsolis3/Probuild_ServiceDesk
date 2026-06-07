using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Centralized writer for the payroll receipt activity / discussion thread.
/// Every state transition (Submit, Approve, Reject, MarkPaid, Confirm) and
/// every human comment funnels through here so the timeline rendering is
/// the single source of truth for "what happened on this receipt and
/// when." Keeping all the AuthorName / AuthorRole denormalization in one
/// place avoids drift between call sites.
/// </summary>
public class PayrollActivityService
{
    private readonly ServiceDeskDbContext _context;

    public PayrollActivityService(ServiceDeskDbContext context)
    {
        _context = context;
    }

    public Task LogSystemAsync(int receiptId, string body)
        => AddAsync(receiptId, "System", "System", null, null, body);

    public Task LogAdminAsync(int receiptId, PortalUser admin, string body)
        => AddAsync(receiptId, "Admin", $"{admin.FirstName} {admin.LastName}",
            admin.Id, null, body);

    public Task LogContractorAsync(int receiptId, Employee contractor, string body)
        => AddAsync(receiptId, "Contractor", $"{contractor.FirstName} {contractor.LastName}",
            null, contractor.Id, body);

    private async Task AddAsync(int receiptId, string role, string name,
        int? portalUserId, int? employeeId, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        _context.PayrollReceiptComments.Add(new PayrollReceiptComment
        {
            PayrollReceiptId   = receiptId,
            AuthorRole         = role,
            AuthorName         = name,
            AuthorPortalUserId = portalUserId,
            AuthorEmployeeId   = employeeId,
            Body               = body.Trim(),
            CreatedDate        = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}
