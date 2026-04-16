using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Models;

namespace ServiceDesk.Web.Controllers;

[Authorize(Roles = "Admin,IT Agent")]
public class AssetsController : Controller
{
    private readonly ServiceDeskDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly IDataProtector _protector;

    private static readonly string[] AllowedAttachmentExtensions =
        [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".png", ".jpg", ".jpeg", ".gif", ".txt", ".csv", ".zip"];

    public AssetsController(ServiceDeskDbContext context, IWebHostEnvironment env, IDataProtectionProvider dpProvider)
    {
        _context = context;
        _env = env;
        _protector = dpProvider.CreateProtector("AssetCredentials.v1");
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Index(AssetType[]? types, AssetStatus[]? statuses, string[]? manufacturers, bool? assigned, string? q)
    {
        var query = _context.Assets.Include(a => a.AssignedTo).AsQueryable();

        if (types is { Length: > 0 })
            query = query.Where(a => types.Contains(a.AssetType));
        if (statuses is { Length: > 0 })
            query = query.Where(a => statuses.Contains(a.Status));
        if (manufacturers is { Length: > 0 })
            query = query.Where(a => manufacturers.Contains(a.Manufacturer));
        if (assigned.HasValue)
            query = assigned.Value
                ? query.Where(a => a.AssignedToId != null)
                : query.Where(a => a.AssignedToId == null);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a => a.Name.Contains(q) || a.AssetTag.Contains(q)
                || (a.Manufacturer != null && a.Manufacturer.Contains(q))
                || (a.Model != null && a.Model.Contains(q))
                || (a.Hostname != null && a.Hostname.Contains(q))
                || (a.IpAddress != null && a.IpAddress.Contains(q)));

        ViewBag.SelectedTypes         = types ?? Array.Empty<AssetType>();
        ViewBag.SelectedStatuses      = statuses ?? Array.Empty<AssetStatus>();
        ViewBag.SelectedManufacturers = manufacturers ?? Array.Empty<string>();
        ViewBag.CurrentAssigned       = assigned;
        ViewBag.CurrentQuery          = q;
        ViewBag.Manufacturers         = await _context.Assets
            .Where(a => a.Manufacturer != null && a.Manufacturer != "")
            .Select(a => a.Manufacturer!).Distinct().OrderBy(m => m).ToListAsync();

        var assets = await query.OrderBy(a => a.AssetTag).ToListAsync();
        return View(assets);
    }

    // ── Details (tabbed) ──────────────────────────────────────────────────────

    [Authorize(Roles = "Admin,IT Agent,Viewer")]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (asset == null) return NotFound();

        var vm = new AssetDetailViewModel
        {
            Asset = asset,
            AssignmentHistory = await _context.AssetAssignmentHistory
                .Where(h => h.AssetId == id)
                .Include(h => h.AssignedTo)
                .Include(h => h.AssignedBy)
                .OrderByDescending(h => h.AssignedDate)
                .Take(50)
                .ToListAsync(),
            AuditLog = await _context.AssetAuditLogs
                .Where(l => l.AssetId == id)
                .OrderByDescending(l => l.ChangedDate)
                .Take(100)
                .ToListAsync(),
            Credentials = await _context.AssetCredentials
                .Where(c => c.AssetId == id)
                .OrderBy(c => c.Label)
                .ToListAsync(),
            Attachments = await _context.AssetAttachments
                .Where(a => a.AssetId == id)
                .OrderByDescending(a => a.UploadedDate)
                .ToListAsync(),
            RelationshipsFrom = await _context.AssetRelationships
                .Where(r => r.SourceAssetId == id)
                .Include(r => r.TargetAsset)
                .ToListAsync(),
            RelationshipsTo = await _context.AssetRelationships
                .Where(r => r.TargetAssetId == id)
                .Include(r => r.SourceAsset)
                .ToListAsync(),
            RelatedTickets = await _context.Tickets
                .Where(t => t.AssetId == id)
                .Include(t => t.SubmittedBy)
                .OrderByDescending(t => t.CreatedDate)
                .Take(20)
                .ToListAsync(),
            AllOtherAssets = await _context.Assets
                .Where(a => a.Id != id)
                .OrderBy(a => a.AssetTag)
                .ToListAsync(),
            ActiveEmployees = await _context.Employees
                .Where(e => e.IsActive)
                .OrderBy(e => e.LastName)
                .ToListAsync(),
        };

        return View(vm);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public IActionResult Create()
    {
        PopulateDropdowns();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Asset asset)
    {
        if (ModelState.IsValid)
        {
            _context.Add(asset);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Asset {asset.AssetTag} created successfully.";
            return RedirectToAction(nameof(Details), new { id = asset.Id });
        }
        PopulateDropdowns(asset);
        return View(asset);
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets.FindAsync(id);
        if (asset == null) return NotFound();

        PopulateDropdowns(asset);
        return View(asset);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Asset asset)
    {
        if (id != asset.Id) return NotFound();

        if (ModelState.IsValid)
        {
            // Build audit log for changed fields
            var original = await _context.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (original != null)
                await WriteAuditDiff(original, asset);

            _context.Update(asset);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Asset updated successfully.";
            return RedirectToAction(nameof(Details), new { id = asset.Id });
        }
        PopulateDropdowns(asset);
        return View(asset);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var asset = await _context.Assets
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (asset == null) return NotFound();
        return View(asset);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var asset = await _context.Assets.FindAsync(id);
        if (asset != null)
        {
            _context.Assets.Remove(asset);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    // ── Assign (custody transfer) ─────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, int? employeeId, string? notes)
    {
        var asset = await _context.Assets.FindAsync(id);
        if (asset == null) return NotFound();

        // Close previous open assignment
        var openHistory = await _context.AssetAssignmentHistory
            .Where(h => h.AssetId == id && h.ReturnedDate == null)
            .ToListAsync();
        foreach (var h in openHistory)
            h.ReturnedDate = DateTime.UtcNow;

        // Create new history entry
        var currentUser = await GetCurrentEmployeeId();
        _context.AssetAssignmentHistory.Add(new AssetAssignmentHistory
        {
            AssetId      = id,
            AssignedToId = employeeId,
            AssignedById = currentUser,
            AssignedDate = DateTime.UtcNow,
            Notes        = notes,
        });

        // Audit the assignment change
        var oldEmployee = asset.AssignedToId.HasValue
            ? (await _context.Employees.FindAsync(asset.AssignedToId))?.FullName
            : "Unassigned";
        var newEmployee = employeeId.HasValue
            ? (await _context.Employees.FindAsync(employeeId))?.FullName
            : "Unassigned";

        _context.AssetAuditLogs.Add(new AssetAuditLog
        {
            AssetId       = id,
            FieldName     = "AssignedTo",
            OldValue      = oldEmployee,
            NewValue      = newEmployee,
            ChangedDate   = DateTime.UtcNow,
            ChangedByEmail = User.Identity?.Name ?? "system",
        });

        asset.AssignedToId = employeeId;
        asset.Status = employeeId.HasValue ? AssetStatus.Assigned : AssetStatus.Available;

        await _context.SaveChangesAsync();
        TempData["Success"] = $"Asset assigned to {newEmployee ?? "Unassigned"}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ── Credential Vault ──────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCredential(int id, string label, string? username,
        string password, string? url, string? credNotes)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(password))
        {
            TempData["Error"] = "Label and password are required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        _context.AssetCredentials.Add(new AssetCredential
        {
            AssetId           = id,
            Label             = label.Trim(),
            Username          = username?.Trim(),
            EncryptedPassword = _protector.Protect(password),
            Url               = url?.Trim(),
            Notes             = credNotes?.Trim(),
            CreatedDate       = DateTime.UtcNow,
            CreatedByEmail    = User.Identity?.Name ?? "system",
        });
        await _context.SaveChangesAsync();

        TempData["Success"] = "Credential added.";
        return RedirectToAction(nameof(Details), new { id, tab = "credentials" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCredential(int id, int credentialId)
    {
        var cred = await _context.AssetCredentials.FindAsync(credentialId);
        if (cred != null && cred.AssetId == id)
        {
            _context.AssetCredentials.Remove(cred);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "Credential deleted.";
        return RedirectToAction(nameof(Details), new { id, tab = "credentials" });
    }

    /// <summary>AJAX endpoint — returns decrypted password for a specific credential.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealPassword(int credentialId)
    {
        var cred = await _context.AssetCredentials.FindAsync(credentialId);
        if (cred == null) return Json(new { success = false, message = "Not found" });

        try
        {
            var plain = _protector.Unprotect(cred.EncryptedPassword);
            return Json(new { success = true, password = plain });
        }
        catch
        {
            return Json(new { success = false, message = "Decryption failed." });
        }
    }

    // ── Attachments ───────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAttachment(int id, IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please select a file to upload.";
            return RedirectToAction(nameof(Details), new { id, tab = "attachments" });
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedAttachmentExtensions.Contains(ext))
        {
            TempData["Error"] = "File type not allowed.";
            return RedirectToAction(nameof(Details), new { id, tab = "attachments" });
        }

        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "File exceeds the 20 MB limit.";
            return RedirectToAction(nameof(Details), new { id, tab = "attachments" });
        }

        var folder = Path.Combine(_env.WebRootPath, "uploads", "assets", id.ToString());
        Directory.CreateDirectory(folder);

        var stored = $"{Guid.NewGuid()}{ext}";
        var path   = Path.Combine(folder, stored);
        using (var fs = new FileStream(path, FileMode.Create))
            await file.CopyToAsync(fs);

        _context.AssetAttachments.Add(new AssetAttachment
        {
            AssetId         = id,
            FileName        = file.FileName,
            StoredFileName  = stored,
            FileSizeBytes   = file.Length,
            ContentType     = file.ContentType,
            UploadedDate    = DateTime.UtcNow,
            UploadedByEmail = User.Identity?.Name ?? "system",
        });
        await _context.SaveChangesAsync();

        TempData["Success"] = $"File '{file.FileName}' uploaded.";
        return RedirectToAction(nameof(Details), new { id, tab = "attachments" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAttachment(int id, int attachmentId)
    {
        var att = await _context.AssetAttachments.FindAsync(attachmentId);
        if (att != null && att.AssetId == id)
        {
            var path = Path.Combine(_env.WebRootPath, "uploads", "assets", id.ToString(), att.StoredFileName);
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);

            _context.AssetAttachments.Remove(att);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "Attachment deleted.";
        return RedirectToAction(nameof(Details), new { id, tab = "attachments" });
    }

    public async Task<IActionResult> DownloadAttachment(int id, int attachmentId)
    {
        var att = await _context.AssetAttachments.FindAsync(attachmentId);
        if (att == null || att.AssetId != id) return NotFound();

        var path = Path.Combine(_env.WebRootPath, "uploads", "assets", id.ToString(), att.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, att.ContentType, att.FileName);
    }

    // ── Relationships ─────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRelationship(int id, int targetAssetId, string relationshipType, string? relNotes)
    {
        if (targetAssetId == id)
        {
            TempData["Error"] = "Cannot link an asset to itself.";
            return RedirectToAction(nameof(Details), new { id, tab = "relationships" });
        }

        var exists = await _context.AssetRelationships
            .AnyAsync(r => r.SourceAssetId == id && r.TargetAssetId == targetAssetId
                        && r.RelationshipType == relationshipType);
        if (!exists)
        {
            _context.AssetRelationships.Add(new AssetRelationship
            {
                SourceAssetId    = id,
                TargetAssetId    = targetAssetId,
                RelationshipType = relationshipType,
                Notes            = relNotes?.Trim(),
                CreatedDate      = DateTime.UtcNow,
                CreatedByEmail   = User.Identity?.Name,
            });
            await _context.SaveChangesAsync();
            TempData["Success"] = "Relationship added.";
        }
        else
        {
            TempData["Error"] = "That relationship already exists.";
        }

        return RedirectToAction(nameof(Details), new { id, tab = "relationships" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRelationship(int id, int relationshipId)
    {
        var rel = await _context.AssetRelationships.FindAsync(relationshipId);
        if (rel != null && (rel.SourceAssetId == id || rel.TargetAssetId == id))
        {
            _context.AssetRelationships.Remove(rel);
            await _context.SaveChangesAsync();
        }
        TempData["Success"] = "Relationship removed.";
        return RedirectToAction(nameof(Details), new { id, tab = "relationships" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void PopulateDropdowns(Asset? asset = null)
    {
        ViewBag.Employees = new SelectList(
            _context.Employees.Where(e => e.IsActive).OrderBy(e => e.LastName)
                .Select(e => new { e.Id, Name = e.FirstName + " " + e.LastName }),
            "Id", "Name", asset?.AssignedToId);
    }

    private async Task<int?> GetCurrentEmployeeId()
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email)) return null;

        var portalUser = await _context.PortalUsers
            .FirstOrDefaultAsync(u => u.Email == email);
        return portalUser?.EmployeeId;
    }

    private async Task WriteAuditDiff(Asset original, Asset updated)
    {
        var email = User.Identity?.Name ?? "system";
        var logs  = new List<AssetAuditLog>();

        void Check(string field, string? oldVal, string? newVal)
        {
            if (oldVal != newVal)
                logs.Add(new AssetAuditLog
                {
                    AssetId       = original.Id,
                    FieldName     = field,
                    OldValue      = oldVal,
                    NewValue      = newVal,
                    ChangedDate   = DateTime.UtcNow,
                    ChangedByEmail = email,
                });
        }

        Check("Name",          original.Name,                          updated.Name);
        Check("AssetTag",      original.AssetTag,                      updated.AssetTag);
        Check("AssetType",     original.AssetType.ToString(),          updated.AssetType.ToString());
        Check("Status",        original.Status.ToString(),             updated.Status.ToString());
        Check("Manufacturer",  original.Manufacturer,                  updated.Manufacturer);
        Check("Model",         original.Model,                         updated.Model);
        Check("SerialNumber",  original.SerialNumber,                  updated.SerialNumber);
        Check("Location",      original.Location,                      updated.Location);
        Check("IpAddress",     original.IpAddress,                     updated.IpAddress);
        Check("MacAddress",    original.MacAddress,                    updated.MacAddress);
        Check("Hostname",      original.Hostname,                      updated.Hostname);
        Check("OsVersion",     original.OsVersion,                     updated.OsVersion);
        Check("OsBuild",       original.OsBuild,                       updated.OsBuild);
        Check("PurchaseCost",  original.PurchaseCost?.ToString("F2"),  updated.PurchaseCost?.ToString("F2"));
        Check("WarrantyExpiry",original.WarrantyExpiry?.ToString("yyyy-MM-dd"), updated.WarrantyExpiry?.ToString("yyyy-MM-dd"));

        if (logs.Count > 0)
        {
            _context.AssetAuditLogs.AddRange(logs);
            await _context.SaveChangesAsync();
        }
    }
}
