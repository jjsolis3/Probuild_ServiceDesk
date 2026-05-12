using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Shared back-end for the two parallel "store product management" flows
/// (Settings hub admin pages + Store Ops Hub pages). Each controller stays a
/// thin wrapper that enforces its own auth and renders its own layout; all the
/// data + image work lives here so the two flows can't drift again.
///
/// This service is intentionally auth-agnostic and does no TempData / redirects
/// — those stay at the action layer.
/// </summary>
public sealed class StoreProductAdminService
{
    private readonly ServiceDeskDbContext _context;
    private readonly IWebHostEnvironment _env;

    public StoreProductAdminService(ServiceDeskDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env     = env;
    }

    // ── File / image helpers ─────────────────────────────────────────────────

    private const int StoreImageMaxPx = 800;

    /// <summary>
    /// Saves an uploaded image file under wwwroot/images/store with a fresh GUID
    /// name, resizing to fit within <see cref="StoreImageMaxPx"/>. When an
    /// <paramref name="existing"/> relative path is provided the old file is
    /// removed after the new one is written. Returns the new relative path, or
    /// the existing one when no file was uploaded / the extension wasn't allowed.
    /// </summary>
    public async Task<string?> SaveImageAsync(IFormFile? file, string? existing)
    {
        if (file == null || file.Length == 0) return existing;

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext)) return existing;

        var dir = Path.Combine(_env.WebRootPath, "images", "store");
        Directory.CreateDirectory(dir);

        // Always save as .jpg for resized output (except .png → keep as .png to preserve transparency)
        var saveExt = ext == ".png" ? ".png" : ".jpg";
        var fileName = $"{Guid.NewGuid()}{saveExt}";
        var path = Path.Combine(dir, fileName);

        try
        {
            using var img = await Image.LoadAsync(file.OpenReadStream());
            if (img.Width > StoreImageMaxPx || img.Height > StoreImageMaxPx)
            {
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(StoreImageMaxPx, StoreImageMaxPx),
                    Mode = ResizeMode.Max
                }));
            }

            if (saveExt == ".png")
                await img.SaveAsPngAsync(path);
            else
                await img.SaveAsJpegAsync(path, new JpegEncoder { Quality = 85 });
        }
        catch
        {
            // Fallback: save original if ImageSharp fails to read the file.
            using var stream = new FileStream(path, FileMode.Create);
            await file.CopyToAsync(stream);
        }

        if (!string.IsNullOrEmpty(existing))
            await DeleteImageFileAsync(existing);

        return $"/images/store/{fileName}";
    }

    /// <summary>
    /// Removes a stored image file from disk, but only when no other product or
    /// gallery row still references the same relative path. Defensive guard so a
    /// future migration that ever shares paths can't corrupt the catalog.
    /// </summary>
    public async Task DeleteImageFileAsync(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;

        var stillReferenced = await _context.StoreProducts
                .AnyAsync(p => p.ImagePath == relativePath)
            || await _context.StoreProductImages
                .AnyAsync(i => i.ImagePath == relativePath);
        if (stillReferenced) return;

        var full = Path.Combine(_env.WebRootPath, relativePath.TrimStart('/'));
        if (System.IO.File.Exists(full))
            System.IO.File.Delete(full);
    }

    /// <summary>
    /// Copies an existing store image file to a fresh GUID-named file so the
    /// new path can be assigned to a different product (used by Duplicate).
    /// Returns the new relative path, or null when the source is missing.
    /// </summary>
    public string? CopyImageFile(string? sourceRelativePath)
    {
        if (string.IsNullOrWhiteSpace(sourceRelativePath)) return null;
        var src = Path.Combine(_env.WebRootPath, sourceRelativePath.TrimStart('/'));
        if (!System.IO.File.Exists(src)) return null;

        var dir = Path.Combine(_env.WebRootPath, "images", "store");
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(src);
        var newFileName = $"{Guid.NewGuid()}{ext}";
        var dest = Path.Combine(dir, newFileName);
        try
        {
            System.IO.File.Copy(src, dest, overwrite: false);
        }
        catch
        {
            return null;
        }
        return $"/images/store/{newFileName}";
    }

    // ── Metadata helpers ─────────────────────────────────────────────────────

    public Task<List<string>> GetExistingCategoriesAsync()
    {
        return _context.StoreProducts
            .Where(p => p.Category != null && p.Category != "")
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    // ── Orchestration ────────────────────────────────────────────────────────

    public Task<List<StoreProduct>> ListAsync()
        => _context.StoreProducts
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();

    public Task<StoreProduct?> GetWithImagesAsync(int id)
        => _context.StoreProducts
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id);

    /// <summary>
    /// Creates a new product from the submitted DTO. Normalises Tags +
    /// CustomOptionsJson, saves the main image and gallery files, and returns
    /// the newly persisted product. Caller is responsible for ModelState
    /// validation and any auth check.
    /// </summary>
    public async Task<StoreProduct> CreateAsync(
        StoreProduct submitted,
        IFormFile? imageFile,
        IList<IFormFile>? galleryFiles,
        IList<string>? galleryTags)
    {
        if (!submitted.HasPrice) submitted.Price = null;
        submitted.Tags = NormalizeCommaList(submitted.Tags);
        submitted.CustomOptionsJson = NormalizeCustomOptionsJson(submitted.CustomOptionsJson);

        submitted.ImagePath   = await SaveImageAsync(imageFile, null);
        submitted.CreatedDate = DateTime.UtcNow;

        _context.StoreProducts.Add(submitted);
        await _context.SaveChangesAsync();

        await AddGalleryFilesAsync(submitted.Id, galleryFiles, galleryTags, startSort: 100);
        return submitted;
    }

    /// <summary>
    /// Updates an existing product's fields, main image, gallery metadata
    /// edits, and any newly uploaded gallery files. Returns false when the
    /// product can't be found; otherwise true. Caller does ModelState first.
    /// </summary>
    public async Task<bool> UpdateAsync(
        int id,
        StoreProduct submitted,
        IFormFile? imageFile,
        IList<IFormFile>? galleryFiles,
        IList<string>? galleryTags,
        IDictionary<int, string>? imageTags,
        IDictionary<int, string>? imageAlts,
        IDictionary<int, int>? imageOrders,
        bool clearImage)
    {
        var existing = await GetWithImagesAsync(id);
        if (existing == null) return false;

        existing.Name             = submitted.Name;
        existing.Description      = submitted.Description;
        existing.Category         = submitted.Category;
        existing.UnitOfMeasure    = submitted.UnitOfMeasure;
        existing.IsActive         = submitted.IsActive;
        existing.SortOrder        = submitted.SortOrder;
        existing.HasSizes         = submitted.HasSizes;
        existing.HasGenderOption  = submitted.HasGenderOption;
        existing.HasColorOptions  = submitted.HasColorOptions;
        existing.AvailableSizes   = NormalizeTrimOrNull(submitted.AvailableSizes);
        existing.AvailableColors  = NormalizeTrimOrNull(submitted.AvailableColors);
        existing.HasPrice         = submitted.HasPrice;
        existing.Price            = submitted.HasPrice ? submitted.Price : null;
        existing.MaxQtyPerOrder   = submitted.MaxQtyPerOrder;
        existing.Tags             = NormalizeCommaList(submitted.Tags);
        existing.CustomOptionsJson = NormalizeCustomOptionsJson(submitted.CustomOptionsJson);

        if (clearImage)
        {
            await DeleteImageFileAsync(existing.ImagePath);
            existing.ImagePath = null;
        }
        else
        {
            existing.ImagePath = await SaveImageAsync(imageFile, existing.ImagePath);
        }

        // Update tag / alt text / sort order on existing gallery images.
        foreach (var img in existing.Images)
        {
            if (imageTags != null && imageTags.TryGetValue(img.Id, out var tag))
                img.VariantTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
            if (imageAlts != null && imageAlts.TryGetValue(img.Id, out var alt))
                img.Alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
            if (imageOrders != null && imageOrders.TryGetValue(img.Id, out var order))
                img.SortOrder = Math.Clamp(order, 0, 9999);
        }

        var startSort = (existing.Images.Any() ? existing.Images.Max(i => i.SortOrder) : 100) + 10;
        await AddGalleryFilesAsync(existing.Id, galleryFiles, galleryTags, startSort, saveChangesAtEnd: false);

        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Hard-deletes a product when it has no order history; otherwise marks it
    /// inactive so historical orders keep their snapshot references intact.
    /// Returns a flag tuple plus the product name for the caller's TempData
    /// success message.
    /// </summary>
    public async Task<(bool found, bool deactivated, string? name)> DeleteOrDeactivateAsync(int id)
    {
        var product = await _context.StoreProducts.FindAsync(id);
        if (product == null) return (false, false, null);

        var hasOrders = await _context.StoreOrderItems.AnyAsync(i => i.StoreProductId == id);
        if (hasOrders)
        {
            product.IsActive = false;
            await _context.SaveChangesAsync();
            return (true, true, product.Name);
        }

        await DeleteImageFileAsync(product.ImagePath);
        _context.StoreProducts.Remove(product);
        await _context.SaveChangesAsync();
        return (true, false, product.Name);
    }

    /// <summary>
    /// Deletes a single gallery image row and (when no longer referenced) its
    /// underlying file. Returns the parent product id so the caller can
    /// redirect back to the edit page, or null if the image didn't exist.
    /// </summary>
    public async Task<int?> DeleteImageAsync(int imageId)
    {
        var img = await _context.StoreProductImages.FindAsync(imageId);
        if (img == null) return null;

        var productId = img.StoreProductId;
        _context.StoreProductImages.Remove(img);
        await _context.SaveChangesAsync();
        // Run the file delete AFTER the row is removed so the AnyAsync guard
        // doesn't see the deleted row as still referencing the path.
        await DeleteImageFileAsync(img.ImagePath);
        return productId;
    }

    /// <summary>
    /// Duplicates a product into a new Inactive row with its own copies of
    /// every image file so the two rows are fully independent.
    /// </summary>
    public async Task<StoreProduct?> DuplicateAsync(int id)
    {
        var src = await GetWithImagesAsync(id);
        if (src == null) return null;

        var copy = new StoreProduct
        {
            Name              = $"Copy of {src.Name}",
            Category          = src.Category,
            UnitOfMeasure     = src.UnitOfMeasure,
            Description       = src.Description,
            Tags              = src.Tags,
            MaxQtyPerOrder    = src.MaxQtyPerOrder,
            SortOrder         = src.SortOrder,
            IsActive          = false,
            HasPrice          = src.HasPrice,
            Price             = src.Price,
            HasGenderOption   = src.HasGenderOption,
            HasSizes          = src.HasSizes,
            HasColorOptions   = src.HasColorOptions,
            AvailableSizes    = src.AvailableSizes,
            AvailableColors   = src.AvailableColors,
            CustomOptionsJson = src.CustomOptionsJson,
            ImagePath         = CopyImageFile(src.ImagePath),
            CreatedDate       = DateTime.UtcNow
        };
        _context.StoreProducts.Add(copy);
        await _context.SaveChangesAsync();

        foreach (var img in src.Images.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
        {
            var newPath = CopyImageFile(img.ImagePath);
            if (string.IsNullOrEmpty(newPath)) continue;
            _context.StoreProductImages.Add(new StoreProductImage
            {
                StoreProductId = copy.Id,
                ImagePath      = newPath,
                VariantTag     = img.VariantTag,
                Alt            = img.Alt,
                SortOrder      = img.SortOrder,
                CreatedDate    = DateTime.UtcNow
            });
        }
        if (src.Images.Any()) await _context.SaveChangesAsync();
        return copy;
    }

    /// <summary>
    /// Activates or deactivates a set of products in one round-trip. Rows
    /// already in the target state are skipped so the returned count reflects
    /// only real changes.
    /// </summary>
    public async Task<int> BulkSetActiveAsync(IReadOnlyCollection<int> ids, bool isActive)
    {
        if (ids == null || ids.Count == 0) return 0;
        var products = await _context.StoreProducts
            .Where(p => ids.Contains(p.Id))
            .ToListAsync();

        int changed = 0;
        foreach (var p in products)
        {
            if (p.IsActive == isActive) continue;
            p.IsActive = isActive;
            changed++;
        }
        if (changed > 0) await _context.SaveChangesAsync();
        return changed;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds uploaded gallery files as <see cref="StoreProductImage"/> rows,
    /// each with its matching variant tag (by index). Default behaviour saves
    /// changes; the Update path defers the save so it can batch with other
    /// edits.
    /// </summary>
    private async Task AddGalleryFilesAsync(
        int productId,
        IList<IFormFile>? files,
        IList<string>? tags,
        int startSort,
        bool saveChangesAtEnd = true)
    {
        if (files == null || files.Count == 0) return;

        var sort = startSort;
        for (var i = 0; i < files.Count; i++)
        {
            var path = await SaveImageAsync(files[i], null);
            if (string.IsNullOrEmpty(path)) continue;
            var tag = tags != null && i < tags.Count ? tags[i]?.Trim() : null;
            _context.StoreProductImages.Add(new StoreProductImage
            {
                StoreProductId = productId,
                ImagePath      = path,
                VariantTag     = string.IsNullOrWhiteSpace(tag) ? null : tag,
                SortOrder      = sort,
                CreatedDate    = DateTime.UtcNow
            });
            sort += 10;
        }
        if (saveChangesAtEnd) await _context.SaveChangesAsync();
    }

    private static string? NormalizeCommaList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? null
            : string.Join(",", raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? NormalizeTrimOrNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Validates the JSON payload from the Custom Options builder. Returns null
    /// for empty or malformed input so the DB stays clean.
    /// </summary>
    public static string? NormalizeCustomOptionsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var keep = new List<object>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var label = el.TryGetProperty("label", out var l) ? l.GetString() : null;
                var values = el.TryGetProperty("values", out var v) ? v.GetString() : null;
                var required = el.TryGetProperty("required", out var r)
                            && r.ValueKind == System.Text.Json.JsonValueKind.True;
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(values)) continue;
                keep.Add(new { label = label!.Trim(), values = values!.Trim(), required });
            }
            return keep.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(keep);
        }
        catch
        {
            return null;
        }
    }
}
