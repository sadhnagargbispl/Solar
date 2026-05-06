using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Mappings;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        // Identity
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.SignIn.RequireConfirmedAccount = false;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        // Cookie config
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

        // Repositories & UoW
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // AutoMapper
        services.AddAutoMapper(typeof(MappingProfile).Assembly);

        // Services
        services.AddScoped<ISolarRequestService, SolarRequestService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IWorkerService, WorkerService>();
        services.AddScoped<IFileUploadService, FileUploadService>();

        return services;
    }
}