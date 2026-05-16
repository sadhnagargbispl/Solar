using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

public class LiveDbAuthBridge : ILiveDbAuthBridge
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly ILogger<LiveDbAuthBridge> _logger;

    public LiveDbAuthBridge(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole> roles,
        ILogger<LiveDbAuthBridge> logger)
    {
        _db     = db;
        _users  = users;
        _roles  = roles;
        _logger = logger;
    }

    public async Task<string?> TryBridgeUserAsync(string idNo, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idNo) || string.IsNullOrWhiteSpace(password))
                return null;

            // Match against m_membermaster (plain-text per spec).
            // Trim defensively in case stored values have trailing spaces.
            var trimmedId = idNo.Trim();
            var member = await _db.Members
                .AsNoTracking()
                .Where(m => m.IdNo != null && m.Passw != null)
                .FirstOrDefaultAsync(m =>
                    m.IdNo!.Trim() == trimmedId &&
                    m.Passw!.Trim() == password);

            if (member == null)
            {
                _logger.LogInformation("LiveDb bridge: no user match for IdNo={IdNo}", trimmedId);
                return null;
            }

            var syntheticEmail = $"member-{trimmedId}@livedb.local";
            await EnsureRoleAsync("User");
            await EnsureUserAsync(syntheticEmail, password, member.FullName, "User");
            return syntheticEmail;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveDb bridge failed for user IdNo={IdNo}", idNo);
            return null;
        }
    }

    public async Task<string?> TryBridgeAdminAsync(string userName, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                return null;

            var trimmedName = userName.Trim();
            var admin = await _db.AdminUsers
                .AsNoTracking()
                .Where(u => u.UserName != null && u.Passw != null)
                .FirstOrDefaultAsync(u =>
                    u.UserName!.Trim() == trimmedName &&
                    u.Passw!.Trim() == password);

            if (admin == null)
            {
                _logger.LogInformation("LiveDb bridge: no admin match for UserName={UserName}", trimmedName);
                return null;
            }

            var syntheticEmail = $"admin-{trimmedName}@livedb.local";
            await EnsureRoleAsync("Admin");
            await EnsureUserAsync(syntheticEmail, password, trimmedName, "Admin");
            return syntheticEmail;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveDb bridge failed for admin UserName={UserName}", userName);
            return null;
        }
    }

    private async Task EnsureRoleAsync(string role)
    {
        if (!await _roles.RoleExistsAsync(role))
            await _roles.CreateAsync(new IdentityRole(role));
    }

    private async Task EnsureUserAsync(string email, string password, string fullName, string role)
    {
        var user = await _users.FindByEmailAsync(email);
        if (user == null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = string.IsNullOrWhiteSpace(fullName) ? email : fullName,
                IsActive = true,
                LockoutEnabled = false   // ← disable lockout to avoid millisecondsDelay bug
            };
            var createResult = await _users.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                _logger.LogWarning("Could not create Identity user {Email}: {Errors}",
                    email, string.Join("; ", createResult.Errors.Select(e => e.Description)));
                return;
            }
            await _users.AddToRoleAsync(user, role);
            return;
        }

        // User already exists — keep them unlocked and refresh password from live DB.
        if (await _users.IsLockedOutAsync(user))
            await _users.SetLockoutEndDateAsync(user, null);

        user.LockoutEnabled = false;
        user.IsActive = true;
        await _users.UpdateAsync(user);

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var reset = await _users.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded)
        {
            _logger.LogWarning("Could not refresh password for {Email}: {Errors}",
                email, string.Join("; ", reset.Errors.Select(e => e.Description)));
        }

        if (!await _users.IsInRoleAsync(user, role))
            await _users.AddToRoleAsync(user, role);
    }
}
