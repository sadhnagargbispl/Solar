using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>Kind of item an installer filed against one IncUploadFormat line.</summary>
public enum ChecklistEntryType
{
    Video = 2,
    Remark = 3
}

/// <summary>
/// The video / typed detail an INC installer filed for one line of the fixed
/// upload format (dbo.InstallationChecklistEntries). Photos for a line are NOT
/// kept here - they stay in InstallationPhotos (tagged with FormatItemId) so the
/// admin Installation Approval page keeps showing them unchanged.
/// </summary>
public class InstallationChecklistEntry : BaseEntity
{
    public int InstallationId { get; set; }

    /// <summary>Denormalised for reports, like InstallationPhoto.</summary>
    public int SolarRequestId { get; set; }

    /// <summary>IncUploadFormats.Id this entry satisfies.</summary>
    public int FormatItemId { get; set; }

    public ChecklistEntryType EntryType { get; set; }

    // Video
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    // Remark / detail text
    public string? RemarkText { get; set; }

    public int? UploadedByWorkerId { get; set; }
}
