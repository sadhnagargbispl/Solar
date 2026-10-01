using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// One line of the fixed "INC TEAM PHOTO UPLOAD FORMAT" sheet (dbo.IncUploadFormats).
///
/// Spec: "INC upload format ye fixed hai, isko ek table me save kar lo. INC panel
/// par ye pura fill hoga tabhi INC commission de sakta hai. Jisme video ho wo
/// video nahi hai to nahi kar sakta."
///
/// The 13 rows are seeded by ADD-IncUploadFormat.sql. Each column maps 1:1 to the
/// sheet: Photo (Yes/No), Vedio (count), Mini / Max No. of Photo, Remark (Yes/No).
/// The rules an installation must meet per row live in InstallationChecklist.
/// </summary>
public class IncUploadFormat : BaseEntity
{
    /// <summary>Sr.No. on the sheet — also the display order.</summary>
    public int SrNo { get; set; }

    /// <summary>"Work" column, e.g. "ALL PANEL SERIAL NUMBER KI".</summary>
    public string Work { get; set; } = string.Empty;

    /// <summary>"Photo" column (Yes/No).</summary>
    public bool PhotoRequired { get; set; }

    /// <summary>"Vedio" column — how many videos this line needs (0 = none).</summary>
    public int VideoCount { get; set; }

    /// <summary>"Mini No. of Photo".</summary>
    public int MinPhotos { get; set; }

    /// <summary>"Max No. of Photo". 0 = no photo upload on this line.</summary>
    public int MaxPhotos { get; set; }

    /// <summary>"Remark" column (Yes) — the installer must type the detail as text.</summary>
    public bool RemarkRequired { get; set; }

    public bool IsActive { get; set; } = true;
}
