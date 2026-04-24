using System.ComponentModel.DataAnnotations;

namespace ServiceDesk.Core.Models;

public class TicketTimeEntry
{
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    [StringLength(200)]
    [Display(Name = "Logged By")]
    public string? LoggedByEmail { get; set; }

    [Display(Name = "Logged By")]
    public int? LoggedByEmployeeId { get; set; }
    public Employee? LoggedByEmployee { get; set; }

    [Display(Name = "Work Date")]
    [DataType(DataType.Date)]
    public DateTime WorkDate { get; set; } = DateTime.UtcNow.Date;

    [Required]
    [Range(0.01, 999)]
    [Display(Name = "Hours")]
    public decimal Hours { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [Display(Name = "Billable")]
    public bool IsBillable { get; set; } = false;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
