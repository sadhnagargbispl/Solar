using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<SolarRequest> SolarRequests => Set<SolarRequest>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<SiteSurvey> SiteSurveys => Set<SiteSurvey>();
    public DbSet<MeterDispatch> MeterDispatches => Set<MeterDispatch>();
    public DbSet<MaterialDispatch> MaterialDispatches => Set<MaterialDispatch>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<DCRDocument> DCRDocuments => Set<DCRDocument>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<WorkerAssignment> WorkerAssignments => Set<WorkerAssignment>();
    public DbSet<Commission> Commissions => Set<Commission>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Apply all entity configurations from assembly
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Rename Identity tables
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("Roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("RoleClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("UserTokens");

        // SolarRequest config
        builder.Entity<SolarRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestNumber).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.RequestNumber).IsUnique();
            e.Property(x => x.PlanAmount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.User)
             .WithMany(u => u.SolarRequests)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Payment
        builder.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.SolarRequest)
             .WithMany(r => r.Payments)
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Document
        builder.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.SolarRequest)
             .WithMany(r => r.Documents)
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Commission
        builder.Entity<Commission>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.CommissionAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.CommissionPercentage).HasColumnType("decimal(5,2)");
            e.HasOne(x => x.SolarRequest)
             .WithOne(r => r.Commission)
             .HasForeignKey<Commission>(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // WorkerAssignment
        builder.Entity<WorkerAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Worker)
             .WithMany(w => w.Assignments)
             .HasForeignKey(x => x.WorkerId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Apply soft delete filter to all BaseEntity types
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(Domain.Common.BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                // Already applied per entity above
            }
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is Domain.Common.BaseEntity &&
                        (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            if (entry.Entity is Domain.Common.BaseEntity entity)
            {
                if (entry.State == EntityState.Added)
                    entity.CreatedAt = DateTime.UtcNow;
                else
                    entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}