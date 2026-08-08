namespace SolarPortal.Application.DTOs;

public class AdminDashboardDto
{
    public int TotalProjects { get; set; }
    public int PendingApprovals { get; set; }
    public int ActiveInstallations { get; set; }
    public int CompletedProjects { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal PendingPayments { get; set; }
    public int TotalWorkers { get; set; }
    public List<SolarRequestDto> RecentRequests { get; set; } = new();
    public Dictionary<string, int> StatusDistribution { get; set; } = new();
}

public class UserDashboardDto
{
    public int TotalProjects { get; set; }
    public int PendingApprovals { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalDue { get; set; }

    /// <summary>
    /// Money an Already-Active member paid on the legacy cPanel order that
    /// activated their ID (image point 1). Already counted inside TotalPaid and
    /// netted out of TotalDue - carried separately only so the card can say where
    /// the extra money came from, which otherwise reads as an arithmetic bug.
    /// </summary>
    public decimal ActiveIdDeposit { get; set; }
    public List<SolarRequestDto> MyProjects { get; set; } = new();
    public SolarRequestDto? LatestProject { get; set; }
    public List<NotificationDto> UnreadNotifications { get; set; } = new();
    public List<SiteSurveyDto> MySiteSurveys { get; set; } = new();
    public int PendingSurveys { get; set; }
    public int CompletedSurveys { get; set; }
}

public class NotificationDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public string? NotificationType { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? SolarRequestId { get; set; }
}