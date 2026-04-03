using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// A saved reply template that IT agents can insert into ticket notes with one click.
/// </summary>
public class CannedResponse
{
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    [Display(Name = "Title")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(4000)]
    [Display(Name = "Response Content")]
    public string Content { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Category")]
    public string? Category { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 0;

    [Display(Name = "Is Active")]
    public bool IsActive { get; set; } = true;
}
