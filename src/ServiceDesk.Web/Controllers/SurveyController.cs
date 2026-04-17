using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Controllers;

/// <summary>
/// Public (no auth) controller for CSAT survey responses.
/// Accessed via a unique token included in the survey email.
/// </summary>
public class SurveyController : Controller
{
    private readonly ServiceDeskDbContext _context;

    public SurveyController(ServiceDeskDbContext context)
    {
        _context = context;
    }

    // GET /Survey/Respond/{token}
    [HttpGet]
    public async Task<IActionResult> Respond(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return NotFound();

        var survey = await _context.CsatSurveys
            .Include(s => s.Ticket)
            .FirstOrDefaultAsync(s => s.Token == token);

        if (survey == null)
            return NotFound();

        if (survey.CompletedDate.HasValue)
            return RedirectToAction("Thanks");

        return View(survey);
    }

    // POST /Survey/Respond
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Respond(string token, int score, string? feedback)
    {
        if (string.IsNullOrWhiteSpace(token))
            return NotFound();

        var survey = await _context.CsatSurveys
            .FirstOrDefaultAsync(s => s.Token == token);

        if (survey == null)
            return NotFound();

        if (survey.CompletedDate.HasValue)
            return RedirectToAction("Thanks");

        if (score < 1 || score > 5)
            ModelState.AddModelError("score", "Please select a rating between 1 and 5.");

        if (!ModelState.IsValid)
        {
            survey.Ticket = await _context.Tickets.FindAsync(survey.TicketId);
            return View(survey);
        }

        survey.Score         = score;
        survey.Feedback      = feedback?.Trim();
        survey.CompletedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return RedirectToAction("Thanks");
    }

    // GET /Survey/Thanks
    public IActionResult Thanks()
    {
        return View();
    }
}
