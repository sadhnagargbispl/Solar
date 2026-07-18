using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class PMDocument : BaseEntity
{
    public int SolarRequestId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? Remarks { get; set; }

    // Spec task 11: documents uploaded by ADMIN on approval (PM Surya Ghar ID docs,
    // sanction letters, etc.) that the USER can download. User uploads keep this false.
    public bool IsAdminUpload { get; set; } = false;

    // Navigation
    public virtual SolarRequest? SolarRequest { get; set; }
}