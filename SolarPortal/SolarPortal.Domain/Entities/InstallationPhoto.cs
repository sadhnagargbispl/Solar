using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// One "Mark Installed" photo uploaded by the INC installer.
///
/// Image point 11 (INC panel + admin): "INC — Mark Installed → multiple photo
/// upload, upto 30 photo. Ye admin ko show hona chahiye."
///
/// Installation.CompletionPhotoPath still holds the FIRST photo so every existing
/// screen that reads that single column keeps working unchanged; the full set
/// lives here.
/// </summary>
public class InstallationPhoto : BaseEntity
{
    /// <summary>Hard cap from the spec — 30 photos per installation.</summary>
    public const int MaxPerInstallation = 30;

    public int InstallationId { get; set; }

    /// <summary>Denormalised so admin reports can filter by request without a join.</summary>
    public int SolarRequestId { get; set; }

    public string FilePath { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    /// <summary>Worker who uploaded it — the assigned INC installer.</summary>
    public int? UploadedByWorkerId { get; set; }

    public virtual Installation? Installation { get; set; }
}
