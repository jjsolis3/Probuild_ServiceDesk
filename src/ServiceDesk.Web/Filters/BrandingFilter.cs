using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Filters;

/// <summary>
/// Injects company branding (CompanyName, logo URL, etc.) from the AppSettings
/// database table into ViewBag for every view. Results are cached for 10 minutes
/// to avoid a DB hit on every request.
/// </summary>
public class BrandingFilter(ServiceDeskDbContext context, IMemoryCache cache) : IAsyncActionFilter
{
    private const string CacheKey = "ss_branding_v1";

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (ctx.Controller is Controller controller)
        {
            var branding = await cache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                var keys = new[] {
                    "CompanyName", "CompanyLogoUrl", "CompanyPhone", "CompanyWebsite", "CompanyAddress",
                    "PortalWelcomeMessage", "PortalSupportTitle",
                    "PortalAnnouncement", "PortalAnnouncementType", "PortalShowKnowledgeBase"
                };
                return await context.AppSettings
                    .Where(s => keys.Contains(s.Key))
                    .ToDictionaryAsync(s => s.Key, s => s.Value ?? string.Empty);
            });

            controller.ViewBag.CompanyName    = branding!.GetValueOrDefault("CompanyName",    "Your Company");
            controller.ViewBag.CompanyLogoUrl = branding!.GetValueOrDefault("CompanyLogoUrl", string.Empty);
            controller.ViewBag.CompanyPhone   = branding!.GetValueOrDefault("CompanyPhone",   string.Empty);
            controller.ViewBag.CompanyWebsite = branding!.GetValueOrDefault("CompanyWebsite", string.Empty);
            controller.ViewBag.CompanyAddress = branding!.GetValueOrDefault("CompanyAddress", string.Empty);

            controller.ViewBag.PortalWelcomeMessage   = branding!.GetValueOrDefault("PortalWelcomeMessage",   "Track and manage your IT support requests.");
            controller.ViewBag.PortalSupportTitle     = branding!.GetValueOrDefault("PortalSupportTitle",     "IT Support Portal");
            controller.ViewBag.PortalAnnouncement     = branding!.GetValueOrDefault("PortalAnnouncement",     string.Empty);
            controller.ViewBag.PortalAnnouncementType = branding!.GetValueOrDefault("PortalAnnouncementType", "info");
            controller.ViewBag.PortalShowKnowledgeBase = !branding!.GetValueOrDefault("PortalShowKnowledgeBase", "true")
                                                                    .Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        await next();
    }
}
