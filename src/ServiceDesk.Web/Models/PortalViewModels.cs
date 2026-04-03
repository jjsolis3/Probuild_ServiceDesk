using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models;

public class PortalDashboardViewModel
{
    public PortalUser CurrentUser { get; set; } = null!;
    public List<Ticket> MyTickets { get; set; } = new();
    public int OpenCount { get; set; }
    public int InProgressCount { get; set; }
    public int ResolvedCount { get; set; }
}

public class PortalSubmitTicketViewModel
{
    [Required]
    [StringLength(200)]
    [Display(Name = "Subject")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    [Display(Name = "Describe your issue")]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Category")]
    public TicketCategory Category { get; set; } = TicketCategory.ServiceRequest;

    [Required]
    [Display(Name = "Priority")]
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    [Display(Name = "Related Service")]
    public int? CompanyServiceId { get; set; }
}

public class PortalTicketDetailViewModel
{
    public Ticket Ticket { get; set; } = null!;
    public PortalUser CurrentUser { get; set; } = null!;
    public string NewComment { get; set; } = string.Empty;
}
