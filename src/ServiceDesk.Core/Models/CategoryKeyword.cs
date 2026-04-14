using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class CategoryKeyword
{
    public int Id { get; set; }

    [Required]
    public int Category { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    public string Keyword { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
