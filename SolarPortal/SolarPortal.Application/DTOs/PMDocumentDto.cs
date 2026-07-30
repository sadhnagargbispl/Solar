using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class PMDocumentDto
{
    public int Id { get; set; }
    public int SolarRequestId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? Remarks { get; set; }
    public bool IsAdminUpload { get; set; }
    public DateTime CreatedAt { get; set; }

    // Set when an existing document is re-uploaded (replace). With CreatedAt this
    // gives the "last touched" time, which the PM Surya page compares against
    // SolarRequest.PMSuryaSubmittedAt to decide if the user may still replace it.
    public DateTime? UpdatedAt { get; set; }
}