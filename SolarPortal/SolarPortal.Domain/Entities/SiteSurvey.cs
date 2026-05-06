using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class SiteSurvey : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? AssignedToUserId { get; set; }
    public DateTime? SurveyDate { get; set; }
    public string? SurveyNotes { get; set; }
    public string? SurveyPhotoPath { get; set; }
    public bool IsCompleted { get; set; } = false;
    public DateTime? CompletedAt { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ApplicationUser? AssignedTo { get; set; }
}