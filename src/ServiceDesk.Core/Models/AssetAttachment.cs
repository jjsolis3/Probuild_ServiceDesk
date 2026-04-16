using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AssetAttachment
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Required]
    [StringLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [StringLength(260)]
    public string StoredFileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    [StringLength(100)]
    public string ContentType { get; set; } = "application/octet-stream";

    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    [Required]
    [StringLength(200)]
    public string UploadedByEmail { get; set; } = string.Empty;
}
