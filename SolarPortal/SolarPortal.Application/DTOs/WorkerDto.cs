namespace SolarPortal.Application.DTOs;

public class WorkerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Specialization { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
}

public class CreateWorkerDto
{
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Specialization { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
}

public class CreateNotificationDto
{
    public string UserId { get; set; } = string.Empty;
    public int? SolarRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? NotificationType { get; set; }
}