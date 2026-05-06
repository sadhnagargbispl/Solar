using SolarPortal.Application.DTOs;

public interface IDashboardService
{
    Task<AdminDashboardDto> GetAdminDashboardAsync();
    Task<UserDashboardDto> GetUserDashboardAsync(string userId);
}
