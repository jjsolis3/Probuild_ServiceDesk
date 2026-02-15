using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class CompanyService
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Status")]
    public ServiceStatus Status { get; set; } = ServiceStatus.Active;

    [StringLength(200)]
    [Display(Name = "Service Owner")]
    public string? ServiceOwner { get; set; }

    [StringLength(500)]
    [Display(Name = "Support Contact")]
    public string? SupportContact { get; set; }

    [StringLength(500)]
    [Display(Name = "Documentation URL")]
    [DataType(DataType.Url)]
    public string? DocumentationUrl { get; set; }

    [Display(Name = "SLA (Hours)")]
    public int? SlaHours { get; set; }

    // Navigation properties
    public ICollection<Ticket> RelatedTickets { get; set; } = new List<Ticket>();
}
