using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketAttachment
{
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }

    [Required]
    [StringLength(255)]
    [Display(Name = "File Name")]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [StringLength(255)]
    public string StoredFileName { get; set; } = string.Empty;

    [StringLength(100)]
    public string ContentType { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [StringLength(200)]
    [Display(Name = "Uploaded By")]
    public string UploadedBy { get; set; } = string.Empty;

    [Display(Name = "Uploaded Date")]
    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket? Ticket { get; set; }

    public string FileSizeDisplay =>
        FileSize >= 1_048_576 ? $"{FileSize / 1_048_576.0:F1} MB" :
        FileSize >= 1_024 ? $"{FileSize / 1_024.0:F0} KB" :
        $"{FileSize} B";
}
