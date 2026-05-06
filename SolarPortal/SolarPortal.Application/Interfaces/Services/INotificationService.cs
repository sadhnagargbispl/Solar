using SolarPortal.Application.DTOs;

public interface INotificationService
{
    Task CreateAsync(CreateNotificationDto dto);
    Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(string userId);
    Task MarkAsReadAsync(int notificationId);
}