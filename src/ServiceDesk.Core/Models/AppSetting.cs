using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AppSetting
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Key { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Value { get; set; }

    [Required]
    [StringLength(100)]
    public string Category { get; set; } = "General";

    [StringLength(500)]
    public string? Description { get; set; }
}
