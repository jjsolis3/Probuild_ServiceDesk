using Microsoft.EntityFrameworkCore;
using ServiceDesk.Infrastructure.Data;
using ServiceDesk.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllersWithViews();

// Configure Entity Framework with SQL Server
builder.Services.AddDbContext<ServiceDeskDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ServiceSphere")));

// Register Gmail API service (singleton BackgroundService for polling + sending)
builder.Services.AddSingleton<GmailApiService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GmailApiService>());

// Register notification service (scoped, uses GmailApiService for sending)
builder.Services.AddScoped<EmailNotificationService>();

var app = builder.Build();

// Seed the database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
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
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
