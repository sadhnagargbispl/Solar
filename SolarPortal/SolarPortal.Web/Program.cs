using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Serilog;
using SolarPortal.Infrastructure;
using SolarPortal.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File("Logs/solar-portal-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// MVC with global auth policy
var mvcBuilder = builder.Services.AddControllersWithViews(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});

// Razor runtime compilation in Development for hot-reload of .cshtml
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}

// Infrastructure (DB, Identity, Services)
builder.Services.AddInfrastructure(builder.Configuration);

// HttpContextAccessor — needed by FileUploadService to build absolute URLs
// (scheme + host) when saving image/document paths to DB.
builder.Services.AddHttpContextAccessor();

// Session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".SolarPortal.User.Session";
});

// Distinct auth cookie so admin/user/inc sites don't collide on localhost
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SolarPortal.User.Auth";
    options.LoginPath  = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Data protection key ring. Antiforgery tokens and the auth cookie are encrypted
// with it; left unconfigured it lands in the launching account's profile folder,
// which an IIS app pool does not load, so the keys are regenerated on every
// recycle and every form rendered before it starts failing with a bare 400.
// Pin it to the deployment and name the app so all workers agree.
var userKeyRing = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys"));
userKeyRing.Create();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(userKeyRing)
    .SetApplicationName("SolarPortal.User");

var app = builder.Build();

// Per-environment exception handling.
//
// In Development we use ASP.NET's built-in DeveloperExceptionPage so the
// developer sees the full stack trace, source code, route values, etc.
// Our custom ExceptionHandlingMiddleware is for Production — it shows a
// friendly error page and tries to keep the user inside the app.
//
// Putting custom middleware in front of DeveloperExceptionPage was masking
// the underlying error and (worse) trying to set headers after the response
// had started — producing "Headers are read-only" / "StatusCode cannot be
// set" exceptions in the logs.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

// Custom error handling middleware — only meaningful in Production.
// In Dev the DeveloperExceptionPage above wins and this is a no-op for
// unhandled exceptions (it still catches anything that slips through and
// has its own HasStarted-guarded fallback).
if (!app.Environment.IsDevelopment())
{
    app.UseMiddleware<SolarPortal.Web.Middleware.ExceptionHandlingMiddleware>();
}

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Seed data on startup
using (var scope = app.Services.CreateScope())
{
    var seeder = new DbSeeder(scope.ServiceProvider);
    await seeder.SeedAsync();
}

app.Run();