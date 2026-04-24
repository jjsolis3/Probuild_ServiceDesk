using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Filters;
using ServiceDesk.Web.Services;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

builder.Services.AddScoped<BrandingFilter>();
builder.Services.AddControllersWithViews(options =>
    options.Filters.AddService<BrandingFilter>());

// Configure Entity Framework with SQL Server
builder.Services.AddDbContext<ServiceDeskDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ServiceSphere")));

// Cookie-based authentication using the existing PortalUsers table
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "ServiceSphere.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Revoke sessions immediately when an employee is deactivated/offboarded
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async ctx =>
            {
                var db = ctx.HttpContext.RequestServices.GetRequiredService<ServiceDeskDbContext>();
                var emailClaim = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                              ?? ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
                if (emailClaim != null)
                {
                    var user = await db.PortalUsers.AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Email == emailClaim);
                    if (user == null || !user.IsActive)
                        ctx.RejectPrincipal();
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // IT staff (admin dashboard)
    options.AddPolicy("ITStaff", policy =>
        policy.RequireRole("Admin", "IT Agent"));

    // Any authenticated user (portal + IT staff)
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Register Gmail API service (singleton BackgroundService for polling + sending)
builder.Services.AddSingleton<GmailApiService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GmailApiService>());

// Register notification service (scoped, uses GmailApiService for sending)
builder.Services.AddScoped<EmailNotificationService>();

// Register Google Workspace service (singleton — stateless, uses IServiceScopeFactory for DB access)
builder.Services.AddSingleton<GoogleWorkspaceService>();

// Register assignment resolver (scoped — needs DbContext)
builder.Services.AddScoped<AssignmentResolverService>();

// Register AI triage service (singleton — holds trained model in memory)
builder.Services.AddSingleton<AiTriageService>();

// Register Ollama LLM service (singleton — stateless HTTP client wrapper)
builder.Services.AddSingleton<OllamaService>();

// Named HTTP client for Ollama with a generous timeout for LLM generation
builder.Services.AddHttpClient("Ollama", c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
});

// Register TF-IDF ticket similarity engine (singleton — builds corpus in memory)
builder.Services.AddSingleton<TicketSimilarityService>();

// Register SLA breach-risk calculator (singleton — caches resolution baselines)
builder.Services.AddSingleton<SlaRiskService>();

// Register scheduled offboarding service (checks hourly, auto-offboards employees whose date has arrived)
builder.Services.AddHostedService<ScheduledOffboardingService>();

var app = builder.Build();

// Apply incremental schema upgrades then seed reference data
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
    DbInitializer.ApplySchemaUpgrades(context);
    DbInitializer.Seed(context);

    // Apply DB-backed SLA hours to the static policy (avoids hard-coded defaults persisting)
    var slaLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        var slaSettings = context.AppSettings
            .Where(s => s.Category == "SLA")
            .ToDictionary(s => s.Key, s => s.Value ?? "");
        if (int.TryParse(slaSettings.GetValueOrDefault("SlaHoursCritical", "4"),  out var slaCrit) &&
            int.TryParse(slaSettings.GetValueOrDefault("SlaHoursHigh",     "8"),  out var slaHigh) &&
            int.TryParse(slaSettings.GetValueOrDefault("SlaHoursMedium",   "24"), out var slaMed)  &&
            int.TryParse(slaSettings.GetValueOrDefault("SlaHoursLow",      "72"), out var slaLow))
        {
            SlaPolicy.Configure(slaCrit, slaHigh, slaMed, slaLow);
        }
    }
    catch (Exception slaEx)
    {
        // On a fresh install the AppSettings table may not exist yet — acceptable. On a running
        // system this would indicate a corrupt row or DB issue; log it so it's not invisible.
        slaLogger.LogWarning(slaEx, "SLA policy could not be loaded from AppSettings; hardcoded defaults will be used.");
    }
}

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();   // Must be before UseAuthorization
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
