using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

/// <summary>
/// Admin-managed company holidays. A time entry whose <c>WorkDate</c> falls
/// on a configured holiday gets pre-selected as <c>Emergency</c> rate on the
/// time-log form (the contractor can override). Also surfaces by name on
/// reports so AP can audit which entries hit the emergency rate due to a
/// holiday vs. a weekend.
/// </summary>
public class CompanyHoliday
{
    public int Id { get; set; }

    [Required]
    [DataType(DataType.Date)]
    public DateTime Date { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When true, the date repeats every year on the same month/day
    /// (e.g. New Year's Day, July 4). When false, the holiday only counts
    /// for the exact <see cref="Date"/> stored.
    /// </summary>
    [Display(Name = "Repeats yearly")]
    public bool IsRecurringYearly { get; set; } = false;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
