using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Infrastructure.Data;

public class DbSeeder
{
    private readonly IServiceProvider _services;

    public DbSeeder(IServiceProvider services) => _services = services;

    public async Task SeedAsync()
    {
        using var scope = _services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (db.Database.GetPendingMigrations().Any())
            await db.Database.MigrateAsync();

        // Seed Roles
        string[] roles = { "Admin", "User" };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // Seed Admin User
        const string adminEmail = "admin@solarportal.com";
        if (await userManager.FindByEmailAsync(adminEmail) == null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                FullName = "System Administrator",
                MobileNumber = "9999999999",
                EmailConfirmed = true,
                IsActive = true
            };

            var result = await userManager.CreateAsync(admin, "Admin@1234");
            if (result.Succeeded)
                await userManager.AddToRoleAsync(admin, "Admin");
        }

        // Seed Demo User
        const string userEmail = "user@solarportal.com";
        if (await userManager.FindByEmailAsync(userEmail) == null)
        {
            var user = new ApplicationUser
            {
                UserName = userEmail,
                Email = userEmail,
                FullName = "Demo User",
                MobileNumber = "9876543210",
                City = "Jaipur",
                State = "Rajasthan",
                EmailConfirmed = true,
                IsActive = true
            };

            var result = await userManager.CreateAsync(user, "User@1234");
            if (result.Succeeded)
                await userManager.AddToRoleAsync(user, "User");
        }

        // Seed Workers
        if (!db.Workers.Any())
        {
            db.Workers.AddRange(
                new Domain.Entities.Worker { Name = "Rajan Sharma", MobileNumber = "9800001111", Specialization = "Solar Electrician", Type = Domain.Enums.WorkerType.JOB, City = "Jaipur", State = "Rajasthan", IsAvailable = true },
                new Domain.Entities.Worker { Name = "Mohan Verma", MobileNumber = "9800002222", Specialization = "Installer", Type = Domain.Enums.WorkerType.INC, City = "Jodhpur", State = "Rajasthan", IsAvailable = true },
                new Domain.Entities.Worker { Name = "Suresh Kumar", MobileNumber = "9800003333", Specialization = "Wiring Expert", Type = Domain.Enums.WorkerType.JOB, City = "Udaipur", State = "Rajasthan", IsAvailable = true }
            );
            await db.SaveChangesAsync();
        }

        // Seed Solar Projects (master)
        if (!db.SolarProjects.Any())
        {
            db.SolarProjects.AddRange(
                new Domain.Entities.SolarProject {
                    Name = "Plan A — 1.1 kV Domestic",
                    SolarTypeKV = 1.1m, ConnectionType = Domain.Enums.ConnectionType.Domestic,
                    BV = 100, FinalBV = 110,
                    DiscomWork = 1500, DealClose = 1500, SCZMenue = 3000, SportainTeam = 3000,
                    TotalAmount = 15900, IsActive = true
                },
                new Domain.Entities.SolarProject {
                    Name = "Plan B — 3 kV Domestic",
                    SolarTypeKV = 3m, ConnectionType = Domain.Enums.ConnectionType.Domestic,
                    BV = 100, FinalBV = 175,
                    DiscomWork = 2500, DealClose = 2500, SCZMenue = 4500, SportainTeam = 4500,
                    TotalAmount = 19900, IsActive = true
                },
                new Domain.Entities.SolarProject {
                    Name = "Plan C — 5 kV Commercial",
                    SolarTypeKV = 5m, ConnectionType = Domain.Enums.ConnectionType.Commercial,
                    BV = 100, FinalBV = 175,
                    DiscomWork = 5000, DealClose = 5000, SCZMenue = 8000, SportainTeam = 8000,
                    TotalAmount = 29900, IsActive = true
                }
            );
            await db.SaveChangesAsync();
        }
    }
}