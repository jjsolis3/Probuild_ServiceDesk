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
