using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketState
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(7)]
    [Display(Name = "Color")]
    public string ColorHex { get; set; } = "#6c757d";

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    [Display(Name = "SLA Enabled")]
    public bool IsSlaEnabled { get; set; } = false;

    [Display(Name = "Default State")]
    public bool IsDefault { get; set; } = false;

    [Display(Name = "System State")]
    public bool IsSystem { get; set; } = false;

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;
}
