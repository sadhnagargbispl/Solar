using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Infrastructure.Data;

/// <summary>
/// Startup bootstrap for the live DB.
///
/// Strategy:
/// - Try MigrateAsync first. If the live DB doesn't have our Solar workflow
///   tables yet, EF will create them. m_membermaster / m_usermaster /
///   m_statedivmaster are excluded from migrations and stay untouched.
/// - If migration fails (e.g., because of partial schema, permissions,
///   or any other reason), we DO NOT crash the app. The app continues to
///   run; login via LiveDbAuthBridge keeps working as long as the AspNet*
///   tables exist (use SETUP-IdentityTables.sql if you'd rather create
///   them by hand and disable migrations entirely).
/// - We ensure Identity roles exist so [Authorize(Roles="Admin")] works.
/// - We seed ONE installer demo account for the Inc site.
/// - We do not seed any user/admin accounts — those come from the live DB.
/// </summary>
public class DbSeeder
{
    private readonly IServiceProvider _services;

    public DbSeeder(IServiceProvider services) => _services = services;

    public async Task SeedAsync()
    {
        using var scope = _services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DbSeeder>>();

        // 1. Try to apply pending migrations. Wrapped so a partial/legacy
        //    schema doesn't crash the host.
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migration completed (or already up to date).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Migration step skipped — the app will continue. If login or " +
                "request creation fails, run SETUP-IdentityTables.sql against " +
                "the live database manually, then restart.");
        }

        // 2. Ensure Identity roles.
        try
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            string[] roles = { "SuperAdmin", "Admin", "User", "Installer" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Role ensure step failed.");
        }

        // 3. Demo installer (Inc site). User and Admin come from live DB.
        try
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await EnsureUser(userManager, "installer@solarportal.com", "Installer@1234",
                "Demo Installer", "9800001111", "Installer");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Installer demo seed skipped.");
        }
    }

    private static async Task EnsureUser(
        UserManager<ApplicationUser> userManager,
        string email, string password, string fullName, string mobile, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                PhoneNumber = mobile,
                IsActive = true
            };
            var result = await userManager.CreateAsync(user, password);
            if (result.Succeeded)
                await userManager.AddToRoleAsync(user, role);
        }
    }
}
