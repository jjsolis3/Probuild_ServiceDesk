using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Audit log for AI model training runs and triage prediction batches.
/// </summary>
public class AiRunLog
{
    public int Id { get; set; }

    public DateTime RunDate { get; set; } = DateTime.UtcNow;

    /// <summary>Training, Prediction, or Retrain.</summary>
    [Required]
    [StringLength(20)]
    public string RunType { get; set; } = "Training";

    /// <summary>Number of resolved tickets used as training data (for Training runs).</summary>
    public int TrainingTicketCount { get; set; }

    /// <summary>Short version label, e.g. "v1" or "SDCA-2024-01-15".</summary>
    [StringLength(50)]
    public string ModelVersion { get; set; } = string.Empty;

    public bool Success { get; set; }

    [StringLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>Wall-clock time in milliseconds for the run.</summary>
    public double DurationMs { get; set; }
}
