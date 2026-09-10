using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Services;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Filters;
using ServiceDesk.Web.Services;

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

// Register in-app portal notification service (scoped, drives the notification bell)
builder.Services.AddScoped<PortalNotificationService>();
builder.Services.AddScoped<MentionService>();

// Persistent store cart service (scoped)
builder.Services.AddScoped<StoreCartService>();

// Shared store-product admin service consumed by both SettingsController and
// StoreController (Ops Hub) so the two flows can't drift again.
builder.Services.AddScoped<StoreProductAdminService>();

// Computes per-receipt payroll totals (second rate + monthly retainer burn-down)
builder.Services.AddScoped<PayrollCalculatorService>();
builder.Services.AddScoped<PayrollReceiptPdfService>();
builder.Services.AddScoped<PayrollReceiptAttachmentService>();
builder.Services.AddScoped<PayrollActivityService>();
builder.Services.AddScoped<BusinessDayCalculator>();

// Register Google Workspace service (singleton — stateless, uses IServiceScopeFactory for DB access)
builder.Services.AddSingleton<GoogleWorkspaceService>();

// Register assignment resolver (scoped — needs DbContext)
builder.Services.AddScoped<AssignmentResolverService>();

// Register AI triage service (singleton — holds trained model in memory)
builder.Services.AddSingleton<AiTriageService>();

// LLM-based enrichment of AI Triage recommendations (sub-cat refinement,
// suggested solution, escalation signal, KB match). Scoped so it can own
// a per-call DbContext; invoked via IServiceScopeFactory from
// AiTriageService's background enrichment task.
builder.Services.AddScoped<AiTriageEnrichmentService>();

// Register Ollama LLM service (singleton — stateless HTTP client wrapper)
builder.Services.AddSingleton<OllamaService>();

// Named HTTP client for Ollama. The per-call timeout is enforced via a
// CancellationTokenSource inside OllamaService (driven by the
// OllamaTimeoutSeconds AppSetting), so this client-level timeout is just a
// safety net for misbehaving HTTP plumbing — set it high enough that the
// per-call CTS always fires first.
builder.Services.AddHttpClient("Ollama", c =>
{
    c.Timeout = TimeSpan.FromMinutes(15);
});

// Shared HttpClient for cloud LLM providers (Gemini, OpenAI, Anthropic).
// Same "outer bound only — inner CTS is authoritative" contract as Ollama.
builder.Services.AddHttpClient("Llm", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
});

// Provider-agnostic LLM plumbing. Providers are singletons so the router can
// hold them in a dict; the settings loader is singleton because it takes
// IServiceScopeFactory itself to open per-call DbContext scopes.
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.LlmSettingsLoader>();
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.ILlmProvider, ServiceDesk.Web.Services.Llm.OllamaProvider>();
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.ILlmProvider, ServiceDesk.Web.Services.Llm.GeminiProvider>();
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.ILlmProvider, ServiceDesk.Web.Services.Llm.OpenAiProvider>();
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.ILlmProvider, ServiceDesk.Web.Services.Llm.AnthropicProvider>();
builder.Services.AddSingleton<ServiceDesk.Web.Services.Llm.LlmProviderRouter>();

// Periodic warm-up so the Ollama model stays loaded in memory and the
// first user request never pays the cold-load tax (~30s on CPU).
builder.Services.AddHostedService<OllamaWarmupService>();

// Register TF-IDF ticket similarity engine (singleton — builds corpus in memory)
builder.Services.AddSingleton<TicketSimilarityService>();

// Register SLA breach-risk calculator (singleton — caches resolution baselines)
builder.Services.AddSingleton<SlaRiskService>();

// Register automation workflow engine (singleton — stateless, uses IServiceScopeFactory for DB)
builder.Services.AddSingleton<WorkflowEngineService>();

// Register scheduled offboarding service (checks hourly, auto-offboards employees whose date has arrived)
builder.Services.AddHostedService<ScheduledOffboardingService>();
builder.Services.AddHostedService<PayrollReminderService>();

var app = builder.Build();

// Apply incremental schema upgrades then seed reference data
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
    DbInitializer.ApplySchemaUpgrades(context);
    DbInitializer.Seed(context);

    // Apply DB-backed SLA hours to the static policy (avoids hard-coded defaults persisting)
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
    catch { /* table may not exist on fresh install — safe to skip */ }
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
