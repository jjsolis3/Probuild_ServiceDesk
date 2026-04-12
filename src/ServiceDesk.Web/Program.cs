using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
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

var app = builder.Build();

// Apply incremental schema upgrades then seed reference data
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
    DbInitializer.ApplySchemaUpgrades(context);
    DbInitializer.Seed(context);
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
