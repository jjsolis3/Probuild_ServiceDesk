using System.ComponentModel.DataAnnotations;
using ServiceDesk.Core.Enums;

namespace ServiceDesk.Core.Models;

public class ConsumableTransaction
{
    public int Id { get; set; }

    public int ConsumableItemId { get; set; }
    public ConsumableItem ConsumableItem { get; set; } = null!;

    [Display(Name = "Type")]
    public ConsumableTransactionType TransactionType { get; set; }

    public int Quantity { get; set; }   // positive = stock in, negative = stock out

    public int QuantityBefore { get; set; }
    public int QuantityAfter  { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    [StringLength(200)]
    public string? PerformedByEmail { get; set; }

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
}
