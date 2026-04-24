using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class AssetCheckout
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    [Display(Name = "Checked Out To")]
    public int? CheckedOutToId { get; set; }
    public Employee? CheckedOutTo { get; set; }

    [StringLength(200)]
    [Display(Name = "Processed By")]
    public string? CheckedOutByEmail { get; set; }

    [Display(Name = "Checkout Date")]
    public DateTime CheckoutDate { get; set; } = DateTime.UtcNow;

    [Required]
    [Display(Name = "Due Date")]
    [DataType(DataType.Date)]
    public DateTime DueDate { get; set; }

    [Display(Name = "Returned Date")]
    [DataType(DataType.Date)]
    public DateTime? ReturnedDate { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public bool IsActive    => ReturnedDate == null;
    public bool IsOverdue   => ReturnedDate == null && DueDate.Date < DateTime.UtcNow.Date;
}
