using ServiceDesk.Core.Models;

namespace ServiceDesk.Web.Models.ViewModels;

/// <summary>
/// Parameter object passed to the shared store-product partials so the same
/// markup can render under either the Settings hub (admin) or the Store Ops
/// Hub (operations) — each provides its own controller name + action names so
/// the partials can build correct URLs without hard-coding either flow.
/// </summary>
public sealed class StoreProductFormContext
{
    /// <summary>Product being rendered. List partial uses this for selection state only.</summary>
    public StoreProduct? Product { get; init; }

    /// <summary>All products, ordered for display. Required by the list partial; null on form partials.</summary>
    public IReadOnlyList<StoreProduct>? Products { get; init; }

    /// <summary>Distinct category strings already present, for the create/edit datalist.</summary>
    public IReadOnlyList<string> ExistingCategories { get; init; } = Array.Empty<string>();

    /// <summary>"Settings" or "Store" — used as <c>asp-controller</c>.</summary>
    public required string ControllerName { get; init; }

    public required string ListAction { get; init; }
    public required string CreateAction { get; init; }
    public required string EditAction { get; init; }
    public required string DeleteAction { get; init; }
    public required string ImageDeleteAction { get; init; }
    public required string DuplicateAction { get; init; }
    public required string BulkSetActiveAction { get; init; }
}
