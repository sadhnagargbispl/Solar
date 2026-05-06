using Microsoft.AspNetCore.Identity;
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

        await db.Database.EnsureCreatedAsync();

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
        var workerRepo = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!workerRepo.Workers.Any())
        {
            workerRepo.Workers.AddRange(
                new Domain.Entities.Worker { Name = "Rajan Sharma", MobileNumber = "9800001111", Specialization = "Solar Electrician", City = "Jaipur", State = "Rajasthan", IsAvailable = true },
                new Domain.Entities.Worker { Name = "Mohan Verma", MobileNumber = "9800002222", Specialization = "Installer", City = "Jodhpur", State = "Rajasthan", IsAvailable = true },
                new Domain.Entities.Worker { Name = "Suresh Kumar", MobileNumber = "9800003333", Specialization = "Wiring Expert", City = "Udaipur", State = "Rajasthan", IsAvailable = true }
            );
            await workerRepo.SaveChangesAsync();
        }
    }
}