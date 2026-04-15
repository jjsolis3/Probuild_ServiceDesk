using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Extensions;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

/// <summary>
/// Knowledge Base — browsable by any authenticated user;
/// create / edit / delete restricted to Admin and IT Agent.
/// </summary>
[Authorize]
public class KbController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public KbController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    private async Task PopulateCategoryViewBagAsync()
    {
        try
        {
            var cats = await _context.TicketCategories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync();
            ViewBag.CategorySelectList = new SelectList(cats, "Id", "Name");
            ViewBag.CategoriesById     = cats.ToDictionary(c => c.Id, c => c.Name);
        }
        catch
        {
            var cats = Enum.GetValues<TicketCategory>()
                .Select(c => new { Id = (int)c, Name = c.GetDisplayName() }).ToList();
            ViewBag.CategorySelectList = new SelectList(cats, "Id", "Name");
            ViewBag.CategoriesById     = cats.ToDictionary(c => c.Id, c => c.Name);
        }
    }

    // -------------------------------------------------------
    // GET /Kb  — search / browse (all authenticated users)
    // -------------------------------------------------------
    public async Task<IActionResult> Index(string? q, int? category)
    {
        var query = _context.KbArticles
            .Where(a => a.IsPublished)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLower();
            query = query.Where(a =>
                a.Title.ToLower().Contains(search) ||
                a.Problem.ToLower().Contains(search) ||
                a.Solution.ToLower().Contains(search));
        }

        if (category.HasValue)
            query = query.Where(a => a.Category == category.Value);

        var articles = await query
            .OrderByDescending(a => a.CreatedDate)
            .ToListAsync();

        ViewBag.SearchQuery = q;
        ViewBag.SelectedCategory = category;
        await PopulateCategoryViewBagAsync();
        return View(articles);
    }

    // -------------------------------------------------------
    // GET /Kb/Details/5  (all authenticated users)
    // -------------------------------------------------------
    public async Task<IActionResult> Details(int id)
    {
        var article = await _context.KbArticles
            .Include(a => a.SourceTicket)
            .FirstOrDefaultAsync(a => a.Id == id && a.IsPublished);

        if (article == null) return NotFound();

        // Increment view count
        article.ViewCount++;
        await _context.SaveChangesAsync();

        return View(article);
    }

    // -------------------------------------------------------
    // GET /Kb/All  — all articles incl. unpublished (IT only)
    // -------------------------------------------------------
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> All(string? q)
    {
        var query = _context.KbArticles.AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLower();
            query = query.Where(a =>
                a.Title.ToLower().Contains(search) ||
                a.Problem.ToLower().Contains(search));
        }

        var articles = await query
            .OrderByDescending(a => a.CreatedDate)
            .ToListAsync();

        ViewBag.SearchQuery = q;
        return View(articles);
    }

    // -------------------------------------------------------
    // GET /Kb/Create  (IT only)
    // -------------------------------------------------------
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> Create(int? ticketId)
    {
        var model = new KbArticle { IsPublished = true };

        if (ticketId.HasValue)
        {
            var ticket = _context.Tickets
                .Include(t => t.SubmittedBy)
                .FirstOrDefault(t => t.Id == ticketId.Value);
            if (ticket != null)
            {
                model.Title = ticket.Title;
                model.Problem = ticket.Description;
                model.Solution = ticket.ResolutionNotes ?? string.Empty;
                model.Category = ticket.Category;
                model.SourceTicketId = ticket.Id;
            }
        }

        await PopulateCategoryViewBagAsync();
        return View(model);
    }

    // -------------------------------------------------------
    // POST /Kb/Create  (IT only)
    // -------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> Create(KbArticle model)
    {
        if (ModelState.IsValid)
        {
            model.CreatedBy = User.Identity?.Name ?? "Unknown";
            model.CreatedDate = DateTime.UtcNow;
            _context.KbArticles.Add(model);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Knowledge Base article \"{model.Title}\" created successfully.";
            return RedirectToAction("Details", new { id = model.Id });
        }
        await PopulateCategoryViewBagAsync();
        return View(model);
    }

    // -------------------------------------------------------
    // GET /Kb/Edit/5  (IT only)
    // -------------------------------------------------------
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> Edit(int id)
    {
        var article = await _context.KbArticles.FindAsync(id);
        if (article == null) return NotFound();
        await PopulateCategoryViewBagAsync();
        return View(article);
    }

    // -------------------------------------------------------
    // POST /Kb/Edit/5  (IT only)
    // -------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> Edit(int id, KbArticle model)
    {
        if (id != model.Id) return BadRequest();

        if (ModelState.IsValid)
        {
            var existing = await _context.KbArticles.FindAsync(id);
            if (existing == null) return NotFound();

            existing.Title = model.Title;
            existing.Problem = model.Problem;
            existing.Solution = model.Solution;
            existing.Category = model.Category;
            existing.IsPublished = model.IsPublished;
            existing.LastUpdated = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Article updated.";
            return RedirectToAction("Details", new { id });
        }
        await PopulateCategoryViewBagAsync();
        return View(model);
    }

    // -------------------------------------------------------
    // POST /Kb/Delete/5  (Admin only)
    // -------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var article = await _context.KbArticles.FindAsync(id);
        if (article != null)
        {
            _context.KbArticles.Remove(article);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Article deleted.";
        }
        return RedirectToAction("All");
    }

    // -------------------------------------------------------
    // POST /Kb/TogglePublish/5  (IT only)
    // -------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,IT Agent")]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var article = await _context.KbArticles.FindAsync(id);
        if (article != null)
        {
            article.IsPublished = !article.IsPublished;
            article.LastUpdated = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            TempData["Success"] = article.IsPublished ? "Article published." : "Article unpublished.";
        }
        return RedirectToAction("All");
    }
}
