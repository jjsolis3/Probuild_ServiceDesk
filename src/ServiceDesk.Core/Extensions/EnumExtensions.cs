using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace ServiceDesk.Core.Extensions;

public static class EnumExtensions
{
    /// <summary>
    /// Returns the [Display(Name = "...")] value for an enum member,
    /// or falls back to the member name if no attribute is set.
    /// </summary>
    public static string GetDisplayName(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        var attr  = field?.GetCustomAttribute<DisplayAttribute>();
        return attr?.Name ?? value.ToString();
    }
}
